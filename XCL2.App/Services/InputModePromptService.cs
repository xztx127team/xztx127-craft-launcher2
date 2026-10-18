using System.Security;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using XCL2.App.Views;

namespace XCL2.App.Services;

/// <summary>
/// 仅为每次游戏启动的输入模式选择提供系统通知，不改变其它弹窗/Toast 的行为。
/// Toolkit 为便携版 WPF 注册桌面通知身份和 COM 激活，通知中心中的按钮也能回到本进程。
/// </summary>
internal sealed class InputModePromptService : IDisposable
{
    private const string NotificationGroup = "input-mode";
    private readonly Window _owner;
    private readonly InputModeSelectDialog _dialog;
    private readonly Action _restoreOwner;
    private readonly CancellationToken _cancellationToken;
    private readonly InputModeNotificationRequest _request = new();
    private CancellationTokenRegistration _cancellationRegistration;
    private ToastNotifierCompat? _notifier;
    private ToastNotification? _notification;
    private bool _loaded;
    private bool _subscribed;
    private bool _disposed;
    private bool _notificationUnavailable;
    private bool _fallbackPending;

    internal static InputModeSelectDialog Show(Window owner, bool defaultTouch, int countdownSeconds,
        Action restoreOwner, CancellationToken cancellationToken)
    {
        owner.Dispatcher.VerifyAccess();
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new InputModeSelectDialog(defaultTouch, countdownSeconds);
        using var prompt = new InputModePromptService(owner, dialog, restoreOwner, cancellationToken);
        dialog.ShowDialog();
        return dialog;
    }

    private InputModePromptService(Window owner, InputModeSelectDialog dialog, Action restoreOwner,
        CancellationToken cancellationToken)
    {
        _owner = owner;
        _dialog = dialog;
        _restoreOwner = restoreOwner;
        _cancellationToken = cancellationToken;
        owner.Activated += OwnerVisibilityChanged;
        owner.Deactivated += OwnerVisibilityChanged;
        owner.StateChanged += OwnerVisibilityChanged;
        owner.IsVisibleChanged += OwnerIsVisibleChanged;
        owner.Closed += OwnerClosed;
        dialog.Loaded += DialogLoaded;
        dialog.RequestClose += DialogClosed;
        dialog.OverlayClosed += DialogClosed;
        _cancellationRegistration = cancellationToken.Register(() => Dispatch(() =>
        {
            if (_disposed) return;
            _request.Close();
            RemoveNotification();
            if (OverlayDialogService.IsOpen(_dialog)) _dialog.Close();
        }));
    }

