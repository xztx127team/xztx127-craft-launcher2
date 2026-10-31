using System.Diagnostics;
using System.Windows;

namespace XCL2.App.Services;

public static class LocalizationService
{
    /// <summary>一种受支持的启动器界面语言：Code 是 BCP-47 风格的语言代码（同时也是
    /// Lang.&lt;Code&gt;.xaml 文件名的一部分），NativeName 是"用该语言自己书写"的显示名
    /// （语言选择弹窗按该展示，不做二次翻译——参照 Windows/主流系统的通行做法，
    /// 用户找自己母语时认的是母语拼写本身，不是当前界面语言翻译过的名字）。</summary>
    public sealed record LanguageOption(string Code, string NativeName);

    /// <summary>
    /// 受支持的语言列表，顺序即语言选择弹窗里的显示顺序。
    /// 新增一种语言时：① 在这里加一行，② 在 Resources/Lang/ 下新建 Lang.&lt;code&gt;.xaml
    /// 且 key 集合要跟 Lang.zh-Hans.xaml 完全一致（否则下面的调试期校验会报警告）。
    /// </summary>
    public static readonly LanguageOption[] SupportedLanguages =
    {
        new("zh-Hans", "简体中文"),
        new("zh-yy", "简体中文（音译）"),
        new("zh-microsoft", "简体中文（Microsoft）"),
        new("zh-Hant", "繁體中文"),
        new("yue-Hant", "粵語（繁體）"),
        new("bo-CN", "བོད་ཡིག"),
        new("zh-wy", "中文（文言）"),
        new("en-US", "English (United States)"),
        new("en-GB", "English (United Kingdom)"),
        new("de-DE", "Deutsch (Deutschland)"),
        new("fr-FR", "Français (France)"),
        new("it-IT", "Italiano (Italia)"),
        new("sv-SE", "Svenska (Sverige)"),
        new("ja-JP", "日本語（日本）"),
        new("ko-KR", "한국어(대한민국)"),
        // 新增一批欧洲/拉美/东南亚语言。排序沿用本列表既有的思路：中文族 → 英语族 →
        // 其余拉丁字母语言 → 西里尔字母 → CJK。这几种放在欧洲语言那一段的后面，
        // 不插到 ja/ko 前面，避免改动已有条目的相对位置（老用户按位置记忆的肌肉记忆会失效）。
        new("es-ES", "Español (España)"),
        new("es-419", "Español (Latinoamérica)"),
        new("pt-PT", "Português (Portugal)"),
        new("pt-BR", "Português (Brasil)"),
        new("pl-PL", "Polski"),
        new("tr-TR", "Türkçe"),
        // 本次新增第二批欧洲语言 + 印地语：跟上面 es/pt/pl/tr 一样是拉丁字母语言，紧跟着放在
        // 同一段里；印地语（天城文）单独放这一小段最后，不搀进拉丁字母那几个中间，
        // 理由跟 ar-SA 放最后一致——自成一套文字系统，且目前项目未对它做 FlowDirection/
        // 字体覆盖之外的排版适配（天城文是从左到右书写，不需要 RTL，但连字/组合字符渲染
        // 依赖系统字体，Windows 默认字体基本都带，一般不需要额外处理）。
        new("nl-NL", "Nederlands"),
        new("cs-CZ", "Čeština"),
        new("hu-HU", "Magyar"),
        new("ro-RO", "Română"),
        new("da-DK", "Dansk"),
        new("nb-NO", "Norsk bokmål"),
        new("fi-FI", "Suomi"),
        new("hi-IN", "हिन्दी"),
        new("id-ID", "Bahasa Indonesia"),
        new("ru-RU", "Русский"),

        new("uk-UA", "Українська"),
        new("vi-VN", "Tiếng Việt"),
        new("th-TH", "ไทย"),
        new("ar-SA", "العربية"),
    };

    public const string DefaultLanguageCode = "zh-Hans";

