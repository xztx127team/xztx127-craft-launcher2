using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 独立设置窗口的消息宿主。保持原 Owner 和模态关系，不唤醒被该窗口禁用的主窗口。
/// 正常提示统一使用 Popup 时长/强度，启动失败和崩溃兜底仍可使用系统 MessageBox。
/// </summary>
internal static class OwnedMessageDialog
{
    private sealed record Choice(string Text, MessageBoxResult Result);

    internal static MessageBoxResult Show(Window owner, string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.Information)
    {
        var (choices, dismiss) = buttons switch
        {
            MessageBoxButton.OK => (new[] { new Choice("确定", MessageBoxResult.OK) }, MessageBoxResult.OK),
            MessageBoxButton.OKCancel => (new[] { new Choice("确定", MessageBoxResult.OK), new Choice("取消", MessageBoxResult.Cancel) }, MessageBoxResult.Cancel),
            MessageBoxButton.YesNo => (new[] { new Choice("是", MessageBoxResult.Yes), new Choice("否", MessageBoxResult.No) }, MessageBoxResult.None),
            MessageBoxButton.YesNoCancel => (new[] { new Choice("是", MessageBoxResult.Yes), new Choice("否", MessageBoxResult.No), new Choice("取消", MessageBoxResult.Cancel) }, MessageBoxResult.Cancel),
            _ => throw new ArgumentOutOfRangeException(nameof(buttons))
        };
        return ShowCore(owner, message, title, choices, choices[0].Result, dismiss, image);
    }

    internal static bool ConfirmCustom(Window owner, string message, string title, string cancelText, string confirmText)
        => ShowCore(owner, message, title,
            new[] { new Choice(cancelText, MessageBoxResult.No), new Choice(confirmText, MessageBoxResult.Yes) },
            MessageBoxResult.No, MessageBoxResult.No, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private static MessageBoxResult ShowCore(Window owner, string message, string title, Choice[] choices,
        MessageBoxResult defaultResult, MessageBoxResult dismissResult, MessageBoxImage image)
    {
        ArgumentNullException.ThrowIfNull(owner);
        owner.Dispatcher.VerifyAccess();
        // 异步模型测试/保存回调可能晚于其独立宿主关闭。不要重新创建/唤醒窗口，
        // 也不要把“宿主失效”当成用户确认。仅读取现有 HWND，不调用 EnsureHandle。
        if (owner.Dispatcher.HasShutdownStarted || new WindowInteropHelper(owner).Handle == IntPtr.Zero)
            return dismissResult;
        var maximumHeight = Math.Max(220, SystemParameters.WorkArea.Height - 48);
        var window = new Window
        {
            Owner = owner, Title = title, Width = Math.Min(500, Math.Max(260, SystemParameters.WorkArea.Width - 40)),
            SizeToContent = SizeToContent.Height, MaxHeight = maximumHeight,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = owner.Topmost,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        window.SetResourceReference(Window.BackgroundProperty, "PanelBrush");
        window.SetResourceReference(Window.ForegroundProperty, "TextPrimaryBrush");
        var body = new Grid { Margin = new Thickness(22) };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var messageRow = new Grid();
        messageRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        messageRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var (glyph, color) = image switch
        {
            MessageBoxImage.Warning => ("⚠", "WarningTextBrush"),
            MessageBoxImage.Error => ("✖", "DangerBrush"),
            MessageBoxImage.Question => ("?", "AccentBrush"),
            _ => ("ℹ", "AccentBrush")
        };
        var icon = new TextBlock
        {
            Text = glyph, FontSize = 25, FontFamily = new FontFamily("Segoe UI Emoji"),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 14, 0)
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, color);
        messageRow.Children.Add(icon);
        var messageText = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 21 };
        var scroll = new ScrollViewer
        {
            Content = messageText, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = Math.Max(100, maximumHeight - 140)
        };
        Grid.SetColumn(scroll, 1);
        messageRow.Children.Add(scroll);
        body.Children.Add(messageRow);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        Grid.SetRow(buttons, 1);
        body.Children.Add(buttons);
        window.Content = body;
        if (dismissResult == MessageBoxResult.None)
        {
            window.SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(window).Handle;
                var menu = GetSystemMenu(handle, false);
                if (menu != IntPtr.Zero) EnableMenuItem(menu, 0xF060, 0x0001); // SC_CLOSE / MF_BYCOMMAND | MF_GRAYED
                DrawMenuBar(handle);
            };
        }

        var result = dismissResult;
        var selected = false;
        var closing = false;
        var committed = false;
        var closed = false;
        void Choose(MessageBoxResult value)
        {
            if (closing || closed) return;
            selected = true;
            result = value;
            window.Close();
        }
        foreach (var choice in choices)
        {
            var button = new Button
            {
                Content = choice.Text, IsDefault = choice.Result == defaultResult,
                MinWidth = 76, Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(8, 0, 0, 0),
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
            };
            button.SetResourceReference(FrameworkElement.StyleProperty, choice.Result == defaultResult ? "PrimaryButton" : "SecondaryButton");
            button.Click += (_, _) => Choose(choice.Result);
            buttons.Children.Add(button);
        }
        void OnClosing(object? sender, CancelEventArgs args)
        {
            if (committed || owner.Dispatcher.HasShutdownStarted) return;
            args.Cancel = true;
            if (closing || (!selected && dismissResult == MessageBoxResult.None)) return;
            closing = true;
            body.IsEnabled = false;
            UiAnimationHelper.FadeOut(body, () => window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (closed) return;
                committed = true;
                window.Close();
            })), from: body.Opacity);
        }
        window.Closing += OnClosing;
        window.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape || dismissResult == MessageBoxResult.None) return;
            args.Handled = true;
            Choose(dismissResult);
        };
        window.IsVisibleChanged += (_, args) =>
        {
            if ((bool)args.NewValue && !closing) UiAnimationHelper.PopupFadeIn(body);
            else if (!(bool)args.NewValue) UiAnimationHelper.Reset(body);
        };
        window.Closed += (_, _) => { closed = true; UiAnimationHelper.Reset(body); };
        // 主窗口正在退出时也结束本地模态等待；不持有独立设置窗口到下一次打开。
        void OwnerClosed(object? sender, EventArgs args)
        {
            result = dismissResult;
            if (closed) return;
            committed = true;
            window.Close();
        }
        owner.Closed += OwnerClosed;
        try { window.ShowDialog(); }
        finally
        {
            owner.Closed -= OwnerClosed;
            UiAnimationHelper.Reset(body);
        }
        return result;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool revert);

    [DllImport("user32.dll")]
    private static extern uint EnableMenuItem(IntPtr menu, uint item, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawMenuBar(IntPtr window);
}
