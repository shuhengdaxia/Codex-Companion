using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// Keeps restart process ownership narrow: only the exact official desktop
// executable discovered by CodexInstall.Desktop() can be inspected or closed.
internal sealed class CodexProcessAdapter : IDisposable
{
    private readonly Func<bool> hasExited;
    private readonly Func<IntPtr> mainWindowHandle;
    private readonly Func<bool> closeMainWindow;
    private readonly Action refresh;
    private readonly Action dispose;
    private bool disposed;
    internal int ProcessId { get; private set; }

    internal CodexProcessAdapter(Process process)
        : this(delegate { return process.HasExited; },
            delegate { return CodexDesktopQuit.FindWindow(process); },
            delegate { return CodexDesktopQuit.RequestQuit(process); },
            delegate { process.Refresh(); },
            delegate { process.Dispose(); }, process.Id)
    {
    }

    internal CodexProcessAdapter(Func<bool> hasExited, Func<IntPtr> mainWindowHandle,
        Func<bool> closeMainWindow, Action refresh, Action dispose, int processId = 0)
    {
        this.hasExited = hasExited;
        this.mainWindowHandle = mainWindowHandle;
        this.closeMainWindow = closeMainWindow;
        this.refresh = refresh;
        this.dispose = dispose;
        ProcessId = processId;
    }

    internal bool HasExited { get { return hasExited(); } }
    internal IntPtr MainWindowHandle { get { return mainWindowHandle(); } }
    internal bool CloseMainWindow() { return closeMainWindow(); }
    internal void Refresh() { if (refresh != null) refresh(); }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (dispose != null) dispose();
    }
}

public static class CodexDesktopRestart
{
    private const int GracefulCloseTimeoutMilliseconds = 30000;
    private const int BackgroundActivationDelayMilliseconds = 250;
    private const int ActivatedWindowTimeoutMilliseconds = 5000;
    internal static Action<CancellationToken> CloseAndWaitOverride { get; set; }
    internal static Func<string, List<CodexProcessAdapter>> FindOverride { get; set; }
    internal static Func<string> ExecutableOverride { get; set; }
    internal static Action<string> ActivateOverride { get; set; }
    private static string Executable()
    { return ExecutableOverride == null ? CodexInstall.Desktop() : ExecutableOverride(); }

    public static void RequireClosed()
    {
        string executable = Executable();
        List<CodexProcessAdapter> processes = Find(executable);
        try
        {
            foreach (CodexProcessAdapter process in processes)
            {
                if (!process.HasExited)
                    throw new InvalidOperationException("Codex 在操作期间重新启动，配置未写入。请稍后重试。");
            }
        }
        finally
        {
            foreach (CodexProcessAdapter process in processes) process.Dispose();
        }
    }

    public static void CloseAndWait(CancellationToken cancellationToken)
    {
        if (CloseAndWaitOverride != null)
        {
            CloseAndWaitOverride(cancellationToken);
            return;
        }
        CloseAndWaitCore(Executable(), cancellationToken, GracefulCloseTimeoutMilliseconds);
    }

