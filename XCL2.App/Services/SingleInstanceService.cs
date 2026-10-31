using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Windows;

namespace XCL2.App.Services;

public static class SingleInstanceService
{
    // 带一个版本后缀：以后如果协议改了（比如指令格式变化），换个新管道名就能避免新旧版本
    // exe 之间互相误连接、读出一堆解析不了的数据。按登录用户隔离，避免
    // 其他桌面会话占用相同端点；访问权限仍由两端的 CurrentUserOnly 强制执行。
    private static readonly string PipeName = GetPipeName();
    private static string GetPipeName()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var user = identity.User?.Value ?? Environment.UserDomainName + "\\" + Environment.UserName;
        return "XCL2_App_SingleInstance_Pipe_v2_" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(user)));
    }
    private const int ProbeConnectTimeoutMs = 300;
    // ACTIVATE 需要等旧实例真正执行完 Dispatcher.Invoke 里的 Show()/Activate() 才能拿到
    // 回执，比单纯探测连接的 300ms 更宽松一些，避免旧实例恰好在做别的耗时同步操作
    // （比如某个弹窗的模态循环）时被误判成"拉不起来"。
    private const int ActivateReplyTimeoutMs = 1500;

    public enum ConflictChoice
    {
        /// <summary>保留此实例：两个实例都继续运行。</summary>
        KeepBothRunning,
        /// <summary>关闭此实例：新实例退出，原实例不受影响。</summary>
        CloseNewInstance,
        /// <summary>关闭此实例且拉起原来的实例：新实例退出，同时激活原实例窗口。</summary>
        CloseNewAndActivateOld,
        /// <summary>打开此实例并关闭原有实例：新实例继续启动，原实例被要求退出。</summary>
        CloseOldKeepNewInstance,
    }

    /// <summary>
    /// 供 App.xaml.cs 在创建 MainWindow 之后接线：新实例发来 "ACTIVATE" 指令时，本实例
    /// 应该怎么把自己的窗口拉到前台，返回值表示"确实拉起来了"还是"拉不起来"
    /// （比如窗口已经被真正 Close() 掉，只剩托盘/便签常驻）。
    /// 在真正接线之前（比如本实例自己也还没走到那一步）保持 null，此时按"拉不起来"处理。
    /// </summary>
    public static Func<bool>? TryActivateMainWindow { get; set; }

    /// <summary>
    /// 供 App.xaml.cs 接线：新实例发来 "CLOSE" 指令、或者本实例自己判定"拉不起来、只能
    /// 关闭"时，负责结束所有游戏/服务器子进程后再彻底退出应用。在接线之前保持 null，
    /// 此时退化成直接 Application.Shutdown()（不清理子进程），仅作为兜底，正常流程下
    /// App.xaml.cs 会在 MainWindow 创建后立刻接好该钩子。
    /// </summary>
    public static Action? PerformFullExit { get; set; }

    /// <summary>
    /// 在 RunStartupSequence 里、创建 MainWindow 之前调用。
    /// </summary>
    /// <param name="askUser">检测到已有实例在运行时才会被调用，用来弹窗询问用户四选一，
    /// 必须在 UI 线程同步返回结果（内部用 Window.ShowDialog() 实现，天然满足这一点）。</param>
    /// <returns>true：本实例应该继续往下走正常启动流程；false：本实例应该立刻退出，
    /// 不再执行任何后续初始化（不建日志 session、不建 MainWindow）。</returns>
    public static bool HandleStartup(Func<ConflictChoice> askUser)
    {
        NamedPipeClientStream? probe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        try
        {
            probe.Connect(ProbeConnectTimeoutMs);
        }
        catch
        {
            // 连接不上：没有实例在监听，本实例就是"第一个"，自己当服务端，正常继续启动。
            probe.Dispose();
            StartServer();
            return true;
        }

        // 连接成功，说明已经有一个实例在跑——用 using 保证不管走哪条分支，这条探测用的
        // 连接最终都会被正确释放（对面的 server.WaitForConnection() 才能继续接下一个连接）。
        using (probe)
        {
            var choice = askUser();
            return HandleChoice(probe, choice);
        }
    }

    private static bool HandleChoice(NamedPipeClientStream probe, ConflictChoice choice)
    {
        switch (choice)
        {
            case ConflictChoice.KeepBothRunning:
                // 不发任何指令，什么也不做：两个实例各自独立运行，互不干涉。
                // 本实例不再尝试抢当服务端——旧实例已经占着该管道名，抢不到也没必要抢，
                // 以后第三次启动时探测到的仍然是旧实例，这符合"只要有实例在跑就该弹窗提醒"
                // 的预期，不算问题。
                return true;

            case ConflictChoice.CloseNewInstance:
                return false;

            case ConflictChoice.CloseNewAndActivateOld:
            {
                var activated = TrySendActivateAndWaitReply(probe);
                if (activated)
                    return false;

                TrySendCommand(probe, "CLOSE");
                StartServer();
                return true;
            }

            case ConflictChoice.CloseOldKeepNewInstance:
                TrySendCommand(probe, "CLOSE");

                StartServer();
                return true;

            default:
                return true;
        }
    }

    /// <summary>
    /// 发送 "ACTIVATE" 指令并同步等待旧实例写回的一行回执（"OK" / "FAIL"）。
    /// 拿不到明确的 "OK" 回执（超时、连接中途断开、读到 "FAIL"、旧实例的
    /// Dispatcher.Invoke 里抛了异常导致压根没写回执等任何情况）一律按"没拉起来"处理，
    /// 交给调用方决定下一步（转去彻底关闭旧实例）——宁可保守地多问一步，也不能让
    /// "看起来关掉了新实例、但旧实例其实也没被拉起来"这种两头都够不着的情况发生。
    /// </summary>
    private static bool TrySendActivateAndWaitReply(NamedPipeClientStream client)
    {
        try
        {
            client.Write(System.Text.Encoding.UTF8.GetBytes("ACTIVATE\n"));
            client.Flush();

            using var reader = new StreamReader(client, System.Text.Encoding.UTF8, leaveOpen: true);
            var readTask = reader.ReadLineAsync();
            if (!readTask.Wait(ActivateReplyTimeoutMs))
                return false;

            return string.Equals(readTask.Result, "OK", StringComparison.Ordinal);
        }
        catch
        {
            // 这一瞬间旧实例可能碰巧已经退出、管道已经断开：按"拉不起来"处理，不能因为
            // 这里失败就阻塞或搞崩新实例自己的启动流程。
            return false;
        }
    }

    private static void TrySendCommand(NamedPipeClientStream client, string command)
    {
        try
        {
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(command);
        }
        catch
        {
            // 这一瞬间旧实例可能碰巧已经退出、管道已经断开，发不出去就算了——不能因为
            // "通知旧实例"这件事失败，就阻塞或搞崩新实例自己的启动流程。
        }
    }

    /// <summary>
    /// 后台线程常驻的管道服务端：每次 WaitForConnection 处理完一条连接（读到一行指令，或者
    /// 对方只是探测一下就断开、读不到任何内容）后 Disconnect 复用同一个流对象继续等下一次
    /// 连接，不需要每次都重新 new 一个 NamedPipeServerStream。
    /// </summary>
    private static void StartServer()
    {
        var thread = new Thread(() =>
        {
            NamedPipeServerStream? server = null;

            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    break;
                }
                catch
                {
                    Thread.Sleep(200);
                }
            }
            // 抢不到管道名也不应该拖垮主程序其它功能：安静放弃即可，代价只是"以后新开的实例
            // 探测不到该进程，会各自变成新的服务端"，多开检测退化但不影响启动器本身可用性。
            if (server == null) return;

            while (true)
            {
                try
                {
                    server.WaitForConnection();
                    string? command = null;
                    try
                    {
                        using var reader = new StreamReader(server, leaveOpen: true);
                        command = reader.ReadLine();
                    }
                    catch { /* 对方只是探测一下就断开连接（没写任何内容），忽略即可 */ }

                    if (!string.IsNullOrEmpty(command))
                        DispatchCommand(server, command);

                    server.Disconnect();
                }
                catch
                {
                    // 管道服务端自身出问题（概率很低，比如系统资源耗尽）：安静退出该循环，
                    // 不影响主程序其它功能——大不了以后的多开检测失效。
                    break;
                }
            }
        })
        { IsBackground = true, Name = "XCL2-SingleInstance-Pipe-Server" };
        thread.Start();
    }

    private static void DispatchCommand(NamedPipeServerStream server, string command)
    {
        var app = Application.Current;
        if (app == null)
        {
            // 极端情况：本实例自己都还没跑到能接住指令的阶段（理论上不该发生，因为服务端
            // 只在 HandleStartup 成功返回、即将继续正常启动流程时才会被启动），保守起见
            // 直接回 FAIL，让对面按"拉不起来"处理，而不是让它无限等到超时。
            if (string.Equals(command, "ACTIVATE", StringComparison.Ordinal))
                TryWriteReply(server, "FAIL");
            return;
        }

        // 收到指令时正处在后台线程里，UI 相关操作（切窗口前台/关闭应用）必须丢回 UI 线程，
        // 且 ACTIVATE 需要拿到 TryActivateMainWindow 的真实返回值才能回执，所以用
        // Invoke（同步等待）而不是 BeginInvoke——反正这条管道连接本来就要等回执，
        // 不会比原来更慢，还顺带解决了"到底拉没拉起来"该问题。
        switch (command)
        {
            case "ACTIVATE":
            {
                var activated = false;
                try
                {
                    app.Dispatcher.Invoke(() =>
                    {

                        activated = TryActivateMainWindow?.Invoke() ?? false;
                    });
                }
                catch
                {
                    activated = false;
                }
                TryWriteReply(server, activated ? "OK" : "FAIL");
                break;
            }
            case "CLOSE":
                _ = app.Dispatcher.BeginInvoke(() =>
                {

                    if (PerformFullExit != null)
                        PerformFullExit();
                    else
                        app.Shutdown();
                });
                break;
        }
    }

    private static void TryWriteReply(NamedPipeServerStream server, string reply)
    {
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(reply + "\n");
            server.Write(bytes, 0, bytes.Length);
            server.Flush();
        }
        catch
        {
            // 对面（新实例）可能已经因为等超时而放弃、断开了连接：写不进去就算了，
            // 不能因为回执发不出去就影响本实例自己的后续状态。
        }
    }
}
