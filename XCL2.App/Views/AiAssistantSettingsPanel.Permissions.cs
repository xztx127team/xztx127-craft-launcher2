using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;
using XCL2.App.Services;

namespace XCL2.App.Views;

public partial class AiAssistantSettingsPanel
{
    public sealed class ExactCommandPermissionRow
    {
        public string Shell { get; set; } = "powershell";
        public string Command { get; set; } = "";
        public bool Allowed { get; set; }
    }
    private readonly ObservableCollection<ExactCommandPermissionRow> _exactCommandPermissions = new();
    private readonly Dictionary<string, CheckBox> _commandPermissionChecks = new(StringComparer.Ordinal);
    private static readonly (string Key, string Label)[] CommandCategories = {
        ("Post", "Post · 内置工具"), ("PowerShell", "PowerShell"), ("Shell", "Shell"), ("Cmd", "CMD"), ("Python", "Python") };
    private static readonly (string Key, string Label)[] CommandOperations = {
        ("Read", "读取"), ("Create", "新建"), ("Modify", "修改"), ("Delete", "删除"),
        ("RunPython", "运行 Python"), ("ShellCommand", "Shell 命令"), ("Other", "其他 / 未识别"),
        ("OpenWindow", "打开窗口"), ("CloseWindow", "关闭窗口"), ("MouseMove", "鼠标移动"),
        ("MouseClick", "鼠标点击"), ("MouseRightClick", "鼠标右键"), ("Keyboard", "键盘") };

    private void LoadCommandPermissionSettings(AiAssistantConfig config)
    {
        if (_commandPermissionChecks.Count == 0)
        {
            AddPermissionChecks(CommandCategoryPermissionsPanel, CommandCategories);
            AddPermissionChecks(CommandOperationPermissionsPanel, CommandOperations);
            ExactCommandPermissionsGrid.ItemsSource = _exactCommandPermissions;
        }
        var permissions = config.CommandPermissions ?? new AiCommandPermissions();
        foreach (var pair in _commandPermissionChecks)
            pair.Value.IsChecked = typeof(AiCommandPermissions).GetProperty(pair.Key)?.GetValue(permissions) is true;
        AllowAllShellCommandsCheck.IsChecked = config.AllowAllShellCommands;
        _exactCommandPermissions.Clear();
        foreach (var pair in permissions.ExactCommands ?? new Dictionary<string, bool>())
        {
            var separator = pair.Key.IndexOf('|');
            if (separator <= 0) continue;
            _exactCommandPermissions.Add(new ExactCommandPermissionRow { Shell = pair.Key[..separator], Command = pair.Key[(separator + 1)..], Allowed = pair.Value });
        }
        AgentAnimationProfileCombo.SelectedItem = AgentAnimationProfileCombo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => Equals(i.Tag, config.AgentAnimationProfile.ToString())) ?? AgentAnimationProfileCombo.Items[0];
    }

    private void AddPermissionChecks(Panel target, IEnumerable<(string Key, string Label)> fields)
    {
        foreach (var (key, label) in fields)
        {
            var check = new CheckBox { Content = label, Margin = new Thickness(0, 4, 16, 4), MinWidth = 90 };
            _commandPermissionChecks[key] = check;
            target.Children.Add(check);
        }
    }

    private AiCommandPermissions SnapshotCommandPermissions()
    {
        ExactCommandPermissionsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        ExactCommandPermissionsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var permissions = new AiCommandPermissions();
        foreach (var pair in _commandPermissionChecks)
            typeof(AiCommandPermissions).GetProperty(pair.Key)?.SetValue(permissions, pair.Value.IsChecked == true);
        permissions.ExactCommands = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var row in _exactCommandPermissions)
        {
            if (string.IsNullOrWhiteSpace(row.Command)) continue;
            var key = AiCommandPermissionService.GetCommandKey(row.Shell, row.Command);
            // Duplicate deny always wins over allow.
            permissions.ExactCommands[key] = !permissions.ExactCommands.TryGetValue(key, out var previous) ? row.Allowed : previous && row.Allowed;
        }
        return permissions;
    }

    private AiAgentAnimationProfile GetAgentAnimationProfile() => AgentAnimationProfileCombo.SelectedItem is ComboBoxItem { Tag: string tag }
        && Enum.TryParse<AiAgentAnimationProfile>(tag, out var profile) && Enum.IsDefined(profile) ? profile : AiAgentAnimationProfile.Balanced;

    private void SelectAllCommandPermissions_Click(object sender, RoutedEventArgs e)
    {
        ExactCommandPermissionsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        ExactCommandPermissionsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        foreach (var check in _commandPermissionChecks.Values) check.IsChecked = true;
        foreach (var row in _exactCommandPermissions) row.Allowed = true;
        ExactCommandPermissionsGrid.Items.Refresh();
        AllowShellCommandsCheck.IsChecked = true;
        AllowAllShellCommandsCheck.IsChecked = true;
    }
    private void ClearCommandPermissions_Click(object sender, RoutedEventArgs e)
    {
        foreach (var check in _commandPermissionChecks.Values) check.IsChecked = false;
        foreach (var row in _exactCommandPermissions) row.Allowed = false;
        ExactCommandPermissionsGrid.Items.Refresh();
        AllowShellCommandsCheck.IsChecked = false;
        AllowAllShellCommandsCheck.IsChecked = false;
    }

    private static void RestrictImportedCommandPermissions(AiAssistantConfig config, AiCommandPermissions? imported)
    {
        if (imported == null) return;
        config.CommandPermissions ??= new AiCommandPermissions();
        foreach (var property in typeof(AiCommandPermissions).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.PropertyType == typeof(bool)))
            if (property.GetValue(imported) is false) property.SetValue(config.CommandPermissions, false);
        config.CommandPermissions.ExactCommands ??= new();
        foreach (var pair in imported.ExactCommands ?? new())
            if (!pair.Value) config.CommandPermissions.ExactCommands[pair.Key] = false;
    }
}
