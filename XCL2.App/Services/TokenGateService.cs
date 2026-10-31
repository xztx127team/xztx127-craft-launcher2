namespace XCL2.App.Services;

public static class TokenGateService
{
    private static readonly ExperimentalPasswordSession Session = new();
    public static void InitializeSession() => _ = Session;
    public static string Issue(string featureId) => Session.Issue(featureId);
    public static bool Consume(string featureId, string? input) => Session.Consume(featureId, input);
    public static void Revoke(string featureId) => Session.Revoke(featureId);
    public static bool ValidateMultiLoaderToken(string? input) => Consume("multi-loader", input);
}
