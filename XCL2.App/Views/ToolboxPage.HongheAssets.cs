using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using XCL2.App.Services;
using XCL2.App.Views.Tools;

namespace XCL2.App.Views;

public partial class ToolboxPage
{
    private byte[]? _achievementImportedIcon;
    private string? _achievementImportedIconName;
    private int _hongheIconsTabIndex;

    private void InitializeHongheAssetTools()
    {
        _hongheIconsTabIndex = ToolboxTabControl.Items.Count;
        Add("游戏图标与原版物品下载", HongheAssetPolicy.IconsUrl,
            "图标来源：红河素材站 honghemc.cn。支持中英文搜索和透明 PNG；图标详情中可制作附魔 GIF。下载后可直接用于成就图片。");
        Add("动画、粒子与绿幕视频素材", HongheAssetPolicy.EffectsUrl,
            "使用红河绿幕工作台的真实转场、粒子与动画模板，预览后导出视频。视频渲染可能占用较多 CPU / GPU，请等待导出完成再离开。");
        Add("MC 主题 3D 文字下载", HongheAssetPolicy.Text3dUrl,
            "在红河 3D 文字工具中输入中英文，调整字体、材质和角度，导出透明 PNG。");

        void Add(string title, string url, string hint) => ToolboxTabControl.Items.Add(new TabItem
        {
            Header = title,
            Content = new HongheAssetTool(title, url, hint, UseHonghePngForAchievement)
        });
    }

    private void AchOpenHonghe_Click(object sender, RoutedEventArgs e) => SelectToolboxFeature(_hongheIconsTabIndex);

    private void AchImportIcon_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择从红河素材站下载的 PNG", Filter = "PNG 图片|*.png" };
        if (dialog.ShowDialog(_owner) == true) UseHonghePngForAchievement(dialog.FileName);
    }

    private void AchClearIcon_Click(object sender, RoutedEventArgs e)
    {
        if (_achGenerating) { ToastService.ShowWarning("成就图片正在生成，请完成后再清除图标。"); return; }
        _achievementImportedIcon = null;
        _achievementImportedIconName = null;
        AchImportedIconText.Text = "尚未选择红河 PNG。可从素材站下载后导入；未导入时按物品 ID 查找贴图。";
        _achPreviewBytes = null;
        AchPreviewImage.Source = null;
    }

    private void UseHonghePngForAchievement(string path)
    {
        try
        {
            if (_achGenerating) { ToastService.ShowWarning("成就图片正在生成，请完成后再切换图标。"); return; }
            var bytes = HongheAssetPolicy.ReadIcon(path);
            using var stream = new MemoryStream(bytes);
            // Decode once to reject truncated/corrupt files before changing the selected icon.
            _ = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            _achievementImportedIcon = bytes;
            _achievementImportedIconName = Path.GetFileName(path);
            AchImportedIconText.Text = "已选 PNG：" + _achievementImportedIconName + "（优先用于成就图片；不改变原文件）";
            _achPreviewBytes = null;
            AchPreviewImage.Source = null;
            SelectToolboxFeature(0);
            ToastService.ShowSuccess("图标已导入，点击生成预览查看效果");
        }
        catch (Exception ex)
        {
            AchImportedIconText.Text = "导入失败：" + ex.Message;
            ToastService.ShowWarning(AchImportedIconText.Text);
        }
    }
}
