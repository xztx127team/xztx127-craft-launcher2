using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>设置总开关的关闭挑战；有效彩蛋码仅解锁当前窗口。F1只结束当天整蛊。</summary>
public sealed class AprilFoolsExitChallengeDialog : OverlayDialogControl, IDisposable
{
    private readonly MainWindow _owner;
    private readonly AprilFoolsExitChallengeState _state = new();
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly StackPanel _body = new();
    private readonly TextBlock _progress = new() { Margin = new Thickness(0, 6, 0, 14), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap, MinHeight = 22 };
    private readonly Random _random = new();
    private CancellationTokenSource _stageStop = new();
    private bool _hintShown, _closed;
    private int _generation;

    public AprilFoolsExitChallengeDialog(MainWindow owner, string? easterEggCode = null)
    {
        _owner = owner;
        _state.TryUnlockShortcut(easterEggCode);
        Width = Math.Min(600, Math.Max(320, owner.ActualWidth - 100));
        MaxHeight = Math.Max(260, owner.ActualHeight - 90);
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(Text("关闭愚人节彩蛋", 20, true));
        panel.Children.Add(Text(_state.ShortcutUnlocked
            ? "彩蛋码验证成功，本次可直接关闭，无需文字游戏或闯关。"
            : "完成7道趣味确认、8个小游戏和本地趣味验证后关闭总开关。小白旗或F1可直接结束今天的整蛊。", 12));
        panel.Children.Add(_progress); panel.Children.Add(_body); panel.Children.Add(_status);
        var footer = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        footer.Children.Add(Button("返回设置", () => CloseWith(false)));
        if (_state.ShortcutUnlocked)
            footer.Children.Add(Button("直接关闭愚人节彩蛋", ConfirmClose));
        var restore = Button("仅结束今天（F1）", RestoreImmediately);
        restore.ToolTip = "只结束今天的整蛊，不关闭设置总开关";
        footer.Children.Add(restore); panel.Children.Add(footer);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _clock.Tick += ClockTick;
        Loaded += (_, _) => { if (!_closed && !_state.ShortcutUnlocked) _clock.Start(); };
        RequestClose += (_, _) => DisposeChallenge();
        OverlayClosed += (_, _) => DisposeChallenge();
        owner.Closed += OwnerClosed;
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key == Key.F1) { e.Handled = true; RestoreImmediately(); }
        }), handledEventsToo: true);
        RenderStage();
    }

    private void ClockTick(object? sender, EventArgs e) => OfferExitHint();
    private void OfferExitHint()
    {
        if (_closed || _hintShown || !_state.ShouldOfferConfigExit(_elapsed.Elapsed)) return;
        _hintShown = true;
        const string instruction = "小白旗或F1可以直接结束今天的整蛊。若想跳过本次关闭挑战，可返回设置，点击“输入彩蛋码”进行验证。";
        ToastService.ShowInfo(instruction);
        ReminderCenterService.Publish("april-config-exit", "彩蛋关闭提示", instruction);
        _status.Text = instruction;
    }
    private void Fail(string message = "这次没有过关，可以再试一次，或按F1直接恢复。")
    { if (_closed) return; _state.Fail(); UpdateProgress(); _status.Text = message; OfferExitHint(); }
    private void Pass()
    {
        if (_closed) return;
        _state.Pass();
        if (_state.Completed)
        {
            ConfirmClose(); return;
        }
        RenderStage();
    }
    private void ConfirmClose()
    {
        if (!_closed && _state.CanClose) CloseWith(true);
    }
    private void RestoreImmediately()
    {
        if (_closed) return;
        AprilFoolsService.DismissForToday();
        ToastService.ShowInfo("今天的整蛊已结束，设置总开关保持原样。");
        if (!_closed) CloseWith(false);
    }
    private void DisposeChallenge()
    {
        if (_closed) return; _closed = true; _generation++;
        _clock.Stop(); _clock.Tick -= ClockTick;
        _stageStop.Cancel(); _stageStop.Dispose();
        _owner.Closed -= OwnerClosed;
    }
    private void OwnerClosed(object? sender, EventArgs e) => DisposeChallenge();
    public void Dispose() => DisposeChallenge();
    private void UpdateProgress() => _progress.Text = $"第 {_state.Stage + 1} / {AprilFoolsExitChallengeState.StageCount} 关 · 累计未过关 {_state.Failures} 次";
    private void RenderStage()
    {
        _stageStop.Cancel(); _stageStop.Dispose(); _stageStop = new(); _generation++;
        _body.Children.Clear(); _status.Text = "";
        if (_state.ShortcutUnlocked)
        {
            _progress.Text = "彩蛋码已验证";
            _body.Children.Add(Text("点击“直接关闭愚人节彩蛋”即可确认关闭；返回设置或取消后，本次验证不再保留。"));
            return;
        }
        UpdateProgress();
        if (_state.IsReading) { Reading(); return; }
        if (_state.IsHumanCheck) { Human(); return; }
        var game = _state.Stage - AprilFoolsExitChallengeState.ReadingCount;
        _body.Children.Add(Text($"小游戏 {game + 1} / 8", 16, true));
        switch (game)
        {
            case 0: Sequence(); break;
            case 1: Memory(); break;
            case 2: Reaction(); break;
            case 3: Counting(); break;
            case 4: Maze(); break;
            case 5: Alignment(); break;
            case 6: Difference(); break;
            case 7: Pairs(); break;
        }
    }
    private void Reading()
    {
        var q = AprilFoolsExitChallengeState.Questions[_state.Stage];
        _body.Children.Add(Text($"趣味确认 {_state.Stage + 1} / 7", 16, true));
        _body.Children.Add(Text(q.Passage)); _body.Children.Add(Text(q.Question, 14, true));
        int? selected = null;
        for (var i = 0; i < q.Options.Length; i++)
        {
            var answer = i;
            var radio = new RadioButton { Content = Text(q.Options[i]), GroupName = "ReadingAnswer", Margin = new Thickness(0, 5, 0, 5) };
            radio.Checked += (_, _) => selected = answer; _body.Children.Add(radio);
        }
        _body.Children.Add(Button("确认并继续", () =>
        {
            if (!selected.HasValue) { Fail("先选择一个答案，再确认。F1可以直接恢复。"); return; }
            if (!_state.AnswerReading(selected.Value)) { UpdateProgress(); _status.Text = "答案不对，再读一下题目吧。"; OfferExitHint(); return; }
            RenderStage();
        }));
    }
    private void Sequence()
    {
        var next = 1; var hint = Text("请按1、2、3、4、5的顺序点击。下一项：1"); _body.Children.Add(hint);
        var row = new WrapPanel();
        foreach (var n in Enumerable.Range(1, 5).OrderBy(_ => _random.Next()))
            row.Children.Add(Button(n.ToString(), () => { if (n != next) { Fail(); return; } next++; if (next == 6) Pass(); else hint.Text = "下一项：" + next; }));
        _body.Children.Add(row);
    }
    private void Memory()
    {
        var values = Enumerable.Range(1, 4).OrderBy(_ => _random.Next()).Take(3).ToArray(); var index = 0; var reveal = 0;
        var label = Text("记住这个顺序：" + string.Join(" → ", values)); _body.Children.Add(label);
        var row = new WrapPanel { IsEnabled = false };
        foreach (var n in Enumerable.Range(1, 4)) row.Children.Add(Button(n.ToString(), () =>
        {
            if (values[index] != n) { index = 0; Fail("顺序不对，从本关第一项重新试试。需要时可点“再看一次”。"); return; }
            index++; if (index == values.Length) Pass();
        }));
        _body.Children.Add(row);
        async Task Reveal()
        {
            var generation = _generation; var currentReveal = ++reveal; var ct = _stageStop.Token;
            row.IsEnabled = false; index = 0; label.Text = "记住这个顺序：" + string.Join(" → ", values);
            try { await Task.Delay(2200, ct); if (!_closed && generation == _generation && reveal == currentReveal) { label.Text = "现在按顺序点击。"; row.IsEnabled = true; } }
            catch (OperationCanceledException) { }
        }
        _body.Children.Add(Button("再看一次", () => _ = Reveal())); _ = Reveal();
    }
    private void Reaction()
    {
        var ready = false; var started = false;
        _body.Children.Add(Text("点“开始等待”，等按钮变成“现在点击”后再点它。没有苛刻的反应时间限制。"));
        var target = Button("尚未开始", () => { });
        target.Click += (_, _) => { if (!started) return; if (ready) Pass(); else Fail("还没变成“现在点击”，再等等。无需重新开始全部挑战。"); };
        _body.Children.Add(target);
        var start = Button("开始等待", () => { });
        start.Click += async (_, _) =>
        {
            if (started) return; started = true; start.IsEnabled = false; target.Content = "等待…";
            var ct = _stageStop.Token; var generation = _generation;
            try { await Task.Delay(_random.Next(1000, 2600), ct); if (!_closed && generation == _generation) { ready = true; target.Content = "现在点击"; } }
            catch (OperationCanceledException) { }
        };
        _body.Children.Add(start);
    }
    private void Counting()
    {
        var count = _random.Next(3, 7);
        _body.Children.Add(Text("数一数有多少个三角形："));
        var shapes = Enumerable.Repeat("▲", count).Concat(Enumerable.Repeat("●", 3)).OrderBy(_ => _random.Next());
        _body.Children.Add(Text(string.Join("  ", shapes), 24));
        var row = new WrapPanel();
        foreach (var n in Enumerable.Range(2, 6)) row.Children.Add(Button(n.ToString(), () => { if (n == count) Pass(); else Fail(); }));
        _body.Children.Add(row);
    }
    private void Maze()
    {
        var position = 0;
        _body.Children.Add(Text("走到右下角。每次只能上下左右移动一格，中央是墙。"));
        var board = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Rows = 3, MaxWidth = 300 };
        var cells = new List<Button>();
        void Update() { for (var i = 0; i < cells.Count; i++) cells[i].Content = i == position ? "胖虎" : i == 4 ? "墙" : i == 8 ? "终点" : "·"; }
        for (var i = 0; i < 9; i++)
        {
            var cell = i;
            var button = Button("·", () =>
            {
                if (cell == position) return;
                if (cell == 4 || Math.Abs(cell / 3 - position / 3) + Math.Abs(cell % 3 - position % 3) != 1) { Fail(); return; }
                position = cell; if (cell == 8) Pass(); else Update();
            });
            button.MinWidth = 68; cells.Add(button); board.Children.Add(button);
        }
        Update(); _body.Children.Add(board);
    }
    private void Alignment()
    {
        var goal = _random.Next(3, 8) * 10;
        _body.Children.Add(Text($"把滑块移到{goal}附近（误差不超过4），也可以用方向键。"));
        var value = Text("当前：0"); var slider = new Slider { Minimum = 0, Maximum = 100, Value = 0, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(0, 12, 0, 12) };
        slider.ValueChanged += (_, _) => value.Text = $"当前：{slider.Value:0}";
        _body.Children.Add(slider); _body.Children.Add(value);
        _body.Children.Add(Button("检查位置", () => { if (Math.Abs(slider.Value - goal) <= 4) Pass(); else Fail(); }));
    }
    private void Difference()
    {
        var answer = _random.Next(9); _body.Children.Add(Text("九个字中有一个不同，找出“猫”。"));
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, MaxWidth = 300 };
        for (var i = 0; i < 9; i++) { var index = i; grid.Children.Add(Button(i == answer ? "猫" : "虎", () => { if (index == answer) Pass(); else Fail(); })); }
        _body.Children.Add(grid);
    }
    private void Pairs()
    {
        var values = new[] { "星", "月", "星", "月" }.OrderBy(_ => _random.Next()).ToArray();
        var matched = new HashSet<int>(); int? first = null; var busy = false;
        var buttons = new List<Button>(); var row = new WrapPanel(); _body.Children.Add(Text("翻开两张相同的卡片，配好两组即可。"));
        for (var i = 0; i < values.Length; i++)
        {
            var index = i; var card = Button("？", () => { });
            card.Click += async (_, _) =>
            {
                if (busy || matched.Contains(index) || first == index) return;
                card.Content = values[index];
                if (!first.HasValue) { first = index; return; }
                var previous = first.Value; first = null;
                if (values[previous] == values[index])
                { matched.Add(previous); matched.Add(index); if (matched.Count == 4) Pass(); return; }
                busy = true; Fail("这两张不同，记住位置再试试。已配好的卡片会保留。");
                var ct = _stageStop.Token; var generation = _generation;
                try { await Task.Delay(700, ct); if (!_closed && generation == _generation) { buttons[previous].Content = "？"; card.Content = "？"; busy = false; } }
                catch (OperationCanceledException) { }
            };
            buttons.Add(card); row.Children.Add(card);
        }
        _body.Children.Add(row);
    }
    private void Human()
    {
        var code = _random.Next(100, 1000).ToString();
        _body.Children.Add(Text("本地人机小游戏", 16, true));
        _body.Children.Add(Text("这不是安全认证，不会联网，也不会收集账号或设备信息。勾选后输入下面的三位数字即可。"));
        var check = new CheckBox { Content = "我是正在操作启动器的人", Margin = new Thickness(0, 10, 0, 10) };
        _body.Children.Add(check); _body.Children.Add(Text("验证码：" + code, 22, true));
        var box = new TextBox { MaxLength = 3, Width = 130, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8), ToolTip = "输入显示的三位数字" };
        _body.Children.Add(box); _body.Children.Add(Button("验证并关闭愚人节彩蛋", () => { if (check.IsChecked == true && box.Text.Trim() == code) Pass(); else Fail("请勾选并输入显示的三位数字。F1依然可以结束今天的整蛊。"); }));
    }
    private static TextBlock Text(string value, double size = 13, bool bold = false)
    {
        var text = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 5), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); return text;
    }
    private static Button Button(string label, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 6, 8, 0), MinHeight = 32, MinWidth = 60 };
        button.SetResourceReference(StyleProperty, "SecondaryButton"); button.Click += (_, _) => action(); return button;
    }
}
