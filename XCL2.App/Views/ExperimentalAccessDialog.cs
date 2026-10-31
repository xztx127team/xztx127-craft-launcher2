using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

public sealed class ExperimentalAccessDialog : OverlayDialogControl
{
    private readonly string _featureId;
    private readonly bool _consume;
    private readonly StackPanel _body = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 8) };
    private ExperimentalHumanCheck? _check;
    private readonly TextBlock _heading = new() { FontSize = 18, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap };
    public string? RevealedPassword { get; private set; }

    public ExperimentalAccessDialog(string featureId, bool consume = true)
    {
        _featureId = featureId; _consume = consume;
        MinWidth = 380; MaxWidth = 580; MaxHeight = 650;
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(_heading); root.Children.Add(_status); root.Children.Add(_body);
        var cancel = Button("取消", () => CloseWith(false)); cancel.HorizontalAlignment = HorizontalAlignment.Right;
        root.Children.Add(cancel);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        OverlayClosed += (_, result) =>
        {
            _check?.Cancel();
            if (result != true) { TokenGateService.Revoke(_featureId); RevealedPassword = null; }
        };
        _heading.Text = "查看密码 · " + ExperimentalFeatureCatalog.All.Single(f => f.Id == _featureId).Title;
        // 先过人机验证，通过后才生成并显示密码授权。
        _status.Text = "查看密码前需要通过人机验证（共 4 关）。";
        _check = new ExperimentalHumanCheck(_body, _status, () => { _body.Children.Clear(); Reveal(); });
        _check.Start();
    }

    private static Button Button(string title, Action click)
    {
        var button = new Button { Content = title, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(4) };
        button.Click += (_, _) => click(); return button;
    }
    private void Reveal()
    {
        _status.Text = "本次启动密码为随机 10～25 位；每次启动或重启都会重新生成。查看后授权十分钟内有效，执行一次后需再次查看以授权。";
        RevealedPassword = TokenGateService.Issue(_featureId);
        var password = new TextBox { Text = RevealedPassword, IsReadOnly = true, FontSize = 20, Margin = new Thickness(0, 8, 0, 8) };
        var passwordRow = new DockPanel { LastChildFill = true };
        var copy = Button("复制", () =>
        {
            try { Clipboard.SetText(password.Text); _status.Text = "密码已复制。"; }
            catch { _status.Text = "剪贴板不可用，可选中文字手动复制。"; }
        });
        copy.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(copy, Dock.Right);
        passwordRow.Children.Add(copy); passwordRow.Children.Add(password);
        _body.Children.Add(passwordRow);
        if (!_consume) { _body.Children.Add(Button("返回填写密码", () => CloseWith(true))); return; }
        var input = new TextBox { Margin = new Thickness(0, 8, 0, 8) };
        _body.Children.Add(new TextBlock { Text = "输入本次口令以执行功能：" }); _body.Children.Add(input);
        _body.Children.Add(Button("验证并执行", () =>
        {
            if (TokenGateService.Consume(_featureId, input.Text)) CloseWith(true);
            else _status.Text = "口令不正确、已使用或已过期。关闭后重新查看密码可重新授权。";
        }));
    }
}
