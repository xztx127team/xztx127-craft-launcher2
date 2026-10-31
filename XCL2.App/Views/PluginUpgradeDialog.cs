using System.IO;
using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XCL2.App.Services;
using XCL2.App.Services.Plugins;

namespace XCL2.App.Views;

internal sealed class PluginUpgradeDialog : OverlayDialogControl, IDisposable
{
    private readonly PluginManager _manager;
    private readonly LoadedPluginInfo _plugin;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _report = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 190 };
    private readonly CheckBox _consent = new() { Content = "我已知悉升级风险、备份范围和本地编译行为", Margin = new Thickness(0, 12, 0, 12) };
    private readonly Button _prepare = new() { Content = "备份并生成升级包", Padding = new Thickness(12, 7, 12, 7) };
    private readonly Button _install = new() { Content = "确认安装此升级包", IsEnabled = false, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _sourceButton = new() { Content = "导入源码（.csproj / .zip，可选）", Margin = new Thickness(0, 8, 0, 8) };
    private readonly Button _register = new() { Content = "插火", ToolTip = "检查并注册所需权限", Visibility = Visibility.Collapsed, Padding = new Thickness(12, 7, 12, 7) };
    private readonly Button _export = new() { Content = "导出插件", Padding = new Thickness(12, 7, 12, 7), MinWidth = 112 };
    private readonly Button _exportSource = new() { Content = "导出源码", Padding = new Thickness(12, 7, 12, 7), MinWidth = 112 };
    private readonly ComboBox _exportFormat = new() { MinWidth = 170, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ComboBox _sourceFormat = new() { MinWidth = 170, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly StackPanel _exports = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 14, 0, 8) };
    private readonly TextBlock _exportHint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Opacity = 0.8 };
    private readonly TextBlock _sourceLabel = new() { Text = "使用已安装插件二进制", TextWrapping = TextWrapping.Wrap };
    private readonly CancellationTokenSource _stop = new();
    private PluginUpgradeResult? _result;
    private string? _source;
    private string? _installedEntry;
    private bool _busy;
    private bool _closed;
    private bool _resourcesDisposed;
    internal PluginUpgradeDialog(PluginManager manager, LoadedPluginInfo plugin)
    {
        _manager = manager; _plugin = plugin; Width = 720; MaxHeight = 780;
        var panel = new StackPanel { Margin = new Thickness(22) };
        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = "升级插件", FontSize = 23, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_register, 1); header.Children.Add(_register); panel.Children.Add(header);
        panel.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(32, 255, 164, 48)), CornerRadius = new CornerRadius(10), Padding = new Thickness(14), Child = new TextBlock
        {
            Text = "升级可能损坏插件、配置或其他文件。启动器会先暂停插件，自动备份并校验全部插件安装文件、配置、数据和启用状态。备份不涵盖游戏存档或任意外部文件。\n\n旧二进制迁移只更新清单与签名，不保证接口自动兼容。新版包使用本机 ECDSA 自签名，仅用于完整性检查，不冒充原作者签名。\n\n导入源码后将在临时副本中运行 dotnet build。MSBuild 目标可以执行代码，依赖恢复可能联网；仅导入可信源码。源码不会被上传给远程 AI。", TextWrapping = TextWrapping.Wrap
        } });
        panel.Children.Add(new TextBlock
        {
            Text = "权限检查：将按升级后插件的清单和可识别的宿主调用申请权限。反射、原生 EXE、动态管道请求或缺失依赖可能有权限不准问题；无法确认的项目会列入报告，安装前请核对。不会自动补入或授予两个默认权限。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 4)
        });
        panel.Children.Add(_sourceButton); panel.Children.Add(_sourceLabel);
        _sourceButton.Click += (_, _) => { var picker = new Microsoft.Win32.OpenFileDialog { Filter = "源码项目 / ZIP|*.csproj;*.zip", Title = "选择插件源码（ZIP 中须只有一个入口 csproj）" }; if (picker.ShowDialog() == true) { _source = picker.FileName; _sourceLabel.Text = _source; ClearResult(); UpdateActions(); } };
        panel.Children.Add(_consent);
        var actions = new WrapPanel(); actions.Children.Add(_prepare); actions.Children.Add(_install);
        var close = new Button { Content = "关闭 / 停止", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 7, 12, 7) };
        close.Click += (_, _) => { if (_busy) { _stop.Cancel(); _status.Text = "已请求停止，请等待当前操作结束。"; } else CloseWith(null); };
        actions.Children.Add(close); panel.Children.Add(actions);
        _exportFormat.ItemsSource = new[] { "XCL 插件包（.xcl）", "DLL + 清单与依赖", "EXE + 清单与依赖" }; _exportFormat.SelectedIndex = 0;
        _sourceFormat.ItemsSource = new[] { "源码文件夹", "源码 ZIP" }; _sourceFormat.SelectedIndex = 0;
        _exports.Children.Add(ExportRow("插件格式", _exportFormat, _export));
        _exports.Children.Add(ExportRow("源码格式", _sourceFormat, _exportSource));
        _exports.Children.Add(_exportHint); panel.Children.Add(_exports);
        _status.Margin = new Thickness(0, 10, 0, 10);
        panel.Children.Add(_status); panel.Children.Add(_report);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _prepare.Click += async (_, _) => await PrepareAsync(); _install.Click += (_, _) => Install();
        _register.Click += async (_, _) => await RegisterRequiredPermissionsAsync();
        _export.Click += async (_, _) => await ExportAsync(false);
        _exportSource.Click += async (_, _) => await ExportAsync(true);
        _exportFormat.SelectionChanged += (_, _) => UpdateExportHint();
        // A nested permissions/confirmation dialog temporarily unloads its parent. Only actual closure cancels work.
        UpdateActions();
    }

    private static Grid ExportRow(string title, ComboBox format, Button button)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        format.Margin = new Thickness(0, 0, 12, 0); Grid.SetColumn(format, 1); row.Children.Add(format);
        Grid.SetColumn(button, 2); row.Children.Add(button);
        return row;
    }

    private void ClearResult() { _result?.Dispose(); _result = null; _exports.Visibility = Visibility.Collapsed; }

    private void UpdateActions()
    {
        if (_closed) return;
        var ready = !_busy && !_stop.IsCancellationRequested;
        _prepare.IsEnabled = _sourceButton.IsEnabled = _consent.IsEnabled = ready && _installedEntry == null;
        _install.IsEnabled = ready && _result != null && _installedEntry == null;
        _register.IsEnabled = ready;
        _export.IsEnabled = _exportFormat.IsEnabled = ready && _result != null;
        _exportSource.IsEnabled = _sourceFormat.IsEnabled = ready && _result?.SourceDirectory != null;
        _exports.Visibility = _result == null ? Visibility.Collapsed : Visibility.Visible;
        UpdateExportHint();
    }

    private void UpdateExportHint()
    {
        if (_result == null) return;
        _exportHint.Text = (_exportFormat.SelectedIndex == 0 ? "XCL 包包含入口、清单、依赖及签名；兼容原 .xclplugin 导入流程。" :
            "当前入口：" + _result.Entry + "。DLL / EXE 按实际入口类型导出到新文件夹，并保留清单、签名和依赖；两种运行契约不能通过改扩展名转换。") +
            (_result.SourceDirectory == null ? "\n本次未导入源码，因此没有可导出的源码副本；可先导入 .csproj 或源码 ZIP 再生成。" :
            "\n源码导出包含更新后的 plugin.json 和迁移报告，原始源码保持不变。");
    }

    private void EndBusy()
    {
        _busy = false;
        if (_closed) ReleaseResources(); else UpdateActions();
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true; _stop.Cancel();
        if (!_busy) ReleaseResources();
    }

    private void ReleaseResources()
    {
        if (_resourcesDisposed) return;
        _resourcesDisposed = true; ClearResult(); _stop.Dispose();
    }
    private async Task PrepareAsync()
    {
        if (_closed || _busy || _stop.IsCancellationRequested) return;
        if (_consent.IsChecked != true) { _status.Text = "请先阅读并勾选升级说明。"; return; }
        var picker = new Microsoft.Win32.SaveFileDialog { Filter = "XCL 插件包|*.xcl|兼容插件包|*.xclplugin", FileName = Path.GetFileNameWithoutExtension(_plugin.FilePath) + "-api2", DefaultExt = ".xcl", AddExtension = true, OverwritePrompt = true };
        if (picker.ShowDialog() != true) return;
        if (_closed) return;
        if (!PluginUpgradeExport.IsPackage(picker.FileName)) { _status.Text = "升级包请使用 .xcl 或 .xclplugin 扩展名。"; return; }
        if (File.Exists(picker.FileName)) { _status.Text = "请选择尚不存在的新文件名，避免覆盖之前的升级包。"; return; }
        _busy = true; ClearResult(); UpdateActions();
        try
        {
            _status.Text = "正在暂停插件并创建备份…";
            var backup = await _manager.BackupBeforeUpgradeAsync(_stop.Token);
            _report.Text = "备份已校验：" + backup;
            _result = await Task.Run(() => PluginUpgradeService.PrepareAsync(_plugin, _source, picker.FileName,
                new Progress<string>(message => Dispatcher.BeginInvoke(new Action(() => _status.Text = message))), _stop.Token));
            _report.Text = "备份：" + backup + "\n\n" + _result.Report + "\n签名公钥 SHA-256：" + _result.Fingerprint + "\n升级包：" + _result.Package;
            _status.Text = string.IsNullOrEmpty(_result.PermissionWarning) ? "升级包已生成，可以安装或导出。" : _result.PermissionWarning;
        }
        catch (OperationCanceledException) { _status.Text = "已停止。已完成的备份保留；没有安装新插件。"; }
        catch (Exception ex) { _status.Text = "升级准备失败，原插件未替换：" + ex.Message; }
        finally
        {
            try { _manager.ScanAndLoad(); } catch (Exception ex) { _status.Text += "\n恢复插件运行失败：" + ex.Message; }
            EndBusy();
        }
    }
    private void Install()
    {
        if (_closed || _result == null || _busy) return;
        if (!MessageBoxDialog.ShowConfirm("请核对上方迁移报告。\n\n" + _result.PermissionWarning + "\n\n安装会替换当前选择的插件，并再次备份当前插件文件。新插件保持禁用，之后点击右上角“插火”检查并注册所需权限、启用插件。升级仍可能有兼容性问题。\n\n包：" + _result.Package, "升级插件")) return;
        if (_closed) return;
        _busy = true; UpdateActions();
        try
        {
            _installedEntry = _manager.InstallUpgrade(_result.Package, _plugin.FilePath);
            _manager.SetEnabled(_installedEntry, false); _install.IsEnabled = false;
            _prepare.IsEnabled = _sourceButton.IsEnabled = _consent.IsEnabled = false;
            _register.Visibility = Visibility.Visible;
            _status.Text = "升级已完成，当前保持禁用。点击右上角“插火”检查并注册插件所需权限。备份：" + _manager.LastBackupPath;
        }
        catch (Exception ex) { _status.Text = "安装失败：" + ex.Message; }
        finally { try { _manager.ScanAndLoad(); } catch (Exception ex) { _status.Text += "\n扫描失败：" + ex.Message; } EndBusy(); }
    }

    private async Task RegisterRequiredPermissionsAsync()
    {
        if (_closed || _busy || _installedEntry == null) return;
        _busy = true; UpdateActions();
        try
        {
            // Re-read and verify the installed signed manifest; the old plugin's defaults/snapshot are never used.
            var inspection = await Task.Run(() =>
            {
                var current = PluginManifestReader.Read(_installedEntry, "") ?? throw new IOException("升级后的插件缺少权限清单。");
                return (Metadata: current, Analysis: PluginUpgradePermissions.Analyze(_installedEntry, _stop.Token));
            }, _stop.Token);
            _stop.Token.ThrowIfCancellationRequested();
            var metadata = inspection.Metadata;
            var undeclared = inspection.Analysis.Required.Where(p => !metadata.Permissions.Contains(p, StringComparer.Ordinal)).ToArray();
            if (undeclared.Length > 0)
                throw new IOException("升级清单遗漏了实际调用需要的权限：" + string.Join("、", undeclared) + "。请重新生成升级包；不会用默认权限代替。");
            var granted = _manager.GetGrantedPermissions(_installedEntry).Where(p => metadata.Permissions.Contains(p, StringComparer.Ordinal)).ToHashSet(StringComparer.Ordinal);
            var missing = metadata.Permissions.Where(p => !granted.Contains(p)).ToImmutableArray();
            if (missing.Length > 0)
            {
                var dialog = new PluginPermissionsDialog(metadata with { Permissions = missing }, Array.Empty<string>());
                if (await OverlayDialogService.ShowModalAsync(dialog) != true) { _status.Text = "尚未注册新增权限，插件继续保持禁用。"; return; }
                _stop.Token.ThrowIfCancellationRequested();
                granted.UnionWith(dialog.Granted);
            }
            // This writes the real permission fingerprint, updates the live service and clears OnceOnImport's flag.
            _manager.SetPermissions(_installedEntry, granted);
            if (metadata.Permissions.Any(p => !granted.Contains(p) || !PluginPermissionService.IsAvailable(p)))
            {
                _manager.SetEnabled(_installedEntry, false);
                _manager.ScanAndLoad();
                _status.Text = "已保存选中的授权；仍有未获授权或当前不可用的所需权限，插件继续保持禁用。";
                return;
            }
            _manager.SetEnabled(_installedEntry, true);
            _manager.ScanAndLoad();
            var loaded = _manager.Plugins.FirstOrDefault(p => p.FilePath.Equals(_installedEntry, StringComparison.OrdinalIgnoreCase));
            if (loaded == null) { _status.Text = "所需权限已注册，但扫描时没有找到升级后的插件。请检查安装目录。"; return; }
            if (loaded.LoadError is { } error) { _status.Text = "所需权限已注册，但插件加载失败：" + error; return; }
            if (!loaded.Enabled) { _status.Text = "所需权限已注册，但插件尚未启用。"; return; }
            if (loaded.Context == null || loaded.IsExecutable && loaded.RunningProcess is not { HasExited: false })
            { _status.Text = "所需权限已注册，插件已启用；当前没有运行实例。"; return; }
            _status.Text = "所需权限已注册，插件已启用。";
            ToastService.ShowSuccess("插件所需权限已注册并启用。");
        }
        catch (OperationCanceledException) { _status.Text = "已停止权限检查。"; }
        catch (Exception ex) { _status.Text = "检查并注册权限失败：" + ex.Message; }
        finally { EndBusy(); }
    }

    private async Task ExportAsync(bool source)
    {
        if (_closed || _busy || _result == null || source && _result.SourceDirectory == null) return;
        var result = _result;
        var format = (PluginExportFormat)_exportFormat.SelectedIndex;
        var zip = source ? _sourceFormat.SelectedIndex == 1 : format == PluginExportFormat.Xcl;
        string destination;
        try
        {
            if (!source) PluginUpgradeExport.RequireFormat(result.Entry, format);
            var name = Path.GetFileNameWithoutExtension(result.Entry) + (source ? "-updated-source" : "-upgraded");
            if (zip)
            {
                var picker = new Microsoft.Win32.SaveFileDialog { Title = source ? "导出更新后的源码 ZIP" : "导出升级后的 XCL 插件包",
                    Filter = source ? "源码 ZIP|*.zip" : "XCL 插件包|*.xcl|兼容插件包|*.xclplugin", DefaultExt = source ? ".zip" : ".xcl",
                    FileName = name, AddExtension = true, OverwritePrompt = true };
                if (picker.ShowDialog() != true) return;
                destination = picker.FileName;
                if (source ? !Path.GetExtension(destination).Equals(".zip", StringComparison.OrdinalIgnoreCase) : !PluginUpgradeExport.IsPackage(destination))
                    throw new IOException(source ? "源码压缩包请使用 .zip 扩展名。" : "插件包请使用 .xcl 或 .xclplugin 扩展名。");
            }
            else
            {
                var picker = new XCL2.App.Services.CompatibleFolderDialog { Title = "选择导出位置（将在其中新建 " + name + " 文件夹）" };
                if (picker.ShowDialog() != true) return;
                destination = Path.Combine(picker.FolderName, name);
                for (var i = 2; File.Exists(destination) || Directory.Exists(destination); i++) destination = Path.Combine(picker.FolderName, name + "-" + i);
            }
            if (_closed) return;
            _busy = true; UpdateActions(); _status.Text = "正在导出…";
            await Task.Run(() =>
            {
                var directory = source ? result.SourceDirectory! : result.PayloadDirectory;
                if (!source) _ = PluginManifestReader.ReadEntry(directory);
                if (zip) PluginUpgradeExport.ExportZip(directory, destination, _stop.Token, skipBuild: source);
                else PluginUpgradeExport.ExportDirectory(directory, destination, _stop.Token, skipBuild: source,
                    validate: source ? null : path => { _ = PluginManifestReader.ReadEntry(path); });
            }, _stop.Token);
            _status.Text = (source ? "源码已导出：" : "插件已导出：") + destination;
            if (!source && !zip) _status.Text += "\n导入时选择其中的 " + result.Entry + "，请保留同目录的清单、签名和依赖。";
        }
        catch (OperationCanceledException) { _status.Text = "导出已停止，现有文件未覆盖。"; }
        catch (Exception ex) { _status.Text = "导出失败：" + ex.Message; }
        finally { if (_busy) EndBusy(); }
    }
}
