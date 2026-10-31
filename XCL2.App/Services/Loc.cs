using System.Windows;

namespace XCL2.App.Services;

public static class Loc
{
    /// <summary>
    /// 取本地化字符串。key 查不到时返回 fallback（一般传中文原文）。
    ///
    /// 注意这里用 Application.Current.TryFindResource 而不是 FindResource：
    /// 后者查不到会抛 ResourceReferenceKeyNotFoundException，
    /// 而"key 还没建"在分批翻译期间是**正常状态**，不该抛异常。
    /// </summary>
    public static string T(string key, string fallback)
    {
        try
        {
            if (Application.Current?.TryFindResource(key) is string s && !string.IsNullOrEmpty(s))
                return s;
        }
        catch
        {
            // 极早期（Application 还没建好）或资源字典异常时静默回退，
            // 绝不能因为取一句文案失败就把主流程带崩。
        }
        return fallback;
    }

    /// <summary>
    /// 带格式化参数的版本，对应原来的 $"..." 插值字符串。
    ///
    /// 插值字符串没法直接做本地化（不同语言的语序不同，占位符位置会变），
    /// 所以抽 key 时要把 $"游戏已启动：{name}" 改写成
    /// Loc.F("Str_X", "游戏已启动：{0}", name)，让译文自己决定 {0} 放哪。
    /// 格式化失败（译文里占位符数量对不上）时回退用 fallback 再格式化一次，
    /// 再失败就直接返回 fallback 原文——宁可显示未替换的模板，也不要抛异常。
    /// </summary>
    public static string F(string key, string fallbackFormat, params object?[] args)
    {
        var fmt = T(key, fallbackFormat);
        try
        {
            return string.Format(fmt, args);
        }
        catch (FormatException)
        {
            try { return string.Format(fallbackFormat, args); }
            catch { return fallbackFormat; }
        }
    }
}
