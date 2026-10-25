namespace XCL2.App.Services;
// The resolver's only external dependency is a profile lookup; use the real transaction lookup.
internal static class FolderService
{
    internal static string? ResolveVersionJson(string directory, string id)
        => ComponentProfileTransaction.FindProfile(directory, id);
}
