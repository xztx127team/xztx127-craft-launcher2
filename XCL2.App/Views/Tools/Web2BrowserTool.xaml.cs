using System;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views.Tools;

/// <summary>按需创建内嵌网页；只有当前地址无法内嵌加载时才提供系统浏览器入口。</summary>
public partial class Web2BrowserTool : UserControl
{
    private IEmbeddedWebView? _browser;
    private bool _initializing;
    private int _lifetimeVersion;
    private int _browserVersion;
    private int _addressVersion;
    private int _browserAddressVersion = -1;
    private string? _navigationUrl;
    private string? _fallbackUrl;

    public Web2BrowserTool()
    {
        InitializeComponent();
    }

    private bool TryResolveUrl(out string url)
    {
        var value = UrlBox.Text.Trim();
        if (string.IsNullOrEmpty(value)) value = "https://xztx127.dpdns.org/xztx127gjx";
        if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        if (ExternalLinkService.TryGetWebUri(value, out var uri))
        {
            url = uri!.AbsoluteUri;
            return true;
        }
        url = "";
        return false;
    }

    private async void OpenEmbeddedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        HideExternalFallback();
        if (!TryResolveUrl(out var url))
        {
            Web2StatusText.Text = "请输入有效的 http / https 网页地址。";
            return;
        }
        var lifetimeVersion = _lifetimeVersion;
        var addressVersion = _addressVersion;
        var requestBrowserVersion = _browserVersion;
        _initializing = true;
        _browserAddressVersion = -1;
        OpenEmbeddedButton.IsEnabled = false;
        Web2StatusText.Text = "正在启动 WebView2 并加载页面…";

        try
        {
            if (!EmbeddedWebView.TryCheckComponents(out var detail))
            {
                ReleaseBrowser();
                ShowExternalFallback(url, addressVersion, EmbeddedWebView.MissingComponentsMessage);
                ErrorPresenter.LogTechnicalDetail($"Web2 工具 WebView2 组件不可用：{detail}");
                return;
            }
            if (!WebView2RuntimeDetector.IsAvailable())
            {
                ReleaseBrowser();
                ShowExternalFallback(url, addressVersion, "未检测到 WebView2 Runtime，可改用系统浏览器访问。");
                return;
            }

            BrowserCard.Visibility = Visibility.Visible;
            if (_browser == null)
            {
                var browserVersion = ++_browserVersion;
                requestBrowserVersion = browserVersion;
                _browser = EmbeddedWebView.Create(
                    (success, error) => Browser_NavigationCompleted(browserVersion, success, error),
                    enableDevTools: true,
                    navigationStarting: target => Browser_NavigationStarting(browserVersion, target));
                BrowserHost.Children.Add(_browser.View);
                await _browser.InitializeAsync();
            }

            if (lifetimeVersion != _lifetimeVersion) return;
            if (addressVersion != _addressVersion)
            {
                BrowserCard.Visibility = Visibility.Collapsed;
                return;
            }
            _browserAddressVersion = addressVersion;
            _navigationUrl = url;
            _browser?.Navigate(url);
        }
        catch (Exception) when (lifetimeVersion != _lifetimeVersion || addressVersion != _addressVersion)
        {
            // 页面已离开或输入地址已改变，旧请求不能改写当前地址的失败状态。
            if (lifetimeVersion == _lifetimeVersion && requestBrowserVersion == _browserVersion)
                ReleaseBrowser();
        }
        catch (Exception ex)
        {
            ReleaseBrowser();
            var message = EmbeddedWebView.IsComponentLoadFailure(ex)
                ? EmbeddedWebView.MissingComponentsMessage
                : "内嵌页面无法打开，可重试或改用系统浏览器。";
            ShowExternalFallback(url, addressVersion, message);
            ErrorPresenter.LogTechnicalDetail($"Web2 工具内嵌网页初始化失败：{ex}");
        }
        finally
        {
            if (lifetimeVersion == _lifetimeVersion)
            {
                _initializing = false;
                OpenEmbeddedButton.IsEnabled = true;
            }
        }
    }

    private void Browser_NavigationStarting(int browserVersion, string target)
    {
        if (!IsCurrentBrowserRequest(browserVersion)) return;
        HideExternalFallback();
        _navigationUrl = ExternalLinkService.TryGetWebUri(target, out var uri) ? uri!.AbsoluteUri : null;
        Web2StatusText.Text = "正在加载页面…";
    }

    private void Browser_NavigationCompleted(int browserVersion, bool isSuccess, string errorStatus)
    {
        if (!IsCurrentBrowserRequest(browserVersion)) return;
        if (isSuccess)
        {
            HideExternalFallback();
            Web2StatusText.Text = "页面已加载。";
        }
        else if (_navigationUrl != null)
        {
            ShowExternalFallback(_navigationUrl, _browserAddressVersion,
                $"页面加载失败（WebErrorStatus: {errorStatus}），可重试或改用系统浏览器。");
        }
    }

    private bool IsCurrentBrowserRequest(int browserVersion) =>
        IsLoaded && _browser != null && browserVersion == _browserVersion &&
        _browserAddressVersion == _addressVersion;

    private void ShowExternalFallback(string url, int addressVersion, string message)
    {
        if (addressVersion != _addressVersion) return;
        _fallbackUrl = url;
        OpenExternalButton.ToolTip = url;
        OpenExternalButton.Visibility = Visibility.Visible;
        Web2StatusText.Text = message;
    }

    private void HideExternalFallback()
    {
        _fallbackUrl = null;
        if (OpenExternalButton == null) return;
        OpenExternalButton.ToolTip = null;
        OpenExternalButton.Visibility = Visibility.Collapsed;
    }

    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _addressVersion++;
        HideExternalFallback();
        // TextChanged 也会在 XAML 字段尚未全部连接时触发。
        if (Web2StatusText != null)
            Web2StatusText.Text = "点击“在启动器内打开”加载当前网址。";
    }

    private void OpenExternalButton_Click(object sender, RoutedEventArgs e)
    {
        if (OpenExternalButton.Visibility == Visibility.Visible && _fallbackUrl != null)
            ExternalLinkService.Open(_fallbackUrl);
    }

    private void Web2BrowserTool_Unloaded(object sender, RoutedEventArgs e)
    {
        _lifetimeVersion++;
        _initializing = false;
        OpenEmbeddedButton.IsEnabled = true;
        HideExternalFallback();
        ReleaseBrowser();
    }

    private void ReleaseBrowser()
    {
        _browserVersion++;
        _browserAddressVersion = -1;
        _navigationUrl = null;
        var browser = _browser;
        _browser = null;
        try { browser?.Dispose(); }
        catch (Exception ex) { ErrorPresenter.LogTechnicalDetail($"Web2 浏览器释放失败：{ex}"); }
        BrowserHost.Children.Clear();
        BrowserCard.Visibility = Visibility.Collapsed;
    }
}
