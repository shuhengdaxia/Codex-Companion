using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

internal interface ICodexQuitDesktop
{
    bool IsExpectedWindow(IntPtr window, int processId);
    bool IsEnabled(IntPtr window);
    bool RequestExitMenu(IntPtr window, int processId);
}

// Codex removes the native Windows application menu; a keyboard shortcut is
// not a reliable app-exit command. Invoke the tray's real Quit/Exit item.
internal sealed class CodexDesktopQuit : ICodexQuitDesktop
{
    internal static Func<IntPtr, int, bool> ApplicationMenuExitOverride { get; set; }

    internal static IntPtr FindWindow(Process process)
    {
        IntPtr main = process.MainWindowHandle;
        if (main != IntPtr.Zero) return main;
        return FindWindow(process.Id);
    }

    // Process.MainWindowHandle omits a window hidden to the tray. Once the
    // executable owner has been verified, prefer its known main title and
    // otherwise accept the only root Chromium window from that same process.
    internal static IntPtr FindWindow(int processId)
    {
        IntPtr known = IntPtr.Zero;
        IntPtr fallback = IntPtr.Zero;
        EnumWindows(delegate(IntPtr window, IntPtr parameter) {
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            if (owner != processId || GetWindow(window, 4) != IntPtr.Zero) return true;
            var className = new StringBuilder(128);
            GetClassName(window, className, className.Capacity);
            if (className.ToString() != "Chrome_WidgetWin_1") return true;
            if (fallback == IntPtr.Zero) fallback = window;
            var title = new StringBuilder(128);
            GetWindowText(window, title, title.Capacity);
            if (title.ToString() == "ChatGPT" || title.ToString() == "Codex")
            {
                known = window;
                return !IsWindowVisible(window);
            }
            return true;
        }, IntPtr.Zero);
        return known != IntPtr.Zero ? known : fallback;
    }

    internal static bool RequestQuit(Process process)
    {
        if (process.HasExited) return true;
        return RequestQuit(FindWindow(process), process.Id, new CodexDesktopQuit());
    }

    internal static bool RequestQuit(IntPtr window, int processId, ICodexQuitDesktop desktop)
    {
        if (window == IntPtr.Zero || !desktop.IsExpectedWindow(window, processId) ||
            !desktop.IsEnabled(window)) return false;
        return desktop.RequestExitMenu(window, processId);
    }

    public bool IsExpectedWindow(IntPtr window, int processId)
    {
        uint owner;
        return GetWindowThreadProcessId(window, out owner) != 0 && owner == processId;
    }

    public bool IsEnabled(IntPtr window) { return IsWindowEnabled(window); }

    public bool RequestExitMenu(IntPtr window, int processId)
    {
        if (!IsExpectedWindow(window, processId) || !IsWindowEnabled(window)) return false;
        bool requested = ApplicationMenuExitOverride == null ?
            CodexApplicationMenu.RequestExit(window, processId) : ApplicationMenuExitOverride(window, processId);
        if (requested) return true;
        IntPtr tray = CodexTrayMenu.FindTrayWindow(processId);
        if (tray != IntPtr.Zero) return CodexTrayMenu.RequestExit(tray, processId, true);
        // WM_CLOSE only hides current Codex builds. Reporting it as an exit
        // request causes the caller to wait until timeout while the app remains.
        return false;
    }

    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
}
