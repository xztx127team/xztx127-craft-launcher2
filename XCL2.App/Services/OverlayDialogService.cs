using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using XCL2.App.Services;
using XCL2.App.Services.Plugins;

namespace XCL2.App.Views;

public static class OverlayDialogService
{
    private sealed class OverlayEntry
    {
        public required object Content;
        public required TaskCompletionSource<bool?> Tcs;
        public required bool DismissOnBackgroundClick;
        /// <summary>是否允许按 Esc 关闭本弹窗。默认 true（跟系统对话框习惯一致）；
        /// 法律协议等"不允许跳过"的弹窗传 false 锁死 Esc，避免用户一个按键
        /// 就把必须显式表态的流程强行关掉。</summary>
        public required bool DismissOnEsc;
        /// <summary>真正挂到 OverlayContentHost 的内容。每个 IOverlayDialog 都包一层 PluginOverlayView，
        /// 这样所有弹窗天然拥有插件扩展区，而不是只有少数手工接过线的弹窗有扩展能力。</summary>
        public PluginOverlayView? PluginView;
        public object PresentedContent => PluginView ?? Content;
        public bool PluginOpenPublished;

        /// <summary>"停留 10 秒没有任何操作就提示一下 Esc 可以关闭"用的一次性计时器。
        /// 只有 DismissOnEsc 为 true 且用户没有关掉该提示（AppConfig.ShowEscCloseHint）
        /// 时才会创建；弹窗关闭时必须一并 Stop，否则弹窗都关了计时器还留着，
        /// Tick 里再去碰已经不在栈顶的弹窗状态，纯属浪费+隐患。</summary>
        public DispatcherTimer? EscHintTimer;
    }

    private static MainWindow? _host;
    private static readonly Stack<OverlayEntry> _stack = new();
    // Includes entries already popped while their exit animation is still running.
    private static readonly HashSet<OverlayEntry> _pending = new();

    /// <summary>已开始关闭动画的弹窗不再接受外部操作（例如通知中心的延迟点击）。</summary>
    public static bool IsOpen(IOverlayDialog dialog) => _stack.Any(entry => ReferenceEquals(entry.Content, dialog));

    /// <summary>MainWindow 构造完成后调用一次，把自己注册为宿主。整个进程只有一个
    /// MainWindow 实例，不需要支持多宿主。</summary>
    public static void Register(MainWindow host)
    {
        if (ReferenceEquals(_host, host)) return;
        if (_host is not null) HostClosed(_host, EventArgs.Empty);
        _host = host;
        host.Closed += HostClosed;
    }

