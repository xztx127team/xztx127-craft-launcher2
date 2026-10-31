using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XCL2.App.Services;

namespace XCL2.App.Views.Tools;

/// <summary>
/// 百宝箱「桌面便签」工具：纯本地文本便签，不联网。
/// - 便签以 .txt 文件形式存放在数据目录 StickyNotes/ 下，随时可以手动打开/备份/迁移；
/// - 左侧列表 + 右侧编辑区，编辑内容自动保存（防抖 800ms，切换/关闭/删除前强制落盘）；
/// - 「📌 作为桌面便签打开」把当前便签弹出为一个置顶小窗口（StickyNoteWindow），
///   可以随手记在桌面上，窗口里的改动同样自动写回同一个文件。
/// </summary>
public partial class StickyNotesTool : UserControl
{
    private static string NotesDir => Path.Combine(App.DataDir, "StickyNotes");

    private string? _currentFile;          // 当前正在编辑的便签文件路径
    private bool _suppressSave;            // 程序自己改文本框（加载内容）时抑制保存
    private bool _dirty;
    private bool _suppressNoteSelection;
    private bool _suppressStyleSelection;
    private bool _suppressLayoutSelection;
    private readonly DispatcherTimer _saveTimer;

    private sealed record NoteItem(string Path, string Title, string TimeText);

    public StickyNotesTool()
    {
        InitializeComponent();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick += (_, _) => SaveCurrent();
        RefreshNoteList();
        SelectStyle("Yellow");
        NoteStatusText.Text = "便签保存在：" + NotesDir;
    }

    // ==================== 列表 ====================

    private void RefreshNoteList()
    {
        try { Directory.CreateDirectory(NotesDir); } catch { /* 建不了会在读写时自然报错 */ }

        var items = new List<NoteItem>();
        try
        {
            items = new DirectoryInfo(NotesDir)
                .GetFiles("*.txt")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => new NoteItem(f.FullName, GetNoteTitle(f.FullName), f.LastWriteTime.ToString("yyyy-MM-dd HH:mm")))
                .ToList();
        }
        catch { /* 读取失败列表为空 */ }

        var prev = _currentFile;
        NoteList.ItemsSource = items;

