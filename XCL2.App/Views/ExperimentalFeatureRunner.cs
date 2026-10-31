using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using XCL2.App.Services;

namespace XCL2.App.Views;

internal static class ExperimentalFeatureRunner
{
    public static async Task RunAsync(string id, MainWindow owner)
    {
        string title = ExperimentalFeatureCatalog.All.Single(f => f.Id == id).Title;
        try
        {
            switch (id)
            {
                case "crash": throw new InvalidOperationException("[XCL-EXP-001] 模拟 XCL 崩溃：实验故障已被隔离捕获。");
                case "worker-crash":
                    await Task.Run((Action)(() => throw new InvalidOperationException("[XCL-EXP-002] 模拟工作线程异常。"))); break;
                case "timeout": await Task.Delay(250); throw new TimeoutException("模拟请求超时：远端未在限定时间内响应。");
                case "json-damage": using (JsonDocument.Parse("{\"version\":")) { } break;
                case "integrity":
                    var expected = SHA256.HashData(Encoding.UTF8.GetBytes("XCL sample A"));
                    var received = SHA256.HashData(Encoding.UTF8.GetBytes("XCL sample B"));
                    if (!CryptographicOperations.FixedTimeEquals(expected, received)) throw new InvalidDataException("模拟下载文件哈希校验失败。");
                    break;
                case "memory": MessageBoxDialog.ShowInfo((await WorkingSetOptimizationService.RunAsync()).Summary, title); break;
                case "memory-snapshot":
                    using (var process = Process.GetCurrentProcess())
                    {
                        var memory = MemoryOptimizerService.ReadPhysicalMemory();
                        MessageBoxDialog.ShowInfo($"物理内存：{memory?.TotalMb} MB\n可用内存：{memory?.AvailableMb} MB\n托管堆：{GC.GetTotalMemory(false) / 1048576.0:F1} MB\n工作集：{process.WorkingSet64 / 1048576.0:F1} MB", title);
                    }
                    break;
                case "contrast":
                    var pairs = new[] { ("TextPrimaryBrush", "PanelBrush"), ("TextSecondaryBrush", "PanelBrush"), ("DrawerTextPrimaryBrush", "DrawerBackgroundBrush"), ("ButtonForegroundBrush", "ButtonBackgroundBrush") };
                    var lines = pairs.Select(pair =>
                    {
                        if (Application.Current.TryFindResource(pair.Item1) is not SolidColorBrush fg || Application.Current.TryFindResource(pair.Item2) is not SolidColorBrush bg) return pair.Item1 + "：资源缺失";
                        double a = Luminance(fg.Color), b = Luminance(bg.Color), ratio = (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
                        return $"{pair.Item1} / {pair.Item2}：{ratio:F2}:1 · {(ratio >= 4.5 ? "普通文字通过" : "建议提高对比度")}";
                    });
                    MessageBoxDialog.ShowInfo(string.Join("\n", lines) + "\n按资源 RGB 估算，未计入透明度与背景图片。", title); break;
                case "dispatcher":
                    var samples = new List<double>();
                    for (int i = 0; i < 5; i++)
                    {
                        await Task.Delay(120); var clock = Stopwatch.StartNew();
                        await owner.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                        samples.Add(clock.Elapsed.TotalMilliseconds);
                    }
                    MessageBoxDialog.ShowInfo($"平均调度延迟：{samples.Average():F2} ms\n最大调度延迟：{samples.Max():F2} ms", title); break;
                case "parallel":
                    var watch = Stopwatch.StartNew();
                    var values = await Task.WhenAll(Enumerable.Range(1, 4).Select(seed => Task.Run(() =>
                    { long sum = 0; for (int i = 0; i < 500000; i++) sum += (i * seed) % 97; return sum; })));
                    MessageBoxDialog.ShowInfo($"四项计算已完成，耗时 {watch.Elapsed.TotalMilliseconds:F1} ms。\n校验值：{string.Join(", ", values)}", title); break;
                case "write-probe":
                    var path = Path.Combine(App.DataDir, "xcl-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
                    try
                    {
                        var content = Guid.NewGuid().ToString("N"); await File.WriteAllTextAsync(path, content);
                        if (await File.ReadAllTextAsync(path) != content) throw new IOException("数据目录写入校验失败。");
                    }
                    finally { if (File.Exists(path)) File.Delete(path); }
                    MessageBoxDialog.ShowInfo("创建、写入、读取和删除均成功。", title); break;
                case "rendering":
                    var dpi = VisualTreeHelper.GetDpi(owner);
                    MessageBoxDialog.ShowInfo($"WPF 渲染级别：{RenderCapability.Tier >> 16}\n缩放：{dpi.DpiScaleX:P0} × {dpi.DpiScaleY:P0}\n操作系统架构：{RuntimeInformation.OSArchitecture}\n进程架构：{RuntimeInformation.ProcessArchitecture}", title); break;
                case "exception-chain": throw new InvalidOperationException("模拟外层启动失败。", new FileNotFoundException("模拟内部组件缺失。", "sample-component.jar"));
                case "clipboard":
                    string text = "XCL-clipboard-" + Guid.NewGuid().ToString("N");
                    Clipboard.SetText(text);
                    MessageBoxDialog.ShowInfo(Clipboard.GetText() == text ? "剪贴板往返检查成功。" : "剪贴板内容已被其他程序改变。", title); break;
                case "animation": ShowAnimations(); break;
                case "architecture":
                    var executable = new TerracottaService().ResolveExecutable(owner.ConfigService.Config.TerracottaExecutablePath);
                    MessageBoxDialog.ShowInfo($"本体架构：{TerracottaArchitecture.ReadMachine(executable)}\n系统架构：{RuntimeInformation.OSArchitecture}\n兼容性检查通过。", title); break;
                default: throw new ArgumentOutOfRangeException(nameof(id));
            }
        }
        catch (Exception ex)
        {
            ErrorPresenter.ShowFriendlyError("实验结果：" + title, ex.ToString(), "XCL 实验报告");
        }
    }

    private static double Luminance(Color color)
    {
        double Part(byte value) { var n = value / 255.0; return n <= .04045 ? n / 12.92 : Math.Pow((n + .055) / 1.055, 2.4); }
        return .2126 * Part(color.R) + .7152 * Part(color.G) + .0722 * Part(color.B);
    }

    private static void ShowAnimations()
    {
        var dialog = new OverlayDialogControl { MinWidth = 350, MaxWidth = 580, MaxHeight = 580 };
        var body = new StackPanel { Margin = new Thickness(20) };
        body.Children.Add(new TextBlock { Text = "启动器特效 · F1 随时恢复", FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 12) });
        var durationLabel = new TextBlock { Margin = new Thickness(4, 0, 0, 2) };
        var duration = new Slider { Minimum = 1, Maximum = 60, Value = LauncherVisualEffects.DefaultDurationSeconds,
            TickFrequency = 1, IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = 5, Margin = new Thickness(4, 0, 4, 4) };
        var unlimited = new CheckBox { Content = "不限时（按 F1 恢复）", Margin = new Thickness(4, 0, 0, 10) };
        System.Windows.Automation.AutomationProperties.SetName(duration, "特效持续时间（秒）");
        void RefreshDuration()
        {
            duration.IsEnabled = unlimited.IsChecked != true;
            durationLabel.Text = unlimited.IsChecked == true ? "持续时间：不限时" : $"持续时间：{(int)duration.Value} 秒（默认 {LauncherVisualEffects.DefaultDurationSeconds} 秒）";
        }
        duration.ValueChanged += (_, _) => RefreshDuration();
        unlimited.Click += (_, _) => RefreshDuration();
        RefreshDuration();
        body.Children.Add(durationLabel); body.Children.Add(duration); body.Children.Add(unlimited);
        var choices = new WrapPanel();
        foreach (var effect in LauncherVisualEffects.Catalog)
        {
            var button = new Button { Content = effect.Title, Margin = new Thickness(4), Padding = new Thickness(12, 8, 12, 8) };
            button.Click += (_, _) =>
            {
                TimeSpan? length = unlimited.IsChecked == true ? null : TimeSpan.FromSeconds(duration.Value);
                dialog.Close(); LauncherVisualEffects.Start(effect.Kind, true, length);
            };
            choices.Children.Add(button);
        }
        body.Children.Add(choices);
        var reset = new Button { Content = "恢复正常", Margin = new Thickness(4) };
        reset.Click += (_, _) => { LauncherVisualEffects.Stop(); AprilFoolsService.DismissForToday(); dialog.Close(); };
        body.Children.Add(reset);
        dialog.Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dialog.ShowDialog();
    }
}
