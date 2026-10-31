using System.Text.Json;
using XCL2.App.Services;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
var directory = Path.Combine(Path.GetTempPath(), "XCL2-NoteOptions-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var note = Path.Combine(directory, "旧便签.txt");
    File.WriteAllText(note, "existing\r\ncontent\n");
    File.WriteAllText(note + ".style", "Blue");
    var defaults = StickyNoteOptions.Load(note);
    Check(defaults.AlignTopLeft && !defaults.SizeToText, "Existing notes default to top-left and fixed size");
    var options = defaults with { AlignTopLeft = false, SizeToText = true };
    options.Save(note);
    Check(StickyNoteOptions.Load(note) == options, "Both explicit choices survive reopening");
    options = options with { AlignTopLeft = true, SizeToText = false };
    options.Save(note);
    Check(StickyNoteOptions.Load(note) == options, "Atomic replacement persists both reverse toggles");
    Check(File.ReadAllText(note) == "existing\r\ncontent\n" && File.ReadAllText(note + ".style") == "Blue",
        "Layout changes preserve exact text/newlines and existing style");
    File.WriteAllText(StickyNoteOptions.OptionsPath(note), "{}");
    Check(StickyNoteOptions.Load(note) == defaults, "Missing properties use backwards-compatible defaults");
    File.WriteAllText(StickyNoteOptions.OptionsPath(note), "broken JSON");
    var rejected = false;
    try { StickyNoteOptions.Load(note); } catch (JsonException) { rejected = true; }
    Check(rejected && File.ReadAllText(StickyNoteOptions.OptionsPath(note)) == "broken JSON",
        "Corrupted settings are surfaced and never silently overwritten during load");
    Check(!Directory.GetFiles(directory, "*.tmp").Any(), "Successful writes leave no temporary files");
    Console.WriteLine("PASS: sticky note defaults, reversible persistence, exact-content preservation and corruption handling.");
}
finally { Directory.Delete(directory, true); }
