using System;
using System.Windows;

namespace XCL2.App.Views;

public partial class MainWindow
{
    private void ApplyCommandLineWindowSize()
    {
        var args = App.StartupArgs;
        if (!args.WindowWidth.HasValue && !args.WindowHeight.HasValue) return;
        // WPF 的窗口大小使用逻辑像素；保留界面本身的最小尺寸并适配可用桌面。
        var workArea = SystemParameters.WorkArea;
        if (args.WindowWidth.HasValue)
            Width = Math.Clamp(args.WindowWidth.Value, MinWidth, Math.Max(MinWidth, workArea.Width));
        if (args.WindowHeight.HasValue)
            Height = Math.Clamp(args.WindowHeight.Value, MinHeight, Math.Max(MinHeight, workArea.Height));
    }
}
