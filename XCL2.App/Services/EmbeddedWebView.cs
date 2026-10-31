using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace XCL2.App.Services;

/// <summary>
/// 页面只持有该不引用 WebView2 SDK 类型的接口。SDK 缺失时，页面本身和 Unloaded
/// 仍能被 JIT 编译；实际 SDK 类型只会在用户主动打开网页后的独立调用边界内解析。
/// </summary>
internal interface IEmbeddedWebView : IDisposable
{
    FrameworkElement View { get; }
    Task InitializeAsync();
    void Navigate(string url);
}

internal static class EmbeddedWebView
{
    public const string MissingComponentsMessage =
        "启动器的 WebView2 组件缺失或损坏。请重新解压完整发布包，保留 Microsoft.Web.WebView2.Core.dll、Microsoft.Web.WebView2.Wpf.dll 和 runtimes 目录，然后重启。安装 WebView2 Runtime 不能补齐这些程序 DLL。当前仍可使用系统浏览器。";

    /// <summary>按本程序编译时引用的完整程序集名称探测，兼容普通发布和单文件发布。</summary>
    public static bool TryCheckComponents(out string? detail)
    {
        try
        {
            var references = typeof(EmbeddedWebView).Assembly.GetReferencedAssemblies();
            foreach (var name in new[] { "Microsoft.Web.WebView2.Core", "Microsoft.Web.WebView2.Wpf" })
            {
                var reference = references.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
                Assembly.Load(reference ?? new AssemblyName(name));
            }
            detail = null;
            return true;
        }
        catch (Exception ex) when (IsComponentLoadFailure(ex))
        {
            detail = ex.ToString();
            return false;
        }
    }

    public static bool IsComponentLoadFailure(Exception ex) =>
        ex is FileNotFoundException or FileLoadException or BadImageFormatException or DllNotFoundException or TypeLoadException
        || ex.InnerException != null && IsComponentLoadFailure(ex.InnerException);

    // 不能内联：否则 JIT 可能在调用方进入 try/catch 之前加载 SDK 类型。
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IEmbeddedWebView Create(Action<bool, string> navigationCompleted, bool enableDevTools = false,
        Action<string>? navigationStarting = null, Action<string>? downloadCompleted = null,
        Action<string>? downloadFailed = null) =>
        new WebViewImplementation(navigationCompleted, enableDevTools, navigationStarting, downloadCompleted, downloadFailed);

    private sealed class WebViewImplementation : IEmbeddedWebView
    {
        private readonly WebView2 _view;
        private readonly Action<bool, string> _navigationCompleted;
        private readonly bool _enableDevTools;
        private readonly Action<string>? _navigationStarting;
        private readonly Action<string>? _downloadCompleted;
        private readonly Action<string>? _downloadFailed;
        private readonly Dictionary<CoreWebView2DownloadOperation, EventHandler<object>> _downloads = new();
        private ulong? _latestNavigationId;
        private bool _disposed;
        private bool _subscribed;

        public WebViewImplementation(Action<bool, string> navigationCompleted, bool enableDevTools,
            Action<string>? navigationStarting, Action<string>? downloadCompleted, Action<string>? downloadFailed)
        {
            _navigationCompleted = navigationCompleted;
            _enableDevTools = enableDevTools;
            _navigationStarting = navigationStarting;
            _downloadCompleted = downloadCompleted;
            _downloadFailed = downloadFailed;
            _view = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                MinHeight = 560
            };
        }

        public FrameworkElement View => _view;

