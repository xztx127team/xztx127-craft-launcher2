using System.IO;

namespace XCL2.App.Services;

public sealed record MiniWorldSource(string Id, string Name, string Kind, string Description,
    string HomeUrl, string? DirectDownloadUrl = null, string? DownloadFileName = null, string? VersionIndexUrl = null);

/// <summary>来源与安装方式分别呈现，不把渠道下载页伪装成可直接下载的安装包。</summary>
public static class MiniWorldCatalog
{
    public const string Disclosure = "作者属于中立群体。制作这个功能主要是为了反讽一些现在仍然坚持网络争端的人们。作者仍然保持中立态度，并不会评判任何游戏。如果你认为这个功能没有任何作用，或会刺激到你，可以在设置 → 体验细节 → 实验性功能中关闭迷你世界入口。";
    public const string ExperimentalNotice = "当前功能为实验性功能";

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
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("请先选择这个客户端的游戏 EXE。");
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
