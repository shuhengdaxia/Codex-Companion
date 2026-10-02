using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml;

public sealed class UserSettings
{
    public string BaseUrl { get; set; }
    public string Model { get; set; }
    // Legacy appearance fields are read only while migrating settings.json.
    // System configuration no longer applies or owns these values.
    public string Theme { get; set; }
    public string Skin { get; set; }
    public UserSettings() { BaseUrl = ""; Model = ""; Theme = "keep"; Skin = "native"; }
}

public sealed class AppearanceSettings
{
    public string Theme { get; set; }
    public string Skin { get; set; }
    public AppearanceSettings() { Theme = "keep"; Skin = "native"; }
}

public static class Json
{
    public static string Write(object value) { return new JavaScriptSerializer { MaxJsonLength = 8388608 }.Serialize(value); }
    public static Dictionary<string, object> Read(string value)
    {
        var result = new JavaScriptSerializer { MaxJsonLength = 8388608 }.DeserializeObject(value) as Dictionary<string, object>;
        if (result == null) throw new InvalidDataException("文件不是有效的 JSON 对象。");
        return result;
    }
    public static string Text(Dictionary<string, object> value, string key)
    { object item; return value.TryGetValue(key, out item) ? item as string : null; }
}

public static class PrivateFiles
{
    public static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    public static void EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
    public static byte[] Protect(byte[] bytes) { return ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser); }
    public static byte[] Unprotect(byte[] bytes) { return ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser); }
    public static byte[] ReadOptional(string path) { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
    public static string Digest(byte[] value)
    { if (value == null) return "missing"; using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(value)); }
    public static void WriteAtomic(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(content, 0, content.Length); file.Flush(true); }
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static byte[] Resource(string name)
    {
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        {
            if (stream == null) throw new InvalidDataException("程序资源缺失，请重新编译：" + name);
            using (var memory = new MemoryStream()) { stream.CopyTo(memory); return memory.ToArray(); }
        }
    }
}

// Keep credentials and before-images encrypted at rest. Restore refuses concurrent edits.
public sealed class FileChange
{
    public string Path { get; set; }
    public string Before { get; set; }
    public string AfterHash { get; set; }
    public byte[] Desired { get; set; }
    public ConfigKeyState[] ConfigBefore { get; set; }
    public ConfigKeyState[] ConfigAfter { get; set; }
}

public sealed class ConfigKeyState
{
    public string KeyPath { get; set; }
    public bool Exists { get; set; }
    public object Value { get; set; }
}

public sealed class RestorePlan
{
    internal string DataDirectory;
    internal string ManifestHash;
    internal List<FileChange> Changes;
    internal string[] Conflicts;
    public string[] ConflictingFiles { get { return (string[])Conflicts.Clone(); } }
}

