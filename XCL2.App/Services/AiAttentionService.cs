using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace XCL2.App.Services;

/// <summary>Independent desktop notices, never owned by a minimized launcher and never activated on arrival.</summary>
internal static class AiAttentionService
{
    private static readonly List<NoticeWindow> Notices = new();

    public static IDisposable Track(Func<Window?> owner, Func<bool> isPresented, string title, string message,
        Action activate, Action<AiElevationDecision>? answer = null)
        => new PendingNotice(owner, isPresented, title, message, activate, answer);

    private sealed class PendingNotice : IDisposable
    {
        private readonly Func<Window?> _owner;
        private readonly Func<bool> _isPresented;
        private readonly string _title, _message;
        private readonly Action _activate;
        private readonly Action<AiElevationDecision>? _answer;
        private readonly DispatcherTimer _timer;
        private NoticeWindow? _notice;
        private bool _dismissed, _disposed;

        public PendingNotice(Func<Window?> owner, Func<bool> isPresented, string title, string message,
            Action activate, Action<AiElevationDecision>? answer)
        {
            _owner = owner; _isPresented = isPresented; _title = title; _message = message;
            _activate = activate; _answer = answer;
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(750) };
            _timer.Tick += (_, _) => Refresh();
            _timer.Start(); Refresh();
        }

        private void Refresh()
        {
            if (_disposed) return;
            var owner = _owner();
            var foreground = owner != null && owner.IsVisible && owner.WindowState != WindowState.Minimized && owner.IsActive && _isPresented();
            if (foreground)
            {
                CloseNotice(); _dismissed = false;
                return;
            }
            if (_notice != null || _dismissed) return;
            var notice = new NoticeWindow(_title, _message, () => { CloseNotice(); _activate(); }, _answer);
            _notice = notice;
            notice.Dismissed += () => { if (ReferenceEquals(_notice, notice)) { _dismissed = true; _notice = null; } };
            notice.Show();
        }

        private void CloseNotice()
        {
            var notice = _notice; _notice = null;
            notice?.Close();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; _timer.Stop(); CloseNotice();
        }
    }

    private sealed class NoticeWindow : Window, IGlobalWindowTreatmentExempt
    {
        public event Action? Dismissed;
        public NoticeWindow(string title, string message, Action activate, Action<AiElevationDecision>? answer)
        {
            Title = "AI 助手等待回应";
            Width = 400; MaxHeight = Math.Max(180, SystemParameters.WorkArea.Height - 60);
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false; ShowActivated = false; Topmost = true; AllowsTransparency = true;
            Background = Brushes.Transparent;
            var body = new StackPanel();
            var heading = new DockPanel();
            var close = new Button { Content = "×", Width = 25, Height = 25, Padding = new Thickness(0), ToolTip = "收起此通知（问题仍保留在对话中）" };
            close.Click += (_, e) => { e.Handled = true; Close(); };
            DockPanel.SetDock(close, Dock.Right); heading.Children.Add(close);
            heading.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(heading);
            var text = new TextBlock { Text = message.Length > 550 ? message[..550] + "…" : message,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8), MaxHeight = 120 };
            body.Children.Add(text);
            var open = new Button { Content = "打开对应对话", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(9, 5, 9, 5) };
            open.Click += (_, _) => activate(); body.Children.Add(open);
            text.Cursor = Cursors.Hand; text.MouseLeftButtonUp += (_, e) => { e.Handled = true; activate(); };
            if (answer != null)
            {
                var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
                foreach (var (label, choice) in new[] { ("允许一次", AiElevationDecision.AllowOnce), ("允许全部", AiElevationDecision.AllowSession), ("不允许", AiElevationDecision.Deny) })
                {
                    var button = new Button { Content = label, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(0, 0, 5, 0) };
                    if (choice == AiElevationDecision.AllowSession)
                        button.ToolTip = "仅当前对话、本次打开期间的管理员重试。普通命令仍按原规则审批，Windows UAC 仍需确认。";
                    button.Click += (_, _) => { answer(choice); Close(); }; buttons.Children.Add(button);
                }
                body.Children.Add(buttons);
            }
            var border = new Border { Child = body, Padding = new Thickness(14), Margin = new Thickness(5), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
            border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
            border.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            border.SetResourceReference(TextElement.ForegroundProperty, "TextPrimaryBrush");
            Content = border;
            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x08000000 | 0x00000080);
            };
            Loaded += (_, _) => { Notices.Add(this); AiAttentionService.Arrange(); };
            SizeChanged += (_, _) => AiAttentionService.Arrange();
            Closed += (_, _) => { Notices.Remove(this); AiAttentionService.Arrange(); Dismissed?.Invoke(); };
        }
    }

    private static void Arrange()
    {
        var work = SystemParameters.WorkArea;
        var bottom = work.Bottom - 12;
        foreach (var notice in Notices.AsEnumerable().Reverse())
        {
            notice.Left = work.Right - notice.Width - 12;
            var height = notice.ActualHeight > 0 ? notice.ActualHeight : 200;
            notice.Top = Math.Max(work.Top + 8, bottom - height);
            bottom = notice.Top - 8;
        }
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
