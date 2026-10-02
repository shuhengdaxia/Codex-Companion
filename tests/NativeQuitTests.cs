using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// Creates only a window owned by this test runner. Never targets real Codex.
internal static class NativeQuitTests
{
    private const string WindowClass = "Chrome_WidgetWin_1";
    private static readonly WindowProcedure Procedure = OnMessage;
    private static bool receivedQuit;
    private static int closeCount;
    private static int iconId;
    private static int menuRequests;
    private static ContextMenuStrip menu;

    internal static void Run()
    {
        Func<IntPtr, int, bool> previousApplicationMenu = CodexDesktopQuit.ApplicationMenuExitOverride;
        CodexDesktopQuit.ApplicationMenuExitOverride = delegate(IntPtr ignoredWindow, int ignoredProcessId) { return false; };
        receivedQuit = false;
        closeCount = 0;
        IntPtr instance = GetModuleHandle(null);
        var type = new WindowClassInfo { Size = (uint)Marshal.SizeOf(typeof(WindowClassInfo)), Procedure = Procedure, Instance = instance, ClassName = WindowClass };
        if (RegisterClassEx(ref type) == 0) throw new InvalidOperationException("Could not create the isolated quit-test window class.");
        type.ClassName = CodexTrayMenu.TrayClass;
        if (RegisterClassEx(ref type) == 0) throw new InvalidOperationException("Could not create the isolated tray-test window class.");
        IntPtr window = IntPtr.Zero;
        IntPtr tray = IntPtr.Zero;
        try
        {
            window = CreateWindowEx(0, WindowClass, "Codex", 0x00CF0000, 50, 50, 200, 100, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (window == IntPtr.Zero) throw new InvalidOperationException("Could not create the isolated quit-test window.");
            tray = CreateWindowEx(0, CodexTrayMenu.TrayClass, "", 0x80000000, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (tray == IntPtr.Zero) throw new InvalidOperationException("Could not create the isolated tray-test window.");
            ShowWindow(window, 5);
            Application.DoEvents();
            SendMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
            Assert(closeCount == 1 && !receivedQuit && !IsWindowVisible(window), "WM_CLOSE must reproduce hide-to-tray without quitting.");
            using (Process process = Process.GetCurrentProcess())
            {
                process.Refresh();
                Assert(CodexDesktopQuit.FindWindow(process) == window, "The hidden main window must still be discoverable.");
                Assert(CodexTrayMenu.FindTrayWindow(process.Id) == tray, "Find only the tray owned by the expected process.");
                Task<bool> show = Task.Run(() => CodexTrayMenu.RequestShow(process.Id));
                var showTimer = Stopwatch.StartNew();
                while ((!show.IsCompleted || !IsWindowVisible(window)) && showTimer.ElapsedMilliseconds < 2000)
                { Application.DoEvents(); Thread.Sleep(10); }
                Assert(show.IsCompleted && show.GetAwaiter().GetResult() && IsWindowVisible(window),
                    "The official tray left-click callback must restore a hidden main window.");
                SendMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
                Assert(!IsWindowVisible(window), "The restored fixture window must return to the background state.");
                int closeCountAfterRestore = closeCount;
                RunMenuCase(window, process.Id, 3, "Exit Codex", true, true);
                RunMenuCase(window, process.Id, 9, "退出(&X)", true, true);
                RunMenuCase(window, process.Id, 3, "Exit Codex", false, false);
                RunMenuCase(window, process.Id, 3, "Delete project", true, false);
                Assert(!CodexTrayMenu.RequestExit(tray, Int32.MaxValue, true), "A different process must not receive a menu request.");
                Assert(closeCount == closeCountAfterRestore, "When a tray exists, failed exit lookup must not fall back to window close.");
            }
            Console.WriteLine("Native tray exit, recreated icon, hidden-window recovery and rejected-menu checks passed in isolated windows.");
        }
        finally
        {
            CodexDesktopQuit.ApplicationMenuExitOverride = previousApplicationMenu;
            if (window != IntPtr.Zero) DestroyWindow(window);
            if (tray != IntPtr.Zero) DestroyWindow(tray);
            if (menu != null) { menu.Dispose(); menu = null; }
            UnregisterClass(WindowClass, instance);
            UnregisterClass(CodexTrayMenu.TrayClass, instance);
        }
    }

    private static void RunMenuCase(IntPtr window, int processId, int requestedIcon, string finalLabel, bool enabled, bool expected)
    {
        receivedQuit = false;
        iconId = requestedIcon;
        menuRequests = 0;
        var observations = new List<string>();
        CodexTrayMenu.TestObservation = observations.Add;
        using (menu = new ContextMenuStrip())
        {
            // The fixture is not opened by a user click and may not own the
            // foreground. Keep it visible until the test explicitly closes it.
            menu.AutoClose = false;
            menu.Items.Add("Exit").Click += delegate { throw new InvalidOperationException("Must not invoke a thread merely named Exit."); };
            menu.Items.Add(new ToolStripSeparator());
            ToolStripItem exit = menu.Items.Add(finalLabel);
            exit.Enabled = enabled;
            exit.Click += delegate { receivedQuit = true; };
            Task<bool> quit = Task.Run(() => CodexDesktopQuit.RequestQuit(window, processId, new CodexDesktopQuit()));
            var timer = Stopwatch.StartNew();
            while ((!quit.IsCompleted || (expected && !receivedQuit)) && timer.ElapsedMilliseconds < 9000)
            { Application.DoEvents(); Thread.Sleep(10); }
            Assert(quit.IsCompleted, "Tray request must finish within its bounded wait.");
            bool result = quit.GetAwaiter().GetResult();
            Application.DoEvents();
            Assert(result == expected && receivedQuit == expected, "Only an enabled official final exit item may be invoked: " + finalLabel +
                "; requested=" + menuRequests + "; returned=" + result + "; invoked=" + receivedQuit + "; observations=" + String.Join(" / ", observations));
            menu.Close();
            Application.DoEvents();
        }
        menu = null;
        CodexTrayMenu.TestObservation = null;
    }

    private static IntPtr OnMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == 0x0010) { closeCount++; ShowWindow(window, 0); return IntPtr.Zero; }
        if (message == 0x8001 && wParam.ToInt64() == iconId && lParam.ToInt64() == 0x007B && menu != null)
        { menuRequests++; menu.Show(new Point(100, 100)); return IntPtr.Zero; }
        return DefWindowProc(window, message, wParam, lParam);
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassInfo
    {
        internal uint Size, Style;
        internal WindowProcedure Procedure;
        internal int ClassExtra, WindowExtra;
        internal IntPtr Instance, Icon, Cursor, Background;
        internal string MenuName, ClassName;
        internal IntPtr SmallIcon;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WindowClassInfo value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string name, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