public static class ChangeSet
{
    public static FileChange Prepare(string path, byte[] content)
    {
        byte[] before = PrivateFiles.ReadOptional(path);
        return new FileChange { Path = System.IO.Path.GetFullPath(path), Before = before == null ? null : Convert.ToBase64String(before),
            Desired = content, AfterHash = PrivateFiles.Digest(content) };
    }
    static byte[] Before(FileChange change) { return change.Before == null ? null : Convert.FromBase64String(change.Before); }
    static void Check(string path, string expected)
    { if (PrivateFiles.Digest(PrivateFiles.ReadOptional(path)) != expected) throw new IOException("配置已被其他程序修改，请重新操作；没有覆盖这些更改。"); }
    static void Put(string path, byte[] bytes) { if (bytes == null) { if (File.Exists(path)) File.Delete(path); } else PrivateFiles.WriteAtomic(path, bytes); }
    public static void Commit(List<FileChange> changes, string dataDirectory)
    { Commit(changes, dataDirectory, "latest-restore.bin"); }
    public static void Commit(List<FileChange> changes, string dataDirectory, string restoreFileName)
    {
        if (restoreFileName != "latest-restore.bin" && restoreFileName != "latest-appearance-restore.bin")
            throw new ArgumentException("恢复点名称无效。", "restoreFileName");
        foreach (var change in changes) Check(change.Path, PrivateFiles.Digest(Before(change)));
        string backup = System.IO.Path.Combine(dataDirectory, "backups", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".bin");
        var manifest = changes.Select(c => new { c.Path, c.Before, c.AfterHash, c.ConfigBefore, c.ConfigAfter }).ToArray();
        byte[] protectedManifest = PrivateFiles.Protect(PrivateFiles.Utf8.GetBytes(Json.Write(manifest)));
        PrivateFiles.WriteAtomic(backup, protectedManifest);
        var written = new List<FileChange>();
        try
        {
            foreach (var change in changes)
            {
                Check(change.Path, PrivateFiles.Digest(Before(change)));
                Put(change.Path, change.Desired); written.Add(change);
            }
            PrivateFiles.WriteAtomic(System.IO.Path.Combine(dataDirectory, restoreFileName), protectedManifest);
        }
        catch
        {
            foreach (var change in written.AsEnumerable().Reverse())
            {
                if (PrivateFiles.Digest(PrivateFiles.ReadOptional(change.Path)) != change.AfterHash)
                    throw new IOException("配置写入未完成，检测到并发改动，已保留加密备份，未覆盖外部更改。");
                Put(change.Path, Before(change));
            }
            throw;
        }
    }
    public static RestorePlan PrepareRestore(string dataDirectory, HashSet<string> allowedPaths)
    {
        string path = System.IO.Path.Combine(dataDirectory, "latest-restore.bin");
        if (!File.Exists(path)) throw new InvalidOperationException("没有可恢复的配置。");
        byte[] manifest = File.ReadAllBytes(path);
        byte[] plain = PrivateFiles.Unprotect(manifest);
        FileChange[] changes;
        try { changes = new JavaScriptSerializer { MaxJsonLength = 8388608 }.Deserialize<FileChange[]>(PrivateFiles.Utf8.GetString(plain)); }
        finally { Array.Clear(plain, 0, plain.Length); }
        if (changes == null || changes.Length == 0 || changes.Length > 8) throw new InvalidDataException("备份格式无效。");
        var reversed = new List<FileChange>();
        var conflicts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            if (change == null || String.IsNullOrWhiteSpace(change.Path) || String.IsNullOrEmpty(change.AfterHash))
                throw new InvalidDataException("备份格式无效。");
            string target = System.IO.Path.GetFullPath(change.Path);
            if (!allowedPaths.Contains(target) || !seen.Add(target))
                throw new InvalidDataException("备份包含重复或不受此工具管理的路径。");
            byte[] original = Before(change);
            string originalHash = PrivateFiles.Digest(original);
            // An unchanged file never needs restoring, even if Codex later updated it.
            if (originalHash == change.AfterHash) continue;
            FileChange reverse = Prepare(target, original);
            reverse.ConfigBefore = change.ConfigBefore;
            reverse.ConfigAfter = change.ConfigAfter;
            string currentHash = PrivateFiles.Digest(Before(reverse));
            if (currentHash == originalHash) continue;
            if (currentHash != change.AfterHash) conflicts.Add(System.IO.Path.GetFileName(target));
            reversed.Add(reverse);
        }
        return new RestorePlan { DataDirectory = System.IO.Path.GetFullPath(dataDirectory),
            ManifestHash = PrivateFiles.Digest(manifest), Changes = reversed, Conflicts = conflicts.ToArray() };
    }
    public static void CompleteRestore(RestorePlan plan, bool overwriteConfirmed)
    {
        if (plan == null) throw new ArgumentNullException("plan");
        if (plan.Conflicts.Length > 0 && !overwriteConfirmed)
            throw new IOException("配置已被其他程序修改：" + String.Join("、", plan.Conflicts) + "。请确认后恢复。");
        string path = System.IO.Path.Combine(plan.DataDirectory, "latest-restore.bin");
        Check(path, plan.ManifestHash);
        // Commit checks the exact inspected file snapshots again. Confirmation
        // never permits overwriting changes made after the preview was created.
        // Its encrypted backup also preserves the configuration being replaced.
        if (plan.Changes.Count > 0) Commit(plan.Changes, plan.DataDirectory);
        File.Delete(path);
    }
    public static void Restore(string dataDirectory, HashSet<string> allowedPaths)
    { CompleteRestore(PrepareRestore(dataDirectory, allowedPaths), false); }
}

public static class CodexInstall
{
    private const string ApplicationActivationManagerClassId = "45BA127D-10A8-46EA-8AB7-56EA9078943C";
    private const string ApplicationActivationManagerInterfaceId = "2E941141-7F97-4756-BA1D-9DECDE894A3D";

