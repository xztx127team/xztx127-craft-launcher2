using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// The only input island over a click-through performance window. Keep this HWND exactly the
/// size of its button: a transparent full-width toolbar would still block the game underneath.
/// </summary>
internal sealed class PerformanceOverlayHandleWindow : Window, IGlobalWindowTreatmentExempt
{
    internal const double HandleSize = 14;
    private readonly Window _overlay;
    private readonly Func<bool> _canMove;
    private readonly Action<bool> _movingChanged;
    private readonly Button _button;
    private HwndSource? _source;
    private NativePoint? _pressPoint;
    private NativeRect _pressWindow;
    private bool _dragging;
    private bool _closed;

    internal PerformanceOverlayHandleWindow(Window overlay, Action<FrameworkElement> openMenu,
        Func<bool> canMove, Action<bool> movingChanged)
    {
        _overlay = overlay;
        _canMove = canMove;
        _movingChanged = movingChanged;
        Title = "性能悬浮窗设置";
        Width = Height = HandleSize;
        MinWidth = MinHeight = 0;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Owner = overlay;
        WindowStartupLocation = WindowStartupLocation.Manual;
        _button = new Button
        {
            Width = HandleSize, Height = HandleSize, MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(0), Margin = new Thickness(0), BorderThickness = new Thickness(1),
            Focusable = false, IsTabStop = false, Cursor = Cursors.Hand, Style = null,
            SnapsToDevicePixels = true, UseLayoutRounding = true, Template = CreateButtonTemplate()
        };
        // Geometry survives missing icon fonts and application-wide font replacements.
        var icon = new System.Windows.Shapes.Path
        {
            Data = CreateGearGeometry(), Width = 12, Height = 12, Stretch = Stretch.Uniform,
            IsHitTestVisible = false, SnapsToDevicePixels = true
        };
        icon.SetBinding(System.Windows.Shapes.Shape.FillProperty, new Binding(nameof(Control.Foreground)) { Source = _button });
        _button.Content = icon;
        AutomationProperties.SetName(_button, "性能悬浮窗设置；按住拖动可移动");
        Content = _button;
        _button.Click += (_, _) => openMenu(_button);
        _button.PreviewMouseLeftButtonDown += BeginPointer;
        _button.PreviewMouseMove += MovePointer;
        _button.PreviewMouseLeftButtonUp += EndPointer;
        _button.LostMouseCapture += (_, _) => FinishPointer();
        _button.PreviewMouseRightButtonUp += (_, e) => { e.Handled = true; openMenu(_button); };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _source = HwndSource.FromHwnd(hwnd);
            _source?.AddHook(WindowHook);
            ApplyInputStyle(hwnd);
        };
        Loaded += (_, _) => ApplyInputStyle(new WindowInteropHelper(this).Handle);
        IsVisibleChanged += (_, _) => { if (!IsVisible) FinishPointer(); };
        Closed += (_, _) =>
        {
            _closed = true;
            FinishPointer();
            _source?.RemoveHook(WindowHook);
            _source = null;
        };
    }

    private static ControlTemplate CreateButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "GearButtonBorder";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        border.SetBinding(Border.BackgroundProperty, new Binding(nameof(Control.Background))
            { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, .8, "GearButtonBorder"));
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, .6, "GearButtonBorder"));
        template.Triggers.Add(pressed);
        return template;
    }

    private static Geometry CreateGearGeometry()
    {
        // Eight distinct teeth and a circular aperture, in a 12×12 viewbox.
        var outline = new StreamGeometry();
        using (var context = outline.Open())
        {
            var first = true;
            for (var tooth = 0; tooth < 8; tooth++)
            {
                foreach (var (offset, radius) in new[] { (-19d, 4.35), (-12d, 5.9), (12d, 5.9), (19d, 4.35) })
                {
                    var angle = (tooth * 45 + offset) * Math.PI / 180;
                    var point = new Point(6 + Math.Cos(angle) * radius, 6 + Math.Sin(angle) * radius);
                    if (first) { context.BeginFigure(point, true, true); first = false; }
                    else context.LineTo(point, true, false);
                }
            }
        }
        var gear = new GeometryGroup { FillRule = FillRule.EvenOdd };
        gear.Children.Add(outline);
        gear.Children.Add(new EllipseGeometry(new Point(6, 6), 2.15, 2.15));
        gear.Freeze();
        return gear;
    }

    internal void ApplyAppearance(bool topmost, bool locked, bool clickThrough,
        Brush background, Brush foreground, Brush border)
    {
        Topmost = topmost;
        _button.Background = background;
        _button.Foreground = foreground;
        _button.BorderBrush = border;
        _button.ToolTip = locked
            ? "点击打开性能设置。位置已锁定，可在菜单中解锁。"
            : "点击打开性能设置；按住并拖动此齿轮可移动悬浮窗。" +
              (clickThrough ? "其余区域均可鼠标穿透。" : "也可拖动悬浮窗内容区。");
    }

    internal void PositionAt(Point screenPoint)
    {
        if (_closed) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var dpi = VisualTreeHelper.GetDpi(_overlay);
        SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y),
            (int)Math.Ceiling(HandleSize * dpi.DpiScaleX), (int)Math.Ceiling(HandleSize * dpi.DpiScaleY),
            0x0004 | 0x0010); // no z-order or activation changes
    }

    private void BeginPointer(object sender, MouseButtonEventArgs e)
    {
        FinishPointer();
        if (!_canMove() || !GetCursorPos(out var point) ||
            !GetWindowRect(new WindowInteropHelper(_overlay).Handle, out _pressWindow)) return;
        _pressPoint = point;
        // Button owns capture and normal click semantics until a drag crosses the system threshold.
    }

    private void MovePointer(object sender, MouseEventArgs e)
    {
        if (_pressPoint is not { } start || e.LeftButton != MouseButtonState.Pressed || !GetCursorPos(out var point)) return;
        if (!_canMove()) { FinishPointer(); return; }
        var dx = point.X - start.X;
        var dy = point.Y - start.Y;
        var dpi = VisualTreeHelper.GetDpi(this);
        if (!_dragging && Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX
            && Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY) return;
        if (!_dragging)
        {
            _dragging = true;
            _movingChanged(true);
        }
        // Physical pixels avoid a jump when the drag crosses monitors with different scaling.
        SetWindowPos(new WindowInteropHelper(_overlay).Handle, IntPtr.Zero,
            _pressWindow.Left + dx, _pressWindow.Top + dy, 0, 0, 0x0001 | 0x0004 | 0x0010);
        e.Handled = true;
    }

    private void EndPointer(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) { _pressPoint = null; return; }
        e.Handled = true; // A drag must not also open the settings menu on release.
        _button.ReleaseMouseCapture();
        FinishPointer();
    }

    private void FinishPointer()
    {
        _pressPoint = null;
        if (!_dragging) return;
        _dragging = false;
        _movingChanged(false);
    }

    private IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x007C && unchecked((int)wParam.ToInt64()) == -20 && lParam != IntPtr.Zero)
        {
            var style = Marshal.PtrToStructure<StyleChange>(lParam);
            style.NewStyle = (style.NewStyle | 0x00080000 | 0x08000000 | 0x00000080) & ~0x00000020;
            Marshal.StructureToPtr(style, lParam, false);
        }
        if (message == 0x0021) // MA_NOACTIVATE; clicks and drag do not take focus from the game.
        {
            handled = true;
            return new IntPtr(3);
        }
        return IntPtr.Zero;
    }

    private static void ApplyInputStyle(IntPtr hwnd)
    {
        var style = (GetWindowLong(hwnd, -20) | 0x00080000 | 0x08000000 | 0x00000080) & ~0x00000020;
        SetWindowLong(hwnd, -20, style);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct StyleChange { public int OldStyle; public int NewStyle; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
