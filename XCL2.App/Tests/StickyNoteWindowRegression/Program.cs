using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using XCL2.App.Services;
using XCL2.App.Views;
using XCL2.App.Views.Tools;

internal static class Program
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    [STAThread]
    private static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources["PrimaryButton"] = new Style(typeof(Button));
        var directory = Path.Combine(Path.GetTempPath(), "XCL2-NoteWindow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "note.txt");
        File.WriteAllText(path, "Short note");
        try
        {
            var note = new StickyNoteWindow(path); note.Show(); Pump();
            var editor = (TextBox)note.FindName("ContentBox");
            Check(editor.TextAlignment == TextAlignment.Left && editor.VerticalContentAlignment == VerticalAlignment.Top,
                "New and legacy notes use explicit top-left alignment");
            var originalWidth = note.Width; var originalHeight = note.Height;
            editor.Text = new string('x', 100); Pump();
            Check(note.Width == originalWidth && note.Height == originalHeight, "Auto size is off by default");
            note.ApplyLayout(new StickyNoteOptions { SizeToText = true }); Pump();
            Check(note.Width <= 640 && note.Height >= note.MinHeight, "Auto sizing uses bounded width and readable minimum height");
            editor.Text = string.Join("\n", Enumerable.Repeat("Long note content", 1000));
            // Applying the same layout also forces measurement without relying on wall-clock delays.
            note.ApplyLayout(new StickyNoteOptions { SizeToText = true }); Pump();
            var area = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(note).Handle).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(note);
            Check(note.Height * dpi.DpiScaleY <= area.Height && editor.Text.Split('\n').Length == 1000,
                "Long notes stay within the working area and retain every line");
            editor.Text = "short";
            note.ApplyLayout(new StickyNoteOptions { SizeToText = true }); Pump();
            Check(note.Height < originalHeight, "Auto sizing also shrinks after deleting text");
            note.ApplyLayout(new StickyNoteOptions { AlignTopLeft = false, SizeToText = false });
            Check(editor.TextAlignment == TextAlignment.Center && editor.VerticalContentAlignment == VerticalAlignment.Center,
                "Alignment toggle is applied to the actual editor");
            foreach (var style in new[] { "Peach", "Ocean", "Sage", "Sand", "Graphite", "Terminal", "Notebook" })
            {
                note.ApplyStyle(style);
                Check(StickyNoteWindow.ReadStyleKey(path) == style, "New palette persists: " + style);
            }
            Check(editor.Background is DrawingBrush, "Notebook style uses a real lined paper brush");
            note.Close(); Pump();
            var reopened = new StickyNoteWindow(path); reopened.Show(); Pump();
            var reopenedEditor = (TextBox)reopened.FindName("ContentBox");
            Check(reopenedEditor.Text == "short" && reopenedEditor.TextAlignment == TextAlignment.Center,
                "Closing/reopening keeps both edited content and selected layout");
            reopened.Close(); Pump();
            Check(!StickyNoteWindow.OpenWindows.Any(), "Closed notes release the active-window registry");
            Check(ToastService.Warnings.Count == 0, "Normal note actions should not emit warnings");
            ToolEditorDoesNotOverwriteDesktopUpdates(directory);
            Console.WriteLine("PASS: note defaults, bounded grow/shrink, content/layout persistence, stale editor protection and safe deletion.");
        }
        finally { Directory.Delete(directory, true); app.Shutdown(); }
    }
    private static void ToolEditorDoesNotOverwriteDesktopUpdates(string directory)
    {
        XCL2.App.App.DataDir = directory;
        var notes = Path.Combine(directory, "StickyNotes");
        Directory.CreateDirectory(notes);
        var file = Path.Combine(notes, "shared.txt");
        File.WriteAllText(file, "initial content");
        var tool = new StickyNotesTool();
        var host = new Window { Content = tool, Width = 1000, Height = 500, ShowInTaskbar = false };
        host.Show(); Pump();
        try
        {
            var list = (ListBox)tool.FindName("NoteList");
            var editor = (TextBox)tool.FindName("NoteEditor");
            list.SelectedIndex = 0; Pump();
            File.WriteAllText(file, "changed by desktop window");
            tool.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Check(File.ReadAllText(file) == "changed by desktop window", "Leaving an unchanged tool editor must preserve newer desktop content");
            editor.Text = "explicit edit in toolbox";
            editor.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Check(File.ReadAllText(file) == "explicit edit in toolbox", "An actual edit still saves normally");

            list.SelectedIndex = -1;
            using (var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                list.SelectedIndex = 0; Pump();
                Check(editor.IsReadOnly && editor.Text.Length == 0, "An unreadable note must not inherit writable text from the previous note");
                tool.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            }
            Check(File.ReadAllText(file) == "explicit edit in toolbox", "Failed read/unload must preserve the unreadable file");
            list.SelectedIndex = -1; list.SelectedIndex = 0; Pump();
            Check(!editor.IsReadOnly, "Reopening a readable note restores editing");
            var desktop = new StickyNoteWindow(file); desktop.Show(); Pump();
            var desktopEditor = (TextBox)desktop.FindName("ContentBox");
            desktopEditor.Text = "pending desktop autosave";
            typeof(StickyNotesTool).GetMethod("NoteDelete_Click", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(tool, new object[] { tool, new RoutedEventArgs() });
            Pump();
            Check(!File.Exists(file) && !StickyNoteWindow.OpenWindows.Any(window => window.FilePath == file),
                "Deleting a note must close its editor so pending autosave cannot recreate it");
        }
        finally { host.Close(); Pump(); }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}

namespace XCL2.App.Services
{
    public static class LauncherLogService { public static void AppendLine(string value) => Console.WriteLine(value); }
    public static class ToastService
    {
        public static List<string> Warnings { get; } = new();
        public static void ShowWarning(string value) => Warnings.Add(value);
        public static void ShowSuccess(string value) => Console.WriteLine(value);
    }
}

namespace XCL2.App
{
    public static class App { public static string DataDir { get; set; } = ""; }
}
namespace XCL2.App.Services
{
    public static class FolderOpenHelper { public static void Open(string path) { } }
    public static class MessageBoxDialog
    {
        public static bool ShowConfirm(string message, string title) => true;
        public static void ShowError(string message) => throw new Exception(message);
    }
}
