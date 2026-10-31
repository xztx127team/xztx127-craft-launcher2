using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace XCL2.App.Services;

/// <summary>
/// AI-output-only context menu. It deliberately has no selection/mouse-up popup handler:
/// dragging text selects it; left-clicking an action, link or copy button keeps its native action.
/// </summary>
internal static class AiTextSelectionMenu
{
    internal static void Attach(TextBoxBase editor, Func<string> selection, Action selectAll,
        Func<string>? allCode = null)
    {
        var menu = CreateMenu(editor);
        var copy = CreateItem("复制", "Ctrl+C");
        var all = CreateItem("全选", "Ctrl+A");
        string selectedAtOpen = string.Empty;
        copy.Click += (_, _) => AiTextClipboard.TryCopy(selectedAtOpen);
        all.Click += (_, _) => { editor.Focus(); selectAll(); };
        menu.Items.Add(copy);
        if (allCode != null)
        {
            var copyCode = CreateItem("复制全部代码", string.Empty);
            copyCode.Click += (_, _) => AiTextClipboard.TryCopy(allCode());
            menu.Items.Add(copyCode);
        }
        menu.Items.Add(all);
        menu.Opened += (_, _) =>
        {
            selectedAtOpen = selection();
            copy.IsEnabled = selectedAtOpen.Length > 0;
        };
        editor.ContextMenu = menu;
        editor.ContextMenuOpening += (_, e) =>
        {
            // An outer RichTextBox must not lend its menu to embedded controls. Checking the
            // original source also leaves code-box menus bound to their own selection.
            var source = e.OriginalSource as DependencyObject;
            // A nested code editor has already supplied its own menu; do not cancel it.
            if (FindTextEditor(source) is { } owner && !ReferenceEquals(owner, editor)) return;
            if (!OwnsTextInput(editor, source)) e.Handled = true;
        };
        editor.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
            (_, e) =>
            {
                if (!OwnsTextInput(editor, e.OriginalSource as DependencyObject)) return;
                AiTextClipboard.TryCopy(selection());
                e.Handled = true;
            },
            (_, e) =>
            {
                if (!OwnsTextInput(editor, e.OriginalSource as DependencyObject)) return;
                e.CanExecute = selection().Length > 0;
                e.Handled = true;
            }));
        editor.Unloaded += (_, _) => menu.IsOpen = false;
    }

    internal static bool OwnsTextInput(TextBoxBase editor, DependencyObject? source)
    {
        for (var current = source; current != null; current = Parent(current))
        {
            if (current is ButtonBase or ScrollBar or Thumb or Hyperlink or BlockUIContainer or InlineUIContainer) return false;
            if (current is TextBoxBase text) return ReferenceEquals(text, editor);
        }
        return false;
    }

    private static TextBoxBase? FindTextEditor(DependencyObject? source)
    {
        for (var current = source; current != null; current = Parent(current))
            if (current is TextBoxBase editor) return editor;
        return null;
    }

    private static DependencyObject? Parent(DependencyObject current)
    {
        if (current is ContentElement content)
            return ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent;
        if (current is FrameworkElement element && element.Parent != null) return element.Parent;
        if (current is Visual or Visual3D) return VisualTreeHelper.GetParent(current);
        return LogicalTreeHelper.GetParent(current);
    }

    private static ContextMenu CreateMenu(Control editor)
    {
        var menu = new ContextMenu { PlacementTarget = editor, StaysOpen = false, Padding = new Thickness(5) };
        menu.SetResourceReference(Control.BackgroundProperty, "PanelBrush");
        menu.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        menu.SetResourceReference(Control.BorderBrushProperty, "BorderBrush2");
        menu.BorderThickness = new Thickness(1);
        // An explicit template prevents WPF's built-in text-editor/system menu from leaking in.
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty, Templated("Background"));
        border.SetBinding(Border.BorderBrushProperty, Templated("BorderBrush"));
        border.SetBinding(Border.BorderThicknessProperty, Templated("BorderThickness"));
        border.SetBinding(Border.PaddingProperty, Templated("Padding"));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        var items = new FrameworkElementFactory(typeof(StackPanel));
        items.SetValue(Panel.IsItemsHostProperty, true);
        items.SetValue(KeyboardNavigation.DirectionalNavigationProperty, KeyboardNavigationMode.Cycle);
        border.AppendChild(items);
        menu.Template = new ControlTemplate(typeof(ContextMenu)) { VisualTree = border };
        return menu;
    }

    private static MenuItem CreateItem(string label, string gesture)
    {
        var item = new MenuItem
        {
            Header = label, InputGestureText = gesture, Padding = new Thickness(10, 7, 10, 7),
            Background = Brushes.Transparent, FontSize = 13, MinWidth = 174
        };
        item.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        var border = new FrameworkElementFactory(typeof(Border), "Card");
        border.SetBinding(Border.BackgroundProperty, Templated("Background"));
        border.SetBinding(Border.PaddingProperty, Templated("Padding"));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        var dock = new FrameworkElementFactory(typeof(DockPanel));
        var shortcut = new FrameworkElementFactory(typeof(TextBlock));
        shortcut.SetValue(DockPanel.DockProperty, Dock.Right);
        shortcut.SetValue(FrameworkElement.MarginProperty, new Thickness(22, 0, 0, 0));
        shortcut.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        shortcut.SetValue(TextBlock.FontSizeProperty, 11d);
        shortcut.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        shortcut.SetBinding(TextBlock.TextProperty, Templated("InputGestureText"));
        dock.AppendChild(shortcut);
        var caption = new FrameworkElementFactory(typeof(TextBlock));
        caption.SetBinding(TextBlock.TextProperty, Templated("Header"));
        caption.SetBinding(TextBlock.ForegroundProperty, Templated("Foreground"));
        caption.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        dock.AppendChild(caption);
        border.AppendChild(dock);
        var template = new ControlTemplate(typeof(MenuItem)) { VisualTree = border };
        var highlight = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
        highlight.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("GlowSoftBrush"), "Card"));
        template.Triggers.Add(highlight);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45, "Card"));
        template.Triggers.Add(disabled);
        item.Template = template;
        return item;
    }

    private static Binding Templated(string property) => new(property)
    {
        RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
    };
}
