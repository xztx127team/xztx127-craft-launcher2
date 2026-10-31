using XCL2.App.Models;
using XCL2.App.Services;

var checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
foreach (var path in new[] { @"\\?\C:\Users\User\XCL2\config.json", @"\\.\C:\secret", @"\??\C:\secret", @"//?/UNC/server/share/file" })
    Check(AiWorkspacePathPolicy.GetDenial(path, new[] { path }) != null,
        "Device namespace aliases cannot bypass private-directory checks.");
foreach (var path in new[] { @"\\untrusted.invalid\share", @"//untrusted.invalid/share/file" })
    Check(AiWorkspacePathPolicy.GetDenial(path) != null,
        "A generated network path must be rejected before filesystem probing.");
Check(AiWorkspacePathPolicy.GetDenial(@"\\server\share\project\file.txt", new[] { @"\\server\share\project" }) == null,
    "An explicitly configured network workspace remains accessible.");
Check(AiWorkspacePathPolicy.GetDenial(@"\\server\share\project-other\file.txt", new[] { @"\\server\share\project" }) != null,
    "An authorized network directory does not authorize prefix siblings.");
Check(AiWorkspacePathPolicy.GetDenial(@"C:\project\readme.txt") == null,
    "Ordinary local workspace paths remain supported.");
