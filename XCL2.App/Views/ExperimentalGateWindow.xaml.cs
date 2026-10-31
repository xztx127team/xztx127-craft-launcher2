using System.Windows;
using System.Windows.Threading;

namespace XCL2.App.Views;

public partial class ExperimentalGateWindow : OverlayDialogControl
{
    private const int CountdownSeconds = 10;
    private int _remaining = CountdownSeconds;
    private readonly DispatcherTimer _timer;

    /// <summary>用户是否点击了"我已阅读，进入实验性功能"（倒计时结束后才可能为 true）。
    /// 调用方据此决定是否把 AppConfig.ExperimentalFeaturesUnlocked 置 true 并打开实验性功能面板。</summary>
    public bool Confirmed { get; private set; }

    public ExperimentalGateWindow()
    {
        InitializeComponent();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;
        _timer.Start();
        OverlayClosed += OnOverlayClosed;

        UpdateCountdownText();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        _remaining--;

        if (_remaining <= 0)
        {
            _timer.Stop();
            CountdownText.Text = "现在可以继续了。";
            ConfirmBtn.IsEnabled = true;
            ConfirmBtn.Content = "我已阅读，进入实验性功能";
            return;
        }

        UpdateCountdownText();
    }

    private void UpdateCountdownText()
    {
        CountdownText.Text = $"请等待 {_remaining} 秒...（请趁这段时间看完上面的说明）";
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        // 双重保险：即便有极端情况下按钮的 IsEnabled 状态被绕过（理论上不应该发生），
        // 这里再校验一次剩余秒数，不满足条件就直接忽略这次点击，不设 Confirmed=true。
        if (_remaining > 0) return;

        Confirmed = true;
        _timer.Stop();
        CloseWith(null);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        _timer.Stop();
        CloseWith(null);
    }

    // 同 MicrosoftLoginWindow：OverlayDialogControl 继承 UserControl，没有 Window.OnClosed
    // 可以重写。改用 OverlayDialogControl.OverlayClosed，逻辑跟原来完全一致——弹窗关闭时把倒计时
    // 计时器停掉，避免它在弹窗已经摘除之后还继续触发 Tick。
    private void OnOverlayClosed(object? sender, bool? result)
    {
        _timer.Stop();
    }
}
