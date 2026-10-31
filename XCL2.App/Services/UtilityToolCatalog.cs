using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XCL2.App.Services;

public sealed record UtilityToolField(string Label, string Example, bool Multiline = false, bool Required = true);
public sealed record UtilityToolDefinition(string Id, string Title, string Description, UtilityToolField[] Fields);
public sealed class UtilityToolInputException : Exception
{
    public int FieldIndex { get; }
    public UtilityToolInputException(int field, string message) : base(message) => FieldIndex = field;
}

/// <summary>Pure, offline tools. No network, shell execution or mutation of user files.</summary>
public static class UtilityToolCatalog
{
    private static UtilityToolField F(string label, string example, bool multiline = false, bool required = true) => new(label, example, multiline, required);
    private static UtilityToolDefinition T(string id, string title, string description, params UtilityToolField[] fields) => new(id, title, description, fields);
    public const int FirstTabIndex = 37;
    public static IReadOnlyList<UtilityToolDefinition> All { get; } = Array.AsReadOnly(new[]
    {
        T("json-format", "JSON 检查与排版", "检查 JSON 语法，并同时给出缩进版和压缩版。", F("JSON", "{\"name\":\"Steve\",\"online\":true}", true)),
        T("json-csv", "JSON 转 CSV", "将对象数组转换为带标题的 CSV；正确转义逗号、引号和换行。", F("对象数组", "[{\"name\":\"Steve\",\"score\":10},{\"name\":\"Alex\",\"score\":20}]", true)),
        T("csv-json", "CSV 转 JSON", "首行作为字段名，保留单元格文本；支持带引号的多行单元格。", F("CSV", "name,score\nSteve,10\nAlex,20", true)),
        T("properties-json", "属性配置转 JSON", "将简单 key=value 配置转换为 JSON 字符串对象；不支持 Java 转义/续行，遇到时明确提示。", F("配置内容", "server-port=25565\nmotd=Hello", true)),
        T("json-properties", "JSON 转属性配置", "将一层 JSON 对象转换为简单 key=value 配置，拒绝嵌套或无法安全表示的值。", F("JSON 对象", "{\"server-port\":25565,\"motd\":\"Hello\"}", true)),
        T("url-inspect", "网址结构检查", "拆分完整网址的协议、主机、端口、路径与查询参数，不访问该网址。", F("网址", "https://example.com:8443/path?q=hello#top")),
        T("query-build", "URL 查询参数生成", "每行 key=value，自动百分号编码并组合查询串；支持重复参数名。", F("参数", "name=Alex\nmessage=你好 世界", true)),
        T("url-codec", "URL 百分号编解码", "按 UTF-8 对单个参数值编码或解码；不把 + 当作空格。", F("操作：encode / decode", "encode"), F("文本", "你好 世界", true)),
        T("html-codec", "HTML 实体编解码", "转义或还原 HTML 实体；结果仅作为文字显示。", F("操作：encode / decode", "encode"), F("文本", "<hello> & \"world\"", true)),
        T("unicode", "Unicode 字符检查", "列出每个 Unicode 码点、UTF-8 字节和类别，用于排查隐藏字符。", F("文本（最多 10000 字符）", "Hello 世界 👋", true)),
        T("text-stats", "文本字数与大小", "统计 Unicode 码点、字素、行数、非空行、UTF-8 字节和空白分隔词数。", F("文本", "Hello 世界\n第二行", true)),
        T("unique-lines", "文本逐行去重", "按原始顺序去重，区分大小写；同时给出重复统计。", F("每行一项", "stone\ndirt\nstone\nsand", true)),
        T("sort-lines", "文本行排序", "使用稳定的序号规则排序，可选择正序、倒序或数值排序；数值模式忽略空白行。", F("每行一项", "10\n2\n30", true), F("排序：asc / desc / numeric", "numeric")),
        T("filter-lines", "正则筛选文本行", "保留匹配正则的行，每次匹配有超时保护；不会改写输入。", F("文本", "[INFO] Ready\n[ERROR] Failed", true), F("正则表达式", "ERROR")),
        T("replace-text", "批量文字替换", "按字面值替换全部匹配，不解释正则或转义。", F("原文", "Hello Steve, Steve!", true), F("查找", "Steve"), F("替换", "Alex", false, false)),
        T("text-diff", "文本逐行对比", "逐行比较相同位置的内容，列出新增、删除和改变；不做自动合并。", F("原文", "stone\ndirt", true), F("新文本", "stone\nsand", true)),
        T("text-case", "文字大小写转换", "支持英文大写、小写和标题形式，使用固定文化规则。", F("文本", "hello minecraft", true), F("模式：upper / lower / title", "title")),
        T("markdown-table", "CSV 转 Markdown 表格", "将 CSV 标题和记录转换为 Markdown 表格，转义竖线和换行。", F("CSV", "name,score\nSteve,10\nAlex,20", true)),
        T("slug", "文件名与路径段整理", "生成可用于单个文件名的文本；去掉 Windows 禁用字符并处理保留名。", F("名称", "My world: 2026 / backup")),
        T("wrap-text", "文字定宽分行", "按 Unicode 码点数插入真实换行；保留已有段落（不是显示宽度）。", F("文本", "ABCDEFGHIJKLMNOPQRSTUVWXYZ", true), F("每行码点数 1–500", "10")),
        T("base-convert", "整数进制转换", "在二、八、十、十六进制间转换有符号整数，支持大整数。", F("整数（不带 0x 前缀）", "FF"), F("输入进制 2 / 8 / 10 / 16", "16")),
        T("stats", "数列统计", "计算数量、总和、均值、中位数、极值和总体标准差。", F("数字，以空格、逗号或换行分隔", "1,2,3,4,5", true)),
        T("byte-units", "存储容量单位换算", "同时给出十进制和二进制单位；输入单位区分 KB 与 KiB。", F("容量", "1024"), F("单位 B / KB / MB / GB / TB / KiB / MiB / GiB / TiB", "MiB")),
        T("download-time", "下载耗时估算", "按文件字节数和网络比特率估算理想耗时；实际速度受协议开销影响。", F("文件大小 MiB", "1024"), F("下载速率 Mbps", "100")),
        T("percent", "百分比与增减计算", "比较原值和新值，给出差值、增减百分比及新值占比。", F("原值（不能为零）", "80"), F("新值", "100")),
        T("scale", "比例缩放计算", "按原始宽高和目标宽度等比缩放，输出高度与比例。", F("原宽度", "1920"), F("原高度", "1080"), F("目标宽度", "1280")),
        T("duration", "时长分解", "将非负秒数拆解为天、时、分、秒，同时给出毫秒。", F("秒数", "90061.5")),
        T("date-diff", "两个日期间隔", "接受带时区的 ISO 8601 日期，按实际时刻计算间隔；无时区按 UTC。", F("开始时间", "2026-01-01T00:00:00Z"), F("结束时间", "2026-01-02T12:00:00+08:00")),
        T("date-add", "日期偏移计算", "给日期增加或减去秒数，保留输入时区；无时区按 UTC。", F("ISO 8601 日期", "2026-01-01T00:00:00Z"), F("偏移秒数（可为负）", "86400")),
        T("subnet", "IPv4 子网计算", "计算 CIDR 的网络地址、广播地址、掩码和地址总数。/31、/32 按点到点或单主机解释。", F("IPv4/CIDR", "192.168.1.42/24")),
        T("uuid-inspect", "UUID 格式检查", "验证 UUID 并转换带横线/无横线格式，展示版本与变体位。", F("UUID", "123e4567-e89b-42d3-a456-426614174000")),
        T("uuid-generate", "随机 UUID 批量生成", "使用系统随机源生成版本 4 UUID，不是离线玩家 UUID。", F("数量 1–500", "5")),
        T("password", "随机密码生成", "使用密码学随机源，从你指定的不重复字符中均匀抽取；不会保存。", F("长度 8–512", "24"), F("字符集", "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%")),
        T("hash-text", "文本 SHA 摘要", "对原始 UTF-8 文本计算 SHA-256 与 SHA-512；真实换行参与计算。", F("文本", "hello", true)),
        T("hmac", "HMAC-SHA256 计算", "使用 UTF-8 密钥与原文计算 HMAC；纯本地，不保存密钥。", F("密钥", "sample-key"), F("原文", "hello", true)),
        T("hex-text", "UTF-8 十六进制编解码", "文本与十六进制字节互转；解码拒绝无效 UTF-8。", F("操作：encode / decode", "encode"), F("文本或十六进制字节", "你好", true)),
        T("gzip-text", "GZip 文本压缩解压", "UTF-8 文本与 GZip Base64 互转；解压输出限制为 1 MiB。", F("操作：compress / decompress", "compress"), F("文本或 Base64", "Hello Minecraft", true)),
        T("regex", "正则表达式测试", "列出匹配位置与捕获组；超时 250 毫秒，最多显示 1000 个匹配。", F("正则", "(?<name>[A-Za-z]+)=(?<value>\\d+)"), F("文本", "stone=64 dirt=32", true)),
        T("version-compare", "语义版本比较", "按 SemVer 2.0.0 比较 major.minor.patch 和预发布标识；构建元数据不影响顺序。", F("版本 A", "1.2.0-rc.1"), F("版本 B", "1.2.0")),
        T("chunk-coord", "方块所在区块", "给出区块坐标和区块内坐标，负数按向下取整处理。", F("方块 X（整数）", "-1"), F("方块 Z（整数）", "31")),
        T("region-coord", "区块所属区域文件", "从区块坐标求 Anvil 区域文件名、区域内索引和方块覆盖范围。", F("区块 X", "-1"), F("区块 Z", "32")),
        T("region-file", "区域文件范围解析", "解析 r.x.z.mca 文件名，显示覆盖的区块及方块范围。", F("区域文件名", "r.-1.2.mca")),
        T("fill-volume", "长方体方块用量", "按两角坐标（包含端点）计算实心、空心外壳和内部方块数量。", F("起点 X Y Z", "0 0 0"), F("终点 X Y Z", "9 9 9")),
        T("sphere-plan", "球体逐层施工表", "以整数中心按 x²+y²+z²≤r² 统计离散实心球；给出每层用量。", F("半径 1–64", "5")),
        T("circle-plan", "圆形逐行施工表", "按 x²+z²≤r² 生成实心圆每行跨度；可用于像素圆施工。", F("半径 1–256", "8")),
        T("cylinder-plan", "圆柱施工用量", "使用离散圆底面乘高度，给出实心用量及每行底面跨度。", F("半径 1–256", "8"), F("高度 1–1000000", "10")),
        T("line-plan", "像素直线坐标生成", "Bresenham 整数直线算法，输出两点间连续像素坐标（最多 10001 点）。", F("起点 X Z", "0 0"), F("终点 X Z", "10 6")),
        T("border-bounds", "世界边界范围计算", "按中心和边长计算边界四边坐标，不会修改存档。", F("中心 X Z", "0 0"), F("边长（方块）", "1000")),
        T("travel", "路线行程耗时", "按路线距离和自定速度估算时间，可加入每段停留秒数。", F("距离（方块）", "1000"), F("速度（方块/秒）", "5.6"), F("额外停留秒数", "0")),
        T("tick-budget", "服务器 Tick 预算", "根据目标 TPS 与实测每 tick 毫秒估算预算余量和理论 TPS；不连接服务器。", F("目标 TPS", "20"), F("实测 MSPT", "35")),
        T("fuel", "熔炼燃料预算", "按目标物品数、每份燃料可熔炼数量及每物品耗时计算燃料与总时长。", F("待熔炼物品数", "100"), F("每份燃料可熔炼数量", "8"), F("每物品秒数", "10")),
        T("craft-batches", "合成批次数量", "计算目标数量所需的整数批次，以及最终产量和多余数量。", F("需要的成品数", "100"), F("每次合成产量", "4")),
        T("recipe-bill", "合成材料清单", "输入每批材料用量 JSON，按目标成品数量计算完整材料总账。", F("每批材料用量 JSON", "{\"oak_planks\":6,\"stick\":1}", true), F("每批成品数", "2"), F("目标成品数", "15")),
        T("rotate-coord", "平面坐标旋转", "以指定原点旋转 X/Z 坐标，正角度为平面逆时针。", F("坐标 X Z", "10 0"), F("原点 X Z", "0 0"), F("旋转角度（度）", "90")),
        T("offset-coord", "批量坐标平移", "每行 X Y Z，加上同一偏移；支持小数坐标。", F("坐标列表", "0 64 0\n10 70 -5", true), F("偏移 X Y Z", "100 0 200")),
        T("tellraw", "聊天 JSON 文字生成", "生成安全转义的 Java 版 /tellraw 纯文字命令；输出仅供复制，不执行。", F("文字", "你好，世界！", true), F("颜色 #RRGGBB", "#55ff55")),
        T("gradient", "渐变聊天文字生成", "按 Unicode 码点插值两端颜色，生成 Java 版 /tellraw JSON（最多 500 码点）。", F("文字", "Minecraft", true), F("起始颜色 #RRGGBB", "#55ffff"), F("结束颜色 #RRGGBB", "#ff55ff")),
        T("resource-id", "资源命名空间检查", "检查 namespace:path 字符规则；无命名空间时使用 minecraft。", F("资源 ID", "minecraft:stone")),
        T("pack-meta", "资源包元数据生成", "生成指定 pack_format 的 pack.mcmeta；版本号由你按目标游戏版本填写。", F("pack_format 正整数", "15"), F("描述", "My resource pack", true)),
        T("mcfunction-clean", "函数文件注释整理", "移除独立注释行和空白行，保留命令原文及行内 #；不会执行命令。", F("mcfunction 内容", "# Greeting\nsay Hello\n\n# Done\nsay Ready", true)),
        T("properties-lint", "服务器配置静态检查", "检查重复键、常见端口/人数/距离字段和布尔值；未知键保留，不自动修复。", F("server.properties 内容", "server-port=25565\nmax-players=20\nonline-mode=true", true)),
        T("whitelist", "白名单 JSON 生成", "每行输入玩家名与已知 UUID，生成 whitelist.json；不会联网推断身份或写入服务器。", F("每行：玩家名 UUID", "Steve 123e4567-e89b-42d3-a456-426614174000", true)),
        T("contrast", "文字颜色对比度", "按 sRGB 相对亮度计算两种颜色的对比度，并与 4.5:1 / 3:1 参考阈值比较。", F("前景 #RRGGBB", "#ffffff"), F("背景 #RRGGBB", "#333333")),
        T("loot-weight", "权重概率计算", "每行 名称=权重，计算独立一次抽取的概率；不模拟游戏的额外条件。", F("权重列表", "stone=70\niron=25\ndiamond=5", true))
    });

