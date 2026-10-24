using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>原生 WPF 房间控制台。通过陶瓦本地 API 管理房间；服务与页面具有独立生命周期。</summary>
public partial class MultiplayerPage : UserControl
{
    private readonly MainWindow _owner;
    private readonly TerracottaService _terracotta = new();
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private TerracottaService.Session? _session;
    private TerracottaRoomState? _state;
    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _operation;
    private Task? _pollTask;
    private bool _busy;
    private bool _stateFresh;
    private bool _pollFailed;
    private string? _rememberedJoinCode;
    private string? _shareCode;
    private string? _membersSignature;
    private long? _joinedGeneration;

    private sealed record MemberCard(string Name, string Details, string Status);

    public MultiplayerPage(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();
        PlayerNameBox.Text = owner.ConfigService.Config.TerracottaPlayerName ?? "";
        PublicNodesBox.Text = owner.ConfigService.Config.TerracottaPublicNodes ?? "";
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
        UpdateControls();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_lifetime != null) return;
        var lifetime = new CancellationTokenSource();
        _lifetime = lifetime;
        _busy = false;
        _stateFresh = false;
        if (_session != null)
        {
            LastUpdatedText.Text = "正在重新读取房间状态…";
            SetNotice("正在同步陶瓦当前状态…");
        }
        UpdateControls();
        _pollTask = PollStateAsync(lifetime);
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        var lifetime = _lifetime;
        _lifetime = null;
        _operation?.Cancel();
        // guest-ok 不返回分享码；离开页面后不能假定后台仍停留在同一房间。
        _rememberedJoinCode = null;
        _shareCode = null;
        _joinedGeneration = null;
        if (lifetime == null) return;
        lifetime.Cancel();
        _ = DisposeLifetimeAsync(lifetime, _pollTask);
        // 页面离开仅停止请求和刷新，不断开可能由其他启动器共用的联机服务。
    }

    private static async Task DisposeLifetimeAsync(CancellationTokenSource lifetime, Task? polling)
    {
        try { if (polling != null) await polling; }
        catch (OperationCanceledException) { }
        finally { lifetime.Dispose(); }
    }

    private bool IsCurrent(CancellationTokenSource lifetime)
        => ReferenceEquals(_lifetime, lifetime) && IsLoaded && !lifetime.IsCancellationRequested;

    private async Task PollStateAsync(CancellationTokenSource lifetime)
    {
        var token = lifetime.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var session = _session;
                if (session != null && !_busy && await _requestGate.WaitAsync(0, token))
                {
                    try
                    {
                        var state = await _terracotta.GetStateAsync(session.Endpoint, token);
                        if (IsCurrent(lifetime) && ReferenceEquals(_session, session))
                        {
                            RenderState(state);
                            if (_pollFailed && state.Mode != TerracottaRoomMode.Exception) SetNotice(state.StatusText);
                            _pollFailed = false;
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        if (IsCurrent(lifetime) && ReferenceEquals(_session, session))
                        {
                            _stateFresh = false;
                            _pollFailed = true;
                            SetNotice("暂时无法读取联机状态：" + ex.Message + "。正在重试，也可以点击“刷新状态”。", true);
                            LastUpdatedText.Text = "当前显示上次成功读取的状态，请等待重新连接。";
                            UpdateControls();
                        }
                    }
                    finally { _requestGate.Release(); }
                }
                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunOperationAsync(string progress, Func<CancellationToken, Task> action, string? success = null)
    {
        var lifetime = _lifetime;
        if (_busy || lifetime == null || !IsCurrent(lifetime)) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        _operation = operation;
        _busy = true;
        SetNotice(progress);
        UpdateControls();
        var entered = false;
        try
        {
            await _requestGate.WaitAsync(operation.Token);
            entered = true;
            await action(operation.Token);
            if (IsCurrent(lifetime) && success != null && _state?.Mode != TerracottaRoomMode.Exception)
                SetNotice(success);
        }
        catch (OperationCanceledException)
        {
            if (IsCurrent(lifetime))
            {
                _stateFresh = false;
                SetNotice("已取消等待。已经提交的房间操作可能仍在进行，请刷新确认实际状态。");
            }
        }
        catch (Exception ex)
        {
            if (IsCurrent(lifetime))
            {
                _stateFresh = false;
                SetNotice("操作未完成：" + ex.Message, true);
            }
            LauncherLogService.AppendLine("[陶瓦原生联机] " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            if (entered) _requestGate.Release();
            if (ReferenceEquals(_operation, operation)) _operation = null;
            if (IsCurrent(lifetime)) { _busy = false; UpdateControls(); }
        }
    }

    private async void StartService_Click(object sender, RoutedEventArgs e)
        => await RunOperationAsync("正在启动或连接陶瓦服务…", async token =>
        {
            var session = await _terracotta.LaunchNativeAsync(_owner.ConfigService.Config.TerracottaExecutablePath, token);
            token.ThrowIfCancellationRequested();
            if (_session?.Endpoint != session.Endpoint)
            {
                _rememberedJoinCode = null;
                _joinedGeneration = null;
            }
            _session = session;
            VersionText.Text = $"Terracotta {session.Version} · EasyTier {session.EasyTierVersion}";
            var state = await _terracotta.GetStateAsync(session.Endpoint, token);
            token.ThrowIfCancellationRequested();
            RenderState(state, announce: true);
        });

    private async void RefreshState_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null) return;
        await RunOperationAsync("正在刷新联机状态…", async token =>
        {
            var state = await _terracotta.GetStateAsync(_session.Endpoint, token);
            token.ThrowIfCancellationRequested();
            RenderState(state, announce: true);
        });
    }

    private async void CreateRoom_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null) return;
        SaveAdvancedOptions();
        var code = EmptyToNull(CreateCodeBox.Text);
        if (code != null)
        {
            if (!TerracottaProtocol.TryNormalizeRoomCode(code, out var normalized))
            { SetNotice("自定义房间码无效，请填写完整的 U/XXXX-XXXX-XXXX-XXXX 分享码，或留空自动生成。", true); CreateCodeBox.Focus(); return; }
            code = normalized;
        }
        var player = EmptyToNull(PlayerNameBox.Text);
        var nodes = ReadPublicNodes();
        await RunOperationAsync("正在创建房间并扫描局域网世界…", async token =>
        {
            var state = await _terracotta.CreateRoomAsync(_session.Endpoint, code, player, nodes, token);
            token.ThrowIfCancellationRequested();
            _rememberedJoinCode = null;
            RenderState(state, announce: true);
        });
    }

    private async void JoinRoom_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null) return;
        if (!TerracottaProtocol.TryNormalizeRoomCode(JoinCodeBox.Text, out var code))
        { SetNotice("房间码无效，请粘贴完整的 U/XXXX-XXXX-XXXX-XXXX 分享码或邀请信息。", true); JoinCodeBox.Focus(); return; }
        JoinCodeBox.Text = code;
        SaveAdvancedOptions();
        var player = EmptyToNull(PlayerNameBox.Text);
        var nodes = ReadPublicNodes();
        await RunOperationAsync("正在加入房间…", async token =>
        {
            var state = await _terracotta.JoinRoomAsync(_session.Endpoint, code, player, nodes, token);
            token.ThrowIfCancellationRequested();
            if (state.IsGuest) { _rememberedJoinCode = code; _joinedGeneration = null; }
            RenderState(state, announce: true);
        });
    }

    private async void LeaveRoom_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null) return;
        await RunOperationAsync("正在断开联机…", async token =>
        {
            var state = await _terracotta.LeaveRoomAsync(_session.Endpoint, token);
            token.ThrowIfCancellationRequested();
            _rememberedJoinCode = null;
            RenderState(state, announce: true);
        });
    }

    private void RenderState(TerracottaRoomState state, bool announce = false)
    {
        _state = state;
        _stateFresh = true;
        CurrentRoomPanel.Visibility = Visibility.Visible;
        RoomStateTitle.Text = ShortStatus(state.Mode);
        RoomStateDescription.Text = state.Mode switch
        {
            TerracottaRoomMode.Waiting => "服务已就绪。创建房间，或使用朋友分享的房间码加入。",
            TerracottaRoomMode.HostScanning => "正在寻找局域网世界。请启动 Minecraft 并在暂停菜单选择“对局域网开放”。",
            TerracottaRoomMode.HostStarting => "已找到局域网世界，正在启动联机网络。连接就绪后即可邀请朋友。",
            TerracottaRoomMode.Hosting => "房间已开放。复制下方房间码发给朋友，保持游戏世界和陶瓦运行。",
            TerracottaRoomMode.GuestConnecting => "正在连接房主，请稍候。双方需要使用兼容的游戏版本和模组。",
            TerracottaRoomMode.GuestStarting => "正在建立联机网络。" + (string.IsNullOrWhiteSpace(state.DifficultyText) ? "" : "\n" + state.DifficultyText),
            TerracottaRoomMode.Joined => "已连接房间。在游戏多人游戏列表寻找世界，也可使用下方地址直接连接。",
            TerracottaRoomMode.Exception => state.Failure == null ? "陶瓦报告了异常，请查看联机日志。" : state.Failure.Message + "\n修复建议：" + state.Failure.RepairSteps,
            _ => "当前陶瓦版本返回了暂不支持的状态（" + state.StateName + "）。可以刷新或打开程序查看。"
        };
        if (state.Mode == TerracottaRoomMode.Joined)
        {
            // index-profile_index 在同一已连接房间中保持不变；核心被其他客户端切换房间后不能复用旧码。
            var generation = state.Index - (state.ProfileIndex ?? 0);
            if (_joinedGeneration.HasValue && _joinedGeneration.Value != generation) _rememberedJoinCode = null;
            _joinedGeneration = generation;
        }
        else _joinedGeneration = null;
        if (state.Mode == TerracottaRoomMode.Waiting) _rememberedJoinCode = null;
        if (state.IsGuest && !string.IsNullOrWhiteSpace(state.RoomCode)) _rememberedJoinCode = state.RoomCode;
        _shareCode = EmptyToNull(state.RoomCode) ?? (state.IsGuest ? _rememberedJoinCode : null);
        ShareCodeBox.Text = _shareCode ?? "";
        ShareCodePanel.Visibility = _shareCode == null ? Visibility.Collapsed : Visibility.Visible;
        ConnectAddressBox.Text = state.ConnectAddress ?? "";
        ConnectAddressPanel.Visibility = string.IsNullOrWhiteSpace(state.ConnectAddress) ? Visibility.Collapsed : Visibility.Visible;
        var memberSignature = string.Join("\n", state.Members.Select(member => member.MachineId + "|" + member.Name + "|" + member.Vendor + "|" + member.Role));
        if (_membersSignature != memberSignature)
        {
            _membersSignature = memberSignature;
            MembersList.ItemsSource = state.Members.Select(member => new MemberCard(
                string.IsNullOrWhiteSpace(member.Name) ? "未命名玩家" : member.Name,
                string.IsNullOrWhiteSpace(member.Vendor) ? "陶瓦联机成员" : "客户端：" + member.Vendor,
                member.RoleText)).ToArray();
        }
        MembersTitle.Text = $"房间成员 · {state.Members.Count}";
        MembersSection.Visibility = state.Members.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        LastUpdatedText.Text = $"{DateTime.Now:HH:mm:ss} 更新 · 每 2 秒自动刷新";
        if (state.Mode == TerracottaRoomMode.Exception)
            SetNotice(state.Failure?.Title ?? "陶瓦服务报告了异常。", true);
        else if (announce) SetNotice(state.StatusText);
        UpdateControls();
    }

    private void UpdateControls()
    {
        var configured = _owner.ConfigService.Config.TerracottaExecutablePath;
        var customExists = !string.IsNullOrWhiteSpace(configured) && File.Exists(configured);
        var executableReady = customExists || TerracottaService.HasEmbeddedExecutable;
        ExecutableStatusText.Text = customExists ? "当前程序：" + configured : TerracottaService.HasEmbeddedExecutable
            ? "使用内置陶瓦 0.4.2，可直接在此管理联机房间。"
            : "当前构建未携带陶瓦程序，请从“程序选项”选择官方可执行文件。";
        StartServiceButton.Content = _session == null ? "启动联机" : "重新连接服务";
        StartServiceButton.IsEnabled = !_busy && executableReady;
        OpenProgramButton.IsEnabled = !_busy && executableReady;
        ExecutableOptionsButton.IsEnabled = !_busy;
        RefreshStateButton.IsEnabled = !_busy && _session != null;
        ReadLogButton.IsEnabled = !_busy && _session != null;
        CancelOperationButton.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        RoomForms.IsEnabled = !_busy && _session != null && _stateFresh && _state?.Mode == TerracottaRoomMode.Waiting;
        LeaveRoomButton.IsEnabled = !_busy && _session != null && _stateFresh && _state?.Mode is not (null or TerracottaRoomMode.Waiting or TerracottaRoomMode.Unknown);
        LeaveRoomButton.Content = _state?.Mode == TerracottaRoomMode.HostScanning ? "停止扫描" : _state?.IsHost == true ? "关闭房间" : "断开联机";
        ShareCodePanel.IsEnabled = _stateFresh && !_busy;
        ConnectAddressPanel.IsEnabled = _stateFresh && !_busy;
        ServiceStatusText.Text = _busy ? "正在处理…" : _session == null ? "尚未启动" : !_stateFresh ? "状态待刷新" : ShortStatus(_state?.Mode ?? TerracottaRoomMode.Unknown);
    }

    private static string ShortStatus(TerracottaRoomMode mode) => mode switch
    {
        TerracottaRoomMode.Waiting => "服务已就绪",
        TerracottaRoomMode.HostScanning => "正在扫描世界",
        TerracottaRoomMode.HostStarting => "正在创建房间",
        TerracottaRoomMode.Hosting => "房间已开放",
        TerracottaRoomMode.GuestConnecting => "正在连接房主",
        TerracottaRoomMode.GuestStarting => "正在建立通道",
        TerracottaRoomMode.Joined => "已加入房间",
        TerracottaRoomMode.Exception => "联机异常",
        _ => "状态待确认"
    };

    private void SetNotice(string text, bool warning = false)
    {
        NoticeText.Text = text;
        NoticeText.SetResourceReference(TextBlock.ForegroundProperty, warning ? "DangerBrush" : "TextSecondaryBrush");
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private IReadOnlyList<string> ReadPublicNodes() => PublicNodesBox.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim()).Where(line => line.Length > 0).Distinct(StringComparer.Ordinal).ToArray();

    private void SaveAdvancedOptions()
    {
        var config = _owner.ConfigService.Config;
        if (config.TerracottaPlayerName == PlayerNameBox.Text && config.TerracottaPublicNodes == PublicNodesBox.Text) return;
        config.TerracottaPlayerName = PlayerNameBox.Text;
        config.TerracottaPublicNodes = PublicNodesBox.Text;
        PersistConfig();
    }

    private void PersistConfig()
    {
        try { _owner.ConfigService.Save(); }
        catch (Exception ex)
        {
            ToastService.ShowWarning("设置已在当前会话应用，但未能保存：" + ex.Message);
            LauncherLogService.AppendLine("[陶瓦设置保存] " + ex.GetType().Name);
        }
    }

    private void AdvancedOptions_LostFocus(object sender, RoutedEventArgs e) => SaveAdvancedOptions();
    private void ResetPublicNodes_Click(object sender, RoutedEventArgs e) { PublicNodesBox.Clear(); SaveAdvancedOptions(); SetNotice("已恢复默认节点，下次创建或加入房间时生效。"); }
    private void CancelOperation_Click(object sender, RoutedEventArgs e) { _operation?.Cancel(); SetNotice("正在取消等待…"); }

    private void PasteRoomCode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            var pasted = Clipboard.GetText().Trim();
            var inviteCode = pasted.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.StartsWith("房间码：", StringComparison.Ordinal) || line.StartsWith("房间码:", StringComparison.Ordinal));
            JoinCodeBox.Text = inviteCode == null ? pasted : inviteCode[4..].Trim();
        }
        catch (Exception ex) { SetNotice("读取剪贴板失败：" + ex.Message, true); }
    }

    private void CopyText(string? text, string success)
    {
        if (string.IsNullOrWhiteSpace(text)) { SetNotice("当前没有可复制的内容。"); return; }
        try { Clipboard.SetText(text); ToastService.ShowSuccess(success); }
        catch (Exception ex) { SetNotice("复制失败：" + ex.Message, true); }
    }
    private void CopyRoomCode_Click(object sender, RoutedEventArgs e) => CopyText(_shareCode, "已复制房间码");
    private void CopyInvitation_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_shareCode)) { SetNotice("当前还没有可分享的房间码。"); return; }
        CopyText("一起加入陶瓦联机房间！\n房间码：" + _shareCode + "\n在启动器的陶瓦联机页面选择“加入房间”，请保持游戏版本和模组兼容。", "已复制邀请信息");
    }
    private void CopyAddress_Click(object sender, RoutedEventArgs e) => CopyText(ConnectAddressBox.Text, "已复制连接地址");
    private void CopyLog_Click(object sender, RoutedEventArgs e) => CopyText(LogTextBox.Text, "已复制日志");

    private async void ReadLog_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null) return;
        LogPanel.IsExpanded = true;
        await RunOperationAsync("正在读取联机日志…", async token =>
        {
            var log = await _terracotta.ReadLogAsync(_session.Endpoint, token);
            token.ThrowIfCancellationRequested();
            LogTextBox.Text = DiagnosticRedactionService.Sanitize(log, 60000);
            LogTextBox.ScrollToEnd();
        }, "联机日志已更新。");
    }

    private async void OpenProgram_Click(object sender, RoutedEventArgs e)
        => await RunOperationAsync("正在打开陶瓦程序…", token => Task.Run(() => _terracotta.Launch(_owner.ConfigService.Config.TerracottaExecutablePath), token), "已打开陶瓦程序。");

    private void ExecutableOptions_Click(object sender, RoutedEventArgs e)
    {
        if (ExecutableOptionsButton.ContextMenu == null) return;
        ExecutableOptionsButton.ContextMenu.PlacementTarget = ExecutableOptionsButton;
        ExecutableOptionsButton.ContextMenu.IsOpen = true;
    }

    private void BrowseExecutable_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择陶瓦联机可执行文件", Filter = "可执行文件|*.exe" };
        if (dialog.ShowDialog() != true) return;
        _owner.ConfigService.Config.TerracottaExecutablePath = dialog.FileName;
        PersistConfig();
        InvalidateSession();
    }

    private void ResetExecutable_Click(object sender, RoutedEventArgs e)
    {
        _owner.ConfigService.Config.TerracottaExecutablePath = null;
        PersistConfig();
        InvalidateSession();
    }

    private void InvalidateSession()
    {
        _session = null;
        _state = null;
        _stateFresh = false;
        _shareCode = _rememberedJoinCode = null;
        _membersSignature = null;
        _joinedGeneration = null;
        CurrentRoomPanel.Visibility = Visibility.Collapsed;
        SetNotice("程序选项已更新。点击“启动联机”连接所选程序。已有房间不会因切换程序选项而自动断开。");
        UpdateControls();
    }
}
