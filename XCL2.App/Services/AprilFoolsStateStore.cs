using System.IO;
using System.Text.Json;

namespace XCL2.App.Services;

/// <summary>用户目录中的彩蛋状态；兼容旧程序目录，只在新路径写入。</summary>
public sealed class AprilFoolsStateStore(string directory, string legacyDirectory)
{
    public string DirectoryPath { get; } = directory;
    public string StatePath => Path.Combine(DirectoryPath, "state.json");
    public bool TryLoad<T>(out T? state, out string? warning)
    {
        state = default; warning = null;
        foreach (var path in new[] { StatePath, Path.Combine(legacyDirectory, "state.json") }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            try
            {
                state = JsonSerializer.Deserialize<T>(File.ReadAllText(path));
                if (state is null) continue;
                if (!string.Equals(path, StatePath, StringComparison.OrdinalIgnoreCase) && !TrySave(state, out var migrationWarning))
                    warning = migrationWarning;
                return true;
            }
            catch (Exception ex) { warning = "无法读取彩蛋退出状态（" + ex.GetType().Name + "）。本次可继续按F1恢复。"; }
        }
        return false;
    }
    public bool TrySave<T>(T state, out string? warning)
    {
        warning = null;
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            temporary = StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, StatePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            warning = "彩蛋退出状态未能保存（" + ex.GetType().Name + "）。本次已恢复，重启后可能再次出现；可在设置中关闭愚人节彩蛋总开关。";
            return false;
        }
        finally { if (temporary is not null) { try { File.Delete(temporary); } catch { } } }
    }
}
