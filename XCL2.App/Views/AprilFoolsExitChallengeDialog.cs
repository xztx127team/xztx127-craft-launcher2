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
    private readonly PasswordBox _codeInput = new() { MaxLength = 64, Padding = new Thickness(8), MinHeight = 34 };
    private CancellationTokenSource _stageStop = new();
    private bool _hintShown, _closed;
    private int _generation;

    public AprilFoolsExitChallengeDialog(MainWindow owner, string? easterEggCode = null, bool focusCodeEntry = false)
    {
        _owner = owner;
        _state.TryUnlockShortcut(easterEggCode);
        Width = Math.Min(600, Math.Max(320, owner.ActualWidth - 100));
        MaxHeight = Math.Max(260, owner.ActualHeight - 90);
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(Text("关闭愚人节彩蛋", 20, true));
        panel.Children.Add(Text(_state.ShortcutUnlocked
            ? "彩蛋码验证成功，本次可直接关闭，无需文字游戏或闯关。"
            : "完成7道趣味确认、12个文字／逻辑小游戏和本地人机验证后关闭总开关。也可在下方查看本次启动专用密码并验证关闭。小白旗或F1只结束今天的整蛊。", 12));
        AddCodeEntry(panel);
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
        Loaded += (_, _) =>
        {
            if (!_closed && !_state.ShortcutUnlocked) _clock.Start();
            if (focusCodeEntry && !_closed) _codeInput.Focus();
        };
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
        const string instruction = "小白旗或F1可以直接结束今天的整蛊。若想直接关闭总开关，点击本窗口右侧“查看密码”，再输入本次启动密码进行验证。";
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
    private void AddCodeEntry(Panel panel)
    {
        panel.Children.Add(Text("彩蛋码 / 本次启动密码", 13, true));
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 0), LastChildFill = true };
        var answer = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(8),
            Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        System.Windows.Automation.AutomationProperties.SetName(_codeInput, "彩蛋码或本次启动密码");
        System.Windows.Automation.AutomationProperties.SetName(answer, "本次启动专用密码");
        var view = Button("查看密码", () =>
        {
            answer.Text = "本次启动密码：" + AprilFoolsExitChallengeState.SessionPassword;
            answer.Visibility = Visibility.Visible;
            answer.SelectAll();
            _status.Text = "这是本次启动专用密码。输入后点“验证并关闭”，下次启动会重新生成。";
        });
        view.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(view, Dock.Right); row.Children.Add(view); row.Children.Add(_codeInput);
        void Verify()
        {
            if (!_state.TryUnlockShortcut(_codeInput.Password))
            {
                Fail("彩蛋码不正确。可重新输入，或点右侧“查看密码”获取本次启动专用密码。");
                _codeInput.SelectAll(); _codeInput.Focus(); return;
            }
            _codeInput.Clear(); ConfirmClose();
        }
        _codeInput.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Verify(); } };
        panel.Children.Add(row); panel.Children.Add(answer);
        panel.Children.Add(Button("验证并关闭", Verify));
    }
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
        _body.Children.Add(Text($"小游戏 {game + 1} / {AprilFoolsExitChallengeState.MiniGameCount}", 16, true));
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
            case 8: WordOrder(); break;
            case 9: TextTransform(); break;
            case 10: RuleSequence(); break;
            case 11: CharacterCheck(); break;
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
        var next = 1; var hint = Text("请按1至9的顺序点击；点错会重置本关顺序。下一项：1"); _body.Children.Add(hint);
        var row = new WrapPanel();
        foreach (var n in Enumerable.Range(1, 9).OrderBy(_ => _random.Next()))
            row.Children.Add(Button(n.ToString(), () => { if (n != next) { next = 1; hint.Text = "顺序已重置。下一项：1"; Fail(); return; } next++; if (next == 10) Pass(); else hint.Text = "下一项：" + next; }));
        _body.Children.Add(row);
    }
    private void Memory()
    {
        var values = Enumerable.Range(1, 6).OrderBy(_ => _random.Next()).Take(5).ToArray(); var index = 0; var reveal = 0;
        var label = Text("记住该顺序：" + string.Join(" → ", values)); _body.Children.Add(label);
        var row = new WrapPanel { IsEnabled = false };
        foreach (var n in Enumerable.Range(1, 6)) row.Children.Add(Button(n.ToString(), () =>
        {
            if (index >= values.Length) return;
            if (values[index] != n) { index = 0; Fail("顺序不对，从本关第一项重新试试。需要时可点“再看一次”。"); return; }
            index++; if (index == values.Length) Pass();
        }));
        _body.Children.Add(row);
        async Task Reveal()
        {
            var generation = _generation; var currentReveal = ++reveal; var ct = _stageStop.Token;
            row.IsEnabled = false; index = 0; label.Text = "记住该顺序：" + string.Join(" → ", values);
            try { await Task.Delay(2600, ct); if (!_closed && generation == _generation && reveal == currentReveal) { label.Text = "现在按顺序点击这5项。"; row.IsEnabled = true; } }
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
        var count = _random.Next(6, 13);
        _body.Children.Add(Text("数一数有多少个三角形："));
        var shapes = Enumerable.Repeat("▲", count).Concat(Enumerable.Repeat("●", 5)).Concat(Enumerable.Repeat("◆", 3)).OrderBy(_ => _random.Next());
        _body.Children.Add(Text(string.Join("  ", shapes), 24));
        var row = new WrapPanel();
        foreach (var n in Enumerable.Range(5, 9)) row.Children.Add(Button(n.ToString(), () => { if (n == count) Pass(); else Fail(); }));
        _body.Children.Add(row);
    }
    private void Maze()
    {
        var position = 0; var walls = new HashSet<int> { 6, 7, 8, 11, 13, 16, 18, 21, 22 };
        _body.Children.Add(Text("走到右下角。每次只能上下左右移动一格，不能穿墙；可沿边缘寻找路线。"));
        var board = new System.Windows.Controls.Primitives.UniformGrid { Columns = 5, Rows = 5, MaxWidth = 440 };
        var cells = new List<Button>();
        void Update() { for (var i = 0; i < cells.Count; i++) cells[i].Content = i == position ? "胖虎" : walls.Contains(i) ? "墙" : i == 24 ? "终点" : "·"; }
        for (var i = 0; i < 25; i++)
        {
            var cell = i;
            var button = Button("·", () =>
            {
                if (cell == position) return;
                if (walls.Contains(cell) || Math.Abs(cell / 5 - position / 5) + Math.Abs(cell % 5 - position % 5) != 1) { Fail(); return; }
                position = cell; if (cell == 24) Pass(); else Update();
            });
            button.MinWidth = 42; button.Padding = new Thickness(7, 6, 7, 6); cells.Add(button); board.Children.Add(button);
        }
        Update(); _body.Children.Add(board);
    }
    private void Alignment()
    {
        var goal = _random.Next(3, 8) * 10;
        _body.Children.Add(Text($"把滑块移到{goal}附近（误差不超过2），也可以用方向键。"));
        var value = Text("当前：0"); var slider = new Slider { Minimum = 0, Maximum = 100, Value = 0, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(0, 12, 0, 12) };
        slider.ValueChanged += (_, _) => value.Text = $"当前：{slider.Value:0}";
        _body.Children.Add(slider); _body.Children.Add(value);
        _body.Children.Add(Button("检查位置", () => { if (Math.Abs(slider.Value - goal) <= 2) Pass(); else Fail(); }));
    }
    private void Difference()
    {
        var answer = _random.Next(16); _body.Children.Add(Text("十六个字中有一个不同，请找出“未”（其余是“末”）。"));
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4, MaxWidth = 360 };
        for (var i = 0; i < 16; i++) { var index = i; var cell = Button(i == answer ? "未" : "末", () => { if (index == answer) Pass(); else Fail(); }); cell.FontSize = 22; grid.Children.Add(cell); }
        _body.Children.Add(grid);
    }
    private void Pairs()
    {
        var values = new[] { "星", "月", "云", "雨", "星", "月", "云", "雨" }.OrderBy(_ => _random.Next()).ToArray();
        var matched = new HashSet<int>(); int? first = null; var busy = false;
        var buttons = new List<Button>(); var row = new WrapPanel(); _body.Children.Add(Text("翻开两张相同的卡片，配好四组即可。"));
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
                { matched.Add(previous); matched.Add(index); if (matched.Count == values.Length) Pass(); return; }
                busy = true; Fail("这两张不同，记住位置再试试。已配好的卡片会保留。");
                var ct = _stageStop.Token; var generation = _generation;
                try { await Task.Delay(700, ct); if (!_closed && generation == _generation) { buttons[previous].Content = "？"; card.Content = "？"; busy = false; } }
                catch (OperationCanceledException) { }
            };
            buttons.Add(card); row.Children.Add(card);
        }
        _body.Children.Add(row);
    }
    private void WordOrder()
    {
        var phrases = new[] { "画龙点睛", "亡羊补牢", "守株待兔", "井底之蛙", "叶公好龙", "掩耳盗铃" };
        var answer = phrases[_random.Next(phrases.Length)];
        var chosen = new List<char>();
        var label = Text("已组成：");
        var row = new WrapPanel(); var buttons = new List<Button>();
        _body.Children.Add(Text("把四个打乱的字组成一个常见成语，按正确顺序逐字点击后确认。"));
        void Reset()
        {
            chosen.Clear(); label.Text = "已组成：";
            foreach (var button in buttons) button.IsEnabled = true;
        }
        foreach (var letter in answer.OrderBy(_ => _random.Next()))
        {
            var value = letter;
            var button = Button(value.ToString(), () => { });
            button.FontSize = 22;
            button.Click += (_, _) =>
            {
                chosen.Add(value); button.IsEnabled = false;
                label.Text = "已组成：" + string.Join(" → ", chosen);
            };
            buttons.Add(button); row.Children.Add(button);
        }
        _body.Children.Add(row); _body.Children.Add(label);
        _body.Children.Add(Button("重新排列", Reset));
        _body.Children.Add(Button("确认成语", () =>
        {
            if (new string(chosen.ToArray()) == answer) Pass();
            else { Reset(); Fail("还没有组成正确的成语。本关已清空，可以重新排列。提示：所有字都会用到。 "); }
        }));
    }

    private void TextTransform()
    {
        const string letters = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        var characters = Enumerable.Range(0, 4).Select(_ => letters[_random.Next(letters.Length)])
            .Concat(Enumerable.Range(0, 3).Select(_ => (char)('2' + _random.Next(8)))).OrderBy(_ => _random.Next()).ToArray();
        var token = new string(characters);
        var answer = AprilFoolsExitChallengeState.ReverseLettersWithoutDigits(token);
        _body.Children.Add(Text("文字加工：先去掉所有数字，再把剩余字母按倒序输入。忽略大小写和空格。"));
        _body.Children.Add(Text("原串：" + token, 24, true));
        var input = new TextBox { MaxLength = 32, Padding = new Thickness(8), MinWidth = 160,
            MaxWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
        System.Windows.Automation.AutomationProperties.SetName(input, "加工后的字母");
        void Check()
        {
            if (AprilFoolsExitChallengeState.MatchesPuzzleAnswer(input.Text, answer)) Pass();
            else { Fail("顺序或内容不对。先删除数字，再从最右边的字母开始读。 "); input.SelectAll(); input.Focus(); }
        }
        input.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Check(); } };
        _body.Children.Add(input); _body.Children.Add(Button("检查文字", Check));
    }

    private void RuleSequence()
    {
        var values = Enumerable.Range(1, 36).OrderBy(_ => _random.Next()).Take(9).ToArray();
        var expected = values.Where(n => n % 2 != 0).OrderBy(n => n).Concat(values.Where(n => n % 2 == 0).OrderBy(n => n)).ToArray();
        var next = 0; var buttons = new Dictionary<int, Button>();
        var label = Text("已完成：0 / 9");
        _body.Children.Add(Text("按规则点完九个数：先点所有奇数，再点所有偶数；两组内部都从小到大。点错只重置本关。"));
        var row = new WrapPanel();
        foreach (var n in values)
        {
            var value = n;
            var button = Button(value.ToString(), () =>
            {
                if (next >= expected.Length) return;
                if (expected[next] != value)
                {
                    next = 0; foreach (var item in buttons.Values) item.IsEnabled = true;
                    label.Text = "顺序已重置：0 / 9"; Fail("先奇数后偶数，每一组按从小到大的顺序。 "); return;
                }
                buttons[value].IsEnabled = false; next++;
                if (next == expected.Length) Pass(); else label.Text = $"已完成：{next} / 9";
            });
            buttons.Add(value, button); row.Children.Add(button);
        }
        _body.Children.Add(row); _body.Children.Add(label);
    }

    private void CharacterCheck()
    {
        var letters = new[] { "己", "已", "巳", "未", "末", "土", "士", "日", "目", "田", "由", "甲" }
            .OrderBy(_ => _random.Next()).ToArray();
        var targets = Enumerable.Range(0, letters.Length).OrderBy(_ => _random.Next()).Take(3).ToHashSet();
        var selected = new HashSet<int>(); var label = Text("已选：0 / 3");
        _body.Children.Add(Text("本地人机小游戏：只勾选下列三个字，其他字不要选。请留意相似字形。"));
        _body.Children.Add(Text(string.Join("　", targets.Select(i => letters[i])), 24, true));
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4, MaxWidth = 380 };
        for (var i = 0; i < letters.Length; i++)
        {
            var index = i;
            var check = new CheckBox { Content = letters[i], FontSize = 22, Margin = new Thickness(8), Padding = new Thickness(4) };
            check.Checked += (_, _) => { selected.Add(index); label.Text = $"已选：{selected.Count} / 3"; };
            check.Unchecked += (_, _) => { selected.Remove(index); label.Text = $"已选：{selected.Count} / 3"; };
            grid.Children.Add(check);
        }
        _body.Children.Add(grid); _body.Children.Add(label);
        _body.Children.Add(Button("检查选择", () =>
        {
            if (selected.SetEquals(targets)) Pass();
            else Fail("所选字还不完全一致，请检查字形和数量。必须且只能选择提示中的三个字。 ");
        }));
    }

    private void Human()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var code = new string(Enumerable.Range(0, 6).Select(_ => alphabet[_random.Next(alphabet.Length)]).ToArray());
        var left = _random.Next(3, 10); var right = _random.Next(2, 10); var factor = _random.Next(2, 5);
        _body.Children.Add(Text("本地人机小游戏", 16, true));
        _body.Children.Add(Text("这不是安全认证，不会联网或收集账号／设备信息。请勾选，倒序输入六位字符，再完成算式。字符不区分大小写。"));
        var check = new CheckBox { Content = "我是正在操作启动器的人", Margin = new Thickness(0, 10, 0, 10) };
        _body.Children.Add(check); _body.Children.Add(Text("原始字符：" + code, 22, true));
        _body.Children.Add(Text("从右向左输入这些字符："));
        var box = new TextBox { MaxLength = 24, Width = 180, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8), ToolTip = "按倒序输入原始字符" };
        _body.Children.Add(box); _body.Children.Add(Text($"计算：({left} + {right}) × {factor} = ?"));
        var calculation = new TextBox { MaxLength = 8, Width = 130, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8) };
        System.Windows.Automation.AutomationProperties.SetName(box, "倒序校验字符");
        System.Windows.Automation.AutomationProperties.SetName(calculation, "算式答案");
        _body.Children.Add(calculation); _body.Children.Add(Button("验证并关闭愚人节彩蛋", () =>
        {
            if (check.IsChecked == true && AprilFoolsExitChallengeState.MatchesPuzzleAnswer(box.Text,
                AprilFoolsExitChallengeState.ReverseVerificationCode(code)) &&
                int.TryParse(calculation.Text.Trim(), out var value) && value == (left + right) * factor) Pass();
            else Fail("请完成勾选、字符倒序和算式三项；本关不会清空，F1仍可结束今天的整蛊。 ");
        }));
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
