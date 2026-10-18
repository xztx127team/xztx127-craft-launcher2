using System.Diagnostics;
using Microsoft.Win32;
using XCL2.App.Models;
using XCL2.App.Views;

namespace XCL2.App.Services;

public static class OfflineLaunchReminderService
{
    private const string RegistryPath = @"SOFTWARE\XCL2";
    private const string SuppressValueName = "jzlzde";
    private const string DonateUrl = "https://ifdian.net/a/xztx127";
    private const string BuyUrl = "https://www.xbox.com/zh-CN/games/store/minecraft-java-bedrock-edition-for-pc/9NXP44L49SHJ";

    public static void OnOfflineLaunch(AppConfig config)
    {
        if (IsSuppressed() || config.DonationAcknowledged) return;
        config.OfflineLaunchCount++;
        var count = config.OfflineLaunchCount;

        if (count >= 25 && (count == 25 || (count - 25) % 50 == 0))
        {
            ReminderCenterService.Publish("offline-license", "离线账户提示",
                "若已拥有正版可忽略；正版账户可使用官方联机和账户服务。",
                "查看购买页面", () => { OpenUrl(BuyUrl); return Task.CompletedTask; });
            return;
        }

        if (count > 25 && (count - 25) % 10 == 0)
        {
            ReminderCenterService.Publish("support-project", "支持 XCL2",
                "项目持续开源维护，可以在方便时查看支持页面，也可以忽略此建议。",
                "查看支持页面", () =>
                {
                    config.DonationAcknowledged = true;
                    (System.Windows.Application.Current?.MainWindow as MainWindow)?.ConfigService.Save();
                    OpenUrl(DonateUrl); return Task.CompletedTask;
                });
        }
    }

    private static bool IsSuppressed()
    {
        try
        {
            using var machine = Registry.LocalMachine.OpenSubKey(RegistryPath, false);
            using var user = Registry.CurrentUser.OpenSubKey(RegistryPath, false);
            return Convert.ToInt32(machine?.GetValue(SuppressValueName, user?.GetValue(SuppressValueName, 0)) ?? 0) == 1;
        }
        catch { return false; }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }
}
