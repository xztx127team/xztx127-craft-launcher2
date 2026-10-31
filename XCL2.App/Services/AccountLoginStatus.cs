using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>仅解释本地缓存，绝不在浏览账户或摘要时联网、刷新令牌或打开登录窗口。</summary>
public static class AccountLoginStatus
{
    public static string Describe(Account? account, DateTime utcNow)
    {
        if (account == null) return "尚未选择账户";
        if (account.IsGuest) return "访客账户，仅本次会话有效";
        if (account.Type == AccountType.Offline) return "离线账户，无登录有效期";
        if (account.Type == AccountType.AuthServer)
            return string.IsNullOrWhiteSpace(account.MinecraftAccessToken)
                ? "缺少认证服务器登录凭据，请重新登录"
                : "认证服务器账户，本地无法判断有效期；登录有效性由认证服务器决定";
        if (string.IsNullOrWhiteSpace(account.MinecraftAccessToken))
            return string.IsNullOrWhiteSpace(account.MsRefreshToken)
                ? "缺少登录凭据，请到账户页重新登录"
                : "尚无可用登录令牌，启动时将尝试刷新";
        if (account.AccessTokenExpiresAtUtc is not DateTime expires)
            return string.IsNullOrWhiteSpace(account.MsRefreshToken)
                ? "本地有效期未知，启动时将检查登录状态"
                : "本地有效期未知，启动时将尝试刷新";
        if (expires <= utcNow)
            return string.IsNullOrWhiteSpace(account.MsRefreshToken)
                ? "本地登录已过期，需要重新登录"
                : "本地登录已过期，启动时将尝试刷新";
        if (expires <= utcNow.AddMinutes(5))
            return string.IsNullOrWhiteSpace(account.MsRefreshToken)
                ? "本地登录即将到期，过期后需要重新登录"
                : "本地登录即将到期，启动时将尝试刷新";
        return "本地登录尚在有效期内（服务器仍可能撤销授权）";
    }
}
