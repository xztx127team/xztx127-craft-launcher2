using System.Windows.Controls;

namespace XCL2.App.Services.Plugins;

public interface IPlugin
{
    /// <summary>插件唯一 ID，建议用"作者名.插件名"这种反向域名风格的短字符串，
    /// 例如 "someone.better-skin-tools"。该 ID 同时也是配置文件、日志前缀的 key，
    /// 上线之后不要再改，否则等于变成了一个新插件（旧配置对不上）。</summary>
    string Id { get; }

    /// <summary>展示给用户看的插件名称，随时可以改，不影响配置归属。</summary>
    string DisplayName { get; }

    /// <summary>插件版本号，纯展示用，建议跟随语义化版本（1.0.0 这种）。</summary>
    string Version { get; }

    /// <summary>一两句话说明该插件是干什么的，显示在插件管理列表里。</summary>
    string Description { get; }

    /// <summary>插件作者，展示用，可以留空字符串。</summary>
    string Author { get; }

    void Initialize(PluginContext ctx);

    /// <summary>
    /// 插件被用户关闭，或者启动器正常退出时调用一次，用来释放插件自己占用的资源
    /// （文件句柄、定时器、后台线程等）。如果插件没有需要清理的东西，留空实现即可。
    /// </summary>
    void Shutdown();

    /// <summary>
    /// 可选：返回一个插件自己的配置界面（一个 UserControl），显示在"插件管理"里
    /// 该插件条目的"配置"按钮之后。不需要图形配置界面的插件（比如只是后台跑点逻辑的）
    /// 可以直接返回 null——这时"配置"按钮会改成显示一个通用的、基于 PluginContext.Config
    /// 里已保存键值对的只读 JSON 预览，方便用户至少能看到当前保存了什么。
    /// </summary>
    UserControl? CreateSettingsPanel(PluginContext ctx) => null;
}

public interface IPluginSettingsPanel
{
    /// <summary>把面板当前的输入状态批量写入 PluginContext.Config。抛出异常会被宿主弹窗
    /// 捕获并以警告形式提示用户，弹窗不会关闭，方便用户看到哪里没填对再改。</summary>
    void Save();
}
