using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

/// <summary>Both temporary dismissal and opt-out are explicit; only a successful opt-out write suppresses future starts.</summary>
public sealed class StartupDownloadNoticeDialog : OverlayDialogControl
{
    private readonly ConfigService _configService;
    private readonly Button _thisStartup;
    private readonly Button _suppress;
    private readonly TextBlock _error = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 12, 0, 0),
        Visibility = Visibility.Collapsed,
    };

    public StartupDownloadNoticeDialog(ConfigService configService)
    {
        _configService = configService;
        var owner = Application.Current?.MainWindow;
        Width = 560;
        MaxWidth = Math.Max(240, (owner?.ActualWidth > 0 ? owner.ActualWidth : 800) - 64);
        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(new TextBlock { Text = "公告", FontSize = 21, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock
        {
            Text = StartupDownloadNoticePolicy.Message,
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 16, 0, 22),
        });

        _thisStartup = MakeButton(StartupDownloadNoticePolicy.ThisStartupLabel, primary: true);
        _thisStartup.IsDefault = true;
        _thisStartup.Click += (_, _) => Choose(StartupDownloadNoticeChoice.ThisStartup);
        _suppress = MakeButton(StartupDownloadNoticePolicy.SuppressLabel, primary: false);
        _suppress.Margin = new Thickness(0, 10, 0, 0);
        _suppress.Click += (_, _) => Choose(StartupDownloadNoticeChoice.Suppress);
        body.Children.Add(_thisStartup);
        body.Children.Add(_suppress);
        _error.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        body.Children.Add(_error);
        Content = new ScrollViewer
        {
            Content = body,
            MaxHeight = Math.Max(180, (owner?.ActualHeight > 0 ? owner.ActualHeight : 600) - 96),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    private static Button MakeButton(string label, bool primary)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 9, 12, 9),
            MinHeight = 38,
        };
        button.SetResourceReference(StyleProperty, primary ? "PrimaryButton" : "SecondaryButton");
        return button;
    }

    private void Choose(StartupDownloadNoticeChoice choice)
    {
        _thisStartup.IsEnabled = _suppress.IsEnabled = false;
        try
        {
            StartupDownloadNoticePolicy.ApplyChoice(_configService.Config, choice, _configService.Save);
            CloseWith(choice == StartupDownloadNoticeChoice.Suppress);
        }
        catch (Exception ex)
        {
            _error.Text = "未能保存“不再提示”的选择，请重试；也可以选择本次关闭。\n" + ex.Message;
            _error.Visibility = Visibility.Visible;
            _thisStartup.IsEnabled = _suppress.IsEnabled = true;
            ErrorPresenter.LogFallback("保存启动公告提醒偏好失败", ex);
        }
    }
}
