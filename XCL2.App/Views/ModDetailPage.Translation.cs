using System.Windows;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class ModDetailPage
{
    private string _originalSummary = "";
    private string? _translatedSummary;
    private bool _showingTranslatedSummary;
    private CancellationTokenSource? _summaryTranslationCancellation;

    private void InitializeSummaryTranslation(string description)
    {
        _originalSummary = description ?? "";
        DescriptionText.Text = string.IsNullOrWhiteSpace(_originalSummary) ? "（暂无简介）" : _originalSummary;
        TranslateSummaryButton.Visibility = _originalSummary.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            ? Visibility.Visible : Visibility.Collapsed;
        Unloaded += (_, _) => CancelSummaryTranslation();
        IsVisibleChanged += (_, _) => { if (!IsVisible) CancelSummaryTranslation(); };
    }

    private async void TranslateSummary_Click(object sender, RoutedEventArgs e)
    {
        if (_summaryTranslationCancellation is not null)
        {
            CancelSummaryTranslation();
            return;
        }
        if (_showingTranslatedSummary)
        {
            DescriptionText.Text = _originalSummary;
            _showingTranslatedSummary = false;
            TranslateSummaryButton.Content = "翻译";
            SummaryTranslationStatus.Visibility = Visibility.Collapsed;
            return;
        }
        if (_translatedSummary is not null)
        {
            ShowTranslatedSummary();
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _summaryTranslationCancellation = cancellation;
        TranslateSummaryButton.Content = "取消";
        SetSummaryTranslationStatus("正在翻译…");
        try
        {
            var translated = await SummaryTranslationService.Shared.TranslateToChineseAsync(_originalSummary, cancellation.Token);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(_summaryTranslationCancellation, cancellation) || !IsLoaded || !IsVisible)
                return;
            _translatedSummary = translated;
            ShowTranslatedSummary();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (SummaryTranslationException ex)
        {
            if (!ReferenceEquals(_summaryTranslationCancellation, cancellation) || !IsLoaded || !IsVisible) return;
            SetSummaryTranslationStatus(ex.Failure switch
            {
                SummaryTranslationFailure.TooLong => "简介过长，暂时无法在线翻译。原文已保留。",
                SummaryTranslationFailure.QuotaExceeded => "免费翻译额度已用尽或请求过于频繁，请稍后再试。",
                SummaryTranslationFailure.Timeout => "翻译请求超时，请稍后重试。",
                _ => "翻译服务暂时不可用，请稍后重试。原文已保留。"
            });
        }
        catch (Exception)
        {
            // Do not log request URLs, returned text or arbitrary provider error messages.
            if (ReferenceEquals(_summaryTranslationCancellation, cancellation) && IsLoaded && IsVisible)
                SetSummaryTranslationStatus("翻译暂时不可用，原文已保留。");
        }
        finally
        {
            if (ReferenceEquals(_summaryTranslationCancellation, cancellation))
            {
                _summaryTranslationCancellation = null;
                TranslateSummaryButton.Content = _showingTranslatedSummary ? "原文" : "翻译";
            }
        }
    }

    private void ShowTranslatedSummary()
    {
        DescriptionText.Text = _translatedSummary ?? _originalSummary;
        _showingTranslatedSummary = true;
        TranslateSummaryButton.Content = "原文";
        SetSummaryTranslationStatus("MyMemory 机器翻译，仅供参考");
    }

    private void SetSummaryTranslationStatus(string text)
    {
        SummaryTranslationStatus.Text = text;
        SummaryTranslationStatus.Visibility = Visibility.Visible;
    }

    private void CancelSummaryTranslation()
    {
        var cancellation = _summaryTranslationCancellation;
        if (cancellation is null) return;
        _summaryTranslationCancellation = null;
        cancellation.Cancel();
        TranslateSummaryButton.Content = _showingTranslatedSummary ? "原文" : "翻译";
        SummaryTranslationStatus.Visibility = Visibility.Collapsed;
    }
}
