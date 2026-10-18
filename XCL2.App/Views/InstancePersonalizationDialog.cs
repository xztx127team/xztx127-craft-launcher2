using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;

namespace XCL2.App.Views;

/// <summary>只在实例菜单打开的个人备注编辑器，不改变实例名或整合包文件。</summary>
internal sealed class InstancePersonalizationDialog : OverlayDialogControl
{
    private readonly TextBox _notes;
    private readonly TextBox _tags;
    public string Notes => _notes.Text;
    public string[] Tags => _tags.Text.Split(new[] { ',', '，', ';', '；', '\n' }, StringSplitOptions.RemoveEmptyEntries);

    public InstancePersonalizationDialog(string name, InstancePersonalization? existing)
    {
        var owner = Application.Current?.MainWindow;
        Width = Math.Max(260, Math.Min(420, (owner?.ActualWidth ?? 854) - 100));
        MaxHeight = Math.Max(180, Math.Min(540, (owner?.ActualHeight ?? 640) - 100));
        var panel = new StackPanel { Margin = new Thickness(20) };
        var title = new TextBlock { Text = "实例备注与标签", FontSize = 18, FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        panel.Children.Add(title);
        panel.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = name, Margin = new Thickness(0, 6, 0, 16) });
        panel.Children.Add(new TextBlock { Text = "标签（用逗号分隔，最多 12 个）", Margin = new Thickness(0, 0, 0, 6) });
        _tags = new TextBox { Text = string.Join("，", existing?.Tags ?? new()), MaxLength = 500, MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center };
        panel.Children.Add(_tags);
        panel.Children.Add(new TextBlock { Text = "个人备注", Margin = new Thickness(0, 14, 0, 6) });
        _notes = new TextBox { Text = existing?.Notes ?? "", MaxLength = 4000, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 160 };
        panel.Children.Add(_notes);
        var hint = new TextBlock { Text = "仅保存在当前启动器设置中，不写入整合包原始文件。", FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        panel.Children.Add(hint);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "取消", MinWidth = 80, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6, 12, 6), IsCancel = true };
        cancel.Click += (_, _) => CloseWith(false);
        var save = new Button { Content = "保存", MinWidth = 80, Padding = new Thickness(12, 6, 12, 6) };
        save.SetResourceReference(StyleProperty, "PrimaryButton");
        save.Click += (_, _) => CloseWith(true);
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = MaxHeight };
        Loaded += (_, _) => _tags.Focus();
    }
}
