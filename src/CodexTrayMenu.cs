using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

// Electron's NotifyIconHost exposes a per-process tray callback window.
// Source: electron/electron shell/browser/ui/win/notify_icon_host.{cc,h}.
// Request only its context menu, then resolve the actual accessible exit item.
// Never send guessed command IDs, inject keys, read chat controls, or kill processes.
internal static class CodexTrayMenu
{
    // Current Codex uses its OwlElectron fork; ordinary Electron is retained
    // for older builds. Both still require the exact desktop process owner.
    internal const string TrayClass = "OwlElectron_NotifyIconHostWindow";
    private static bool IsTrayClass(string name)
    { return name == TrayClass || name == "Electron_NotifyIconHostWindow"; }
    internal static Action<string> TestObservation { get; set; }
    private static void Observe(string value) { if (TestObservation != null) TestObservation(value); }

    internal static IntPtr FindTrayWindow(int processId)
    {
        IntPtr result = IntPtr.Zero;
        int count = 0;
        EnumWindows(delegate(IntPtr window, IntPtr unused) {
            if (OwnedBy(window, processId) && (ClassName(window) == TrayClass || ClassName(window) == "Electron_NotifyIconHostWindow"))
            { result = window; count++; }
            return true;
        }, IntPtr.Zero);
        if (count > 1) throw new InvalidOperationException("发现多个 Codex 托盘窗口，无法唯一确认退出入口。请手动从官方托盘菜单退出后重试。");
        return result;
    }