    public static string Run(string id, IReadOnlyList<string> values, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tool = All.FirstOrDefault(x => x.Id == id) ?? throw new ArgumentException("未知工具。");
        if (values.Count != tool.Fields.Length) throw new ArgumentException("输入字段数量不正确。");
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i].Length > 262144) throw E(i, "输入超过 256 Ki 字符，请分批处理。");
            if (tool.Fields[i].Required && values[i].Length == 0) throw E(i, $"请填写{tool.Fields[i].Label}。");
        }
        try
        {
            string result = Execute(id, values, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Length > 1048576) throw E(0, "输出超过 1 Mi 字符，请缩小输入范围。");
            return result;
        }
        catch (UtilityToolInputException) { throw; }
        catch (RegexMatchTimeoutException) { throw E(id == "filter-lines" ? 1 : 0, "正则匹配超时，请简化表达式或缩小输入。"); }
        catch (JsonException ex) { throw E(0, $"JSON 语法错误：第 {(ex.LineNumber ?? 0) + 1} 行，第 {(ex.BytePositionInLine ?? 0) + 1} 字节。{ex.Message}"); }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException or InvalidDataException)
        { throw E(0, "输入无法处理：" + ex.Message); }
    }

    private static string Execute(string id, IReadOnlyList<string> v, CancellationToken cancellationToken) => id switch
    {
        "json-format" => FormatJson(v[0]),
        "json-csv" => JsonCsv(v[0]),
        "csv-json" => Serialize(CsvObjects(v[0])),
        "properties-json" => Serialize(Properties(v[0]).ToDictionary(x => x.Key, x => x.Value)),
        "json-properties" => JsonProperties(v[0]),
        "url-inspect" => InspectUrl(v[0]),
        "query-build" => Query(v[0]),
        "url-codec" => Choice(v, 0, "encode", "decode") == "encode" ? Uri.EscapeDataString(v[1]) : DecodeUri(v[1]),
        "html-codec" => Choice(v, 0, "encode", "decode") == "encode" ? WebUtility.HtmlEncode(v[1]) : WebUtility.HtmlDecode(v[1]),
        "unicode" => Codepoints(v[0]),
        "text-stats" => TextStats(v[0]),
        "unique-lines" => UniqueLines(v[0]),
        "sort-lines" => SortLines(v),
        "filter-lines" => FilterLines(v, cancellationToken),
        "replace-text" => ReplaceText(v),
        "text-diff" => Diff(v[0], v[1]),
        "text-case" => Choice(v, 1, "upper", "lower", "title") switch { "upper" => v[0].ToUpperInvariant(), "lower" => v[0].ToLowerInvariant(), _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(v[0].ToLowerInvariant()) },
        "markdown-table" => MarkdownTable(v[0]),
        "slug" => FileSlug(v[0]),
        "wrap-text" => Wrap(v[0], I(v, 1, 1, 500)),
        "base-convert" => BaseConvert(v),
        "stats" => Statistics(v[0]),
        "byte-units" => ByteUnits(v),
        "download-time" => Duration(N(v, 0, 0, 1e12) * 1048576 * 8 / (N(v, 1, 0.000001, 1e12) * 1000000)),
        "percent" => Percent(v),
        "scale" => $"缩放比例：{S(N(v, 2, 0.000001, 1e12) / N(v, 0, 0.000001, 1e12))}\n目标尺寸：{S(N(v, 2))} × {S(N(v, 1, 0.000001, 1e12) * N(v, 2) / N(v, 0))}",
        "duration" => Duration(N(v, 0, 0, 1e12)),
        "date-diff" => DateDifference(v),
        "date-add" => AddDate(v),
        "subnet" => Subnet(v[0]),
        "uuid-inspect" => InspectUuid(v[0]),
        "uuid-generate" => Join(Enumerable.Range(0, I(v, 0, 1, 500)).Select(_ => Guid.NewGuid().ToString())),
        "password" => Password(v),
        "hash-text" => "SHA-256: " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(v[0]))).ToLowerInvariant() + "\nSHA-512: " + Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes(v[0]))).ToLowerInvariant(),
        "hmac" => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(v[0]), Encoding.UTF8.GetBytes(v[1]))).ToLowerInvariant(),
        "hex-text" => Choice(v, 0, "encode", "decode") == "encode" ? Convert.ToHexString(Encoding.UTF8.GetBytes(v[1])) : new UTF8Encoding(false, true).GetString(Convert.FromHexString(Regex.Replace(v[1], @"\s", ""))),
        "gzip-text" => Gzip(v),
        "regex" => TestRegex(v, cancellationToken),
        "version-compare" => CompareVersions(v),
        "chunk-coord" => Chunk(v),
        "region-coord" => Region(v),
        "region-file" => RegionFile(v[0]),
        "fill-volume" => FillVolume(v),
        "sphere-plan" => Sphere(I(v, 0, 1, 64)),
        "circle-plan" => Circle(I(v, 0, 1, 256), 1),
        "cylinder-plan" => Circle(I(v, 0, 1, 256), I(v, 1, 1, 1000000)),
        "line-plan" => PixelLine(v),
        "border-bounds" => Border(v),
        "travel" => Duration(N(v, 0, 0, 1e12) / N(v, 1, 0.000001, 1e12) + N(v, 2, 0, 1e12)),
        "tick-budget" => TickBudget(v),
        "fuel" => Fuel(v),
        "craft-batches" => Craft(v),
        "recipe-bill" => Recipe(v),
        "rotate-coord" => Rotate(v),
        "offset-coord" => Offset(v),
        "tellraw" => "/tellraw @a " + JsonSerializer.Serialize(new { text = v[0], color = Color(v[1], 1).Hex }),
        "gradient" => Gradient(v),
        "resource-id" => ResourceId(v[0]),
        "pack-meta" => Serialize(new { pack = new { pack_format = I(v, 0, 1, 1000000), description = v[1] } }),
        "mcfunction-clean" => Join(Lines(v[0]).Where(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'))),
        "properties-lint" => LintProperties(v[0]),
        "whitelist" => Whitelist(v[0]),
        "contrast" => Contrast(v),
        "loot-weight" => Weights(v[0]),
        _ => throw new ArgumentException("未知工具。")
    };

    private static UtilityToolInputException E(int field, string message) => new(field, message);
    private static string S(double n) => double.IsFinite(n) ? n.ToString("G15", CultureInfo.InvariantCulture) : throw E(0, "计算结果超出可表示的有限数值范围，请调整输入数量级。");
    private static string Join(IEnumerable<string> values) => string.Join("\n", values);
    private static string[] Lines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static string Serialize(object value) => JsonSerializer.Serialize(value, Pretty);
    private static double Number(string text, int field, double min = -1e15, double max = 1e15)
    {
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n) || n < min || n > max)
            throw E(field, $"请输入 {S(min)} 到 {S(max)} 之间的有限数字；小数点使用 .。");
        return n;
    }
    private static double N(IReadOnlyList<string> v, int i, double min = -1e15, double max = 1e15) => Number(v[i], i, min, max);
    private static int I(IReadOnlyList<string> v, int i, int min, int max)
    {
        if (!int.TryParse(v[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < min || value > max) throw E(i, $"请输入 {min} 到 {max} 之间的整数。");
        return value;
    }
    private static string Choice(IReadOnlyList<string> v, int i, params string[] allowed)
    {
        string selected = v[i].Trim().ToLowerInvariant();
        if (!allowed.Contains(selected)) throw E(i, "可选值：" + string.Join(" / ", allowed));
        return selected;
    }
    private static double[] Vector(string text, int count, int field, bool integer = false)
    {
        var parts = Regex.Split(text.Trim(), @"[\s,，]+");
        if (parts.Length != count) throw E(field, $"请输入 {count} 个坐标，以空格分隔。");
        var values = parts.Select(x => Number(x, field, -30000000, 30000000)).ToArray();
        if (integer && values.Any(x => x != Math.Truncate(x))) throw E(field, "此工具需要整数坐标。");
        return values;
    }
    private static Regex RegexFor(string expression, int field)
    {
        if (expression.Length > 4096) throw E(field, "正则表达式最多 4096 个字符。请拆分处理。");
        try { return new Regex(expression, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)); }
        catch (ArgumentException ex) { throw E(field, "正则表达式无效：" + ex.Message); }
    }
    private static JsonDocument Json(string text) => JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
    private static string FormatJson(string text)
    {
        using var doc = Json(text);
        return "缩进格式：\n" + Serialize(doc.RootElement) + "\n\n压缩格式：\n" + JsonSerializer.Serialize(doc.RootElement);
    }
    private static List<string[]> Csv(string text)
    {
        var rows = new List<string[]>(); var row = new List<string>(); var cell = new StringBuilder();
        bool quoted = false, afterQuote = false, started = false;
        void EndCell() { row.Add(cell.ToString()); cell.Clear(); afterQuote = false; started = false; }
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else { quoted = false; afterQuote = true; } }
                else cell.Append(c);
            }
            else if (c == ',' ) EndCell();
            else if (c is '\r' or '\n') { EndCell(); rows.Add(row.ToArray()); row.Clear(); if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; }
            else if (c == '"' && !started && !afterQuote) { quoted = true; started = true; }
            else { if (afterQuote || c == '"') throw E(0, "CSV 引号格式无效：引号必须包围整个单元格。"); cell.Append(c); started = true; }
            if (rows.Count > 10000) throw E(0, "最多处理 10000 条 CSV 记录。");
        }
        if (quoted) throw E(0, "CSV 包含未闭合的引号。");
        if (cell.Length > 0 || started || afterQuote || row.Count > 0) { EndCell(); rows.Add(row.ToArray()); }
        if (rows.Count == 0) throw E(0, "CSV 不能为空。");
        if (rows.Any(r => r.Length != rows[0].Length)) throw E(0, "CSV 各行列数不一致，请检查逗号与引号。");
        return rows;
    }
    private static string CsvCell(string value) => value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0 ? value : '"' + value.Replace("\"", "\"\"") + '"';
    private static object CsvObjects(string text)
    {
        var rows = Csv(text); var headers = rows[0];
        if (headers.Any(string.IsNullOrWhiteSpace) || headers.Distinct(StringComparer.Ordinal).Count() != headers.Length) throw E(0, "CSV 标题必须非空且不能重复。");
        return rows.Skip(1).Select(row => headers.Select((header, i) => (header, row[i])).ToDictionary(p => p.header, p => p.Item2)).ToArray();
    }
    private static string JsonCsv(string text)
    {
        using var doc = Json(text);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.Object)) throw E(0, "请输入 JSON 对象数组。");
        var rows = doc.RootElement.EnumerateArray().ToArray();
        var headers = rows.SelectMany(x => x.EnumerateObject().Select(p => p.Name)).Distinct(StringComparer.Ordinal).ToArray();
        if (headers.Length == 0) throw E(0, "对象数组中没有可导出的字段。");
        if ((long)headers.Length * (rows.Length + 1) > 200000) throw E(0, "CSV 输出将超过 200000 个单元格，请缩小输入或统一对象字段。");
        return Join(new[] { string.Join(',', headers.Select(CsvCell)) }.Concat(rows.Select(row => string.Join(',', headers.Select(header => CsvCell(row.TryGetProperty(header, out var p) ? (p.ValueKind == JsonValueKind.String ? p.GetString()! : p.GetRawText()) : ""))))));
    }
    private static List<KeyValuePair<string, string>> Properties(string text, bool allowDuplicate = false)
    {
        var result = new List<KeyValuePair<string, string>>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in Lines(text))
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#') || line.TrimStart().StartsWith('!')) continue;
            int equals = line.IndexOf('=');
            if (equals < 1) throw E(0, "每个配置项须使用非空 key=value 格式。");
            string key = line[..equals].Trim(), value = line[(equals + 1)..];
            if (key.Length == 0) throw E(0, "配置项的键不能为空。");
            if (line.Contains('\\')) throw E(0, "此转换器不接受 Java 转义或续行，请先展开转义内容。");
            if (!seen.Add(key) && !allowDuplicate) throw E(0, $"重复的配置键：{key}。请先明确要保留哪一个值。");
            result.Add(new(key, value));
        }
        if (result.Count == 0) throw E(0, "没有找到配置项。");
        return result;
    }
    private static string JsonProperties(string text)
    {
        using var doc = Json(text);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw E(0, "请输入一层 JSON 对象。");
        var lines = new List<string>();
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null) throw E(0, $"{p.Name}：不支持嵌套或 null 值。");
            string value = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
            if (p.Name.Length == 0 || p.Name.IndexOfAny(new[] { '=', ':', '\\', '\r', '\n', ' ', '\t' }) >= 0 || p.Name.StartsWith('#') || p.Name.StartsWith('!') || value.IndexOfAny(new[] { '\r', '\n', '\\' }) >= 0 || value != value.TrimStart()) throw E(0, "键或值无法安全表示为简单属性配置，请去除特殊字符/前导空白。");
            lines.Add(p.Name + "=" + value);
        }
        return Join(lines);
    }
    private static string InspectUrl(string text)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) || uri.Host.Length == 0) throw E(0, "请输入包含协议和主机的完整网址，例如 https://example.com/path。");
        string query = Join(uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(x => DecodeUri(x)));
        return $"协议：{uri.Scheme}\n主机：{uri.IdnHost}\n端口：{uri.Port}\n路径：{uri.AbsolutePath}\n片段：{uri.Fragment}\n参数：\n{query}";
    }
    private static string Query(string text)
    {
        var pairs = new List<string>();
        foreach (string line in Lines(text).Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            int equals = line.IndexOf('=');
            if (equals < 1) throw E(0, "每行参数须为非空 key=value 格式。");
            pairs.Add(Uri.EscapeDataString(line[..equals]) + "=" + Uri.EscapeDataString(line[(equals + 1)..]));
            if (pairs.Count > 10000) throw E(0, "一次最多生成 10000 个查询参数。");
        }
        if (pairs.Count == 0) throw E(0, "请填写至少一行查询参数。");
        return "?" + string.Join("&", pairs);
    }
    private static string DecodeUri(string text)
    {
        if (Regex.IsMatch(text, @"%(?![0-9a-fA-F]{2})")) throw E(1, "百分号后必须紧跟两位十六进制数字。");
        return Uri.UnescapeDataString(text);
    }
    private static string Codepoints(string text)
    {
        if (text.Length > 10000) throw E(0, "字符检查最多接受 10000 个 UTF-16 代码单元。");
        return Join(text.EnumerateRunes().Select((r, i) => $"{i + 1}\tU+{r.Value:X4}\t{Convert.ToHexString(Encoding.UTF8.GetBytes(r.ToString()))}\t{Rune.GetUnicodeCategory(r)}\t{(Rune.IsControl(r) ? "[控制字符]" : r.ToString())}"));
    }
    private static string TextStats(string text) => $"Unicode 码点：{text.EnumerateRunes().Count()}\n字素：{StringInfo.ParseCombiningCharacters(text).Length}\n行数：{Lines(text).Length}\n非空行：{Lines(text).Count(x => !string.IsNullOrWhiteSpace(x))}\nUTF-8 字节：{Encoding.UTF8.GetByteCount(text)}\n空白分隔词数：{Regex.Matches(text, @"\S+").Count}";
    private static string UniqueLines(string text)
    {
        var lines = Lines(text); var unique = lines.Distinct(StringComparer.Ordinal).ToArray();
        return Join(unique) + $"\n\n—— 原始 {lines.Length} 行，去重后 {unique.Length} 行，删除 {lines.Length - unique.Length} 行 ——";
    }
    private static string SortLines(IReadOnlyList<string> v)
    {
        var mode = Choice(v, 1, "asc", "desc", "numeric"); var lines = Lines(v[0]);
        if (mode == "numeric") { lines = lines.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray(); if (lines.Length == 0) throw E(0, "请输入至少一个数字。"); }
        return Join(mode == "numeric" ? lines.OrderBy(x => Number(x, 0)) : mode == "desc" ? lines.OrderByDescending(x => x, StringComparer.Ordinal) : lines.OrderBy(x => x, StringComparer.Ordinal));
    }
    private static string ReplaceText(IReadOnlyList<string> v)
    {
        long outputLength = v[0].Length;
        int start = 0, index;
        while ((index = v[0].IndexOf(v[1], start, StringComparison.Ordinal)) >= 0)
        {
            outputLength += (long)v[2].Length - v[1].Length;
            if (outputLength > 1048576) throw E(2, "替换后的文本将超过 1 Mi 字符，请缩短替换文本或分批处理。");
            start = index + v[1].Length;
        }
        return v[0].Replace(v[1], v[2], StringComparison.Ordinal);
    }
    private static string Diff(string before, string after)
    {
        var a = Lines(before); var b = Lines(after); var result = new List<string>();
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            if (i < a.Length && i < b.Length && a[i] == b[i]) continue;
            result.Add($"@@ 第 {i + 1} 行 @@");
            if (i < a.Length) result.Add("- " + a[i]);
            if (i < b.Length) result.Add("+ " + b[i]);
        }
        return result.Count == 0 ? "两段文本逐行相同。" : Join(result);
    }
    private static string MarkdownTable(string text)
    {
        var rows = Csv(text);
        string Row(IEnumerable<string> cells) => "| " + string.Join(" | ", cells.Select(c => c.Replace("\\", "\\\\").Replace("|", "\\|").Replace("\r\n", "<br>").Replace("\n", "<br>").Replace("\r", "<br>"))) + " |";
        return Row(rows[0]) + "\n" + Row(rows[0].Select(_ => "---")) + (rows.Count > 1 ? "\n" + Join(rows.Skip(1).Select(Row)) : "");
    }
    private static string FileSlug(string text)
    {
        var clean = Regex.Replace(text.Normalize(NormalizationForm.FormC), "[<>:\"/\\\\|?*\\x00-\\x1f]", "-");
        clean = Regex.Replace(clean.Trim(), @"\s+", "-").TrimEnd('.', ' ');
        if (clean.Length == 0) throw E(0, "整理后名称为空，请输入有效名称。");
        if (Regex.IsMatch(clean.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase)) clean = "_" + clean;
        if (clean.Length > 200) throw E(0, "整理后名称超过 200 个字符，请缩短名称。");
        return clean;
    }
    private static string Wrap(string text, int width) => Join(Lines(text).Select(line =>
    {
        var runes = line.EnumerateRunes().Select(r => r.ToString()).ToArray();
        return Join(runes.Chunk(width).Select(chunk => string.Concat(chunk)));
    }));
    private static string BaseConvert(IReadOnlyList<string> v)
    {
        int radix = I(v, 1, 2, 16);
        if (radix is not (2 or 8 or 10 or 16)) throw E(1, "输入进制只能为 2、8、10 或 16。");
        string input = v[0].Trim(); bool negative = input.StartsWith('-'); input = input.StartsWith('-') || input.StartsWith('+') ? input[1..] : input;
        if (input.Length is 0 or > 4096) throw E(0, "整数需包含 1–4096 位数字。");
        BigInteger value = BigInteger.Zero;
        foreach (char c in input.ToUpperInvariant()) { int digit = "0123456789ABCDEF".IndexOf(c); if (digit < 0 || digit >= radix) throw E(0, $"字符 {c} 不能用于 {radix} 进制。"); value = value * radix + digit; }
        if (negative) value = -value;
        string ConvertBase(int to)
        {
            var n = BigInteger.Abs(value); if (n.IsZero) return "0"; var sb = new StringBuilder();
            while (n > 0) { sb.Append("0123456789ABCDEF"[(int)(n % to)]); n /= to; }
            return (value.Sign < 0 ? "-" : "") + new string(sb.ToString().Reverse().ToArray());
        }
        return Join(new[] { 2, 8, 10, 16 }.Select(b => $"{b} 进制：{ConvertBase(b)}"));
    }
    private static string Statistics(string text)
    {
        var numbers = Regex.Split(text.Trim(), @"[\s,，;；]+").Select(x => Number(x, 0)).OrderBy(x => x).ToArray();
        double sum = numbers.Sum(), mean = sum / numbers.Length, median = numbers.Length % 2 == 0 ? (numbers[numbers.Length / 2 - 1] + numbers[numbers.Length / 2]) / 2 : numbers[numbers.Length / 2];
        double variance = numbers.Sum(x => (x - mean) * (x - mean)) / numbers.Length;
        return $"数量：{numbers.Length}\n总和：{S(sum)}\n均值：{S(mean)}\n中位数：{S(median)}\n最小值：{S(numbers[0])}\n最大值：{S(numbers[^1])}\n总体标准差：{S(Math.Sqrt(variance))}";
    }
    private static string ByteUnits(IReadOnlyList<string> v)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "KiB", "MiB", "GiB", "TiB" };
        int unit = Array.FindIndex(units, u => string.Equals(u, v[1].Trim(), StringComparison.OrdinalIgnoreCase));
        if (unit < 0) throw E(1, "请选择说明中列出的容量单位。");
        double[] factors = { 1, 1e3, 1e6, 1e9, 1e12, 1024, 1048576, 1073741824, 1099511627776 };
        double bytes = N(v, 0, 0, 1e15) * factors[unit];
        return Join(units.Select((name, i) => $"{S(bytes / factors[i])} {name}"));
    }
    private static string Percent(IReadOnlyList<string> v)
    {
        double a = N(v, 0), b = N(v, 1); if (a == 0) throw E(0, "原值不能为零，百分比没有定义。");
        return $"差值：{S(b - a)}\n相对原值的变化：(新值−原值)÷|原值| = {S((b - a) / Math.Abs(a) * 100)}%\n新值÷原值：{S(b / a * 100)}%";
    }
    private static string Duration(double seconds)
    {
        var days = Math.Floor(seconds / 86400); var hours = Math.Floor(seconds % 86400 / 3600); var minutes = Math.Floor(seconds % 3600 / 60);
        return $"{S(days)} 天 {S(hours)} 小时 {S(minutes)} 分 {S(seconds % 60)} 秒\n总秒数：{S(seconds)}\n总毫秒：{S(seconds * 1000)}";
    }
    private static DateTimeOffset Date(string text, int field)
    {
        if (!Regex.IsMatch(text.Trim(), @"^\d{4}-\d{2}-\d{2}(?:[Tt ]\d{2}:\d{2}(?::\d{2}(?:\.\d{1,7})?)?(?:[Zz]|[+-]\d{2}:\d{2})?)?$")
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var date)) throw E(field, "请输入有效 ISO 8601 日期，例如 2026-01-01T00:00:00Z。");
        return date;
    }
    private static string AddDate(IReadOnlyList<string> v)
    {
        var date = Date(v[0], 0); double seconds = N(v, 1, -1e11, 1e11);
        try { return date.AddSeconds(seconds).ToString("O", CultureInfo.InvariantCulture); }
        catch (ArgumentOutOfRangeException) { throw E(1, "偏移后日期超出可表示范围，请缩小偏移秒数。"); }
    }
    private static string DateDifference(IReadOnlyList<string> v)
    {
        var span = Date(v[1], 1) - Date(v[0], 0);
        return $"方向：{(span.Ticks < 0 ? "结束早于开始" : "结束不早于开始")}\n总天数：{S(span.TotalDays)}\n总小时：{S(span.TotalHours)}\n总秒数：{S(span.TotalSeconds)}\n绝对间隔：{Duration(Math.Abs(span.TotalSeconds))}";
    }
    private static string Subnet(string text)
    {
        var parts = text.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || !int.TryParse(parts[1], out int prefix) || prefix < 0 || prefix > 32) throw E(0, "请输入 IPv4/CIDR，前缀长度为 0–32。");
        byte[] b = ip.GetAddressBytes(); uint address = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix); uint network = address & mask, broadcast = network | ~mask;
        string Ip(uint n) => $"{n >> 24}.{(n >> 16) & 255}.{(n >> 8) & 255}.{n & 255}";
        ulong count = 1UL << (32 - prefix);
        return $"网络：{Ip(network)}/{prefix}\n掩码：{Ip(mask)}\n广播/末地址：{Ip(broadcast)}\n地址总数：{count}\n可用主机数：{(prefix >= 31 ? count : count - 2)}\n可用范围：{Ip(prefix >= 31 ? network : network + 1)} – {Ip(prefix >= 31 ? broadcast : broadcast - 1)}";
    }
    private static string InspectUuid(string text)
    {
        if (!Guid.TryParse(text, out var id)) throw E(0, "UUID 格式无效。");
        string hex = id.ToString("N"); int variant = Convert.ToInt32(hex[16].ToString(), 16);
        return $"带横线：{id:D}\n无横线：{hex}\n版本位：{Convert.ToInt32(hex[12].ToString(), 16)}\n变体：{(variant is >= 8 and <= 11 ? "RFC 4122/9562" : "非 RFC 标准变体")}";
    }
    private static string Password(IReadOnlyList<string> v)
    {
        int length = I(v, 0, 8, 512); var chars = v[1].EnumerateRunes().Select(x => x.ToString()).Distinct().ToArray();
        if (chars.Length < 2 || chars.Length > 256 || chars.Any(x => x.Any(char.IsWhiteSpace) || x.Any(char.IsControl))) throw E(1, "字符集需有 2–256 个不同的非空白、非控制字符。");
        return string.Concat(Enumerable.Range(0, length).Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)]));
    }
    private static string Gzip(IReadOnlyList<string> v)
    {
        if (Choice(v, 0, "compress", "decompress") == "compress")
        {
            using var output = new MemoryStream(); using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true)) { var bytes = Encoding.UTF8.GetBytes(v[1]); gzip.Write(bytes); }
            return Convert.ToBase64String(output.ToArray());
        }
        using var input = new MemoryStream(Convert.FromBase64String(v[1])); using var unzip = new GZipStream(input, CompressionMode.Decompress); using var decoded = new MemoryStream();
        byte[] buffer = new byte[8192]; int read;
        while ((read = unzip.Read(buffer)) > 0) { if (decoded.Length + read > 1048576) throw E(1, "解压后超过 1 MiB，已停止，避免过量占用内存。"); decoded.Write(buffer, 0, read); }
        return new UTF8Encoding(false, true).GetString(decoded.ToArray());
    }
    private static string FilterLines(IReadOnlyList<string> v, CancellationToken cancellationToken)
    {
        var regex = RegexFor(v[1], 1); var result = new List<string>(); var timer = Stopwatch.StartNew();
        foreach (var line in Lines(v[0]))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timer.Elapsed > TimeSpan.FromSeconds(2)) throw E(1, "筛选总耗时超过 2 秒，请简化表达式或分批处理。");
            if (regex.IsMatch(line)) result.Add(line);
        }
        return Join(result);
    }
    private static string TestRegex(IReadOnlyList<string> v, CancellationToken cancellationToken)
    {
        var regex = RegexFor(v[0], 0); var result = new StringBuilder(); int count = 0; var timer = Stopwatch.StartNew();
        void Add(string text)
        {
            if ((long)result.Length + text.Length + 1 > 1048576) throw E(1, "匹配和捕获组的输出将超过 1 Mi 字符，请缩小输入或减少捕获组。");
            if (result.Length > 0) result.Append('\n');
            result.Append(text);
        }
        foreach (Match m in regex.Matches(v[1]))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timer.Elapsed > TimeSpan.FromSeconds(2)) throw E(0, "匹配总耗时超过 2 秒，请简化表达式或分批处理。");
            if (++count > 1000) { Add("已达到 1000 条显示上限，请缩小输入。"); break; }
            Add($"#{count} 位置 {m.Index}，长度 {m.Length}：{m.Value}");
            foreach (string group in regex.GetGroupNames().Where(x => x != "0")) Add($"  {group} = {m.Groups[group].Value}");
        }
        return count == 0 ? "没有匹配项。" : result.ToString();
    }
    private sealed record SemVersion(BigInteger Major, BigInteger Minor, BigInteger Patch, string[] Pre);
    private static SemVersion Semver(string text, int field)
    {
        if (text.Length > 1000) throw E(field, "版本字符串最多 1000 个字符。");
        var match = Regex.Match(text.Trim(), @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        if (!match.Success) throw E(field, "请输入有效 SemVer 版本，例如 1.2.3-rc.1+build.5。");
        var pre = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : Array.Empty<string>();
        if (pre.Any(x => x.Length > 1 && x[0] == '0' && x.All(char.IsAsciiDigit))) throw E(field, "预发布数字标识不能有前导零。");
        return new(BigInteger.Parse(match.Groups[1].Value), BigInteger.Parse(match.Groups[2].Value), BigInteger.Parse(match.Groups[3].Value), pre);
    }
    private static string CompareVersions(IReadOnlyList<string> v)
    {
        var a = Semver(v[0], 0); var b = Semver(v[1], 1); int compare = a.Major.CompareTo(b.Major);
        if (compare == 0) compare = a.Minor.CompareTo(b.Minor);
        if (compare == 0) compare = a.Patch.CompareTo(b.Patch);
        if (compare == 0 && a.Pre.Length != b.Pre.Length && (a.Pre.Length == 0 || b.Pre.Length == 0)) compare = a.Pre.Length == 0 ? 1 : -1;
        if (compare == 0)
        {
            for (int i = 0; i < Math.Min(a.Pre.Length, b.Pre.Length) && compare == 0; i++)
            {
                bool na = a.Pre[i].All(char.IsAsciiDigit), nb = b.Pre[i].All(char.IsAsciiDigit);
                compare = na && nb ? BigInteger.Parse(a.Pre[i]).CompareTo(BigInteger.Parse(b.Pre[i])) : na != nb ? (na ? -1 : 1) : string.CompareOrdinal(a.Pre[i], b.Pre[i]);
            }
            if (compare == 0) compare = a.Pre.Length.CompareTo(b.Pre.Length);
        }
        return $"{v[0].Trim()} {(compare < 0 ? "<" : compare > 0 ? ">" : "=")} {v[1].Trim()}";
    }
    private static int FloorDiv(int n, int d) => (int)Math.Floor((double)n / d);
    private static string Chunk(IReadOnlyList<string> v)
    {
        int x = I(v, 0, -30000000, 30000000), z = I(v, 1, -30000000, 30000000), cx = FloorDiv(x, 16), cz = FloorDiv(z, 16);
        return $"区块：{cx}, {cz}\n区块内：{x - cx * 16}, {z - cz * 16}\nX 范围：{cx * 16} … {cx * 16 + 15}\nZ 范围：{cz * 16} … {cz * 16 + 15}";
    }
    private static string Region(IReadOnlyList<string> v)
    {
        int x = I(v, 0, -2000000, 2000000), z = I(v, 1, -2000000, 2000000), rx = FloorDiv(x, 32), rz = FloorDiv(z, 32);
        return $"文件：r.{rx}.{rz}.mca\n区域内区块：{x - rx * 32}, {z - rz * 32}\n位置表索引：{(x - rx * 32) + (z - rz * 32) * 32}\n方块 X：{rx * 512} … {rx * 512 + 511}\n方块 Z：{rz * 512} … {rz * 512 + 511}";
    }
    private static string RegionFile(string text)
    {
        var m = Regex.Match(text.Trim(), @"^r\.(-?\d+)\.(-?\d+)\.mca$");
        if (!m.Success) throw E(0, "请输入 r.x.z.mca 形式的区域文件名。");
        var coords = new[] { m.Groups[1].Value, m.Groups[2].Value }; long x = I(coords, 0, -62500, 62500), z = I(coords, 1, -62500, 62500);
        return $"区块 X：{x * 32} … {x * 32 + 31}\n区块 Z：{z * 32} … {z * 32 + 31}\n方块 X：{x * 512} … {x * 512 + 511}\n方块 Z：{z * 512} … {z * 512 + 511}";
    }
    private static string FillVolume(IReadOnlyList<string> v)
    {
        var a = Vector(v[0], 3, 0, true); var b = Vector(v[1], 3, 1, true);
        var sizes = a.Select((x, i) => new BigInteger(Math.Abs(x - b[i]) + 1)).ToArray();
        BigInteger total = sizes.Aggregate(BigInteger.One, (x, y) => x * y), inside = sizes.Aggregate(BigInteger.One, (x, y) => x * BigInteger.Max(0, y - 2));
        return $"尺寸：{string.Join(" × ", sizes)}\n实心方块：{total}\n外壳方块：{total - inside}\n内部方块：{inside}\n此结果仅计算用量；命令填充上限取决于游戏版本与配置。";
    }
    private static long Disk(int radius, int y = 0)
    {
        long sum = 0; int remaining = radius * radius - y * y;
        for (int z = -(int)Math.Sqrt(remaining); z <= (int)Math.Sqrt(remaining); z++) sum += 2 * (int)Math.Floor(Math.Sqrt(remaining - z * z)) + 1;
        return sum;
    }
    private static string Sphere(int radius)
    {
        var rows = Enumerable.Range(-radius, 2 * radius + 1).Select(y => (Y: y, Count: Disk(radius, y))).ToArray();
        return $"实心球总用量：{rows.Sum(r => r.Count)}\n直径：{2 * radius + 1}\n" + Join(rows.Select(r => $"Y={r.Y}: {r.Count} 方块"));
    }
    private static string Circle(int radius, int height)
    {
        return $"底面方块：{Disk(radius)}\n高度：{height}\n总用量：{Disk(radius) * height}\n" + Join(Enumerable.Range(-radius, radius * 2 + 1).Select(z => { int half = (int)Math.Floor(Math.Sqrt(radius * radius - z * z)); return $"Z={z}: X={-half} … {half}（{2 * half + 1} 方块）"; }));
    }
    private static string PixelLine(IReadOnlyList<string> v)
    {
        var a = Vector(v[0], 2, 0, true); var b = Vector(v[1], 2, 1, true);
        int x = (int)a[0], z = (int)a[1], endX = (int)b[0], endZ = (int)b[1], dx = Math.Abs(endX - x), dz = Math.Abs(endZ - z);
        if (Math.Max(dx, dz) > 10000) throw E(1, "最多生成 10001 个点，请缩短线段。");
        int sx = x < endX ? 1 : -1, sz = z < endZ ? 1 : -1, error = dx - dz; var points = new List<string>();
        while (true) { points.Add($"{x} {z}"); if (x == endX && z == endZ) break; int twice = 2 * error; if (twice > -dz) { error -= dz; x += sx; } if (twice < dx) { error += dx; z += sz; } }
        return Join(points);
    }
    private static string Border(IReadOnlyList<string> v)
    {
        var center = Vector(v[0], 2, 0); double size = N(v, 1, 1, 60000000);
        return $"X：{S(center[0] - size / 2)} … {S(center[0] + size / 2)}\nZ：{S(center[1] - size / 2)} … {S(center[1] + size / 2)}\n平面面积：{S(size * size)} 平方方块";
    }
    private static string TickBudget(IReadOnlyList<string> v)
    {
        double target = N(v, 0, 0.01, 1000), ms = N(v, 1, 0.000001, 1e9), budget = 1000 / target;
        return $"目标每 tick 预算：{S(budget)} ms\n预算余量：{S(budget - ms)} ms\n计算理论 TPS：{S(Math.Min(target, 1000 / ms))}\n预算占用：{S(ms / budget * 100)}%";
    }
    private static string Fuel(IReadOnlyList<string> v)
    {
        int count = I(v, 0, 0, 1000000000); double capacity = N(v, 1, 0.000001, 1e9), time = N(v, 2, 0, 1e9);
        return $"燃料份数（向上取整）：{S(Math.Ceiling(count / capacity))}\n额外可熔炼量：{S(Math.Ceiling(count / capacity) * capacity - count)}\n单炉理想耗时：{Duration(count * time)}";
    }
    private static string Craft(IReadOnlyList<string> v)
    {
        long needed = I(v, 0, 0, 1000000000), output = I(v, 1, 1, 1000000000), batches = (needed + output - 1) / output;
        return $"批次：{batches}\n成品总数：{batches * output}\n多余：{batches * output - needed}";
    }
    private static string Recipe(IReadOnlyList<string> v)
    {
        using var doc = Json(v[0]); if (doc.RootElement.ValueKind != JsonValueKind.Object) throw E(0, "请输入材料名到非负整数用量的 JSON 对象。");
        long output = I(v, 1, 1, 1000000000), target = I(v, 2, 0, 1000000000), batches = (target + output - 1) / output; var lines = new List<string> { $"批次：{batches}，产量：{batches * output}" };
        foreach (var p in doc.RootElement.EnumerateObject()) { if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt64(out long n) || n < 0 || n > 1000000000) throw E(0, $"{p.Name} 的用量须为 0–1000000000 整数。"); lines.Add($"{p.Name}: {new BigInteger(n) * batches}"); }
        return Join(lines);
    }
    private static string Rotate(IReadOnlyList<string> v)
    {
        var point = Vector(v[0], 2, 0); var origin = Vector(v[1], 2, 1); double angle = N(v, 2) % 360 * Math.PI / 180, x = point[0] - origin[0], z = point[1] - origin[1];
        return $"{S(origin[0] + x * Math.Cos(angle) - z * Math.Sin(angle))} {S(origin[1] + x * Math.Sin(angle) + z * Math.Cos(angle))}";
    }
    private static string Offset(IReadOnlyList<string> v)
    {
        var offset = Vector(v[1], 3, 1);
        return Join(Lines(v[0]).Where(x => !string.IsNullOrWhiteSpace(x)).Select(line => string.Join(" ", Vector(line, 3, 0).Select((x, i) => S(x + offset[i])))));
    }
    private static (int R, int G, int B, string Hex) Color(string text, int field)
    {
        string hex = text.Trim(); if (!Regex.IsMatch(hex, "^#[0-9a-fA-F]{6}$")) throw E(field, "颜色需使用 #RRGGBB 格式。");
        return (Convert.ToInt32(hex.Substring(1, 2), 16), Convert.ToInt32(hex.Substring(3, 2), 16), Convert.ToInt32(hex.Substring(5, 2), 16), hex.ToLowerInvariant());
    }
    private static string Gradient(IReadOnlyList<string> v)
    {
        var chars = v[0].EnumerateRunes().Select(x => x.ToString()).ToArray(); if (chars.Length > 500) throw E(0, "渐变文字最多 500 个 Unicode 码点。");
        var a = Color(v[1], 1); var b = Color(v[2], 2);
        return "/tellraw @a " + JsonSerializer.Serialize(chars.Select((text, i) => { double t = chars.Length == 1 ? 0 : (double)i / (chars.Length - 1); return new { text, color = $"#{(int)Math.Round(a.R + (b.R - a.R) * t):x2}{(int)Math.Round(a.G + (b.G - a.G) * t):x2}{(int)Math.Round(a.B + (b.B - a.B) * t):x2}" }; }));
    }
    private static string ResourceId(string text)
    {
        string id = text.Trim(); if (!id.Contains(':')) id = "minecraft:" + id;
        if (!Regex.IsMatch(id, @"^[a-z0-9_.-]+:[a-z0-9_./-]+$")) throw E(0, "命名空间仅允许小写字母、数字、_、.、-；路径还允许 /，且两部分均不能为空。");
        return "格式有效：" + id + "\n仅验证格式，不代表目标版本一定存在该资源。";
    }
    private static string LintProperties(string text)
    {
        var messages = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal); int number = 0;
        var booleans = new HashSet<string>(new[] { "online-mode", "white-list", "pvp", "enable-command-block", "allow-flight", "enable-rcon", "enable-query", "enforce-whitelist", "hardcore", "spawn-monsters", "allow-nether", "hide-online-players" }, StringComparer.Ordinal);
        foreach (var line in Lines(text))
        {
            number++; if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#') || line.TrimStart().StartsWith('!')) continue;
            int separator = line.IndexOf('='); if (separator < 1) { messages.Add($"第 {number} 行：应使用 key=value。"); continue; }
            string key = line[..separator].Trim(), value = line[(separator + 1)..].Trim();
            if (!seen.Add(key)) messages.Add($"第 {number} 行：重复键 {key}，后面的值可能覆盖前面的值。");
            if (booleans.Contains(key) && value is not ("true" or "false")) messages.Add($"第 {number} 行：{key} 只能为 true 或 false。");
            if (key is "server-port" or "query.port" or "rcon.port") { if (!int.TryParse(value, out var port) || port < 1 || port > 65535) messages.Add($"第 {number} 行：{key} 应为 1–65535。"); }
            if (key is "max-players" or "view-distance" or "simulation-distance") { if (!int.TryParse(value, out var n) || n < 1) messages.Add($"第 {number} 行：{key} 应为正整数。"); }
        }
        return messages.Count == 0 ? "未发现以上规则覆盖的静态问题。未知设置或版本兼容性仍需由服务器验证。" : Join(messages);
    }
    private static string Whitelist(string text)
    {
        var list = new List<object>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var ids = new HashSet<Guid>();
        foreach (string line in Lines(text).Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var parts = Regex.Split(line.Trim(), @"\s+");
            if (parts.Length != 2 || !Regex.IsMatch(parts[0], "^[A-Za-z0-9_]{1,16}$") || !Guid.TryParse(parts[1], out var id)) throw E(0, "每行须为 1–16 位玩家名和有效 UUID，以空格分隔。");
            if (!names.Add(parts[0]) || !ids.Add(id)) throw E(0, "玩家名或 UUID 重复，请先核实身份。");
            list.Add(new { uuid = id.ToString(), name = parts[0] });
        }
        return Serialize(list);
    }
    private static string Contrast(IReadOnlyList<string> v)
    {
        double L((int R, int G, int B, string Hex) c) { double C(int n) { double x = n / 255.0; return x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4); } return .2126 * C(c.R) + .7152 * C(c.G) + .0722 * C(c.B); }
        double a = L(Color(v[0], 0)), b = L(Color(v[1], 1)), ratio = (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
        return $"对比度：{S(ratio)}:1\n4.5:1 参考阈值：{(ratio >= 4.5 ? "达到" : "未达到")}\n3:1 参考阈值：{(ratio >= 3 ? "达到" : "未达到")}";
    }
    private static string Weights(string text)
    {
        var weights = Properties(text).Select(p => (p.Key, Weight: Number(p.Value, 0, 0, 1e12))).ToArray(); double total = weights.Sum(p => p.Weight);
        if (total <= 0) throw E(0, "权重总和必须大于零。");
        return $"总权重：{S(total)}\n" + Join(weights.Select(p => $"{p.Key}: {S(p.Weight / total * 100)}%"));
    }
}
