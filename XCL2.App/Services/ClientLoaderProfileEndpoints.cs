namespace XCL2.App.Services;

internal static class ClientLoaderProfileEndpoints
{
    // Client profile routes have only game + loader. An installer version belongs to the server JAR route.
    // https://github.com/FabricMC/fabric-meta/blob/master/README.md
    // https://meta.legacyfabric.net/
    internal static string ClientProfile(string metaBase, string gameVersion, string loaderVersion)
    {
        if (string.IsNullOrWhiteSpace(gameVersion) || string.IsNullOrWhiteSpace(loaderVersion))
            throw new ArgumentException("Minecraft 和加载器版本不能为空。");
        return metaBase.TrimEnd('/') + "/versions/loader/" + Uri.EscapeDataString(gameVersion) + "/" +
            Uri.EscapeDataString(loaderVersion) + "/profile/json";
    }
}
