using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using Windows.Management.Deployment;

namespace XCL2.App.Services;

public static class BedrockPackageRegistrationHelper
{
    /// <summary>正式版 PackageFamilyName，跟 <see cref="BedrockLaunchService"/> 保持一致。</summary>
    public const string PackageFamilyName = "Microsoft.MinecraftUWP_8wekyb3d8bbwe";

    /// <summary>
    /// 检测开发者模式是否已开启（读注册表，跟 BedrockBoot 的 DeveloperModeHelper 同一个
    /// 判断依据：HKLM 下 AppModelUnlock 或组策略 Appx 项的 AllowDevelopmentWithoutDevLicense）。
    /// 侧载未走 Store 签发的包，部分系统/包签名组合下需要该开关，提前检测能给用户
    /// 明确的修复指引，而不是让注册失败后只看到一串英文异常堆栈。
    /// </summary>
    public static bool IsDeveloperModeEnabled()
    {
        if (!BedrockLaunchService.IsOsSupported) return false;
        // 直接用注册表 API 读取，不再启动 powershell.exe（脚本调用容易被安全软件启发式拦截）。
        // 同时检查 64 位视图，避免 32 位进程被重定向到 WOW6432Node 读不到值。
        const string valueName = "AllowDevelopmentWithoutDevLicense";
        string[] subKeys =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock",
            @"SOFTWARE\Policies\Microsoft\Windows\Appx",
        };
        try
        {
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            foreach (var subKey in subKeys)
            {
                try
                {
                    using var key = baseKey.OpenSubKey(subKey, writable: false);
                    var value = key?.GetValue(valueName);
                    if (value is int i && i == 1) return true;
                    if (value is long l && l == 1) return true;
                }
                catch { /* 单个键读不了就看下一个 */ }
            }
        }
        catch { }
        return false;
    }

    /// <summary>打开"系统 - 开发者选项"设置页，引导用户手动开启。</summary>
    public static void OpenDeveloperModeSettings()
    {
        EnsureSupported();
        Process.Start(new ProcessStartInfo("ms-settings:developers") { UseShellExecute = true });
    }

    /// <summary>
    /// 在解压出来的目录里找 AppxManifest.xml。注册需要该文件——它是包的身份清单，
    /// 单纯有 Minecraft.Windows.exe 不代表这是一个能注册的合法包（比如手动裁剪过的
    /// 解压产物就没有它）。
    /// </summary>
    public static string? FindAppxManifest(string extractedDir)
    {
        if (!Directory.Exists(extractedDir)) return null;
        return Directory.GetFiles(extractedDir, "AppxManifest.xml", SearchOption.AllDirectories)
            .FirstOrDefault();
    }

    /// <summary>当前系统是否已经注册了基岩版正式版该包（不区分注册来源，Store 装的
    /// 和本方法注册的都算）。</summary>
    public static bool IsRegistered()
    {
        if (!BedrockLaunchService.IsOsSupported) return false;
        try { return IsRegisteredNative(); }
        catch
        {
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsRegisteredNative()
    {
        var pm = new PackageManager();
        return pm.FindPackagesForUser(string.Empty, PackageFamilyName).Any();
    }

    /// <summary>
    /// 把解压好的包目录注册进系统（PackageManager.AddPackageAsync）。
    /// 注册成功后应该用 <see cref="BedrockLaunchService.Launch"/>（shell:AppsFolder）
    /// 去启动，而不是直接 Process.Start 里面的 exe——注册过的包需要通过系统的应用
    /// 激活路径启动，才能拿到完整的包身份（存档路径、Xbox Live 关联等都依赖该）。
    /// </summary>
    /// <param name="extractedDir">DownloadClientAsync 返回的解压目录。</param>
    /// <param name="progress">注册进度回调（百分比 0-100 + 当前阶段文字）。</param>
    public static Task RegisterAsync(string extractedDir, IProgress<(int Percent, string State)>? progress = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        EnsureSupported();
        return RegisterNativeAsync(extractedDir, progress, ct);
    }

    // WinRT types live only in this leaf state machine. The old-system guard above must run
    // before the runtime tries to resolve PackageManager or deployment interfaces.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RegisterNativeAsync(string extractedDir, IProgress<(int Percent, string State)>? progress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var manifestPath = FindAppxManifest(extractedDir);
        if (manifestPath == null)
            throw new InvalidOperationException(
                $"在 {extractedDir} 下没有找到 AppxManifest.xml，该解压产物不是一个合法的应用包，" +
                "无法注册。可能是下载源给的文件不完整，建议删除后重新下载。");

        var manifestUri = new Uri(manifestPath);
        var pm = new PackageManager();

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deployment = pm.RegisterPackageAsync(manifestUri, null,
            DeploymentOptions.DevelopmentMode);

        deployment.Progress = (_, p) =>
        {
            progress?.Report(((int)p.percentage, p.state.ToString()));
        };
        deployment.Completed = (result, status) =>
        {
            try
            {
                if (status == Windows.Foundation.AsyncStatus.Canceled)
                {
                    tcs.TrySetCanceled(ct.IsCancellationRequested ? ct : new CancellationToken(true));
                    return;
                }
                // A failed WinRT operation may throw from GetResults itself. Complete the
                // Task in either case instead of letting the COM callback strand the await.
                var details = result.GetResults();
                if (status == Windows.Foundation.AsyncStatus.Error)
                    tcs.TrySetException(new InvalidOperationException(
                        $"注册基岩版客户端包失败：{details.ErrorText}"));
                else
                    tcs.TrySetResult();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(new InvalidOperationException("注册基岩版客户端包失败：" + ex.Message, ex));
            }
        };

        using var cancellation = ct.Register(() => { try { deployment.Cancel(); } catch { } });
        await tcs.Task;
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// 一站式：检测开发者模式 → 未注册则注册 → 通过系统应用激活路径启动。
    /// 调用方（BedrockPage/BedrockClientDownloadService）应该用该方法替代
    /// "直接 Process.Start 解压出来的 exe"这条旧路径。
    /// </summary>
    public static async Task RegisterAndLaunchAsync(string extractedDir,
        IProgress<(int Percent, string State)>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        EnsureSupported();
        if (!IsDeveloperModeEnabled())
            throw new InvalidOperationException(
                "系统未开启开发者模式，侧载（非 Microsoft Store 安装）方式注册应用包可能会被拒绝。\n" +
                "请前往「设置 → 系统 → 开发者选项」开启「开发人员模式」后重试。");

        if (!IsRegistered())
        {
            progress?.Report((0, "正在注册应用包"));
            await RegisterAsync(extractedDir, progress, ct);
        }

        ct.ThrowIfCancellationRequested();
        progress?.Report((100, "启动游戏"));
        BedrockLaunchService.Launch();
    }

    private static void EnsureSupported()
    {
        if (!BedrockLaunchService.IsOsSupported)
            throw new PlatformNotSupportedException(BedrockLaunchService.UnsupportedOsMessage);
    }
}
