using System;
using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;

namespace XCL2.App.Views;

public partial class AiFloatingButton : UserControl
{
    public event EventHandler? Clicked;
    internal Button BallButton => MainButton;

    public AiFloatingButton()
    {
        InitializeComponent();
    }

    private void MainButton_Click(object sender, RoutedEventArgs e) => Clicked?.Invoke(this, EventArgs.Empty);

    public void ShowBadge(bool show) => Badge.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    internal void ApplySettings(AiFloatingBetaSettings settings, double size)
    {
        Width = Height = size;
        AiFloatingBallAppearance.Apply(MainButton, settings, size);
    }
}
