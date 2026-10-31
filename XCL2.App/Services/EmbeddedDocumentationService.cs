using System.IO;

namespace XCL2.App.Services;


public static class EmbeddedDocumentationService
{
    public const string ApiFileName = "API_REFERENCE.md";
    public const string PluginExeProtocolFileName = "PLUGIN_EXE_PROTOCOL.md";
    public const string PluginGuideFileName = "PLUGIN_GUIDE.md";
    public const string LauncherControlFileName = "API_LAUNCHER_CONTROL.md";
    public const string ApiExamplesFileName = "API_EXAMPLES.md";
    public const string WorldsApiFileName = "API_WORLDS.md";
    public const string CompositionApiFileName = "API_LAUNCHER_COMPOSITION.md";

    public static string GetTitle(string fileName) => fileName switch
    {
        ApiFileName => "启动器 API 接入说明",
        PluginExeProtocolFileName => "EXE 插件协议",
        PluginGuideFileName => "插件开发指南",
        LauncherControlFileName => "启动器控制与界面定制 API",
        ApiExamplesFileName => "API 完整样例与常见错误",
        WorldsApiFileName => "存档读写 API",
        CompositionApiFileName => "组合自己的启动器界面",
        _ => throw new ArgumentException("未知的帮助文档。", nameof(fileName))
    };

    private static Stream OpenResource(string fileName)
    {
        _ = GetTitle(fileName);
        var assembly = typeof(EmbeddedDocumentationService).Assembly;
        

        var stream = assembly.GetManifestResourceStream("XCL2.App.Docs." + fileName);
        if (stream == null && string.Equals(fileName, PluginGuideFileName, StringComparison.Ordinal))
            stream = assembly.GetManifestResourceStream("XCL2.App.PLUGIN_GUIDE.md");
        return stream ?? throw new FileNotFoundException("未找到内置帮助文档：" + fileName);
    }

    public static string Read(string fileName)
    {
        using var stream = OpenResource(fileName);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public static void Export(string fileName, string destination)
    {
        using var stream = OpenResource(fileName);
        using var output = File.Create(destination);
        stream.CopyTo(output);
    }
}
