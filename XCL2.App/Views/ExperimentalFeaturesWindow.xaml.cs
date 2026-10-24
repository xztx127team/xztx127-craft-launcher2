using System.Windows;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// "实验性功能"面板：换肤（白/蓝/黄/紫/粉，各自浅色版/深色版）已经全部做完，
/// 搬到了正式的「设置」页配色皮肤下拉框 + 首页「模式设置」/「自动循环」按钮，
/// 不再放在这个实验性面板里（见 ExperimentalFeaturesWindow.xaml 顶部注释）。
/// 现在这里只剩"初步测试版功能"这一层，比正式功能更不成熟，需要单独的强烈风险
/// 提示 + token 校验才能进入。
///
/// 只有先通过 ExperimentalGateWindow 那次强制 10 秒等待确认之后，调用方（SettingsPage）
/// 才会打开这个窗口——这里不重复校验 AppConfig.ExperimentalFeaturesUnlocked，信任调用方
/// 已经做过这个检查（同一个模式在这个项目里很常见，比如各个 XxxWindow 都不重复校验
/// 调用者是否已经登录/已经选好版本，这类前置条件由外层页面负责）。
/// </summary>
public partial class ExperimentalFeaturesWindow : OverlayDialogControl
{
    private readonly MainWindow _owner;

    public ExperimentalFeaturesWindow(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();
        MiniWorldFeatureButton.Visibility = owner.ConfigService.Config.EnableMiniWorldExperimental ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        CloseWith(null);
    }

    private void MiniWorldFeature_Click(object sender, RoutedEventArgs e)
    {
        // 每次打开都阅读并答题；不复用外层实验功能总开关的一次性解锁。
        if (!_owner.ConfigService.Config.EnableMiniWorldExperimental) return;
        if (new MiniWorldGateDialog().ShowDialog() != true) return;
        if (_owner.ConfigService.Config.EnableMiniWorldExperimental)
            new MiniWorldLauncherDialog(_owner).ShowDialog();
    }

    /// <summary>多加载器合装保留自己的风险提示与授权校验。</summary>
    private void MultiLoaderFeature_Click(object sender, RoutedEventArgs e)
    {
        var window = new MultiLoaderInstallWindow(_owner) ;
        var result = window.ShowDialog();

        // 修复："一锅乱炖"装完之后启动器找不到刚装好的版本，根本原因是这里以前从不
        // 处理 ShowDialog() 的返回值——版本文件确实已经装进 versions/ 目录了，
        // 只是没人告诉版本选择页"该重新扫一遍了"。现在只要 MultiLoaderInstallWindow
        // 报告"至少装成功了一个"，就顺手刷新一下版本页（如果它正开着的话）。
        if (result == true && window.InstalledVersionIds.Count > 0)
        {
            _owner.RefreshVersionsPageIfActive();
        }
    }
}