    /// <summary>
    /// 只有该语言代码的界面，「实验性功能」入口才会显示（见 HomePage/MainWindow 里
    /// 对 Str_Nav_Experimental 相关按钮显隐的判断）。原话："那个实验性功能仅对简体中文
    /// 开放，其他语言不翻译"——不是禁止其它语言用户使用该功能，只是这批功能本身还没有
    /// 多语言界面，贸然展示给不懂中文的用户体验反而更差，所以先隐藏而不是显示未翻译的中文。
    /// </summary>
    public const string ExperimentalFeaturesLanguageGate = "zh-Hans";

    /// <summary>当前生效的语言代码。默认简体中文；ApplyForCurrentState 会同步更新该值。</summary>
    public static string CurrentLanguageCode { get; private set; } = DefaultLanguageCode;

    /// <summary>语言切换完成后触发（在窗口刷新之后）。code-behind 里如果自己缓存过某个
    /// 字符串资源的值（没有用 DynamicResource 绑定，而是手动 GetString 取了一次），
    /// 应该订阅该事件、在回调里重新取值刷新自己缓存的那份。</summary>
    public static event Action? LanguageChanged;

    /// <summary>兜底语言：任何语言缺 key 时都用英文补（见 Apply 里的回退链）。\n    /// 选英文而不是中文，是为了让非中文用户在遇到未翻译条目时至少能读懂。</summary>
    private const string FallbackLanguageCode = "en-US";

    private const string DictPathPrefix = "/Resources/Lang/Lang.";
    private const string DictPathSuffix = ".xaml";

    /// <summary>
    /// 根据配置里持久化的语言代码应用界面语言。传入非法/不支持的代码时兜底为简体中文，
    /// 不让配置文件被手改坏了之后直接崩溃或者显示成一堆资源键找不到的占位符。
    /// </summary>
    public static void ApplyForCurrentState(string? persistedLanguageCode)
    {
        var code = persistedLanguageCode;
        if (code is null || Array.TrueForAll(SupportedLanguages, l => l.Code != code))
        {
            code = DefaultLanguageCode;
        }

        Apply(code);
    }

    private static void Apply(string code)
    {
        var dictUri = new Uri($"{DictPathPrefix}{code}{DictPathSuffix}", UriKind.Relative);
        ResourceDictionary newDict;
        try
        {
            newDict = new ResourceDictionary { Source = dictUri };
        }
        catch
        {
            // 万一某个语言的 xaml 文件丢失/损坏，兜底回简体中文，而不是让整个启动器崩溃在
            // 语言切换这一步——界面语言这种非核心功能不应该有能力让程序完全打不开。
            if (code == DefaultLanguageCode) throw; // 连默认语言都加载不了，那是真正的部署问题，不应该静默吞掉
            Apply(DefaultLanguageCode);
            return;
        }

        DebugValidateKeys(newDict, code);

        if (code != DefaultLanguageCode)
        {
            try
            {
                var mergedDict = new ResourceDictionary();

                void Overlay(string langCode)
                {
                    try
                    {
                        var d = new ResourceDictionary
                        {
                            Source = new Uri($"{DictPathPrefix}{langCode}{DictPathSuffix}", UriKind.Relative)
                        };
                        foreach (var key in d.Keys) mergedDict[key] = d[key];
                    }
                    catch { /* 某一层读不到就跳过，不影响其它层 */ }
                }

                // 回退链自下而上：英文打底 → （仅中文变体额外铺简体中文）→ 目标语言覆盖。
                //
                // 为什么用英文而不是中文打底：一个德国/日本/瑞典用户碰到还没翻译的条目，
                // 看到英文至少能猜出该按钮干什么；看到中文完全无从下手。
                // 繁体/粤语是例外——它们跟简体的重合度远高于英文，所以在英文之上
                // 再铺一层简体，未翻条目显示简体中文比显示英文更贴近这批用户的习惯。
                Overlay(FallbackLanguageCode);                                  // 1. 英文底
                if (code is "zh-Hant" or "yue-Hant" or "zh-microsoft" or "zh-wy" or "zh-yy") Overlay(DefaultLanguageCode); // 2. 中文变体额外铺简体
                foreach (var key in newDict.Keys) mergedDict[key] = newDict[key];  // 3. 目标语言

                newDict = mergedDict;
            }
            catch
            {
                // 整个回退链构建失败就用原样的目标语言字典，不影响主流程。
            }
        }

        var merged = Application.Current.Resources.MergedDictionaries;

        // 找到当前已经挂载的语言字典（通过给它一个约定的 x:Key 标记来识别，见下方
        // "__XCL2_LangDictMarker__" 用法）并整份替换掉；第一次调用时(启动时)集合里
        // 还没有语言字典，直接添加。
        var oldIndex = -1;
        for (var i = 0; i < merged.Count; i++)
        {
            if (merged[i].Contains(LangDictMarkerKey))
            {
                oldIndex = i;
                break;
            }
        }

        newDict[LangDictMarkerKey] = code; // 标记 + 顺便记录当前是哪个语言，供上面下次查找

        if (oldIndex >= 0) merged[oldIndex] = newDict;
        else merged.Add(newDict);

        CurrentLanguageCode = code;

        // 跟 ThemeService.RefreshOpenWindows 是完全同一份逻辑：DynamicResource 引用字符串
        // 的控件在样式 Seal 后同样需要"摘掉 Style 再装回去"才会重新查找资源，直接复用
        // ThemeService 已经写好、已经在生产环境验证过的这套刷新方法，不重复造轮子。
        ThemeService.RefreshOpenWindowsPublic();

        LanguageChanged?.Invoke();
    }