    private static void HostClosed(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _host)) return;
        _host!.Closed -= HostClosed;
        _host = null;
        _stack.Clear();
        foreach (var entry in _pending.ToArray()) CompleteEntry(entry, false, hostClosing: true);
    }

    public static bool? ShowModal(IOverlayDialog dialog, bool dismissOnBackgroundClick = false, bool dismissOnEsc = true)
    {
        if (_host is null)
            throw new InvalidOperationException("OverlayDialogService 尚未 Register 宿主 MainWindow。");

        var tcs = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new OverlayEntry { Content = dialog, Tcs = tcs, DismissOnBackgroundClick = dismissOnBackgroundClick, DismissOnEsc = dismissOnEsc };

        void OnRequestClose(object? s, bool? result) => CloseEntry(entry, result);
        dialog.RequestClose += OnRequestClose;

        try { Push(entry, () => dialog.RequestClose -= OnRequestClose); }
        catch
        {
            dialog.RequestClose -= OnRequestClose;
            RemoveFromStack(entry);
            CompleteEntry(entry, false);
            throw;
        }

        // 局部消息泵：跟 Window.ShowDialog() 内部机制一致，这里手动跑一个 DispatcherFrame，
        // 在弹窗关闭（tcs 完成）之前一直"假装同步阻塞"，实际上 UI 线程消息循环没有真的
        // 停止，用户仍然能跟 Overlay 内容交互、动画能正常播放。
        var frame = new DispatcherFrame();
        tcs.Task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.PushFrame(frame);

        return tcs.Task.Result;
    }

    public static Task<bool?> ShowModalAsync(IOverlayDialog dialog, bool dismissOnBackgroundClick = false, bool dismissOnEsc = true)
    {
        if (_host is null)
            throw new InvalidOperationException("OverlayDialogService 尚未 Register 宿主 MainWindow。");

        var tcs = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new OverlayEntry { Content = dialog, Tcs = tcs, DismissOnBackgroundClick = dismissOnBackgroundClick, DismissOnEsc = dismissOnEsc };

        void OnRequestClose(object? s, bool? result) => CloseEntry(entry, result);
        dialog.RequestClose += OnRequestClose;

        try { Push(entry, () => dialog.RequestClose -= OnRequestClose); }
        catch
        {
            dialog.RequestClose -= OnRequestClose;
            RemoveFromStack(entry);
            CompleteEntry(entry, false);
            throw;
        }

        return tcs.Task;
    }

    /// <summary>非模态、无返回值的纯展示型 Overlay（极少数场景，比如设备码登录窗口那种
    /// "只是展示信息，用户自己去外部完成操作后再手动关闭"的情况）。dismissOnBackgroundClick
    /// 默认值同 ShowModal（默认 false，不允许点遮罩关闭），仍然支持 Esc 关闭，只是不强制
    /// 调用方等待结果。</summary>
    public static void ShowNonModal(IOverlayDialog dialog, bool dismissOnBackgroundClick = false, bool dismissOnEsc = true)
    {
        _ = ShowModalAsync(dialog, dismissOnBackgroundClick, dismissOnEsc);
    }

    private static readonly Dictionary<object, Action> _unsubscribers = new();

    private static void Push(OverlayEntry entry, Action unsubscribe)
    {
        _pending.Add(entry);
        // 冷知识提示属于当前顶层弹窗，进入下一层时收起旧提示。
        ToastService.DismissActionPrompt("esc-close-hint");
        if (_stack.TryPeek(out var previous))
        {
            previous.EscHintTimer?.Stop();
            previous.EscHintTimer = null;
        }
        _unsubscribers[entry.Content] = unsubscribe;
        if (entry.Content is IOverlayDialog dialog)
            entry.PluginView = new PluginOverlayView(dialog);

        // 已经有弹窗在显示：先把当前该"藏起来"（从 ContentHost 摘掉但保留在栈里），
        // 新弹窗压栈显示，形成"弹窗里弹弹窗"的层级观感。
        _stack.Push(entry);
        _host!.OverlayRenderEntry(entry.PresentedContent, animateIn: true,
            onShown: () => ScheduleShownEntry(entry));

        StartEscHintTimerIfNeeded(entry);
    }

    private static void ScheduleShownEntry(OverlayEntry entry)
    {
        // 0 强度时 onShown 同步调用；低于 Render 的队列仍保证先呈现原弹窗，
        // 再执行任意 DLL 工厂或打开订阅者。已关闭、被子层覆盖的条目不能重新激活。
        var host = _host;
        if (host is null || entry.Tcs.Task.IsCompleted) return;
        host.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (entry.Tcs.Task.IsCompleted || !_stack.TryPeek(out var current) || !ReferenceEquals(current, entry)) return;
            entry.PluginView?.ActivateExtensions();
            // 控件工厂也可以打开/关闭弹窗；重入后重新核对顶层生命周期。
            if (entry.Tcs.Task.IsCompleted || !_stack.TryPeek(out current) || !ReferenceEquals(current, entry)) return;
            if (entry.PluginOpenPublished || entry.Content is not IOverlayDialog openedDialog) return;
            entry.PluginOpenPublished = true;
            PluginUiRegistry.Publish(openedDialog, open: true);
        }));
    }

    /// <summary>
    /// "没有明显关闭按钮的内嵌弹窗，停留 10 秒用户还没有任何操作，弹一条 Esc 提示"。
    ///
    /// 只针对 DismissOnEsc=true 的弹窗——锁死 Esc 的弹窗（协议页等）本来就按不动，
    /// 提示"可以按 Esc"反而是误导。用户在设置页关掉该开关，或者点了 Toast 上的
    /// "以后不再提示"之后（见下面 Tick 里的 secondaryAction），AppConfig.ShowEscCloseHint
    /// 变成 false，这里直接跳过、不再创建计时器。
    ///
    /// 只在该弹窗还是"当前显示的最顶层"时才真的弹出提示——如果 10 秒内用户已经把它
    /// 关掉，或者又在它上面叠了一个新弹窗（该弹窗被"暂存"起来、不在栈顶了），都不应该
    /// 再冒出一条针对它的提示，那样要么是提示一个已经不存在的东西，要么是打断新弹窗的操作。
    ///
    /// Toast 走 ToastService.ShowActionPrompt：非模态、不抢焦点、不会把弹窗当前已经填的
    /// 内容清空或者打断用户正在进行的操作，纯粹是"多一条看不看都行的提示"，符合"不打断用户
    /// 当前操作、保留用户当前设置进度"的要求。
    /// </summary>
    private static void StartEscHintTimerIfNeeded(OverlayEntry entry)
    {
        entry.EscHintTimer?.Stop();
        entry.EscHintTimer = null;
        if (!entry.DismissOnEsc) return;
        if (_host?.ConfigService.Config.ShowEscCloseHint != true) return;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();

            // 弹窗已经不在了，或者已经不是栈顶（被新弹窗压住）：什么都不做。
            if (_host?.ConfigService.Config.ShowEscCloseHint != true) return;
            if (_stack.Count == 0 || !ReferenceEquals(_stack.Peek(), entry)) return;

            ToastService.ShowActionPrompt(
                "冷知识：按 Esc 键可以关闭当前弹窗",
                primaryText: "知道了",
                primaryAction: () => { },
                secondaryText: "以后不再提示",
                secondaryAction: () =>
                {
                    _host.ConfigService.Config.ShowEscCloseHint = false;
                    _host.ConfigService.Save();
                },
                key: "esc-close-hint",
                dismissible: true);
        };
        entry.EscHintTimer = timer;
        timer.Start();
    }

    /// <summary>关闭指定弹窗。正常用户操作关闭栈顶；插件注销时也可能请求关闭被子弹窗覆盖的父层，
    /// 因此不能把所有 RequestClose 都错误地解释成 CloseTop。</summary>
    private static void CloseEntry(OverlayEntry entry, bool? result)
    {
        if (!_stack.Contains(entry)) return;
        if (ReferenceEquals(_stack.Peek(), entry))
        {
            CloseTop(result);
            return;
        }

        RemoveFromStack(entry);
        CompleteEntry(entry, result);
    }

    private static void RemoveFromStack(OverlayEntry entry)
    {
        if (!_stack.Contains(entry)) return;
        var above = new Stack<OverlayEntry>();
        while (_stack.Count > 0)
        {
            var current = _stack.Pop();
            if (ReferenceEquals(current, entry)) break;
            above.Push(current);
        }
        while (above.Count > 0) _stack.Push(above.Pop());
    }

    /// <summary>关闭当前最顶层的弹窗，把结果回传给对应的 TaskCompletionSource，
    /// 并恢复显示上一层（如果有的话），否则收起整个 OverlayRoot。</summary>
    private static void CloseTop(bool? result)
    {
        if (_stack.Count == 0) return;

        var top = _stack.Pop();
        top.EscHintTimer?.Stop();
        top.EscHintTimer = null;
        // Esc/确定/叉号关闭弹窗后，提示随之向左退场，不留下孤立的冷知识卡片。
        ToastService.DismissActionPrompt("esc-close-hint");

        _host!.OverlayDismissEntry(top.PresentedContent, () =>
        {
            if (top.Tcs.Task.IsCompleted) return;
            CompleteEntry(top, result);
            // Completing the task can run a caller continuation that closes the host.
            var host = _host;
            if (host is null) return;

            if (_stack.Count > 0)
            {
                var previous = _stack.Peek();
                host.OverlayRenderEntry(previous.PresentedContent, animateIn: false,
                    onShown: () => ScheduleShownEntry(previous));
                StartEscHintTimerIfNeeded(previous);
            }
            else
            {
                host.OverlayHideRoot();
            }
        });
    }

    private static void CompleteEntry(OverlayEntry entry, bool? result, bool hostClosing = false)
    {
        if (!_pending.Remove(entry)) return;
        void Cleanup(Action action, string operation)
        {
            try { action(); }
            catch (Exception ex) { ErrorPresenter.LogFallback(operation, ex); }
        }
        if (_unsubscribers.Remove(entry.Content, out var unsubscribe)) Cleanup(unsubscribe, "解绑弹窗关闭请求失败");
        entry.EscHintTimer?.Stop();
        entry.EscHintTimer = null;
        if (entry.Content is IOverlayDialogLifecycle lifecycle)
            Cleanup(() => lifecycle.OnOverlayClosed(result), "通知弹窗已关闭失败");
        if (entry.Content is IDisposable disposable)
            Cleanup(disposable.Dispose, "释放弹窗资源失败");
        if (entry.PluginView is { } view) Cleanup(view.Dispose, "释放弹窗插件视图失败");
        entry.PluginView = null;
        // 所有关闭入口均先结束控件生命周期，再异步恢复调用方；单个清理失败不能悬挂等待者。
        entry.Tcs.TrySetResult(result);
        if (!entry.PluginOpenPublished || entry.Content is not IOverlayDialog dialog) return;
        entry.PluginOpenPublished = false;
        if (!hostClosing && _host is { } host && !host.Dispatcher.HasShutdownStarted)
            host.Dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => PluginUiRegistry.Publish(dialog, open: false, result)));
        else Cleanup(() => PluginUiRegistry.Publish(dialog, open: false, result), "通知插件弹窗关闭失败");
    }

    /// <summary>供 MainWindow 里遮罩点击事件调用：只有当前最顶层弹窗允许"点遮罩关闭"时
    /// 才生效，返回 null 结果（对应原来 Window 的"没有点确定/取消、直接叉掉"语义）。</summary>
    internal static void RequestDismissTopByBackgroundClick()
    {
        if (_stack.Count == 0) return;
        if (!_stack.Peek().DismissOnBackgroundClick) return;
        if (_stack.Peek().Content is IOverlayDialogDismissGuard guard) { guard.RequestDismiss(); return; }
        CloseTop(null);
    }

    /// <summary>供 MainWindow 里 Esc 按键处理调用，语义同上。多数弹窗允许 Esc 取消，
    /// 跟 Windows 系统对话框的通行习惯一致；但个别"必须显式表态、不允许跳过"的弹窗
    /// （例如首次启动的协议页）通过 ShowModal 的 dismissOnEsc: false 锁死 Esc，
    /// 此时按 Esc 不做任何事——修复"按 Esc 会强行关闭协议窗口"这类问题。</summary>
    internal static void RequestDismissTopByEscape()
    {
        if (_stack.Count == 0) return;
        if (!_stack.Peek().DismissOnEsc) return;
        if (_stack.Peek().Content is IOverlayDialogDismissGuard guard) { guard.RequestDismiss(); return; }
        CloseTop(null);
    }

    internal static bool TopHandlesDismissal => _stack.TryPeek(out var top) && top.Content is IOverlayDialogDismissGuard;

    internal static bool HasActiveOverlay => _pending.Count > 0;

    // 检查所有层，包括确认框后面的编辑器和正在淡出的内容。
    internal static bool BlocksHostClose => _pending.Any(entry =>
        entry.Content is IOverlayHostCloseGuard { IsHostCloseBlocked: true });
}
