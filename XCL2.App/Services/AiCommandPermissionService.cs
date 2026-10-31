using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>在本地从真实工具名和命令文本判断权限，不使用模型提供的分类或说明。
/// 分类不能代替操作系统沙盒；无法静态识别的脚本与复合命令需要「其他 / Shell 命令」授权。</summary>
public static class AiCommandPermissionService
{
    [Flags]
    private enum Operation
    {
        None = 0, Read = 1, Create = 2, Modify = 4, Delete = 8, OpenWindow = 16,
        CloseWindow = 32, MouseMove = 64, MouseClick = 128, MouseRightClick = 256,
        Keyboard = 512, ShellCommand = 1024, Other = 2048, RunPython = 4096
    }

    private sealed record Inspection(Operation Operations, bool UsesPython, bool SafePython);
    private const Operation Unknown = Operation.Other | Operation.ShellCommand;
    private const string NoWorkspace = "系统：工作目录为「无」或已经不可用时不能执行命令或操作电脑。请在 AI 助手设置中选择有效工作目录，或开启「完全访问 / 默许所有 Shell 指令」。";

    public static string? GetAccessDenial(AiAssistantConfig? config, string? action, string? path, AiWorkMode? workMode = null)
    {
        if (config == null) return "系统：AI 助手权限设置不可用，未执行。";
        var name = (action ?? "").Trim().ToLowerInvariant();
        if ((workMode ?? AiWorkModeContext.ResolveCurrentMode(config.WorkMode)) == AiWorkMode.Chat)
            return name == "read_log" && config.AllowCrashLogReading ? null : AiWorkModePolicy.ChatDeniedMessage;
        if (name == "run_command") return CommandsWithoutWorkspaceDenied(config) ? NoWorkspace : null;
        if (name is "list_workspace" or "read_workspace_file" or "read_file_range" or "list_project_files"
            or "search_project" or "write_workspace_file" or "create_workspace_file" or "patch_workspace_file"
            or "delete_workspace_file" or "create_directory" or "delete_directory" or "request_directory"
            or "window_open" or "open_window")
        {
            var pathDenial = GetStructuredPathDenial(config, path, name == "request_directory");
            if (pathDenial != null) return "系统：" + pathDenial;
        }
        var operations = name switch
        {
            "list_workspace" or "read_workspace_file" or "read_file_range" or "list_project_files"
                or "search_project" or "read_log" or "read_config" or "registry_read" or "request_directory" => Operation.Read,
            "write_workspace_file" => WorkspaceFileExists(config, path) ? Operation.Modify : Operation.Create,
            "create_workspace_file" or "create_directory" => Operation.Create,
            "patch_workspace_file" or "apply_setting" or "registry_write" => Operation.Modify,
            "delete_workspace_file" or "delete_directory" => Operation.Delete,
            "window_open" or "open_window" => Operation.OpenWindow,
            "window_close" or "close_window" => Operation.CloseWindow,
            "mouse_move" => Operation.MouseMove,
            "mouse_click" => Operation.MouseClick,
            "mouse_right_click" => Operation.MouseRightClick,
            "key_press" or "keyboard" or "keyboard_input" or "keyboard_type" => Operation.Keyboard,
            _ => Operation.Other
        };
        if (HasComputerOperation(operations) && CommandsWithoutWorkspaceDenied(config)) return NoWorkspace;
        var permissions = config.CommandPermissions ?? new AiCommandPermissions();
        if (!permissions.Post) return Denied("Post 工具");
        return GetOperationDenial(permissions, operations);
    }

    /// <summary>完整字符串比较保留命令的大小写、内部空白和引号，避免改变脚本/字符串的语义。</summary>
    public static string GetCommandKey(string? shell, string? command) =>
        NormalizeShell(shell) + "|" + (command ?? "").Trim();

