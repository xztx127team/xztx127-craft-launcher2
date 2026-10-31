using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using System.Runtime.CompilerServices;

namespace XCL2.App.Services;

public static class WebView2RuntimeDetector
{
    /// <summary>Use the existing system-browser authentication flow before creating SDK controls.</summary>
    public static bool PreferExternalBrowser => WindowsCompatibilityService.Current.IsLegacyWindows
        || WindowsCompatibilityService.Current.IsEnabled;

    public const string CompatibilityBrowserMessage =
        "当前系统或兼容模式改用系统浏览器登录，无需内嵌 WebView2。";

    /// <summary>官方 WebView2 Runtime 独立安装包下载地址（Evergreen Bootstrapper）。</summary>
    public const string DownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    /// <summary>
    /// 探测本机是否已安装可用的 WebView2 Runtime。
    /// 优先用官方 SDK 提供的 <see cref="CoreWebView2Environment.GetAvailableBrowserVersionString"/>，
    /// 该调用本身很轻量(只读注册表/文件系统探测版本号，不会启动浏览器进程、不会创建窗口)，
    /// 所以可以放心在"点击内嵌登录"该时机同步调用，不会有明显卡顿。
    /// 万一将来 SDK 行为有变化导致该调用本身抛异常，兜底再手动查一次常见注册表路径，
    /// 尽量不要出现"运行时明明装了、却被误判为没装"的情况。
    /// </summary>
    public static bool IsAvailable()
    {
        try
        {
            var version = GetAvailableVersion();
            if (!string.IsNullOrEmpty(version)) return true;
        }
        catch
        {
            // 忽略：SDK 内部探测失败不代表运行时一定没装，继续走下面的注册表兜底检测。
        }

        return HasRegistryEvidence();
    }

    // 隔离 SDK 类型加载：Core.dll 缺失时，异常必须发生在 IsAvailable 的 try/catch 内。
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string GetAvailableVersion() => CoreWebView2Environment.GetAvailableBrowserVersionString();

    /// <summary>
    /// 兜底：直接查 WebView2 Runtime 安装时会写入的注册表位置（Evergreen 版安装器的标准落点）。
    /// 同时查 64 位和 32 位视图，以及 HKLM(系统级安装)和 HKCU(用户级安装，部分环境下企业策略
    /// 会限定只能装到用户目录)两个位置，尽量覆盖不同安装方式，减少误判"没装"的概率。
    /// </summary>
    private static bool HasRegistryEvidence()
    {
        // {F3017226-FE2A-4295-8BDF-00C3A9A7E4C5} 是 WebView2 Runtime 的固定 ClientState GUID，
        // 微软官方文档里给出的检测方式就是查该键下面是否有 pv (版本号) 值。
        const string clientStateSubKey = @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

        var roots = new (RegistryKey Hive, RegistryView View)[]
        {
            (Registry.LocalMachine, RegistryView.Registry64),
            (Registry.LocalMachine, RegistryView.Registry32),
            (Registry.CurrentUser, RegistryView.Registry64),
            (Registry.CurrentUser, RegistryView.Registry32),
        };

        foreach (var (hive, view) in roots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(
                    hive == Registry.LocalMachine ? Microsoft.Win32.RegistryHive.LocalMachine : Microsoft.Win32.RegistryHive.CurrentUser, view);
                using var key = baseKey.OpenSubKey(clientStateSubKey);
                var pv = key?.GetValue("pv") as string;
                if (!string.IsNullOrEmpty(pv) && pv != "0.0.0.0") return true;
            }
            catch
            {
                // 单个视图/权限问题导致的异常不影响继续检查其他位置。
            }
        }

        return false;
    }
}
