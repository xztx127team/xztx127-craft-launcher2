using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using XCL2.App.Services;
using XCL2.App.Views;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var key in new[] { "PanelBrush", "TextPrimaryBrush", "TextSecondaryBrush", "BorderBrush2", "GlowSoftBrush", "SideBrush", "DividerBrush", "AccentBrush" })
            app.Resources[key] = Brushes.White;
        var viewer = new MarkdownViewer { Markdown = "Email body\nwith a real break\n\n```text\r\nfirst  \r\nsecond\n```", Width = 300 };
        var window = new Window { Content = viewer, Width = 340, Height = 300, ShowInTaskbar = false };
        try
        {
            window.Show();
            window.UpdateLayout();
            Assert(viewer.IsDocumentEnabled, "Embedded AI actions must remain enabled.");
            Assert(viewer.ContextMenu?.Template != null, "Body text needs an explicit custom menu.");
            Assert(AiTextSelectionMenu.OwnsTextInput(viewer, viewer), "Body text must own its menu.");
            var block = viewer.Document.Blocks.OfType<BlockUIContainer>().Single();
            var stack = (StackPanel)((Border)block.Child).Child;
            var header = (Grid)stack.Children[0];
            var button = header.Children.OfType<Button>().Single();
            var code = (TextBox)((Border)stack.Children[1]).Child;
            Assert(code.Text == "first  \r\nsecond\n", "Code must preserve source newline styles and trailing whitespace.");
            Assert(code.ContextMenu?.Template != null, "Code selection needs its own custom menu.");
            Assert(AiTextSelectionMenu.OwnsTextInput(code, code), "Code selection must target the code editor.");
            Assert(!AiTextSelectionMenu.OwnsTextInput(viewer, code), "The body must never consume code editor commands.");
            Assert(!AiTextSelectionMenu.OwnsTextInput(viewer, button), "Embedded copy button must not invoke text selection menu.");
            Assert(!AiTextSelectionMenu.OwnsTextInput(viewer, header), "Code header chrome must not invoke the body selection menu.");
            var link = new Hyperlink(new Run("Open"));
            ((Paragraph)viewer.Document.Blocks.FirstBlock).Inlines.Add(link);
            Assert(!AiTextSelectionMenu.OwnsTextInput(viewer, link), "AI links must not invoke text selection menu.");
            Assert(button.IsEnabled, "The copy button must be enabled.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(Clipboard.GetText() == code.Text, "Whole-code copy must copy original code, not rendered rows.");
            Assert(!viewer.ContextMenu!.IsOpen && !code.ContextMenu!.IsOpen, "Copy button must not open a selection menu.");
            code.Select(0, 9);
            ApplicationCommands.Copy.Execute(null, code);
            Assert(Clipboard.GetText() == "first  \r\n", "Selected-code copy must preserve real newlines.");
            code.Text = new string('a', 3000);
            code.TextWrapping = TextWrapping.Wrap;
            code.SelectAll();
            ApplicationCommands.Copy.Execute(null, code);
            Assert(Clipboard.GetText() == new string('a', 3000), "Visual wrapping must not insert clipboard newlines.");
            var menu = viewer.ContextMenu!;
            menu.IsOpen = true;
            window.UpdateLayout();
            app.Resources["PanelBrush"] = Brushes.Black;
            Assert(ReferenceEquals(menu.Background, Brushes.Black), "An open menu must follow theme resource changes.");
            menu.IsOpen = false;
            Console.WriteLine("PASS: AI body/code command ownership, real clipboard copies, theme updates and action isolation.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally { window.Close(); app.Shutdown(); }
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
