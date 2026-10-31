using System.IO;
using XCL2.App.Models;

namespace XCL2.App.Services;

public class GuestModeService
{
    /// <summary>本次进程启动的时间，用于区分"这次会话新产生的日志"和"之前就存在的日志"。</summary>
    private readonly DateTime _sessionStartUtc = DateTime.UtcNow;

    /// <summary>生成一个仅本次会话有效的临时离线账户，用户名固定为"访客"（不与真实账户混淆），
    /// 每次调用都会生成一个新的随机后缀，避免多次开关访客模式时 UUID 冲突导致的皮肤/存档误关联。</summary>
    public Account CreateGuestAccount()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var username = $"访客{suffix}";
        var uuid = OfflineAuthService.GenerateOfflineUuid(username);
        return new Account
        {
            Type = AccountType.Offline,
            Username = username,
            Uuid = uuid,
            IsSelected = true,
            IsGuest = true
        };
    }

    /// <summary>
    /// 应用退出前调用：清理本次会话产生的日志/临时下载文件。所有清理动作都用 try/catch 单独
    /// 包裹，某个文件清理失败(比如仍被占用)不应该阻止应用正常退出或影响其余文件的清理。
    /// </summary>
    public void CleanupSessionArtifacts()
    {
        CleanupNewLogFiles();
        CleanupTempDownloads();
    }

    private void CleanupNewLogFiles()
    {
        var logsDir = Path.Combine(App.DataDir, "logs");
        if (!Directory.Exists(logsDir)) return;

        foreach (var file in Directory.EnumerateFiles(logsDir, "*", SearchOption.AllDirectories))
        {
            try
            {
                // 只清理本次会话开始之后才被创建或修改过的文件，不动会话开始前就已经存在的
                // 历史日志（那些不是"这次"产生的痕迹，用户可能还需要留着排查之前的问题）。
                var lastWrite = File.GetLastWriteTimeUtc(file);
                var created = File.GetCreationTimeUtc(file);
                if (lastWrite >= _sessionStartUtc || created >= _sessionStartUtc)
                    File.Delete(file);
            }
            catch { /* 单个文件清理失败(可能仍被占用)不影响其余文件，也不阻止应用退出 */ }
        }
    }

    private void CleanupTempDownloads()
    {
        var tempDir = Path.Combine(App.DataDir, "downloads-temp");
        if (!Directory.Exists(tempDir)) return;

        try { Directory.Delete(tempDir, recursive: true); }
        catch { /* 清理失败不阻止应用退出，残留的临时文件下次启动时会被覆盖/复用 */ }
    }
}