        public async Task InitializeAsync()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _view.EnsureCoreWebView2Async();
            // 初始化期间页面可能已经切换。不能访问已释放的 CoreWebView2，也不能复活页面。
            ObjectDisposedException.ThrowIf(_disposed, this);
            _view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _view.CoreWebView2.Settings.AreDevToolsEnabled = _enableDevTools;
            _view.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            _view.CoreWebView2.NavigationStarting += OnNavigationStarting;
            if (_downloadCompleted != null) _view.CoreWebView2.DownloadStarting += OnDownloadStarting;
            _subscribed = true;
        }

        // WebView2 handles blob: exports as well as ordinary HTTP downloads. Keep the
        // native save dialog outside the WebView event stack using a deferral.
        private async void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
        {
            CoreWebView2Deferral? deferral = null;
            try
            {
                deferral = e.GetDeferral();
                e.Handled = true;
                await _view.Dispatcher.InvokeAsync(() =>
                {
                    if (_disposed) { TryCancel(e); return; }
                    var suggested = Path.GetFileName(e.ResultFilePath);
                    if (!HongheAssetPolicy.IsSupportedDownload(suggested))
                    {
                        TryCancel(e);
                        _downloadFailed?.Invoke("仅支持图片和视频素材下载（PNG/JPG/WebP/GIF/WebM/MP4/MOV）。");
                        return;
                    }
                    var extension = Path.GetExtension(suggested).ToLowerInvariant();
                    var dialog = new Microsoft.Win32.SaveFileDialog
                    {
                        Title = "保存红河素材", FileName = suggested, DefaultExt = extension,
                        Filter = $"素材文件 (*{extension})|*{extension}", AddExtension = true, OverwritePrompt = true
                    };
                    if (dialog.ShowDialog(System.Windows.Window.GetWindow(_view)) != true || _disposed)
                    { TryCancel(e); return; }
                    // ShowDialog runs a nested message loop. Unload/close can dispose the browser
                    // while the picker is open, so recheck it before accessing any COM event args.
                    e.ResultFilePath = dialog.FileName;
                    TrackDownload(e.DownloadOperation);
                });
            }
            catch (Exception ex)
            {
                TryCancel(e);
                if (!_disposed) _downloadFailed?.Invoke("无法保存素材：" + ex.Message);
            }
            finally
            {
                // A deferred callback can finish after its browser was disposed. Never let an
                // invalid COM deferral escape this async-void event handler into the launcher.
                try { deferral?.Dispose(); }
                catch (Exception ex) when (ex is InvalidOperationException or COMException)
                { ErrorPresenter.LogTechnicalDetail("结束素材下载请求：" + ex); }
            }
        }

        private static void TryCancel(CoreWebView2DownloadStartingEventArgs args)
        {
            try { args.Cancel = true; }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            { ErrorPresenter.LogTechnicalDetail("取消已关闭的素材下载请求：" + ex); }
        }

        private void TrackDownload(CoreWebView2DownloadOperation operation)
        {
            EventHandler<object>? handler = null;
            handler = (_, _) =>
            {
                if (_disposed) return;
                try
                {
                    var state = operation.State;
                    if (state == CoreWebView2DownloadState.InProgress) return;
                    ForgetDownload(operation, handler!);
                    if (state == CoreWebView2DownloadState.Completed)
                        _downloadCompleted?.Invoke(operation.ResultFilePath);
                    else
                    {
                        var reason = operation.InterruptReason;
                        // This host provides no resume UI. Stop the interrupted operation so it
                        // cannot silently resume after reporting a failure and dropping tracking.
                        operation.Cancel();
                        _downloadFailed?.Invoke("下载未完成：" + reason + "。请返回素材页面重试导出。");
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or COMException)
                {
                    ForgetDownload(operation, handler!);
                    if (!_disposed) _downloadFailed?.Invoke("无法读取素材下载状态：" + ex.Message + "。请重试导出。");
                }
            };
            _downloads[operation] = handler;
            operation.StateChanged += handler;
            handler(operation, EventArgs.Empty);
        }

        private void ForgetDownload(CoreWebView2DownloadOperation operation, EventHandler<object> handler)
        {
            _downloads.Remove(operation);
            try { operation.StateChanged -= handler; }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            { ErrorPresenter.LogTechnicalDetail("解除素材下载监听：" + ex); }
        }

        public void Navigate(string url)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _latestNavigationId = null;
            _view.CoreWebView2.Navigate(url);
        }

        private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (_disposed) return;
            _latestNavigationId = e.NavigationId;
            _navigationStarting?.Invoke(e.Uri);
        }

        private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            // 被后一次导航替换的完成事件不能覆盖当前页的成功/失败状态。
            if (!_disposed && _latestNavigationId == e.NavigationId)
                _navigationCompleted(e.IsSuccess, e.WebErrorStatus.ToString());
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_subscribed && _view.CoreWebView2 != null)
                {
                    _view.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                    _view.CoreWebView2.NavigationStarting -= OnNavigationStarting;
                    if (_downloadCompleted != null) _view.CoreWebView2.DownloadStarting -= OnDownloadStarting;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            { ErrorPresenter.LogTechnicalDetail("关闭素材浏览器事件：" + ex); }
            finally
            {
                foreach (var download in _downloads.ToArray())
                {
                    ForgetDownload(download.Key, download.Value);
                    try
                    {
                        if (download.Key.State == CoreWebView2DownloadState.InProgress) download.Key.Cancel();
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or COMException)
                    { ErrorPresenter.LogTechnicalDetail("关闭素材下载：" + ex); }
                }
                _downloads.Clear();
                _subscribed = false;
                _view.Dispose();
            }
        }
    }
}