        // 重新绑定后按路径还原选中项（新建/删除后列表刷新，选中态不应丢失）
        if (prev != null)
        {
            foreach (var it in items)
            {
                if (string.Equals(it.Path, prev, StringComparison.OrdinalIgnoreCase))
                {
                    NoteList.SelectedItem = it;
                    break;
                }
            }
        }
    }

    private static string GetNoteTitle(string path)
    {
        try
        {
            var firstLine = File.ReadLines(path).FirstOrDefault()?.Trim();
            if (!string.IsNullOrWhiteSpace(firstLine)) return firstLine.Length > 20 ? firstLine[..20] + "…" : firstLine;
        }
        catch { }
        return Path.GetFileNameWithoutExtension(path);
    }

    private void NoteList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressNoteSelection) return;
        if (!SaveCurrent())
        {
            // A failed write must not discard the old editor or redirect its text to another file.
            _suppressNoteSelection = true;
            try
            {
                NoteList.SelectedItem = NoteList.Items.OfType<NoteItem>().FirstOrDefault(item =>
                    string.Equals(item.Path, _currentFile, StringComparison.OrdinalIgnoreCase));
            }
            finally { _suppressNoteSelection = false; }
            return;
        }

        _currentFile = (NoteList.SelectedItem as NoteItem)?.Path;
        if (_currentFile == null)
        {
            NoteEditor.Clear();
            NoteEditor.IsEnabled = false;
            NotePinBtn.IsEnabled = false;
            NoteStyleCombo.IsEnabled = false;
            NoteAlignTopLeftCheck.IsEnabled = NoteSizeToTextCheck.IsEnabled = false;
            return;
        }

        _suppressSave = true;
        _dirty = false;
        NoteEditor.IsReadOnly = false;
        try
        {
            NoteEditor.Text = File.Exists(_currentFile)
                ? File.ReadAllText(_currentFile)
                : "";
        }
        catch (Exception ex)
        {
            NoteEditor.Clear();
            NoteEditor.IsReadOnly = true;
            NoteStatusText.Text = "读取便签失败，已暂停编辑以保护原文件：" + ex.Message;
            ToastService.ShowWarning(NoteStatusText.Text);
        }
        finally
        {
            _suppressSave = false;
        }
        NoteEditor.IsEnabled = true;
        NotePinBtn.IsEnabled = !NoteEditor.IsReadOnly;
        NoteStyleCombo.IsEnabled = true;
        SelectStyle(StickyNoteWindow.ReadStyleKey(_currentFile));
        NoteAlignTopLeftCheck.IsEnabled = NoteSizeToTextCheck.IsEnabled = true;
        _suppressLayoutSelection = true;
        try
        {
            var options = StickyNoteOptions.Load(_currentFile);
            NoteAlignTopLeftCheck.IsChecked = options.AlignTopLeft;
            NoteSizeToTextCheck.IsChecked = options.SizeToText;
            NoteEditor.TextAlignment = options.AlignTopLeft ? TextAlignment.Left : TextAlignment.Center;
            NoteEditor.VerticalContentAlignment = options.AlignTopLeft ? VerticalAlignment.Top : VerticalAlignment.Center;
        }
        catch (Exception ex)
        {
            // Display defaults for this note, not the previous note's checkbox values; do not write.
            NoteAlignTopLeftCheck.IsChecked = true;
            NoteSizeToTextCheck.IsChecked = false;
            NoteEditor.TextAlignment = TextAlignment.Left;
            NoteEditor.VerticalContentAlignment = VerticalAlignment.Top;
            NoteStatusText.Text = "便签布局读取失败，暂用默认布局：" + ex.Message;
            ToastService.ShowWarning(NoteStatusText.Text);
        }
        finally { _suppressLayoutSelection = false; }
    }

    // ==================== 编辑保存 ====================

    private void NoteEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSave || _currentFile == null || NoteEditor.IsReadOnly) return;
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start(); // 防抖：停下来不敲了 800ms 后再写盘
    }

    private void NoteEditor_LostFocus(object sender, RoutedEventArgs e) => SaveCurrent();

    private void StickyNotesTool_Unloaded(object sender, RoutedEventArgs e) => SaveCurrent();

    private bool SaveCurrent()
    {
        _saveTimer.Stop();
        // The desktop window can update the same file while this editor is inactive. A focus
        // or layout change must never write an unchanged, stale editor snapshot over that update.
        if (_currentFile == null || _suppressSave || !_dirty || NoteEditor.IsReadOnly) return true;
        try
        {
            Directory.CreateDirectory(NotesDir);
            File.WriteAllText(_currentFile, NoteEditor.Text);
            _dirty = false;
            NoteStatusText.Text = "已自动保存 " + Path.GetFileName(_currentFile);
            return true;
        }
        catch (Exception ex)
        {
            NoteStatusText.Text = "保存便签失败，正文仍保留在编辑区：" + ex.Message;
            ToastService.ShowWarning(NoteStatusText.Text);
            return false;
        }
    }

    private void SelectStyle(string styleKey)
    {
        _suppressStyleSelection = true;
        try
        {
            foreach (var item in NoteStyleCombo.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Tag?.ToString(), styleKey, StringComparison.OrdinalIgnoreCase))
                {
                    NoteStyleCombo.SelectedItem = item;
                    return;
                }
            }
            NoteStyleCombo.SelectedIndex = 0;
        }
        finally { _suppressStyleSelection = false; }
    }

    private void NoteStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressStyleSelection || _currentFile == null) return;
        var key = (NoteStyleCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Yellow";
        StickyNoteWindow.WriteStyleKey(_currentFile, key);
        NoteStatusText.Text = $"已切换便签样式：{(NoteStyleCombo.SelectedItem as ComboBoxItem)?.Content}";

        foreach (var window in StickyNoteWindow.OpenWindows.Where(w =>
                     string.Equals(w.FilePath, _currentFile, StringComparison.OrdinalIgnoreCase)))
            window.ApplyStyle(key, persist: false);
    }

    private void NoteLayout_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressLayoutSelection || _currentFile == null || NoteSizeToTextCheck == null) return;
        var options = new StickyNoteOptions
        {
            AlignTopLeft = NoteAlignTopLeftCheck.IsChecked == true,
            SizeToText = NoteSizeToTextCheck.IsChecked == true
        };
        try { options.Save(_currentFile); }
        catch (Exception ex)
        {
            NoteStatusText.Text = "便签布局未保存：" + ex.Message;
            ToastService.ShowWarning(NoteStatusText.Text);
            return;
        }
        NoteEditor.TextAlignment = options.AlignTopLeft ? TextAlignment.Left : TextAlignment.Center;
        NoteEditor.VerticalContentAlignment = options.AlignTopLeft ? VerticalAlignment.Top : VerticalAlignment.Center;
        foreach (var window in StickyNoteWindow.OpenWindows.Where(w =>
                     string.Equals(w.FilePath, _currentFile, StringComparison.OrdinalIgnoreCase)))
            window.ApplyLayout(options, persist: false);
        NoteStatusText.Text = "已保存便签布局。";
    }

    // ==================== 按钮 ====================

    private void NoteNew_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(NotesDir, $"便签-{DateTime.Now:yyyyMMddHHmmss}.txt");
        try
        {
            Directory.CreateDirectory(NotesDir);
            File.WriteAllText(path, "新便签\n");
        }
        catch (Exception ex)
        {
            MessageBoxDialog.ShowError($"创建便签失败：{ex.Message}");
            return;
        }
        RefreshNoteList();
        foreach (var it in NoteList.ItemsSource as IEnumerable<NoteItem> ?? Enumerable.Empty<NoteItem>())
        {
            if (string.Equals(it.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                NoteList.SelectedItem = it;
                NoteList.ScrollIntoView(it);
                break;
            }
        }
        NoteEditor.Focus();
    }

    private void NoteDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFile == null) return;
        if (!MessageBoxDialog.ShowConfirm(
                $"确定删除便签「{Path.GetFileName(_currentFile)}」吗？删除后无法恢复。",
                "删除便签")) return;

        if (!SaveCurrent()) return;
        // Close matching editors before deleting. Otherwise their pending autosave recreates
        // the file (and can leave layout/style sidecars behind after an apparent deletion).
        foreach (var window in StickyNoteWindow.OpenWindows.Where(window =>
                     string.Equals(window.FilePath, _currentFile, StringComparison.OrdinalIgnoreCase)).ToArray())
            window.Close();
        if (StickyNoteWindow.OpenWindows.Any(window =>
                string.Equals(window.FilePath, _currentFile, StringComparison.OrdinalIgnoreCase)))
        {
            NoteStatusText.Text = "便签窗口尚未成功保存并关闭，已取消删除。";
            ToastService.ShowWarning(NoteStatusText.Text);
            return;
        }
        try
        {
            File.Delete(_currentFile);
            try { File.Delete(_currentFile + ".style"); File.Delete(StickyNoteOptions.OptionsPath(_currentFile)); } catch { }
        }
        catch (Exception ex) { MessageBoxDialog.ShowError($"删除失败：{ex.Message}"); return; }
        _currentFile = null;
        RefreshNoteList();
        NoteStatusText.Text = "已删除便签";
    }

    private void NoteOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(NotesDir); } catch { }
        FolderOpenHelper.Open(NotesDir);
    }

    private void NotePin_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFile == null || NoteEditor.IsReadOnly) return;
        if (!SaveCurrent()) return;
        var styleKey = (NoteStyleCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? StickyNoteWindow.ReadStyleKey(_currentFile);
        var existing = StickyNoteWindow.OpenWindows.FirstOrDefault(w =>
            string.Equals(w.FilePath, _currentFile, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.ApplyStyle(styleKey);
            try { existing.ApplyLayout(StickyNoteOptions.Load(_currentFile), persist: false); }
            catch (Exception ex) { ToastService.ShowWarning("便签布局读取失败：" + ex.Message); }
            existing.Topmost = true;
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Show();
            existing.Activate();
            NoteStatusText.Text = $"已显示桌面便签：{Path.GetFileName(_currentFile)}";
            return;
        }
        var win = new StickyNoteWindow(_currentFile, styleKey) { Topmost = true };
        win.Show();
        NoteStatusText.Text = $"已弹出桌面便签：{Path.GetFileName(_currentFile)}（窗口置顶，可拖动）";
    }
}
