using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using XCL2.App.Models;
using XCL2.App.Views;

namespace XCL2.App.Services;

public enum DownloadQueueItemStatus
{
    Downloading,
    Paused,
    Completed,
    Failed,
    Canceled,
    Waiting,
    Pausing,
    Canceling
}

/// <summary>
/// 标题栏统一任务。暂停会取消当前执行，等待写入和清理真正结束后才允许继续。
/// 暂停／取消先确认当前执行停止，再后台清理本任务已登记的临时缓存。
/// 继续执行复用完整且校验通过的文件，未完成片段重新下载，不删除实例或存档。
/// </summary>
public sealed class DownloadQueueItem : INotifyPropertyChanged
{
    public Guid Id { get; } = Guid.NewGuid();
    public Guid RecoveryId { get; init; } = Guid.NewGuid();
    public DownloadRecoveryRequest? RecoveryRequest { get; init; }
    public string Name { get; }
    public string OriginPage { get; init; } = "启动器任务";
    private string _remark = "";
    public string Remark { get => _remark; set { var text = (value ?? "").Trim(); _remark = text[..Math.Min(text.Length, 200)]; OnChanged(); } }
    public string TransferStageText => TransferObservation.ActivitySummary(
        DownloadQueueService.Instance.Config?.Ux50DownloadStageDetails != false,
        DownloadQueueService.Instance.Config?.Ux50DownloadRetryCountdown != false);
    public string Category => RecoveryRequest != null || TargetVersionId != null ? "游戏实例"
        : Name.Contains("Java", StringComparison.OrdinalIgnoreCase) || Name.Contains("DirectX", StringComparison.OrdinalIgnoreCase) || Name.Contains("运行库") || Name.Contains("VC++") ? "运行环境"
        : Name.Contains("模组") || Name.Contains("资源") || Name.Contains("整合包") ? "模组与资源" : "其他";
    public DownloadTransferObservation TransferObservation { get; } = new();
    private string _taskExplanation = "";
    public string TaskExplanation { get => _taskExplanation; internal set { _taskExplanation = value; OnChanged(); } }
    private readonly System.Diagnostics.Stopwatch _executionWatch = new();
    private string _stateReason = "";
    /// <summary>在任务卡常驻显示的停止、失败、占用和重试原因，不依赖通知气泡。</summary>
    public string StateReason { get => _stateReason; internal set { if (_stateReason == value) return; _stateReason = value; OnChanged(); } }
    public bool IsStopping => Status is DownloadQueueItemStatus.Pausing or DownloadQueueItemStatus.Canceling;
    private bool _isCleaningUp;
    public bool IsCleaningUp
    {
        get => _isCleaningUp;
        internal set { _isCleaningUp = value; OnChanged(); NotifyControls(); }
    }
    public string PauseResumeLabel => IsCleaningUp ? "清理中…" : Status switch
    {
        DownloadQueueItemStatus.Pausing => "正在暂停…",
        DownloadQueueItemStatus.Canceling => "正在取消…",
        DownloadQueueItemStatus.Paused => "继续",
        DownloadQueueItemStatus.Failed => "重试",
        _ => "暂停"
    };
    public string ActivityText => IsCleaningUp ? "执行已停止，正在清理本任务临时缓存"
        : Status == DownloadQueueItemStatus.Waiting ? "等待执行；可右键调整顺序"
        : $"已用 {(int)_executionWatch.Elapsed.TotalMinutes}分{_executionWatch.Elapsed.Seconds:00}秒";
    internal void StartTiming() { _lastSampleTime = DateTime.MinValue; _unitsPerSecond = 0; _lastStage = null; _executionWatch.Start(); RefreshTiming(); }
    internal void StopTiming() { _executionWatch.Stop(); RefreshTiming(); }
    public void RefreshTiming()
    {
        OnChanged(nameof(ActivityText)); OnChanged(nameof(TransferStageText));
        if (IsRunning && Status == DownloadQueueItemStatus.Downloading)
            StateReason = TransferObservation.CurrentReason;
    }
    public bool CanMoveWaiting => !IsRunning && !IsCleaningUp && Status == DownloadQueueItemStatus.Waiting;
    internal bool BypassScheduler;


