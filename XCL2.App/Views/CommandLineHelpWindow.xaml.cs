using System.Windows;

namespace XCL2.App.Views;

public partial class CommandLineHelpWindow : Window
{
    public CommandLineHelpWindow(string helpText)
    {
        InitializeComponent();
        HelpTextBox.Text = helpText;
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(HelpTextBox.Text);
            CopyAllBtn.Content = "已复制 ✓";
        }
        catch
        {
            // 极少数情况下剪贴板被其它进程占用会抛异常，这里不是关键功能，静默忽略即可，
            // 用户仍然可以自己在 TextBox 里手动选中复制。
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
