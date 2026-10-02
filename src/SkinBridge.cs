using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

public static class SkinBridge
{
    private const string SessionFileName = "skin-session.json";
    private const string ReadyFileName = "skin-ready.json";
    private const string ErrorFileName = "skin-error-state.json";
    private const string StyleId = "midweb-qq-skin-style";
    private const string LoopbackHost = "127.0.0.1";
    internal const int MaxCdpMessageBytes = 64 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

    public static void Start(string codexExe, string appExe, string dataDirectory)
    {
        if (String.IsNullOrWhiteSpace(codexExe) || !File.Exists(codexExe)) throw new FileNotFoundException("Codex 程序不存在。", codexExe);
        if (String.IsNullOrWhiteSpace(appExe) || !File.Exists(appExe)) throw new FileNotFoundException("皮肤宿主程序不存在。", appExe);
        string root = PrepareDataDirectory(dataDirectory);
        string sessionPath = Path.Combine(root, SessionFileName);
        Stop(root);
        DeleteQuietly(Path.Combine(root, ReadyFileName));
        DeleteQuietly(Path.Combine(root, ErrorFileName));

        int port = PickLoopbackPort();
        Process codexProcess = null;
        Process hostProcess = null;
        try
        {
            codexProcess = CodexInstall.StartDesktop(codexExe,
                "--remote-debugging-address=127.0.0.1 --remote-debugging-port=" + port.ToString());
            if (codexProcess == null) throw new InvalidOperationException("Codex 启动失败。");

            WriteSession(sessionPath, new SessionState
            {
                Port = port,
                CodexPid = codexProcess.Id,
                HostPid = 0,
                AppExe = Path.GetFullPath(appExe),
                CodexExe = Path.GetFullPath(codexExe),
                CreatedAtUtc = DateTime.UtcNow.ToString("o")
            });

            DateTime handshakeStartedAtUtc = DateTime.UtcNow;
            hostProcess = Process.Start(new ProcessStartInfo(appExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = root,
                Arguments = "--skin-host " + QuoteArgument(root)
            });
            if (hostProcess == null) throw new InvalidOperationException("皮肤宿主启动失败。");

            string hostStartedAtUtc = "";
            try { hostStartedAtUtc = hostProcess.StartTime.ToUniversalTime().ToString("o"); } catch { }
            WriteSession(sessionPath, new SessionState
            {
                Port = port,
                CodexPid = codexProcess.Id,
                HostPid = hostProcess.Id,
                HostStartedAtUtc = hostStartedAtUtc,
                AppExe = Path.GetFullPath(appExe),
                CodexExe = Path.GetFullPath(codexExe),
                CreatedAtUtc = DateTime.UtcNow.ToString("o")
            });
            WaitForReady(root, hostProcess, codexProcess, handshakeStartedAtUtc);
        }
        catch
        {
            if (hostProcess != null) TryKill(hostProcess);
            throw;
        }
    }

