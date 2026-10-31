using System.IO;
using XCL2.App.Models;

namespace XCL2.App.Services;

public static class AccountAvatarCacheService
{
    public static string CacheDir => Path.Combine(App.DataDir, "skins", "onlineAvatarCache");

    public static string GetCachePath(Account account) => Path.Combine(CacheDir, account.Id + ".png");

    /// <summary>是否属于"应该去 API 查一次当前皮肤"的范围。</summary>
    public static bool ShouldFetch(Account account) => account.Type switch
    {
        AccountType.Microsoft => true,
        AccountType.AuthServer => !string.IsNullOrWhiteSpace(account.AuthServerApiRoot) && !string.IsNullOrWhiteSpace(account.Uuid),
        // A matching display name is not proof of the same identity. Never replace a local
        // Custom/Alex skin with the unrelated official player's online skin.
        AccountType.Offline => false,
        _ => false
    };

    /// <summary>
    /// 后台发起一次皮肤查询 + 下载；成功则把 PNG 写入缓存文件并调用 onUpdated（调用方负责
    /// 切回 UI 线程刷新头像绑定）。查询失败、下载失败、账户在服务器上查不到正版档案等任何
    /// 一步出错，都原样吞掉异常、不抛出、不提示——如果解析/下载到的皮肤文件本身有问题
    /// （尺寸不对/损坏），交给 AccountAvatarConverter 已有的"皮肤文件读取失败退回默认头像"
    /// 兜底逻辑处理，这里不重复做皮肤合法性校验（同一套裁剪/合成逻辑见
    /// <see cref="SkinAvatarRenderService"/>，「百宝箱」-「皮肤头像生成器」用的也是它）。
    /// </summary>
    public static async Task RefreshAsync(Account account, Action onUpdated)
    {
        if (!ShouldFetch(account)) return;

        try
        {
            using var service = new OfficialSkinFetchService();
            OfficialSkinFetchService.OfficialSkinInfo info = account.Type switch
            {
                AccountType.Microsoft when !string.IsNullOrWhiteSpace(account.Uuid)
                    => await service.LookupByUuidAsync(account.Uuid, account.Username),
                AccountType.Microsoft
                    => await service.LookupAsync(account.Username),
                AccountType.AuthServer
                    => await service.LookupByUuidFromYggdrasilAsync(account.AuthServerApiRoot!, account.Uuid, account.Username),
                _ => await service.LookupAsync(account.Username)
            };

            var bytes = await service.DownloadSkinBytesAsync(info);
            if (bytes.Length == 0) return;

            Directory.CreateDirectory(CacheDir);
            await File.WriteAllBytesAsync(GetCachePath(account), bytes);
            onUpdated();
        }
        catch
        {
            // 静默失败：保持原有的 本地皮肤 / 默认头像 优先级链条不变，不打断账户列表显示。
        }
    }
}
