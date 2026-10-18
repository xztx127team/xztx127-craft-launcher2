namespace System.Windows
{
    public enum Visibility { Visible, Hidden, Collapsed }
    public enum HorizontalAlignment { Left, Stretch }
    public enum VerticalAlignment { Stretch }
    public class RoutedEventArgs : EventArgs { }
    public class FrameworkElement
    {
        public bool IsLoaded { get; set; } = true;
        public Visibility Visibility { get; set; }
        public HorizontalAlignment HorizontalAlignment { get; set; }
        public VerticalAlignment VerticalAlignment { get; set; }
        public double MinHeight { get; set; }
    }
}
namespace System.Windows.Controls
{
    public class UserControl : System.Windows.FrameworkElement { }
    public class TextChangedEventArgs : System.Windows.RoutedEventArgs { }
    public class TextBox { public string Text { get; set; } = ""; }
    public class TextBlock { public string Text { get; set; } = ""; }
    public class Button : System.Windows.FrameworkElement
    {
        public bool IsEnabled { get; set; } = true;
        public object? ToolTip { get; set; }
    }
    public class Border : System.Windows.FrameworkElement { }
    public class Grid : System.Windows.FrameworkElement
    {
        public List<System.Windows.FrameworkElement> Children { get; } = new();
    }
}
namespace Microsoft.Web.WebView2.Core
{
    public enum CoreWebView2WebErrorStatus { Unknown, OperationCanceled, ConnectionAborted }
    public class CoreWebView2NavigationStartingEventArgs(ulong navigationId, string uri) : EventArgs
    {
        public ulong NavigationId { get; } = navigationId;
        public string Uri { get; } = uri;
    }
    public class CoreWebView2NavigationCompletedEventArgs(ulong navigationId, bool success, CoreWebView2WebErrorStatus error) : EventArgs
    {
        public ulong NavigationId { get; } = navigationId;
        public bool IsSuccess { get; } = success;
        public CoreWebView2WebErrorStatus WebErrorStatus { get; } = error;
    }
    public class CoreWebView2Settings
    {
        public bool AreDefaultContextMenusEnabled { get; set; }
        public bool AreDevToolsEnabled { get; set; }
    }
    public class CoreWebView2
    {
        public CoreWebView2Settings Settings { get; } = new();
        public event EventHandler<CoreWebView2NavigationStartingEventArgs>? NavigationStarting;
        public event EventHandler<CoreWebView2NavigationCompletedEventArgs>? NavigationCompleted;
        public string? RequestedUrl { get; private set; }
        public void Navigate(string uri) => RequestedUrl = uri;
        public void Start(ulong id, string uri) => NavigationStarting?.Invoke(this, new(id, uri));
        public void Complete(ulong id, bool success) => NavigationCompleted?.Invoke(this,
            new(id, success, CoreWebView2WebErrorStatus.OperationCanceled));
    }
}
namespace Microsoft.Web.WebView2.Wpf
{
    public class WebView2 : System.Windows.FrameworkElement, IDisposable
    {
        public Microsoft.Web.WebView2.Core.CoreWebView2 CoreWebView2 { get; } = new();
        public Task EnsureCoreWebView2Async() => Task.CompletedTask;
        public void Dispose() { }
    }
}
namespace XCL2.App.Services
{
    public static class WebView2RuntimeDetector { public static bool IsAvailable() => true; }
    public static class ErrorPresenter { public static void LogTechnicalDetail(string message) { } }
}
namespace XCL2.App.Views
{
    public static class MessageBoxDialog { public static void ShowWarning(string text, string title) { } }
}
namespace XCL2.App.Views.Tools
{
    public partial class Web2BrowserTool
    {
        private readonly System.Windows.Controls.TextBox UrlBox = new();
        private readonly System.Windows.Controls.Button OpenEmbeddedButton = new();
        private readonly System.Windows.Controls.Button OpenExternalButton = new() { Visibility = System.Windows.Visibility.Collapsed };
        private readonly System.Windows.Controls.TextBlock Web2StatusText = new();
        private readonly System.Windows.Controls.Border BrowserCard = new();
        private readonly System.Windows.Controls.Grid BrowserHost = new();
        private void InitializeComponent() { }
        internal bool HasFallback => OpenExternalButton.Visibility == System.Windows.Visibility.Visible;
        internal string? Fallback => _fallbackUrl;
        internal string Status => Web2StatusText.Text;
        internal async Task<Microsoft.Web.WebView2.Core.CoreWebView2> AttachFixtureBrowserAsync()
        {
            var version = ++_browserVersion;
            _browser = Services.EmbeddedWebView.Create(
                (success, error) => Browser_NavigationCompleted(version, success, error),
                navigationStarting: uri => Browser_NavigationStarting(version, uri));
            await _browser.InitializeAsync();
            return ((Microsoft.Web.WebView2.Wpf.WebView2)_browser.View).CoreWebView2;
        }
        internal void RequestFixtureNavigation(string uri)
        {
            HideExternalFallback();
            _browserAddressVersion = _addressVersion;
            _navigationUrl = uri;
            _browser!.Navigate(uri);
        }
        internal void EditAddress(string uri)
        {
            UrlBox.Text = uri;
            UrlBox_TextChanged(this, new());
        }
        internal void LeavePage() => Web2BrowserTool_Unloaded(this, new());
    }
}