    private double _progressPercent;
    private bool _isIndeterminate = true;
    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        set { _isIndeterminate = value; OnChanged(); }
    }
    public double ProgressPercent
    {
        get => _progressPercent;
        set { _progressPercent = value; OnChanged(); }
    }

    private string _statusText = "等待中...";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnChanged(); }
    }

    private DownloadQueueItemStatus _status = DownloadQueueItemStatus.Downloading;
    public DownloadQueueItemStatus Status
    {
        get => _status;
        set { _status = value; OnChanged(); OnChanged(nameof(IsStopping)); NotifyControls(); }
    }

    /// <summary>该任务是否允许暂停。安装加载器这类不能安全中断后续传的任务设为 false，界面上不会出现暂停按钮。</summary>
    private bool _supportsPause = true;
    public bool SupportsPause
    {
        get => _supportsPause;
        set { _supportsPause = value; NotifyControls(); }
    }
    public bool SupportsRetry { get; init; } = true;
    private bool _supportsCancel = true;
    public bool SupportsCancel
    {
        get => _supportsCancel;
        set { _supportsCancel = value; NotifyControls(); }
    }
    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        internal set { _isRunning = value; OnChanged(); NotifyControls(); }
    }
    public bool CanPause => SupportsPause && IsRunning && Status == DownloadQueueItemStatus.Downloading;
    public bool CanResume => !IsRunning && !IsCleaningUp && (Status == DownloadQueueItemStatus.Paused ||
        (SupportsRetry && Status == DownloadQueueItemStatus.Failed));
    public bool CanPauseOrResume => CanPause || CanResume;
    public bool CanCancel => !IsCleaningUp && !IsStopping && (!IsRunning || SupportsCancel);
    public string CancelLabel => IsRunning || Status == DownloadQueueItemStatus.Paused ? "取消" : "移除";
    internal bool PauseRequested;
    internal bool CancelRequested;
    internal string StopReason = "";
    internal int ExplanationRevision;
    internal Task CancellationDispatch = Task.CompletedTask;
    internal TaskCompletionSource<DownloadQueueItemStatus> CompletionSource = NewCompletionSource();
    internal static TaskCompletionSource<DownloadQueueItemStatus> NewCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>当前这次执行的真实结束状态，不等待完成条目自动收起。</summary>
    public Task<DownloadQueueItemStatus> Completion => CompletionSource.Task;
    public Exception? LastError { get; internal set; }
    public string? CompletionText { get; set; }
    /// <summary>系统安装进程不能安全强停；仅在这一阶段暂时关闭暂停/取消。</summary>
    internal async Task<T> RunInstallerPhaseAsync<T>(Func<Task<T>> action)
    {
        var previousPause = SupportsPause;
        var previousCancel = SupportsCancel;
        SupportsPause = false;
        SupportsCancel = false;
        try { return await action(); }
        finally
        {
            SupportsPause = previousPause;
            SupportsCancel = previousCancel;
        }
    }
    private void NotifyControls()
    {
        OnChanged(nameof(CanPause)); OnChanged(nameof(CanResume)); OnChanged(nameof(CanPauseOrResume));
        OnChanged(nameof(CanCancel)); OnChanged(nameof(CancelLabel)); OnChanged(nameof(CanMoveWaiting)); OnChanged(nameof(ActivityText)); OnChanged(nameof(PauseResumeLabel));
    }

    /// <summary>驱动本次下载的取消令牌源。暂停/取消都通过 Cancel() 它来打断当前
    /// await 中的下载调用；暂停之后如果要继续，会 new 一个新的 Cts 重新跑。</summary>
    internal CancellationTokenSource Cts;

    /// <summary>重新发起该条目对应的下载调用（拿新的 CancellationToken 以及条目自身，
    /// 后者主要是方便调用方拿 CreateProgress() 把进度接回来），由 DownloadCenterPage、
    /// BedrockPage 等发起下载的地方在注册条目时提供，这里不关心具体是"游戏版本"
    /// "整合包""基岩版客户端"还是别的什么类型的下载。</summary>
    internal readonly Func<DownloadQueueItem, CancellationToken, Task> ResumeAction;

    public string? TargetVersionId { get; init; }

    /// <summary>这条下载任务安装到哪个 .minecraft 文件夹。同一个版本号可以同时存在于多个
    /// 游戏文件夹里，只比版本 ID 会误伤——用户完全可能一边在 A 文件夹装 1.20.1，
    /// 一边启动 B 文件夹里早就装好的 1.20.1。为 null 时退化成只比版本 ID。</summary>
    public string? TargetFolderPath { get; init; }

    private readonly HashSet<string> _installationTargets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>登记这条任务实际会写入的版本 ID。加载器安装的最终版本名要到安装过程中才能确定
    /// （自定义实例名、Forge 解析出的目录名等），所以允许在运行中追加，可多次调用。</summary>
    public void RegisterInstallationTarget(string? versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return;
        DownloadQueueService.Instance.RegisterInstallationTarget(this, versionId.Trim());
    }
    internal void AddInstallationTarget(string versionId)
    {
        lock (_installationTargets) _installationTargets.Add(versionId);
    }

    /// <summary>该任务是否正在写入指定版本（初始目标或运行中登记的目标）。</summary>
    internal bool TargetsVersion(string versionId)
    {
        if (!string.IsNullOrWhiteSpace(TargetVersionId) &&
            string.Equals(TargetVersionId, versionId, StringComparison.OrdinalIgnoreCase)) return true;
        lock (_installationTargets) return _installationTargets.Contains(versionId.Trim());
    }

    public DownloadQueueItem(string name, CancellationTokenSource cts, Func<DownloadQueueItem, CancellationToken, Task> resumeAction)
    {
        Name = name;
        Cts = cts;
        ResumeAction = resumeAction;
        TransferObservation.Changed += () => DownloadQueueService.PostOnUi(RefreshTiming);
    }

    private DateTime _lastSampleTime = DateTime.MinValue;
    private int _lastSampleDone;
    private double _unitsPerSecond;
    private string? _lastStage;
    private string _progressUnit = "项";
    private int _detailDone, _detailTotal;
    private string _currentProgressFile = "";

    private string _detailText = "";
    /// <summary>格式化好的详情行文本（剩余时间 · 当前速度 · 大小），按
    /// AppConfig.DownloadPopupSizeDisplayMode 决定"大小"这一段展示剩余/已下载/两者。
    /// 直接绑定给 MainWindow 下载气泡里新增的详情 TextBlock。</summary>
    public string DetailText
    {
        get => _detailText;
        private set { _detailText = value; OnChanged(); }
    }

    private void UpdateDetail(int done, int total)
    {
        _detailDone = done;
        _detailTotal = total;
        var now = DateTime.UtcNow;
        if (_lastSampleTime != DateTime.MinValue)
        {
            var elapsed = (now - _lastSampleTime).TotalSeconds;
            // 至少间隔 0.4s 才重新采样速度，避免同一批回调打太密时速度抖动到没法看。
            if (elapsed >= 0.4)
            {
                var deltaDone = done - _lastSampleDone;
                var instant = deltaDone > 0 ? deltaDone / elapsed : 0;
                // 简单指数平滑，减少速度数字来回跳。
                _unitsPerSecond = _unitsPerSecond <= 0 ? instant : _unitsPerSecond * 0.7 + instant * 0.3;
                _lastSampleTime = now;
                _lastSampleDone = done;
            }
        }
        else
        {
            _lastSampleTime = now;
            _lastSampleDone = done;
        }

        RenderDetail(done, total);
    }

    /// <summary>单位设置改变后重新格式化，保留原始进度与速度采样，不伪造新采样。</summary>
    public void RefreshDisplayUnits()
    {
        if (_lastStage == null) return;
        RenderDetail(_detailDone, _detailTotal);
        if (IsRunning && Status == DownloadQueueItemStatus.Downloading) RenderProgressStatus();
    }

    private void RenderProgressStatus()
    {
        var counts = _progressUnit == "KiB"
            ? DownloadTransferPolicy.FormatCapacity((long)_detailDone * 1024) + "/" + DownloadTransferPolicy.FormatCapacity((long)_detailTotal * 1024)
            : $"{_detailDone}/{_detailTotal}";
        StatusText = _detailTotal > 0
            ? $"{_lastStage} {counts}" + (string.IsNullOrEmpty(_currentProgressFile) ? "" : $" {_currentProgressFile}")
            : _lastStage ?? "正在执行…";
    }

    private void RenderDetail(int done, int total)
    {
        var remaining = Math.Max(0, total - done);
        var speedText = _unitsPerSecond > 0.05 ? $"{_unitsPerSecond:0.#} {_progressUnit}/秒" : "速度待计算";
        string etaText;
        if (_unitsPerSecond > 0.05 && remaining > 0)
        {
            var etaSeconds = remaining / _unitsPerSecond;
            etaText = etaSeconds < 60 ? $"{etaSeconds:0}秒"
                : etaSeconds < 3600 ? $"{etaSeconds / 60:0}分{etaSeconds % 60:0}秒"
                : $"{etaSeconds / 3600:0}时{etaSeconds % 3600 / 60:0}分";
        }
        else etaText = total > 0 && done >= total ? "已完成" : "计算中...";

        var mode = DownloadQueueService.Instance?.Config?.DownloadPopupSizeDisplayMode ?? 0;
        var sizeText = mode switch
        {
            1 => $"已完成 {done}/{total}",
            2 => $"已完成 {done}/{total}（剩余 {remaining}）",
            _ => $"剩余 {remaining}/{total}",
        };

        var suffix = " " + _progressUnit;
        if (_progressUnit == "KiB")
        {
            var finishedSize = DownloadTransferPolicy.FormatCapacity((long)done * 1024);
            var totalSize = DownloadTransferPolicy.FormatCapacity((long)total * 1024);
            var remainingSize = DownloadTransferPolicy.FormatCapacity((long)remaining * 1024);
            sizeText = mode switch { 1 => $"已完成 {finishedSize}/{totalSize}", 2 => $"已完成 {finishedSize}/{totalSize}（剩余 {remainingSize}）", _ => $"剩余 {remainingSize}/{totalSize}" };
            suffix = "";
            speedText = _unitsPerSecond > 0.05
                ? DownloadTransferPolicy.FormatCapacity(DownloadDisplayFormatting.KiBToBytes(_unitsPerSecond)) + "/秒"
                : "速度待计算";
        }
        DetailText = total > 0 ? $"本阶段预计剩余 {etaText} · {speedText} · {sizeText}{suffix}" : "当前阶段没有可计算的总量";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    /// <summary>把该条目包成一个 IProgress&lt;ProgressInfo&gt;，方便直接传给下载/解压/安装
    /// 这类本来就接受 ProgressInfo 回调的方法（DownloadService、BedrockClientDownloadService、
    /// BedrockContentService 等全项目统一用的该类型），不需要调用方自己再手写一遍
    /// "算百分比 + 切 UI 线程 + 更新 StatusText" 的样板代码。</summary>
    public IProgress<ProgressInfo> CreateProgress()
    {
        var attempt = Cts;
        return new Progress<ProgressInfo>(info =>
        {
            if (!AcceptProgress(attempt)) return;
            if (_lastStage != info.Stage || info.Done < _lastSampleDone)
            {
                _lastStage = info.Stage;
                _lastSampleTime = DateTime.MinValue;
                _unitsPerSecond = 0;
            }
            _progressUnit = info.Unit;
            TransferObservation.ClearIssue();
            RefreshTiming();
            IsIndeterminate = info.Total <= 0;
            var pct = info.Total > 0 ? (double)info.Done / info.Total * 100 : 0;
            ProgressPercent = Math.Clamp(pct, 0, 100);
            _currentProgressFile = info.CurrentFile;
            UpdateDetail(info.Done, info.Total);
            RenderProgressStatus();
        });
    }
    /// <summary>命令行下载器同样按执行轮次过滤排队中的旧输出。</summary>
    public IProgress<string> CreateTextProgress()
    {
        var attempt = Cts;
        return new Progress<string>(text => { if (AcceptProgress(attempt)) StatusText = text; });
    }
    private bool AcceptProgress(CancellationTokenSource attempt) => ReferenceEquals(Cts, attempt)
        && IsRunning && Status == DownloadQueueItemStatus.Downloading && !attempt.IsCancellationRequested;

}

