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

internal static class Regression
{
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

    internal static void RunRelayContract()
    {
        TestNormalizeUrl();
        TestRelayProviderSettings();
        TestConnectionSuccess();
        TestConnectionErrorMapping();
        Console.WriteLine("Relay contract: URL, provider settings, Responses success and error mapping passed.");
    }

    public static int Run(string directory)
    {
        if (String.IsNullOrWhiteSpace(directory)) throw new ArgumentException("directory is required.");
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);

        List<CaseResult> results = new List<CaseResult>();
        RunCase(results, "NormalizeUrl", delegate { TestNormalizeUrl(); });
        RunCase(results, "Packaged Codex activation identity", delegate { TestPackagedCodexActivationIdentity(); });
        RunCase(results, "Auth-only state is not a relay credential", delegate { TestAuthOnlyIsNotCredential(Path.Combine(directory, "auth-only")); });
        RunCase(results, "Relay provider settings", delegate { TestRelayProviderSettings(); });
        RunCase(results, "ChangeSet preservation", delegate { TestChangeSetPreservation(Path.Combine(directory, "changeset-preserve")); });
        RunCase(results, "ChangeSet commit conflict", delegate { TestCommitConflict(Path.Combine(directory, "changeset-commit-conflict")); });
        RunCase(results, "ChangeSet restore conflict", delegate { TestRestoreConflict(Path.Combine(directory, "changeset-restore-conflict")); });
        RunCase(results, "Restore consent and automatic close workflows", delegate { ConfigurationWorkflowTests.Run(Path.Combine(directory, "configuration-workflows")); });
        RunCase(results, "DPAPI roundtrip", delegate { TestDpapiRoundtrip(); });
        RunCase(results, "CodexConfig batchWrite preservation", delegate { TestCodexConfigStage(Path.Combine(directory, "codex-config-stage")); });
        RunCase(results, "CodexConfig Unicode paths and payload", delegate { TestCodexConfigUnicode(Path.Combine(directory, "睿睿睿", "中转配置")); });
        RunCase(results, "Connection test success", delegate { TestConnectionSuccess(); });
        RunCase(results, "Connection test error mapping", delegate { TestConnectionErrorMapping(); });

        WriteReport(Path.Combine(directory, "report.json"), results);

