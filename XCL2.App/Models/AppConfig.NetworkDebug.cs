namespace XCL2.App.Models;

public partial class AppConfig
{
    [ExperiencePreference("诊断与日志", "网络请求调试消息", Description = "在左下角显示启动器 HTTP 请求、响应状态和耗时；不显示密钥、查询参数或请求正文。")]
    public bool NetworkDebugEnabled { get; set; }
}
