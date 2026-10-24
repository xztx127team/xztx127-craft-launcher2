using System.Diagnostics;
using XCL2.App.Models;

namespace XCL2.App.Services;

public static partial class AiFileAccessService
{
    private static async Task<string?> TryRunElevatedCommandAsync(AiCommandApproval approval,
        ProcessStartInfo originalStart, int timeoutSeconds, int? originalExitCode, string originalOutput,
        Exception? originalError, AiAssistantActivity activity, Action<AiAssistantActivity>? report,
        CancellationToken ct, AiWorkModeContext workMode)
    {
        if (!OperatingSystem.IsWindows() || !AiElevationPolicy.ShouldOffer(originalExitCode, originalOutput, originalError)) return null;
        var interaction = AiElevationInteraction.Current;
        if (interaction == null) return null;
        if (!approval.CanRequestElevatedRetry(ConfigService.Active?.Config.AiAssistant, workMode, ct, out var denial))
        {
            FinishActivity(activity, AiActivityStatus.Denied, denial ?? "当前权限不允许重试。", report);
            return denial;
        }
        var previous = originalError?.Message ?? originalOutput;
        if (previous.Length > 16000) previous = previous[..16000] + "\n……（原输出已截断）";
        activity.Status = AiActivityStatus.Pending;
        activity.Details = "普通权限命令失败，等待管理员权限重试审批。原命令可能已产生部分修改。\n" + previous;
        PublishActivity(activity, report);
        var choice = AiElevationDecision.AllowOnce;
        if (!interaction.Session.Allows(approval.PermissionFingerprint))
        {
            try
            {
                choice = await interaction.AskAsync(new AiElevationRequest(approval.Shell, approval.Command,
                    approval.Directory, timeoutSeconds, previous), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                FinishActivity(activity, AiActivityStatus.Cancelled, "管理员权限审批已取消，命令未提权重试。普通权限尝试可能已有部分修改。", report);
                throw;
            }
        }
        if (choice == AiElevationDecision.Deny)
        {
            var refused = "系统：普通权限尝试失败；用户不允许管理员权限重试，未再次执行命令。\n" + previous;
            FinishActivity(activity, AiActivityStatus.Denied, refused, report);
            return refused;
        }
        if (choice == AiElevationDecision.AllowSession) interaction.Session.Allow(approval.PermissionFingerprint);
        activity.Details = "等待 Windows UAC 确认；原命令尚未以管理员权限启动。";
        PublishActivity(activity, report);
        string? changedPermission = null;
        var result = await AiElevatedCommandRunner.RunAsync(originalStart, timeoutSeconds,
            validateBeforeConsent: () =>
            {
                if (!approval.CanRequestElevatedRetry(ConfigService.Active?.Config.AiAssistant, workMode, ct, out changedPermission))
                    throw new UnauthorizedAccessException(changedPermission);
            },
            authorizeExecution: () =>
            {
                if (!approval.TryBeginElevatedRetry(ConfigService.Active?.Config.AiAssistant, workMode, ct, out changedPermission))
                    throw new UnauthorizedAccessException(changedPermission);
                activity.Status = AiActivityStatus.Running;
                activity.Details = "Windows UAC 已确认，当前命令许可已复检，正在启动管理员重试。";
                PublishActivity(activity, report);
            },
            isStillAllowed: () => approval.IsStillPermitted(ConfigService.Active?.Config.AiAssistant, workMode, out _), ct);
        if (result.UacDenied) interaction.Session.Clear();
        var success = result.ExitCode == 0 && !result.Cancelled && !result.TimedOut && !result.PermissionRevoked && changedPermission == null;
        var stopped = result.StopConfirmed ? "主进程已结束（终止时已请求结束进程树；脱离的独立进程不能保证）" : "无法确认管理员进程已结束，请检查系统进程";
        var reason = changedPermission ?? (result.CommandNotStarted && result.AwaitingUac ? "本次 UAC 等待已取消或超时，原命令未以管理员权限启动；Windows 确认窗口可能仍需手动关闭。"
            : result.PermissionRevoked ? "模式或命令权限已撤销，已请求停止管理员重试。"
            : result.UacDenied ? "用户拒绝 Windows UAC，命令未提权重试。"
            : result.Cancelled ? "管理员命令已取消；" + stopped
            : result.TimedOut ? "管理员命令超时；" + stopped
            : result.ExitCode.HasValue ? $"管理员重试结束，退出码 {result.ExitCode}。"
            : "管理员重试未得到有效退出码；" + stopped);
        var text = "系统：" + reason + "\n输出：\n```\n" + result.Output + "\n```\n原命令及重试都可能产生部分修改，请如实说明结果。";
        var status = result.Cancelled || ct.IsCancellationRequested ? AiActivityStatus.Cancelled
            : result.UacDenied || result.PermissionRevoked || changedPermission != null ? AiActivityStatus.Denied
            : success ? AiActivityStatus.Completed : AiActivityStatus.Failed;
        FinishActivity(activity, status, text, report);
        AuditSystemLog("管理员命令重试结果：" + reason);
        ct.ThrowIfCancellationRequested();
        return text;
    }
}