    public static string Desktop()
    {
        string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        if (Directory.Exists(packages))
        {
            try
            {
                foreach (string folder in Directory.GetDirectories(packages, "OpenAI.Codex_*").OrderByDescending(p => p))
                {
                    string candidate = InDirectory(Path.Combine(folder, "app"));
                    if (candidate != null) return candidate;
                }
            }
            catch (UnauthorizedAccessException) { }
        }
        foreach (string candidate in new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Codex", "Codex.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "Codex.exe") })
            if (File.Exists(candidate)) return candidate;
        // WindowsApps is often not listable without elevation; package discovery does not need it.
        var info = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command \"Get-AppxPackage -Name OpenAI.Codex | Select-Object -ExpandProperty InstallLocation\"")
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var process = Process.Start(info))
        {
            var result = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(12000)) { process.Kill(); throw new InvalidOperationException("检测 Codex 安装超时。"); }
            foreach (string line in result.Result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = InDirectory(Path.Combine(line.Trim(), "app"));
                if (candidate != null) return candidate;
            }
        }
        throw new InvalidOperationException("未找到 Codex 桌面版，请先安装并启动官方 Codex 一次。");
    }
    static string InDirectory(string directory)
    {
        // Current MSIX manifest launches ChatGPT.exe; Codex.exe is only a compatibility launcher.
        foreach (string name in new[] { "ChatGPT.exe", "Codex.exe" })
        { string path = Path.Combine(directory, name); if (File.Exists(path)) return path; }
        return null;
    }

    // Packaged ChatGPT.exe must be activated by Windows; starting its file path
    // directly drops the MSIX package identity and the client exits immediately.
    public static Process StartDesktop(string executable, string arguments)
    {
        if (String.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            throw new FileNotFoundException("Codex 程序不存在。", executable);

        string appUserModelId = AppUserModelIdForPath(executable);
        if (!String.IsNullOrEmpty(appUserModelId))
        {
            IApplicationActivationManager manager = null;
            try
            {
                uint processId;
                manager = (IApplicationActivationManager)new ApplicationActivationManager();
                int result = manager.ActivateApplication(appUserModelId, arguments ?? "", ActivateOptions.None, out processId);
                Marshal.ThrowExceptionForHR(result);
                if (processId == 0) throw new InvalidOperationException("Windows 未返回 Codex 启动进程，未确认启动成功。");
                return Process.GetProcessById((int)processId);
            }
            catch (COMException error)
            {
                throw new InvalidOperationException("无法通过 Windows 应用包启动 Codex（" + appUserModelId +
                    "，错误 0x" + error.ErrorCode.ToString("X8") + "）。请检查官方应用安装状态。", error);
            }
            finally { if (manager != null) Marshal.ReleaseComObject(manager); }
        }

        return Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable),
            Arguments = arguments ?? ""
        });
    }

    internal static string AppUserModelIdForPath(string executable)
    {
        string family = PackageFamilyName(executable);
        if (String.IsNullOrEmpty(family)) return null;
        DirectoryInfo directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(executable)));
        for (int i = 0; directory != null && i < 5; i++, directory = directory.Parent)
        {
            if (!directory.Name.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)) continue;
            string manifest = Path.Combine(directory.FullName, "AppxManifest.xml");
            if (!File.Exists(manifest)) break;
            return AppUserModelIdFromManifest(File.ReadAllText(manifest), directory.FullName, executable, family);
        }
        throw new InvalidOperationException("未找到 Codex 应用包清单，无法确认启动入口，请检查官方应用安装状态。");
    }

    internal static string AppUserModelIdFromManifest(string manifest, string packageDirectory, string executable, string family)
    {
        var document = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(new StringReader(manifest), new XmlReaderSettings {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1048576
        })) document.Load(reader);
        string expected = Path.GetFullPath(executable);
        foreach (XmlNode application in document.SelectNodes("/*[local-name()='Package']/*[local-name()='Applications']/*[local-name()='Application']"))
        {
            XmlAttribute path = application.Attributes["Executable"];
            XmlAttribute id = application.Attributes["Id"];
            if (path == null || id == null || String.IsNullOrWhiteSpace(id.Value)) continue;
            string candidate = Path.GetFullPath(Path.Combine(packageDirectory, path.Value.Replace('/', Path.DirectorySeparatorChar)));
            if (String.Equals(candidate, expected, StringComparison.OrdinalIgnoreCase)) return family + "!" + id.Value;
        }
        throw new InvalidOperationException("Codex 应用包清单没有与桌面程序匹配的启动入口，请检查官方应用安装状态。");
    }

    private static string PackageFamilyName(string executable)
    {
        string fullPath;
        try { fullPath = Path.GetFullPath(executable); }
        catch { return null; }
        string windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps") + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(windowsApps, StringComparison.OrdinalIgnoreCase)) return null;

        DirectoryInfo directory = new DirectoryInfo(Path.GetDirectoryName(fullPath));
        for (int i = 0; directory != null && i < 5; i++, directory = directory.Parent)
        {
            string name = directory.Name;
            int marker = name.IndexOf("__", StringComparison.Ordinal);
            if (marker <= 0 || !name.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)) continue;
            string publisherId = name.Substring(marker + 2);
            string packageVersion = name.Substring(0, marker);
            int versionSeparator = packageVersion.IndexOf('_');
            if (String.IsNullOrEmpty(publisherId) || versionSeparator <= 0) continue;
            return packageVersion.Substring(0, versionSeparator) + "_" + publisherId;
        }
        return null;
    }

    private enum ActivateOptions
    {
        None = 0
    }

    [ComImport, Guid(ApplicationActivationManagerInterfaceId), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, ActivateOptions options, out uint processId);
        [PreserveSig]
        int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr itemArray,
            [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
        [PreserveSig]
        int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr itemArray, out uint processId);
    }

    [ComImport, Guid(ApplicationActivationManagerClassId), ClassInterface(ClassInterfaceType.None)]
    private class ApplicationActivationManager { }

    public static string Cli()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(root))
        {
            string found = Directory.GetFiles(root, "codex.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (found != null) return found;
        }
        string bundled = Path.Combine(Path.GetDirectoryName(Desktop()), "resources", "codex.exe");
        if (File.Exists(bundled)) return bundled;
        throw new InvalidOperationException("未找到 Codex 配置服务，请先启动或更新 Codex 桌面版。");
    }
    public static void RequireClosed()
    {
        foreach (var process in Process.GetProcesses().Where(p => p.ProcessName.Equals("Codex", StringComparison.OrdinalIgnoreCase) || p.ProcessName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase)))
        {
            using (process)
            {
                string location;
                try { location = process.MainModule.FileName; }
                catch { throw new InvalidOperationException("无法确认 Codex 是否已退出。请完全退出 Codex 后再配置。"); }
                if (File.Exists(Path.Combine(Path.GetDirectoryName(location), "resources", "app.asar")))
                    throw new InvalidOperationException("请先保存工作并完全退出 Codex，再点击此操作。工具不会强制结束任务。");
            }
        }
    }
}

