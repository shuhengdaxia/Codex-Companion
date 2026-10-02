using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

// Applies catalog-validated, data-only CSS to a live Codex renderer.  This
// class deliberately has no access to Codex configuration or credentials.
public static class ThemeRuntime
{
    private const string RuntimeStyleId = "midweb-codexthemes-runtime-style";
    private const string ModernTokenBridgeCss = @"
:root[data-codexthemes-theme] {
  --app-color-background-surface: var(--color-token-main-surface-primary, var(--color-background-surface, #101827)) !important;
  --app-color-background-surface-under: var(--color-token-bg-secondary, var(--color-background-surface-under, #0b1320)) !important;
  --app-color-text-foreground: var(--color-token-foreground, var(--color-text-foreground, #f5f2ea)) !important;
  --app-color-text-foreground-secondary: var(--color-token-text-secondary, var(--color-text-foreground-secondary, #aab8c9)) !important;
  --color-surface: var(--color-token-main-surface-primary, var(--color-background-surface, #101827)) !important;
  --color-surface-primary: var(--color-token-main-surface-primary, var(--color-background-surface, #101827)) !important;
  --color-surface-secondary: var(--color-token-main-surface-primary, var(--color-background-surface, #101827)) !important;
  --color-surface-tertiary: var(--color-token-bg-tertiary, var(--color-background-elevated-primary, #1d2939)) !important;
  --color-text-primary: var(--color-token-foreground, var(--color-text-foreground, #f5f2ea)) !important;
  --color-text-secondary: var(--color-token-text-secondary, var(--color-text-foreground-secondary, #aab8c9)) !important;
  --color-text-tertiary: var(--color-token-text-tertiary, var(--color-text-foreground-tertiary, #718096)) !important;
  --color-icon-primary: var(--color-token-foreground, #f5f2ea) !important;
  --color-icon-secondary: var(--color-token-icon-foreground, #aab8c9) !important;
}
:root[data-codexthemes-theme] body,
:root[data-codexthemes-theme] main.main-surface,
:root[data-codexthemes-theme] main[class*=""MainContentSurface""] {
  color: var(--color-token-foreground, var(--color-text-foreground, #f5f2ea)) !important;
  background-color: var(--color-token-main-surface-primary, var(--color-background-surface, #101827)) !important;
}
";
    // Keep the live payload bounded while allowing the gallery's largest
    // supported inline artwork to reach the renderer.
    public const int MaxCssBytes = 32 * 1024 * 1024;
    private static readonly int[] KnownPorts = new[] { 9335, 9222, 9223, 9224, 9225 };
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    internal static Func<string, string, string, string, CancellationToken, ThemeApplyResult> LaunchWithDebuggingOverride { get; set; }

    public static ThemeApplyResult TryApplyToRunning(string themeId, string css, string mode)
    {
        return TryApplyToRunning(themeId, css, mode, "home");
    }

    public static ThemeApplyResult TryApplyToRunning(string themeId, string css, string mode, string backgroundScope)
    {
        mode = NormalizeMode(mode);
        backgroundScope = NormalizeBackgroundScope(backgroundScope);
        try { Validate(themeId, css, mode, backgroundScope); }
        catch (Exception ex) { return Failure(ex.Message, themeId); }

        var ports = CandidatePorts().ToArray();
        ThemeApplyResult last = null;
        foreach (int port in ports)
        {
            ThemeApplyResult result = TryApplyAtPort(port, themeId, css, mode, backgroundScope);
            if (result.Success)
            {
                StopOwnedHostQuietly();
                return result;
            }
            last = result;
            if (IsPartialMessage(result.Message)) return result;
        }
        if (last != null && !String.IsNullOrEmpty(last.Message) && !IsEndpointFailure(last.Message)) return last;
        return Failure("未检测到调试端口，请退出后启用外观并启动。", themeId);
    }

    public static ThemeApplyResult TryApplyToRunning(int port, string themeId, string css, string mode)
    {
        return TryApplyToRunning(port, themeId, css, mode, "home");
    }

    public static ThemeApplyResult TryApplyToRunning(int port, string themeId, string css, string mode, string backgroundScope)
    {
        mode = NormalizeMode(mode);
        backgroundScope = NormalizeBackgroundScope(backgroundScope);
        try { Validate(themeId, css, mode, backgroundScope); }
        catch (Exception ex) { return Failure(ex.Message, themeId); }
        ThemeApplyResult result = TryApplyAtPort(port, themeId, css, mode, backgroundScope);
        if (result.Success) StopOwnedHostQuietly();
        return result;
    }

    public static ThemeApplyResult RestoreRunning()
    {
        ThemeApplyResult last = null;
        foreach (int port in CandidatePorts())
        {
            ThemeApplyResult result = RestoreAtPort(port);
            if (result.Success)
            {
                StopOwnedHostQuietly();
                return result;
            }
            last = result;
            if (IsPartialMessage(result.Message)) return result;
        }
        if (last != null && !IsEndpointFailure(last.Message)) return last;
        return Failure("未发现可调试的 Codex 页面，无法恢复当前会话。请通过本工具的“启用外观”启动入口打开 Codex后重试。", "");
    }

    public static ThemeApplyResult RestoreRunning(int port)
    {
        ThemeApplyResult result = RestoreAtPort(port);
        if (result.Success) StopOwnedHostQuietly();
        return result;
    }

    // The caller must have closed Codex first.  This method starts a fresh
    // instance with a loopback debugging endpoint and leaves it running even
    // if the endpoint never becomes available.
    public static ThemeApplyResult LaunchWithDebugging(string themeId, string css, string mode)
    {
        return LaunchWithDebugging(themeId, css, mode, "home");
    }

    public static ThemeApplyResult LaunchWithDebugging(string themeId, string css, string mode, string backgroundScope)
    {
        return LaunchWithDebugging(themeId, css, mode, backgroundScope, CancellationToken.None);
    }

    public static ThemeApplyResult RestartWithDebugging(string themeId, string css, string mode, string backgroundScope, CancellationToken cancellationToken)
    {
        mode = NormalizeMode(mode);
        backgroundScope = NormalizeBackgroundScope(backgroundScope);
        try { Validate(themeId, css, mode, backgroundScope); cancellationToken.ThrowIfCancellationRequested(); }
        catch (OperationCanceledException) { return Failure("已取消重启，未关闭或启动 Codex。", themeId); }
        catch (Exception ex) { return Failure(ex.Message, themeId); }

        try
        {
            CodexDesktopRestart.CloseAndWait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (LaunchWithDebuggingOverride != null)
                return LaunchWithDebuggingOverride(themeId, css, mode, backgroundScope, cancellationToken);
            return LaunchWithDebugging(themeId, css, mode, backgroundScope, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Failure("已取消重启，未启动新的 Codex 会话。", themeId);
        }
        catch (Exception ex)
        {
            return Failure(ex.Message, themeId);
        }
    }

    private static ThemeApplyResult LaunchWithDebugging(string themeId, string css, string mode, string backgroundScope, CancellationToken cancellationToken)
    {
        mode = NormalizeMode(mode);
        backgroundScope = NormalizeBackgroundScope(backgroundScope);
        try { Validate(themeId, css, mode, backgroundScope); }
        catch (Exception ex) { return Failure(ex.Message, themeId); }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            CodexDesktopRestart.RequireClosed();
        }
        catch
        {
            return Failure("Codex 当前仍在运行，未启动新会话。请先完全退出 Codex，再点击“启用外观”。", themeId);
        }

        Process codex = null;
        int port = SkinBridge.PickLoopbackPortForRuntime();
        try
        {
            string executable = CodexInstall.Desktop();
            StopOwnedHostQuietly();
            codex = CodexInstall.StartDesktop(executable,
                "--remote-debugging-address=127.0.0.1 --remote-debugging-port=" + port.ToString());
            if (codex == null) return Failure("Codex 启动失败，未能启用调试端口。", themeId);

            DateTime deadline = DateTime.UtcNow.AddSeconds(25);
            string lastError = "";
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThemeApplyResult result = TryApplyAtPort(port, themeId, css, mode, backgroundScope);
                if (result.Success) return result;
                if (!String.IsNullOrEmpty(result.Message)) lastError = result.Message;
                try { if (codex.HasExited) break; } catch { }
                Thread.Sleep(300);
            }
            return Failure(String.IsNullOrEmpty(lastError)
                ? "Codex 已启动，但未开放可调试的页面；该版本可能忽略远程调试参数。"
                : "Codex 已启动，但主题未完成应用：" + lastError, themeId);
        }
        catch (Exception ex)
        {
            return Failure("无法启用 Codex 调试启动：" + ex.Message, themeId);
        }
    }

    private static ThemeApplyResult TryApplyAtPort(int port, string themeId, string css, string mode, string backgroundScope)
    {
        int matched;
        int completed;
        string failure;
        try
        {
            bool success = SkinBridge.ExecuteRuntimeOnce(port,
                BuildInstallExpression(themeId, css, mode, backgroundScope),
                BuildVerifyExpression(themeId, mode, backgroundScope),
                "", BuildRemoveVerifyExpression(), out matched, out completed, out failure);
            if (success)
                return new ThemeApplyResult { Success = true, Message = "主题已应用到当前 Codex 会话；SPA 页面导航会继续保留。完全退出或重新加载页面后需要重新应用。", ThemeId = themeId };
            if (matched > 0)
                return Failure("主题只应用到 " + completed.ToString() + "/" + matched.ToString() + " 个 Codex 页面，未报告成功。" +
                    (String.IsNullOrEmpty(failure) ? "请重试。" : "原因：" + failure), themeId, completed);
            return Failure("该调试端口没有可识别的 Codex 页面。", themeId);
        }
        catch (Exception ex)
        {
            if (IsPortUnavailable(ex)) return Failure("未检测到调试端口，请退出后启用外观并启动。", themeId);
            return Failure(Clean(ex.Message), themeId);
        }
    }

    private static ThemeApplyResult RestoreAtPort(int port)
    {
        int matched;
        int completed;
        string failure;
        try
        {
            bool success = SkinBridge.ExecuteRuntimeOnce(port, "", "", BuildRemoveExpression(),
                BuildRemoveVerifyExpression(), out matched, out completed, out failure);
            if (success) return new ThemeApplyResult { Success = true, Message = "已移除本工具主题并恢复此前的页面根标识。", ThemeId = "" };
            if (matched > 0)
                return Failure("只恢复了 " + completed.ToString() + "/" + matched.ToString() + " 个 Codex 页面，未报告成功。" +
                    (String.IsNullOrEmpty(failure) ? "请重试。" : "原因：" + failure), "", completed);
            return Failure("该调试端口没有可识别的 Codex 页面。", "");
        }
        catch (Exception ex)
        {
            if (IsPortUnavailable(ex)) return Failure("未检测到调试端口，请退出后启用外观并启动。", "");
            return Failure(Clean(ex.Message), "");
        }
    }

    private static IEnumerable<int> CandidatePorts()
    {
        var ports = new List<int>();
        int recorded = ReadRecordedPort();
        if (recorded >= 1024 && recorded <= 65535) ports.Add(recorded);
        ports.AddRange(OwnedDesktopListenerPorts());
        ports.AddRange(KnownPorts);
        return ports.Distinct().Where(p => p >= 1024 && p <= 65535);
    }

    private static IEnumerable<int> OwnedDesktopListenerPorts()
    {
        var result = new List<int>();
        string expected;
        try { expected = Path.GetFullPath(CodexInstall.Desktop()); }
        catch { return result; }

        var ownedPids = new HashSet<int>();
        foreach (string name in new[] { "ChatGPT", "Codex" })
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); }
            catch { continue; }
            foreach (Process process in processes)
            {
                using (process)
                {
                    try
                    {
                        if (String.Equals(Path.GetFullPath(process.MainModule.FileName), expected, StringComparison.OrdinalIgnoreCase))
                            ownedPids.Add(process.Id);
                    }
                    catch { }
                }
            }
        }
        if (ownedPids.Count == 0) return result;

        try
        {
            using (var netstat = Process.Start(new ProcessStartInfo("netstat.exe", "-ano -p tcp")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = false
            }))
            {
                if (netstat == null) return result;
                string output = netstat.StandardOutput.ReadToEnd();
                if (!netstat.WaitForExit(5000))
                {
                    try { netstat.Kill(); } catch { }
                    return result;
                }
                foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] fields = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length < 5 || !String.Equals(fields[0], "TCP", StringComparison.OrdinalIgnoreCase) ||
                        !String.Equals(fields[3], "LISTENING", StringComparison.OrdinalIgnoreCase)) continue;
                    int pid;
                    if (!Int32.TryParse(fields[4], out pid) || !ownedPids.Contains(pid)) continue;
                    int separator = fields[1].LastIndexOf(':');
                    int port;
                    if (separator < 0 || !Int32.TryParse(fields[1].Substring(separator + 1), out port)) continue;
                    if (port >= 1024 && port <= 65535) result.Add(port);
                }
            }
        }
        catch { }
        return result;
    }

    private static int ReadRecordedPort()
    {
        try
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MidWebCodex");
            string path = Path.Combine(root, "skin-session.json");
            if (!File.Exists(path)) return 0;
            var map = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path, Utf8)) as Dictionary<string, object>;
            object value;
            if (map != null && map.TryGetValue("Port", out value)) return Convert.ToInt32(value);
            if (map != null && map.TryGetValue("port", out value)) return Convert.ToInt32(value);
        }
        catch { }
        return 0;
    }

    private static void StopOwnedHostQuietly()
    {
        try { SkinBridge.StopOwnedHostForRuntime(); } catch { }
    }

    private static void Validate(string themeId, string css, string mode, string backgroundScope)
    {
        if (String.IsNullOrWhiteSpace(themeId) || themeId.Length > 128 || themeId.Any(Char.IsControl))
            throw new ArgumentException("主题 ID 无效。");
        if (String.IsNullOrEmpty(css) || css.Length > MaxCssBytes || Utf8.GetByteCount(css) > MaxCssBytes || css.IndexOf('\0') >= 0)
            throw new ArgumentException("主题 CSS 为空或超过运行时限制。");
        if (String.IsNullOrWhiteSpace(mode) || mode.Length > 80 || mode.Any(Char.IsControl))
            throw new ArgumentException("主题模式无效。");
        if (backgroundScope != "home" && backgroundScope != "workspace")
            throw new ArgumentException("主题背景范围无效。");
    }

    private static string NormalizeMode(string mode)
    {
        if (String.IsNullOrWhiteSpace(mode)) return "mixed";
        string value = mode.Trim().ToLowerInvariant();
        return value == "light" || value == "dark" || value == "mixed" ? value : "mixed";
    }

    private static string NormalizeBackgroundScope(string backgroundScope)
    {
        return String.Equals(backgroundScope, "workspace", StringComparison.OrdinalIgnoreCase) ? "workspace" : "home";
    }

    private static string BuildInstallExpression(string themeId, string css, string mode, string backgroundScope)
    {
        if (!String.IsNullOrEmpty(css) && css[0] == '\uFEFF') css = css.Substring(1);
        string runtimeCss = css + "\n" + ModernTokenBridgeCss;
        string encoded = Convert.ToBase64String(Utf8.GetBytes(runtimeCss));
        string dreamInstall = DreamThemeRuntime.BuildInstallScript(themeId);
        string dreamClassifyHook = DreamThemeRuntime.BuildClassifyHook();
        return @"(() => {
  const config = { themeId: " + Quote(themeId) + @", mode: " + Quote(mode) + @", backgroundScope: " + Quote(backgroundScope) + @", css: decodeURIComponent(escape(atob(" + Quote(encoded) + @"))) };
  const stateKey = '__midwebThemeRuntime';
  const styleId = " + Quote(RuntimeStyleId) + @";
  const root = document.documentElement;
  const previous = globalThis[stateKey];
  if (previous && previous.dreamCleanup) previous.dreamCleanup();
  const objectUrls = [];
  const objectUrlsByData = Object.create(null);
  const imageDataPattern = /url\(\s*[""']data:(image\/(?:png|jpeg|gif|webp));base64,([A-Za-z0-9+/=]+)[""']\s*\)/gi;
  const revokeObjectUrls = (urls) => {
    if (!urls) return;
    urls.forEach((url) => { try { URL.revokeObjectURL(url); } catch (_) {} });
  };
  const materializeImageData = (source) => source.replace(imageDataPattern, (match, mime, encoded) => {
    const key = mime.toLowerCase() + ';' + encoded;
    let objectUrl = objectUrlsByData[key];
    if (!objectUrl) {
      const raw = atob(encoded);
      const bytes = new Uint8Array(raw.length);
      for (let index = 0; index < raw.length; index++) bytes[index] = raw.charCodeAt(index);
      objectUrl = URL.createObjectURL(new Blob([bytes], { type: mime.toLowerCase() }));
      objectUrlsByData[key] = objectUrl;
      objectUrls.push(objectUrl);
    }
    return 'url(""' + objectUrl + '"")';
  });
  let materializedCss;
  try { materializedCss = materializeImageData(config.css); }
  catch (error) { revokeObjectUrls(objectUrls); throw error; }
  if (previous && previous.observer) previous.observer.disconnect();
  if (previous && previous.frame) cancelAnimationFrame(previous.frame);
  if (previous && previous.objectUrls) revokeObjectUrls(previous.objectUrls);
  const state = previous && previous.saved ? previous : { saved: {
    codexTheme: root.getAttribute('data-codex-theme'),
    codexthemesTheme: root.getAttribute('data-codexthemes-theme'),
    codexthemesScope: root.getAttribute('data-codexthemes-background-scope'),
    codexMode: root.getAttribute('data-codex-theme-mode'),
    darkClass: root.classList.contains('dark')
  } };
  const oldStyle = document.getElementById(styleId);
  if (oldStyle && oldStyle.getAttribute('data-owner') === 'midweb') oldStyle.remove();
  const releasePage = (node) => {
    node.removeAttribute('data-codexthemes-page');
    if (node.getAttribute('data-midweb-theme-class') === 'owned') {
      node.classList.remove('main-surface');
      node.removeAttribute('data-midweb-theme-class');
    }
    if (node.getAttribute('data-midweb-theme-main-surface') === 'owned') {
      node.removeAttribute('data-app-shell-main-surface');
      node.removeAttribute('data-midweb-theme-main-surface');
    }
    node.removeAttribute('data-midweb-theme-page');
  };
  document.querySelectorAll('[data-midweb-theme-page=\'owned\']').forEach(releasePage);
  if (config.mode === 'dark') {
    if (!root.classList.contains('dark')) {
      root.classList.add('dark');
      state.darkClassOwned = true;
    }
  } else if (config.mode === 'light' || state.darkClassOwned) {
    root.classList.remove('dark');
    state.darkClassOwned = false;
  }
  root.setAttribute('data-codex-theme', config.themeId);
  root.setAttribute('data-codex-theme-mode', config.mode);
  root.setAttribute('data-codexthemes-theme', config.themeId);
  root.setAttribute('data-codexthemes-background-scope', config.backgroundScope);
  root.setAttribute('data-midweb-theme-active', 'active');
  root.setAttribute('data-midweb-theme-id', config.themeId);
  const style = document.createElement('style');
  style.id = styleId;
  style.setAttribute('data-owner', 'midweb');
  style.setAttribute('data-midweb-theme-id', config.themeId);
  state.objectUrls = objectUrls;
  style.textContent = materializedCss;
  (document.head || root).appendChild(style);
  const classify = () => {
    const shell = document.querySelector('aside.app-shell-left-panel');
    const candidates = Array.from(document.querySelectorAll('main.main-surface, main[class*=""MainContentSurface""]'));
    const main = candidates.find((node) => node.matches('main[class*=""MainContentSurface""]')) || candidates[0];
    if (!shell || !main) {
      if (state.dreamClassify) state.dreamClassify(null);
      return;
    }
    document.querySelectorAll('main[data-midweb-theme-page=\'owned\']').forEach((node) => {
      if (node !== main) releasePage(node);
    });
    if (!main.classList.contains('main-surface')) {
      main.classList.add('main-surface');
      main.setAttribute('data-midweb-theme-class', 'owned');
    }
    if (!main.hasAttribute('data-app-shell-main-surface')) {
      main.setAttribute('data-app-shell-main-surface', '');
      main.setAttribute('data-midweb-theme-main-surface', 'owned');
    }
    const hasConversation = Boolean(main.querySelector('[data-thread-user-message-navigation-item-id]'));
    const hasComposer = Boolean(main.querySelector('[data-composer-navigation-target], .composer-surface-chrome'));
    main.setAttribute('data-codexthemes-page', hasConversation ? 'conversation' : hasComposer ? 'home' : 'system');
    main.setAttribute('data-midweb-theme-page', 'owned');
" + dreamClassifyHook + @"  };
  state.frame = 0;
  state.observer = new MutationObserver(() => {
    if (state.frame) return;
    state.frame = requestAnimationFrame(() => { state.frame = 0; classify(); });
  });
  state.observer.observe(root, { childList: true, subtree: true });
  globalThis[stateKey] = state;
" + dreamInstall + @"
  classify();
  return true;
})()";
    }

    private static string BuildVerifyExpression(string themeId, string mode, string backgroundScope)
    {
        return @"(() => {
  const root = document.documentElement;
  const style = document.getElementById(" + Quote(RuntimeStyleId) + @");
  return Boolean(style && style.getAttribute('data-owner') === 'midweb' &&
    root.getAttribute('data-midweb-theme-active') === 'active' &&
    root.getAttribute('data-midweb-theme-id') === " + Quote(themeId) + @" &&
    root.getAttribute('data-codex-theme') === " + Quote(themeId) + @" &&
    root.getAttribute('data-codex-theme-mode') === " + Quote(mode) + @" &&
    root.getAttribute('data-codexthemes-background-scope') === " + Quote(backgroundScope) + @" &&
    root.getAttribute('data-codexthemes-theme') === " + Quote(themeId) + @");
})()";
    }

    private static string BuildRemoveExpression()
    {
        return @"(() => {
  const stateKey = '__midwebThemeRuntime';
  const state = globalThis[stateKey];
  const root = document.documentElement;
" + DreamThemeRuntime.BuildCleanupCall() + @"
  if (state && state.observer) state.observer.disconnect();
  if (state && state.frame) cancelAnimationFrame(state.frame);
  const style = document.getElementById(" + Quote(RuntimeStyleId) + @");
  if (style && style.getAttribute('data-owner') === 'midweb') style.remove();
  if (state && state.objectUrls) state.objectUrls.forEach((url) => { try { URL.revokeObjectURL(url); } catch (_) {} });
  document.querySelectorAll('[data-midweb-theme-page=\'owned\']').forEach((node) => {
    node.removeAttribute('data-codexthemes-page');
    if (node.getAttribute('data-midweb-theme-class') === 'owned') {
      node.classList.remove('main-surface');
      node.removeAttribute('data-midweb-theme-class');
    }
    if (node.getAttribute('data-midweb-theme-main-surface') === 'owned') {
      node.removeAttribute('data-app-shell-main-surface');
      node.removeAttribute('data-midweb-theme-main-surface');
    }
    node.removeAttribute('data-midweb-theme-page');
  });
  if (root.getAttribute('data-midweb-theme-active') === 'active') {
    const saved = state && state.saved ? state.saved : {};
    const restore = (name, value) => { if (value === null || value === undefined) root.removeAttribute(name); else root.setAttribute(name, value); };
    restore('data-codex-theme', saved.codexTheme);
    restore('data-codex-theme-mode', saved.codexMode);
    restore('data-codexthemes-theme', saved.codexthemesTheme);
    restore('data-codexthemes-background-scope', saved.codexthemesScope);
    if (state && state.saved) {
      if (saved.darkClass) root.classList.add('dark'); else if (state.darkClassOwned) root.classList.remove('dark');
    }
    root.removeAttribute('data-midweb-theme-active');
    root.removeAttribute('data-midweb-theme-id');
  }
  delete globalThis[stateKey];
  return true;
})()";
    }

    private static string BuildRemoveVerifyExpression()
    {
        return @"(() => {
  const style = document.getElementById(" + Quote(RuntimeStyleId) + @");
  const root = document.documentElement;
  return Boolean((!style || style.getAttribute('data-owner') !== 'midweb') &&
    !root.hasAttribute('data-midweb-theme-active') && !root.hasAttribute('data-midweb-theme-id'));
})()";
    }

    private static string Quote(string value)
    {
        return new JavaScriptSerializer { MaxJsonLength = SkinBridge.MaxCdpMessageBytes }.Serialize(value ?? "");
    }

    private static ThemeApplyResult Failure(string message, string themeId, int pages = 0)
    {
        return new ThemeApplyResult { Success = false, Message = Clean(message), ThemeId = themeId };
    }

    private static string Clean(string message)
    {
        if (String.IsNullOrEmpty(message)) return "";
        string clean = message.Replace('\r', ' ').Replace('\n', ' ');
        return clean.Length > 500 ? clean.Substring(0, 500) : clean;
    }

    private static bool IsEndpointFailure(string message)
    {
        return String.IsNullOrEmpty(message) || message.IndexOf("连接", StringComparison.OrdinalIgnoreCase) >= 0 ||
            message.IndexOf("拒绝", StringComparison.OrdinalIgnoreCase) >= 0 ||
            message.IndexOf("endpoint", StringComparison.OrdinalIgnoreCase) >= 0 ||
            message.IndexOf("调试端口", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsPortUnavailable(Exception error)
    {
        return error is TaskCanceledException || error is HttpRequestException || error is WebException;
    }

    private static bool IsPartialMessage(string message)
    {
        return !String.IsNullOrEmpty(message) &&
            (message.IndexOf("只应用到", StringComparison.OrdinalIgnoreCase) >= 0 ||
             message.IndexOf("只恢复了", StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
