using System.IO;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views.Tools;

/// <summary>Uses the site's real PNG/GIF/video export, including blob downloads.</summary>
public sealed class HongheAssetTool : UserControl
{
    private readonly string _url;
    private readonly Action<string> _useIcon;
    private readonly Grid _host = new() { MinHeight = 560 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly Button _open = new() { Content = "加载红河素材站", Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _use = new() { Content = "将刚下载的 PNG 用于成就图片", IsEnabled = false };
    private IEmbeddedWebView? _browser;
    private string? _downloadedPng;
    private int _generation;
    private bool _opening;

    public HongheAssetTool(string title, string url, string hint, Action<string> useIcon)
    {
        _url = url;
        _useIcon = useIcon;
        var root = new DockPanel { Margin = new Thickness(4, 12, 4, 4) };
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 10) });
        var actions = new WrapPanel();
        actions.Children.Add(_open);
        var external = new Button { Content = "系统浏览器打开", Margin = new Thickness(0, 0, 8, 0) };
        external.Click += (_, _) => ExternalLinkService.Open(_url);
        actions.Children.Add(external);
        var import = new Button { Content = "选择已下载的 PNG", Margin = new Thickness(0, 0, 8, 0) };
        import.Click += (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Title = "选择红河 PNG 图标", Filter = "PNG 图片|*.png" };
            if (picker.ShowDialog(Window.GetWindow(this)) == true) _useIcon(picker.FileName);
        };
        actions.Children.Add(import);
        actions.Children.Add(_use);
        heading.Children.Add(actions);
        heading.Children.Add(_status);
        _status.Text = "点击加载后，在网站中搜索、预览并导出。离开此工具会取消尚未完成的导出和下载。";
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);
        root.Children.Add(_host);
        Content = root;
        _open.Click += Open;
        _use.Click += (_, _) => { if (_downloadedPng != null) _useIcon(_downloadedPng); };
        Unloaded += (_, _) => Release();
    }

    private async void Open(object sender, RoutedEventArgs e)
    {
        if (_opening) return;
        Release();
        var generation = _generation;
        _opening = true;
        _open.IsEnabled = false;
        _status.Text = "正在加载红河素材站…";
        try
        {
            if (!EmbeddedWebView.TryCheckComponents(out var detail))
                throw new InvalidOperationException(EmbeddedWebView.MissingComponentsMessage);
            if (!WebView2RuntimeDetector.IsAvailable())
                throw new InvalidOperationException("未安装 WebView2 Runtime。可使用系统浏览器导出，再选择下载的 PNG。");
            var browser = EmbeddedWebView.Create((success, error) =>
            {
                if (generation == _generation)
                    _status.Text = success ? "使用网站的“下载 PNG / 导出视频 / 导出 PNG”按钮，随后选择保存位置。" : "页面加载失败：" + error + "。可重试或使用系统浏览器。";
            }, downloadCompleted: path =>
            {
                if (generation != _generation) return;
                _status.Text = "已保存：" + path;
                _downloadedPng = string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase) ? path : null;
                _use.IsEnabled = _downloadedPng != null;
                ToastService.ShowSuccess("素材已保存");
            }, downloadFailed: message =>
            {
                if (generation != _generation) return;
                _status.Text = message;
                ToastService.ShowWarning(message);
            });
            _browser = browser;
            _host.Children.Add(browser.View);
            await browser.InitializeAsync();
            if (generation != _generation) return;
            browser.Navigate(_url);
        }
        catch (Exception ex)
        {
            if (generation != _generation) return;
            Release();
            _status.Text = "素材页面未能打开：" + ex.Message;
            ErrorPresenter.LogTechnicalDetail("红河素材页面：" + ex);
        }
        finally
        {
            if (generation == _generation) { _opening = false; _open.IsEnabled = true; }
        }
    }

    private void Release()
    {
        _generation++;
        _opening = false;
        _open.IsEnabled = true;
        var browser = _browser;
        _browser = null;
        try { browser?.Dispose(); }
        catch (Exception ex) { ErrorPresenter.LogTechnicalDetail("红河素材页面关闭：" + ex); }
        _host.Children.Clear();
    }
}
