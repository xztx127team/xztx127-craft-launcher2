using System.IO;
using Microsoft.Data.Sqlite;

namespace XCL2.App.Services;

/// <summary>Reads only instance metadata; no account, token, cache or settings tables are queried.</summary>
internal static class ModrinthProfileDatabase
{
    internal static IReadOnlyList<ExternalInstanceCandidate> Read(string path, string selected, CancellationToken ct = default)
    {
        ArchiveImportSafety.CheckLinks(path);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 3 }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        var result = new List<ExternalInstanceCandidate>();
        var appRoot = Path.GetDirectoryName(path)!;
        var profilesRoot = Path.Combine(appRoot, "profiles");
        var selectedFull = Path.GetFullPath(selected).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var all = string.Equals(selectedFull, appRoot, StringComparison.OrdinalIgnoreCase) || string.Equals(selectedFull, profilesRoot, StringComparison.OrdinalIgnoreCase);
        command.CommandText = "SELECT path, name, game_version, mod_loader, mod_loader_version FROM profiles";
        if (!all)
        {
            if (!selectedFull.StartsWith(profilesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return result;
            command.CommandText += " WHERE path = $path COLLATE NOCASE";
            command.Parameters.AddWithValue("$path", Path.GetRelativePath(profilesRoot, selectedFull));
        }
        command.CommandText += " LIMIT 2001";
        using var reader = command.ExecuteReader();
        var rows = 0;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (++rows > 2000) throw new InvalidDataException("Modrinth 实例超过 2000 项，请选择单个实例。");
            // Official profiles.path is relative to profiles/. Do not follow absolute or escaping metadata paths.
            var relative = reader.GetString(0);
            string directory;
            try { directory = ArchiveImportSafety.Resolve(profilesRoot, relative); }
            catch (InvalidDataException) { continue; }
            if (!all && !string.Equals(directory, selectedFull, StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(ExternalInstanceDiscovery.Validate(new(reader.GetString(1), "Modrinth App (数据库)", directory, directory,
                reader.GetString(2), ExternalInstanceDiscovery.NormalizeLoader(reader.GetString(3)), reader.IsDBNull(4) ? null : reader.GetString(4),
                Path.Combine(appRoot, "meta"))));
        }
        return result;
    }
}