var directory = Path.Combine(Path.GetTempPath(), "xcl-permissions-" + Guid.NewGuid().ToString("N"));
var outside = directory + "-outside";
Directory.CreateDirectory(directory);
Directory.CreateDirectory(outside);
try
{
    var config = new AiAssistantConfig { WorkingDirectory = directory };
    foreach (var remote in new[] { @"\\untrusted.invalid\share\file.txt", @"//untrusted.invalid/share/file.txt", @"\\?\UNC\untrusted.invalid\share\file.txt" })
        foreach (var operation in new[] { "write_workspace_file", "read_workspace_file", "request_directory", "list_workspace" })
            Check(AiCommandPermissionService.GetAccessDenial(config, operation, remote) != null,
                "Production admission must reject a network/device path before checking whether it exists: " + operation);
    var outsideEvidence = Path.Combine(outside, "outside-evidence.py");
    File.WriteAllText(outsideEvidence, "import pyautogui\npyautogui.click()\n");
    Check(AiCommandPermissionService.GetCommandApprovalDenial(config, "cmd", "python \"" + outsideEvidence + "\"", directory) == null,
        "Pre-approval classification must not read an external script merely to inspect its operations.");
    var linkedDirectory = Path.Combine(directory, "linked-evidence");
    Directory.CreateSymbolicLink(linkedDirectory, outside);
    var linkedRejected = false;
    try { AiWorkspacePathPolicy.EnsureNoLinks(Path.Combine(linkedDirectory, "missing-child.py")); }
    catch (UnauthorizedAccessException) { linkedRejected = true; }
    Check(linkedRejected, "A missing leaf cannot bypass the linked-parent check.");
    Directory.Delete(linkedDirectory);
    var chat = new AiAssistantConfig { WorkMode = AiWorkMode.Chat, WorkingDirectory = directory,
        FullDiskAccess = true, AllowShellCommands = true, AllowAllShellCommands = true };
    foreach (var property in typeof(AiCommandPermissions).GetProperties().Where(p => p.PropertyType == typeof(bool)))
        property.SetValue(chat.CommandPermissions, true);
    Check(AiCommandPermissionService.GetCommandDenial(chat, "python", "print(1)", directory) != null,
        "Pure chat cannot inherit Shell, full-access or all-selected grants.");
    Check(!AiCommandPermissionService.IsPreauthorizedPython(chat, "python", "print(1)", directory),
        "Pure chat has no default workspace Python grant.");
    foreach (var action in new[] { "request_directory", "read_workspace_file", "run_command", "apply_setting", "mouse_click" })
        Check(AiCommandPermissionService.GetAccessDenial(chat, action, directory) != null,
            "Pure chat command policy rejects " + action);
    string? Denial(string shell, string command) => AiCommandPermissionService.GetCommandDenial(config, shell, command, directory);
    Check(config.CommandPermissions.Create && config.CommandPermissions.RunPython && !config.CommandPermissions.MouseClick, "Defaults must preserve workspace creation / Python and disable desktop input.");
    Check(Denial("python", "value = 2 * (3 + 4)\nprint(value, 'hello')") == null, "Restricted workspace Python should be preauthorized with the Shell master disabled.");
    Check(Denial("python", "print('click here')") == null, "Strings describing input must not be mistaken for actual mouse commands.");
    Check(Denial("python", "import os\nos.remove('x')") != null, "Arbitrary Python is not workspace preauthorization.");
    Check(!AiCommandPermissionService.IsPreauthorizedPython(config, "python", "print(1)", outside), "An outside current directory cannot inherit Python preauthorization.");
    config.AllowShellCommands = true;
    Check(Denial("powershell", "Get-Content readme.txt") == null, "A recognized atomic read should honor the read category.");
    Check(Denial("powershell", "Get-Content mouse_event.txt") == null, "A file name must not trigger desktop automation permission checks.");
    Check(Denial("powershell", "Remove-Item readme.txt") != null, "Delete defaults off.");
    Check(Denial("powershell", "Get-Content readme.txt; Remove-Item readme.txt") != null, "Compound commands may not inherit read permission.");
    Check(Denial("powershell", "Get-Content $(Invoke-Expression 'anything')") != null, "Interpolation cannot be classified as read.");
    Check(Denial("cmd", "echo 'hello & del file.txt'") != null, "CMD does not treat apostrophes as quotes, so this is not an atomic read.");
    Check(Denial("cmd", "Get-Content readme.txt") != null, "A PowerShell cmdlet name cannot authorize a CMD external executable.");
    Check(Denial("powershell", "./Get-Content readme.txt") != null, "An explicit executable path cannot impersonate a cmdlet.");
    File.WriteAllText(Path.Combine(directory, "Get-Content.cmd"), "echo fake");
    Check(Denial("cmd", "Get-Content readme.txt") != null, "A local executable cannot inherit a known command name's category.");
    File.Delete(Path.Combine(directory, "Get-Content.cmd"));
    Check(Denial("shell", "find . -exec anything {} +") != null, "A find expression can execute other commands and must remain other.");
    Check(Denial("python", "__import__('os').system('whoami')") != null, "Dynamic Python needs other / Shell permission.");

    File.WriteAllText(Path.Combine(directory, "safe.py"), "x = 6 / 2\nprint(x)\n");
    var localCommand = "python -I -S safe.py";
    Check(AiCommandPermissionService.IsPreauthorizedPython(config, "powershell", localCommand, directory), "An isolated restricted script inside the workspace is preauthorized.");
    Check(AiCommandPermissionService.TryGetPreauthorizedPythonSource(config, "powershell", localCommand, directory, out var source), "Preauthorization must provide a source snapshot.");
    File.WriteAllText(Path.Combine(directory, "safe.py"), "import os\nos.remove('anything')");
    Check(source.Contains("print(x)") && !AiCommandPermissionService.IsPreauthorizedPython(config, "powershell", localCommand, directory), "The approved snapshot must not change when the on-disk script changes.");
    File.WriteAllText(Path.Combine(directory, "safe.py"), "print(1)");
    Check(!AiCommandPermissionService.IsPreauthorizedPython(config, "cmd", "python safe.py", directory), "Non-isolated interpreter execution is not preauthorized.");
    File.WriteAllText(Path.Combine(outside, "outside.py"), "print(1)");
    Check(!AiCommandPermissionService.IsPreauthorizedPython(config, "cmd", "python -I -S ../" + Path.GetFileName(outside) + "/outside.py", directory), "A script outside the current workspace cannot be preauthorized.");
    File.WriteAllText(Path.Combine(directory, "python.exe"), "fake");
    Check(!AiCommandPermissionService.IsPreauthorizedPython(config, "cmd", localCommand, directory), "A local fake interpreter blocks preauthorization.");
    File.Delete(Path.Combine(directory, "python.exe"));

    config.CommandPermissions.ExactCommands[AiCommandPermissionService.GetCommandKey("powershell", "Remove-Item readme.txt")] = true;
    Check(Denial("powershell", "Remove-Item readme.txt") == null, "An individual complete command can grant an operation.");
    Check(AiCommandPermissionService.GetCommandKey("pwsh", " print('A') ") == "powershell|print('A')", "Command keys must use the persisted UI delimiter and normalize shell aliases.");
    Check(AiCommandPermissionService.GetCommandKey("python", "print('A')") != AiCommandPermissionService.GetCommandKey("python", "print('a')"), "Exact command keys must preserve string content case.");
    Check(AiCommandPermissionService.GetCommandKey("powershell", "echo 'a  b'") != AiCommandPermissionService.GetCommandKey("powershell", "echo 'a b'"), "Exact command keys must preserve internal whitespace.");
    var clone = config.CommandPermissions.Clone();
    clone.ExactCommands.Clear();
    Check(config.CommandPermissions.ExactCommands.Count > 0, "Permission clones must not share the exact command dictionary.");

    config.AllowAllShellCommands = true;
    Check(Denial("powershell", "Remove-Item other.txt") == null, "Allow all includes operations not previously included in categories.");
    config.CommandPermissions.ExactCommands[AiCommandPermissionService.GetCommandKey("powershell", "Remove-Item other.txt")] = false;
    Check(Denial("powershell", "Remove-Item other.txt") != null, "Individual denial takes priority over allow all.");
    Check(Denial("python", "import pyautogui\npyautogui.moveTo(1, 2)") != null, "Allow all cannot enable disabled mouse movement.");
    config.CommandPermissions.ExactCommands[AiCommandPermissionService.GetCommandKey("python", "import pyautogui\npyautogui.click()")] = true;
    Check(Denial("python", "import pyautogui\npyautogui.click()") != null, "Exact command allowance cannot enable disabled mouse clicks.");
    File.WriteAllText(Path.Combine(directory, "desktop.py"), "import pyautogui\npyautogui.rightClick()\n");
    Check(Denial("cmd", "python desktop.py") != null, "An obvious desktop action inside a referenced Python script must honor the desktop gate.");
    Check(AiCommandPermissionService.GetAccessDenial(config, "mouse_click", null) != null, "Structured mouse actions honor disabled input gates even with allow all.");
    config.CommandPermissions.MouseClick = true;
    Check(AiCommandPermissionService.GetAccessDenial(config, "mouse_click", null) == null, "An explicitly enabled structured mouse action can execute.");
    config.CommandPermissions.Post = false;
    Check(AiCommandPermissionService.GetAccessDenial(config, "mouse_click", null) != null, "Structured desktop actions must also honor the Post category.");
    config.CommandPermissions.Post = true;

    config.AllowAllShellCommands = false;
    config.FullDiskAccess = true;
    config.CommandPermissions.PowerShell = false;
    Check(Denial("powershell", "Get-Content readme.txt") != null, "Full disk access cannot override a disabled command category.");
    config.CommandPermissions.PowerShell = true;
    config.CommandPermissions.Create = false;
    File.WriteAllText(Path.Combine(directory, "existing.txt"), "existing");
    Check(AiCommandPermissionService.GetAccessDenial(config, "write_workspace_file", "existing.txt") == null, "An existing workspace-relative file requires modify, not create.");
    Check(AiCommandPermissionService.GetAccessDenial(config, "write_workspace_file", "new.txt") != null, "A new workspace-relative file requires create.");
    Check(Denial("powershell", "Set-Content new.txt 'content'") != null, "Set-Content cannot create a missing file through modify-only permission.");
    Check(Denial("powershell", "Add-Content new.txt 'content'") != null, "Add-Content also requires create because target existence may change.");
    config.CommandPermissions.Create = true;
    config.CommandPermissions.Modify = false;
    Check(Denial("powershell", "New-Item -ItemType File -Force -Path existing.txt -Value replacement") != null,
        "New-Item cannot overwrite an existing file through create-only permission.");
    Check(Denial("powershell", "Set-Content existing.txt 'content'") != null, "Set-Content still requires modify.");
    config.CommandPermissions.Modify = true;
    Check(Denial("powershell", "Set-Content new.txt 'content'") == null, "Both categories allow an explicit content command with normal confirmation retained.");
    Check(Denial("powershell", "New-Item -ItemType File -Force -Path existing.txt -Value replacement") == null,
        "Both categories allow New-Item without claiming filesystem sandboxing.");
    config.CommandPermissions.Modify = false;
    Check(AiCommandPermissionService.GetAccessDenial(config, "write_workspace_file", "existing.txt") != null, "Existing files cannot bypass a disabled modify category.");

    config.FullDiskAccess = false;
    config.WorkingDirectory = Path.Combine(directory, "missing-workspace");
    Check(Denial("powershell", "Get-Content readme.txt") != null, "An unavailable configured directory cannot silently fall back to an executable current directory.");
    config.WorkingDirectory = null;
    Check(Denial("python", "print(1)") != null, "No workspace blocks commands even when Shell is enabled.");
    Check(AiCommandPermissionService.GetAccessDenial(config, "mouse_click", null) != null, "No workspace also blocks structured desktop input.");
    Check(AiCommandPermissionService.GetAccessDenial(config, "read_workspace_file", "attachment.txt") == null, "Read-only attachments remain available without a command workspace.");
    config.AllowAllShellCommands = true;
    Check(Denial("python", "print(1)") == null, "The explicit all-Shell setting permits execution without a workspace.");

    // Exercise production single-command admission without starting a process or changing global grants.
    var approvalConfig = new AiAssistantConfig { WorkingDirectory = directory };
    var currentMode = AiWorkMode.Build;
    AiWorkModeContext Mode() => new(currentMode, () => currentMode);
    var approvalsAsked = 0;
    string approvalMessage = "", approvalTitle = "";
    AiCommandApproval? Request(bool accepted = true, string shell = "powershell", string command = "Get-Content readme.txt",
        string? originalShell = null, string? originalCommand = null, Action? duringConfirm = null, CancellationToken ct = default,
        AiWorkModeContext? mode = null)
        => AiCommandApproval.Request(() => approvalConfig, mode ?? Mode(), originalShell ?? shell, originalCommand ?? command,
            shell, command, directory, 30, (title, message) =>
            { approvalsAsked++; approvalTitle = title; approvalMessage = message; duringConfirm?.Invoke(); return accepted; }, ct, out _);
    var approval = Request();
    Check(approval != null && approvalsAsked == 1 && approvalTitle.Contains("申请本次"), "A disabled Shell master must request per-command approval instead of directly failing.");
    Check(approvalMessage.Contains("Get-Content readme.txt") && approvalMessage.Contains(directory)
        && approvalMessage.Contains("不会开启全局"), "Approval shows the actual command, directory and one-time scope.");
    Check(!approvalConfig.AllowShellCommands && approvalConfig.CommandPermissions.ExactCommands.Count == 0,
        "Approving one command never changes master, category or exact-command settings.");
    Check(approval!.TryBeginExecution(approvalConfig, Mode(), default, out _), "An accepted one-time command can start even without a persistent Shell grant.");
    Check(!approval.TryBeginExecution(approvalConfig, Mode(), default, out _), "A one-time approval cannot be consumed twice.");
    Check(Request(false) == null && approvalsAsked == 2, "Refusing the request produces no execution permit.");
    approvalConfig.AllowShellCommands = true;
    approvalConfig.CommandPermissions.PowerShell = false;
    Check(Request() != null && approvalsAsked == 3, "A disabled Shell category asks for approval of this command.");
    approvalConfig.CommandPermissions.PowerShell = true;
    Check(Request(command: "Remove-Item readme.txt") != null && approvalsAsked == 4,
        "An ungranted file operation asks for one-command approval without enabling Delete.");
    Check(!approvalConfig.CommandPermissions.Delete, "One-command approval does not enable the disabled operation category.");
    Check(Request(command: "Write-Output 1; Write-Output 2") != null && approvalsAsked == 5,
        "Unclassified compound commands also request approval rather than silently executing or failing.");
    approvalConfig.AllowAllShellCommands = true;
    approval = Request();
    Check(approval != null && !approval.Confirmed && approvalsAsked == 5
        && approval.TryBeginExecution(approvalConfig, Mode(), default, out _), "An existing allow-all grant retains automatic execution.");
    approvalConfig.CommandPermissions.ExactCommands[AiCommandPermissionService.GetCommandKey("powershell", "Get-Content readme.txt")] = false;
    Check(Request() == null && approvalsAsked == 5, "An explicit exact-command prohibition cannot be approved through a retry prompt.");
    approvalConfig.CommandPermissions.ExactCommands.Clear();
    Check(Request(command: "Start-Process explorer.exe") == null && approvalsAsked == 5,
        "Approval cannot bypass a disabled computer window control, even with allow-all.");
    Check(Request(shell: "python", command: "import pyautogui\npyautogui.click()") == null && approvalsAsked == 5,
        "Approval cannot bypass disabled desktop input.");
    approvalConfig.AllowAllShellCommands = false;
    approvalConfig.AllowShellCommands = false;
    approvalConfig.WorkingDirectory = null;
    Check(Request() == null && approvalsAsked == 5, "A missing workspace is a hard boundary, with no misleading command approval.");
    approvalConfig.WorkingDirectory = directory;
    foreach (var restricted in new[] { AiWorkMode.Chat, AiWorkMode.Plan })
    {
        currentMode = restricted;
        Check(Request() == null && approvalsAsked == 5, restricted + " does not request command escalation.");
    }
    currentMode = AiWorkMode.Build;
    approvalConfig.WorkMode = AiWorkMode.Chat;
    approval = Request();
    Check(approval != null && approval.TryBeginExecution(approvalConfig, Mode(), default, out _),
        "A background Build context retains its mode when another conversation sets the shared config to Chat.");
    approvalConfig.WorkMode = AiWorkMode.Build;
    approval = Request(duringConfirm: () => currentMode = AiWorkMode.Plan);
    Check(approval == null, "Switching the requesting conversation to Plan while approving blocks execution.");
    currentMode = AiWorkMode.Build;
    approval = Request(duringConfirm: () => approvalConfig.CommandPermissions.Read = false);
    Check(approval == null, "Changing permissions inside the confirmation window invalidates approval.");
    approvalConfig.CommandPermissions.Read = true;
    approval = Request();
    approvalConfig.CommandPermissions.Read = false;
    Check(approval != null && !approval.TryBeginExecution(approvalConfig, Mode(), default, out _),
        "Revoking a permission after confirmation is checked immediately before process creation.");
    approvalConfig.CommandPermissions.Read = true;
    approval = Request();
    approvalConfig.WorkingDirectory = outside;
    Check(approval != null && !approval.TryBeginExecution(approvalConfig, Mode(), default, out _),
        "Changing the workspace after approval does not execute the command in the stale directory.");
    approvalConfig.WorkingDirectory = directory;
    approval = Request(shell: "powershell", command: "Get-ChildItem -Force", originalShell: "bash", originalCommand: "ls -la");
    Check(approval != null && approvalMessage.Contains("ls -la") && approvalMessage.Contains("Get-ChildItem -Force"),
        "Converted commands show both the requested text and actual Windows command in one approval.");
    approvalConfig.CommandPermissions.ExactCommands[AiCommandPermissionService.GetCommandKey("bash", "ls -la")] = false;
    Check(Request(shell: "powershell", command: "Get-ChildItem -Force", originalShell: "bash", originalCommand: "ls -la") == null,
        "Conversion cannot bypass a prohibition on the original command.");
    approvalConfig.CommandPermissions.ExactCommands.Clear();
    var asksBeforePython = approvalsAsked;
    approval = Request(shell: "python", command: "print(1)");
    Check(approval != null && !approval.Confirmed && approval.PreauthorizedPythonSource == "print(1)"
        && approval.TryBeginExecution(approvalConfig, Mode(), default, out _) && approvalsAsked == asksBeforePython,
        "Restricted workspace Python retains its preauthorization and immutable source snapshot.");
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        var asksBeforeCancel = approvalsAsked;
        var stopped = false;
        try { Request(ct: cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
        Check(stopped && approvalsAsked == asksBeforeCancel, "Cancellation before approval never asks or yields a permit.");
    }
    using (var cancelled = new CancellationTokenSource())
    {
        var stopped = false;
        try { Request(ct: cancelled.Token, duringConfirm: cancelled.Cancel); } catch (OperationCanceledException) { stopped = true; }
        Check(stopped, "Cancellation during the approval dialog never yields an execution permit.");
    }
    approval = Request();
    currentMode = AiWorkMode.Chat;
    Check(approval != null && !approval.TryBeginExecution(approvalConfig, Mode(), default, out _),
        "The final admission check rejects a later Chat switch.");

    // UAC is a second, bounded use of an already-approved exact command; it cannot replace admission.
    currentMode = AiWorkMode.Build;
    var retryMode = Mode();
    approval = Request(mode: retryMode);
    Check(approval != null && !approval.CanRequestElevatedRetry(approvalConfig, retryMode, default, out _),
        "UAC cannot start a command that was never attempted with the normal one-time permit.");
    Check(approval!.TryBeginExecution(approvalConfig, retryMode, default, out _), "Normal command attempt consumes its own permit.");
    Check(approval.CanRequestElevatedRetry(approvalConfig, retryMode, default, out _)
        && approval.CanRequestElevatedRetry(approvalConfig, retryMode, default, out _),
        "Checks before a UAC dialog do not consume the post-consent retry permit.");
    Check(approval.TryBeginElevatedRetry(approvalConfig, retryMode, default, out _)
        && !approval.TryBeginElevatedRetry(approvalConfig, retryMode, default, out _),
        "Post-UAC execution has exactly one retry, never an unlimited launch token.");
    Check(approval.IsStillPermitted(approvalConfig, retryMode, out _), "Running retry can monitor permissions without consuming another launch.");
    approvalConfig.CommandPermissions.ExactCommands[AiCommandPermissionService.GetCommandKey("powershell", "Get-Content readme.txt")] = false;
    Check(!approval.IsStillPermitted(approvalConfig, retryMode, out _), "An exact prohibition revokes a running administrator retry.");
    approvalConfig.CommandPermissions.ExactCommands.Clear();
    retryMode = Mode(); approval = Request(mode: retryMode);
    Check(approval != null && approval.TryBeginExecution(approvalConfig, retryMode, default, out _), "Prepare simulated pending-UAC command.");
    currentMode = AiWorkMode.Plan;
    Check(!approval!.TryBeginElevatedRetry(approvalConfig, retryMode, default, out _),
        "Switching to Plan while UAC is pending blocks post-consent authorization.");
    currentMode = AiWorkMode.Build;
    Check(!approval.TryBeginElevatedRetry(approvalConfig, retryMode, default, out _),
        "Switching back cannot upgrade the already-revoked running turn.");

    Check(AiElevationPolicy.ShouldOffer(null, null, new System.ComponentModel.Win32Exception(740)), "Windows elevation-required launch failure can request consent.");
    Check(AiElevationPolicy.ShouldOffer(1, "Access is denied."), "Actual failed access-denied output can request consent.");
    Check(!AiElevationPolicy.ShouldOffer(0, "Access is denied."), "Successful output text alone cannot request UAC.");
    Check(!AiElevationPolicy.ShouldOffer(null, null, new System.ComponentModel.Win32Exception(2)), "Missing executable is not an elevation problem.");
    Check(!AiElevationPolicy.ShouldOffer(1, "PSSecurityException: running scripts is disabled; Access is denied"), "UAC must not bypass a PowerShell execution policy.");
    Check(!AiElevationPolicy.ShouldOffer(1, "HTTP 403 Access is denied"), "A network authorization failure must not be treated as local elevation.");
    Check(!AiElevationPolicy.ShouldOffer(1, "SyntaxError: Access is denied"), "Syntax errors do not request an administrator retry.");
    var grants = new AiElevationSession();
    var otherGrants = new AiElevationSession();
    grants.Allow("same-permissions");
    Check(grants.Allows("same-permissions") && !otherGrants.Allows("same-permissions") && !grants.Allows("changed-permissions"),
        "Allow-all belongs only to the exact in-memory conversation and matching permission snapshot.");
    grants.Clear();
    Check(!grants.Allows("same-permissions"), "Session grants are revocable and never persisted to global settings.");
    Console.WriteLine($"PASS: {checks} AI permission regression checks");
}
finally
{
    Directory.Delete(directory, true);
    Directory.Delete(outside, true);
}
