using System.Globalization;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>只改变展示文本，连接地址、文件长度和时间原值不变。</summary>
public static class PresentationPreferencesService
{
    public static event Action? Changed;
    public static void NotifyChanged() => Changed?.Invoke();

    public static string FormatServerAddress(string? address, AppConfig? config = null)
        => (config ?? ConfigService.Active?.Config)?.HideServerAddresses == true
            ? "地址已隐藏" : address ?? "";

    public static string FormatDateTime(DateTimeOffset value, AppConfig? config = null)
    {
        var preference = (config ?? ConfigService.Active?.Config)?.PreferredTimeFormat ?? LauncherTimeFormat.System;
        var local = value.ToLocalTime();
        return preference switch
        {
            LauncherTimeFormat.TwelveHour => local.ToString("d", CultureInfo.CurrentCulture) + " " + local.ToString("h:mm tt", CultureInfo.CurrentCulture),
            LauncherTimeFormat.TwentyFourHour => local.ToString("d", CultureInfo.CurrentCulture) + " " + local.ToString("HH:mm", CultureInfo.CurrentCulture),
            _ => local.ToString("g", CultureInfo.CurrentCulture)
        };
    }

    public static string FormatDateTime(string? value, AppConfig? config = null)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
            ? FormatDateTime(parsed, config) : value ?? "";

    public static string FormatFullDateTime(DateTimeOffset value, AppConfig? config = null)
    {
        config ??= ConfigService.Active?.Config;
        if (config?.Ux50FullTimeTooltips == false) return FormatDateTime(value, config);
        var local = value.ToLocalTime();
        return local.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.CurrentCulture)
            + "（" + TimeZoneInfo.Local.Id + "）";
    }

    /// <summary>Formatting only; never normalize tokens, paths, file names or stored values.</summary>
    public static string FormatNumberWithUnit(double value, string unit, string format = "0.##", AppConfig? config = null)
    {
        config ??= ConfigService.Active?.Config;
        // Existing capacity display already used a space; keep that shape when disabled too.
        var gap = config?.Ux50UniformNumberSpacing == false ? " " : "\u00A0";
        return value.ToString(format, CultureInfo.CurrentCulture) + gap + unit;
    }

    public static string FormatCapacity(long bytes, AppConfig? config = null)
    {
        config ??= ConfigService.Active?.Config;
        var amount = Math.Max(0, bytes);
        var unit = config?.PreferredCapacityUnit ?? LauncherCapacityUnit.Automatic;
        var megabytes = amount / 1048576d;
        var gigabytes = unit == LauncherCapacityUnit.GB || (unit == LauncherCapacityUnit.Automatic && megabytes >= 1024);
        var value = gigabytes ? megabytes / 1024 : megabytes;
        var number = value is > 0 and < 0.01
            ? "< " + 0.01.ToString("0.00", CultureInfo.CurrentCulture)
            : value.ToString("0.##", CultureInfo.CurrentCulture);
        var gap = config?.Ux50UniformNumberSpacing == false ? " " : "\u00A0";
        var result = number + gap + (gigabytes ? "GB" : "MB");
        return config?.ShowExactCapacityValues == true
            ? result + "（" + amount.ToString("N0", CultureInfo.CurrentCulture) + gap + "字节）" : result;
    }
}