    public static string? GetCommandDenial(AiAssistantConfig? config, string? shell, string? command, string? workingDir, AiWorkMode? workMode = null)
    {
        var hardDenial = GetCommandApprovalDenial(config, shell, command, workingDir, workMode);
        if (hardDenial != null) return hardDenial;
        var grantedConfig = config!;
        var permissions = grantedConfig.CommandPermissions ?? new AiCommandPermissions();
        var inspection = InspectCommand(shell, command!, workingDir);
        if (permissions.ExactCommands?.TryGetValue(GetCommandKey(shell, command), out var explicitAllow) == true)
            return explicitAllow ? null : "系统：此完整命令已在 Shell 设置中被单独禁止，未执行。";
        if (grantedConfig.AllowAllShellCommands) return null;
        if (!grantedConfig.AllowShellCommands && !IsPreauthorizedPython(grantedConfig, shell, command, workingDir, workMode))
            return "系统：此 Shell 命令尚未预授权，需要用户批准本次具体命令；工作区内受限 Python 指令沿用默认预授权。";
        var categoryDenial = NormalizeShell(shell) switch
        {
            "powershell" when !permissions.PowerShell => Denied("PowerShell"),
            "cmd" when !permissions.Cmd => Denied("CMD"),
            "python" when !permissions.Python => Denied("Python"),
            "shell" when !permissions.Shell => Denied("Shell"),
            "powershell" or "cmd" or "python" or "shell" => null,
            _ => permissions.Other && permissions.ShellCommand ? null : Denied("其他 / Shell 命令")
        };
        if (categoryDenial != null) return categoryDenial;
        if (inspection.UsesPython && !permissions.Python) return Denied("Python");
        return GetOperationDenial(permissions, inspection.Operations);
    }

    /// <summary>单次 Shell 审批也不能覆盖工作模式、目录、精确禁止和关闭的电脑操作能力。</summary>
    public static string? GetCommandApprovalDenial(AiAssistantConfig? config, string? shell, string? command, string? workingDir, AiWorkMode? workMode = null)
    {
        if (config == null) return "系统：AI 助手权限设置不可用，未执行。";
        var mode = workMode ?? AiWorkModeContext.ResolveCurrentMode(config.WorkMode);
        if (!AiWorkModePolicy.Allows(mode, "run_command")) return AiWorkModePolicy.GetDeniedMessage(mode);
        if (string.IsNullOrWhiteSpace(command)) return "系统：命令不能为空，未执行。";
        if (CommandsWithoutWorkspaceDenied(config)) return NoWorkspace;
        var permissions = config.CommandPermissions ?? new AiCommandPermissions();
        var inspection = InspectCommand(shell, command, workingDir);
        var computerDenial = GetOperationDenial(permissions, ComputerOperations(inspection.Operations));
        if (computerDenial != null) return computerDenial;
        if (permissions.ExactCommands?.TryGetValue(GetCommandKey(shell, command), out var explicitAllow) == true && !explicitAllow)
            return "系统：此完整命令已在 Shell 设置中被单独禁止，未执行。";
        return null;
    }

    /// <summary>仅限隔离解释器中的受限纯计算程序。目录内脚本并不天然安全；不能因 .py 后缀批准任意代码。</summary>
    public static bool IsPreauthorizedPython(AiAssistantConfig? config, string? shell, string? command, string? workingDir, AiWorkMode? workMode = null) =>
        TryGetPreauthorizedPythonSource(config, shell, command, workingDir, out _, workMode);

    
    public static bool TryGetPreauthorizedPythonSource(AiAssistantConfig? config, string? shell, string? command,
        string? workingDir, out string source, AiWorkMode? workMode = null)
    {
        source = "";
        if (config == null || !AiWorkModePolicy.Allows(workMode ?? AiWorkModeContext.ResolveCurrentMode(config.WorkMode), "run_command") || string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(config.WorkingDirectory)) return false;
        var permissions = config.CommandPermissions ?? new AiCommandPermissions();
        if (!permissions.Python || !permissions.RunPython) return false;
        var kind = NormalizeShell(shell);
        if (kind == "powershell" && !permissions.PowerShell || kind == "cmd" && !permissions.Cmd
            || kind == "shell" && !permissions.Shell) return false;
        if (permissions.ExactCommands?.TryGetValue(GetCommandKey(shell, command), out var allowed) == true && !allowed) return false;
        if (!TryGetWorkspaceDirectory(config.WorkingDirectory, workingDir, out var directory)) return false;
        string? candidate = null;
        if (kind == "python") candidate = command;
        else if ((kind is "powershell" or "cmd" or "shell") && !(kind == "cmd" && command.Contains('\''))
                 && TryTokenizeAtomicCommand(command, out var tokens)
                 && tokens.Count > 0 && IsPythonExecutable(tokens[0].ToLowerInvariant()))
            candidate = TryReadIsolatedPythonScript(tokens, directory);
        if (candidate == null || !IsRestrictedPythonSource(candidate)) return false;
        source = candidate;
        return true;
    }