public sealed class AppController
{
    // Change this one constant when the official relay is deployed.
    // Keep the website destination separate from the temporary local API relay.
    public const string OfficialWebsiteUrl = "https://xai-tools.cn";
    public const string OfficialRechargeUrl = "https://xai-tools.cn/#recharge";
    public const string OfficialRelayUrl = "https://xai-tools.cn/v1";
    public const string RelayModel = "gpt-5.6-sol";
    const string AppearanceRestoreFileName = "latest-appearance-restore.bin";
    static readonly string[] SystemConfigKeys = new[] {
        "model_provider", "model", "model_reasoning_effort", "disable_response_storage", "model_providers.custom"
    };
    readonly string dataDirectory;
    public string CodexHome { get; private set; }
    string ConfigPath { get { return Path.Combine(CodexHome, "config.toml"); } }
    string AuthPath { get { return Path.Combine(CodexHome, "auth.json"); } }
    string StatePath { get { return Path.Combine(CodexHome, ".codex-global-state.json"); } }
    string SettingsPath { get { return Path.Combine(dataDirectory, "settings.json"); } }
    string AppearanceSettingsPath { get { return Path.Combine(dataDirectory, "appearance-settings.json"); } }
    string KeyPath { get { return Path.Combine(dataDirectory, "credential.bin"); } }
    public bool HasCredential { get { return File.Exists(KeyPath); } }
    public AppController() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MidWebCodex"),
        Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")) { }
    public AppController(string dataDirectory, string codexHome)
    { this.dataDirectory = Path.GetFullPath(dataDirectory); CodexHome = Path.GetFullPath(codexHome); }
    public UserSettings Load()
    {
        if (!File.Exists(SettingsPath)) return new UserSettings();
        return new JavaScriptSerializer().Deserialize<UserSettings>(File.ReadAllText(SettingsPath, PrivateFiles.Utf8)) ?? new UserSettings();
    }

    public AppearanceSettings LoadAppearanceSettings()
    {
        if (File.Exists(AppearanceSettingsPath))
        {
            AppearanceSettings saved = new JavaScriptSerializer().Deserialize<AppearanceSettings>(File.ReadAllText(AppearanceSettingsPath, PrivateFiles.Utf8));
            if (saved == null) throw new InvalidDataException("外观设置文件格式无效。");
            ValidateAppearance(saved);
            return saved;
        }
        UserSettings legacy = Load();
        return NormalizeLegacyAppearance(legacy);
    }

