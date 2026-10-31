using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using XCL2.App.Services;

namespace XCL2.App;

public partial class App : Application
{
    /// <summary>
    /// XCL2 的便携数据目录：启动器运行目录下的 "xcl2" 文件夹。
    /// 主要存放日志、下载的 Java、缓存和便携资源；2.2.9 起全局 config/账户主副本改存 AppData，
    /// 启动器目录下 json/config.json 只保留可人工恢复的配置镜像。
    /// </summary>
    public static string DataDir { get; } = Path.Combine(AppContext.BaseDirectory, "xcl2");

    private const string WriteXorExecuteEnvVar = "DOTNET_EnableWriteXorExecute";
    private const string Win7RelaunchMarkerEnvVar = "XCL2_WIN7_WXORX_RELAUNCHED";

    /// <summary>
    /// 本次启动解析出来的命令行参数（-r / -gui / --d 等），供 MainWindow 首帧渲染完成后消费。
    /// -help 分支不会走到这里——命中 -help 直接在 OnStartup 里打印帮助并退出，不创建主窗口。
    /// </summary>
    public static CommandLineService.ParsedArgs StartupArgs { get; private set; } = new();

    static App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            WriteEarliestCrashLog(ex, isTerminating: args.IsTerminating);
            ShowEarliestCrashMessageBox(ex);
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteEarliestCrashLog(args.Exception, isTerminating: false, contextTag: "未观察的后台 Task 异常");
            args.SetObserved();
        };
    }

    /// <summary>
    /// 落盘尽量不依赖任何还没初始化好的状态：DataDir 只是一个基于 AppContext.BaseDirectory
    /// 的字符串拼接，本身不会抛异常，但下面创建目录、写文件这些 IO 操作可能因为各种环境问题
    /// 失败，所以整个方法体自己也要能"打不着一枪就换下一枪"，不能因为落盘失败又抛出新异常，
    /// 那样会在异常处理器里再触发一次异常，导致进程更早、更诡异地终止。
    /// </summary>
    private static void WriteEarliestCrashLog(Exception? ex, bool isTerminating, string? contextTag = null)
    {
        var tag = contextTag ?? $"AppDomain 未处理异常，IsTerminating={isTerminating}";
        var text = $"[{DateTime.Now}] [{tag}] {ex?.ToString() ?? "(无异常对象，可能是非 CLR 异常/原生崩溃)"}\n\n";

        // 主日志：跟 RunStartupSequence 里后续正常运行阶段用的是同一个文件，方便统一排查。
        try
        {
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(Path.Combine(DataDir, "logs"));
            File.AppendAllText(Path.Combine(DataDir, "logs", "crash.log"), text);
        }
        catch { /* 落盘失败也不能再抛异常，继续尝试下面的兜底路径 */ }

        // 兜底路径：万一连 xcl2 目录都创建不了（比如安装目录只读/权限问题），
        // 直接写在 exe 同级目录下，不需要额外创建任何子目录，成功率更高。
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "xcl2_crash.log"), text);
        }
        catch { /* 两份都失败的话确实没辙了，至少上面的 MessageBox 还能让用户看到内容 */ }
    }

    /// <summary>
    /// 显式弹出原生 MessageBox 而不是依赖任何内嵌 Overlay：该阶段可能连 MainWindow 都
    /// 还不存在，也可能是在非 UI 线程上触发的，只有系统原生 MessageBox 能保证在这些情况下
    /// 都还能弹出来。MessageBox.Show 本身理论上也可能因为极端情况（比如没有桌面/无人值守
    /// 服务器环境）失败，所以同样包一层 try/catch，绝不能让"兜底逻辑自己出错"盖过真正的
    /// 原始异常信息（原始信息已经在上面 WriteEarliestCrashLog 里落盘了）。
    /// </summary>
    private static void ShowEarliestCrashMessageBox(Exception? ex)
    {
        try
        {
            var detail = ex == null ? "（未捕获到具体异常对象）" : GetFullExceptionMessage(ex);
            MessageBox.Show(
                "XCL2 在启动过程中发生了未处理的异常，程序即将退出：\n\n" + detail +
                "\n\n详细堆栈已尝试写入 xcl2\\logs\\crash.log（或程序所在目录下的 xcl2_crash.log），" +
                "可以把该文件发给开发者排查。",
                "XCL2 启动崩溃", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch {  }
    }

    private static bool TryRelaunchForWin7WriteXorExecuteFix()
    {
        // 只处理 Win7（内核版本 6.1）。Win8/8.1 是 6.2/6.3，官方支持更完整，且基岩版这类
        // 功能本来就已经按 Win10+ 判定，不需要跟着一起处理；Win10/11 内核版本号都是 10.x，
        // 直接跳过，不受影响。
        var ver = Environment.OSVersion.Version;
        var isWin7 = ver.Major == 6 && ver.Minor == 1;
        if (!isWin7) return false;

        // 已经是重新拉起之后的那个新进程了（标记环境变量已经带上了），不要再拉一次，
        // 避免自己无限重启自己。
        if (Environment.GetEnvironmentVariable(Win7RelaunchMarkerEnvVar) == "1") return false;

        // 用户/运维已经在系统层面手动设置过该环境变量（比如自己按教程配置好了），
        // 就不要越俎代庖再包一层子进程，尊重现有设置，直接用当前进程即可。
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(WriteXorExecuteEnvVar))) return false;

        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return false;

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
                psi.ArgumentList.Add(arg);

            psi.EnvironmentVariables[WriteXorExecuteEnvVar] = "0";
            psi.EnvironmentVariables[Win7RelaunchMarkerEnvVar] = "1";

            Process.Start(psi);
            return true;
        }
        catch
        {
            // 重新拉起失败（比如权限问题）：退回原来的行为，正常在当前进程里启动，
            // 只是 Win7 上会比较慢——总比直接打不开、或者代码抛异常崩溃强。
            return false;
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 基岩版 MSIXVC 解压隔离子进程模式：必须放在整个 OnStartup 最开头、比 Win7 修复
        // 检测还要早——该分支根本不是要启动 UI，是自身 exe 被
        // BedrockDecode.BedrockExtractWorkerProcess.RunAsync 当作命令行工具重新拉起的
        // 一次性子进程，只需要跑完解压逻辑、把结果打到 stdout、然后整个进程退出。
        // 绝对不能往下走到 base.OnStartup/创建任何窗口——那样会平白多出一个看不见的
        // WPF 应用实例、White 掉子进程本该有的"跑完就退出"语义。
        // 用 Environment.Exit 而不是 Shutdown()：该阶段 WPF 消息循环还没跑起来，
        // Shutdown() 依赖的机制此时不生效。
        if (e.Args.Length > 0 && e.Args[0] == BedrockDecode.BedrockExtractWorkerProcess.WorkerArgMarker)
        {
            int exitCode;
            try
            {
                exitCode = BedrockDecode.BedrockExtractWorkerProcess.RunAsWorkerAsync(e.Args).GetAwaiter().GetResult();
            }
            catch
            {
                // 兜底：worker 逻辑内部已经把能捕获的异常都转成 ERR 行处理了，这里再包一层
                // 纯粹是防止极端情况下 GetAwaiter().GetResult() 本身抛出未预期异常时，
                // 子进程还能带着非 0 退出码正常结束，而不是变成另一种诡异的卡死状态。
                exitCode = 1;
            }
            Environment.Exit(exitCode);
            return;
        }

        // 命令行参数：-help 需要在整个启动流程最前面处理——用户是想快速看一眼用法就退出，
        // 不需要（也不应该）先跑一遍 Win7 修复检测、创建启动提示窗、加载主界面这些跟
        // "查看帮助"完全无关的重活。命中 -help 直接用 MessageBox 展示帮助文本（WPF 应用
        // 默认没有附加控制台，写 Console.WriteLine 在双击运行时用户根本看不到；如果是从
        // 命令行/脚本调用，MessageBox 同样能保证用户一定能看到内容），然后立即退出，
        // 不再往下创建任何窗口。
        //
        // 其余参数（-r/-gui/--d）只是解析出来存到 StartupArgs，真正的动作（选账户、切实例、
        // 跳页面、开下载中心）留到 MainWindow 首帧渲染完成之后再执行——命令行启动这一刻，
        // 账户列表/实例列表/ConfigService 都还没加载好，此时就去匹配账户名/实例名没有意义。
        var cliArgs = CommandLineService.Parse(e.Args);

        // 帮助和拼写/数值错误必须先于等待旧进程、访客重启和任何启动动作。
        // 查看 --debug --help 只显示快捷键，不创建主窗口或启动游戏。
        if (cliArgs.ShowHelp || cliArgs.HasErrors)
        {
            var text = CommandLineService.HelpFor(cliArgs);
            if (cliArgs.HasErrors)
                text = "启动参数有误，本次没有执行启动动作：\n\n" +
                    string.Join("\n", cliArgs.Diagnostics.Select(message => "• " + message)) +
                    "\n\n" + text;
            new Views.CommandLineHelpWindow(text).ShowDialog();
            Shutdown(cliArgs.HasErrors ? 2 : 0);
            return;
        }

        // 访客模式必须经过一次真正的进程重启：-l 只是“请求进入访客”，当前进程不直接
        // 切模式，而是拉起带隐藏 --guest-session 标记的新进程。--wait-pid 让接力进程先等
        // 旧进程退出，避免旧实例的单实例管道还没释放就误弹“已经运行”。
        if (cliArgs.WaitForPid > 0 && cliArgs.WaitForPid != Environment.ProcessId)
        {
            try
            {
                using var oldProcess = Process.GetProcessById(cliArgs.WaitForPid);
                oldProcess.WaitForExit(15000);
            }
            catch { /* 父进程已经退出/不存在，直接继续 */ }
        }

        if (cliArgs.GuestModeRelaunchRequested && !cliArgs.EnterGuestMode)
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    WorkingDirectory = AppContext.BaseDirectory
                };
                // 原样传递本次会话参数（包含带空格的值），使 -l --debug 等组合不丢失。
                // 接力标记放在最后；接力进程不会再次执行 -l 重启。
                foreach (var argument in e.Args) psi.ArgumentList.Add(argument);
                psi.ArgumentList.Add("--guest-session");
                psi.ArgumentList.Add("--wait-pid");
                psi.ArgumentList.Add(Environment.ProcessId.ToString());
                Process.Start(psi);
            }
            Shutdown();
            return;
        }

        StartupArgs = cliArgs;

        if (TryRelaunchForWin7WriteXorExecuteFix())
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        TokenGateService.InitializeSession();

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (!SingleInstanceService.HandleStartup(() =>
        {
            var dialog = new Views.InstanceConflictDialog();
            dialog.ShowDialog();
            return dialog.Result;
        }))
        {
            Shutdown();
            return;
        }

        if (!IsRunningOnNet8Desktop())
        {
            // 这里**必须**用系统原生 MessageBox，不能换成内嵌的 MessageBoxDialog：
            // 内嵌弹窗要挂在 MainWindow 的 OverlayContentHost 上（见 OverlayDialogService.Register），
            // 而这段代码跑在 OnStartup 里、MainWindow 还没被创建，没有宿主可挂。
            // 这是全项目仅剩的两处原生 MessageBox 之一，另一处是下面的全局未处理异常兜底，
            // 同理：异常可能发生在主窗口已经崩掉/还没建好的时刻，不能依赖它。
            MessageBox.Show(
                "你需要安装 .NET8 运行时才可以继续使用本程序。\n\n" +
                "检测到当前机器上的 .NET 运行时版本不是 8.x（或者装的是不含桌面支持的精简版），" +
                "无法正常启动 XCL2。\n\n" +
                "请前往微软官方页面下载安装「.NET Desktop Runtime 8.0」（Windows x64，" +
                "选择 \".NET Desktop Runtime\" 而不是 \"ASP.NET Core Runtime\"）后重新打开本程序：\n" +
                "https://dotnet.microsoft.com/download/dotnet/8.0",
                "缺少 .NET8 运行时", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }

        Views.StartupSplashWindow? splash = null;
        try
        {

            splash = new Views.StartupSplashWindow();
            splash.Show();
            // 跟下面 mainWindow.UpdateLayout() 同样的道理：Show() 不保证立刻真正画到屏幕上，
            // 这里强制走一遍首帧，确保用户能第一时间看到该提示窗，而不是被接下来的
            // ConfigService.Load()/ThemeService 初始化这些同步工作抢占，导致提示窗跟主窗口
            // 一起延后才出现，失去了"提前反馈"的意义。
            splash.UpdateLayout();
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);

            RunStartupSequence(splash);
        }
        catch (Exception ex)
        {
            WriteEarliestCrashLog(ex, isTerminating: false, contextTag: "启动阶段异常，发生在主窗口创建/显示完成之前");

            // 启动失败也要把提示窗关掉，不然用户会看到一个卡死的"正在启动…"窗口
            // 停留在桌面上，跟后面弹出的错误提示框叠在一起，体验很奇怪。
            try { splash?.Close(); } catch { /* 已经关闭或窗口本身出问题时忽略 */ }

            ShowEarliestCrashMessageBox(ex);
            Shutdown(1);
        }
    }

    /// <summary>
    /// OnStartup 的主体逻辑抽成独立方法，纯粹是为了让上面那层 try/catch 能完整覆盖
    /// "从读配置到窗口创建完成"的全过程，而不需要把 try/catch 的缩进套进整个方法体里
    /// 让本来就很长的 OnStartup 变得更难读。行为跟原来完全一致，只是外层多包了一层。
    /// </summary>
    /// <param name="splash">OnStartup 一开始弹出的轻量提示窗，MainWindow 首帧渲染完成后
    /// 由本方法负责关闭并让位。</param>
    private void RunStartupSequence(Views.StartupSplashWindow splash)
    {
        // 修复"切换页面后才会变黑/侧边栏和底部账户区一直是浅色"：必须在 MainWindow 构造
        // 之前完成"读配置 + 应用主题/语言"，让 MainWindow.xaml 第一次 InitializeComponent()
        // 时资源字典里就已经是正确的皮肤颜色和语言，不需要任何事后刷新。这里单独 new 一个
        // ConfigService 只是为了在窗口存在之前读一次持久化配置，跟 MainWindow 自己持有的
        // ConfigService 实例互不冲突。
        splash.SetStatus("正在读取启动器配置…");
        var earlyConfig = new ConfigService();
        earlyConfig.Load();
        WindowsCompatibilityService.Configure(earlyConfig.Config.WindowsCompatibilityMode);
        var startupCompatibility = WindowsCompatibilityService.Current;
        if (startupCompatibility.IsAutomatic && startupCompatibility.IsEnabled)
            splash.SetStatus("正在打开兼容模式…");
        splash.SetStatus("正在应用主题与语言…");
        ThemeService.ApplyForCurrentState(earlyConfig.Config.GuestModeEnabled, earlyConfig.Config.UiSkin, earlyConfig.Config.IsDarkMode, earlyConfig.Config.CustomAccentColor);
        LocalizationService.ApplyForCurrentState(earlyConfig.Config.LauncherLanguage);

        ThemeService.ApplyWindowTransparency(earlyConfig.Config.EnableWindowTransparency, earlyConfig.Config.WindowOpacityPercent);
        ThemeService.ApplyGlobalWindowTransparency(earlyConfig.Config.EnableGlobalWindowTransparency, earlyConfig.Config.GlobalWindowOpacityPercent);
        ThemeService.ApplyTextOpacity(earlyConfig.Config.TextOpacityPercent);
        ThemeService.SetPopupAppearanceConfig(earlyConfig.Config.PopupUseCustomAppearance, earlyConfig.Config.PopupOpacityPercent, earlyConfig.Config.PopupFrostPercent, earlyConfig.Config.PopupTextOpacityPercent);
        ThemeService.SetDrawerAppearanceConfig(earlyConfig.Config.DrawerUseCustomAppearance, earlyConfig.Config.DrawerOpacityPercent, earlyConfig.Config.DrawerFrostPercent, earlyConfig.Config.DrawerTextOpacityPercent);
        ScrollWheelBehavior.SetSensitivityPercent(earlyConfig.Config.MouseWheelSensitivityPercent);
        var earlyMaterial = Enum.TryParse<Win11EffectsService.BackdropMaterial>(earlyConfig.Config.Win11BackdropMaterial, out var earlyM)
            ? earlyM : Win11EffectsService.BackdropMaterial.Mica;
        Win11EffectsService.SetEnabled(earlyConfig.Config.EnableWin11VisualEffects, earlyMaterial);
        

        Win11EffectsService.SetWinUi3Enabled(earlyConfig.Config.EnableWinUi3Design);
        ThemeService.ApplyFontFamily(earlyConfig.Config.AppFontFamily, earlyConfig.Config.EnableWinUi3Design);

        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window w) AppIconService.ApplyTo(w);
            }));

        splash.SetStatus("正在初始化数据目录与日志…");
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(Path.Combine(DataDir, "logs"));
        Directory.CreateDirectory(Path.Combine(DataDir, "runtime")); // java
        Directory.CreateDirectory(Path.Combine(DataDir, "scripts")); 

        LauncherLogService.BeginSession(earlyConfig);
        RegisterUiInteractionLogging();

        // 轻微背景反馈不缩放文字，也不改变文字透明度；统一尊重动画设置。
        ControlFeedbackBehavior.Register();

        // 兜底：正常情况下 MainWindow.Closed 会调用一次 EndSessionAndFlush（见 MainWindow 构造函数），
        // 但如果窗口没能正常触发 Closed 就整个应用退出了（比如上面 Shutdown() 分支、或者其它
        // 异常路径导致的提前退出），这里的 Application.Exit 兜底确保日志文件仍然会被写出去。
        // EndSessionAndFlush 内部做了幂等处理，不会因为被调用两次而出问题。
        Exit += (_, _) => LauncherLogService.EndSessionAndFlush();

        Exit += (_, _) => { TouchMousePromotionFilter.Disable(); OriginalTouchMousePromotionFilter.Disable(); };

        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new System.Windows.RoutedEventHandler((sender, _) =>
            {
                if (sender is Window w && !WindowTreatmentPolicy.IsExempt(w))
                    WindowChromeService.ApplyTitleBarTheme(w, ThemeService.CurrentIsDarkMode);
            }));

        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new System.Windows.RoutedEventHandler((sender, _) =>
            {
                if (sender is Window w && !WindowTreatmentPolicy.IsExempt(w))
                    Win11EffectsService.Apply(w, Win11EffectsService.CurrentEnabled);
            }));

        // 全局窗口透明（整窗 Window.Opacity）：跟上面两个类处理器同一套思路，让"当前已存在的
        // + 以后新增的"所有 Window 子类在 Loaded 时都自动套用最新的全局透明度状态。
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new System.Windows.RoutedEventHandler((sender, _) =>
            {
                if (sender is Window w && !WindowTreatmentPolicy.IsExempt(w))
                    ThemeService.ApplyGlobalOpacityToWindow(w);
            }));

        DispatcherUnhandledException += (s, args) =>
        {
            try
            {
                File.AppendAllText(Path.Combine(DataDir, "logs", "crash.log"),
                    $"[{DateTime.Now}] {args.Exception}\n\n");
            }
            catch { /* ignore */ }
            LauncherLogService.AppendLine($"[未处理异常] {args.Exception.GetType().Name}: {args.Exception.Message}");
            // 同上：全局未处理异常的最后兜底，此时主窗口可能已经不可用，
            // 只能用系统原生 MessageBox，不能走内嵌 Overlay。
            MessageBox.Show("发生未处理的异常：\n" + GetFullExceptionMessage(args.Exception), "XCL2 错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // 等价于原来 Program.cs 里的 app.Run(new Views.MainWindow())：显式创建并显示
        // 主窗口。App.xaml 没有设置 StartupUri，所以 WPF 不会自动创建任何窗口，这一步
        // 是必须的，否则应用会启动后立刻因为"没有任何窗口、也没有设置
        // ShutdownMode=OnExplicitShutdown"而退出。
        splash.SetStatus("正在构建主界面…");
        var mainWindow = new Views.MainWindow();
        // WPF 会把最先创建的启动提示窗记作 MainWindow；关闭它不会自动改为启动器。
        // 在任何 Loaded/ContentRendered 回调前切换宿主，避免页面的主窗口检查静默返回。
        MainWindow = mainWindow;

        // 不再在 OnStartup 这条同步 UI 调用栈里强制 mainWindow.UpdateLayout() +
        // Dispatcher.Invoke(Render)。那种“为了保证首帧而强制同步渲染”的做法会让整个主窗口
        // 的 Measure/Arrange/Render 一次性堵住 UI 线程，复杂配置/低速磁盘机器上 Windows 会
        // 直接把窗口判成“未响应”。正确做法是先挂 ContentRendered，再 Show，然后尽快让
        // OnStartup 返回消息循环；首帧真正画出来时事件自然触发，再关闭启动提示窗。
        EventHandler? closeSplashAfterFirstFrame = null;
        closeSplashAfterFirstFrame = (_, _) =>
        {
            mainWindow.FirstFrameRendered -= closeSplashAfterFirstFrame;
            try { splash.Close(); } catch { /* splash 已关闭时忽略 */ }
            // ToastHost 已注册并完成首帧；旧系统也使用启动器自己的通知栏。
            if (startupCompatibility.IsAutomatic && startupCompatibility.IsEnabled)
                ToastService.Show($"正在打开兼容模式：{startupCompatibility.DisplayName}。可在设置中调整。",
                    ToastService.ToastKind.Info, 8);
            if (mainWindow.ConfigService.Config.FollowSystemTheme && !startupCompatibility.SupportsSystemTheme)
                ToastService.Show(SystemThemeDetectionPolicy.UnsupportedMessage, ToastService.ToastKind.Warning, 10);
        };
        mainWindow.FirstFrameRendered += closeSplashAfterFirstFrame;
        splash.SetStatus("正在渲染主界面…");
        mainWindow.Show();

        // 接线 SingleInstanceService 的两个钩子：
        // - TryActivateMainWindow：新实例静默探测"能不能把我拉到前台"时调用，具体判断
        //   "窗口是否已被 Close() 掉、只是缩在托盘/便签模式后台"的逻辑都在 MainWindow 内部
        //   （它最清楚自己当前的可见性状态），这里只是把入口接上。
        // - PerformFullExit：收到别的实例发来的"确认要关闭我"指令时调用，负责结束所有
        //   游戏/服务器子进程后再退出，同样把实现放在 MainWindow（它持有 ProcessManager/
        //   ServerProcessManager 引用）。
        // 两个委托都指向同一个 mainWindow 实例，注册一次即可长期有效——本进程存活期间
        // 只会有这一个 MainWindow。
        SingleInstanceService.TryActivateMainWindow = mainWindow.TryActivateFromAnotherInstance;
        SingleInstanceService.PerformFullExit = mainWindow.PerformFullExit;

        ShutdownMode = ShutdownMode.OnLastWindowClose;

        var needsAgreementsBeforeUpdateCheck =
            mainWindow.ConfigService.Config.AcceptedAgreementVersion < Views.AgreementsText.AgreementsVersion;
        if (!needsAgreementsBeforeUpdateCheck)
        {
            UpdateCheckService.CheckForUpdateInBackground(() => mainWindow.ConfigService.Config);
        }
    }

    private static void RegisterUiInteractionLogging()
    {
        EventManager.RegisterClassHandler(typeof(ButtonBase), ButtonBase.ClickEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is ButtonBase button)
                    LogInteractionAsync(button, () => "[交互] 点击 " + DescribeElement(button));
            }), handledEventsToo: true);

        EventManager.RegisterClassHandler(typeof(Selector), Selector.SelectionChangedEvent,
            new SelectionChangedEventHandler((sender, _) =>
            {
                if (sender is Selector selector)
                    LogInteractionAsync(selector, () => "[交互] 选择变化 " + DescribeElement(selector) +
                                                  DescribeSelectedValue(selector));
            }), handledEventsToo: true);

        EventManager.RegisterClassHandler(typeof(TextBox), UIElement.GotKeyboardFocusEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is TextBox textBox)
                    LogInteractionAsync(textBox, () => "[交互] 聚焦输入框 " + DescribeElement(textBox));
            }), handledEventsToo: true);
    }

    private static void LogInteractionAsync(System.Windows.Threading.DispatcherObject element, Func<string> describe)
    {
        element.Dispatcher.InvokeAsync(() =>
        {
            try { LauncherLogService.AppendLine(describe()); }
            catch { /* 交互留痕失败不应该影响正常使用 */ }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private static string DescribeElement(FrameworkElement element)
    {
        var window = Window.GetWindow(element)?.GetType().Name ?? "UnknownWindow";
        var type = element.GetType().Name;
        var name = string.IsNullOrWhiteSpace(element.Name) ? "" : $"#{element.Name}";
        var text = TryGetElementText(element);
        return string.IsNullOrWhiteSpace(text)
            ? $"{window}.{type}{name}"
            : $"{window}.{type}{name} \"{text}\"";
    }

    private static string TryGetElementText(object element)
    {
        return element switch
        {
            ButtonBase { Content: string s } => s,
            ButtonBase { Content: TextBlock tb } => tb.Text,
            HeaderedContentControl { Header: string s } => s,
            TextBox tb when !string.IsNullOrWhiteSpace(tb.Name) => tb.Name,
            _ => ""
        };
    }

    private static string DescribeSelectedValue(Selector selector)
    {
        return selector.SelectedItem switch
        {
            null => "",
            string s => $" -> \"{s}\"",
            ComboBoxItem { Content: string s } => $" -> \"{s}\"",
            FrameworkElement fe when !string.IsNullOrWhiteSpace(fe.Name) => $" -> {fe.GetType().Name}#{fe.Name}",
            // 匿名类型（例如 ComboBox 里装 new { Label, Version } 的选项）没有有意义的 ToString，
            // 默认反射显示它的真实属性名，比 GetType().Name 那种 <>f__AnonymousType 有用得多。
            _ => $" -> {DescribeAnonymousType(selector.SelectedItem)}"
        };
    }

    /// <summary>反射列出匿名类型的属性，形如 { Label = xxx, Version = ... }；普通类型则显示类型名。</summary>
    private static string DescribeAnonymousType(object item)
    {
        var t = item.GetType();
        if (!IsAnonymousType(t))
            return t.Name;

        var props = t.GetProperties();
        if (props.Length == 0) return t.Name;

        var parts = props.Select(p =>
        {
            try
            {
                var v = p.GetValue(item);
                return $"{p.Name} = {DescribeValue(v)}";
            }
            catch { return $"{p.Name} = <获取失败>"; }
        });
        return $"{{ {string.Join(", ", parts)} }}";
    }

    private static string DescribeValue(object? value)
    {
        return value switch
        {
            null => "null",
            string s => $"\"{s}\"",
            Selector sel => DescribeSelectedValue(sel),
            _ => value.ToString() ?? ""
        };
    }

    private static bool IsAnonymousType(Type t)
    {
        // 编译器生成的匿名类型：C# 是 <>f__AnonymousType...，VB 是 VB$AnonymousType...
        return t.IsGenericType
            && (t.Name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal)
                || t.Name.StartsWith("VB$AnonymousType", StringComparison.Ordinal));
    }

    /// <summary>
    /// WPF 里"设置属性 XXX 时引发了异常"这类是外层包装异常（一般是
    /// System.Windows.Markup.XamlParseException 或 TargetInvocationException），
    /// 它自己的 Message 只会说"设置属性 XXX 时引发了异常"，完全不提到底是什么原因——
    /// 真正有诊断价值的信息在 InnerException（有时候还要再往下一层）里。之前只显示
    /// args.Exception.Message，用户看到的永远是这句没有信息量的外层包装文案，没法
    /// 定位真实问题。这里沿着 InnerException 链把每一层的类型名+消息都拼出来。
    /// </summary>
    private static string GetFullExceptionMessage(Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        var current = ex;
        var depth = 0;
        while (current != null)
        {
            if (depth > 0) sb.Append("\n\n  → 内部原因：");
            sb.Append($"[{current.GetType().Name}] {current.Message}");
            current = current.InnerException;
            depth++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// 检测当前进程实际运行在哪个 .NET 版本上。用 RuntimeInformation.FrameworkDescription
    /// (形如 ".NET 8.0.7")而不是 Environment.Version(那个反映的是 CLR 内部版本号，
    /// 跟"用户装的是 .NET 几"该概念在 .NET 5+ 之后已经对不上，容易读出误导性的结果)。
    /// 只要求主版本号是 8，不强求具体的补丁版本——.NET 的补丁版本升级向后兼容，不需要
    /// 卡死在某个具体的 8.0.x。
    /// </summary>
    private static bool IsRunningOnNet8Desktop()
    {
        try
        {
            var desc = RuntimeInformation.FrameworkDescription; // 例如 ".NET 8.0.7"
            var match = System.Text.RegularExpressions.Regex.Match(desc, @"\.NET\s+(\d+)\.");
            if (!match.Success) return true; // 解析不出版本号时不误伤用户，放行交给后续初始化去暴露真正的问题
            return int.Parse(match.Groups[1].Value) >= 8;
        }
        catch
        {
            // 检测本身出错不应该拦住启动——宁可放过一个真正有问题的环境，
            // 也不要因为检测逻辑自身的 bug 挡住所有正常用户。
            return true;
        }
    }
}
