using System;
using System.Text.Json;
using System.Threading;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>只批准当前命令的一次启动，不修改 Shell 总开关或持久化类别权限。</summary>
public sealed class AiCommandApproval
{
    private const string Changed = "系统：命令授权或工作目录已变更，本次未执行。请重新发起请求以便确认。";
    private readonly string _permissionState;
    private int _consumed;
    private int _elevatedRetryConsumed;
    internal string PermissionFingerprint => _permissionState;
    public string OriginalShell { get; }
    public string OriginalCommand { get; }
    public string Shell { get; }
    public string Command { get; }
    public string Directory { get; }
    public bool Confirmed { get; }
    public string? PreauthorizedPythonSource { get; }

    private AiCommandApproval(AiAssistantConfig config, string originalShell, string originalCommand,
        string shell, string command, string directory, bool confirmed, string? pythonSource)
    {
        OriginalShell = originalShell;
        OriginalCommand = originalCommand;
        Shell = shell;
        Command = command;
        Directory = directory;
        Confirmed = confirmed;
        PreauthorizedPythonSource = pythonSource;
        _permissionState = PermissionState(config);
    }

    /// <summary>缺少预授权时先询问用户；拒绝、取消和模式变更均不会产生执行许可。</summary>
    public static AiCommandApproval? Request(Func<AiAssistantConfig?> currentConfig, AiWorkModeContext workMode,
        string originalShell, string originalCommand, string shell, string command, string directory, int timeoutSeconds,
        Func<string, string, bool> confirm, CancellationToken ct, out string? denial)
    {
        ct.ThrowIfCancellationRequested();
        denial = null;
        if (!workMode.Allows("run_command")) { denial = workMode.DeniedMessage; return null; }
        var config = currentConfig();
        denial = HardDenial(config, originalShell, originalCommand, shell, command, directory, workMode.EffectiveMode);
        if (denial != null) return null;
        var permissionState = PermissionState(config!);
        var missingPermission = AiCommandPermissionService.GetCommandDenial(config, originalShell, originalCommand, directory, workMode.EffectiveMode)
            ?? AiCommandPermissionService.GetCommandDenial(config, shell, command, directory, workMode.EffectiveMode);
        var preauthorizedPython = AiCommandPermissionService.TryGetPreauthorizedPythonSource(config, shell, command, directory, out var pythonSource, workMode.EffectiveMode);
        var requiresConfirmation = missingPermission != null || AiShellPolicy.RequiresConfirmation(config, workMode.EffectiveMode) && !preauthorizedPython;
        var confirmed = false;
        if (requiresConfirmation)
        {
            var converted = originalShell != shell || originalCommand != command
                ? $"原 Shell：{originalShell}\n原命令：\n{originalCommand}\n\n实际执行：\n" : "";
            var reason = missingPermission != null
                ? "该命令尚未预先放行，正在申请仅执行这一次的权限。批准不会开启全局 Shell 或改变类别开关。\n\n" : "";
            confirmed = confirm(missingPermission != null ? "申请本次 Shell 命令权限" : "运行 Shell 命令",
                reason + $"Shell：{shell}\n工作目录：{directory}\n超时：{Math.Clamp(timeoutSeconds, 1, 120)} 秒\n\n"
                + converted + command + "\n\n命令以当前用户权限执行，可能修改其他目录；输出将发送给当前 AI 接口。是否仅批准并执行这条命令？");
            ct.ThrowIfCancellationRequested();
            if (!confirmed) { denial = "系统：用户拒绝执行命令。"; return null; }
        }
        ct.ThrowIfCancellationRequested();
        if (!workMode.Allows("run_command")) { denial = workMode.DeniedMessage; return null; }
        var latest = currentConfig();
        denial = HardDenial(latest, originalShell, originalCommand, shell, command, directory, workMode.EffectiveMode);
        if (denial != null) return null;
        if (permissionState != PermissionState(latest!)) { denial = Changed; return null; }
        return new AiCommandApproval(latest!, originalShell, originalCommand, shell, command, directory, confirmed,
            preauthorizedPython ? pythonSource : null);
    }