    static AppearanceSettings NormalizeLegacyAppearance(UserSettings legacy)
    {
        var result = new AppearanceSettings();
        if (legacy == null) return result;
        if (ThemeCatalog.Themes.Any(t => t.Id == legacy.Theme)) result.Theme = legacy.Theme;
        if (legacy.Skin == "native" || legacy.Skin == "qq") result.Skin = legacy.Skin;
        return result;
    }

    static void ValidateAppearance(AppearanceSettings settings)
    {
        if (settings == null) throw new ArgumentNullException("settings");
        if (!ThemeCatalog.Themes.Any(t => t.Id == settings.Theme)) throw new ArgumentException("请选择列表中的主题。");
        if (settings.Skin != "native" && settings.Skin != "qq") throw new ArgumentException("请选择原生或 QQ 皮肤。");
    }

    string ReadStoredApiKey()
    {
        if (File.Exists(KeyPath))
        {
            byte[] plain = null;
            try
            {
                if (new FileInfo(KeyPath).Length > 32768) throw new InvalidDataException();
                plain = PrivateFiles.Unprotect(File.ReadAllBytes(KeyPath));
                return PrivateFiles.Utf8.GetString(plain);
            }
            catch { throw new InvalidOperationException("无法解密已保存的 API Key，请重新输入后配置。"); }
            finally { if (plain != null) Array.Clear(plain, 0, plain.Length); }
        }

        return null;
    }

    static void ValidateApiKey(string apiKey)
    {
        if (String.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 8192 || apiKey.Any(c => c < 33 || c > 126))
            throw new ArgumentException("API Key 必须为不含空格或换行的有效 ASCII 字符串。");
    }

    internal static Dictionary<string, object> BuildRelayProviderSettings(string apiKey)
    {
        return new Dictionary<string, object> {
            { "name", "Codex Relay" }, { "base_url", OfficialRelayUrl }, { "wire_api", "responses" },
            { "requires_openai_auth", false }, { "experimental_bearer_token", apiKey }
        };
    }