    private const string LangDictMarkerKey = "__XCL2_LangDictMarker__";

    /// <summary>
    /// 运行时按 key 查询当前语言的字符串，给 code-behind 里需要拼接文案（不是单纯 XAML
    /// 静态绑定）的场景用。找不到 key 时返回 key 本身（方便一眼看出漏翻译，而不是显示
    /// 空字符串让人以为文案本来就是空的）。
    /// </summary>
    public static string GetString(string key)
    {
        if (Application.Current?.Resources[key] is string s) return s;
        return key;
    }

    /// <summary>
    /// Debug 模式下的开发期校验：拿新加载的语言字典跟简体中文（基准/权威 key 集合）比对，
    /// 缺失的 key 打印到输出窗口，帮助在开发阶段就发现漏翻译，而不是等用户切换语言后
    /// 才发现某处显示成英文 fallback 或者资源键名本身。Release 构建不做该校验（避免
    /// 正式发布版本承担额外的启动开销），所以生产环境里静默 fallback 到 zh-Hans 兜底值，
    /// 不会抛异常影响用户使用。
    /// </summary>
    [Conditional("DEBUG")]
    private static void DebugValidateKeys(ResourceDictionary dict, string code)
    {
        if (code == DefaultLanguageCode) return; // 基准语言自己不用跟自己比

        ResourceDictionary baseline;
        try
        {
            baseline = new ResourceDictionary
            {
                Source = new Uri($"{DictPathPrefix}{DefaultLanguageCode}{DictPathSuffix}", UriKind.Relative)
            };
        }
        catch
        {
            return; // 基准语言都加载不出来，跳过这次校验（Apply 里对基准语言加载失败已经会直接抛出）
        }

        var missing = 0;
        foreach (var key in baseline.Keys)
        {
            if (key is string k && k != LangDictMarkerKey && !dict.Contains(k)) missing++;
        }

        if (missing > 0)
        {
            var total = baseline.Count - 1; // 减掉 LangDictMarkerKey
            Debug.WriteLine(
                $"[LocalizationService] 语言 '{code}' 覆盖 {total - missing}/{total} 条，" +
                $"缺 {missing} 条（运行时会自动回退到 {FallbackLanguageCode}）。");
        }
    }
}
