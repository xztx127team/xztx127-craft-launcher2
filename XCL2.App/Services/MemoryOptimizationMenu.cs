using System.Windows.Controls;
using XCL2.App.Models;

namespace XCL2.App.Services;

public static class MemoryOptimizationMenu
{
    public static void AddMenu(ItemsControl menu, AppConfig config, Action<Action<AppConfig>> save)
    {
        var run = new MenuItem { Header = "内存优化", ToolTip = "整理当前桌面会话的进程工作集；不会结束进程。再次访问内存时可能出现短暂延迟。" };
        run.Click += async (_, _) =>
        {
            run.IsEnabled = false;
            try { ToastService.ShowSuccess((await WorkingSetOptimizationService.RunAsync()).Summary); }
            catch (Exception ex) { ErrorPresenter.ShowFriendlyError("内存优化未完成", ex.ToString(), "XCL 内存优化错误"); }
            finally { run.IsEnabled = true; }
        };
        menu.Items.Add(run);
        var schedule = new MenuItem { Header = "定时执行内存优化" };
        foreach (var minutes in new[] { 0, 5, 10, 15, 30, 60, 120 })
        {
            var option = new MenuItem { Header = minutes == 0 ? "关闭" : $"每 {minutes} 分钟", IsCheckable = true,
                IsChecked = config.MemoryOptimizationIntervalMinutes == minutes };
            option.Click += (_, _) => save(c => c.MemoryOptimizationIntervalMinutes = minutes);
            schedule.Items.Add(option);
        }
        menu.Items.Add(schedule);
    }

}
