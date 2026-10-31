using System.Security.Principal;
using Microsoft.Win32;

namespace XCL2.App.Services;

public static class RegistryConfigService
{
    private const string SubKeyPath = @"SOFTWARE\XCL2";

    /// <summary>当前进程是否以管理员身份运行。</summary>
    public static bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            // 极端环境（比如非 Windows 沙箱、身份查询被拒绝）下保守认为不是管理员，
            // 不让异常向上传播打断整个配置读取流程。
            return false;
        }
    }

    /// <summary>
    /// 读取一个字符串值。按"先 HKLM 后 HKCU"的顺序查找，都没有则返回 <paramref name="defaultValue"/>。
    /// 同时通过 <paramref name="foundInHive"/> 告知调用方该值实际来自哪一支（或者两边都没找到）——
    /// ConfigService 用不到该信息也可以忽略，主要给"导出注册表"之类需要知道来源的功能使用。
    /// </summary>
    public static string? GetString(string name, string? defaultValue, out RegistryHive? foundInHive)
    {
        if (TryGetValue(Registry.LocalMachine, name, out var hklmValue))
        {
            foundInHive = RegistryHive.LocalMachine;
            return hklmValue as string ?? defaultValue;
        }
        if (TryGetValue(Registry.CurrentUser, name, out var hkcuValue))
        {
            foundInHive = RegistryHive.CurrentUser;
            return hkcuValue as string ?? defaultValue;
        }
        foundInHive = null;
        return defaultValue;
    }

    /// <summary>读取一个整型值（内部用 DWORD 存储），规则同 <see cref="GetString"/>。</summary>
    public static int GetInt(string name, int defaultValue)
    {
        if (TryGetValue(Registry.LocalMachine, name, out var hklmValue) && hklmValue is int hklmInt)
            return hklmInt;
        if (TryGetValue(Registry.CurrentUser, name, out var hkcuValue) && hkcuValue is int hkcuInt)
            return hkcuInt;
        return defaultValue;
    }

    /// <summary>读取一个布尔值（内部用 DWORD 0/1 存储），规则同 <see cref="GetString"/>。</summary>
    public static bool GetBool(string name, bool defaultValue) =>
        GetInt(name, defaultValue ? 1 : 0) != 0;

    private static bool TryGetValue(RegistryKey root, string name, out object? value)
    {
        value = null;
        try
        {
            using var key = root.OpenSubKey(SubKeyPath, writable: false);
            if (key == null) return false;
            value = key.GetValue(name, null);
            return value != null;
        }
        catch
        {
            // 键存在但没有访问权限、或者其它注册表异常：视为"查不到"，让调用方退到下一支/默认值。
            return false;
        }
    }

    /// <summary>
    /// 写入一个字符串值。<paramref name="useLocalMachine"/> 为 true 且当前进程确实是管理员时
    /// 写 HKLM，否则一律写 HKCU（即使用户开着"使用管理员"开关但这次没有真正提权运行，
    /// 也不会假装能写 HKLM——避免抛出无权限异常中断整个保存流程，静默退化到写 HKCU，
    /// 保证"至少有一份写成功"）。
    /// </summary>
    public static bool SetString(string name, string value, bool useLocalMachine)
    {
        var root = (useLocalMachine && IsRunningAsAdministrator()) ? Registry.LocalMachine : Registry.CurrentUser;
        return TrySetValue(root, name, value, RegistryValueKind.String);
    }

    public static bool SetInt(string name, int value, bool useLocalMachine)
    {
        var root = (useLocalMachine && IsRunningAsAdministrator()) ? Registry.LocalMachine : Registry.CurrentUser;
        return TrySetValue(root, name, value, RegistryValueKind.DWord);
    }

    public static bool SetBool(string name, bool value, bool useLocalMachine) =>
        SetInt(name, value ? 1 : 0, useLocalMachine);

    /// <summary>
    /// 把 AppData 主配置文件路径写到 HKCU\SOFTWARE\XCL2 的 32 位注册表视图，值名固定 appd。
    /// “路径”本质是字符串，因此值类型是 REG_SZ；这里的“32 位”指 RegistryView.Registry32，
    /// 而不是 REG_DWORD（DWORD 无法保存一个文件路径）。
    /// </summary>
    public static bool SetAppDataConfigPath(string configPath)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.CurrentUser, RegistryView.Registry32);
            using var key = root.CreateSubKey(SubKeyPath, writable: true);
            if (key == null) return false;
            key.SetValue("appd", configPath, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback("写入 32 位注册表 appd 配置路径失败", ex);
            return false;
        }
    }

    private static bool TrySetValue(RegistryKey root, string name, object value, RegistryValueKind kind)
    {
        try
        {
            using var key = root.CreateSubKey(SubKeyPath, writable: true);
            if (key == null) return false;
            key.SetValue(name, value, kind);
            return true;
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback($"写入注册表失败：{root.Name}\\{SubKeyPath}\\{name}", ex);
            return false;
        }
    }

    /// <summary>
    /// 判断 XCL2 的注册表键在 HKLM 和/或 HKCU 下是否存在（用于设置页展示"注册表功能"当前状态、
    /// 以及"删除所有新增的启动器注册表项"前先确认有没有东西可删）。
    /// </summary>
    public static (bool ExistsInHklm, bool ExistsInHkcu) CheckExistence()
    {
        bool hklm = false, hkcu = false;
        try { using var k = Registry.LocalMachine.OpenSubKey(SubKeyPath, false); hklm = k != null; } catch { /* 无权限视为不存在（查不到就是查不到） */ }
        try { using var k = Registry.CurrentUser.OpenSubKey(SubKeyPath, false); hkcu = k != null; } catch { /* 同上 */ }
        return (hklm, hkcu);
    }

    public static (bool HklmDeleted, bool HkcuDeleted) DeleteXcl2Key()
    {
        bool hklmDeleted = false, hkcuDeleted = false;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(SubKeyPath, false);
            if (k != null)
            {
                Registry.LocalMachine.DeleteSubKeyTree(SubKeyPath, throwOnMissingSubKey: false);
                hklmDeleted = true;
            }
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback("删除 HKLM\\SOFTWARE\\XCL2 失败（可能缺少管理员权限）", ex);
        }

        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(SubKeyPath, false);
            if (k != null)
            {
                Registry.CurrentUser.DeleteSubKeyTree(SubKeyPath, throwOnMissingSubKey: false);
                hkcuDeleted = true;
            }
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback("删除 HKCU\\SOFTWARE\\XCL2 失败", ex);
        }

        return (hklmDeleted, hkcuDeleted);
    }

    /// <summary>
    /// 导出 XCL2 注册表键为标准 .reg 文件内容（不管键实际在 HKLM 还是 HKCU，两边只要存在
    /// 都各自导出成一段，都不存在则返回 null 交由调用方提示"当前没有可导出的注册表内容"）。
    /// 手写标准 .reg 文本格式而不是调用外部 reg.exe（避免额外进程依赖/编码坑），
    /// 格式对齐 Windows 注册表编辑器"导出"生成的标准文件头 <c>Windows Registry Editor Version 5.00</c>。
    /// </summary>
    public static string? ExportToRegFileContent()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Windows Registry Editor Version 5.00");
        sb.AppendLine();

        var any = false;
        any |= AppendHiveSection(sb, Registry.LocalMachine, "HKEY_LOCAL_MACHINE");
        any |= AppendHiveSection(sb, Registry.CurrentUser, "HKEY_CURRENT_USER");

        return any ? sb.ToString() : null;
    }

    private static bool AppendHiveSection(System.Text.StringBuilder sb, RegistryKey root, string hiveName)
    {
        try
        {
            using var key = root.OpenSubKey(SubKeyPath, false);
            if (key == null) return false;

            sb.AppendLine($"[{hiveName}\\{SubKeyPath}]");
            foreach (var valueName in key.GetValueNames())
            {
                var kind = key.GetValueKind(valueName);
                var value = key.GetValue(valueName);
                var escapedName = valueName.Replace("\\", "\\\\").Replace("\"", "\\\"");
                switch (kind)
                {
                    case RegistryValueKind.DWord:
                        sb.AppendLine($"\"{escapedName}\"=dword:{Convert.ToInt32(value):x8}");
                        break;
                    case RegistryValueKind.String:
                    default:
                        var escapedValue = (value?.ToString() ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
                        sb.AppendLine($"\"{escapedName}\"=\"{escapedValue}\"");
                        break;
                }
            }
            sb.AppendLine();
            return true;
        }
        catch (Exception ex)
        {
            ErrorPresenter.LogFallback($"导出注册表分支失败：{hiveName}\\{SubKeyPath}", ex);
            return false;
        }
    }
}

/// <summary>标识一个值实际来自哪一个注册表分支。</summary>
public enum RegistryHive
{
    LocalMachine,
    CurrentUser
}
