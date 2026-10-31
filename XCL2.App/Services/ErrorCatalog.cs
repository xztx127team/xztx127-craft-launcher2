using System.Text.RegularExpressions;

namespace XCL2.App.Services;

public sealed record ErrorDefinition(string Code, string Title, string Cause, string Guidance, params string[] Evidence);

public static class ErrorCatalog
{
    public static IReadOnlyList<ErrorDefinition> All { get; } = new ErrorDefinition[]
    {
        new("XCL-EXP-001", "模拟启动器崩溃", "实验面板主动触发了崩溃演示。", "关闭模拟报告即可继续使用；真实程序和游戏进程未终止。", "[XCL-EXP-001]"),
        new("XCL-EXP-002", "模拟后台异常", "实验任务主动抛出了后台异常。", "关闭报告后可继续其他实验。", "[XCL-EXP-002]"),
        new("XCL-MEM-001", "托管内存不足", "启动器无法继续分配所需的托管内存。", "关闭不用的应用；在 64 位系统上使用 64 位启动器。保留报告并检查内存占用持续增长的操作。", "outofmemoryexception", "托管内存不足"),
        new("XCL-JVM-001", "JVM 堆预留失败", "Java 无法预留指定大小的堆。", "核对 -Xms/-Xmx；32 位 Java 降低分配，或在 64 位系统上改用 64 位 Java。", "could not reserve enough space", "invalid maximum heap size"),
        new("XCL-JVM-002", "Java 字节码版本不兼容", "所选 Java 版本低于游戏或模组编译目标。", "在实例设置中选择符合游戏要求的 Java 主版本。", "unsupportedclassversionerror", "unsupported class file major version"),
        new("XCL-JVM-003", "Java 参数不受支持", "启动参数不适用于当前 Java 运行时。", "检查自定义 JVM 参数，移除已失效或拼写错误的选项后重试。", "unrecognized vm option", "unrecognized option", "could not create the java virtual machine"),
        new("XCL-JVM-004", "Java 运行时内存不足", "Java 堆、元空间或本机内存分配失败。", "查看异常中标明的内存区域；堆不足时在物理内存允许的范围内调整 -Xmx，原生内存不足时减少堆分配和并行任务，并排查模组持续占用。", "outofmemoryerror", "java heap space", "gc overhead limit exceeded"),
        new("XCL-AUTH-001", "登录凭据失效", "账户凭据过期、被撤销或不正确。", "重新登录对应账户；自定义服务检查凭据是否正确。", "invalid_grant", "invalid_token", "invalid credentials", "401", "登录已过期"),
        new("XCL-AUTH-002", "服务拒绝授权", "当前账户没有访问目标资源的权限。", "确认账户的资源权限、游戏所有权或接口授权。", "403", "forbidden", "xsts"),
        new("XCL-NET-001", "域名解析失败", "系统无法把域名解析为地址。", "检查域名拼写、网络和 DNS；必要时切换下载源。", "socketexception (11001)", "no such host", "name or service not known", "dns", "找不到主机"),
        new("XCL-NET-002", "连接或请求超时", "远端未在限定时间内响应。", "检查网络和代理；稍后重试或选择其他下载源。", "timed out", "timeout", "超时", "taskcanceledexception"),
        new("XCL-NET-003", "TLS 证书验证失败", "无法建立可信的加密连接。", "核对系统日期、证书与代理设置；保留证书验证。", "ssl", "tls", "certificate", "证书"),
        new("XCL-NET-004", "服务器限制请求频率", "请求速率超过远端限制。", "等待服务器指定的重试时间，并减少并行请求数。", "429", "rate limit", "too many requests"),
        new("XCL-NET-005", "远端资源不存在", "服务器没有找到所请求的资源。", "核对版本和地址；重新获取版本清单，或切换下载源。", "404", "410"),
        new("XCL-NET-006", "远端服务故障", "远端服务内部错误或暂不可用。", "稍后重试；必要时更换可用的官方或镜像节点。", "500", "502", "503", "504", "bad gateway", "service unavailable"),
        new("XCL-NET-007", "连接被拒绝或重置", "目标端口未提供服务，或连接中途断开。", "检查服务是否运行、端口、代理、防火墙与网络连接。", "connection refused", "connection reset", "actively refused", "10061", "10054"),
        new("XCL-FILE-001", "文件访问被拒绝", "文件或目录不允许当前用户读写。", "检查目录权限和只读属性，改用当前用户可写的目录。", "unauthorizedaccessexception", "access is denied", "拒绝访问", "权限不足"),
        new("XCL-FILE-002", "文件或目录缺失", "需要的本地文件已移动、删除或尚未下载。", "重新选择文件位置，或下载缺失组件；存档优先从备份恢复。", "filenotfoundexception", "directorynotfoundexception", "找不到文件", "文件不存在", "目录不存在"),
        new("XCL-FILE-003", "磁盘空间不足", "目标卷没有足够的可用空间。", "清理不需要的文件或选择空间充足的磁盘后重试。", "disk full", "not enough space on the disk", "0x80070070", "磁盘空间不足"),
        new("XCL-FILE-004", "文件正被占用", "其他进程持有目标文件的独占访问锁。", "停止使用该文件的游戏或编辑器后重试。", "being used by another process", "sharing violation", "0x80070020", "文件被占用"),
        new("XCL-DATA-001", "JSON 内容无效", "配置或远端响应不是有效的 JSON。", "检查 JSON 语法；远端响应异常时重试；配置损坏时使用备份恢复。", "jsonexception", "jsonreaderexception", "json 格式", "版本 json 与所选版本不一致"),
        new("XCL-DATA-002", "文件校验失败", "文件长度或哈希与来源记录不一致。", "删除未完成的下载缓存并重新下载，必要时切换来源。", "hash mismatch", "checksum", "sha1 mismatch", "哈希", "校验失败", "校验不匹配"),
        new("XCL-DATA-003", "压缩包损坏", "归档结构或压缩数据不完整。", "重新下载归档；保留原始存档备份。", "end of central directory", "central directory", "invaliddataexception", "压缩包损坏"),
        new("XCL-CFG-001", "设置值超出范围", "输入内容与设置允许的格式或范围不符。", "按输入提示修正格式、单位和范围后重新保存。", "argumentoutofrangeexception", "formatexception", "输入无效"),
        new("XCL-ARCH-001", "程序架构不兼容", "运行文件的 CPU 架构与当前 Windows 不匹配。", "32 位 Windows 选择 x86 程序；64 位程序不能通过重命名变成 32 位。", "badimageformatexception", "not a valid win32", "0x800700c1", "架构不兼容", "不是有效的 win32"),
        new("XCL-LAN-001", "陶瓦缺少兼容的本体", "构建没有携带适用于当前系统架构的 Terracotta。", "在陶瓦设置选择与系统匹配的官方可执行文件；上游无 x86 发行版时，32 位系统无法启动该本体。", "[XCL-LAN-001]", "没有携带内置陶瓦"),
        new("XCL-LAN-002", "陶瓦接口握手失败", "联机本体启动后没有提供有效的本地接口。", "检查本体版本、端口文件和联机日志；关闭旧服务后重试。", "陶瓦进程未能启动", "陶瓦服务启动超时", "端口文件"),
        new("XCL-MOD-001", "模组或加载器依赖冲突", "游戏、加载器和模组的版本或依赖不匹配。", "核对加载器版本及模组依赖；备份实例后停用最近添加的模组排查。", "missing mandatory dependencies", "mod dependency missing", "incompatible mod", "模组冲突", "缺失依赖"),
        new("XCL-APP-001", "操作已取消", "任务由用户或上层流程取消。", "需要继续时重新发起操作。", "operationcanceledexception", "操作已取消"),
        new("XCL-APP-999", "未分类的操作失败", "故障尚未匹配到专用分类；技术详情中保留了实际异常。", "记录复现步骤，复制脱敏报告并附上报告编号反馈。"),
    };

    public static ErrorDefinition Find(string evidence)
    {
        var explicitCode = All.FirstOrDefault(item => evidence.Contains("[" + item.Code + "]", StringComparison.Ordinal));
        if (explicitCode != null) return explicitCode;
        foreach (var (type, code) in new[]
        {
            ("OutOfMemoryException", "XCL-MEM-001"), ("BadImageFormatException", "XCL-ARCH-001"),
            ("UnauthorizedAccessException", "XCL-FILE-001"), ("FileNotFoundException", "XCL-FILE-002"),
            ("DirectoryNotFoundException", "XCL-FILE-002"), ("JsonException", "XCL-DATA-001"),
            ("JsonReaderException", "XCL-DATA-001")
        })
            if (evidence.Contains(type, StringComparison.OrdinalIgnoreCase)) return All.Single(item => item.Code == code);
        foreach (var definition in All)
            foreach (var marker in definition.Evidence)
            {
                bool match = marker.All(char.IsDigit)
                    ? Regex.IsMatch(evidence, @"(?<![\w])" + Regex.Escape(marker) + @"(?![\w])")
                    : evidence.Contains(marker, StringComparison.OrdinalIgnoreCase);
                if (match) return definition;
            }
        return All[^1];
    }
}
