using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>模块扫描结果的展示包装（给 ListView 用）。</summary>
public class ModuleRow
{
    public string RiskLabel { get; init; } = "";
    public string FileName { get; init; } = "";
    public string? MatchedRule { get; init; }
    public string? CompanyName { get; init; }
    public string FullPath { get; init; } = "";
}

public partial class LogsPage : UserControl
{
    private readonly MainWindow _owner;
    private readonly CrashAnalyzerService _crashAnalyzer = new();
    private readonly InjectionScanService _injectionScan = new();
    private List<(string path, DateTime modifiedAt)> _crashFiles = new();

    // 游戏日志 Tab：记录当前订阅了 OutputReceived 事件的进程，切换选择时需要先取消订阅旧的。
    private GameProcessInfo? _subscribedProcess;
    private Action<string>? _subscribedOutputHandler;
    private int _gameLogSubscriptionGeneration;
    private long _fullLauncherLogRevision = -1;
    private readonly object _pendingGameLogLock = new();
    private readonly StringBuilder _pendingGameLog = new();
    private GameProcessInfo? _pendingGameLogSource;
    private int _pendingGameLogGeneration;
    private bool _pendingGameLogDrain;
    private int _processReloadQueued;

    // 需求：游戏崩溃退出后，这个 Tab 不应该瞬间"空屏"——用户往往就是想在崩溃那一刻
    // 立刻看最后几行输出来定位问题，日志一闪而空对排错完全没有帮助。
    // 用户手动点"清空本视图内的日志"按钮之前，都不应该清空。
    //
    // 之前的实现完全没有这个考虑：ReloadProcessCombos 每次进程列表变化（包括进程退出后
    // 从"运行中列表"移除）都会重新绑定 ProcessCombo.ItemsSource，一旦当前选中的进程不在
    // 新列表里了，SelectionChanged 触发 LoadGameLog()，走到 "else" 分支直接把
    // GameLogBox.Text 整个替换成"当前没有正在运行的游戏进程"提示语——玩家刚看到的崩溃输出
    // 瞬间被这句提示覆盖掉，等于自己伸手把最有用的信息删了。
    //
    // 修复方式：额外记一份"这个进程退出前最后订阅到的完整日志文本"快照 (_lastKnownLogSnapshot)，
    // 在 LoadGameLog() 因为找不到匹配进程要显示占位提示时，如果这份快照非空，
    // 优先继续展示快照内容（并追加一行明显的分隔提示，说明"进程已结束，以下为退出前的日志"），
    // 而不是清空。只有用户主动点了"清空本视图内的日志"按钮，或者选中了一个新的、
    // 确实在运行的进程时，才会真正替换/清空这份快照。
    private string? _lastKnownLogSnapshot;
    private string? _lastKnownLogVersionId;

    // 启动器日志 Tab：文件不会主动推送变化事件，改用轻量轮询定时器实现"自动刷新"。
    private readonly DispatcherTimer _launcherLogTimer;
    private DateTime _launcherLogLastWrite = DateTime.MinValue;

    // 会话实时视图每秒仅检查修订号；无新增内容时不复制日志字符串，也不重新投影。
    private readonly DispatcherTimer _fullLauncherLogTimer;

    // 独立启动器日志窗口：见 LauncherLogWindowService 类头注释。只在用户点了"独立窗口"按钮
    // 之后才会创建；可以重复点击复用同一个实例（Open() 内部每次都会弹一个新窗口+新同步定时器，
    // 这里只是持有引用方便页面卸载时统一 Dispose 掉同步定时器）。
    private LauncherLogWindowService? _launcherLogWindowService;

    // 最近一次注入扫描的可序列化快照。ListView 只是展示层，AI 分析不从控件反向拼数据，
    // 避免排序/虚拟化后丢字段，也方便把 PID、风险统计等一起带上。
    private string? _lastInjectionScanSnapshot;

    public LogsPage(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();
        InitializeLogReading();

        // 默认不显示日志面板，符合"小白 0 基础也能用"的定位：日志只是可选的高手工具。
        ShowLogCheck.IsChecked = _owner.ConfigService.Config.ShowLogPanel;
        ApplyVisibility();

        ReloadProcessCombos();
        RefreshLauncherLog_Click(this, new RoutedEventArgs());
        RescanCrash_Click(this, new RoutedEventArgs());
        LoadFullLauncherLog();

        _owner.ProcessManager.Changed += OnProcessesChanged;

        // 启动器日志文件每 2 秒检查一次是否有更新，有变化才重新读取，避免频繁磁盘 IO。
        _launcherLogTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _launcherLogTimer.Tick += (_, _) => AutoRefreshLauncherLog();
        _launcherLogTimer.Start();

        // 完整启动器日志每秒自动刷新一次。纯内存读取，没有磁盘 IO，
        // 但如果用户觉得费性能/费内存，可以用 Tab 里的"刷新"按钮手动模式代替
        // （后续计划在设置页加一个开关，允许关掉这个自动定时器、隐藏刷新按钮改成纯手动）。
        _fullLauncherLogTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _fullLauncherLogTimer.Tick += (_, _) => AutoRefreshFullLauncherLog();
        _fullLauncherLogTimer.Start();

        Loaded += (_, _) =>
        {
            _owner.ProcessManager.Changed -= OnProcessesChanged;
            _owner.ProcessManager.Changed += OnProcessesChanged;
            _launcherLogTimer.Start(); _fullLauncherLogTimer.Start();
            LoadGameLog();
        };
        Unloaded += (_, _) =>
        {
            _owner.ProcessManager.Changed -= OnProcessesChanged;
            _launcherLogTimer.Stop();
            _fullLauncherLogTimer.Stop();
            ++_gameLogSubscriptionGeneration;
            if (_subscribedProcess != null && _subscribedOutputHandler != null) _subscribedProcess.OutputReceived -= _subscribedOutputHandler;
            // 只停掉我们进程内的同步定时器；已经弹出的独立日志窗口本身不受影响，继续独立存在
            // （见 LauncherLogWindowService.Dispose 的注释：这正是"不依附于咱们的进程"的应有之义）。
            _launcherLogWindowService?.Dispose();
        };
    }

