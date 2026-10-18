using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Diagnostics;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 危险操作二次确认弹窗：关闭注册表功能 / 删除所有新增的启动器注册表项 / 清除本机痕迹
/// 三个操作共用。用户必须原样输入固定字符串 <see cref="RequiredCode"/>（"xztx127"）才能点
/// "确认执行"，纯粹起"确认你真的知道自己在做什么"的作用，不是真正的安全凭据。
/// </summary>
public partial class DangerousConfirmDialog : OverlayDialogControl
{
    public const string RequiredCode = "xztx127";

    private readonly string _requiredCode;
    private readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Stopwatch _heldFor = new();
    private bool _holdAccepted;
    private bool _holdingWithMouse;
    private bool NeedsHold => ConfigService.Active?.Config is { Ux50HoldDangerousConfirm: true } cfg
        && (_requiredCode == RequiredCode ? cfg.Ux50HoldSystemOperations : cfg.Ux50HoldForceLaunch);

    /// <summary>确认通过时为 true，取消/校验未通过关闭时为 false。</summary>
    public bool Confirmed { get; private set; }

    public DangerousConfirmDialog(string title, string message) : this(title, message, RequiredCode) { }

    /// <summary>
    /// 需求修复：强制启动损坏/未下载完成的版本需要单独一套确认码（"我确认"），
    /// 不能沿用全局统一的 "xztx127"——那个是"我知道这是危险的注册表/系统级操作"，
    /// 语义上和"我知道这个版本文件不全，仍要坚持启动"是两件不同的事，混用会让同一个
    /// 确认码在不同场景下失去针对性。加这个重载而不是新建一个几乎一样的弹窗类，
    /// 复用已有的输入校验/错误提示 UI 逻辑。
    /// </summary>
    public DangerousConfirmDialog(string title, string message, string requiredCode)
    {
        InitializeComponent();
        _requiredCode = requiredCode;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmCodeHintText.Text = $"请输入确认码 {_requiredCode} 以继续：";
        Loaded += (_, _) => ConfirmCodeBox.Focus();
        if (NeedsHold) { OkBtn.Content = "按住确认"; HoldHint.Visibility = Visibility.Visible; }
        _holdTimer.Tick += (_, _) =>
        {
            if (_holdingWithMouse ? !OkBtn.IsMouseOver || Mouse.LeftButton != MouseButtonState.Pressed : !OkBtn.IsKeyboardFocused || !Keyboard.IsKeyDown(Key.Space))
            { ResetHold(); return; }
            var progress = Math.Clamp(_heldFor.Elapsed.TotalMilliseconds / 1200, 0, 1);
            OkBtn.Content = $"按住 {progress:P0}";
            if (progress < 1) return;
            _holdAccepted = true; _holdTimer.Stop();
            Ok_Click(OkBtn, new RoutedEventArgs());
        };
        OkBtn.PreviewMouseLeftButtonDown += (_, _) => BeginHold(true);
        OkBtn.PreviewMouseLeftButtonUp += (_, _) => ResetHold();
        OkBtn.MouseLeave += (_, _) => { if (_holdingWithMouse) ResetHold(); };
        OkBtn.PreviewKeyDown += (_, e) => { if (e.Key == Key.Space && !e.IsRepeat) BeginHold(false); };
        OkBtn.PreviewKeyUp += (_, e) => { if (e.Key == Key.Space) ResetHold(); };
        OkBtn.LostKeyboardFocus += (_, _) => ResetHold();
        ConfirmCodeBox.TextChanged += (_, _) => ResetHold();
        Unloaded += (_, _) => ResetHold();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmCodeBox.Text.Trim() != _requiredCode)
        {
            ErrorText.Text = $"确认码不正确，请原样输入 {_requiredCode}。";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        if (NeedsHold && !_holdAccepted)
        {
            ErrorText.Text = "确认码已通过。请按住按钮 1.2 秒，或聚焦按钮后按住空格键。";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        _holdTimer.Stop();
        if (OkBtn.IsMouseCaptured) OkBtn.ReleaseMouseCapture();
        Confirmed = true;
        CloseWith(true);
    }

    private void BeginHold(bool mouse)
    {
        if (!NeedsHold || ConfirmCodeBox.Text.Trim() != _requiredCode) return;
        _holdAccepted = false; _holdingWithMouse = mouse; _heldFor.Restart(); _holdTimer.Start();
    }

    private void ResetHold()
    {
        _holdTimer.Stop(); _heldFor.Reset(); _holdAccepted = false;
        if (NeedsHold) OkBtn.Content = "按住确认";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        CloseWith(false);
    }
}
