using XCL2.App.Models;

namespace XCL2.App.Services;

public sealed class AiParameterAdaptationException : InvalidOperationException
{
    public string ModelKey { get; }
    public string ModelName { get; }
    public IReadOnlyList<AiParameterRule> Attempts { get; }
    public AiParameterAdaptationException(AiRequestTarget target, string message, IEnumerable<AiParameterRule> attempts, Exception? inner = null)
        : base(message, inner)
    {
        ModelKey = target.ModelKey;
        ModelName = target.ProviderName + " · " + target.ModelId;
        Attempts = attempts.ToArray();
    }
    public static string Describe(AiParameterRule rule) => rule.Action switch
    {
        "set" => rule.Parameter + "=" + rule.Value,
        "rename" => rule.Parameter + " → " + rule.Value,
        _ => "省略 " + rule.Parameter + (rule.Parameter == "reasoning_effort" ? "（思考行为由服务端决定）" : "")
    };
}