    public static int RunHost(string dataDirectory)
    {
        try
        {
            string root = PrepareDataDirectory(dataDirectory);
            SessionState state = ReadSession(Path.Combine(root, SessionFileName));
            if (state == null || state.Port < 1024 || state.Port > 65535 || state.CodexPid <= 0)
                throw new InvalidDataException("皮肤会话无效。");

            string css = LoadCss();
            Process codexProcess = Process.GetProcessById(state.CodexPid);
            DateTime lastError = DateTime.MinValue;
            string lastStartupError = "";
            bool injected = false;
            DateTime started = DateTime.UtcNow;
            bool startupErrorWritten = false;

            while (!codexProcess.HasExited)
            {
                try
                {
                    bool changed = ScanAndInjectOnce(state.Port, css, false);
                    if (changed && !injected)
                    {
                        injected = true;
                        WriteStatus(Path.Combine(root, ReadyFileName), "ready", "QQ 皮肤已注入并验证。");
                    }
                }
                catch (Exception ex)
                {
                    lastStartupError = CleanError(ex.Message);
                    if ((DateTime.UtcNow - lastError).TotalSeconds >= 5)
                    {
                        AppendLog(root, "inject: " + CleanError(ex.Message));
                        lastError = DateTime.UtcNow;
                    }
                }
                if (!injected && !startupErrorWritten && (DateTime.UtcNow - started).TotalSeconds >= 20)
                {
                    startupErrorWritten = true;
                    WriteStatus(Path.Combine(root, ErrorFileName), "timeout",
                        String.IsNullOrEmpty(lastStartupError) ? "20 秒内未找到可注入的 Codex 页面。" : lastStartupError);
                }
                Thread.Sleep(injected ? 2500 : 700);
            }
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                string root = PrepareDataDirectory(dataDirectory);
                string message = CleanError(ex.Message);
                AppendLog(root, "host: " + message);
                WriteStatus(Path.Combine(root, ErrorFileName), "error", message);
            }
            catch { }
            return 1;
        }
    }

    public static void Stop(string dataDirectory)
    {
        string root = PrepareDataDirectory(dataDirectory);
        string sessionPath = Path.Combine(root, SessionFileName);
        SessionState state = ReadSession(sessionPath);
        if (state != null && state.HostPid > 0)
        {
            try
            {
                Process host = Process.GetProcessById(state.HostPid);
                if (IsRecordedHost(host, state)) TryKill(host);
            }
            catch { }
        }
        if (state != null && state.Port >= 1024 && state.Port <= 65535)
        {
            try { ScanAndInjectOnce(state.Port, "", true); }
            catch (Exception ex) { AppendLog(root, "remove: " + CleanError(ex.Message)); }
        }
        try { if (File.Exists(sessionPath)) File.Delete(sessionPath); } catch { }
        DeleteQuietly(Path.Combine(root, ReadyFileName));
        DeleteQuietly(Path.Combine(root, ErrorFileName));
    }

    private static bool ScanAndInjectOnce(int port, string css, bool removeOnly)
    {
        List<TargetInfo> targets = ListTargets(port);
        bool ok = false;
        foreach (TargetInfo target in targets)
        {
            if (!IsSafeTarget(target, port)) continue;
            using (var session = new CdpSession(target.WebSocketDebuggerUrl))
            {
                session.Open();
                if (!ProbeCodexRenderer(session, target.Url)) continue;
                if (removeOnly)
                {
                    session.Evaluate(BuildRemoveExpression());
                    ok = true;
                    continue;
                }
                string script = BuildInstallScript(css);
                session.Evaluate(script);
                object verified = session.Evaluate("Boolean(document.getElementById(" + JsonQuote(StyleId) + "))");
                if (verified is bool && (bool)verified) ok = true;
                else throw new InvalidOperationException("CSS 注入后未通过验证。");
            }
        }
        return ok;
    }

    // ThemeRuntime uses the same loopback target validation and CDP session as
    // the QQ skin, but supplies its own data-only expressions.  Every matching
    // renderer must complete before the operation is reported as successful.
    internal static bool ExecuteRuntimeOnce(int port, string installExpression, string verifyExpression,
        string removeExpression, string removeVerifyExpression, out int matchedTargets, out int completedTargets,
        out string failure)
    {
        matchedTargets = 0;
        completedTargets = 0;
        failure = "";
        List<TargetInfo> targets = ListTargets(port);
        foreach (TargetInfo target in targets)
        {
            if (!IsSafeTarget(target, port)) continue;
            try
            {
                matchedTargets++;
                using (var session = new CdpSession(target.WebSocketDebuggerUrl))
                {
                    session.Open();
                    if (!ProbeCodexRenderer(session, target.Url))
                    {
                        matchedTargets--;
                        continue;
                    }
                    string expression = String.IsNullOrEmpty(removeExpression) ? installExpression : removeExpression;
                    session.Evaluate(expression);
                    object verified = session.Evaluate(String.IsNullOrEmpty(removeExpression) ? verifyExpression : removeVerifyExpression);
                    if (!(verified is bool) || !(bool)verified)
                        throw new InvalidOperationException("主题运行时注入后未通过验证。");
                    completedTargets++;
                }
            }
            catch (Exception ex)
            {
                failure = CleanError(ex.Message);
            }
        }
        return matchedTargets > 0 && completedTargets == matchedTargets;
    }

    internal static int PickLoopbackPortForRuntime()
    {
        return PickLoopbackPort();
    }

    // A previously launched QQ host is ours, so it is safe to stop that host
    // before a user-selected theme is applied.  This leaves Codex running.
    internal static void StopOwnedHostForRuntime()
    {
        Stop(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MidWebCodex"));
    }

    private static List<TargetInfo> ListTargets(int port)
    {
        using (var handler = new HttpClientHandler { AllowAutoRedirect = false, Proxy = null, UseProxy = false })
        using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2), MaxResponseContentBufferSize = 1024 * 1024 })
        {
            string body = client.GetStringAsync("http://127.0.0.1:" + port.ToString() + "/json/list").GetAwaiter().GetResult();
            object raw = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 }.DeserializeObject(body);
            object[] array = raw as object[];
            if (array == null) throw new InvalidDataException("CDP target 列表格式无效。");
            var result = new List<TargetInfo>();
            foreach (object item in array)
            {
                var map = item as Dictionary<string, object>;
                if (map == null) continue;
                result.Add(new TargetInfo
                {
                    Id = Text(map, "id"),
                    Type = Text(map, "type"),
                    Url = Text(map, "url"),
                    WebSocketDebuggerUrl = Text(map, "webSocketDebuggerUrl")
                });
            }
            return result;
        }
    }

    private static bool IsSafeTarget(TargetInfo target, int port)
    {
        if (target == null || target.Type != "page" || String.IsNullOrEmpty(target.Id) || String.IsNullOrEmpty(target.WebSocketDebuggerUrl)) return false;
        if (!IsCodexLocalUrl(target.Url)) return false;
        Uri ws;
        if (!Uri.TryCreate(target.WebSocketDebuggerUrl, UriKind.Absolute, out ws)) return false;
        if (ws.Scheme != "ws" || ws.Host != LoopbackHost || ws.Port != port || !String.IsNullOrEmpty(ws.Query) || !String.IsNullOrEmpty(ws.Fragment)) return false;
        string expected = "/devtools/page/" + target.Id;
        return String.Equals(ws.AbsolutePath, expected, StringComparison.Ordinal);
    }

    private static bool IsCodexLocalUrl(string value)
    {
        if (String.IsNullOrWhiteSpace(value)) return false;
        Uri uri;
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri)) return false;
        // The live Codex desktop renderer uses the app://-/ origin.  Do not
        // treat an arbitrary app:// page as ours: another Electron desktop
        // app may expose the same loopback debugging endpoint.
        if (String.Equals(uri.Scheme, "app", StringComparison.OrdinalIgnoreCase))
            return String.Equals(uri.Host, "-", StringComparison.Ordinal) &&
                   String.Equals(uri.AbsolutePath, "/index.html", StringComparison.OrdinalIgnoreCase);
        if (!String.Equals(uri.Scheme, "file", StringComparison.OrdinalIgnoreCase) || !uri.IsFile) return false;
        string path = uri.LocalPath.Replace('\\', '/');
        return path.IndexOf("/webview/index.html", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool ProbeCodexRenderer(CdpSession session, string targetUrl)
    {
        if (!IsCodexLocalUrl(targetUrl)) return false;
        object value = session.Evaluate(@"(() => {
  const markers = {
    shell: Boolean(document.querySelector('main.main-surface')),
    sidebar: Boolean(document.querySelector('aside.app-shell-left-panel')),
    composer: Boolean(document.querySelector('.composer-surface-chrome')),
    main: Boolean(document.querySelector('[role=""main""]')),
    currentShell: Boolean(document.querySelector('aside.app-shell-left-panel')) &&
      Boolean(document.querySelector('main'))
  };
  return Boolean(markers.currentShell || (markers.shell && markers.sidebar) || (markers.main && markers.composer));
})()");
        return value is bool && (bool)value;
    }

    private static string BuildInstallScript(string css)
    {
        string encoded = Convert.ToBase64String(Utf8.GetBytes(css));
        return @"(() => {
  const id = " + JsonQuote(StyleId) + @";
  const css = decodeURIComponent(escape(atob(" + JsonQuote(encoded) + @")));
  document.documentElement.classList.add('midweb-qq-skin');
  document.documentElement.setAttribute('data-midweb-qq-skin', 'active');
  let style = document.getElementById(id);
  if (!style) {
    style = document.createElement('style');
    style.id = id;
    style.setAttribute('data-owner', 'midweb');
    (document.head || document.documentElement).appendChild(style);
  }
  if (style.textContent !== css) style.textContent = css;
  return true;
})()";
    }

    private static string BuildRemoveExpression()
    {
        return @"(() => {
  const node = document.getElementById(" + JsonQuote(StyleId) + @");
  if (node) node.remove();
  document.documentElement.classList.remove('midweb-qq-skin');
  document.documentElement.removeAttribute('data-midweb-qq-skin');
  return true;
})()";
    }

    private static void WaitForReady(string root, Process hostProcess, Process codexProcess, DateTime startedAtUtc)
    {
        string readyPath = Path.Combine(root, ReadyFileName);
        string errorPath = Path.Combine(root, ErrorFileName);
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(readyPath))
            {
                StatusState ready = ReadStatus(readyPath);
                DateTime readyCreatedAt;
                if (ready != null &&
                    String.Equals(ready.Status, "ready", StringComparison.OrdinalIgnoreCase) &&
                    DateTime.TryParse(ready.CreatedAtUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out readyCreatedAt) &&
                    readyCreatedAt.ToUniversalTime() >= startedAtUtc)
                    return;
            }
            if (File.Exists(errorPath))
            {
                StatusState error = ReadStatus(errorPath);
                throw new InvalidOperationException("QQ 皮肤启动失败：" + CleanError(error == null ? "未知错误。" : error.Message));
            }
            try { if (hostProcess.HasExited) throw new InvalidOperationException("QQ 皮肤宿主已退出，未完成注入。"); } catch (InvalidOperationException) { throw; } catch { }
            try { if (codexProcess.HasExited) throw new InvalidOperationException("Codex 已退出，QQ 皮肤未完成注入。"); } catch (InvalidOperationException) { throw; } catch { }
            Thread.Sleep(200);
        }
        throw new TimeoutException("QQ 皮肤启动超时：20 秒内未完成 CSS 注入验证。");
    }

    private static string LoadCss()
    {
        Assembly asm = Assembly.GetExecutingAssembly();
        foreach (string name in asm.GetManifestResourceNames())
        {
            if (name.EndsWith("qq-skin.css", StringComparison.OrdinalIgnoreCase))
            {
                using (Stream stream = asm.GetManifestResourceStream(name))
                using (var reader = new StreamReader(stream, Utf8))
                    return reader.ReadToEnd();
            }
        }
        string fallback = Path.Combine(Path.GetDirectoryName(asm.Location) ?? "", "assets", "qq-skin.css");
        if (File.Exists(fallback)) return File.ReadAllText(fallback, Utf8);
        fallback = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(asm.Location) ?? "", "..", "assets", "qq-skin.css"));
        if (File.Exists(fallback)) return File.ReadAllText(fallback, Utf8);
        throw new InvalidDataException("程序资源缺失：qq-skin.css");
    }

    private static int PickLoopbackPort()
    {
        TcpListener listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally { if (listener != null) listener.Stop(); }
    }

    private static string PrepareDataDirectory(string dataDirectory)
    {
        if (String.IsNullOrWhiteSpace(dataDirectory)) throw new ArgumentException("数据目录不能为空。", "dataDirectory");
        string root = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteSession(string path, SessionState state)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, new JavaScriptSerializer().Serialize(state), Utf8);
        if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
    }

    private static SessionState ReadSession(string path)
    {
        if (!File.Exists(path)) return null;
        try { return new JavaScriptSerializer().Deserialize<SessionState>(File.ReadAllText(path, Utf8)); }
        catch { return null; }
    }

    private static bool IsRecordedHost(Process process, SessionState state)
    {
        if (process == null || process.HasExited || String.IsNullOrEmpty(state.AppExe) || String.IsNullOrEmpty(state.HostStartedAtUtc)) return false;
        try
        {
            string file = process.MainModule.FileName;
            string startedAt = process.StartTime.ToUniversalTime().ToString("o");
            return String.Equals(Path.GetFullPath(file), Path.GetFullPath(state.AppExe), StringComparison.OrdinalIgnoreCase) &&
                String.Equals(startedAt, state.HostStartedAtUtc, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static void WriteStatus(string path, string status, string message)
    {
        var state = new StatusState { Status = status, Message = CleanError(message), CreatedAtUtc = DateTime.UtcNow.ToString("o") };
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, new JavaScriptSerializer().Serialize(state), Utf8);
        if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
    }

    private static StatusState ReadStatus(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return new JavaScriptSerializer().Deserialize<StatusState>(File.ReadAllText(path, Utf8));
        }
        catch { return null; }
    }

    private static void DeleteQuietly(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (process != null && !process.HasExited)
            {
                process.Kill();
                process.WaitForExit(5000);
            }
        }
        catch { }
    }

    private static void AppendLog(string root, string message)
    {
        Directory.CreateDirectory(root);
        File.AppendAllText(Path.Combine(root, "skin-error.log"), DateTime.UtcNow.ToString("o") + " " + CleanError(message) + Environment.NewLine, Utf8);
    }

    private static string CleanError(string value)
    {
        if (String.IsNullOrEmpty(value)) return "";
        string s = value.Replace('\r', ' ').Replace('\n', ' ');
        int q = s.IndexOf('?');
        if (q >= 0) s = s.Substring(0, q) + "?...";
        return s.Length > 500 ? s.Substring(0, 500) : s;
    }

    private static string Text(Dictionary<string, object> map, string key)
    {
        object value;
        return map.TryGetValue(key, out value) ? value as string : null;
    }

    private static string JsonQuote(string value)
    {
        return new JavaScriptSerializer().Serialize(value ?? "");
    }

    private static string QuoteArgument(string value)
    {
        return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
    }

    private sealed class SessionState
    {
        public int Port { get; set; }
        public int CodexPid { get; set; }
        public int HostPid { get; set; }
        public string HostStartedAtUtc { get; set; }
        public string AppExe { get; set; }
        public string CodexExe { get; set; }
        public string CreatedAtUtc { get; set; }
    }

    private sealed class StatusState
    {
        public string Status { get; set; }
        public string Message { get; set; }
        public string CreatedAtUtc { get; set; }
    }

    private sealed class TargetInfo
    {
        public string Id;
        public string Type;
        public string Url;
        public string WebSocketDebuggerUrl;
    }

    private sealed class CdpSession : IDisposable
    {
        private const int OperationTimeoutMilliseconds = 4000;
        private readonly Uri uri;
        private readonly ClientWebSocket socket = new ClientWebSocket();
        private int nextId;

        public CdpSession(string webSocketUrl)
        {
            uri = new Uri(webSocketUrl);
            socket.Options.Proxy = null;
        }

        public void Open()
        {
            using (CancellationTokenSource timeout = NewTimeout())
                socket.ConnectAsync(uri, timeout.Token).GetAwaiter().GetResult();
            Send("Runtime.enable", null);
            Send("Page.enable", null);
        }

        public object Evaluate(string expression)
        {
            var result = Send("Runtime.evaluate", new Dictionary<string, object>
            {
                { "expression", expression },
                { "awaitPromise", true },
                { "returnByValue", true },
                { "userGesture", false }
            });
            object details;
            if (result.TryGetValue("exceptionDetails", out details)) throw new InvalidOperationException("页面执行脚本失败。");
            object raw;
            if (!result.TryGetValue("result", out raw)) return null;
            var resultMap = raw as Dictionary<string, object>;
            if (resultMap == null) return null;
            object value;
            return resultMap.TryGetValue("value", out value) ? value : null;
        }

        public Dictionary<string, object> Send(string method, Dictionary<string, object> parameters)
        {
            using (CancellationTokenSource timeout = NewTimeout())
            {
                int id = Interlocked.Increment(ref nextId);
                var request = new Dictionary<string, object> { { "id", id }, { "method", method } };
                if (parameters != null) request["params"] = parameters;
                byte[] bytes = Utf8.GetBytes(new JavaScriptSerializer { MaxJsonLength = MaxCdpMessageBytes }.Serialize(request));
                if (bytes.Length > MaxCdpMessageBytes) throw new InvalidDataException("CDP 消息过大。");
                socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, timeout.Token).GetAwaiter().GetResult();

                while (true)
                {
                    string text = ReceiveText(timeout.Token);
                    var message = new JavaScriptSerializer { MaxJsonLength = MaxCdpMessageBytes }.DeserializeObject(text) as Dictionary<string, object>;
                    if (message == null) continue;
                    object messageId;
                    if (!message.TryGetValue("id", out messageId) || Convert.ToInt32(messageId) != id) continue;
                    object error;
                    if (message.TryGetValue("error", out error)) throw new InvalidOperationException("CDP 命令失败：" + method);
                    object result;
                    return message.TryGetValue("result", out result) && result is Dictionary<string, object>
                        ? (Dictionary<string, object>)result
                        : new Dictionary<string, object>();
                }
            }
        }

        private string ReceiveText(CancellationToken token)
        {
            using (var memory = new MemoryStream())
            {
                var buffer = new byte[8192];
                while (true)
                {
                    WebSocketReceiveResult result = socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).GetAwaiter().GetResult();
                    if (result.MessageType == WebSocketMessageType.Close) throw new IOException("CDP WebSocket 已关闭。");
                    memory.Write(buffer, 0, result.Count);
                    if (memory.Length > MaxCdpMessageBytes) throw new InvalidDataException("CDP 消息过大。");
                    if (result.EndOfMessage) break;
                }
                return Utf8.GetString(memory.ToArray());
            }
        }

        private static CancellationTokenSource NewTimeout()
        {
            var source = new CancellationTokenSource();
            source.CancelAfter(OperationTimeoutMilliseconds);
            return source;
        }

        public void Dispose()
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    using (CancellationTokenSource timeout = new CancellationTokenSource(1000))
                        socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token).Wait(1000);
                }
            }
            catch { try { socket.Abort(); } catch { } }
            socket.Dispose();
        }
    }
}
