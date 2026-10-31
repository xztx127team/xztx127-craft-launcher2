using System.IO;

namespace XCL2.App.Services;

/// <summary>Remember an explicit save location within one instance/content destination, not across all downloads.</summary>
internal sealed class DownloadDirectoryMemory
{
    private readonly Dictionary<string, string> _locations = new(StringComparer.OrdinalIgnoreCase);
    private static string Scope(string directory) => Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    internal string Resolve(string defaultDirectory)
        => _locations.TryGetValue(Scope(defaultDirectory), out var saved) ? saved : defaultDirectory;

    internal void Clear() => _locations.Clear();

    internal void Remember(string defaultDirectory, string chosenDirectory)
    {
        var key = Scope(defaultDirectory);
        if (_locations.Count >= 64 && !_locations.ContainsKey(key)) _locations.Remove(_locations.Keys.First());
        _locations[key] = Path.GetFullPath(chosenDirectory);
    }
}
