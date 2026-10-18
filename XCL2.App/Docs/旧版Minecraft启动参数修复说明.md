# 旧版 Minecraft 启动参数修复

1.0 官方元数据使用 `玩家名 会话 --gameDir ... --assetsDir ...` 这种位置参数。原实现先拼入现代 `--username` 等选项，再追加旧模板，而且没有替换旧模板中的玩家名、游戏目录等变量。Launchwrapper 会把未识别的参数交给旧客户端，导致身份位置错位、占位符原样传入。

现在仅在元数据明确使用位置身份参数时，按旧模板原有顺序启动，不再插入现代客户端的身份、窗口、Quick Play 等选项。1.6+ 使用 `--username` 的旧 Forge / OptiFine 模板、现代客户端参数和用户自定义 JVM 参数继续使用原有路径。旧模板出现不完整的引号时，直接报告格式错误，不静默丢弃模板。

同时补全旧资源布局：资源索引声明 `virtual` 时创建带名字的虚拟资源文件；声明 `map_to_resources` 时映射到实际游戏目录的 `resources`，支持实例隔离。新资源索引不执行旧资源复制。

Java 自动匹配优先读取版本 JSON 及继承链声明；缺失时使用 JSON 内的真实版本号估算，文件夹改名不再丢失旧版 Java 8 要求。客户端回退表区分 1.17 的 Java 16、1.18–1.20.4 的 Java 17、1.20.5+ 的 Java 21；Java 16 仍可按已有规则使用 Java 17。已声明的 Java 版本和用户明确选定的运行时仍优先。

实际 Java 位数从可执行文件的 PE 头读取，不使用启动器自身位数推断。32 位 Java 配置了过大的堆内存时给出提示；不覆盖用户显式自定义的 `-Xmx`。

回归项目：`Tests/LegacyLaunchRegression/LegacyLaunchRegression.csproj`。当前环境缺少 .NET SDK，且环境自带 Java 因缺少 `libjli.so` 无法运行；已执行源码静态检查，未进行 C# 编译或 Windows 游戏启动验证。

核对来源：

- [Mojang 官方 1.0 版本元数据](https://piston-meta.mojang.com/v1/packages/75062586b830dd5160f13f1c9130eb365e01f1b9/1.0.json)
- [Mojang LegacyLauncher 的启动入口](https://github.com/Mojang/LegacyLauncher/blob/master/src/main/java/net/minecraft/launchwrapper/Launch.java)
- [Minecraft 1.20.5 官方发布说明](https://www.minecraft.net/fr-ca/article/minecraft-java-edition-1-20-5)
- [Microsoft PE 文件格式说明](https://learn.microsoft.com/en-us/windows/win32/debug/pe-format)