    private static bool CommandsWithoutWorkspaceDenied(AiAssistantConfig config) =>
        (string.IsNullOrWhiteSpace(config.WorkingDirectory) || !Directory.Exists(config.WorkingDirectory))
        && !config.FullDiskAccess && !config.AllowAllShellCommands;

    // This gate must not probe the filesystem: even File.Exists on a UNC path can
    // initiate Windows network authentication before the ordinary tool confirmation.
    private static string? GetStructuredPathDenial(AiAssistantConfig config, string? path, bool directoryRequest = false)
    {
        var roots = new[] { config.WorkingDirectory ?? "" }
            .Concat(config.WorkingDirectories ?? new List<string>()).Where(root => !string.IsNullOrWhiteSpace(root)).ToArray();
        var denial = AiWorkspacePathPolicy.GetDenial(path, directoryRequest ? null : roots);
        if (denial != null) return denial;
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var resolved = Path.IsPathRooted(path) ? Path.GetFullPath(path)
                : !string.IsNullOrWhiteSpace(config.WorkingDirectory) ? Path.GetFullPath(path, config.WorkingDirectory) : null;
            if (resolved == null) return null;
            return AiWorkspacePathPolicy.GetDenial(resolved, directoryRequest ? null : roots);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        { return "文件路径无效，未执行。"; }
    }

