using System.Runtime.InteropServices;
using XCL2.App.Models;

namespace XCL2.App.Services;

public enum WindowsCompatibilityLayer { None, Standard, Full }

/// <summary>
/// Immutable capability snapshot. Disabling optional compatibility must never make an absent
/// operating-system API callable. This policy does not supply or replace the .NET runtime.
/// </summary>
public sealed record WindowsCompatibilityProfile(
    Version OsVersion, WindowsCompatibilityMode Mode, bool IsWindows,
    WindowsCompatibilityLayer Layer)
{
    public bool IsEnabled => Layer != WindowsCompatibilityLayer.None;
    public bool IsAutomatic => Mode == WindowsCompatibilityMode.Auto;
    public bool IsLegacyWindows => IsWindows && OsVersion < new Version(10, 0);
    public bool IsAtLeast(int major, int minor = 0, int build = 0)
        => IsWindows && OsVersion >= new Version(major, minor, build);

    // The launcher promises automatic light/dark tracking only from Windows 10 1809.
    public bool SupportsSystemTheme => IsAtLeast(10, 0, 17763);
    public bool SupportsNativeToasts => IsAtLeast(10) && !IsEnabled;
    public bool SupportsPerMonitorDpi => IsAtLeast(6, 3);
    public bool SupportsWinUi => IsAtLeast(10, 0, 17763) && !IsEnabled;
    public bool SupportsCpuRateControl => IsAtLeast(6, 2);
    public bool SupportsPointerInput => IsAtLeast(6, 2);
    public bool SupportsNativeDarkTitleBar => SupportsSystemTheme && !IsEnabled;
    public bool SupportsNativeCorners => IsAtLeast(10, 0, 22000) && !IsEnabled;
    public bool SupportsSystemBackdrop => IsAtLeast(10, 0, 22621) && !IsEnabled;
    public bool UseClassicFolderPicker => IsWindows && (IsEnabled || IsLegacyWindows);
    public string DisplayName => Layer switch
    {
        WindowsCompatibilityLayer.Full => "完整兼容层（Windows Vista / 7）",
        WindowsCompatibilityLayer.Standard => "普通兼容层",
        _ => "兼容模式已关闭"
    };
}

/// <summary>
/// OS policy shared by UI and actual API adapters. RtlGetVersion avoids the application
/// manifest-dependent GetVersion result. Capability gates still enforce each API minimum.
/// </summary>
public static class WindowsCompatibilityService
{
    private static readonly bool IsWindowsHost = OperatingSystem.IsWindows();
    private static readonly Version HostVersion = ReadWindowsVersion();
    private static WindowsCompatibilityProfile _current = Evaluate(HostVersion, WindowsCompatibilityMode.Auto, IsWindowsHost);
    public static WindowsCompatibilityProfile Current => Volatile.Read(ref _current);

    public static void Configure(WindowsCompatibilityMode mode)
        => Volatile.Write(ref _current, Evaluate(HostVersion, mode, IsWindowsHost));

    /// <summary>Pure policy entry point, also used by version-boundary regression tests.</summary>
    public static WindowsCompatibilityProfile Evaluate(Version version, WindowsCompatibilityMode mode, bool isWindows = true)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (!Enum.IsDefined(mode)) mode = WindowsCompatibilityMode.Auto;
        // Version(6, 3) has Build = -1; normalize so it means the start of that release.
        var normalized = new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
        var enabled = isWindows && (mode == WindowsCompatibilityMode.Enabled ||
            (mode == WindowsCompatibilityMode.Auto && normalized < new Version(10, 0)));
        var layer = !enabled ? WindowsCompatibilityLayer.None
            : normalized < new Version(6, 2) ? WindowsCompatibilityLayer.Full : WindowsCompatibilityLayer.Standard;
        return new(normalized, mode, isWindows, layer);
    }

    private static Version ReadWindowsVersion()
    {
        if (IsWindowsHost)
        {
            try
            {
                var info = new RtlOsVersionInfo { Size = (uint)Marshal.SizeOf<RtlOsVersionInfo>(), ServicePack = string.Empty };
                if (RtlGetVersion(ref info) == 0)
                    return new((int)info.Major, (int)info.Minor, (int)info.Build);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch (BadImageFormatException) { }
        }
        return Environment.OSVersion.Version;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RtlOsVersionInfo
    {
        public uint Size, Major, Minor, Build, Platform;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ServicePack;
    }

    [DllImport("ntdll.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RtlGetVersion(ref RtlOsVersionInfo version);
}
