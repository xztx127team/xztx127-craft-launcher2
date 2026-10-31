using System.IO;

namespace XCL2.App.Services;

public sealed record MiniWorldSource(string Id, string Name, string Kind, string Description,
    string HomeUrl, string? DirectDownloadUrl = null, string? DownloadFileName = null, string? VersionIndexUrl = null);

public sealed record MiniWorldReadingQuestion(string Prompt, IReadOnlyList<string> Choices, int CorrectIndex);

/// <summary>来源与安装方式分别呈现，不把渠道下载页伪装成可直接下载的安装包。</summary>
public static class MiniWorldCatalog
{
    public const string Disclosure = """
        一、作者立场与功能目的
        作者属于中立群体。制作该功能主要是为了反讽一些现在仍然坚持网络争端的人们。作者仍然保持中立态度，并不会评判任何游戏。入口的存在不代表要求你喜欢某款游戏，也不代表对任何玩家群体的评价。

        二、尊重不同选择
        请理性看待不同游戏和玩家的喜好，不以游戏选择辱骂、骚扰、引战或传播未经核实的信息。你可以随时返回；如果不想看到此入口，可在设置 → 体验细节 → 实验性功能中关闭“显示迷你世界下载与启动入口”。

        三、实验功能与来源说明
        此功能仍处于实验阶段，可能出现下载链接失效、版本目录变化、启动失败等情况。官方、渠道、历史版本和社区修改版本分别展示；社区收录不等于官方认证，XCL2 不拥有这些游戏，也不能替发布者承诺文件安全或账号兼容。

        四、下载、CDK 与个人信息
        优先使用可信发布者提供的 HTTPS 文件直链。CDK 只适用于发布者明确支持的兑换接口，兑换码仅发送给界面显示的服务，不会由 XCL2 生成、破解或绕过授权。请勿将游戏密码、支付密码或其他服务的验证码当作 CDK 填写。兑换失败、过期或无权限时请联系发行方；XCL2 不会自动打开浏览器或替你购买兑换码。

        五、安装、更新与存档
        下载只保存文件，不会自动运行安装包。压缩包需解压，分卷需要全部放在同一目录；安装后选择实际的游戏 EXE。历史版、渠道版和社区版可能使用不同账号与存档格式，切换、覆盖或更新前请备份存档，并分别保存安装目录。

        六、网络与文件校验
        下载会使用网络流量与磁盘空间，可在下载队列中查看进度和取消。若发布者提供 SHA-256，启动器会按其值校验；文件格式检查和校验通过只表示满足相应检查条件，不能代替发布者身份核实。网页、人机验证或错误响应不能当作游戏安装包。

        七、自主选择与阅读确认
        是否下载、安装和启动由你自行决定。遇到异常请停止操作并保留错误信息；不要为使用本功能关闭系统防护。下方问题只确认你理解这份说明，不要求你表达对某款游戏的好恶。每次进入迷你世界面板都需要重新阅读并完成确认。
        """;
    public const string ExperimentalNotice = "当前功能为实验性功能";

    public static IReadOnlyList<MiniWorldReadingQuestion> ReadingQuestions { get; } = new MiniWorldReadingQuestion[]
    {
        new("作者属于什么群体？", new[] { "支持某一游戏并反对其他游戏", "中立群体", "评判所有游戏的群体" }, 1),
        new("不想看到迷你世界入口时，可以怎么做？", new[] { "只能卸载启动器", "必须使用一次后才能关闭", "在设置的体验细节 → 实验性功能中关闭入口" }, 2),
        new("如何对待其他玩家的游戏选择？", new[] { "尊重选择，避免辱骂和引战", "依据游戏选择评价所有玩家", "传播未经核实的信息" }, 0),
        new("社区目录收录代表什么？", new[] { "已经获得官方认证", "由社区维护，仍需核实发布者与版本", "保证兼容所有账号和存档" }, 1),
        new("什么时候可以使用 CDK 下载？", new[] { "任意游戏都可使用同一兑换码", "把游戏密码当作 CDK 即可", "发布者提供支持的接口与有效兑换码时" }, 2),
        new("下载完成后会自动安装或运行吗？", new[] { "不会，需要自行决定安装或解压，并关联游戏 EXE", "会自动运行所有文件", "会自动替换所有已有版本" }, 0),
        new("切换旧版、渠道版或社区版前应怎样做？", new[] { "直接覆盖同一目录即可", "备份存档，核实兼容性，分别保存安装目录", "删除所有备份" }, 1),
        new("文件格式检查或 SHA-256 校验通过意味着什么？", new[] { "绝对安全且无需核实来源", "保证发布者身份真实", "满足相应检查条件，仍需核实来源" }, 2)
    };

    public static bool AreReadingAnswersCorrect(IReadOnlyList<int> answers)
        => answers.Count == ReadingQuestions.Count && ReadingQuestions.Select((q, i) => answers[i] == q.CorrectIndex).All(value => value);

