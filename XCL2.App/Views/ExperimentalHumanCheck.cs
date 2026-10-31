using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace XCL2.App.Views;

/// <summary>查看实验性功能密码前的本地人机验证：四道小关卡，全部通过才回调 onPassed。</summary>
internal sealed class ExperimentalHumanCheck
{
    private const int LevelCount = 4;
    private readonly Random _random = new();
    private readonly StackPanel _host;
    private readonly TextBlock _status;
    private readonly Action _onPassed;
    private DispatcherTimer? _timer;
    private int _level;
    private bool _done;

    public ExperimentalHumanCheck(StackPanel host, TextBlock status, Action onPassed)
    { _host = host; _status = status; _onPassed = onPassed; }

    public void Start() { _level = 0; Render(); }
    public void Cancel() { _done = true; StopTimer(); }

    private void StopTimer() { if (_timer != null) { _timer.Stop(); _timer = null; } }

    private void Render()
    {
        StopTimer(); _host.Children.Clear();
        _host.Children.Add(new TextBlock { Text = $"人机验证 · 第 {_level + 1} / {LevelCount} 关", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) });
        switch (_level)
        {
            case 0: OrderLevel(); break;
            case 1: MathLevel(); break;
            case 2: SliderLevel(); break;
            default: ReactionLevel(); break;
        }
    }

    private void Pass()
    {
        if (_done) return;
        if (++_level >= LevelCount) { _done = true; StopTimer(); _onPassed(); return; }
        _status.Text = "通过！进入下一关。"; Render();
    }

    private void Fail(string message) { if (_done) return; _status.Text = message; Render(); }

    private static Button Btn(string text, Action click, double minWidth = 0)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(4), MinWidth = minWidth };
        b.Click += (_, _) => click(); return b;
    }

    // 第 1 关：按 1→5 的顺序点击打乱的按钮
    private void OrderLevel()
    {
        _host.Children.Add(new TextBlock { Text = "请按 1 → 5 的顺序依次点击数字。", Margin = new Thickness(0, 0, 0, 6) });
        var next = 1; var panel = new WrapPanel();
        foreach (var n in Enumerable.Range(1, 5).OrderBy(_ => _random.Next()))
        {
            var number = n; Button? button = null;
            button = Btn(number.ToString(), () =>
            {
                if (number != next) { Fail("顺序错了，重新来过。"); return; }
                button!.IsEnabled = false; next++;
                if (next > 5) Pass();
            }, 44);
            panel.Children.Add(button);
        }
        _host.Children.Add(panel);
    }

    // 第 2 关：简单算术
    private void MathLevel()
    {
        int a = _random.Next(3, 10), b = _random.Next(2, 9), c = _random.Next(2, 9), answer = a + b * c;
        _host.Children.Add(new TextBlock { Text = $"{a} + {b} × {c} = ?", FontSize = 20, Margin = new Thickness(0, 0, 0, 6) });
        var input = new TextBox { MaxLength = 4, Padding = new Thickness(8), Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        void Submit() { if (int.TryParse(input.Text.Trim(), out var v) && v == answer) Pass(); else Fail("答案不对，换一道题。"); }
        input.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Submit(); } };
        _host.Children.Add(input); _host.Children.Add(Btn("提交", Submit));
        input.Loaded += (_, _) => input.Focus();
    }

    // 第 3 关：把滑块对准目标值
    private void SliderLevel()
    {
        int target = _random.Next(15, 86);
        _host.Children.Add(new TextBlock { Text = $"把滑块拖到 {target}（允许误差 ±3），然后点确认。", Margin = new Thickness(0, 0, 0, 6) });
        var value = new TextBlock { Text = "当前：0", Margin = new Thickness(0, 4, 0, 0) };
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = 0, SmallChange = 1, LargeChange = 5, MinWidth = 260 };
        slider.ValueChanged += (_, _) => value.Text = $"当前：{(int)Math.Round(slider.Value)}";
        _host.Children.Add(slider); _host.Children.Add(value);
        _host.Children.Add(Btn("确认", () =>
        {
            if (Math.Abs(Math.Round(slider.Value) - target) <= 3) Pass(); else Fail("没对准，重新来一次。");
        }));
    }

    // 第 4 关：等按钮变绿后再点（过早或过晚都算失败）
    private void ReactionLevel()
    {
        _host.Children.Add(new TextBlock { Text = "按钮变绿后立刻点击；变绿之前点击或超过 2 秒都会失败。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        var green = false; var armed = DateTime.MinValue;
        var button = new Button { Content = "等待……", Padding = new Thickness(12, 14, 12, 14), Margin = new Thickness(4), MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) =>
        {
            if (!green) { Fail("太早了，等它变绿。"); return; }
            if ((DateTime.UtcNow - armed).TotalSeconds > 2) { Fail("太慢了，重来。"); return; }
            Pass();
        };
        _host.Children.Add(button);
        var wait = TimeSpan.FromMilliseconds(_random.Next(1200, 3200));
        _timer = new DispatcherTimer { Interval = wait };
        _timer.Tick += (_, _) =>
        {
            if (green) { StopTimer(); Fail("太慢了，重来。"); return; }
            green = true; armed = DateTime.UtcNow; button.Content = "现在点！";
            button.Background = Brushes.MediumSeaGreen; button.Foreground = Brushes.White;
            _timer!.Interval = TimeSpan.FromSeconds(2.2);
        };
        _timer.Start();
    }
}
