using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class AiAssistantPanel
{
    public ObservableCollection<AiAttachment> PendingAttachments { get; } = new();
    private readonly HashSet<string> _newPendingAttachmentIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<AiAttachment>> _attachmentDrafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IDisposable> _attachmentDraftPins = new(StringComparer.Ordinal);
    private readonly HashSet<string> _discardedAttachmentSessions = new(StringComparer.Ordinal);
    private bool _uploadingAttachments;
    private CancellationTokenSource? _uploadCts;

    private async void UploadAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (_sending || _uploadingAttachments || _newSessionDecisionPending) return;
        var chat = _config.WorkMode == AiWorkMode.Chat;
        var picker = new OpenFileDialog
        {
            Title = chat ? "上传图片（文字或日志请直接粘贴）" : "上传图片或文件（ZIP 会安全解压到临时只读目录）",
            Filter = chat ? "图片|*.png;*.jpg;*.jpeg;*.gif;*.webp" : "图片、ZIP 和文件|*.*|图片|*.png;*.jpg;*.jpeg;*.gif;*.webp|ZIP 压缩包|*.zip",
            Multiselect = true,
            CheckFileExists = true
        };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        if (picker.FileNames.Length + PendingAttachments.Count > AiAttachmentService.MaxAttachmentsPerMessage)
        { ToastService.ShowWarning("每条消息最多添加 8 个附件。"); return; }

        _uploadingAttachments = true;
        var sessionId = _session.Id;
        using var uploadCts = new CancellationTokenSource();
        _uploadCts = uploadCts;
        RefreshUploadState();
        try
        {
            foreach (var file in picker.FileNames)
            {
                AiAttachment? imported = null;
                try
                {
                    if (chat && !new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" }.Contains(System.IO.Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                        throw new InvalidOperationException("纯聊天模式仅接收图片；文字或日志请直接粘贴到消息中。");
                    imported = await AiAttachmentService.ImportAsync(file, uploadCts.Token);
                    if (_overlayClosed || _discardedAttachmentSessions.Contains(sessionId))
                    { AiAttachmentService.RemovePending(imported); continue; }
                    if (_session.Id == sessionId)
                    {
                        AiAttachmentService.ValidateForMessage(PendingAttachments.Append(imported));
                        PendingAttachments.Add(imported);
                        UpdateAttachmentDraftPin(sessionId, PendingAttachments);
                    }
                    else
                    {
                        if (!_attachmentDrafts.TryGetValue(sessionId, out var draft)) _attachmentDrafts[sessionId] = draft = new();
                        AiAttachmentService.ValidateForMessage(draft.Append(imported));
                        draft.Add(imported);
                        UpdateAttachmentDraftPin(sessionId, draft);
                    }
                    _newPendingAttachmentIds.Add(imported.Id);
                }
                catch (OperationCanceledException)
                { if (imported != null) AiAttachmentService.RemovePending(imported); break; }
                catch (Exception ex)
                {
                    if (imported != null) AiAttachmentService.RemovePending(imported);
                    ToastService.ShowWarning("无法上传 " + System.IO.Path.GetFileName(file) + "：" + ex.Message);
                }
            }
        }
        finally { _uploadCts = null; _uploadingAttachments = false; RefreshUploadState(); }
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (_sending || _uploadingAttachments || sender is not Button { CommandParameter: AiAttachment attachment }) return;
        if (!PendingAttachments.Remove(attachment)) return;
        if (_newPendingAttachmentIds.Remove(attachment.Id)) AiAttachmentService.RemovePending(attachment);
        UpdateAttachmentDraftPin(_session.Id, PendingAttachments);
    }

    private void ClearPendingAttachments(bool deleteUnused)
    {
        foreach (var attachment in PendingAttachments)
            if (_newPendingAttachmentIds.Remove(attachment.Id) && deleteUnused) AiAttachmentService.RemovePending(attachment);
        PendingAttachments.Clear();
        _attachmentDrafts.Remove(_session.Id);
        ReleaseAttachmentDraftPin(_session.Id);
    }

    private void RestorePendingAttachments(IEnumerable<AiAttachment>? attachments)
    {
        ClearPendingAttachments(deleteUnused: true);
        foreach (var attachment in attachments ?? Array.Empty<AiAttachment>()) PendingAttachments.Add(attachment.Clone());
        UpdateAttachmentDraftPin(_session.Id, PendingAttachments);
    }

    private void SaveAttachmentDraft(string sessionId)
    {
        if (!_discardedAttachmentSessions.Contains(sessionId))
        {
            _attachmentDrafts[sessionId] = PendingAttachments.Select(a => a.Clone()).ToList();
            UpdateAttachmentDraftPin(sessionId, PendingAttachments);
        }
    }

    private void RestoreAttachmentDraft(string sessionId)
    {
        PendingAttachments.Clear();
        if (_attachmentDrafts.TryGetValue(sessionId, out var draft))
            foreach (var attachment in draft) PendingAttachments.Add(attachment.Clone());
        UpdateAttachmentDraftPin(sessionId, PendingAttachments);
    }

    private void DiscardAttachmentDraft(string sessionId)
    {
        _discardedAttachmentSessions.Add(sessionId);
        var draft = _session.Id == sessionId ? PendingAttachments.ToList()
            : _attachmentDrafts.TryGetValue(sessionId, out var saved) ? saved : new();
        foreach (var attachment in draft)
            if (_newPendingAttachmentIds.Remove(attachment.Id)) AiAttachmentService.RemovePending(attachment);
        _attachmentDrafts.Remove(sessionId);
        if (_session.Id == sessionId) PendingAttachments.Clear();
        ReleaseAttachmentDraftPin(sessionId);
    }

    private void DiscardAllAttachmentDrafts()
    {
        foreach (var sessionId in _attachmentDrafts.Keys.Append(_session.Id).Distinct().ToList()) DiscardAttachmentDraft(sessionId);
    }

    private void UpdateAttachmentDraftPin(string sessionId, IEnumerable<AiAttachment> attachments)
    {
        // Acquire the replacement before releasing the previous lease, including when two
        // conversations or a recall draft share an already-sent attachment.
        var next = AiAttachmentService.PinAttachments(attachments);
        ReleaseAttachmentDraftPin(sessionId);
        _attachmentDraftPins[sessionId] = next;
    }

    private void ReleaseAttachmentDraftPin(string sessionId)
    {
        if (_attachmentDraftPins.Remove(sessionId, out var pin)) pin.Dispose();
    }

    private void RefreshUploadState()
    {
        UploadAttachmentButton.IsEnabled = !_sending && !_uploadingAttachments && !_newSessionDecisionPending;
        PendingAttachmentsItemsControl.IsEnabled = UploadAttachmentButton.IsEnabled;
        SendButton.IsEnabled = !_sending && !_uploadingAttachments && !_newSessionDecisionPending;
        UploadAttachmentButton.Content = _uploadingAttachments ? "正在上传…" : "上传文件";
    }
}
