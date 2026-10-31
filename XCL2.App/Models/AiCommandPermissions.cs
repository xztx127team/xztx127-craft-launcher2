using System;
using System.Collections.Generic;

namespace XCL2.App.Models;

/// <summary>AI 工具的类别与具体操作权限。未识别命令不会继承读取/创建权限。</summary>
public sealed class AiCommandPermissions
{
    public bool Post { get; set; } = true;
    public bool PowerShell { get; set; } = true;
    public bool Shell { get; set; } = true;
    public bool Cmd { get; set; } = true;
    public bool Python { get; set; } = true;
    public bool Read { get; set; } = true;
    public bool Create { get; set; } = true;
    public bool Modify { get; set; } = true;
    public bool Delete { get; set; }
    public bool OpenWindow { get; set; }
    public bool CloseWindow { get; set; }
    public bool MouseMove { get; set; }
    public bool MouseClick { get; set; }
    public bool MouseRightClick { get; set; }
    public bool Keyboard { get; set; }
    public bool ShellCommand { get; set; }
    public bool Other { get; set; }
    public bool RunPython { get; set; } = true;

    /// <summary>完整命令的独立许可；键由 AiCommandPermissionService.GetCommandKey 生成。
    /// false 始终拒绝，true 不能覆盖关闭的鼠标/键盘/窗口权限。</summary>
    public Dictionary<string, bool> ExactCommands { get; set; } = new(StringComparer.Ordinal);

    public AiCommandPermissions Clone()
    {
        var copy = (AiCommandPermissions)MemberwiseClone();
        copy.ExactCommands = ExactCommands == null
            ? new Dictionary<string, bool>(StringComparer.Ordinal)
            : new Dictionary<string, bool>(ExactCommands, StringComparer.Ordinal);
        return copy;
    }
}
