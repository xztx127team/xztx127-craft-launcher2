using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using XCL2.App.Services;

// Run on an interactive Windows desktop with the test window in front and no physical touch/mouse input.
// This uses real InjectTouchInput -> WM_POINTER -> WPF touch events, not a mocked message sequence.
// The receiving camera is a documented GLFW disabled-cursor MODEL, not a real game integration.
internal static class Program
{
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    private static int _checks;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        _checks++;
    }

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var result = 0;
        app.Startup += async (_, _) =>
        {
            try { await Run(); Console.WriteLine($"PASS: {_checks} native pointer checks"); }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { app.Shutdown(); }
        };
        app.Run();
        return result;
    }

    private static async Task Run()
    {
        if (!InitializeTouchInjection(10, 3)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var game = new Window { Title = "Legacy pointer regression — keep in foreground", Width = 720,
            Height = 480, Left = 60, Top = 60, Background = Brushes.DimGray };
        var view = new Border { Background = Brushes.Transparent };
        var key = new Border { Width = 100, Height = 100, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(24), Background = Brushes.SteelBlue };
        var jumpKey = new Border { Width = 100, Height = 100, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(144, 24, 0, 0), Background = Brushes.SeaGreen };
        var grid = new Grid(); grid.Children.Add(view); grid.Children.Add(key); grid.Children.Add(jumpKey);
        var overlay = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true,
            Background = Brushes.Transparent, ShowActivated = false, ShowInTaskbar = false, Topmost = true,
            Width = 600, Height = 360, Left = 100, Top = 120, Content = grid };
        LegacyTouchInputSurface? surface = null;
        try
        {
            game.Show(); overlay.Show(); game.Activate();
            await Task.Delay(120);
            var gameHwnd = new WindowInteropHelper(game).Handle;
            var source = HwndSource.FromHwnd(new WindowInteropHelper(overlay).Handle)!;
            Check(GetForegroundWindow() == gameHwnd, "test game must have foreground focus");
            var bridge = new LegacyTouchInputBridge(gameHwnd, new LegacyTouchWorldEvidence());
            var held = new HashSet<int>();
            var keyContacts = new Dictionary<int, TouchDevice>();
            var jumpContacts = new Dictionary<int, TouchDevice>();
            TouchDevice? viewTouch = null;
            var last = new Point();
            var delta = new Vector();
            var ups = 0;
            var keyPoint = new Point();
            key.TouchDown += (_, e) =>
            {
                e.Handled = true; keyPoint = e.GetTouchPoint(key).Position;
                if (held.Count == 0) TouchInputInjector.KeyDown(0x57);
                keyContacts[e.TouchDevice.Id] = e.TouchDevice;
                held.Add(e.TouchDevice.Id); key.CaptureTouch(e.TouchDevice);
            };
            void ReleaseKey(int id)
            {
                if (!keyContacts.Remove(id, out var touch)) return;
                held.Remove(id);
                if (ReferenceEquals(touch.Captured, key)) key.ReleaseTouchCapture(touch);
                if (held.Count == 0) TouchInputInjector.KeyUp(0x57);
            }
            key.TouchUp += (_, e) =>
            {
                e.Handled = true; ReleaseKey(e.TouchDevice.Id);
            };
            key.LostTouchCapture += (_, e) =>
            {
                var touch = e.TouchDevice;
                LegacyTouchContactRecovery.Schedule(touch, key, () => keyContacts.ContainsKey(touch.Id),
                    () => GetForegroundWindow() == gameHwnd && surface?.OwnsContact(touch) == true,
                    () => ReleaseKey(touch.Id));
            };
            void ReleaseJump(int id)
            {
                if (!jumpContacts.Remove(id, out var touch)) return;
                if (ReferenceEquals(touch.Captured, jumpKey)) jumpKey.ReleaseTouchCapture(touch);
                if (jumpContacts.Count == 0) TouchInputInjector.KeyUp(0x20);
            }
            jumpKey.TouchDown += (_, e) =>
            {
                e.Handled = true;
                if (jumpContacts.Count == 0) TouchInputInjector.KeyDown(0x20);
                jumpContacts[e.TouchDevice.Id] = e.TouchDevice;
                jumpKey.CaptureTouch(e.TouchDevice);
            };
            jumpKey.TouchUp += (_, e) => { e.Handled = true; ReleaseJump(e.TouchDevice.Id); };
            jumpKey.LostTouchCapture += (_, e) =>
            {
                var touch = e.TouchDevice;
                LegacyTouchContactRecovery.Schedule(touch, jumpKey, () => jumpContacts.ContainsKey(touch.Id),
                    () => GetForegroundWindow() == gameHwnd && surface?.OwnsContact(touch) == true,
                    () => ReleaseJump(touch.Id));
            };
            view.TouchDown += (_, e) =>
            {
                e.Handled = true; viewTouch = e.TouchDevice;
                last = e.GetTouchPoint(view).Position; view.CaptureTouch(e.TouchDevice);
            };
            view.TouchMove += (_, e) =>
            {
                e.Handled = true;
                if (viewTouch != e.TouchDevice) return;
                var next = e.GetTouchPoint(view).Position;
                var movement = next - last;
                var screenMovement = view.PointToScreen(next) - view.PointToScreen(last);
                delta += movement; last = next;
                // Exercise production accumulation and injection while the key finger is still held.
                // Like the production overlay, inject physical screen pixels rather than WPF DIPs.
                bridge.MoveLook(screenMovement.X, screenMovement.Y);
                bridge.FlushLook();
            };
            view.TouchUp += (_, e) =>
            {
                e.Handled = true; ups++; viewTouch = null;
                bridge.ResetPendingInput(); view.ReleaseTouchCapture(e.TouchDevice);
            };
            view.LostTouchCapture += (_, e) =>
            {
                if (viewTouch != e.TouchDevice) return;
                viewTouch = null; bridge.Reset();
            };
            var mouseMoves = 0;
            var failed = false;
            surface = new LegacyTouchInputSurface(overlay, source, () => GetForegroundWindow() == gameHwnd,
                (message, wParam, lParam) =>
                {
                    if (message != 0x0200) return;
                    mouseMoves++;
                    var kind = TouchInputInjector.ClassifyMouseMessage(source.Handle, gameHwnd, message, lParam);
                    if (LegacyTouchMousePolicy.CanForwardGameplayMove(kind, surface?.HasContacts == true))
                        GlfwMouseMessageRelay.Forward(source.Handle, gameHwnd, message, wParam, lParam, kind);
                }, () => { }, () => failed = true);
            var origin = overlay.PointToScreen(new Point());
            var end = overlay.PointToScreen(new Point(overlay.ActualWidth, overlay.ActualHeight));
            surface.Show((int)origin.X, (int)origin.Y, (int)(end.X - origin.X), (int)(end.Y - origin.Y));
            TouchMousePromotionFilter.Enable(gameHwnd, source.Handle);
            for (var attempt = 0; attempt < 40 && !TouchMousePromotionFilter.IsEnabled; attempt++) await Task.Delay(25);
            Check(TouchMousePromotionFilter.IsEnabled, "the native integration test installed its hook");
            var anchor = view.PointToScreen(new Point(260, 260));
            Check(SetCursorPos((int)anchor.X, (int)anchor.Y), "set initial cursor anchor");
            await Task.Delay(60);
            Check(mouseMoves > 0, "native mouse messages still reach the overlay callback");
            using var camera = new GlfwDisabledCursorModel(game);
            await Task.Delay(60);
            camera.ResetMeasurement();
            await CheckConsumerRejectsFalseSuccess(gameHwnd, camera);
            await LegacyGameCursorProbeCases.Run(game, Check);
            camera.Recenter();
            await Task.Delay(60);
            camera.ResetMeasurement();
            Check(GetCursorPos(out var before), "read recentered cursor position");
            var keyScreen = key.PointToScreen(new Point(30, 30));
            var jumpScreen = jumpKey.PointToScreen(new Point(30, 30));
            var viewStart = view.PointToScreen(new Point(360, 160));
            var viewEnd = view.PointToScreen(new Point(384, 148));
            var pressed = Contact(1, keyScreen, Down);

            async Task Frame(bool expectLook, params POINTER_TOUCH_INFO[] contacts)
            {
                if (!InjectTouchInput((uint)contacts.Length, contacts)) throw new Win32Exception(Marshal.GetLastWin32Error());
                await Task.Delay(40);
                if (!expectLook)
                {
                    // Check before the test model warps the cursor: otherwise its recenter could
                    // hide a real button-touch warp whose mouse message was subsequently filtered.
                    Check(GetCursorPos(out var untouched) && before.X == untouched.X && before.Y == untouched.Y,
                        "a button, stationary, or release frame leaves the system cursor untouched before recentering");
                }
                // Mirror the game's end-of-poll recenter, including its new last-position baseline.
                camera.Recenter();
                await Task.Delay(40);
                Check(GetCursorPos(out var after) && before.X == after.X && before.Y == after.Y,
                    "the game-model cursor returns to its center after a touch frame");
                Check(GetForegroundWindow() == gameHwnd, "native input surface never takes game focus");
                Check(!failed, "native input callback did not fail");
            }

            await Frame(false, pressed);
            Check(held.Count == 1 && viewTouch == null, "virtual key touch does not start camera movement");
            Check((GetAsyncKeyState(0x57) & 0x8000) != 0, "movement touch holds the actual injected W state");
            Check((keyPoint - new Point(30, 30)).Length < 2, "screen pixels convert back to local control coordinates");
            pressed = Contact(1, keyScreen, Update);
            for (var i = 0; i < 4; i++) await Frame(false, pressed);
            Check(delta.Length == 0, "stationary held key produces zero view delta");
            Check(camera.CameraDelta.LengthSquared == 0, "held button produces zero consumed camera movement");
            await Frame(false, pressed, Contact(2, viewStart, Down));
            Check(held.Count == 1 && viewTouch != null, "two fingers independently hold a key and the view");
            await Frame(false, pressed, Contact(2, viewStart, Update), Contact(3, jumpScreen, Down));
            await Frame(true, pressed, Contact(2, viewEnd, Update), Contact(3, jumpScreen, Update));
            Check((GetAsyncKeyState(0x57) & 0x8000) != 0 && (GetAsyncKeyState(0x20) & 0x8000) != 0 && jumpContacts.Count == 1,
                "three independent contacts hold W and Space while the view produces consumed movement");
            await Frame(false, pressed, Contact(2, viewEnd, Update), Contact(3, jumpScreen, Up));
            Check((GetAsyncKeyState(0x57) & 0x8000) != 0 && (GetAsyncKeyState(0x20) & 0x8000) == 0,
                "releasing jump does not release forward movement or the camera contact");
            var heldDevice = keyContacts.Values.Single();
            key.ReleaseTouchCapture(heldDevice);
            Check((GetAsyncKeyState(0x57) & 0x8000) != 0, "transient capture loss must not immediately send W-up");
            await Task.Delay(20);
            Check(ReferenceEquals(heldDevice.Captured, key) && held.Count == 1 && (GetAsyncKeyState(0x57) & 0x8000) != 0,
                "the production recovery helper restores only the still-live held contact");
            Check((delta - new Vector(24, -12)).Length < 2, "view delta comes only from the moving finger");
            Check(held.Count == 1 && camera.NonzeroMoves > 0 && camera.CameraDelta.X > 0 && camera.CameraDelta.Y < 0,
                "production touch bridge produces camera movement while the key remains held, beyond message counts");
            var firstCamera = camera.CameraDelta;
            await Frame(false, pressed, Contact(2, viewEnd, Update));
            Check(camera.CameraDelta == firstCamera, "stationary fingers and a recenter frame cannot cancel or repeat camera movement");
            var continuousEnd = viewEnd;
            for (var sample = 0; sample < 6; sample++)
            {
                continuousEnd.Offset(8, -4);
                var previousCamera = camera.CameraDelta;
                await Frame(true, pressed, Contact(2, continuousEnd, Update));
                Check(camera.CameraDelta.X > previousCamera.X && camera.CameraDelta.Y < previousCamera.Y,
                    "each successive touch frame adds camera motion after native recentering");
            }
            var beforeRelease = camera.CameraDelta;
            await Frame(false, pressed, Contact(2, continuousEnd, Up));
            Check(held.Count == 1 && viewTouch == null && ups == 1, "lifting view finger keeps movement key held");
            Check((GetAsyncKeyState(0x57) & 0x8000) != 0, "camera release leaves the game's injected movement key down");
            Check(camera.CameraDelta == beforeRelease, "lifting the camera finger contributes no recenter reversal");
            await Frame(false, Contact(1, keyScreen, Up));
            Check(held.Count == 0 && !surface.HasContacts, "all contact and capture state clears on release");
            Check((GetAsyncKeyState(0x57) & 0x8000) == 0, "last movement contact sends W-up");
            Check(camera.CameraDelta == beforeRelease, "releasing the control does not move the camera");
            await CheckLookRelay(game, overlay, camera);
            Check(SetCursorPos(before.X, before.Y), "restore cursor anchor after explicit camera injection");
            await Task.Delay(60);

            await Frame(false, Contact(3, keyScreen, Down), Contact(4, viewStart, Down));
            bridge.Reset();
            surface.Hide();
            await Task.Delay(20); // Per-contact cancellation settles on the input dispatcher.
            Check(held.Count == 0 && viewTouch == null && !surface.HasContacts,
                "hide/focus cleanup releases both key and view captures");
            Check(ups == 1, "canceling view contact must not generate a release tap");
            // Finish the OS injection sequence after hiding; input outside the surface is normal Windows input.
            var release = new[] { Contact(3, keyScreen, Up), Contact(4, viewStart, Up) };
            if (!InjectTouchInput(2, release)) throw new Win32Exception(Marshal.GetLastWin32Error());
            await Task.Delay(40);
        }
        finally
        {
            TouchInputInjector.KeyUp(0x57); TouchInputInjector.KeyUp(0x20);
            TouchMousePromotionFilter.Disable(); surface?.Dispose(); overlay.Close(); game.Close();
        }
    }

    private static async Task CheckConsumerRejectsFalseSuccess(IntPtr game, GlfwDisabledCursorModel camera)
    {
        IntPtr Position(int x, int y) => new(unchecked((int)((uint)(ushort)x | ((uint)(ushort)y << 16))));
        var x = (int)camera.Center.X;
        var y = (int)camera.Center.Y;
        Check(PostMessage(game, 0x0200, IntPtr.Zero, Position(x, y)), "queue a synthetic zero-delta counterexample");
        await Task.Delay(40);
        Check(camera.MoveMessages == 1 && camera.CameraDelta.LengthSquared == 0,
            "consumer oracle distinguishes a delivered center message from actual camera movement");
        camera.ResetMeasurement();
        Check(PostMessage(game, 0x0200, IntPtr.Zero, Position(x + 12, y - 7)) &&
            PostMessage(game, 0x0200, IntPtr.Zero, Position(x, y)), "queue a stale move followed by a recenter echo");
        await Task.Delay(40);
        Check(camera.MoveMessages == 2 && camera.NonzeroMoves == 2 && camera.CameraDelta.LengthSquared == 0,
            "consumer oracle detects camera cancellation even though both posted messages were delivered");
        camera.Recenter();
        await Task.Delay(40);
        camera.ResetMeasurement();
    }

    private static async Task CheckLookRelay(Window game, Window overlay, GlfwDisabledCursorModel camera)
    {
        var gameHwnd = new WindowInteropHelper(game).Handle;
        var overlayHwnd = new WindowInteropHelper(overlay).Handle;
        async Task PrepareFrame()
        {
            camera.Recenter();
            await Task.Delay(60);
            camera.ResetMeasurement();
        }
        // Test the actual production sender -> native surface -> receiver -> game queue.
        // Count both delivery and consumed delta; a delivered center position is not success.
        await PrepareFrame();
        Check(TouchInputInjector.TryMouseMoveRelative(6, -4, gameHwnd), "production camera sender accepts a live game");
        await Task.Delay(100);
        Check(camera.NonzeroMoves == 1 && camera.CameraDelta.X > 0 && camera.CameraDelta.Y < 0,
            "one issued input produces exactly one nonzero camera movement with the hook installed");

        // Windows can remove WH_MOUSE_LL without clearing the application's cached handle.
        var field = typeof(TouchMousePromotionFilter).GetField("_hook",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var hookHandle = (IntPtr)field.GetValue(null)!;
        Check(hookHandle != IntPtr.Zero && UnhookWindowsHookEx(hookHandle), "simulate silent hook removal");
        Check(TouchMousePromotionFilter.IsEnabled, "cached hook state intentionally remains stale as in the reported failure");
        await PrepareFrame();
        Check(TouchInputInjector.TryMouseMoveRelative(8, 3, gameHwnd), "camera sender still works after hook removal");
        await Task.Delay(100);
        Check(camera.NonzeroMoves == 1 && camera.CameraDelta.X > 0 && camera.CameraDelta.Y > 0,
            "camera consumption does not require any hook-issued receipt");

        await PrepareFrame();
        TouchInputInjector.MouseClick(TouchInputInjector.MouseButton.Left);
        await Task.Delay(100);
        Check(camera.NonzeroMoves == 0 && camera.CameraDelta.LengthSquared == 0,
            "button injection and its synchronization echoes produce zero consumed camera delta");
        var pending = LegacyTouchMousePolicy.LookSignature(0x6000);
        Check(TouchMousePromotionFilter.RegisterLook(gameHwnd, pending), "register a pending packet before focus loss");
        using var inactive = new ForegroundProbeWindow();
        inactive.Show(); inactive.Activate(); await Task.Delay(60);
        Check(!TouchInputInjector.TryMouseMoveRelative(6, 4, gameHwnd), "inactive game cannot receive an issued camera packet");
        Check(TouchMousePromotionFilter.ClassifyMessage(gameHwnd, overlayHwnd, 0x0200, 510, 300, 50,
            pending, 0, 0) == LegacyMouseMessageKind.Rejected, "focus loss rejects an in-flight packet");
        inactive.Close(); game.Activate(); await Task.Delay(60);
        Check(TouchMousePromotionFilter.ClassifyMessage(gameHwnd, overlayHwnd, 0x0200, 510, 300, 50,
            pending, 0, 0) == LegacyMouseMessageKind.Rejected, "focus restoration cannot revive a discarded packet");
    }

    private sealed class ForegroundProbeWindow : Window, IDisposable
    {
        public ForegroundProbeWindow() { Width = 160; Height = 100; Left = 820; Top = 80; Title = "Input focus probe"; }
        public void Dispose() { if (IsVisible) Close(); }
    }

    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);

    private const uint Down = 0x00010000 | 0x2 | 0x4, Update = 0x00020000 | 0x2 | 0x4, Up = 0x00040000;
    private static POINTER_TOUCH_INFO Contact(uint id, Point point, uint flags)
    {
        var p = new POINT { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) };
        return new POINTER_TOUCH_INFO { Pointer = new POINTER_INFO { Type = 2, Id = id, Flags = flags, Pixel = p } };
    }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINTER_INFO
    {
        public uint Type, Id, FrameId, Flags;
        public IntPtr SourceDevice, Target;
        public POINT Pixel, Himetric, RawPixel, RawHimetric;
        public uint Time, HistoryCount;
        public int InputData;
        public uint KeyStates;
        public ulong PerformanceCount;
        public uint ButtonChangeType;
    }
    [StructLayout(LayoutKind.Sequential)] private struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO Pointer;
        public uint TouchFlags, TouchMask;
        public RECT Contact, RawContact;
        public uint Orientation, Pressure;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool InitializeTouchInjection(uint maxCount, uint feedbackMode);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool InjectTouchInput(uint count, [In] POINTER_TOUCH_INFO[] contacts);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