    // A background-only Codex can destroy its Chromium window while retaining
    // the official Electron tray callback host. Replaying the tray's left-click
    // notification asks Codex itself to recreate/show the main window. Unknown
    // icon IDs are ignored; stop as soon as the exact owner exposes its window.
    internal static bool RequestShow(int processId)
    {
        IntPtr existing = CodexDesktopQuit.FindWindow(processId);
        if (existing != IntPtr.Zero)
        {
            ShowWindowAsync(existing, 5);
            var visibleTimer = Stopwatch.StartNew();
            while (visibleTimer.ElapsedMilliseconds < 500)
            {
                if (IsWindowVisible(existing)) return true;
                Thread.Sleep(20);
            }
        }

        IntPtr tray = FindTrayWindow(processId);
        if (tray == IntPtr.Zero || !OwnedBy(tray, processId) || !IsTrayClass(ClassName(tray))) return false;
        UIntPtr response;
        if (SendMessageTimeout(tray, 0, IntPtr.Zero, IntPtr.Zero, 2, 1000, out response) == IntPtr.Zero) return false;
        for (int firstId = 0; firstId < 256; firstId += 8)
        {
            if (!OwnedBy(tray, processId) || !IsTrayClass(ClassName(tray))) return false;
            for (int iconId = firstId; iconId < firstId + 8; iconId++)
                if (SendMessageTimeout(tray, 0x8001, new IntPtr(iconId), new IntPtr(0x0202), 2, 500, out response) == IntPtr.Zero)
                    return false;
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 150)
            {
                IntPtr window = CodexDesktopQuit.FindWindow(processId);
                if (window != IntPtr.Zero)
                {
                    ShowWindowAsync(window, 5);
                    return true;
                }
                Thread.Sleep(15);
            }
        }
        return false;
    }

    internal static bool RequestExit(IntPtr tray, int processId, bool invoke)
    {
        if (!OwnedBy(tray, processId) || !IsTrayClass(ClassName(tray))) return false;
        var before = new HashSet<IntPtr>(VisibleWindows(processId));
        uint foregroundOwner; GetWindowThreadProcessId(GetForegroundWindow(), out foregroundOwner);
        Observe("before-classes=" + String.Join(",", new List<IntPtr>(before).ConvertAll(ClassName)) + " foreground-owner=" + foregroundOwner + " caller=" + Process.GetCurrentProcess().Id);
        bool foregroundAllowed = AllowSetForegroundWindow((uint)processId);
        Observe("allow-foreground=" + foregroundAllowed);
        UIntPtr response;
        IntPtr delivered = SendMessageTimeout(tray, 0, IntPtr.Zero, IntPtr.Zero, 2, 1000, out response);
        Observe("tray-responsive=" + (delivered != IntPtr.Zero));
        if (delivered == IntPtr.Zero) return false;
        // Account for fork-specific initial IDs and IDs advancing when the
        // tray is recreated. Unknown IDs are ignored by the tray host.
        // Both supported hosts use legacy notifications, not VERSION_4.
        // Batch obsolete IDs (ignored by Electron) to tolerate tray recreation
        // without spending the shutdown deadline on one wait per old icon.
        for (int firstId = 0; firstId < 256; firstId += 8)
        {
            if (!OwnedBy(tray, processId) || !IsTrayClass(ClassName(tray))) return false;
            for (int iconId = firstId; iconId < firstId + 8; iconId++)
                if (SendMessageTimeout(tray, 0x8001, new IntPtr(iconId), new IntPtr(0x007B), 2, 500, out response) == IntPtr.Zero)
                { Observe("menu-message-failed=" + Marshal.GetLastWin32Error()); return false; }
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 300)
            {
                // Windows may create a separate SysShadow for the popup. It is
                // decoration, not a second menu or an actionable window.
                var opened = VisibleWindows(processId).FindAll(window => !before.Contains(window) && ClassName(window) != "SysShadow");
                if (opened.Count > 0) Observe("icon-range=" + firstId + ".." + (firstId + 7) + " popup-count=" + opened.Count + " classes=" + String.Join(",", opened.ConvertAll(ClassName)));
                if (opened.Count > 1) return false;
                if (opened.Count == 1)
                {
                    bool invoked = false;
                    try { invoked = InvokeExitItem(opened[0], processId, invoke); return invoked; }
                    finally
                    {
                        if ((!invoke || !invoked) && OwnedBy(opened[0], processId))
                            PostMessage(opened[0], 0x001F, IntPtr.Zero, IntPtr.Zero);
                    }
                }
                Thread.Sleep(20);
            }
        }
        Observe("after-classes=" + String.Join(",", VisibleWindows(processId).ConvertAll(ClassName)));
        return false;
    }

    private static bool InvokeExitItem(IntPtr popup, int processId, bool invoke)
    {
        var timer = Stopwatch.StartNew();
        bool observed = false;
        while (timer.ElapsedMilliseconds < 2000)
        {
            if (!OwnedBy(popup, processId) || !IsWindowVisible(popup)) { Observe("popup-no-longer-visible"); return false; }
            try
            {
                // The new popup only: never traverse the existing main window.
                AutomationElement root = AutomationElement.FromHandle(popup);
                if (!observed)
                {
                    var types = new List<string>();
                    foreach (AutomationElement child in root.FindAll(TreeScope.Children, Condition.TrueCondition))
                        types.Add(child.Current.ControlType.ProgrammaticName);
                    Observe("root-type=" + root.Current.ControlType.ProgrammaticName + " child-types=" + String.Join(",", types));
                    observed = true;
                }
                AutomationElement menu = root.Current.ControlType == ControlType.Menu ? root :
                    root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu));
                if (menu != null)
                {
                    AutomationElementCollection items = menu.FindAll(TreeScope.Children,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));
                    if (items.Count > 0)
                    {
                        // The installed app appends its official Exit last, after
                        // thread items and a separator. Read only this final label.
                        AutomationElement exit = items[items.Count - 1];
                        Observe("items=" + items.Count + " exit-label=" + IsExitLabel(exit.Current.Name) + " enabled=" + exit.Current.IsEnabled + " offscreen=" + exit.Current.IsOffscreen);
                        if (!IsExitLabel(exit.Current.Name) || !exit.Current.IsEnabled || exit.Current.IsOffscreen) return false;
                        object action;
                        if (!exit.TryGetCurrentPattern(InvokePattern.Pattern, out action)) { Observe("no-invoke-pattern"); return false; }
                        if (!OwnedBy(popup, processId) || !IsWindowVisible(popup)) return false;
                        if (invoke) ((InvokePattern)action).Invoke();
                        return true;
                    }
                }
            }
            catch (ElementNotAvailableException error) { Observe("popup-accessibility-unavailable=" + error.HResult); return false; }
            catch (InvalidOperationException error) { Observe("popup-accessibility-invalid=" + error.HResult); return false; }
            Thread.Sleep(25);
        }
        return false;
    }

    internal static bool IsExitLabel(string label)
    {
        if (String.IsNullOrWhiteSpace(label)) return false;
        string normalized = Regex.Replace(label.Split('	')[0], @" *[（(]&?[A-Za-z][)）]", "").Replace("&", "").Trim();
        return Regex.IsMatch(normalized, @"^(?:(?:Exit|Quit)(?: +(?:Codex|ChatGPT))?|退出(?: *(?:Codex|ChatGPT))?|結束(?: *(?:Codex|ChatGPT))?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static List<IntPtr> VisibleWindows(int processId)
    {
        var windows = new List<IntPtr>();
        EnumWindows(delegate(IntPtr window, IntPtr unused) {
            if (OwnedBy(window, processId))
            {
                if (IsWindowVisible(window)) windows.Add(window);
                EnumChildWindows(window, delegate(IntPtr child, IntPtr parameter) {
                    if (OwnedBy(child, processId) && IsWindowVisible(child)) windows.Add(child);
                    return true;
                }, IntPtr.Zero);
            }
            return true;
        }, IntPtr.Zero);
        return windows;
    }
    private static bool OwnedBy(IntPtr window, int processId)
    { uint owner; return GetWindowThreadProcessId(window, out owner) != 0 && owner == processId; }
    private static string ClassName(IntPtr window)
    { var text = new StringBuilder(128); GetClassName(window, text, text.Capacity); return text.ToString(); }
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