    /// <summary>紧邻 Process.Start 调用；许可绑定固定命令/目录，只能消费一次，执行前复检撤销和模式。</summary>
    public bool TryBeginExecution(AiAssistantConfig? config, AiWorkModeContext workMode, CancellationToken ct, out string? denial)
    {
        if (!ValidateExecution(config, workMode, ct, out denial)) return false;
        if (Interlocked.Exchange(ref _consumed, 1) != 0)
        { denial = "系统：本次命令许可已经使用，未重复执行。"; return false; }
        return true;
    }

    /// <summary>Only the same already-attempted command can receive one UAC retry. UI consent is separate.</summary>
    internal bool TryBeginElevatedRetry(AiAssistantConfig? config, AiWorkModeContext workMode, CancellationToken ct, out string? denial)
    {
        if (!CanRequestElevatedRetry(config, workMode, ct, out denial)) return false;
        if (Interlocked.Exchange(ref _elevatedRetryConsumed, 1) != 0)
        { denial = "系统：本次命令的提权重试许可已使用，未重复执行。"; return false; }
        return true;
    }

    internal bool CanRequestElevatedRetry(AiAssistantConfig? config, AiWorkModeContext workMode, CancellationToken ct, out string? denial)
    {
        if (!ValidateExecution(config, workMode, ct, out denial)) return false;
        if (Volatile.Read(ref _consumed) != 1)
        { denial = "系统：原命令尚未正常尝试，不能直接提权执行。"; return false; }
        if (Volatile.Read(ref _elevatedRetryConsumed) != 0)
        { denial = "系统：本次提权重试许可已使用。"; return false; }
        return true;
    }

    internal bool IsStillPermitted(AiAssistantConfig? config, AiWorkModeContext workMode, out string? denial)
        => ValidateExecution(config, workMode, CancellationToken.None, out denial);

    private bool ValidateExecution(AiAssistantConfig? config, AiWorkModeContext workMode, CancellationToken ct, out string? denial)
    {
        ct.ThrowIfCancellationRequested();
        denial = null;
        if (!workMode.Allows("run_command")) { denial = workMode.DeniedMessage; return false; }
        denial = HardDenial(config, OriginalShell, OriginalCommand, Shell, Command, Directory, workMode.EffectiveMode);
        if (denial != null) return false;
        if (_permissionState != PermissionState(config!)) { denial = Changed; return false; }
        if (!Confirmed)
        {
            denial = AiCommandPermissionService.GetCommandDenial(config, OriginalShell, OriginalCommand, Directory, workMode.EffectiveMode)
                ?? AiCommandPermissionService.GetCommandDenial(config, Shell, Command, Directory, workMode.EffectiveMode);
            if (denial != null) return false;
            var pythonStillApproved = PreauthorizedPythonSource != null
                && AiCommandPermissionService.IsPreauthorizedPython(config, "python", PreauthorizedPythonSource, Directory, workMode.EffectiveMode);
            if (AiShellPolicy.RequiresConfirmation(config, workMode.EffectiveMode) && !pythonStillApproved) { denial = Changed; return false; }
        }
        return true;
    }

    private static string? HardDenial(AiAssistantConfig? config, string originalShell, string originalCommand,
        string shell, string command, string directory, AiWorkMode mode)
        => AiCommandPermissionService.GetCommandApprovalDenial(config, originalShell, originalCommand, directory, mode)
            ?? AiCommandPermissionService.GetCommandApprovalDenial(config, shell, command, directory, mode);

    // 包括全部类别/精确命令及目录设置；确认框或等待界面期间的权限变更要求重新请求。
    private static string PermissionState(AiAssistantConfig config) => JsonSerializer.Serialize(new
    {
        config.WorkingDirectory, config.FullDiskAccess, config.AbsoluteAccess, config.UnrestrictedAccess,
        config.SystemControlAccess, config.AllowShellCommands, config.AllowAllShellCommands,
        CommandPermissions = config.CommandPermissions ?? new AiCommandPermissions()
    });
}
