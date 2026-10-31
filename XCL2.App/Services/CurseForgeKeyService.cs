using System.IO;

namespace XCL2.App.Services;

public class CurseForgeKeyService
{
    public string KeyFilePath { get; }

    /// <summary>
    /// 内置 key（测试阶段临时写死，正式发布前必须替换为从加密仓库/构建流程注入，
    /// 不要把该值带入面向公众发布的版本）。
    /// </summary>
    private const string BuiltInKey = "$2a$10$AELa495vU.ZJ6rstJOuSb.gMHCd0R/USkAkXgdfT.AbuA45Od63xm";

    public CurseForgeKeyService()
    {
        KeyFilePath = Path.Combine(App.DataDir, "curseforge_key.txt");
    }

    /// <summary>内置 key 的读取入口，按需替换实现（解密/环境变量等）。</summary>
    private static string? TryGetBuiltInKey()
    {
        return string.IsNullOrWhiteSpace(BuiltInKey) ? null : BuiltInKey.Trim();
    }

    /// <summary>
    /// 读取生效的 key：优先使用用户本地保存的 key（用户自己配置过，视为更明确的意图），
    /// 否则回退到内置 key；都没有则返回 null（不抛异常）。
    /// </summary>
    public string? TryGetKey()
    {
        try
        {
            if (File.Exists(KeyFilePath))
            {
                var content = File.ReadAllText(KeyFilePath).Trim();
                if (!string.IsNullOrEmpty(content)) return content;
            }
        }
        catch
        {
            // 忽略本地文件读取失败，继续尝试内置 key
        }

        return TryGetBuiltInKey();
    }

    /// <summary>是否已配置 key（用于 UI 判断要不要显示"未配置"提示）。</summary>
    public bool HasKey() => TryGetKey() != null;

    /// <summary>把 key 写入本地文件（用户在设置页粘贴 key 后调用）。</summary>
    public void SaveKey(string key)
    {
        Directory.CreateDirectory(App.DataDir);
        File.WriteAllText(KeyFilePath, key.Trim());
    }

    /// <summary>清空已保存的 key（用户想撤销时）。</summary>
    public void ClearKey()
    {
        try
        {
            if (File.Exists(KeyFilePath)) File.Delete(KeyFilePath);
        }
        catch { /* 忽略删除失败 */ }
    }
}
