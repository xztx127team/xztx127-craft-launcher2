using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace XCL2.App.Services;

public sealed record ErrorReport(string Code, string IncidentId, DateTimeOffset Time, string Title,
    string Summary, string Guidance, string Detail)
{
    public string Cause { get; init; } = "";
    public string CategoryCode { get; init; } = "";

    public static ErrorReport Create(string summary, string detail, string title)
    {
        var safeSummary = DiagnosticRedactionService.Sanitize(summary ?? "", 4000);
        var safeDetail = DiagnosticRedactionService.Sanitize(detail ?? "", 16000);
        var definition = ErrorCatalog.Find((detail ?? "") + "\n" + (summary ?? ""));
        // 行号和事件时间不参与指纹；异常类型、原因和调用位置仍可区分不同故障。
        var evidence = Regex.Replace(safeDetail, @"(?i)(:line |:行 |line )\d+", "$1#");
        evidence = Regex.Replace(evidence, @"\b[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\b", "<id>");
        evidence = Regex.Replace(evidence, @"\b\d{4}-\d{2}-\d{2}[T ][\d:.+Z-]+", "<time>");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            definition.Code + "\n" + safeSummary + "\n" + evidence)))[..16];
        return new($"{definition.Code}-{fingerprint}", Guid.NewGuid().ToString("N"), DateTimeOffset.Now,
            "XCL 错误 · " + definition.Title, safeSummary, definition.Guidance, safeDetail)
        { Cause = definition.Cause, CategoryCode = definition.Code };
    }

    public string ToText() => $"{Title}\n错误代码：{Code}\n分类代码：{CategoryCode}\n报告编号：{IncidentId}\n发生时间：{Time:O}\n启动器版本：{typeof(ErrorReport).Assembly.GetName().Version}\n系统：{Environment.OSVersion.VersionString}\n运行时：{Environment.Version}\n\n发生了什么\n{Summary}\n\n错误原因\n{Cause}\n\n处理方法\n{Guidance}\n\n脱敏技术详情\n{Detail}\n\n反馈时请补充：操作步骤、预期结果、实际结果、能否再次复现。";

    public static (string Category, string Guidance) Classify(string evidence)
    {
        var definition = ErrorCatalog.Find(evidence ?? "");
        return (definition.Code[4..], definition.Guidance);
    }
}
