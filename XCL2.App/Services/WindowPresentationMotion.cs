using System.Windows;
using System.Windows.Media;

namespace XCL2.App.Services;

/// <summary>
/// 原生窗口仍由 Windows 移动/缩放；内容层在恢复后的可绘制帧上补一次轻量过渡。
/// Hide → Show 不一定发生 WindowState 变化，因此同时观察显隐状态。
/// </summary>
public sealed class WindowPresentationMotion : IDisposable
{
    private readonly Window _window;
    private readonly FrameworkElement _surface;
    private readonly ScaleTransform _scale;
    private readonly Func<bool> _canAnimate;
    private WindowState _previousState;
    private bool _hasPresentedFrame;
    private bool _disposed;

    public WindowPresentationMotion(Window window, FrameworkElement surface, ScaleTransform scale,
        Func<bool>? canAnimate = null)
    {
        _window = window;
        _surface = surface;
        _scale = scale;
        _canAnimate = canAnimate ?? (() => true);
        _previousState = window.WindowState;
        window.ContentRendered += OnContentRendered;
        window.StateChanged += OnStateChanged;
        window.IsVisibleChanged += OnVisibilityChanged;
        window.Closed += OnClosed;
    }

    private void OnContentRendered(object? sender, EventArgs e) => _hasPresentedFrame = true;

    private void OnStateChanged(object? sender, EventArgs e)
    {
        var previous = _previousState;
        _previousState = _window.WindowState;
        if (_previousState == WindowState.Minimized)
        {
            UiAnimationHelper.Reset(_surface, scale: _scale);
            return;
        }
        if (previous == _previousState) return;
        Replay(previous == WindowState.Minimized ? AnimationKind.WindowRestore : AnimationKind.WindowFullscreen);
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!(bool)e.NewValue)
        {
            UiAnimationHelper.Reset(_surface, scale: _scale);
            return;
        }
        Replay(AnimationKind.WindowRestore);
    }

    private void Replay(AnimationKind kind)
    {
        if (_disposed || !_hasPresentedFrame || !_canAnimate() || !_window.IsVisible
            || _window.WindowState == WindowState.Minimized) return;
        // SettleScale 等待布局和合成提交后才启动时钟；仅淡入，保留文字和窗口的实际尺寸。
        UiAnimationHelper.SettleScale(_surface, _scale, kind: kind);
    }

    private void OnClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.ContentRendered -= OnContentRendered;
        _window.StateChanged -= OnStateChanged;
        _window.IsVisibleChanged -= OnVisibilityChanged;
        _window.Closed -= OnClosed;
        UiAnimationHelper.Reset(_surface, scale: _scale);
    }
}
