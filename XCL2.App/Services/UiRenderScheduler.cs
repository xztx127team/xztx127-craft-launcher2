using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace XCL2.App.Services;

/// <summary>让已排队的 Loaded、布局和一次合成提交先完成；不在 Rendering 回调中执行页面业务。</summary>
public static class UiRenderScheduler
{
    public static Task<bool> AfterRenderAsync(FrameworkElement element, CancellationToken cancellationToken = default,
        bool waitForVisible = false)
    {
        element.Dispatcher.VerifyAccess();
        if (cancellationToken.IsCancellationRequested || element.Dispatcher.HasShutdownStarted)
            return Task.FromResult(false);

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = element.Dispatcher;
        var finished = false;
        var armed = false;
        Window? window = null;
        DispatcherOperation? continuation = null;
        CancellationTokenRegistration registration = default;
        var fallback = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            { Interval = TimeSpan.FromMilliseconds(250) };

        void Finish(bool rendered)
        {
            if (finished) return;
            finished = true;
            CompositionTarget.Rendering -= Rendered;
            element.Loaded -= Loaded;
            element.Unloaded -= Unloaded;
            element.IsVisibleChanged -= VisibilityChanged;
            dispatcher.ShutdownStarted -= Shutdown;
            if (window != null) window.StateChanged -= WindowStateChanged;
            fallback.Stop();
            fallback.Tick -= TimedOut;
            if (continuation?.Status == DispatcherOperationStatus.Pending) continuation.Abort();
            registration.Dispose();
            completion.TrySetResult(rendered && !cancellationToken.IsCancellationRequested);
        }
        void Rendered(object? sender, EventArgs e)
        {
            CompositionTarget.Rendering -= Rendered;
            // Rendering 位于提交之前；只排续步，先让本次布局/合成调用栈返回。
            // 等本次提交调用栈返回即可，不等待 ContextIdle。启动/下载繁忙时
            // 空闲队列可能迟迟排不到，让已准备的低透明度弹窗停在起点数秒。
            continuation = dispatcher.BeginInvoke(DispatcherPriority.Render,
                new Action(() => Finish(true)));
        }
        void Arm()
        {
            if (finished || armed || !element.IsLoaded) return;
            window = Window.GetWindow(element);
            if (!element.IsVisible || window?.WindowState == WindowState.Minimized)
            {
                // Show 的窗口可见事件可能早于内容的 Loaded/IsVisible 传播。
                // 恢复准备保留起点，等内容真的可见再挂 Rendering，不提前消耗时钟。
                if (waitForVisible && window?.WindowState != WindowState.Minimized) return;
                Finish(false);
                return;
            }
            armed = true;
            if (window != null) window.StateChanged += WindowStateChanged;
            CompositionTarget.Rendering += Rendered;
            fallback.Start();
        }
        void Loaded(object sender, RoutedEventArgs e) => Arm();
        void Unloaded(object sender, RoutedEventArgs e)
        {
            // 快速 Hide→Show 时旧 Unloaded 可能仍在队列里。尚未挂上渲染监听的准备
            // 等待新 Loaded；真正已开始呈现后再卸载则取消。调用者的 Reset 仍可随时取消。
            if (armed || !waitForVisible) Finish(false);
        }
        void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (armed && !(bool)e.NewValue) Finish(false);
            else if (waitForVisible && (bool)e.NewValue) Arm();
        }
        void WindowStateChanged(object? sender, EventArgs e)
        {
            if (window?.WindowState == WindowState.Minimized) Finish(false);
        }
        void Shutdown(object? sender, EventArgs e) => Finish(false);
        void TimedOut(object? sender, EventArgs e) => Finish(false);

        element.Loaded += Loaded;
        element.Unloaded += Unloaded;
        element.IsVisibleChanged += VisibilityChanged;
        dispatcher.ShutdownStarted += Shutdown;
        fallback.Tick += TimedOut;
        registration = cancellationToken.Register(() =>
        {
            if (dispatcher.CheckAccess()) Finish(false);
            else if (!dispatcher.HasShutdownStarted)
                dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => Finish(false)));
        });
        if (finished) registration.Dispose();
        else
        {
            // 连从未挂载的元素也有界结束，避免 ShutdownStarted 长期保留准备状态。
            fallback.Start();
            Arm();
        }
        return completion.Task;
    }
}