    private static bool WorkspaceFileExists(AiAssistantConfig config, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || GetStructuredPathDenial(config, path) != null) return false;
        try
        {
            var resolved = Path.IsPathRooted(path) ? Path.GetFullPath(path)
                : !string.IsNullOrWhiteSpace(config.WorkingDirectory) ? Path.GetFullPath(path, config.WorkingDirectory) : null;
            if (resolved == null) return false;
            AiWorkspacePathPolicy.EnsureNoLinks(resolved);
            return File.Exists(resolved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    private static string Denied(string label) => "系统：AI 助手设置中的「" + label + "」权限已关闭，未执行。胖虎建议你前往「AI 助手 → Shell / 命令权限」打开对应权限后继续。";

    private static string? GetOperationDenial(AiCommandPermissions p, Operation operations)
    {
        foreach (var (operation, enabled, label) in new[]
        {
            (Operation.MouseMove, p.MouseMove, "鼠标移动"), (Operation.MouseClick, p.MouseClick, "鼠标点击"),
            (Operation.MouseRightClick, p.MouseRightClick, "鼠标右键"), (Operation.Keyboard, p.Keyboard, "键盘"),
            (Operation.OpenWindow, p.OpenWindow, "打开窗口"), (Operation.CloseWindow, p.CloseWindow, "关闭窗口"),
            (Operation.Read, p.Read, "读取"), (Operation.Create, p.Create, "新建"),
            (Operation.Modify, p.Modify, "修改"), (Operation.Delete, p.Delete, "删除"),
            (Operation.RunPython, p.RunPython, "运行 Python"), (Operation.ShellCommand, p.ShellCommand, "Shell 命令"),
            (Operation.Other, p.Other, "其他")
        }) if (operations.HasFlag(operation) && !enabled) return Denied(label);
        return null;
    }

    private static Operation ComputerOperations(Operation operations) => operations &
        (Operation.MouseMove | Operation.MouseClick | Operation.MouseRightClick | Operation.Keyboard | Operation.OpenWindow | Operation.CloseWindow);
    private static bool HasComputerOperation(Operation operations) => ComputerOperations(operations) != Operation.None;

    private static string NormalizeShell(string? shell) => (shell ?? "powershell").Trim().ToLowerInvariant() switch
    {
        "ps" or "pwsh" or "powershell.exe" or "pwsh.exe" or "powershell" => "powershell",
        "cmd.exe" or "cmd" => "cmd",
        "python" or "python.exe" or "python3" or "python3.exe" or "py" or "py.exe" => "python",
        "bash" or "sh" or "zsh" or "shell" => "shell",
        var other => other
    };

    private static Inspection InspectCommand(string? shell, string command, string? directory)
    {
        var computer = DetectComputerOperations(command);
        if (NormalizeShell(shell) == "python")
        {
            var safe = IsRestrictedPythonSource(command);
            return new Inspection(Operation.RunPython | (safe ? Operation.None : computer | Unknown), true, safe);
        }
        var kind = NormalizeShell(shell);
        if (kind == "cmd" && command.Contains('\'') || !TryTokenizeAtomicCommand(command, out var tokens) || tokens.Count == 0)
        {
            var python = Regex.IsMatch(command, @"\b(?:python(?:3)?(?:\.exe)?|py(?:\.exe)?)\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return new Inspection(Unknown | computer | (python ? Operation.RunPython : Operation.None), python, false);
        }
        var executable = tokens[0].Replace('\\', '/').Split('/').Last().ToLowerInvariant();
        if (IsPythonExecutable(executable))
        {
            var source = TryReadIsolatedPythonScript(tokens, directory);
            var safe = source != null && IsRestrictedPythonSource(source);
            var evidence = source ?? TryReadPythonScriptEvidence(tokens, directory);
            if (evidence != null) computer |= DetectComputerOperations(evidence);
            return new Inspection(Operation.RunPython | (safe ? Operation.None : computer | Unknown), true, safe);
        }
        // 路径指向的同名程序不是系统命令；工作目录中的同名可执行文件也不能冒充读取指令。
        if (tokens[0].IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || HasLocalCommandOverride(tokens[0], directory))
            return new Inspection(Unknown | computer, false, false);
        // 明确的单条原生命令可以归到细类；解释器、脚本、网络调用等保持「其他」。
        var operation = GetAtomicOperation(kind, executable);
        return new Inspection(operation | (operation == Unknown ? computer : Operation.None), false, false);
    }

    private static Operation GetAtomicOperation(string shell, string executable)
    {
        // PowerShell 的 cmdlet 名称不是 CMD 内建命令，不能给同名外部程序自动授予读取权。
        if (shell == "cmd") return executable switch
        {
            "dir" or "type" or "ver" or "echo" => Operation.Read,
            "mkdir" or "md" => Operation.Create,
            "copy" => Operation.Create | Operation.Modify,
            "move" => Operation.Modify | Operation.Delete,
            "ren" or "rename" => Operation.Modify,
            "del" or "erase" or "rd" or "rmdir" => Operation.Delete,
            "start" => Operation.OpenWindow,
            // WindowsCommandAdapter 会把这些明确的 POSIX 指令转为 PowerShell，再独立检查实际命令。
            "cat" or "ls" or "pwd" => Operation.Read,
            "cp" => Operation.Create | Operation.Modify,
            "mv" => Operation.Modify | Operation.Delete,
            "rm" => Operation.Delete,
            _ => Unknown
        };
        if (shell == "shell") return executable switch
        {
            "cat" or "ls" or "pwd" or "echo" or "type" => Operation.Read,
            "mkdir" => Operation.Create,
            "touch" => Operation.Create | Operation.Modify,
            "cp" => Operation.Create | Operation.Modify,
            "mv" => Operation.Modify | Operation.Delete,
            "rm" or "rmdir" => Operation.Delete,
            "open" or "xdg-open" => Operation.OpenWindow,
            "kill" or "pkill" => Operation.CloseWindow,
            _ => Unknown
        };
        if (shell != "powershell") return Unknown;
        return executable switch
        {
            "get-content" or "gc" or "cat" or "type" or "get-childitem" or "gci" or "ls" or "dir"
                or "get-item" or "gi" or "test-path" or "get-location" or "pwd" or "resolve-path"
                or "select-string" or "get-filehash" or "get-process" or "ver" => Operation.Read,
            // New-Item may overwrite an existing file (-Force/-Value), while Set/Add-Content
            // may create a missing file. Do not guess target/provider existence or rely on a
            // racy preflight: these cmdlets require both affected categories.
            "new-item" or "mkdir" or "md" => Operation.Create | Operation.Modify,
            "touch" => Operation.Create | Operation.Modify,
            "copy-item" or "copy" or "cp" => Operation.Create | Operation.Modify,
            "move-item" or "move" or "mv" => Operation.Modify | Operation.Delete,
            "set-content" or "add-content" => Operation.Create | Operation.Modify,
            "rename-item" or "ren" or "rename" => Operation.Modify,
            "remove-item" or "ri" or "rm" or "rmdir" or "rd" or "del" or "erase" => Operation.Delete,
            "start-process" or "start" or "explorer" or "explorer.exe" or "open" => Operation.OpenWindow,
            "stop-process" or "taskkill" or "taskkill.exe" or "kill" or "pkill" => Operation.CloseWindow,
            "echo" or "write-output" or "write-host" => Operation.Read,
            _ => Unknown
        };
    }

    private static bool IsPythonExecutable(string executable) => executable is
        "python" or "python.exe" or "python3" or "python3.exe" or "py" or "py.exe";

    private static bool HasLocalCommandOverride(string executable, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;
        try
        {
            return new[] { "", ".exe", ".com", ".cmd", ".bat", ".ps1", ".sh" }
                .Any(extension =>
                {
                    var path = Path.Combine(directory, executable + extension);
                    AiWorkspacePathPolicy.EnsureNoLinks(path);
                    return File.Exists(path);
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return true; }
    }

    // 非隔离脚本仍不预授权，但要读取可识别的脚本以执行关闭的鼠标/键盘门。
    private static string? TryReadPythonScriptEvidence(IReadOnlyList<string> tokens, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        var argument = tokens.Skip(1).FirstOrDefault(token => token.EndsWith(".py", StringComparison.OrdinalIgnoreCase));
        if (argument == null) return null;
        try
        {
            if (AiWorkspacePathPolicy.GetDenial(argument, new[] { directory }) != null) return null;
            var script = Path.GetFullPath(argument, directory);
            if (AiWorkspacePathPolicy.GetDenial(script, new[] { directory }) != null || !IsInside(directory, script)) return null;
            AiWorkspacePathPolicy.EnsureNoLinks(script);
            if (!File.Exists(script) || new FileInfo(script).Length > 32768) return null;
            return File.ReadAllText(script);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    private static string? TryReadIsolatedPythonScript(IReadOnlyList<string> tokens, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || tokens.Count < 4) return null;
        var index = 1;
        if (tokens[index] == "-3" && (tokens[0] is "py" or "py.exe")) index++;
        var isolated = false;
        var noSite = false;
        while (index < tokens.Count && tokens[index].StartsWith('-'))
        {
            if (tokens[index] == "-I" && !isolated) isolated = true;
            else if (tokens[index] == "-S" && !noSite) noSite = true;
            else return null;
            index++;
        }
        if (!isolated || !noSite || index != tokens.Count - 1) return null;
        try
        {
            if (AiWorkspacePathPolicy.GetDenial(tokens[index], new[] { directory }) != null) return null;
            var script = Path.GetFullPath(tokens[index], directory);
            if (!script.EndsWith(".py", StringComparison.OrdinalIgnoreCase) || !IsInside(directory, script)
                || AiWorkspacePathPolicy.GetDenial(script, new[] { directory }) != null || HasLinkedComponent(script)
                || !File.Exists(script) || new FileInfo(script).Length > 32768) return null;
            
            if (tokens[0].IndexOfAny(new[] { '/', '\\', ':' }) >= 0
                || new[] { "python.exe", "python3.exe", "py.exe", "python", "python3", "py", "python.cmd", "python.bat" }
                    .Any(name => HasLinkedComponent(Path.Combine(directory, name)) || File.Exists(Path.Combine(directory, name)))) return null;
            return File.ReadAllText(script);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    private static bool TryGetWorkspaceDirectory(string root, string? requested, out string directory)
    {
        directory = "";
        try
        {
            if (AiWorkspacePathPolicy.IsDevicePath(root)
                || AiWorkspacePathPolicy.GetDenial(requested, new[] { root }) != null) return false;
            var workspace = Path.GetFullPath(root);
            directory = Path.GetFullPath(string.IsNullOrWhiteSpace(requested) ? workspace : requested);
            return IsInside(workspace, directory)
                && AiWorkspacePathPolicy.GetDenial(directory, new[] { workspace }) == null
                && !HasLinkedComponent(directory) && Directory.Exists(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    private static bool IsInside(string root, string path)
    {
        var directory = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var target = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return target.Equals(directory, comparison) || target.StartsWith(directory + Path.DirectorySeparatorChar, comparison);
    }

    private static bool HasLinkedComponent(string path)
    {
        try { AiWorkspacePathPolicy.EnsureNoLinks(path); return false; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static bool TryTokenizeAtomicCommand(string command, out List<string> tokens)
    {
        tokens = new List<string>();
        var token = new StringBuilder();
        var quote = '\0';
        var active = false;
        foreach (var c in command)
        {
            // 插值、环境变量、转义、连接符与重定向无法可靠视为一个静态操作。
            if (c is '\r' or '\n' or '`' or '$' or '%' or '^') return false;
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else token.Append(c);
                continue;
            }
            if (c is ';' or '&' or '|' or '>' or '<' or '(' or ')' or '{' or '}') return false;
            if (c is '\'' or '"') { quote = c; active = true; continue; }
            if (char.IsWhiteSpace(c))
            {
                if (active) { tokens.Add(token.ToString()); token.Clear(); active = false; }
            }
            else { token.Append(c); active = true; }
        }
        if (quote != '\0') return false;
        if (active) tokens.Add(token.ToString());
        return true;
    }

    private static Operation DetectComputerOperations(string text)
    {
        bool Has(string pattern) => Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var result = Operation.None;
        if (Has(@"\b(moveTo|moveRel|setcursorpos)\b|mouse\s*\.\s*move|xdotool\s+mousemove")) result |= Operation.MouseMove;
        if (Has(@"\b(right_?click|rightDown|rightUp)\b|button\s*=\s*['"" ]right|xdotool\s+click\s+3")) result |= Operation.MouseRightClick;
        if (Has(@"\b(click|doubleclick|mouseDown|mouseUp|mouse_event)\b")) result |= Operation.MouseClick;
        if (Has(@"\b(sendkeys|keybd_event|sendinput|keyboard|keyboard_type)\b|pyautogui\s*\.\s*(press|write|typewrite|hotkey|keyDown|keyUp)|xdotool\s+(key|type)")) result |= Operation.Keyboard;
        if (Has(@"\b(start-process|explorer(?:\.exe)?|xdg-open)\b|(?:^|[;&|]\s*)start\s+|\b(?:os\.startfile|webbrowser\.open)\b")) result |= Operation.OpenWindow;
        if (Has(@"\b(stop-process|taskkill(?:\.exe)?|pkill|closeMainWindow)\b|(?:^|[;&|]\s*)kill\s+")) result |= Operation.CloseWindow;
        // 使用通用自动化库而没有公开方法名时，不能判断会操作哪种输入设备，要求全部输入门。
        if (Has(@"\b(pyautogui|pynput|autohotkey|robotjs|win32api|sendinput)\b")
            && (result & (Operation.MouseMove | Operation.MouseClick | Operation.MouseRightClick | Operation.Keyboard)) == Operation.None)
            result |= Operation.MouseMove | Operation.MouseClick | Operation.MouseRightClick | Operation.Keyboard;
        // Win32 SendInput 可注入键盘与鼠标；mouse_event 可包含任一鼠标按钮。
        if (Has(@"\bsendinput\b")) result |= Operation.MouseMove | Operation.MouseClick | Operation.MouseRightClick | Operation.Keyboard;
        if (Has(@"\bmouse_event\b")) result |= Operation.MouseMove | Operation.MouseClick | Operation.MouseRightClick;
        return result;
    }

    private static bool IsRestrictedPythonSource(string source)
    {
        if (source.Length > 32768 || source.Contains('\0') || source.Contains('\r') && !source.Contains("\r\n")) return false;
        var lines = source.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > 256) return false;
        var variables = new HashSet<string>(StringComparer.Ordinal);
        var hasStatement = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            hasStatement = true;
            if (line.StartsWith("print(", StringComparison.Ordinal) && line.EndsWith(')'))
            {
                if (!TrySplitPrintArguments(line[6..^1], out var arguments)
                    || !arguments.All(argument => IsPlainPythonString(argument) || IsNumericExpression(argument, variables))) return false;
                continue;
            }
            var assignment = Regex.Match(line, @"^([A-Za-z][A-Za-z0-9_]*)\s*=\s*(.+)$", RegexOptions.CultureInvariant);
            if (!assignment.Success || assignment.Groups[1].Value == "print"
                || !IsNumericExpression(assignment.Groups[2].Value, variables)) return false;
            variables.Add(assignment.Groups[1].Value);
        }
        return hasStatement;
    }

    private static bool TrySplitPrintArguments(string source, out List<string> arguments)
    {
        arguments = new List<string>();
        if (string.IsNullOrWhiteSpace(source)) { arguments.Add("\"\""); return true; }
        var start = 0;
        var depth = 0;
        var quote = '\0';
        for (var index = 0; index < source.Length; index++)
        {
            var c = source[index];
            if (quote != '\0')
            {
                if (c == '\\') { index++; continue; }
                if (c == quote) quote = '\0';
            }
            else if (c is '\'' or '"') quote = c;
            else if (c == '(') depth++;
            else if (c == ')' && --depth < 0) return false;
            else if (c == ',' && depth == 0) { arguments.Add(source[start..index].Trim()); start = index + 1; }
        }
        if (quote != '\0' || depth != 0) return false;
        arguments.Add(source[start..].Trim());
        return arguments.All(argument => argument.Length > 0);
    }

    private static bool IsPlainPythonString(string expression)
    {
        if (expression.Length < 2 || expression[0] is not ('\'' or '"') || expression[^1] != expression[0]) return false;
        for (var index = 1; index < expression.Length - 1; index++)
        {
            if (expression[index] == '\\') { if (++index >= expression.Length - 1) return false; }
            else if (expression[index] == expression[0]) return false;
        }
        return true;
    }

    private static bool IsNumericExpression(string expression, HashSet<string> variables)
    {
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > 512) return false;
        var index = 0;
        var hasValue = false;
        while (index < expression.Length)
        {
            var c = expression[index];
            if (char.IsWhiteSpace(c) || c is '+' or '-' or '*' or '/' or '%' or '(' or ')') { index++; continue; }
            var number = Regex.Match(expression[index..], @"^(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?", RegexOptions.CultureInvariant);
            if (number.Success) { index += number.Length; hasValue = true; continue; }
            var name = Regex.Match(expression[index..], @"^[A-Za-z][A-Za-z0-9_]*", RegexOptions.CultureInvariant);
            if (!name.Success || !variables.Contains(name.Value)) return false;
            index += name.Length;
            hasValue = true;
        }
        return hasValue;
    }
}
