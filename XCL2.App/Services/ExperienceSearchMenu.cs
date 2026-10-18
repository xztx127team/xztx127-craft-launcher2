using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>History/presets remain in the search box's contextual menu. Every write is a user action.</summary>
public static class ExperienceSearchMenu
{
    private const string MenuTag = "XCL2.ExperienceSearch";
    public static void Attach(TextBox box, Func<AppConfig> config, Action save, string scope,
        Func<Dictionary<string, string>> capture, Action<Dictionary<string, string>> apply)
    {
        if (box.ContextMenu == null)
        {
            box.ContextMenu = new ContextMenu();
            foreach (var pair in new[] { ("撤销", ApplicationCommands.Undo), ("剪切", ApplicationCommands.Cut),
                ("复制", ApplicationCommands.Copy), ("粘贴", ApplicationCommands.Paste), ("全选", ApplicationCommands.SelectAll) })
                box.ContextMenu.Items.Add(new MenuItem { Header = pair.Item1, Command = pair.Item2, CommandTarget = box });
        }
        box.ContextMenuOpening += (_, _) =>
        {
            var menu = box.ContextMenu;
            if (menu == null) return;
            foreach (var old in menu.Items.OfType<FrameworkElement>().Where(item => Equals(item.Tag, MenuTag)).ToArray()) menu.Items.Remove(old);
            var cfg = config();
            if (!cfg.Ux50SearchSavedFilters && !cfg.Ux50SearchHistory) return;
            ExperienceSearchState.Normalize(cfg);
            menu.Items.Add(new Separator { Tag = MenuTag });
            if (cfg.Ux50SearchSavedFilters)
            {
                var presets = new MenuItem { Header = "筛选组合", Tag = MenuTag };
                var saveCurrent = new MenuItem { Header = "保存当前筛选" };
                saveCurrent.Click += (_, _) =>
                {
                    var values = capture();
                    ExperienceSearchState.SavePreset(cfg, scope, DescribeFilter(values), values);
                    SaveSafely(save);
                };
                presets.Items.Add(saveCurrent);
                foreach (var preset in (cfg.Ux50SearchFilterPresets ?? new()).Where(item => item.Scope == scope).Take(ExperienceSearchState.PresetLimit).ToArray())
                {
                    var item = new MenuItem { Header = SafeLabel(preset.Label) };
                    var use = new MenuItem { Header = "应用" };
                    use.Click += (_, _) => apply(new(preset.Values ?? new()));
                    var remove = new MenuItem { Header = "删除此组合" };
                    remove.Click += (_, _) => { cfg.Ux50SearchFilterPresets?.Remove(preset); SaveSafely(save); };
                    item.Items.Add(use); item.Items.Add(remove); presets.Items.Add(item);
                }
                menu.Items.Add(presets);
            }
            if (cfg.Ux50SearchHistory)
            {
                var history = new MenuItem { Header = "搜索历史（仅本机）", Tag = MenuTag };
                foreach (var record in (cfg.Ux50SearchHistoryItems ?? new()).Where(item => item.Scope == scope).Take(ExperienceSearchState.HistoryLimit).ToArray())
                {
                    var item = new MenuItem { Header = SafeLabel(record.Query) };
                    var use = new MenuItem { Header = "填入搜索框" };
                    use.Click += (_, _) => { box.Text = record.Query; box.Focus(); box.CaretIndex = box.Text.Length; };
                    var remove = new MenuItem { Header = "删除这条" };
                    remove.Click += (_, _) => { cfg.Ux50SearchHistoryItems?.Remove(record); SaveSafely(save); };
                    item.Items.Add(use); item.Items.Add(remove); history.Items.Add(item);
                }
                var clear = new MenuItem { Header = "清空全部搜索历史", IsEnabled = cfg.Ux50SearchHistoryItems?.Count > 0 };
                clear.Click += (_, _) => { cfg.Ux50SearchHistoryItems?.Clear(); SaveSafely(save); };
                history.Items.Add(clear); menu.Items.Add(history);
            }
        };
    }
    public static void Commit(AppConfig config, string scope, string query, Action save)
    {
        if (!config.Ux50SearchHistory || string.IsNullOrWhiteSpace(query)) return;
        ExperienceSearchState.Remember(config, scope, query);
        SaveSafely(save);
    }
    private static string DescribeFilter(Dictionary<string, string> values)
    {
        var parts = new List<string>();
        foreach (var pair in values.Where(pair => !string.IsNullOrWhiteSpace(pair.Value)))
        {
            string value = pair.Value;
            string description = pair.Key switch
            {
                "query" => value,
                "version" => "版本 " + value,
                "loader" => "加载器 " + value,
                "all" => value == "True" ? "全部页面" : "当前页面",
                "source" => value switch { "combined" => "综合来源", "modrinth" => "Modrinth", "curseforge" => "CurseForge", _ => "插件来源" },
                "category" => value switch { "Release" => "正式版", "Snapshot" => "快照版", "Legacy" => "远古版", "AprilFools" => "愚人节版", _ => "" },
                "type" => value switch { "ResourcePack" => "资源包", "DataPack" => "数据包", "Shader" => "光影包", "Modpack" => "整合包", _ => "" },
                "preview" => value == "True" ? "含预览版" : "",
                _ => ""
            };
            if (description.Length > 0) parts.Add(description);
        }
        return parts.Count == 0 ? "默认筛选" : string.Join(" · ", parts);
    }
    private static string SafeLabel(string text) => (text.Length > 54 ? text[..54] + "…" : text).Replace("_", "__");
    private static void SaveSafely(Action save)
    {
        try { save(); }
        catch (Exception ex) { LauncherLogService.AppendLine("[搜索偏好] 保存失败：" + ex.Message); }
    }
}