    private void DialogLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        RefreshNotification();
    }

    private bool IsBackground => !_owner.IsVisible || _owner.WindowState == WindowState.Minimized || !_owner.IsActive;
    private bool IsPending => !_disposed && _request.IsPending && !_cancellationToken.IsCancellationRequested &&
        _loaded && OverlayDialogService.IsOpen(_dialog);

    private void OwnerVisibilityChanged(object? sender, EventArgs e) => RefreshNotification();
    private void OwnerIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => RefreshNotification();
    private void OwnerClosed(object? sender, EventArgs e) => Dispose();
    private void DialogClosed(object? sender, bool? result) => Dispose();

    private void RefreshNotification()
    {
        if (!IsPending || !IsBackground)
        {
            RemoveNotification();
            return;
        }
        if (_notificationUnavailable)
        {
            RestoreForFallback();
            return;
        }
        if (_notification != null) return;

        try
        {
            if (!_subscribed)
            {
                ToastNotificationManagerCompat.OnActivated += NotificationActivated;
                _subscribed = true;
            }
            _notifier ??= ToastNotificationManagerCompat.CreateToastNotifier();
            if (_notifier.Setting != NotificationSetting.Enabled)
                throw new InvalidOperationException("Windows 已禁用启动器的系统通知。");

            var seconds = _dialog.RemainingCountdownSeconds;
            var hint = seconds is > 0
                ? $"请为本次游戏选择操作方式；未选择将在约 {seconds} 秒后使用键鼠模式。"
                : "请为本次游戏选择操作方式；点击通知正文可返回启动器。";
            var xml = new XmlDocument();
            xml.LoadXml($"""
                <toast launch="{_request.Argument(InputModeNotificationAction.Open)}" duration="long">
                  <visual><binding template="ToastGeneric">
                    <text>XCL2 · 本次启动使用哪种操作方式？</text>
                    <text>{SecurityElement.Escape(hint)}</text>
                    <text>触屏模式为实验功能，会加载虚拟按键悬浮层；按钮仅对本次启动生效。</text>
                  </binding></visual>
                  <actions>
                    <action content="键鼠模式" arguments="{_request.Argument(InputModeNotificationAction.Keyboard)}" activationType="foreground" />
                    <action content="触屏模式" arguments="{_request.Argument(InputModeNotificationAction.Touch)}" activationType="foreground" />
                  </actions>
                </toast>
                """);
            _notification = new ToastNotification(xml)
            {
                Tag = _request.Tag,
                Group = NotificationGroup,
                ExpirationTime = seconds is > 0 ? DateTimeOffset.UtcNow.AddSeconds(seconds.Value) : DateTimeOffset.UtcNow.AddDays(1)
            };
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
                _notification.ExpiresOnReboot = true;
            _notification.Failed += NotificationFailed;
            // 不因横幅消失或用户划走就关闭选择：通知仍可在通知中心打开，倒计时只由原弹窗管理。
            _notifier.Show(_notification);
        }
        catch (Exception ex)
        {
            NotificationUnavailable(ex.Message);
        }
    }

    private void NotificationActivated(ToastNotificationActivatedEventArgsCompat args)
    {
        // COM 回调不在 UI 线程上；在 Dispatcher 上再次核对凭据和弹窗是否仍在等待。
        var argument = args.Argument;
        Dispatch(() =>
        {
            if (!IsPending || !_request.TryGetAction(argument, out var action)) return;
            if (action == InputModeNotificationAction.Open)
            {
                _dialog.PauseAutoSelection();
                _restoreOwner();
                RemoveNotification();
                return;
            }
            // 触屏随后可能有旧版兼容确认，先恢复窗口，确保后续确认也能看见。
            if (action == InputModeNotificationAction.Touch) _restoreOwner();
            if (IsPending) _dialog.TrySelectFromNotification(action == InputModeNotificationAction.Touch);
        });
    }

    private void NotificationFailed(ToastNotification sender, ToastFailedEventArgs args)
        => Dispatch(() => NotificationUnavailable(args.ErrorCode.Message));

    private void NotificationUnavailable(string reason)
    {
        if (!IsPending) return;
        _notificationUnavailable = true;
        RemoveNotification();
        LauncherLogService.AppendLine("[操作模式通知] 系统通知不可用，已切回启动器选择：" + reason);
        RestoreForFallback();
    }

    private void RestoreForFallback()
    {
        if (_fallbackPending || !IsPending || !IsBackground) return;
        _fallbackPending = true;
        // 恢复窗口会触发 Activated/StateChanged，排队后执行避免在这些事件里重入。
        Dispatch(() =>
        {
            _fallbackPending = false;
            if (!IsPending || !IsBackground) return;
            _dialog.PauseAutoSelection();
            _restoreOwner();
        });
    }

    private void Dispatch(Action action)
    {
        if (_owner.Dispatcher.HasShutdownStarted || _owner.Dispatcher.HasShutdownFinished) return;
        try
        {
            _owner.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                if (_disposed) return;
                try { action(); }
                catch (Exception ex) { ErrorPresenter.LogFallback("处理操作模式通知失败", ex); }
            }));
        }
        catch (InvalidOperationException) when (_owner.Dispatcher.HasShutdownStarted || _owner.Dispatcher.HasShutdownFinished)
        {
            // 通知的 COM 回调可能恰好遇到主窗口关闭，不再访问已结束的 UI。
        }
    }

    private void RemoveNotification()
    {
        var notification = _notification;
        _notification = null;
        if (notification == null) return;
        notification.Failed -= NotificationFailed;
        try { _notifier?.Hide(notification); }
        catch (Exception ex) { LauncherLogService.AppendLine("[操作模式通知] 隐藏通知失败：" + ex.Message); }
        try { ToastNotificationManagerCompat.History.Remove(_request.Tag, NotificationGroup); }
        catch (Exception ex) { LauncherLogService.AppendLine("[操作模式通知] 清理通知中心失败：" + ex.Message); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _request.Close();
        RemoveNotification();
        _cancellationRegistration.Dispose();
        if (_subscribed) ToastNotificationManagerCompat.OnActivated -= NotificationActivated;
        _owner.Activated -= OwnerVisibilityChanged;
        _owner.Deactivated -= OwnerVisibilityChanged;
        _owner.StateChanged -= OwnerVisibilityChanged;
        _owner.IsVisibleChanged -= OwnerIsVisibleChanged;
        _owner.Closed -= OwnerClosed;
        _dialog.Loaded -= DialogLoaded;
        _dialog.RequestClose -= DialogClosed;
        _dialog.OverlayClosed -= DialogClosed;
    }
}
