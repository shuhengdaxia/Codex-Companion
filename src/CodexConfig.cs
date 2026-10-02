using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Use the installed Codex TOML writer instead of editing TOML with regular expressions.
// The child works in a private staging CODEX_HOME. It never writes the live configuration.
public static class CodexConfig
{
    public static object Edit(string key, object value)
    { return new { keyPath = key, value = value, mergeStrategy = "replace" }; }

    public static object Remove(string key)
    { return new { keyPath = key, value = (object)null, mergeStrategy = "replace" }; }

    // Reads only the explicit user config layer from an isolated CODEX_HOME.
    // Defaults, system policy and session flags are intentionally excluded.
    public static Dictionary<string, object> ReadUserConfig(string cli, string dataDirectory, byte[] original)
    {
        string scratch = Path.Combine(dataDirectory, "staging", Guid.NewGuid().ToString("N"));
        PrivateFiles.EnsureDirectory(scratch);
        string path = Path.Combine(scratch, "config.toml");
        if (original != null) File.WriteAllBytes(path, original);
        try
        {
            var info = CreateStartInfo(cli, scratch);
            using (var process = Process.Start(info))
            using (var input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
            {
                var diagnostics = process.StandardError.ReadToEndAsync();
                try
                {
                    Request(process, input, 1, "initialize", new { clientInfo = new { name = "midweb_codex_companion", version = "1.0.0" } });
                    input.WriteLine(Json.Write(new { method = "initialized" }));
                    input.Flush();
                    var response = Request(process, input, 2, "config/read", new { includeLayers = true });
                    object rawLayers;
                    object[] layers = response.TryGetValue("layers", out rawLayers) ? rawLayers as object[] : null;
                    if (layers != null)
                    {
                        foreach (object rawLayer in layers)
                        {
                            var layer = rawLayer as Dictionary<string, object>;
                            object rawName;
                            var name = layer != null && layer.TryGetValue("name", out rawName) ? rawName as Dictionary<string, object> : null;
                            object rawType;
                            object rawFile;
                            if (name == null || !name.TryGetValue("type", out rawType) || Convert.ToString(rawType) != "user" ||
                                !name.TryGetValue("file", out rawFile) || !String.Equals(Path.GetFullPath(Convert.ToString(rawFile)), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                                continue;
                            object rawConfig;
                            var config = layer.TryGetValue("config", out rawConfig) ? rawConfig as Dictionary<string, object> : null;
                            return config ?? new Dictionary<string, object>(StringComparer.Ordinal);
                        }
                    }
                    return new Dictionary<string, object>(StringComparer.Ordinal);
                }
                finally
                {
                    input.Close();
                    if (!process.WaitForExit(3000)) process.Kill();
                    process.WaitForExit(3000);
                }
            }
        }
        finally { DeleteOwnedScratch(dataDirectory, scratch); }
    }

    public static byte[] Stage(string cli, string dataDirectory, byte[] original, List<object> edits)
    {
        string scratch = Path.Combine(dataDirectory, "staging", Guid.NewGuid().ToString("N"));
        PrivateFiles.EnsureDirectory(scratch);
        string path = Path.Combine(scratch, "config.toml");
        if (original != null) File.WriteAllBytes(path, original);
        try
        {
            var info = CreateStartInfo(cli, scratch);
            using (var process = Process.Start(info))
            // .NET Framework's StandardInput defaults to the Windows code page
            // (e.g. GB2312). JSON-RPC requires UTF-8, including Chinese file paths.
            // Do not emit a BOM before the first JSON message.
            using (var input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
            {
                // Drain diagnostics without logging: a user's configuration may contain secrets.
                var diagnostics = process.StandardError.ReadToEndAsync();
                try
                {
                    Request(process, input, 1, "initialize", new { clientInfo = new { name = "midweb_codex_companion", version = "1.0.0" } });
                    input.WriteLine(Json.Write(new { method = "initialized" }));
                    input.Flush();
                    var response = Request(process, input, 2, "config/read", new { includeLayers = false });
                    object value;
                    var config = response.TryGetValue("config", out value) ? value as Dictionary<string, object> : null;
                    if (config != null && !String.IsNullOrEmpty(Json.Text(config, "profile")))
                        throw new InvalidOperationException("当前 Codex 启用了独立 profile，暂不自动覆盖，请先使用默认配置。");
                    Request(process, input, 3, "config/batchWrite", new { edits = edits, filePath = path, reloadUserConfig = false });
                    // A read after writing verifies that Codex can load the generated TOML.
                    var verified = Request(process, input, 4, "config/read", new { includeLayers = false });
                    if (!verified.ContainsKey("config")) throw new InvalidDataException("Codex 配置校验没有完成。");
                    return File.ReadAllBytes(path);
                }
                finally
                {
                    input.Close();
                    if (!process.WaitForExit(3000)) process.Kill();
                    process.WaitForExit(3000);
                }
            }
        }
        finally
        {
            DeleteOwnedScratch(dataDirectory, scratch);
        }
    }

    static ProcessStartInfo CreateStartInfo(string cli, string scratch)
    {
        var info = new ProcessStartInfo(cli, "app-server --listen stdio:// -c analytics.enabled=false")
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            // Keep the child out of scratch so Windows does not retain the
            // staging directory as the process current directory on exit.
            WorkingDirectory = Path.GetTempPath()
        };
        info.EnvironmentVariables["CODEX_HOME"] = scratch;
        return info;
    }

    static void DeleteOwnedScratch(string dataDirectory, string scratch)
    {
        // Only our known fresh GUID directory is eligible for cleanup.
        string root = Path.GetFullPath(Path.Combine(dataDirectory, "staging")) + Path.DirectorySeparatorChar;
        string resolved = Path.GetFullPath(scratch);
        if (resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved))
            DeleteScratch(resolved);
    }

    static void DeleteScratch(string path)
    {
        // Codex may release inherited handles a moment after its process exits.
        // Retry briefly so a transient Windows lock cannot turn a successful
        // configuration write into a user-visible failure.
        for (int attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                Directory.Delete(path, true);
                return;
            }
            catch (IOException) { Thread.Sleep(100); }
            catch (UnauthorizedAccessException) { Thread.Sleep(100); }
        }
        // Cleanup is best-effort after the child has exited. The next run uses
        // a fresh GUID, and no live configuration file has been touched here.
    }
    static Dictionary<string, object> Request(Process process, TextWriter input, int id, string method, object parameters)
    {
        input.WriteLine(Json.Write(new { id = id, method = method, @params = parameters }));
        input.Flush();
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 20000)
        {
            var next = process.StandardOutput.ReadLineAsync();
            if (!next.Wait((int)Math.Max(1, 20000 - timer.ElapsedMilliseconds))) throw new TimeoutException("Codex 配置服务超时，请更新 Codex 后重试。");
            string line = next.Result;
            if (line == null) throw new InvalidOperationException("Codex 配置服务提前退出，原配置没有修改。");
            var item = Json.Read(line);
            object itemId;
            if (!item.TryGetValue("id", out itemId) || Convert.ToString(itemId) != id.ToString()) continue;
            if (item.ContainsKey("error")) throw new InvalidOperationException("Codex 拒绝了配置操作（" + method + "）。请检查 Codex 版本和现有配置；原配置未改动。");
            object result;
            if (!item.TryGetValue("result", out result) || !(result is Dictionary<string, object>)) throw new InvalidDataException("Codex 配置服务返回了不兼容的数据。");
            return (Dictionary<string, object>)result;
        }
        throw new TimeoutException("Codex 配置服务超时。");
    }
}