    internal static List<object> BuildRelayConfigEdits(string apiKey)
    {
        // Leave machine-specific notify commands and unrelated settings intact.
        return new List<object> {
            CodexConfig.Edit("model_provider", "custom"),
            CodexConfig.Edit("model", RelayModel),
            CodexConfig.Edit("model_reasoning_effort", "xhigh"),
            CodexConfig.Edit("disable_response_storage", true),
            CodexConfig.Edit("model_providers.custom", BuildRelayProviderSettings(apiKey))
        };
    }
    public static string NormalizeUrl(string value)
    {
        Uri uri;
        if (!Uri.TryCreate((value ?? "").Trim(), UriKind.Absolute, out uri) || (uri.Scheme != "https" && uri.Scheme != "http") ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("请输入不带账号、查询参数的 mid-web HTTP(S) 地址。");
        if (uri.Scheme != "https" && !uri.IsLoopback) throw new ArgumentException("公网中转地址需要使用 HTTPS；HTTP 仅限本机测试。");
        string path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase)) path = path.Substring(0, path.Length - 10);
        if (path.Length == 0) path = "/v1";
        return uri.GetLeftPart(UriPartial.Authority) + path;
    }
    public async Task<string[]> FetchModelsAsync(string url)
    {
        string normalized = NormalizeUrl(url);
        using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
        using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 1048576 })
        {
            // /models is public in mid-web. No credential leaves the machine for this check.
            using (var response = await client.GetAsync(normalized + "/models"))
            {
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException("模型列表请求失败，HTTP " + (int)response.StatusCode + "。请检查网关地址。");
                var body = Json.Read(await response.Content.ReadAsStringAsync());
                object rows;
                if (!body.TryGetValue("data", out rows) || !(rows is object[])) throw new InvalidDataException("该地址没有返回兼容的模型列表。");
                var models = ((object[])rows).OfType<Dictionary<string, object>>().Select(x => Json.Text(x, "id"))
                    .Where(x => !String.IsNullOrWhiteSpace(x)).Distinct().OrderBy(x => x).ToArray();
                if (models.Length == 0) throw new InvalidOperationException("网关没有可用的公开模型，请先在 mid-web 后台配置模型。");
                return models;
            }
        }
    }

    public Task<ConnectionTestResult> TestConnectionAsync(string url, string model, string newApiKey, CancellationToken cancellationToken)
    { return TestConnectionAsync(url, model, newApiKey, cancellationToken, new ConnectionTestClient()); }

    internal async Task<ConnectionTestResult> TestConnectionAsync(string url, string model, string newApiKey,
        CancellationToken cancellationToken, ConnectionTestClient client)
    {
        string normalized = NormalizeUrl(url);
        if (String.IsNullOrWhiteSpace(model) || model.Length > 200 || model.Any(Char.IsControl))
            throw new ArgumentException("请选择或填写有效的模型 ID。");
        cancellationToken.ThrowIfCancellationRequested();
        string key = newApiKey;
        byte[] plain = null;
        try
        {
            if (String.IsNullOrWhiteSpace(key))
            {
                if (!HasCredential) throw new ArgumentException("请先输入用于测试的 API Key。");
                // A saved key is only reusable at its saved endpoint. Changing
                // the endpoint requires explicitly entering a key for that site.
                UserSettings saved;
                try { saved = Load(); }
                catch { throw new InvalidOperationException("无法读取已保存的网关设置，请重新输入 API Key 后测试。"); }
                string savedUrl;
                try { savedUrl = NormalizeUrl(saved.BaseUrl); }
                catch { throw new InvalidOperationException("已保存的网关地址无效，请重新输入 API Key 后测试。"); }
                if (!String.Equals(normalized, savedUrl, StringComparison.Ordinal))
                    throw new InvalidOperationException("网关地址已改变，请重新输入该地址的 API Key 后测试。");
                try
                {
                    if (new FileInfo(KeyPath).Length > 32768) throw new InvalidDataException();
                    plain = PrivateFiles.Unprotect(File.ReadAllBytes(KeyPath));
                    key = PrivateFiles.Utf8.GetString(plain);
                }
                catch { throw new InvalidOperationException("无法解密已保存的 API Key，请重新输入后测试。"); }
            }
            if (String.IsNullOrWhiteSpace(key) || key.Length > 8192 || key.Any(c => c < 33 || c > 126))
                throw new ArgumentException("API Key 必须为不含空格或换行的有效 ASCII 字符串。");
            return await client.TestAsync(normalized, model.Trim(), key, cancellationToken).ConfigureAwait(false);
        }
        finally { if (plain != null) Array.Clear(plain, 0, plain.Length); }
    }

    public Task<string> ApplyAsync(UserSettings settings, string newApiKey)
    { return Task.Run(() => Apply(settings, newApiKey)); }
    string Apply(UserSettings settings, string newApiKey)
    {
        if (settings == null) throw new ArgumentNullException("settings");
        settings.BaseUrl = OfficialRelayUrl;
        settings.Model = RelayModel;
        string apiKey = String.IsNullOrWhiteSpace(newApiKey) ? ReadStoredApiKey() : newApiKey.Trim();
        if (String.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("首次配置需要填写 mid-web API Key。");
        ValidateApiKey(apiKey);
        string cli = CodexInstall.Cli();
        CodexDesktopRestart.CloseAndWait(CancellationToken.None);
        PrivateFiles.EnsureDirectory(dataDirectory);
        byte[] before = PrivateFiles.ReadOptional(ConfigPath);
        Dictionary<string, object> beforeConfig = CodexConfig.ReadUserConfig(cli, dataDirectory, before);
        var edits = BuildRelayConfigEdits(apiKey);
        byte[] staged = CodexConfig.Stage(cli, dataDirectory, before, edits);
        var changes = new List<FileChange>();
        var configChange = ChangeSet.Prepare(ConfigPath, staged);
        configChange.ConfigBefore = CaptureConfigStates(beforeConfig);
        configChange.ConfigAfter = CaptureConfigStates(CodexConfig.ReadUserConfig(cli, dataDirectory, staged));
        if (PrivateFiles.Digest(before) != PrivateFiles.Digest(PrivateFiles.ReadOptional(ConfigPath))) throw new IOException("配置发生变化，请重试。");
        changes.Add(configChange);
        if (!String.IsNullOrWhiteSpace(newApiKey))
        {
            byte[] secret = PrivateFiles.Utf8.GetBytes(apiKey);
            try { changes.Add(ChangeSet.Prepare(KeyPath, PrivateFiles.Protect(secret))); }
            finally { Array.Clear(secret, 0, secret.Length); }
        }
        AppearanceSettings legacyAppearance = File.Exists(AppearanceSettingsPath)
            ? null
            : TryReadLegacyAppearance(PrivateFiles.ReadOptional(SettingsPath));
        changes.Add(ChangeSet.Prepare(SettingsPath, BuildSystemSettings(settings, legacyAppearance)));
        CodexDesktopRestart.RequireClosed();
        ChangeSet.Commit(changes, dataDirectory);
        return "配置完成，已更新 config.toml。点击“打开 Codex”后新建任务使用。";
    }

    public Task<string> ApplyAppearanceAsync(AppearanceSettings settings)
    { return Task.Run(() => ApplyAppearance(settings)); }

    string ApplyAppearance(AppearanceSettings settings)
    {
        ValidateAppearance(settings);
        string cli = CodexInstall.Cli();
        CodexDesktopRestart.CloseAndWait(CancellationToken.None);
        PrivateFiles.EnsureDirectory(dataDirectory);
        var changes = new List<FileChange>();
        if (settings.Theme != "keep")
        {
            byte[] before = PrivateFiles.ReadOptional(ConfigPath);
            var edits = new List<object>();
            ThemeCatalog.AddEdits(settings.Theme, edits);
            byte[] staged = CodexConfig.Stage(cli, dataDirectory, before, edits);
            if (PrivateFiles.Digest(before) != PrivateFiles.Digest(PrivateFiles.ReadOptional(ConfigPath)))
                throw new IOException("配置发生变化，请重试。");
            changes.Add(ChangeSet.Prepare(ConfigPath, staged));
        }
        changes.Add(ChangeSet.Prepare(AppearanceSettingsPath, PrivateFiles.Utf8.GetBytes(Json.Write(settings))));
        CodexDesktopRestart.RequireClosed();
        ChangeSet.Commit(changes, dataDirectory, AppearanceRestoreFileName);
        return "外观已保存。";
    }

    static byte[] BuildSystemSettings(UserSettings settings, AppearanceSettings legacyAppearance)
    {
        object value = legacyAppearance == null
            ? (object)new { settings.BaseUrl, settings.Model }
            : new { settings.BaseUrl, settings.Model, legacyAppearance.Theme, legacyAppearance.Skin };
        return PrivateFiles.Utf8.GetBytes(Json.Write(value));
    }

    static ConfigKeyState[] CaptureConfigStates(Dictionary<string, object> config)
    {
        return SystemConfigKeys.Select(delegate(string key)
        {
            object value;
            bool exists = TryGetConfigValue(config, key, out value);
            return new ConfigKeyState { KeyPath = key, Exists = exists, Value = exists ? value : null };
        }).ToArray();
    }

    static bool TryGetConfigValue(Dictionary<string, object> config, string keyPath, out object value)
    {
        value = null;
        object current = config;
        foreach (string part in keyPath.Split('.'))
        {
            var map = current as IDictionary<string, object>;
            if (map == null || !map.TryGetValue(part, out current)) return false;
        }
        value = current;
        return true;
    }

    static bool ConfigStatesMatch(Dictionary<string, object> config, ConfigKeyState[] expected)
    {
        if (expected == null) return false;
        foreach (ConfigKeyState state in expected)
        {
            object actual;
            bool exists = TryGetConfigValue(config, state.KeyPath, out actual);
            if (exists != state.Exists) return false;
            if (exists && Json.Write(actual) != Json.Write(state.Value)) return false;
        }
        return true;
    }

    static List<object> RestoreConfigEdits(ConfigKeyState[] states)
    {
        var edits = new List<object>();
        foreach (ConfigKeyState state in states)
        {
            // Existing conversations keep their original provider ID. Preserve
            // the relay definition introduced by this tool so those sessions
            // remain loadable after the active account is restored.
            if (!state.Exists && state.KeyPath == "model_providers.custom") continue;
            edits.Add(state.Exists ? CodexConfig.Edit(state.KeyPath, state.Value) : CodexConfig.Remove(state.KeyPath));
        }
        return edits;
    }

    static byte[] DecodeBefore(FileChange change)
    { return change.Before == null ? null : Convert.FromBase64String(change.Before); }

    byte[] BuildRestoredSystemSettings(byte[] original, AppearanceSettings legacyAppearance)
    {
        UserSettings restored = new UserSettings();
        if (original != null)
            restored = new JavaScriptSerializer().Deserialize<UserSettings>(PrivateFiles.Utf8.GetString(original)) ?? restored;
        if (original == null && legacyAppearance == null) return null;
        return BuildSystemSettings(restored, legacyAppearance);
    }

    RestorePlan PrepareSystemRestorePlan(RestorePlan plan)
    {
        string cli = CodexInstall.Cli();
        var conflicts = new HashSet<string>(plan.Conflicts, StringComparer.OrdinalIgnoreCase);
        var retained = new List<FileChange>();
        foreach (FileChange change in plan.Changes)
        {
            if (String.Equals(change.Path, StatePath, StringComparison.OrdinalIgnoreCase))
            {
                conflicts.Remove(Path.GetFileName(StatePath));
                continue;
            }
            if (String.Equals(change.Path, ConfigPath, StringComparison.OrdinalIgnoreCase))
            {
                byte[] current = DecodeBefore(change);
                Dictionary<string, object> currentConfig = CodexConfig.ReadUserConfig(cli, dataDirectory, current);
                ConfigKeyState[] beforeStates = change.ConfigBefore;
                if (beforeStates == null)
                    beforeStates = CaptureConfigStates(CodexConfig.ReadUserConfig(cli, dataDirectory, change.Desired));
                if (change.ConfigAfter != null)
                {
                    conflicts.Remove(Path.GetFileName(ConfigPath));
                    if (!ConfigStatesMatch(currentConfig, change.ConfigAfter)) conflicts.Add(Path.GetFileName(ConfigPath));
                }
                change.Desired = CodexConfig.Stage(cli, dataDirectory, current, RestoreConfigEdits(beforeStates));
                change.AfterHash = PrivateFiles.Digest(change.Desired);
                retained.Add(change);
                continue;
            }
            if (String.Equals(change.Path, SettingsPath, StringComparison.OrdinalIgnoreCase))
            {
                AppearanceSettings legacy = File.Exists(AppearanceSettingsPath)
                    ? null
                    : TryReadLegacyAppearance(DecodeBefore(change));
                UserSettings currentSystem = DeserializeSettings(DecodeBefore(change));
                conflicts.Remove(Path.GetFileName(SettingsPath));
                if (!String.Equals(currentSystem.BaseUrl ?? "", OfficialRelayUrl, StringComparison.Ordinal) ||
                    !String.Equals(currentSystem.Model ?? "", RelayModel, StringComparison.Ordinal))
                    conflicts.Add(Path.GetFileName(SettingsPath));
                change.Desired = BuildRestoredSystemSettings(change.Desired, legacy);
                change.AfterHash = PrivateFiles.Digest(change.Desired);
                retained.Add(change);
                continue;
            }
            retained.Add(change);
        }
        plan.Changes = retained;
        plan.Conflicts = conflicts.ToArray();
        return plan;
    }

    static UserSettings DeserializeSettings(byte[] bytes)
    {
        if (bytes == null) return new UserSettings();
        return new JavaScriptSerializer().Deserialize<UserSettings>(PrivateFiles.Utf8.GetString(bytes)) ?? new UserSettings();
    }

    static AppearanceSettings TryReadLegacyAppearance(byte[] bytes)
    {
        if (bytes == null) return null;
        Dictionary<string, object> raw = Json.Read(PrivateFiles.Utf8.GetString(bytes));
        if (!raw.ContainsKey("Theme") && !raw.ContainsKey("Skin")) return null;
        return NormalizeLegacyAppearance(new JavaScriptSerializer().Deserialize<UserSettings>(PrivateFiles.Utf8.GetString(bytes)));
    }

    public string Restore()
    {
        return CompleteRestore(PrepareRestore(), false);
    }
    public RestorePlan PrepareRestore()
    {
        var allowed = new HashSet<string>(new[] { ConfigPath, AuthPath, StatePath, SettingsPath, KeyPath, Path.Combine(dataDirectory, "MidWebCredential.exe") }, StringComparer.OrdinalIgnoreCase);
        // Check backup availability before requesting the desktop to close.
        ChangeSet.PrepareRestore(dataDirectory, allowed);
        CodexDesktopRestart.CloseAndWait(CancellationToken.None);
        return PrepareSystemRestorePlan(ChangeSet.PrepareRestore(dataDirectory, allowed));
    }
    public string CompleteRestore(RestorePlan plan, bool overwriteConfirmed)
    {
        if (plan == null || !String.Equals(plan.DataDirectory, dataDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("恢复计划不属于当前配置目录。");
        CodexDesktopRestart.RequireClosed();
        ChangeSet.CompleteRestore(plan, overwriteConfirmed);
        return "已恢复上次配置；custom provider 已按旧任务兼容规则处理。恢复前内容已加密备份，点击“打开 Codex”使配置生效。";
    }
    public void OpenCodex()
    {
        AppearanceSettings settings = LoadAppearanceSettings();
        string executable = CodexInstall.Desktop();
        if (settings.Skin == "qq")
        {
            CodexInstall.RequireClosed();
            PrivateFiles.EnsureDirectory(dataDirectory);
            SkinBridge.Start(executable, Assembly.GetExecutingAssembly().Location, dataDirectory);
        }
        else
        {
            SkinBridge.Stop(dataDirectory);
            CodexInstall.StartDesktop(executable, "");
        }
    }
}