    // 2026-10-21 核对来源：mini1.cn、mini1.cn/article/20171026/152.html、
    // news.4399.com/html/mnsjnew/、news.7k7k.com/mnsj/、miniworldgame.com/zh-hant。
    public static IReadOnlyList<MiniWorldSource> Sources { get; } = new MiniWorldSource[]
    {
        new("official", "官方正式版", "官方 PC", "下载官方 PC 安装包。安装完成后选择实际的游戏程序，即可从这里启动。",
            "https://www.mini1.cn/", "https://mdownload.mini1.cn/latest/miniworldoffice.exe", "miniworldoffice.exe"),
        new("beta", "官方先遣服", "官方测试服", "需要满足官方先遣服资格要求；与正式服分别选择启动程序。",
            "https://www.mini1.cn/", "https://mdownload.mini1.cn/office/MiniworldBeta_PC.exe", "MiniworldBeta_PC.exe"),
        new("cbl-history", "PC 历史版本", "CBL 社区收录", "读取 CBL 发布者维护的原版历史版本目录。由社区整理和重新分发，版本身份以维护者说明为准，未经过本启动器的官方原版鉴定。",
            "https://blog.song3060.cn/5347.html", VersionIndexUrl: "https://api.song3060.top/CBL_config/DownloadPage/DownloadPageConfig/O_DownloadConfig.json"),
        new("cbl-community", "社区游戏版本", "CBL 社区收录", "读取维护者公开的二次创作客户端目录，包含星辉阁、XHG 等社区版本。不同客户端、修改版和账号环境可能不兼容。",
            "https://blog.song3060.cn/5347.html", VersionIndexUrl: "https://api.song3060.top/CBL_config/DownloadPage/DownloadPageConfig/M_DownloadConfig.json"),
        new("cbl-launcher", "CBL 社区启动器", "独立启动器", "这是社区启动器本身，不是迷你世界游戏包。v1.1.3，发布者标注更新于 2026-07-20。",
            "https://cbl.song3060.com/", "https://file.song3060.top/f/d/e1vcv/CreateBlocksLauncher%E5%AE%89%E8%A3%85%E5%8C%85.exe", "CreateBlocksLauncher-1.1.3.exe"),
        new("4399", "4399 社区渠道", "社区渠道", "4399 提供的电脑微端。渠道账号与登录方式以发布方说明为准。",
            "https://news.4399.com/html/mnsjnew/", "https://video.5054399.com/microgame/miniworldmicro4399.exe", "miniworldmicro4399.exe"),
        new("7k7k", "7k7k 社区渠道", "社区渠道", "前往 7k7k 专区查看当前下载入口；本站未提供经过核对的固定安装包直链。",
            "https://news.7k7k.com/mnsj/"),
        new("qqgame", "QQ 游戏大厅渠道", "社区渠道", "按迷你世界官方教程，在 QQ 游戏大厅内搜索并安装。可选择本地大厅或游戏 EXE 启动。",
            "https://qqgame.qq.com/"),
        new("international", "Mini World: CREATA", "官方国际版", "国际版官网提供的 PC 下载入口；账号与服务器以该版本说明为准。",
            "https://www.miniworldgame.com/", "https://mwu-api.miniworldgame.com/app_install/1/410?os_flag=universal", "MiniWorld-CREATA-Setup.exe"),
        new("international-steam", "CREATA · Steam", "官方商店", "通过国际版官网链接到的 Steam 商店安装和管理国际版。需要 Steam 客户端。",
            "https://store.steampowered.com/app/814480/Mini_World_CREATA/"),
        new("xinghuige", "星辉阁社区发布站", "社区版本", "发布者的独立下载站，提供 PC 社区客户端和维护公告；可打开发布页，或为各版本分别添加下载直链。",
            "https://xinghuige.cn/"),
        new("songzhi", "松纸普通版 / 纯净版", "社区版本", "发布者维护的历史版本更新下载页。普通版与纯净版分别安装、分别绑定启动程序。",
            "https://blog.song3060.cn/3492.html")
    };

    public static bool IsCorrectAnswer(string? answer) => string.Equals(answer?.Trim(), "中立群体", StringComparison.Ordinal);

    public static string ClientKey(string sourceId, string versionId)
    {
        var value = sourceId.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + sourceId + versionId;
        return "mini-version:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    public static string ValidateExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("请先选择该客户端的游戏 EXE。");
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            throw new FileNotFoundException("启动程序不存在或不是 EXE，请重新选择已安装的游戏程序。", fullPath);
        return fullPath;
    }

    /// <summary>只检查 Windows 程序格式，不能据此判断发布方身份或文件安全性。</summary>
    public static void ValidateDownloadedExecutable(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 64 || reader.ReadUInt16() != 0x5a4d)
            throw new InvalidDataException("下载结果不是 Windows 安装程序，可能是过期链接或网页。请使用来源下载页重试。");
        stream.Position = 0x3c;
        var peOffset = reader.ReadInt32();
        if (peOffset < 64 || peOffset > stream.Length - 4)
            throw new InvalidDataException("安装程序格式不完整，请重新下载。");
        stream.Position = peOffset;
        if (reader.ReadUInt32() != 0x00004550) throw new InvalidDataException("安装程序格式无效，请重新下载。");
    }
}
