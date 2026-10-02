using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

// Current Codex renders its official Windows File menu in the application UI.
// Use only its fixed automation IDs and the final exact Quit/Exit label; never
// enumerate chat controls or send keyboard input to an assumed foreground app.
internal static class CodexApplicationMenu
{
    private const string MainWindowClass = "Chrome_WidgetWin_1";
    private const string FileMenuId = "application-menu-trigger-file";
    private const string MenuContentId = "application-menu-content";

    internal static bool RequestExit(IntPtr window, int processId)
    {
        if (!OwnedBy(window, processId) || ClassName(window) != MainWindowClass) return false;
        AllowSetForegroundWindow((uint)processId);
        ShowWindowAsync(window, 5);
        SetForegroundWindow(window);

        AutomationElement file = FindUnique(window, FileMenuId, 2000);
        if (file == null || !file.Current.IsEnabled || file.Current.IsOffscreen) return false;
        object action;
        try
        {
            if (file.TryGetCurrentPattern(InvokePattern.Pattern, out action))
                ((InvokePattern)action).Invoke();
            else if (file.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out action))
                ((ExpandCollapsePattern)action).Expand();
            else return false;
        }
        catch (ElementNotAvailableException) { return false; }
        catch (InvalidOperationException) { return false; }

        AutomationElement content = FindUnique(window, MenuContentId, 2000);
        if (content == null) return false;
        try
        {
            AutomationElement menu = content.Current.ControlType == ControlType.Menu ? content :
                content.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu));
            if (menu == null) return false;
            AutomationElementCollection items = menu.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));
            if (items.Count == 0) return false;
            AutomationElement exit = items[items.Count - 1];
            if (!CodexTrayMenu.IsExitLabel(exit.Current.Name) || !exit.Current.IsEnabled || exit.Current.IsOffscreen)
                return false;
            if (!exit.TryGetCurrentPattern(InvokePattern.Pattern, out action)) return false;
            ((InvokePattern)action).Invoke();
            return true;
        }
        catch (ElementNotAvailableException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static AutomationElement FindUnique(IntPtr window, string automationId, int timeoutMilliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < timeoutMilliseconds)
        {
            if (!IsWindow(window)) return null;
            try
            {
                AutomationElement root = AutomationElement.FromHandle(window);
                AutomationElementCollection matches = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
                if (matches.Count > 1) return null;
                if (matches.Count == 1) return matches[0];
            }
            catch (ElementNotAvailableException) { return null; }
            catch (InvalidOperationException) { return null; }
            Thread.Sleep(25);
        }
        return null;
    }

    private static bool OwnedBy(IntPtr window, int processId)
    { uint owner; return GetWindowThreadProcessId(window, out owner) != 0 && owner == processId; }
    private static string ClassName(IntPtr window)
    { var text = new StringBuilder(128); GetClassName(window, text, text.Capacity); return text.ToString(); }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
}
