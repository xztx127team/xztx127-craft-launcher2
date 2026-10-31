using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using XCL2.App.Models;

namespace XCL2.App.Services;

/// <summary>明确的桌面操作入口；使用当前用户权限，始终先检查操作开关和工作模式。</summary>
internal static class AiComputerControlService
{
    internal static bool IsComputerAction(string? action) => action is
        "mouse_move" or "mouse_click" or "mouse_right_click" or "key_press" or "keyboard_type" or "window_open" or "window_close";

    internal static string Execute(JsonElement request, AiAssistantConfig? config, AiWorkModeContext workMode, CancellationToken ct)
    {
        var action = AiControlProtocol.String(request, "action");
        workMode.Demand(action);
        var denial = AiCommandPermissionService.GetAccessDenial(config, action, AiControlProtocol.String(request, "path"), workMode.EffectiveMode);
        if (denial != null) return denial;
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return "系统：电脑操作只支持 Windows，未执行。";
        try
        {
            // 此处没有 await：权限检查到 Win32 调用之间不让出 UI 线程。
            switch (action)
            {
                case "mouse_move":
                    if (!SetCursorPos(request.GetProperty("x").GetInt32(), request.GetProperty("y").GetInt32()))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    break;
                case "mouse_click": SendMouse(0x0002, 0x0004); break;
                case "mouse_right_click": SendMouse(0x0008, 0x0010); break;
                case "key_press":
                    var text = AiControlProtocol.String(request, "key") ?? "";
                    if (!Enum.TryParse<System.Windows.Forms.Keys>(text, true, out var key)
                        || key == System.Windows.Forms.Keys.None || !Enum.IsDefined(key) || ((int)key & ~0xffff) != 0)
                        return "系统：key_press 的 key 必须是一个 Windows 键名（如 Enter、Tab、Escape、A），不支持组合键。";
                    SendKey((ushort)key);
                    break;
                case "keyboard_type":
                    SendText(AiControlProtocol.String(request, "content") ?? "");
                    break;
                case "window_open":
                    var path = AiControlProtocol.String(request, "path")!;
                    if (!System.IO.Path.IsPathRooted(path) || !System.IO.File.Exists(path))
                        return "系统：window_open 只接受已授权范围内的现有本地文件绝对路径。";
                    using (Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true })) { }
                    break;
                case "window_close":
                    using (var process = Process.GetProcessById(request.GetProperty("process_id").GetInt32()))
                    {
                        if (process.Id == Environment.ProcessId) return "系统：不能通过电脑操作关闭启动器自身。";
                        if (!process.CloseMainWindow()) return "系统：该进程没有可关闭的主窗口，未强制结束进程。";
                    }
                    break;
                default: return "系统：未知电脑操作，未执行。";
            }
            return "系统：电脑操作已执行：" + action + "。";
        }
        catch (Exception ex) { return "系统：电脑操作失败：" + ex.Message; }
    }

    internal static string BuildPrompt(AiAssistantConfig? config)
    {
        var permissions = config?.CommandPermissions ?? new AiCommandPermissions();
        return "\n电脑操作权限由 AI 设置中的鼠标移动、点击、右键、键盘、打开/关闭窗口开关控制，默认关闭。"
            + "用户要求操作电脑但相应开关关闭时，请明确建议用户去 AI 设置 → Shell / 命令权限打开相应开关，不要用 Shell 或 Python 绕过。"
            + $"当前权限：鼠标移动={permissions.MouseMove}，点击={permissions.MouseClick}，右键={permissions.MouseRightClick}，键盘={permissions.Keyboard}，打开窗口={permissions.OpenWindow}，关闭窗口={permissions.CloseWindow}。"
            + "已授权时可请求 <<XCL2_ACCESS:{\"action\":\"mouse_move\",\"x\":100,\"y\":100}>>、mouse_click、mouse_right_click；"
            + "key_press 使用 key Windows 键名；keyboard_type 使用 content 输入文本（最多 2000 字符）；window_open 使用 path 本地文件绝对路径；window_close 使用 process_id 数字。每个标记只执行一个操作。";
    }

    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    { public int Dx, Dy; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr ExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
    private static void SendMouse(uint down, uint up) => Send(new[] {
        new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = down } } },
        new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = up } } } });
    private static void SendKey(ushort key) => Send(new[] {
        new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = key } } },
        new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = key, Flags = 0x0002 } } } });
    private static void SendText(string text)
    {
        foreach (var character in text)
            Send(new[] {
                new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Scan = character, Flags = 0x0004 } } },
                new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Scan = character, Flags = 0x0006 } } } });
    }
    private static void Send(Input[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法发送鼠标/键盘输入；目标程序的权限可能高于启动器。");
    }
}
