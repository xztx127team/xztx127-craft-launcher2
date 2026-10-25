using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace XCL2.App.Services;

/// <summary>
/// Keeps all folder-picking callers on one compatibility boundary. The classic shell browser
/// is available on older Windows and does not require the modern common-item dialog path.
/// </summary>
public sealed class CompatibleFolderDialog : CommonDialog
{
    public string Title { get; set; } = string.Empty;
    public string InitialDirectory { get; set; } = string.Empty;
    public bool Multiselect { get; set; }
    public string FolderName { get; private set; } = string.Empty;
    public string[] FolderNames { get; private set; } = Array.Empty<string>();

    public override void Reset()
    {
        Title = InitialDirectory = FolderName = string.Empty;
        Multiselect = false;
        FolderNames = Array.Empty<string>();
    }

    protected override bool RunDialog(IntPtr hwndOwner)
    {
        FolderName = string.Empty;
        FolderNames = Array.Empty<string>();
        if (WindowsCompatibilityService.Current.UseClassicFolderPicker)
            return ShowClassic(hwndOwner);
        try { return ShowModern(hwndOwner); }
        catch (Exception ex) when (ex is PlatformNotSupportedException or EntryPointNotFoundException
            or DllNotFoundException or System.Runtime.InteropServices.COMException)
        {
            return ShowClassic(hwndOwner);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool ShowModern(IntPtr hwndOwner)
    {
        var dialog = new OpenFolderDialog { Title = Title, Multiselect = Multiselect };
        if (!string.IsNullOrWhiteSpace(InitialDirectory) && Directory.Exists(InitialDirectory))
            dialog.InitialDirectory = InitialDirectory;
        var owner = HwndSource.FromHwnd(hwndOwner)?.RootVisual as Window;
        var result = owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (result != true) return false;
        FolderName = dialog.FolderName;
        FolderNames = dialog.FolderNames;
        return true;
    }

    private bool ShowClassic(IntPtr hwndOwner)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = Title,
            AutoUpgradeEnabled = false,
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(InitialDirectory) ? InitialDirectory : string.Empty
        };
        // The classic browser selects one folder at a time. All current launcher call sites
        // consume a single FolderName; no requested selection is silently dropped.
        if (dialog.ShowDialog(new NativeOwner(hwndOwner)) != Forms.DialogResult.OK) return false;
        FolderName = dialog.SelectedPath;
        FolderNames = new[] { FolderName };
        return true;
    }

    private sealed class NativeOwner(IntPtr handle) : Forms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }
}
