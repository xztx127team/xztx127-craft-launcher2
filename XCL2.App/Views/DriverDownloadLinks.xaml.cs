using System.Windows;
using System.Windows.Controls;
using System.IO;
using Microsoft.Win32;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class DriverDownloadLinks : UserControl
{
    public DriverDownloadLinks() => InitializeComponent();
    private void DownloadPackage_Click(object sender, RoutedEventArgs e)
    {
        var url = DriverPackageUrl.Text.Trim();
        if (!MiniWorldVersionService.IsWebAddress(url))
        { MessageBoxDialog.ShowWarning("请填写显卡厂商提供的有效 https 安装包直链。", "下载 OpenGL 显卡驱动"); return; }
        var suggested = Path.GetFileName(new Uri(url).LocalPath);
        if (!suggested.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) suggested = "GraphicsDriver-Setup.exe";
        var picker = new SaveFileDialog { Title = "保存显卡驱动安装包", Filter = "Windows 安装程序 (*.exe)|*.exe", DefaultExt = ".exe", FileName = suggested, OverwritePrompt = true };
        if (picker.ShowDialog() != true) return;
        var destination = picker.FileName;
        var task = DownloadQueueService.Instance.StartNew("OpenGL 显卡驱动 · " + Path.GetFileName(destination), async (item, ct) =>
        {
            var staging = Path.Combine(Path.GetDirectoryName(destination)!, ".xcl-driver-" + item.Id.ToString("N"));
            try
            {
                using var downloader = new GenericFileDownloadService();
                var downloaded = await downloader.DownloadAsync(new[] { url }, staging, Path.GetFileName(destination), null, item.CreateProgress(), ct,
                    validateFile: MiniWorldCatalog.ValidateDownloadedExecutable);
                MiniWorldCatalog.ValidateDownloadedExecutable(downloaded); ct.ThrowIfCancellationRequested();
                File.Move(downloaded, destination, overwrite: true);
                item.CompletionText = "驱动安装包已下载：" + destination;
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }, originPage: "OpenGL 显卡驱动");
        DriverDownloadStatus.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(DownloadQueueItem.StatusText)) { Source = task });
        ToastService.ShowInfo("驱动安装包已加入下载管理。");
    }
    private void DownloadOpenGlDll_Click(object sender, RoutedEventArgs e)
        => OpenGlDllDownloadService.StartWithConfirmation();
    private void OpenLink_Click(object sender, RoutedEventArgs e)
        => ExternalLinkService.Open((sender as Button)?.Tag as string);
    private void OpenCustom_Click(object sender, RoutedEventArgs e)
        => ExternalLinkService.Open(CustomUrl.Text.Trim());
}