    internal static void CloseAndWaitCore(string executable, CancellationToken cancellationToken, int timeoutMilliseconds)
    {
        var timer = Stopwatch.StartNew();
        var requestedWindows = new HashSet<IntPtr>();
        var requestedProcesses = new HashSet<int>();
        int quietPolls = 0;
        bool activationRequested = false;
        long activationRequestedAt = 0;
        while (timer.ElapsedMilliseconds < timeoutMilliseconds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Re-enumerate while waiting: a starting client can create its main
            // window or additional desktop processes after the first snapshot.
            List<CodexProcessAdapter> processes = Find(executable);
            var backgroundProcessIds = new List<int>();
            bool running = false;
            bool foundWindow = false;
            try
            {
                foreach (CodexProcessAdapter process in processes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IntPtr mainWindow;
                    try
                    {
                        process.Refresh();
                        if (process.HasExited) continue;
                        running = true;
                        if (process.ProcessId != 0 && requestedProcesses.Contains(process.ProcessId)) continue;
                        mainWindow = process.MainWindowHandle;
                    }
                    catch (Exception error) { throw CannotConfirmExit(error); }
                    if (mainWindow == IntPtr.Zero)
                    {
                        if (process.ProcessId != 0) backgroundProcessIds.Add(process.ProcessId);
                        continue;
                    }
                    if (!requestedWindows.Add(mainWindow)) continue;
                    foundWindow = true;
                    if (!process.CloseMainWindow())
                    {
                        if (process.HasExited) continue;
                        throw new InvalidOperationException("无法请求官方 Codex 优雅退出，配置未修改。请处理打开的对话框，或从 Codex 托盘菜单选择退出后重试。");
                    }
                    if (process.ProcessId != 0) requestedProcesses.Add(process.ProcessId);
                }
            }
            finally
            {
                foreach (CodexProcessAdapter process in processes) process.Dispose();
            }
            if (running && !foundWindow && requestedWindows.Count == 0 && !activationRequested &&
                timer.ElapsedMilliseconds >= BackgroundActivationDelayMilliseconds &&
                (ActivateOverride != null || File.Exists(executable)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (ActivateOverride != null) ActivateOverride(executable);
                    else
                    {
                        bool restored = false;
                        foreach (int processId in backgroundProcessIds)
                            if (CodexTrayMenu.RequestShow(processId)) { restored = true; break; }
                        if (!restored) using (Process activated = CodexInstall.StartDesktop(executable, "")) { }
                    }
                }
                catch (Exception error)
                {
                    throw new InvalidOperationException("Codex 正在后台运行，但无法恢复官方主窗口以请求正常退出。配置未修改，请手动打开并退出 Codex 后重试。", error);
                }
                activationRequested = true;
                activationRequestedAt = timer.ElapsedMilliseconds;
            }
            if (activationRequested && requestedWindows.Count == 0 &&
                timer.ElapsedMilliseconds - activationRequestedAt >= ActivatedWindowTimeoutMilliseconds)
                throw new InvalidOperationException("已尝试恢复 Codex 主窗口，但没有出现可请求正常退出的界面。配置未修改，请手动打开并退出 Codex 后重试。");
            quietPolls = running ? 0 : quietPolls + 1;
            if (quietPolls >= 2) return;
            cancellationToken.WaitHandle.WaitOne(100);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (requestedWindows.Count == 0)
            throw new InvalidOperationException("Codex 仍在启动或后台运行，没有可请求正常退出的主窗口。配置未修改，请手动打开并退出 Codex 后重试。");
        throw new TimeoutException("等待 Codex 正常退出超时，配置未修改。请确认 Codex 的退出提示，或从文件菜单选择退出后重试。");
    }

    static InvalidOperationException CannotConfirmExit(Exception error)
    {
        return new InvalidOperationException("无法确认官方 Codex 是否已退出，未启动新会话。请手动退出 Codex 后重试。", error);
    }

    private static List<CodexProcessAdapter> Find(string executable)
    {
        if (FindOverride != null) return FindOverride(executable);
        List<CodexProcessAdapter> matches = new List<CodexProcessAdapter>();
        string expected = Path.GetFullPath(executable);
        Process[] candidates = Process.GetProcesses();
        var retained = new HashSet<Process>();
        try
        {
            foreach (Process process in candidates)
            {
                try
                {
                    string name = process.ProcessName;
                    if (!name.Equals("Codex", StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase)) continue;
                    if (process.HasExited) continue;
                    string actual = ReadImagePath(process.Id);
                    if (String.Equals(Path.GetFullPath(actual), expected, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(new CodexProcessAdapter(process));
                        retained.Add(process);
                    }
                }
                catch (Win32Exception error)
                {
                    if (error.NativeErrorCode == 87 || process.HasExited) continue;
                    throw CannotConfirmExit(error);
                }
                catch (InvalidOperationException error)
                {
                    if (process.HasExited) continue;
                    throw CannotConfirmExit(error);
                }
            }
            return matches;
        }
        catch { retained.Clear(); throw; }
        finally { foreach (Process process in candidates) if (!retained.Contains(process)) process.Dispose(); }
    }

    // Reading the image name requires less access than enumerating modules,
    // which can fail for the client's sandboxed renderer processes.
    internal static string ReadImagePath(int processId)
    {
        IntPtr handle = OpenProcess(0x1000, false, processId);
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var buffer = new StringBuilder(32768);
            int length = buffer.Capacity;
            if (!QueryFullProcessImageName(handle, 0, buffer, ref length))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return buffer.ToString();
        }
        finally { CloseHandle(handle); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
