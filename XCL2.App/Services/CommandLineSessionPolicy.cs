using System;

namespace XCL2.App.Services;

/// <summary>将会话内存覆盖应用在动态/实例内存解析之后，不修改已保存的配置。</summary>
public static class CommandLineSessionPolicy
{
    public static string? CombineJvmArguments(bool advancedMode, string? instanceArguments,
        string? globalArguments, string? explicitSessionArguments)
    {
        var saved = advancedMode ? instanceArguments ?? globalArguments : null;
        var parts = new[] { saved, explicitSessionArguments }.Where(value => !string.IsNullOrWhiteSpace(value));
        var combined = string.Join(" ", parts);
        return combined.Length == 0 ? null : combined;
    }

    public static (int MinMb, int MaxMb) ResolveMemory(
        int configuredMinMb, int configuredMaxMb, int? requestedMinMb, int? requestedMaxMb)
    {
        var min = requestedMinMb ?? configuredMinMb;
        var max = requestedMaxMb ?? configuredMaxMb;
        // 仅指定一个端点时，另一端不应让本次覆盖产生 Xms > Xmx。
        // 显式指定两个端点的非法组合由命令行解析器报告，不静默改写。
        if (!requestedMinMb.HasValue && requestedMaxMb.HasValue) min = Math.Min(min, max);
        if (requestedMinMb.HasValue && !requestedMaxMb.HasValue) max = Math.Max(min, max);
        return (min, max);
    }
}
