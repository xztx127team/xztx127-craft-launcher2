using System.Windows;
using System.Windows.Controls;
using XCL2.App.Models;

namespace XCL2.App.Views;

/// <summary>Only virtual controls consume right-click; the game view keeps its own mouse input.</summary>
internal static class TouchOverlaySettingsShortcut
{
    internal static void Attach(FrameworkElement target, Action openSettings, string hint)
    {
        target.ToolTip = hint + "\n右键打开触屏按键与布局设置，可修改绑定、位置、大小和显示范围。";
        ToolTipService.SetShowDuration(target, 12000);
        target.PreviewMouseRightButtonUp += (_, e) =>
        {
            if (e.StylusDevice != null) return;
            e.Handled = true;
            // Leave the native mouse callback before activating a settings window.
            target.Dispatcher.BeginInvoke(openSettings);
        };
    }

    internal static string Describe(TouchControlDefinition control)
    {
        if (control.Mode == TouchControlMode.Command)
            return control.Binding.Trim().ToUpperInvariant() switch
            {
                "SHOW" => "点击重新显示触屏按键。",
                "HIDE" => "点击收起触屏按键；通过恢复按钮可重新显示。",
                "SETTINGS" => "点击打开触屏按键与布局设置。",
                "MODE" => "点击在自动、手动视角和手动菜单模式之间切换。",
                "MENU" => "点击切换菜单操作与游戏视角模式。",
                "KEYBOARD" => "点击打开屏幕键盘以输入文字。",
                "SENS+" => "点击提高本次触屏控制的视角灵敏度。",
                "SENS-" => "点击降低本次触屏控制的视角灵敏度。",
                "GRAB" => "点击将操作焦点返回游戏。",
                _ => $"点击执行“{control.Label}”。"
            };
        return control.Mode switch
        {
            TouchControlMode.Hold => $"按住发送 {control.Binding}，松开即释放。",
            TouchControlMode.Toggle => $"点击保持 {control.Binding}，再次点击释放。",
            _ => $"点击发送一次 {control.Binding}。"
        };
    }
}