    private void OnProcessesChanged()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished || Interlocked.Exchange(ref _processReloadQueued, 1) != 0) return;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            Interlocked.Exchange(ref _processReloadQueued, 0);
            if (IsLoaded) ReloadProcessCombos();
        }));
    }

    private void OpenButtonMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        { menu.PlacementTarget = button; menu.IsOpen = true; }
    }

    private void TaskResults_Click(object sender, RoutedEventArgs e)
        => TaskResultHistoryDialog.Show(_owner.ConfigService.Config, () => _owner.ConfigService.Save());

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Capture UI-owned buffers before background file IO. No config/account files are read.
            string game;
            if (ProcessCombo.SelectedItem is GameProcessInfo process)
            {
                lock (process.OutputBuffer)
                {
                    var count = Math.Min(process.OutputBuffer.Length, DiagnosticRedactionService.MaxEntryChars);
                    game = process.OutputBuffer.ToString(process.OutputBuffer.Length - count, count);
                    if (process.OutputBuffer.Length > count) game = DiagnosticRedactionService.TrimPartialFirstLine(game);
                }
            }
            else
            {
                var snapshot = _lastKnownLogSnapshot ?? "";
                game = snapshot.Length > DiagnosticRedactionService.MaxEntryChars ? DiagnosticRedactionService.TrimPartialFirstLine(snapshot[^DiagnosticRedactionService.MaxEntryChars..]) : snapshot;
            }
            var crash = RawLogView(CrashRawBox);
            var launcher = LauncherLogService.GetBufferedTail();
            var entries = await Task.Run(() =>
            {
                var sources = new List<DiagnosticExportSource>
                {
                    new("启动器会话日志（末尾）", launcher), new("当前游戏输出（末尾）", game),
                    new("已选崩溃报告", crash)
                };
                var path = Path.Combine(App.DataDir, "logs", "crash.log");
                if (File.Exists(path))
                {
                    try { sources.Add(new("启动器异常日志（末尾）", DiagnosticRedactionService.ReadTextTail(path))); }
                    catch { sources.Add(new("启动器异常日志", "该日志暂时无法读取。")); }
                }
                return sources;
            });
            DiagnosticsExportDialog.Show(entries);
        }
        catch (Exception ex) { ToastService.ShowWarning("准备诊断失败：" + DiagnosticRedactionService.Sanitize(ex.Message, 1000)); }
    }

    private void ApplyVisibility()
    {
        var show = ShowLogCheck.IsChecked == true;
        LogTabs.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        HiddenHint.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowLogCheck_Changed(object sender, RoutedEventArgs e)
    {
        _owner.ConfigService.Config.ShowLogPanel = ShowLogCheck.IsChecked == true;
        _owner.ConfigService.Save();
        ApplyVisibility();
    }

    private void ReloadProcessCombos()
    {
        var running = _owner.ProcessManager.Running;
        ProcessCombo.ItemsSource = running;
        ProcessCombo.DisplayMemberPath = nameof(GameProcessInfo.VersionId);
        if (ProcessCombo.SelectedItem == null && running.Count > 0) ProcessCombo.SelectedItem = running[0];

        ScanProcessCombo.ItemsSource = running;
        ScanProcessCombo.DisplayMemberPath = nameof(GameProcessInfo.VersionId);
        if (ScanProcessCombo.SelectedItem == null && running.Count > 0) ScanProcessCombo.SelectedItem = running[0];
    }

    // --- 游戏日志 Tab ---
    // 自动刷新原理：GameProcessInfo 每收到一行新的控制台输出就会触发 OutputReceived 事件，
    // 这里订阅这个事件、直接把新行追加到文本框末尾，不需要用户手动点"刷新"重新加载全部内容。
    private void ProcessCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadGameLog();

    // "刷新"按钮保留：用于手动重新从头加载一次完整缓冲区（比如怀疑漏看了内容时）。
    private void RefreshGameLog_Click(object sender, RoutedEventArgs e) => LoadGameLog();

    private void LoadGameLog()
    {
        var generation = ++_gameLogSubscriptionGeneration;
        if (_subscribedProcess != null)
        {
            if (_subscribedOutputHandler != null) _subscribedProcess.OutputReceived -= _subscribedOutputHandler;
            _lastKnownLogSnapshot = _subscribedProcess.GetOutputSnapshot();
            _lastKnownLogVersionId = _subscribedProcess.VersionId;
            _subscribedProcess = null;
            _subscribedOutputHandler = null;
        }

        if (ProcessCombo.SelectedItem is GameProcessInfo info)
        {
            var snapshot = info.GetOutputSnapshot();
            SetLogViewSource(GameLogBox, snapshot.Length > 0 ? snapshot : Loc.T("Str_Cs_No_Output_Yet", "(暂无输出)"), initial: true);

            _subscribedProcess = info;
            _subscribedOutputHandler = line => OnGameLogLineReceived(info, generation, line);
            info.OutputReceived += _subscribedOutputHandler;

            // 记下这份快照：万一这个进程接下来崩溃/退出、从"运行中列表"里消失，
            // 下次 LoadGameLog 找不到匹配进程时还能回退展示这份内容，而不是清空。
            _lastKnownLogSnapshot = snapshot;
            _lastKnownLogVersionId = info.VersionId;
        }
        else if (!string.IsNullOrEmpty(_lastKnownLogSnapshot))
        {
            // 找不到匹配的运行中进程（通常是游戏刚崩溃/退出，进程从列表里被移除了），
            // 但我们手上还留着它退出前的日志快照——继续展示这份内容，不清空，
            // 并且在末尾加一行明显的提示，说明这是"进程已结束时的最后日志"而不是实时输出，
            // 避免用户误以为游戏还在运行。
            SetLogViewSource(GameLogBox, _lastKnownLogSnapshot.TrimEnd('\r', '\n') + Environment.NewLine +
                Environment.NewLine +
                $"==== [{_lastKnownLogVersionId}] 进程已退出，以上为退出前的最后日志（点击上方\"清空本视图内的日志\"可清空此视图） ====", initial: true);
        }
        else
        {
            SetLogViewSource(GameLogBox, Loc.T("Str_Cs_No_Game_Process_Is_Running_Once_You_Laun", "当前没有正在运行的游戏进程。启动游戏后这里会实时显示游戏的控制台输出。"));
        }
    }

    private void OnGameLogLineReceived(GameProcessInfo source, int generation, string line)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        lock (_pendingGameLogLock)
        {
            if (generation != Volatile.Read(ref _gameLogSubscriptionGeneration)) return;
            if (!ReferenceEquals(source, _pendingGameLogSource) || generation != _pendingGameLogGeneration)
            { _pendingGameLog.Clear(); _pendingGameLogSource = source; _pendingGameLogGeneration = generation; }
            _pendingGameLog.AppendLine(line);
            if (_pendingGameLog.Length > 500_000)
            {
                _pendingGameLog.Remove(0, _pendingGameLog.Length - 400_000);
                _pendingGameLog.Insert(0, "[实时输出较多，排队视图已省略较早内容]" + Environment.NewLine);
                if (_pendingGameLog.Capacity > 500_000) _pendingGameLog.Capacity = 500_000;
            }
            if (_pendingGameLogDrain) return;
            _pendingGameLogDrain = true;
        }
        // Only one queued UI operation exists even while a noisy game produces thousands of lines.
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(DrainGameLogLines));
    }

    private void DrainGameLogLines()
    {
        string text; GameProcessInfo? source; int generation;
        lock (_pendingGameLogLock)
        {
            text = _pendingGameLog.ToString(); source = _pendingGameLogSource; generation = _pendingGameLogGeneration;
            _pendingGameLog.Clear(); _pendingGameLogDrain = false;
        }
        if (!LogViewTextService.AcceptLiveOutput(source, _subscribedProcess, ProcessCombo.SelectedItem,
            generation, _gameLogSubscriptionGeneration, IsLoaded)) return;
        AppendLogViewText(GameLogBox, text);
    }

    /// <summary>"清空本视图内的日志"按钮：只清空这个 Tab 当前显示的文本和内部快照，
    /// 不影响 GameProcessInfo.OutputBuffer（进程管理等其它地方还依赖它），也不删磁盘上的
    /// latest.log/crash.log。清空后如果选中的进程仍在运行，会继续正常实时追加新日志——
    /// 只是清掉了"清空这一刻之前"的历史内容。</summary>
    private void ClearGameLogView_Click(object sender, RoutedEventArgs e)
    {
        lock (_pendingGameLogLock) _pendingGameLog.Clear();
        SetLogViewSource(GameLogBox, Loc.T("Str_Cs_No_Output_Yet", "(暂无输出)"));
        _lastKnownLogSnapshot = null;
        _lastKnownLogVersionId = null;
    }

    // --- 日志提交给 AI ---
    private void AnalyzeCurrentGameLogWithAi_Click(object sender, RoutedEventArgs e)
    {
        var (raw, source) = GetCurrentGameLogRawAndSource();

        if (string.IsNullOrWhiteSpace(raw))
        {
            MessageBoxDialog.ShowInfo("当前没有可提交的游戏日志。请先启动游戏，或保留一次已退出游戏的日志快照。", "AI 日志分析");
            return;
        }

        SubmitLogToAi(
            source,
            raw,
            "请分析下面的 Minecraft 游戏日志。先给出最可能的根因，再列出关键证据（错误类型、类名、Mod/Loader 名、退出码等），最后给出按优先级排序且可直接执行的修复步骤。不要把普通 INFO/WARN 当成崩溃根因；信息不足时明确说明还缺什么。" );
    }

    /// <summary>提取自 AnalyzeCurrentGameLogWithAi_Click：拿"当前游戏日志"这份原始文本
    /// （选中运行中进程就取它的 OutputBuffer，否则退回最近一次的日志快照），供 AI 分析和
    /// "复制"按钮共用同一份取值逻辑，避免两处各写一遍、以后改一处忘了改另一处。</summary>
    private (string? raw, string source) GetCurrentGameLogRawAndSource()
    {
        string? raw = null;
        string source = "当前游戏日志";

        if (ProcessCombo.SelectedItem is GameProcessInfo info)
        {
            lock (info.OutputBuffer) raw = info.OutputBuffer.ToString();
            source = $"当前游戏日志 - {info.VersionId}";
        }
        else if (!string.IsNullOrWhiteSpace(_lastKnownLogSnapshot))
        {
            raw = _lastKnownLogSnapshot;
            source = string.IsNullOrWhiteSpace(_lastKnownLogVersionId)
                ? "最近一次游戏日志"
                : $"最近一次游戏日志 - {_lastKnownLogVersionId}";
        }

        return (raw, source);
    }

    /// <summary>"复制"：把 AI 分析同一份原始游戏日志文本整个复制到剪贴板，不是复制
    /// GameLogBox 里可能因为性能考虑被截断显示的那部分。</summary>
    private void CopyCurrentGameLog_Click(object sender, RoutedEventArgs e)
    {
        if (CopySelectedLog(GameLogBox, sender as FrameworkElement)) return;
        var (raw, _) = GetCurrentGameLogRawAndSource();
        CopyLogTextToClipboard(raw, sender as FrameworkElement);
    }

    private void AnalyzeLauncherLogWithAi_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadLauncherCrashLog(out var raw, out var error))
        {
            MessageBoxDialog.ShowError(error!, "AI 日志分析");
            return;
        }
        if (raw == null)
        {
            MessageBoxDialog.ShowInfo("当前没有启动器异常日志可提交。", "AI 日志分析");
            return;
        }

        SubmitLogToAi(
            "启动器异常日志",
            raw,
            "请分析下面的 XCL2 启动器异常日志。区分启动器自身代码错误、Java、网络/下载、权限、文件占用、配置或 Minecraft 环境问题；给出最可能根因、关键证据和最短修复路径。" );
    }

    /// <summary>提取自 AnalyzeLauncherLogWithAi_Click：读取磁盘上的 crash.log 全文，
    /// 供 AI 分析和"复制"按钮共用。返回 false 表示读取过程中出了异常（error 带原因）；
    /// 返回 true 但 raw 为 null 表示文件本来就不存在（不算错误，是正常的"还没有异常日志"）。</summary>
    private bool TryReadLauncherCrashLog(out string? raw, out string? error)
    {
        raw = null;
        error = null;

        var crashLog = Path.Combine(App.DataDir, "logs", "crash.log");
        if (!File.Exists(crashLog)) return true;

        try
        {
            using var stream = new FileStream(crashLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            raw = reader.ReadToEnd();
            return true;
        }
        catch (Exception ex)
        {
            error = "读取启动器日志失败：" + ex.Message;
            return false;
        }
    }

    private void CopyLauncherLog_Click(object sender, RoutedEventArgs e)
    {
        if (CopySelectedLog(LauncherLogBox, sender as FrameworkElement)) return;
        if (!TryReadLauncherCrashLog(out var raw, out var error))
        {
            MessageBoxDialog.ShowError(error!, "复制日志");
            return;
        }
        CopyLogTextToClipboard(raw, sender as FrameworkElement);
    }

    private void AnalyzeCrashLogWithAi_Click(object sender, RoutedEventArgs e)
    {
        if (!_owner.AiAssistantConfig.AllowCrashLogReading)
        {
            MessageBoxDialog.ShowWarning("请先在 AI 助手设置中开启“允许把日志/崩溃内容发给 AI 分析”，再提交崩溃报告。", "AI 日志分析");
            return;
        }
        if (string.IsNullOrWhiteSpace(RawLogView(CrashRawBox)))
        {
            MessageBoxDialog.ShowInfo("请先选择一个崩溃报告。", "AI 日志分析");
            return;
        }

        SubmitLogToAi(
            "Minecraft 崩溃报告",
            RawLogView(CrashRawBox),
            "请深入分析下面的 Minecraft 崩溃报告。优先定位第一责任方（具体 Mod、前置依赖、Mixin、Loader、Java 版本、显卡驱动或资源问题），区分根因与后续连锁异常，并给出可执行的修复顺序。",
            isCrashLogContext: true);
    }

    /// <summary>"复制"：跟"AI 分析崩溃报告"不同，复制不涉及把内容发给第三方 AI 接口，
    /// 不需要检查 AllowCrashLogReading 这个"允许发给 AI"的开关——用户自己复制给自己用，
    /// 不受这个限制。</summary>
    private void CopyCrashLog_Click(object sender, RoutedEventArgs e)
    {
        if (!CopySelectedLog(CrashRawBox, sender as FrameworkElement)) CopyLogTextToClipboard(RawLogView(CrashRawBox), sender as FrameworkElement);
    }

    private void AnalyzeInjectionLogWithAi_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastInjectionScanSnapshot))
        {
            MessageBoxDialog.ShowInfo("请先选择游戏进程并执行一次“扫描”。", "AI 注入分析");
            return;
        }

        SubmitLogToAi(
            "游戏进程注入扫描",
            _lastInjectionScanSnapshot,
            "请分析下面的 Minecraft/Java 进程模块注入扫描结果。重点判断 Suspicious/Unknown 项更像系统组件、显卡驱动、JVM/LWJGL、录屏/Overlay/输入法等正常模块，还是需要人工排查的注入模块。不要仅凭 Unknown 就判定恶意；列出最值得核实的文件、理由和安全的核实步骤。" );
    }

    /// <summary>"复制"：这个 Tab 没有日志 TextBox，复制的是跟"AI 分析注入日志"同一份
    /// _lastInjectionScanSnapshot（BuildInjectionScanSnapshot 生成的文本化扫描结果），
    /// 还没执行过扫描时跟 AI 分析一样提示"请先扫描"，不复制空内容。</summary>
    private void CopyInjectionLog_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastInjectionScanSnapshot))
        {
            MessageBoxDialog.ShowInfo("请先选择游戏进程并执行一次“扫描”。", "复制日志");
            return;
        }
        CopyLogTextToClipboard(_lastInjectionScanSnapshot, sender as FrameworkElement);
    }

    private async void AnalyzeFullLauncherLogWithAi_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 汇总磁盘日志可能涉及多个文件，放在线程池做，避免用户点 AI 分析时界面卡住。
            var raw = await Task.Run(BuildAllLauncherLogsText);
            SubmitLogToAi(
                "完整启动器日志",
                raw,
                "请综合分析下面的 XCL2 当前会话日志与磁盘日志。先按时间线找出真正导致失败/异常的第一处错误，再区分连锁报错与噪音，最后给出按优先级排序的修复方案。" );
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError("汇总日志失败：" + ex.Message, "AI 日志分析");
        }
    }

    /// <summary>"复制"：跟 AI 分析一样调用同一个 BuildAllLauncherLogsText 汇总当前会话+磁盘
    /// 日志，同样放线程池跑避免卡界面；复制的内容跟提交给 AI 的完全一致（未经过 PrepareLogForAi
    /// 的截断处理——复制给用户自己看应该给全文，截断只是为了不把过长内容灌爆 AI 的上下文）。</summary>
    private async void CopyFullLauncherLog_Click(object sender, RoutedEventArgs e)
    {
        if (CopySelectedLog(FullLauncherLogBox, sender as FrameworkElement)) return;
        try
        {
            var raw = await Task.Run(BuildAllLauncherLogsText);
            CopyLogTextToClipboard(raw, sender as FrameworkElement);
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError("汇总日志失败：" + ex.Message, "复制日志");
        }
    }

    /// <summary>五个"复制"按钮共用的落地逻辑：写入剪贴板 + Toast 提示。内容为空时提示
    /// "没有可复制的日志内容"而不是静默复制一个空字符串（空字符串一旦复制会覆盖用户剪贴板
    /// 里原来的内容，属于有害的"静默副作用"，必须先挡住）。剪贴板偶发被其它程序占用会抛
    /// 异常，跟项目里其它 Clipboard.SetText 调用处理方式一致：捕获后用 Toast 提示失败，
    /// 不弹阻断式对话框打断用户。</summary>
    private static void CopyLogTextToClipboard(string? text, FrameworkElement? feedbackSource = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            ToastService.ShowWarning("没有可复制的日志内容。");
            return;
        }

        try
        {
            Clipboard.SetText(DiagnosticRedactionService.Sanitize(text));
            if (ConfigService.Active?.Config.Ux50InputLocalCopyFeedback == true && feedbackSource?.IsVisible == true)
                InputExperienceService.ShowCopied(feedbackSource);
            else ToastService.ShowSuccess("已复制脱敏后的日志。");
        }
        catch
        {
            ToastService.ShowWarning("复制失败，剪贴板可能正被其它程序占用。");
        }
    }

    private void SubmitLogToAi(string sourceName, string rawText, string guidance, bool isCrashLogContext = true)
    {
        if (isCrashLogContext && !_owner.AiAssistantConfig.AllowCrashLogReading)
        {
            MessageBoxDialog.ShowWarning("请先在 AI 助手设置中开启“允许把日志/崩溃内容发给 AI 分析”。", "AI 日志分析");
            return;
        }

        if (string.IsNullOrWhiteSpace(rawText))
        {
            MessageBoxDialog.ShowInfo("没有可提交的日志内容。", "AI 日志分析");
            return;
        }

        var prepared = PrepareLogForAi(rawText, out var truncated);
        var prompt = new StringBuilder()
            .AppendLine(guidance)
            .AppendLine()
            .AppendLine($"日志来源：{sourceName}")
            .AppendLine("说明：XCL2 已在本地尝试隐藏常见 access token / refresh token / API Key / Bearer 凭据。")
            .AppendLine(truncated ? "说明：原始日志过长，本次保留了开头环境信息和末尾错误区段，中间部分已截断。" : "说明：以下为本次日志内容。")
            .AppendLine("```text")
            .AppendLine(prepared)
            .AppendLine("```")
            .ToString();

        _owner.NavigateToAiAssistantWithPrompt(prompt, sourceName, isCrashLogContext);
    }

    /// <summary>日志发往 AI 前做两件事：隐藏最常见的认证凭据；限制单次文本体积。
    /// 错误通常集中在日志末尾，因此超长时保留少量开头（环境/版本）+ 大部分末尾（异常堆栈）。</summary>
    private static string PrepareLogForAi(string rawText, out bool truncated)
    {
        var text = DiagnosticRedactionService.Sanitize(rawText);

        const int maxChars = 32000;
        const int headChars = 6000;
        truncated = text.Length > maxChars;
        if (!truncated) return text.Trim();

        var tailChars = maxChars - headChars;
        return text[..headChars].TrimEnd() +
               Environment.NewLine + Environment.NewLine +
               $"===== 中间 {text.Length - maxChars:N0} 个字符已截断 =====" +
               Environment.NewLine + Environment.NewLine +
               text[^tailChars..].TrimStart();
    }

    // --- 启动器日志 Tab ---
    // "刷新"按钮保留，供用户手动强制重新读取一次（比如怀疑轮询没生效时）。
    private void RefreshLauncherLog_Click(object sender, RoutedEventArgs e) => LoadLauncherLog(force: true);

    // --- 完整启动器日志 Tab ---
    // 显示的是"当前会话"的内存日志（LauncherLogService.Buffer），不是磁盘上的历史 .log/crash.log 文件，
    // 实时内存有上限，裁剪时显示提示；完整前缀由日志服务落入会话文件。
    private void RefreshFullLauncherLog_Click(object sender, RoutedEventArgs e) => LoadFullLauncherLog();

    /// <summary>需求："加入一个按钮，可以独立出一个启动器日志，不依附于咱们的进程"。
    /// 见 LauncherLogWindowService 类头注释：弹出一个真正独立的 cmd 进程持续 tail 日志文件，
    /// 不是同进程内的第二个 WPF 窗口。每次点击都弹一个新窗口——用户可能想同时开好几个
    /// （比如一个专门盯着看，一个截图存档），不做"只能开一个"的限制，跟大多数支持多开
    /// 日志窗口的工具（比如各类 tail 工具）的习惯一致。</summary>
    private void OpenLauncherLogInIndependentWindow_Click(object sender, RoutedEventArgs e)
    {
        // 复用同一个字段持有"最近一次"打开的实例，页面 Unloaded 时统一 Dispose 掉同步定时器；
        // 旧实例（如果还在）先 Dispose 一次，避免两个同步定时器同时写同一批新增内容造成浪费
        // ——不影响旧窗口本身的显示，旧窗口对应的文件已经写好的内容还在，只是不再收到新的
        // 增量更新（用户如果还想继续看最新日志，重新点一次这个按钮弹新窗口即可）。
        _launcherLogWindowService?.Dispose();
        _launcherLogWindowService = new LauncherLogWindowService();
        _launcherLogWindowService.Open();
    }

    /// <summary>由 _fullLauncherLogTimer 每秒调用一次。</summary>
    private void AutoRefreshFullLauncherLog() => LoadFullLauncherLog();

    private void LoadFullLauncherLog()
    {
        if (!LauncherLogService.TryGetBufferedText(ref _fullLauncherLogRevision, out var text)) return;
        var newText = string.IsNullOrWhiteSpace(text)
            ? Loc.T("Str_Cs_No_Session_Log_Yet", "(当前会话暂无内存日志)")
            : text;

        SetLogViewSource(FullLauncherLogBox, newText);
    }

    /// <summary>由定时器每 2 秒调用一次：只有文件的最后修改时间变化了才重新读取，避免无意义的磁盘 IO。</summary>
    private void AutoRefreshLauncherLog() => LoadLauncherLog(force: false);

    private void LoadLauncherLog(bool force)
    {
        try
        {
            var crashLog = Path.Combine(App.DataDir, "logs", "crash.log");
            if (!File.Exists(crashLog))
            {
                if (force) SetLogViewSource(LauncherLogBox, Loc.T("Str_Cs_No_Launcher_Log_Yet_Which_Means_The_Laun", "(暂无启动器日志，说明启动器运行正常，没有记录到异常。)"));
                return;
            }

            var lastWrite = File.GetLastWriteTimeUtc(crashLog);
            if (!force && lastWrite == _launcherLogLastWrite) return; // 文件没变化，跳过这次刷新

            _launcherLogLastWrite = lastWrite;
            SetLogViewSource(LauncherLogBox, File.ReadAllText(crashLog));
        }
        catch (Exception ex)
        {
            if (force) SetLogViewSource(LauncherLogBox, Loc.T("Str_Cs_Couldn_T_Read_The_Launcher_Log", "读取启动器日志失败: ") + ex.Message);
        }
    }

    private static string BuildAllLauncherLogsText()
    {
        var sb = new StringBuilder();
        var logDir = Path.Combine(App.DataDir, "logs");

        sb.AppendLine("===== 当前会话内存日志 =====");
        var buffered = LauncherLogService.GetBufferedText();
        sb.AppendLine(string.IsNullOrWhiteSpace(buffered) ? "(当前会话暂无内存日志)" : buffered.TrimEnd());
        sb.AppendLine();

        if (!Directory.Exists(logDir))
        {
            sb.AppendLine("===== 磁盘日志 =====");
            sb.AppendLine("(日志目录不存在)");
            return sb.ToString();
        }

        var files = Directory.EnumerateFiles(logDir, "*", SearchOption.TopDirectoryOnly)
            .Where(path =>
                string.Equals(Path.GetExtension(path), ".log", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(path), "crash.log", StringComparison.OrdinalIgnoreCase))
            .Select(path => new FileInfo(path))
            .OrderBy(f => string.Equals(f.Name, "crash.log", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        if (files.Count == 0)
        {
            sb.AppendLine("===== 磁盘日志 =====");
            sb.AppendLine("(没有找到 .log 或 crash.log 文件)");
            return sb.ToString();
        }

        foreach (var file in files)
        {
            sb.AppendLine($"===== {file.Name}  修改时间: {file.LastWriteTime:yyyy-MM-dd HH:mm:ss}  大小: {PresentationPreferencesService.FormatNumberWithUnit(file.Length, "字节", "N0")} =====");
            try
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                sb.AppendLine(reader.ReadToEnd().TrimEnd());
            }
            catch (Exception ex)
            {
                sb.AppendLine($"(读取失败: {ex.Message})");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // --- 崩溃报告分析 Tab ---
    private void RescanCrash_Click(object sender, RoutedEventArgs e)
    {
        _crashFiles.Clear();
        foreach (var folder in _owner.ConfigService.Config.Folders)
        {
            // .minecraft 根目录（版本隔离关闭时崩溃报告在这里）
            try { _crashFiles.AddRange(_crashAnalyzer.ListCrashFiles(folder.Path)); }
            catch { /* 忽略单个目录扫描失败 */ }

            // ===== 关键补充：逐个版本目录也要扫 =====
            // 版本隔离是**默认开启**的，开启时游戏的工作目录是 versions/<版本名>/，
            // crash-reports 和 logs 都写在那里面，而不是 .minecraft 根目录。
            // 之前只扫根目录，等于对绝大多数用户的崩溃记录完全失明——
            // 表现就是"游戏明明崩了，崩溃分析却说没有发现崩溃报告"。
            try
            {
                var versionsDir = Path.Combine(folder.Path, "versions");
                if (Directory.Exists(versionsDir))
                {
                    foreach (var versionDir in Directory.GetDirectories(versionsDir))
                    {
                        try { _crashFiles.AddRange(_crashAnalyzer.ListCrashFiles(versionDir)); }
                        catch { /* 单个版本目录扫描失败不影响其它 */ }
                    }
                }
            }
            catch { /* 忽略 */ }
        }

        // 同一个文件可能被根目录和版本目录两条路径扫到（隔离关闭时），按全路径去重。
        _crashFiles = _crashFiles
            .GroupBy(f => Path.GetFullPath(f.path), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(f => f.modifiedAt)
            .ToList();

        // 现在会扫到多个版本目录下的同名文件（每个实例都有自己的 latest.log），
        // 只显示文件名的话完全分不清是哪个版本的，所以把上一级目录名一起带上。
        CrashFileCombo.ItemsSource = _crashFiles.Select(f =>
        {
            var owner = TryDescribeOwningInstance(f.path);
            return owner == null
                ? $"{Path.GetFileName(f.path)}  ({f.modifiedAt:yyyy-MM-dd HH:mm})"
                : $"[{owner}] {Path.GetFileName(f.path)}  ({f.modifiedAt:yyyy-MM-dd HH:mm})";
        }).ToList();
        if (_crashFiles.Count == 0)
        {
            CrashFindingsText.Text = Loc.T("Str_Cs_No_Crash_Reports_Found_So_No_Game_Crashe", "没有发现崩溃报告文件，说明目前没有检测到游戏崩溃记录。");
            SetLogViewSource(CrashRawBox, "");
        }
        else
        {
            CrashFileCombo.SelectedIndex = 0;
        }
    }

    private void CrashFileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var idx = CrashFileCombo.SelectedIndex;
        if (idx < 0 || idx >= _crashFiles.Count) return;

        var result = _crashAnalyzer.Analyze(_crashFiles[idx].path);

        // 带上置信度标记：让用户一眼分清"日志里明说的原因"和"启发式猜测"。
        // 不加区分的话，一条泛泛的"可能是内存不足"跟一条确定的"缺少前置 mod X"
        // 看起来分量一样，用户会去试错误的方向。
        CrashFindingsText.Text = result.RankedFindings.Count > 0
            ? string.Join("\n\n", result.RankedFindings.Select((f, i) =>
            {
                var tag = f.Confidence switch
                {
                    CrashConfidence.Certain => "【基本确定】",
                    CrashConfidence.Likely => "【很可能】",
                    _ => "【推测】",
                };
                return $"{i + 1}. {tag} {f.Text}";
            }))
            : string.Join("\n\n", result.Findings.Select((f, i) => $"{i + 1}. {f}"));

        SetLogViewSource(CrashRawBox, result.RawText, initial: true);
        CrashFileCombo.ToolTip = PresentationPreferencesService.FormatFullDateTime(new DateTimeOffset(_crashFiles[idx].modifiedAt));
    }

    /// <summary>从崩溃文件路径反推它属于哪个游戏实例（versions/&lt;名字&gt;/... → 名字）。
    /// 扫不出来就返回 null，界面退回只显示文件名。</summary>
    private static string? TryDescribeOwningInstance(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            while (!string.IsNullOrEmpty(dir))
            {
                var parent = Path.GetDirectoryName(dir);
                if (parent != null &&
                    string.Equals(Path.GetFileName(parent), "versions", StringComparison.OrdinalIgnoreCase))
                    return Path.GetFileName(dir);
                dir = parent;
            }
        }
        catch { }
        return null;
    }

    // --- 注入检测 Tab ---
    private void ScanNow_Click(object sender, RoutedEventArgs e)
    {
        if (ScanProcessCombo.SelectedItem is not GameProcessInfo info)
        {
            MessageBoxDialog.ShowInfo(Loc.T("Str_Cs_Please_Select_A_Running_Game_Process_Fir", "请先选择一个正在运行的游戏进程。"), Loc.T("Str_Status_Tip", "提示"));
            return;
        }

        var result = _injectionScan.Scan(info.Process);
        _lastInjectionScanSnapshot = BuildInjectionScanSnapshot(info, result);
        ModuleListView.ItemsSource = result.Modules
            .OrderByDescending(m => m.Risk)
            .Select(m => new ModuleRow
            {
                RiskLabel = m.Risk switch
                {
                    ModuleRisk.Suspicious => "⚠ 可疑",
                    ModuleRisk.Unknown => "? 未知",
                    _ => "✓ 可信"
                },
                FileName = m.FileName,
                MatchedRule = m.MatchedRule,
                CompanyName = m.CompanyName,
                FullPath = m.FullPath
            })
            .ToList();

        if (result.HasSuspiciousModule)
            MessageBoxDialog.ShowWarning(Loc.T("Str_Cs_Suspicious_Modules_Were_Found_Check_The_", "扫描发现可疑模块，请在列表中查看标记为「⚠ 可疑」的条目，建议立即处理。"), Loc.T("Str_Cs_Injection_Scan", "注入检测"));
    }

    private static string BuildInjectionScanSnapshot(GameProcessInfo info, InjectionScanResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"版本: {info.VersionId}");
        sb.AppendLine($"PID: {result.Pid}");
        sb.AppendLine($"模块总数: {result.Modules.Count}");
        sb.AppendLine($"可疑: {result.Modules.Count(m => m.Risk == ModuleRisk.Suspicious)}");
        sb.AppendLine($"未知: {result.UnknownCount}");
        sb.AppendLine();

        foreach (var m in result.Modules.OrderByDescending(m => m.Risk).ThenBy(m => m.FileName, StringComparer.OrdinalIgnoreCase))
        {
            var risk = m.Risk switch
            {
                ModuleRisk.Suspicious => "Suspicious",
                ModuleRisk.Unknown => "Unknown",
                _ => "Trusted"
            };
            sb.Append('[').Append(risk).Append("] ").AppendLine(m.FileName);
            if (!string.IsNullOrWhiteSpace(m.MatchedRule)) sb.Append("  规则: ").AppendLine(m.MatchedRule);
            if (!string.IsNullOrWhiteSpace(m.CompanyName)) sb.Append("  厂商: ").AppendLine(m.CompanyName);
            if (!string.IsNullOrWhiteSpace(m.FileDescription)) sb.Append("  描述: ").AppendLine(m.FileDescription);
            if (!string.IsNullOrWhiteSpace(m.FullPath)) sb.Append("  路径: ").AppendLine(m.FullPath);
        }
        return sb.ToString();
    }
}
