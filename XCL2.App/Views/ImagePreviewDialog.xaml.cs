using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>
/// 背景候选池"预览图片"的极简放大预览弹窗：给背景候选列表看图用，不做任何编辑，
/// 也不影响当前正在应用的背景——跟"点列表条目 = 设为当前背景"是两件完全独立的事，
/// 用户可能只是想看清楚某一张长什么样，并不一定想切换过去。
/// </summary>
public partial class ImagePreviewDialog : OverlayDialogControl
{
    public ImagePreviewDialog(string path)
    {
        InitializeComponent();
        FileNameText.Text = Path.GetFileName(path);

        try
        {

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            PreviewImage.Source = bmp;
        }
        catch (Exception ex)
        {
            FileNameText.Text += "（图片加载失败）";
            ErrorPresenter.LogTechnicalDetail($"[背景图片预览加载失败] {path}\n{ex}");
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseWith(null);
}