        List<string> failures = new List<string>();
        foreach (CaseResult result in results)
        {
            if (!result.Passed) failures.Add(result.Name + ": " + result.Message);
        }
        if (failures.Count > 0) throw new InvalidOperationException(String.Join(Environment.NewLine, failures.ToArray()));
        return 0;
    }

    private static void TestNormalizeUrl()
    {
        AssertEqual("https://xai-tools.cn", AppController.OfficialWebsiteUrl, "official website URL");
        AssertEqual("https://xai-tools.cn/#recharge", AppController.OfficialRechargeUrl, "official recharge URL");
        AssertEqual("https://xai-tools.cn/v1", AppController.OfficialRelayUrl, "official API URL");
        ExpectThrows("illegal scheme", delegate { AppController.NormalizeUrl("ftp://relay.example.com/v1"); });
        ExpectThrows("http public", delegate { AppController.NormalizeUrl("http://relay.example.com/v1"); });
        ExpectThrows("query", delegate { AppController.NormalizeUrl("https://relay.example.com/v1?x=1"); });
        ExpectThrows("userinfo", delegate { AppController.NormalizeUrl("https://user:pass@relay.example.com/v1"); });

        AssertEqual("https://relay.example.com/v1", AppController.NormalizeUrl(" https://relay.example.com/v1/ "), "https normalized");
        AssertEqual("https://relay.example.com/v1", AppController.NormalizeUrl("https://relay.example.com/v1/responses"), "responses suffix");
        AssertEqual("http://127.0.0.1:8080/v1", AppController.NormalizeUrl("http://127.0.0.1:8080/v1"), "loopback http");
        AssertEqual("http://localhost:8080/v1", AppController.NormalizeUrl("http://localhost:8080"), "loopback default path");
    }

    private static void TestPackagedCodexActivationIdentity()
    {
        string root = @"C:\fixture\OpenAI.Codex_26.924.2738.0_x64__2p2nqsd0c76g0";
        string path = Path.Combine(root, "app", "ChatGPT.exe");
        string manifest = "<Package xmlns='http://schemas.microsoft.com/appx/manifest/foundation/windows10'><Applications>" +
            "<Application Id='CodexCoreCommandRunner' Executable='app/resources/codex-command-runner.exe'/>" +
            "<Application Id='App' Executable='app/ChatGPT.exe'/></Applications></Package>";
        AssertEqual("OpenAI.Codex_2p2nqsd0c76g0!App", CodexInstall.AppUserModelIdFromManifest(manifest, root, path, "OpenAI.Codex_2p2nqsd0c76g0"), "manifest application ID instead of executable name");
        AssertEqual("OpenAI.Codex_2p2nqsd0c76g0!RenamedApp", CodexInstall.AppUserModelIdFromManifest(manifest.Replace("Id='App'", "Id='RenamedApp'"), root, path, "OpenAI.Codex_2p2nqsd0c76g0"), "manifest application rename");
        ExpectThrows("unknown executable must not activate the command runner", delegate { CodexInstall.AppUserModelIdFromManifest(manifest, root, Path.Combine(root, "Codex.exe"), "fixture"); });
        ExpectThrows("manifest external entities must be prohibited", delegate { CodexInstall.AppUserModelIdFromManifest("<!DOCTYPE Package [<!ENTITY external SYSTEM 'file:///fixture'>]>" + manifest, root, path, "fixture"); });
        AssertEqual(null, CodexInstall.AppUserModelIdForPath(@"C:\Users\tester\AppData\Local\Programs\Codex\Codex.exe"), "unpackaged app identity");
    }

    private static void TestChangeSetPreservation(string root)
    {
        CreateFreshDirectory(root);
        string data = Path.Combine(root, "data");
        string target = Path.Combine(root, "config.txt");
        File.WriteAllText(target, "before", Utf8);

        List<FileChange> changes = new List<FileChange>();
        changes.Add(ChangeSet.Prepare(target, Utf8.GetBytes("after")));
        ChangeSet.Commit(changes, data);
        AssertEqual("after", File.ReadAllText(target, Utf8), "commit wrote desired value");

        ChangeSet.Restore(data, new HashSet<string>(new string[] { Path.GetFullPath(target) }, StringComparer.OrdinalIgnoreCase));
        AssertEqual("before", File.ReadAllText(target, Utf8), "restore preserved before image");
    }

    private static void TestCommitConflict(string root)
    {
        CreateFreshDirectory(root);
        string data = Path.Combine(root, "data");
        string target = Path.Combine(root, "config.txt");
        File.WriteAllText(target, "before", Utf8);

        List<FileChange> changes = new List<FileChange>();
        changes.Add(ChangeSet.Prepare(target, Utf8.GetBytes("after")));
        File.WriteAllText(target, "external", Utf8);

        ExpectThrows("commit conflict", delegate { ChangeSet.Commit(changes, data); });
        AssertEqual("external", File.ReadAllText(target, Utf8), "commit conflict did not overwrite external edit");
    }

    private static void TestRestoreConflict(string root)
    {
        CreateFreshDirectory(root);
        string data = Path.Combine(root, "data");
        string target = Path.Combine(root, "config.txt");
        File.WriteAllText(target, "before", Utf8);

        List<FileChange> changes = new List<FileChange>();
        changes.Add(ChangeSet.Prepare(target, Utf8.GetBytes("after")));
        ChangeSet.Commit(changes, data);
        File.WriteAllText(target, "external", Utf8);

        ExpectThrows("restore conflict", delegate
        {
            ChangeSet.Restore(data, new HashSet<string>(new string[] { Path.GetFullPath(target) }, StringComparer.OrdinalIgnoreCase));
        });
        AssertEqual("external", File.ReadAllText(target, Utf8), "restore conflict did not overwrite external edit");
    }

    private static void TestDpapiRoundtrip()
    {
        byte[] plain = Utf8.GetBytes("sk-test-fictional-dpapi-only");
        byte[] protectedBytes = PrivateFiles.Protect(plain);
        byte[] roundtrip = PrivateFiles.Unprotect(protectedBytes);
        AssertEqual("sk-test-fictional-dpapi-only", Utf8.GetString(roundtrip), "DPAPI roundtrip");
        if (IndexOf(protectedBytes, plain) >= 0) throw new InvalidOperationException("protected payload contains plaintext.");
    }

    private static void TestCodexConfigStage(string root)
    {
        EnsureFreshRoot(root);
        string cli = CodexInstall.Cli();
        string originalText =
            "# keep-comment\n" +
            "model = \"old-model\"\n" +
            "model_provider = \"other_node\"\n" +
            "approval_policy = \"never\"\n" +
            "instructions = \"\"\"\n" +
            "line one\n" +
            "line two\n" +
            "\"\"\"\n" +
            "\n" +
            "[model_providers.other_node]\n" +
            "name = \"Other\"\n" +
            "base_url = \"https://other.example.com/v1\"\n" +
            "wire_api = \"responses\"\n";

        List<object> edits = new List<object>();
        edits.Add(CodexConfig.Edit("model", "new-model"));
        edits.Add(CodexConfig.Edit("model_provider", "mid_web_companion"));
        edits.Add(CodexConfig.Edit("model_providers.mid_web_companion", new Dictionary<string, object> {
            { "name", "Mid-web" },
            { "base_url", "https://relay.example.com/v1" },
            { "wire_api", "responses" },
            { "supports_websockets", false },
            { "requires_openai_auth", false },
            { "experimental_bearer_token", "sk-test-fictional-stage-only" }
        }));
        ThemeCatalog.AddEdits("dracula", edits);

        byte[] stagedBytes = CodexConfig.Stage(cli, root, Utf8.GetBytes(originalText), edits);
        string staged = Utf8.GetString(stagedBytes);

        AssertContains(staged, "# keep-comment", "comment preserved");
        AssertContains(staged, "line one", "multiline body preserved");
        AssertContains(staged, "line two", "multiline body preserved");
        AssertContains(staged, "model_providers.other_node", "other provider node preserved");
        AssertContains(staged, "https://other.example.com/v1", "other provider value preserved");
        AssertContains(staged, "new-model", "model batchWrite applied");
        AssertContains(staged, "mid_web_companion", "provider batchWrite applied");
        AssertContains(staged, "https://relay.example.com/v1", "mid-web provider written");
        AssertContains(staged, "experimental_bearer_token", "provider bearer token written");
        AssertContains(staged, "appearanceTheme", "desktop theme written");
        AssertContains(staged, "dark", "dracula mode written");
        AssertContains(staged, "appearanceDarkCodeThemeId", "dracula code theme key written");
        AssertContains(staged, "dracula", "dracula code theme written");
        AssertContains(staged, "#FF79C6", "dracula accent written");
    }

    private static void TestCodexConfigUnicode(string root)
    {
        EnsureFreshRoot(root);
        string notify = "notify = [ 'C:\\Users\\睿睿睿\\AppData\\Local\\OpenAI\\Codex\\runtimes\\cua_node\\df473e5367fa2b42\\bin\\node_modules\\@oai\\sky\\bin\\windows\\codex-computer-use.exe', \"turn-ended\" ]";
        string original = "# 保留原有注释\nmodel = \"gpt-6-astra\"\nmodel_provider = \"custom\"\n" +
            "model_reasoning_effort = \"low\"\ndisable_response_storage = false\n" + notify + "\n" +
            "[model_providers.custom]\nname = \"luchikey\"\nbase_url = \"https://relay.example.com\"\n" +
            "wire_api = \"responses\"\nrequires_openai_auth = true\nexperimental_bearer_token = \"sk-test-old-token\"\n" +
            "[model_providers.other]\nname = \"其他服务\"\nwire_api = \"responses\"\n";
        string instructions = "中文指令与 Unicode：睿睿睿 / café / \U0001F680";
        var edits = AppController.BuildRelayConfigEdits("sk-test-unicode-pipe");
        edits.Add(CodexConfig.Edit("instructions", instructions));
        string staged = Utf8.GetString(CodexConfig.Stage(CodexInstall.Cli(), root, Utf8.GetBytes(original), edits));
        AssertContains(staged, "# 保留原有注释", "Chinese comment preserved");
        AssertContains(staged, "其他服务", "other provider preserved");
        AssertContains(staged, notify, "machine-specific notify command preserved exactly");
        AssertContains(staged, instructions, "UTF-8 request payload preserved");
        AssertRelayConfig(staged, "sk-test-unicode-pipe");
        foreach (string obsolete in new[] { "gpt-6-astra", "luchikey", "https://relay.example.com", "sk-test-old-token" })
            if (staged.Contains(obsolete)) throw new InvalidOperationException("旧中转设置没有被替换：" + obsolete);
        if (staged.IndexOf('\uFFFD') >= 0) throw new InvalidOperationException("配置中出现编码替换字符。");
        string fresh = Utf8.GetString(CodexConfig.Stage(CodexInstall.Cli(), root, null,
            AppController.BuildRelayConfigEdits("sk-test-fresh-config")));
        AssertRelayConfig(fresh, "sk-test-fresh-config");
        if (fresh.Contains("notify")) throw new InvalidOperationException("新配置不应写入其他机器的 notify 路径。");
        string stagingRoot = Path.Combine(root, "staging");
        if (Directory.Exists(stagingRoot) && Directory.GetDirectories(stagingRoot).Length != 0)
            throw new InvalidOperationException("配置服务退出后留下 staging 子目录。");
    }

    private static void AssertRelayConfig(string config, string apiKey)
    {
        AssertContains(config, "model_provider = \"custom\"", "custom provider selected");
        AssertContains(config, "model = \"gpt-5.6-sol\"", "relay model written");
        AssertContains(config, "model_reasoning_effort = \"xhigh\"", "reasoning effort written");
        AssertContains(config, "disable_response_storage = true", "response storage disabled");
        AssertContains(config, "[model_providers.custom]", "custom provider table written");
        AssertContains(config, "name = \"Codex Relay\"", "relay name written");
        AssertContains(config, "base_url = \"" + AppController.OfficialRelayUrl + "\"", "relay URL written");
        AssertContains(config, "wire_api = \"responses\"", "Responses protocol written");
        AssertContains(config, "requires_openai_auth = false", "OpenAI auth disabled");
        AssertContains(config, "experimental_bearer_token = \"" + apiKey + "\"", "input bearer token written");
    }

    private static void RunCase(List<CaseResult> results, string name, Action action)
    {
        CaseResult result = new CaseResult();
        result.Name = name;
        try
        {
            action();
            result.Passed = true;
            result.Message = "ok";
        }
        catch (Exception ex)
        {
            result.Passed = false;
            result.Message = ex.GetType().Name + " (0x" + ex.HResult.ToString("X8") + "): " + ex.Message + Environment.NewLine + ex.StackTrace;
        }
        results.Add(result);
    }

    private static void WriteReport(string path, List<CaseResult> results)
    {
        List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
        foreach (CaseResult result in results)
        {
            Dictionary<string, object> row = new Dictionary<string, object>();
            row["name"] = result.Name;
            row["passed"] = result.Passed;
            row["message"] = result.Message;
            rows.Add(row);
        }
        Dictionary<string, object> report = new Dictionary<string, object>();
        report["generated_utc"] = DateTime.UtcNow.ToString("o");
        report["tests"] = rows.ToArray();
        File.WriteAllText(path, Json.Write(report), Utf8);
    }

    private static void CreateFreshDirectory(string path)
    {
        if (Directory.Exists(path)) throw new InvalidOperationException("test directory already exists: " + path);
        Directory.CreateDirectory(path);
    }

    private static void TestAuthOnlyIsNotCredential(string root)
    {
        CreateFreshDirectory(root);
        string home = Path.Combine(root, "home");
        string data = Path.Combine(root, "data");
        Directory.CreateDirectory(home);
        string target = Path.Combine(home, "auth.json");
        string original = "{\"account_id\":\"old-account\",\"OPEN_API_KEY\":\"sk-test-legacy\",\"OPENAI_API_KEY\":\"sk-test-old\"," +
            "\"auth_mode\":\"chatgpt\",\"tokens\":{\"access_token\":\"fictional-old-token\"},\"last_refresh\":\"2026-01-01\"}";
        File.WriteAllText(target, original, Utf8);
        var controller = new AppController(data, home);
        if (controller.HasCredential) throw new InvalidOperationException("auth.json must not be treated as the relay credential.");
        if (!File.ReadAllBytes(target).SequenceEqual(Utf8.GetBytes(original)))
            throw new InvalidOperationException("credential detection must preserve auth.json byte for byte.");
    }

    private static void TestRelayProviderSettings()
    {
        Dictionary<string, object> provider = AppController.BuildRelayProviderSettings("sk-test-provider-settings");
        AssertEqual("Codex Relay", Convert.ToString(provider["name"]), "provider name");
        AssertEqual(AppController.OfficialRelayUrl, Convert.ToString(provider["base_url"]), "provider base URL");
        AssertEqual("responses", Convert.ToString(provider["wire_api"]), "provider wire API");
        AssertEqual("False", Convert.ToString(provider["requires_openai_auth"]), "provider auth mode");
        AssertEqual("sk-test-provider-settings", Convert.ToString(provider["experimental_bearer_token"]), "provider bearer token");
        if (provider.ContainsKey("auth") || provider.ContainsKey("env_key"))
            throw new InvalidOperationException("provider still contains an old credential source.");
    }

    private static void TestConnectionSuccess()
    {
        string response = Json.Write(new Dictionary<string, object> {
            { "id", "resp_test" }, { "object", "response" }, { "status", "completed" },
            { "output", new object[] { new Dictionary<string, object> {
                { "type", "message" }, { "role", "assistant" },
                { "content", new object[] { new Dictionary<string, object> { { "type", "output_text" }, { "text", "OK" } } } }
            } } }
        });
        ConnectionTestClient client = new ConnectionTestClient(new StubHandler(200, response), TimeSpan.FromSeconds(2));
        ConnectionTestResult result = client.TestAsync("https://relay.example.com/v1", "model-a", "sk-test-connection", CancellationToken.None).GetAwaiter().GetResult();
        if (!result.Success || result.HttpStatus != 200 || result.Message.IndexOf("成功", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("有效 Responses 响应未报告连接成功。");
    }

    private static void TestConnectionErrorMapping()
    {
        string response = Json.Write(new Dictionary<string, object> {
            { "error", new Dictionary<string, object> { { "code", "invalid_api_key" }, { "message", "secret must not leak" } } }
        });
        ConnectionTestClient client = new ConnectionTestClient(new StubHandler(401, response), TimeSpan.FromSeconds(2));
        ConnectionTestResult result = client.TestAsync("https://relay.example.com/v1", "model-a", "sk-test-connection", CancellationToken.None).GetAwaiter().GetResult();
        if (result.Success || result.HttpStatus != 401 || result.Message.IndexOf("密钥无效", StringComparison.Ordinal) < 0 ||
            result.Message.IndexOf("secret", StringComparison.OrdinalIgnoreCase) >= 0)
            throw new InvalidOperationException("鉴权失败没有映射为脱敏提示。");
    }

    private static void EnsureFreshRoot(string path)
    {
        if (Directory.Exists(path)) throw new InvalidOperationException("test directory already exists: " + path);
    }

    private static void AssertPathWritten(string text, string path, string name)
    {
        string escaped = path.Replace("\\", "\\\\");
        if (text.IndexOf(path, StringComparison.Ordinal) >= 0 || text.IndexOf(escaped, StringComparison.Ordinal) >= 0) return;
        throw new InvalidOperationException(name + " missing [" + path + "] or [" + escaped + "].");
    }

    private static void ExpectThrows(string name, Action action)
    {
        try
        {
            action();
        }
        catch
        {
            return;
        }
        throw new InvalidOperationException(name + " should throw.");
    }

    private static void AssertEqual(string expected, string actual, string name)
    {
        if (!String.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidOperationException(name + " expected [" + expected + "] but got [" + actual + "].");
    }

    private static void AssertContains(string haystack, string needle, string name)
    {
        if (haystack == null || haystack.IndexOf(needle, StringComparison.Ordinal) < 0)
            throw new InvalidOperationException(name + " missing [" + needle + "].");
    }

    private static Dictionary<string, object> RequireObject(Dictionary<string, object> parent, string key)
    {
        object value;
        if (!parent.TryGetValue(key, out value) || !(value is Dictionary<string, object>))
            throw new InvalidOperationException("missing object [" + key + "].");
        return (Dictionary<string, object>)value;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        if (haystack == null || needle == null || needle.Length == 0 || needle.Length > haystack.Length) return -1;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool matched = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    matched = false;
                    break;
                }
            }
            if (matched) return i;
        }
        return -1;
    }

    private sealed class CaseResult
    {
        public string Name;
        public bool Passed;
        public string Message;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly int status;
        private readonly string body;
        public StubHandler(int status, string body) { this.status = status; this.body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post || request.RequestUri.AbsoluteUri != "https://relay.example.com/v1/responses")
                throw new InvalidOperationException("连接测试请求方法或路径不正确。");
            if (request.Headers.Authorization == null || request.Headers.Authorization.Parameter != "sk-test-connection")
                throw new InvalidOperationException("连接测试没有使用输入的 API Key。");
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
