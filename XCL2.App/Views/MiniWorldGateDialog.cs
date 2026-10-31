using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

internal sealed class MiniWorldGateDialog : OverlayDialogControl
{
    public MiniWorldGateDialog()
    {
        Width = 580; MaxHeight = 660;
        var panel = new StackPanel { Margin = new Thickness(24) };
        var title = new TextBlock { Text = "进入前，请阅读作者声明", FontSize = 21, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(title);
        var badge = new TextBlock { Text = MiniWorldCatalog.ExperimentalNotice, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 18) };
        badge.SetResourceReference(TextBlock.ForegroundProperty, "WarningTextBrush"); panel.Children.Add(badge);
        panel.Children.Add(new TextBlock { Text = MiniWorldCatalog.Disclosure, TextWrapping = TextWrapping.Wrap, FontSize = 15, LineHeight = 27 });
        var read = new CheckBox { Content = "我已阅读并理解上方声明", Margin = new Thickness(0, 20, 0, 16) };
        panel.Children.Add(read);
        var answers = new List<ComboBox>();
        foreach (var question in MiniWorldCatalog.ReadingQuestions)
        {
            panel.Children.Add(new TextBlock { Text = $"{answers.Count + 1}. {question.Prompt}", FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 6) });
            var answer = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = -1,
                Margin = new Thickness(0, 0, 0, 8), ItemsSource = question.Choices };
            answers.Add(answer); panel.Children.Add(answer);
        }
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 12) };
        error.SetResourceReference(TextBlock.ForegroundProperty, "WarningTextBrush"); panel.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "返回", Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 10, 0) };
        cancel.Click += (_, _) => CloseWith(false); actions.Children.Add(cancel);
        var enter = new Button { Content = "确认并进入", Padding = new Thickness(16, 8, 16, 8), IsEnabled = false };
        enter.SetResourceReference(Button.StyleProperty, "PrimaryButton");
        void Update() => enter.IsEnabled = read.IsChecked == true && answers.All(answer => answer.SelectedIndex >= 0);
        read.Checked += (_, _) => Update(); read.Unchecked += (_, _) => Update(); foreach (var answer in answers) answer.SelectionChanged += (_, _) => Update();
        enter.Click += (_, _) =>
        {
            if (read.IsChecked != true || !MiniWorldCatalog.AreReadingAnswersCorrect(answers.Select(answer => answer.SelectedIndex).ToArray()))
            {
                var wrong = answers.Select((answer, i) => (answer, i))
                    .Where(item => item.answer.SelectedIndex != MiniWorldCatalog.ReadingQuestions[item.i].CorrectIndex).ToArray();
                error.Text = "请重新阅读并检查第 " + string.Join("、", wrong.Select(item => item.i + 1)) + " 题。";
                if (wrong.Length > 0) { wrong[0].answer.BringIntoView(); wrong[0].answer.Focus(); }
                return;
            }
            CloseWith(true);
        };
        actions.Children.Add(enter); panel.Children.Add(actions);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, PanningMode = PanningMode.VerticalOnly };
    }
}