/// <summary>
/// 全局单例：游戏、资源、运行时、启动前补全与驱动任务统一登记。
/// 启动前必需的下载会等待真实结果；无关版本的下载不会阻塞启动。
///
/// 后来补的一条例外（也只有这一条）：**正在下载/安装中的那个版本本身**不能启动。
/// 上面那条"互不阻塞"说的是"下载 A 的时候可以启动 B"，这没问题；但如果用户选中的
/// 恰恰就是此刻正在被写入的那个版本目录，启动出来的必然是缺 libraries/assets 的
/// 半成品，结果是一个用户看不懂的原生 Java 崩溃窗口。与其让它崩，不如明确告诉用户
/// "该版本还在装，装完再启动"。判断入口见 FindActiveForVersion，拦截点在
/// MainWindow.LaunchInternalAsync。跟这条下载无关的其它版本仍然照常可以启动。
/// </summary>
public sealed partial class DownloadQueueService
{
    public static DownloadQueueService Instance { get; } = new();
    private readonly AsyncLocal<DownloadQueueItem?> _currentItem = new();
    /// <summary>同一下载流水线中的运行库补全复用父任务，避免嵌套登记两条下载。</summary>
    internal DownloadQueueItem? CurrentItem => _currentItem.Value is { IsRunning: true } item ? item : null;

