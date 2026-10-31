using System.IO;
using System.Text.Json;

namespace XCL2.App.Services;

/// <summary>Per-note layout sidecar. Existing text and .style files remain compatible.</summary>
public sealed record StickyNoteOptions
{
    public bool AlignTopLeft { get; init; } = true;
    public bool SizeToText { get; init; } = false;

    public static string OptionsPath(string notePath) => notePath + ".options.json";

    public static StickyNoteOptions Load(string notePath)
    {
        var path = OptionsPath(notePath);
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<StickyNoteOptions>(File.ReadAllText(path))
            ?? throw new InvalidDataException("便签布局文件为空。请检查同名 .options.json 文件。");
    }

    public void Save(string notePath)
    {
        var path = OptionsPath(notePath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
