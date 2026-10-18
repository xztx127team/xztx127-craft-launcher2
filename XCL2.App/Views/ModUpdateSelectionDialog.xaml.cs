using System.Linq;
using System.Windows;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>见 ModUpdateSelectionDialog.xaml 顶部注释：一键批量升级模组时的"选择要升级哪些"弹窗。</summary>
public partial class ModUpdateSelectionDialog : OverlayDialogControl
{
    /// <summary>ItemsControl 绑定用的行包装：直接持有对应的 UpdateCandidate，
    /// IsSelected 双向绑定 CheckBox，弹窗关闭时把结果写回 candidate.Selected 即可，
    /// 不需要克隆一份数据、也不需要 INotifyPropertyChanged（跟 HiddenInstancesDialog.HiddenItem
    /// 是同一个思路：CheckBox 是最终唯一的写入方，读取只发生在用户点了"确定"之后）。</summary>
    public class CandidateRow
    {
        public UpdateCandidate Candidate { get; }
        public string DisplayName => Candidate.DisplayName;
        public string VersionChangeText =>
            (string.IsNullOrEmpty(Candidate.CurrentVersionName) ? "当前版本" : Candidate.CurrentVersionName)
            + " → " + Candidate.NewVersionName
            + (Candidate.IsCurrentlyDisabled ? "（当前已禁用，升级后维持禁用状态）" : "");
        public string CompatibilityText => string.IsNullOrWhiteSpace(Candidate.CompatibilitySummary) ? "未提供适配范围。" : Candidate.CompatibilitySummary;
        public string DependencyText => string.IsNullOrWhiteSpace(Candidate.DependencySummary) ? "未提供依赖信息。" : Candidate.DependencySummary;
        public string UpdateNotes => string.IsNullOrWhiteSpace(Candidate.UpdateNotes) ? "此版本未提供更新说明。" : Candidate.UpdateNotes;
        public bool IsSelected { get; set; } = true;
        public bool CanSelect { get; set; } = true;
        public Visibility IgnoreVisibility { get; set; } = Visibility.Collapsed;

        public CandidateRow(UpdateCandidate candidate) => Candidate = candidate;
    }

    private readonly List<CandidateRow> _rows;
    private readonly Action<UpdateCandidate>? _ignoreVersion;

    public ModUpdateSelectionDialog(List<UpdateCandidate> candidates, Action<UpdateCandidate>? ignoreVersion = null, bool readOnly = false, string? cacheLabel = null)
    {
        InitializeComponent();
        var owner = System.Windows.Application.Current.MainWindow;
        MaxWidth = Math.Max(360, (owner?.ActualWidth ?? 854) - 100);
        DialogScroll.MaxHeight = Math.Max(260, (owner?.ActualHeight ?? 480) - 90);
        CandidateScroll.MaxHeight = Math.Clamp((owner?.ActualHeight ?? 480) - 310, 80, 340);
        _ignoreVersion = ignoreVersion;
        _rows = candidates.Select(c => new CandidateRow(c) { CanSelect = !readOnly, IgnoreVisibility = !readOnly && ignoreVersion != null ? Visibility.Visible : Visibility.Collapsed }).ToList();
        CandidateListControl.ItemsSource = _rows;
        TitleText.Text = $"发现 {candidates.Count} 个模组有更新";
        SummaryText.Text = (cacheLabel == null ? "" : cacheLabel + "\n") + (readOnly ? "只读资料：不会使用缓存执行更新。需要更新时请重新检查。" : $"共 {candidates.Count} 个，默认全部勾选。忽略仅针对这个更新版本。");
        if (readOnly) { TitleText.Text = "上次检查的模组资料"; InstructionsText.Text = "这是手动检查时缓存的资料，可能已经变化。"; OkBtn.Visibility = Visibility.Collapsed; SelectAllBtn.Visibility = Visibility.Collapsed; SelectNoneBtn.Visibility = Visibility.Collapsed; CancelBtn.Content = "关闭"; }
    }

    private void IgnoreVersion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: CandidateRow row } || _ignoreVersion == null) return;
        try
        {
            _ignoreVersion(row.Candidate);
            row.Candidate.Selected = false; _rows.Remove(row); RefreshCheckBoxes();
            SummaryText.Text = $"还显示 {_rows.Count} 个更新。可在模组页“更多 → 已忽略的更新版本”中恢复提醒。";
        }
        catch (Exception ex) { ErrorPresenter.ShowFriendlyError("无法保存忽略规则。", ex.Message, "忽略更新版本"); }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.IsSelected = true;
        RefreshCheckBoxes();
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.IsSelected = false;
        RefreshCheckBoxes();
    }

    /// <summary>全选/全不选按钮直接改的是数据源（没有 INotifyPropertyChanged），
    /// CheckBox 不会自动跟着刷新，重新绑一次 ItemsSource 强制界面重新读取当前值。</summary>
    private void RefreshCheckBoxes()
    {
        CandidateListControl.ItemsSource = null;
        CandidateListControl.ItemsSource = _rows;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.Candidate.Selected = row.IsSelected;

        if (_rows.All(r => !r.IsSelected))
        {
            MessageBoxDialog.ShowInfo("一个都没勾选，没有需要升级的模组。可以点「取消」放弃这次升级。", Loc.T("Str_Status_Tip", "提示"));
            return;
        }

        CloseWith(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CloseWith(false);
    }
}