    /// <summary>由 MainWindow 启动时赋值一次，供 DownloadQueueItem.UpdateDetail 读取
    /// DownloadPopupSizeDisplayMode 等设置项——该单例服务本身不持有 ConfigService，
    /// 避免额外拉一份 DI/构造依赖，直接让宿主窗口把当前配置对象塞进来即可（引用类型，
    /// 设置页保存后原地修改同一个 AppConfig 实例，这里读到的自然是最新值）。</summary>
    public AppConfig? Config { get; set; }

    public ObservableCollection<DownloadQueueItem> Items { get; } = new();
    /// <summary>只保留本会话最近的完成任务，供用户主动定位文件；不落盘路径或操作记录。</summary>
    public ObservableCollection<DownloadQueueItem> RecentCompleted { get; } = new();

    public bool HasActive => Items.Any(i => i.IsRunning || i.IsCleaningUp || i.Status is DownloadQueueItemStatus.Paused or DownloadQueueItemStatus.Waiting);

    public DownloadQueueItem? FindActiveForVersion(string? folderPath, string? versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return null;

        return Items.FirstOrDefault(i =>
            (i.IsRunning || i.IsCleaningUp || i.Status is DownloadQueueItemStatus.Paused or DownloadQueueItemStatus.Waiting) &&
            i.TargetsVersion(versionId) &&
            (string.IsNullOrWhiteSpace(i.TargetFolderPath) || string.IsNullOrWhiteSpace(folderPath) ||
             string.Equals(NormalizeFolder(i.TargetFolderPath), NormalizeFolder(folderPath), StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>把路径归一成可比较的形式（去掉结尾分隔符、转成绝对路径）。
    /// 下载登记方拿到的路径和 cfg.SelectedFolderPath 可能一个带结尾反斜杠一个不带，
    /// 直接字符串比会漏判，那样拦截就形同虚设。</summary>
    private static string NormalizeFolder(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
    }

    private DownloadQueueService() { }

    internal void RegisterInstallationTarget(DownloadQueueItem item, string versionId) => RunOnUi(() =>
    {
        var existing = FindActiveForVersion(item.TargetFolderPath, versionId);
        if (existing != null && existing != item)
            throw new InvalidOperationException($"「{versionId}」已有下载或补全任务，无法同时写入该版本。");
        item.AddInstallationTarget(versionId);
    });

    public DownloadQueueItem StartNew(string name, Func<DownloadQueueItem, CancellationToken, Task> resumeAction,
        string? targetVersionId = null, string? targetFolderPath = null, bool supportsPause = true,
        bool supportsRetry = true, bool supportsCancel = true, DownloadRecoveryRequest? recoveryRequest = null,
        string? originPage = null, [CallerFilePath] string originFile = "")
    {
        var cts = new CancellationTokenSource();
        var item = new DownloadQueueItem(name, cts, resumeAction)
        {
            TargetVersionId = string.IsNullOrWhiteSpace(targetVersionId) ? null : targetVersionId,
            TargetFolderPath = string.IsNullOrWhiteSpace(targetFolderPath) ? null : targetFolderPath,
            SupportsPause = supportsPause,
            SupportsRetry = supportsRetry,
            SupportsCancel = supportsCancel,
            RecoveryRequest = recoveryRequest,
            OriginPage = originPage ?? CurrentItem?.OriginPage ?? DescribeOrigin(originFile),
            BypassScheduler = CurrentItem != null,
            Status = DownloadQueueItemStatus.Waiting,
            StatusText = "等待执行"
        };
        RunOnUi(() =>
        {
            if (FindActiveForVersion(targetFolderPath, targetVersionId) != null)
                throw new InvalidOperationException($"「{targetVersionId}」已有下载或补全任务，请等待该任务结束。");
            Items.Add(item);
            PersistRecovery();

        });
        RunOnUi(PumpWaiting);
        return item;
    }

    private async Task RunAsync(DownloadQueueItem item)
    {
        using var observation = DownloadTransferPolicy.Observe(item.TransferObservation);
        item.StartTiming();
        var attemptCts = item.Cts;
        var completion = item.CompletionSource;
        var previousItem = _currentItem.Value;
        _currentItem.Value = item;
        var outcome = DownloadQueueItemStatus.Completed;
        Exception? failure = null;
        RunOnUi(() => { item.Status = DownloadQueueItemStatus.Downloading; item.StatusText = "正在执行…"; item.StateReason = ""; });
        try
        {
            await item.ResumeAction(item, attemptCts.Token);
            attemptCts.Token.ThrowIfCancellationRequested();
        }
        catch (DownloadUserCanceledException ex)
        {
            outcome = DownloadQueueItemStatus.Canceled;
            item.StopReason = ex.Message;
        }
        catch (OperationCanceledException) when (attemptCts.IsCancellationRequested || item.PauseRequested || item.CancelRequested)
        {
            outcome = item.PauseRequested ? DownloadQueueItemStatus.Paused : DownloadQueueItemStatus.Canceled;
        }
        catch (OperationCanceledException ex)
        {
            // 只有确认的 HttpClient 超时才说超时；未知 OCE 保留原始停止原因，不能虚构用户取消。
            outcome = DownloadQueueItemStatus.Failed;
            failure = ex is TaskCanceledException && ex.InnerException is TimeoutException
                ? new TimeoutException("连接或传输超时，下载已停止。请检查网络或下载源后重试。 " + ex.Message, ex)
                : new InvalidOperationException("任务意外停止，下载未完成：" + ex.Message, ex);
        }
        catch (Exception ex)
        {
            if (attemptCts.IsCancellationRequested || item.PauseRequested || item.CancelRequested)
                outcome = item.PauseRequested ? DownloadQueueItemStatus.Paused : DownloadQueueItemStatus.Canceled;
            else { outcome = DownloadQueueItemStatus.Failed; failure = ex; }
        }
        finally
        {
            // CancelAsync 的回调也属于本次执行，必须真实收尾后才能允许下一轮写入。
            await item.CancellationDispatch;
            if (item.PauseRequested || item.CancelRequested)
                outcome = item.PauseRequested ? DownloadQueueItemStatus.Paused : DownloadQueueItemStatus.Canceled;
            var stopped = outcome is DownloadQueueItemStatus.Paused or DownloadQueueItemStatus.Canceled;
            RunOnUi(() =>
            {
                item.LastError = failure;
                item.Status = outcome;
                item.StopTiming();
                if (stopped && string.IsNullOrWhiteSpace(item.StopReason))
                    item.StopReason = outcome == DownloadQueueItemStatus.Paused ? "用户请求暂停" : "用户请求取消";
                item.IsCleaningUp = stopped;
                item.IsRunning = false;
                item.IsIndeterminate = false;
                if (outcome == DownloadQueueItemStatus.Completed)
                {
                    item.StatusText = item.CompletionText ?? "已完成";
                    item.StateReason = "";
                    item.ProgressPercent = 100;
                }
                else if (outcome == DownloadQueueItemStatus.Failed)
                {
                    item.StatusText = "下载失败，执行已停止";
                    item.StateReason = FailureReason(failure);
                }
                else
                {
                    item.StatusText = outcome == DownloadQueueItemStatus.Paused ? "已暂停 · 正在清理临时缓存" : "已取消 · 正在清理临时缓存";
                    item.StateReason = item.StopReason +
                        "；下载和写入已经停止，正在后台清理本任务临时缓存。完整且校验通过的文件保留。";
                }
                PersistRecovery();
                PumpWaiting(); // 无关任务不必等待本任务的缓存扫描。
            });
            if (stopped) await CleanupStoppedItemAsync(item);
            else _ = RefreshFileExplanationAsync(item, attemptCts);
            _currentItem.Value = previousItem;
            completion.TrySetResult(outcome);
            attemptCts.Dispose();
            RunOnUi(PumpWaiting);
        }
        if (outcome == DownloadQueueItemStatus.Completed)
        {
            RunOnUi(() =>
            {
                RecentCompleted.Insert(0, item);
                while (RecentCompleted.Count > 20) RecentCompleted.RemoveAt(RecentCompleted.Count - 1);
                ShowCompletionNotification(item);
            });
            await Task.Delay(TimeSpan.FromSeconds(item.CompletionText == null ? 4 : 15));
            RunOnUi(() => Items.Remove(item));
        }
    }

    /// <summary>登记必须完成才能继续的任务。失败/取消向调用方传播，队列仍保留对应状态。
    /// 不提供暂停或重试，避免调用方已结束后后台重试又触发启动或安装。</summary>
    public async Task<T> RunAndWaitAsync<T>(string name,
        Func<DownloadQueueItem, CancellationToken, Task<T>> action, CancellationToken ct = default,
        string? targetVersionId = null, string? targetFolderPath = null, bool supportsCancel = true,
        [CallerFilePath] string originFile = "")
    {
        ct.ThrowIfCancellationRequested();
        T result = default!;
        var item = StartNew(name, async (entry, token) => { result = await action(entry, token); },
            targetVersionId, targetFolderPath, supportsPause: false, supportsRetry: false, supportsCancel: supportsCancel, originPage: DescribeOrigin(originFile));
        // 安装程序不可安全中断时仅取消调用方后续步骤；等安装进程真实退出后才释放任务。
        using var registration = supportsCancel ? ct.Register(() =>
        {
            // 取消回调不能同步等待 UI；否则 UI Dispose(registration) 与 Dispatcher.Invoke 会互等。
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.BeginInvoke(new Action(() => CancelFromCaller(item)));
            else CancelFromCaller(item);
        }) : default;
        var status = await item.Completion;
        ct.ThrowIfCancellationRequested();
        if (status == DownloadQueueItemStatus.Canceled)
            throw new OperationCanceledException("下载已取消。", ct);
        if (status != DownloadQueueItemStatus.Completed)
            throw item.LastError ?? new InvalidOperationException("下载未完成。");
        return result;
    }

    private void ShowCompletionNotification(DownloadQueueItem item)
    {
        try
        {
            if (item.Status != DownloadQueueItemStatus.Completed) return;

            var cfg = (Application.Current?.MainWindow as MainWindow)?.ConfigService.Config;
            if (cfg == null) return;

            bool noPopup = false;
            var completionMessage = item.CompletionText == null
                ? $"{item.Name} 完成" : $"{item.Name}：{item.CompletionText}";
            if (item.Name.StartsWith("安装 ") && cfg.GameVersionNoPopup) noPopup = true;
            else if (item.Name.StartsWith("下载 ") && cfg.CommunityResourceNoPopup) noPopup = true;
            else if (item.Name.StartsWith("整合包 ") && cfg.ModpackNoPopup) noPopup = true;

            // 原有“不提醒”优先；安静时段只改变仍允许出现的完成提醒。
            if (noPopup && cfg.DownloadNotifyMode == 3) return;
            if (DownloadNotificationPolicy.IsQuiet(cfg, DateTime.Now))
            {
                ReminderCenterService.Publish("download-quiet-" + item.Id, "下载完成", completionMessage);
                return;
            }
            if (noPopup)
            {
                if (cfg.DownloadNotifyMode == 1) // 角标
                {
                    ReminderCenterService.Publish("download-complete-" + item.Id, "下载完成", completionMessage);
                }
                else if (cfg.DownloadNotifyMode == 2) // Windows 通知
                {
                    ToastService.ShowSystemNotification(completionMessage, "下载完成");
                }
                // 3 = 不提示，什么都不做
            }
            else
            {
                ToastService.ShowSuccess(completionMessage);
            }
        }
        catch { }
    }

    internal static string DescribeOrigin(string file) => Path.GetFileNameWithoutExtension(file).Replace(".xaml", "") switch
    {
        "DownloadCenterPage" => "下载中心",
        "BedrockPage" => "基岩版",
        "ServerManagerPage" or "CreateServerWindow" => "服务器管理",
        "QuickStartWizardWindow" => "快速开始",
        "PluginModSourcesDialog" or "PluginManagementService" or "PluginResourceService" => "插件与资源",
        "LoaderInstallQueue" => "加载器安装",
        "WindowsDisplayDriverInstallService" => "显示驱动工具",
        "JavaDownloadQueueService" or "DirectXRuntimeInstallService" or "MainWindow" => "启动与运行环境补全",
        _ => "启动器任务"
    };
    public int PauseAllSafe()
    {
        // 先暂停安全的等待项，避免运行任务停下后调度器立刻开始下一项。
        var waiting = Items.Where(item => !item.IsRunning && item.Status == DownloadQueueItemStatus.Waiting && item.SupportsPause).ToArray();
        foreach (var entry in waiting)
        {
            entry.Status = DownloadQueueItemStatus.Paused;
            entry.StatusText = "排队已暂停，本次执行尚未开始";
            entry.StopReason = "用户请求暂停排队";
            entry.StateReason = "用户请求暂停排队；正在清理本任务此前记录的临时缓存，完整文件保留。";
            entry.IsIndeterminate = false;
            entry.IsCleaningUp = true;
            _ = CompleteWaitingPauseAsync(entry);
        }
        var entries = Items.Where(item => item.CanPause).ToArray();
        foreach (var entry in entries) Pause(entry);
        PersistRecovery();
        return waiting.Length + entries.Length;
    }
    private async Task CompleteWaitingPauseAsync(DownloadQueueItem item)
    {
        var completion = item.CompletionSource;
        await CleanupStoppedItemAsync(item);
        completion.TrySetResult(DownloadQueueItemStatus.Paused);
    }
    public int CancelSelectedWaiting(IEnumerable<DownloadQueueItem> selected)
    {
        var entries = selected.Distinct().Where(item => Items.Contains(item) && item.Status == DownloadQueueItemStatus.Waiting && !item.IsRunning).ToArray();
        foreach (var entry in entries) Cancel(entry);
        return entries.Length;
    }

    public void Pause(DownloadQueueItem item)
    {
        if (!item.CanPause) return;
        RequestStop(item, pause: true);
    }

    private static void RequestStop(DownloadQueueItem item, bool pause)
    {
        item.PauseRequested = pause;
        item.CancelRequested = !pause;
        item.StopReason = pause ? "用户请求暂停" : "用户请求取消";
        item.Status = pause ? DownloadQueueItemStatus.Pausing : DownloadQueueItemStatus.Canceling;
        item.StatusText = pause ? "正在暂停…" : "正在取消…";
        item.StateReason = (pause ? "用户请求暂停" : "用户请求取消") +
            "；正在停止当前连接和写入，确认停止后会自动清理本任务临时缓存。";
        item.IsIndeterminate = false;
        // .NET 8 CancelAsync 立即设置令牌状态，在后台执行取消回调，避免 UI 同步卡在 socket/插件清理中。
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        item.CancellationDispatch = dispatched.Task;
        _ = DispatchCancellationAsync(item.Cts, dispatched);
    }
    private static async Task DispatchCancellationAsync(CancellationTokenSource source, TaskCompletionSource completion)
    {
        try { await source.CancelAsync().ConfigureAwait(false); }
        catch (Exception ex) { LauncherLogService.AppendLine("[下载停止] 取消回调异常：" + ex.GetType().Name); }
        finally { completion.TrySetResult(); }
    }
    private static string FailureReason(Exception? failure)
    {
        if (failure == null) return "任务发生错误，下载已停止。";
        var messages = new List<string>();
        for (var current = failure; current != null && messages.Count < 20; current = current.InnerException)
            if (!string.IsNullOrWhiteSpace(current.Message) && !messages.Contains(current.Message)) messages.Add(current.Message);
        if (messages.Count > 4) messages = messages.Take(2).Concat(messages.TakeLast(2)).ToList();
        return string.Join("；原因：", messages);
    }

    public void Resume(DownloadQueueItem item)
    {
        if (!item.CanResume) return;
        var active = FindActiveForVersion(item.TargetFolderPath, item.TargetVersionId);
        if (active != null && active != item)
        {
            item.StateReason = "该版本已有其他下载或补全任务；请等待该任务结束后继续。";
            return;
        }
        item.PauseRequested = false;
        item.CancelRequested = false;
        item.StopReason = "";
        item.CancellationDispatch = Task.CompletedTask;
        item.LastError = null;
        item.CompletionText = null;
        item.Cts.Dispose();
        item.Cts = new CancellationTokenSource();
        item.CompletionSource = DownloadQueueItem.NewCompletionSource();
        item.Status = DownloadQueueItemStatus.Waiting;
        item.StatusText = "等待执行";
        item.StateReason = "";
        item.TransferObservation.ClearIssue();
        PersistRecovery();
        PumpWaiting();
    }

    private void CancelFromCaller(DownloadQueueItem item)
    {
        if (item.IsRunning && !item.SupportsCancel && !item.IsStopping)
        {
            RequestStop(item, pause: false);
            item.StopReason = "调用方已取消；系统安装程序不能安全强制中止，已等待其真实退出";
            item.StateReason = "调用方已取消；系统安装程序不能安全强制中止，正在等待它真实退出，再清理临时缓存。";
            PersistRecovery();
        }
        else Cancel(item);
    }
    public void Cancel(DownloadQueueItem item)
    {
        if (!item.CanCancel) return;
        if (item.IsRunning) { RequestStop(item, pause: false); PersistRecovery(); return; }
        if (item.Status is DownloadQueueItemStatus.Paused or DownloadQueueItemStatus.Waiting)
        {
            var waiting = item.Status == DownloadQueueItemStatus.Waiting;
            item.PauseRequested = false;
            item.CancelRequested = true;
            item.StopReason = "用户请求取消";
            item.Status = DownloadQueueItemStatus.Canceled;
            item.StatusText = waiting ? "已取消排队；本次执行尚未开始" : "已取消后续下载";
            item.StateReason = "用户请求取消；执行已经停止，正在清理本任务记录的临时缓存。完整文件保留。";
            item.IsCleaningUp = true;
            item.Cts.Dispose();
            // 已暂停轮次的 Completion 保留其原始结果；排队轮次会在缓存清理后完成。
            _ = CompleteInactiveCancellationAsync(item);
            PersistRecovery();
            return;
        }
        // 已结束条目只移出列表，保留真实结果及已完成的 Completion。
        Items.Remove(item);
        PersistRecovery();
    }
    private async Task CompleteInactiveCancellationAsync(DownloadQueueItem item)
    {
        var completion = item.CompletionSource;
        await CleanupStoppedItemAsync(item);
        completion.TrySetResult(DownloadQueueItemStatus.Canceled);
        RunOnUi(PumpWaiting);
    }

    private static async Task RefreshFileExplanationAsync(DownloadQueueItem item, CancellationTokenSource attempt)
    {
        var revision = item.ExplanationRevision;
        var summary = await Task.Run(item.TransferObservation.Summary);
        RunOnUi(() => { if (ReferenceEquals(item.Cts, attempt) && !item.IsCleaningUp && item.ExplanationRevision == revision) item.TaskExplanation = summary; });
    }
    private Task CleanupStoppedItemAsync(DownloadQueueItem item) => CleanupItemCacheAsync(item);

    private bool _pumpingWaiting;
    public Task<int> CleanupRecordedPartsAsync(DownloadQueueItem item)
    {
        RunOnUi(() =>
        {
            if (item.IsRunning || item.IsStopping || item.IsCleaningUp)
                throw new InvalidOperationException("请先停止本任务并等待本任务的缓存清理完成。");
            item.IsCleaningUp = true;
            item.StateReason = "正在后台清理本任务记录的临时缓存；完整文件保留。";
        });
        return CleanupItemCacheAsync(item);
    }
    private async Task<int> CleanupItemCacheAsync(DownloadQueueItem item)
    {
        var oldText = item.StatusText;
        item.ExplanationRevision++;
        try
        {
            var result = await Task.Run(async () =>
            {
                var cleaned = await item.TransferObservation.DeleteRecordedTemporaryFilesAsync().ConfigureAwait(false);
                return (Cleaned: cleaned, Summary: item.TransferObservation.Summary());
            });
            RunOnUi(() =>
            {
                var label = item.Status == DownloadQueueItemStatus.Paused ? "已暂停" : item.Status == DownloadQueueItemStatus.Canceled ? "已取消" : oldText;
                // 排队取消仍明确这次没有开始，不能因清理历史缓存改写事实。
                if (oldText.Contains("本次执行尚未开始")) label = oldText;
                item.StatusText = label;
                item.StateReason = (item.Status == DownloadQueueItemStatus.Failed ? FailureReason(item.LastError) + "；" :
                    string.IsNullOrWhiteSpace(item.StopReason) ? "" : item.StopReason + "；") +
                    $"已清理本任务临时文件 {result.Cleaned.Deleted} 个；完整文件保留。" +
                    (result.Cleaned.Retained == 0 ? "" : $"另有 {result.Cleaned.Retained} 个临时文件未清理：{result.Cleaned.Reason}");
                item.TaskExplanation = item.StateReason + "\n" + result.Summary;
            });
            return result.Cleaned.Deleted;
        }
        catch (Exception ex)
        {
            RunOnUi(() =>
            {
                item.StatusText = item.Status == DownloadQueueItemStatus.Paused ? "已暂停" : item.Status == DownloadQueueItemStatus.Canceled ? "已取消" : oldText;
                item.StateReason = (item.Status == DownloadQueueItemStatus.Failed ? FailureReason(item.LastError) + "；" :
                    string.IsNullOrWhiteSpace(item.StopReason) ? "" : item.StopReason + "；") +
                    "执行已停止，临时缓存清理未完成：" + ex.Message + "；完整文件保留，可稍后重试清理。";
                item.TaskExplanation = item.StateReason;
            });
            return 0;
        }
        finally { RunOnUi(() => { item.IsCleaningUp = false; PersistRecovery(); PumpWaiting(); }); }
    }
    /// <summary>只改变尚未开始的任务；运行中任务、单文件并发数和安装阶段不受排序影响。</summary>
    public void RefreshScheduling() => RunOnUi(PumpWaiting);
    private void PumpWaiting()
    {
        if (_pumpingWaiting) return;
        _pumpingWaiting = true;
        try
        {
            while (true)
            {
                var limit = Math.Clamp(Config?.DownloadTaskConcurrency ?? 0, 0, 8);
                var next = Items.FirstOrDefault(entry => entry.CanMoveWaiting &&
                    (entry.BypassScheduler || limit == 0 || Items.Count(active => active.IsRunning && !active.BypassScheduler) < limit));
                if (next == null) break;
                next.IsRunning = true;
                _ = RunAsync(next);
            }
        }
        finally { _pumpingWaiting = false; }
    }
    public void MoveWaiting(DownloadQueueItem item, int direction) => RunOnUi(() =>
    {
        if (!item.CanMoveWaiting || !Items.Contains(item)) return;
        var index = Items.IndexOf(item);
        var target = direction < 0
            ? Enumerable.Range(0, index).Reverse().FirstOrDefault(i => Items[i].CanMoveWaiting, -1)
            : Enumerable.Range(index + 1, Items.Count - index - 1).FirstOrDefault(i => Items[i].CanMoveWaiting, -1);
        if (target >= 0) Items.Move(index, target);
    });
    public int RetryFailed()
    {
        var failed = Items.Where(entry => entry.Status == DownloadQueueItemStatus.Failed && entry.CanResume).ToArray();
        foreach (var item in failed) Resume(item);
        return failed.Length;
    }

    internal static void PostOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(action);
        else action();
    }
    private static void RunOnUi(Action action)
    {
        var app = Application.Current;
        if (app?.Dispatcher == null || app.Dispatcher.CheckAccess())
            action();
        else
            app.Dispatcher.Invoke(action);
    }
}
