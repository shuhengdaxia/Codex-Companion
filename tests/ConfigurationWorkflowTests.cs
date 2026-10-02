using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

// All files are fictional and private to the test run. Desktop operations are
// substituted so testing never closes the user's running Codex session.
internal static class ConfigurationWorkflowTests
{
    internal static void Run(string root)
    {
        Directory.CreateDirectory(root);
        TestConfirmedRestore(Path.Combine(root, "consent"));
        TestPreviewRace(Path.Combine(root, "race"));
        TestUnchangedFiles(Path.Combine(root, "unchanged"));
        TestControllerLifecycle(Path.Combine(root, "controller"));
        TestCredentialReusePreservesDesktopState(Path.Combine(root, "credential-reuse"));
        TestAppearanceMigration(Path.Combine(root, "appearance-migration"));
        TestAppearanceBackupRollback(Path.Combine(root, "appearance-rollback"));
        TestSystemRestorePreservesAppearance(Path.Combine(root, "appearance-system-isolation"));
        TestLegacySystemRestorePreservesAppearance(Path.Combine(root, "legacy-appearance-isolation"));
        TestConfigurationRelaunch();
        Console.WriteLine("Restore consent, appearance isolation and migration, encrypted recovery, race protection and configuration shutdown checks passed.");
    }

    static void TestConfigurationRelaunch()
    {
        bool configured = false;
        int starts = 0;
        ConfigurationLaunchResult result = ConfigurationLaunchWorkflow.RunAsync(
            () => { configured = true; return Task.FromResult("configured"); },
            () => { Assert(configured, "Start must follow configuration commit."); starts++; }).GetAwaiter().GetResult();
        Assert(result.Started && starts == 1, "Successful configuration must start Codex exactly once.");
        ExpectFailure(delegate { ConfigurationLaunchWorkflow.RunAsync(
            () => { throw new TimeoutException("quit timeout"); }, () => starts++).GetAwaiter().GetResult(); });
        Assert(starts == 1, "Quit or configuration failure must never launch Codex.");
        result = ConfigurationLaunchWorkflow.RunAsync(() => Task.FromResult("configured"),
            () => { throw new InvalidOperationException("activation failed"); }).GetAwaiter().GetResult();
        Assert(!result.Started && result.Message.Contains("配置已保存") && result.Message.Contains("activation failed"),
            "Launch failure must distinguish saved configuration from a successful relaunch.");
    }

    static void TestConfirmedRestore(string root)
    {
        Directory.CreateDirectory(root);
        string target = Path.Combine(root, "auth.json");
        string data = Path.Combine(root, "data");
        File.WriteAllText(target, "original-login");
        ChangeSet.Commit(new List<FileChange> { ChangeSet.Prepare(target, Bytes("relay-login")) }, data);
        File.WriteAllText(target, "subsequent-login");
        string manifest = Path.Combine(data, "latest-restore.bin");
        string digest = PrivateFiles.Digest(File.ReadAllBytes(manifest));
        RestorePlan plan = ChangeSet.PrepareRestore(data, Allowed(target));
        Assert(plan.ConflictingFiles.Length == 1 && plan.ConflictingFiles[0] == "auth.json", "Preview must name the conflicting file only.");
        Assert(File.ReadAllText(target) == "subsequent-login", "Preview must not change files.");
        ExpectFailure(delegate { ChangeSet.CompleteRestore(plan, false); });
        Assert(PrivateFiles.Digest(File.ReadAllBytes(manifest)) == digest && File.ReadAllText(target) == "subsequent-login", "Declining must preserve files and the backup pointer.");
        ChangeSet.CompleteRestore(plan, true);
        Assert(File.ReadAllText(target) == "original-login", "Confirmed restore must recover the original login.");
        Assert(!File.Exists(manifest), "Successful restoration must consume the restore pointer.");
        bool rescueFound = false;
        foreach (string backup in Directory.GetFiles(Path.Combine(data, "backups"), "*.bin"))
        {
            byte[] encrypted = File.ReadAllBytes(backup);
            Assert(PrivateFiles.Utf8.GetString(encrypted.Select(b => b < 128 ? b : (byte)32).ToArray()).IndexOf("subsequent-login", StringComparison.Ordinal) < 0, "Rescue data must not be stored in plaintext.");
            byte[] plain = PrivateFiles.Unprotect(encrypted);
            try
            {
                FileChange[] entries = new JavaScriptSerializer().Deserialize<FileChange[]>(PrivateFiles.Utf8.GetString(plain));
                rescueFound |= entries.Any(e => e.Before != null && PrivateFiles.Utf8.GetString(Convert.FromBase64String(e.Before)) == "subsequent-login");
            }
            finally { Array.Clear(plain, 0, plain.Length); }
        }
        Assert(rescueFound, "The displaced login must have an encrypted rescue backup.");
    }

    static void TestPreviewRace(string root)
    {
        Directory.CreateDirectory(root);
        string first = Path.Combine(root, "config.toml");
        string second = Path.Combine(root, "auth.json");
        string data = Path.Combine(root, "data");
        File.WriteAllText(first, "before-config");
        File.WriteAllText(second, "before-auth");
        ChangeSet.Commit(new List<FileChange> { ChangeSet.Prepare(first, Bytes("after-config")), ChangeSet.Prepare(second, Bytes("after-auth")) }, data);
        File.WriteAllText(first, "external-config");
        RestorePlan plan = ChangeSet.PrepareRestore(data, Allowed(first, second));
        File.WriteAllText(second, "changed-during-confirmation");
        ExpectFailure(delegate { ChangeSet.CompleteRestore(plan, true); });
        Assert(File.ReadAllText(first) == "external-config" && File.ReadAllText(second) == "changed-during-confirmation", "A confirmation race must leave every file untouched.");
        plan = ChangeSet.PrepareRestore(data, Allowed(first, second));
        ChangeSet.Commit(new List<FileChange> { ChangeSet.Prepare(first, Bytes("new-operation")) }, data);
        ExpectFailure(delegate { ChangeSet.CompleteRestore(plan, true); });
        Assert(File.ReadAllText(first) == "new-operation", "A stale restore pointer must never supersede a newer operation.");
        ExpectFailure(delegate { ChangeSet.PrepareRestore(data, Allowed(second)); });
    }

    static void TestUnchangedFiles(string root)
    {
        Directory.CreateDirectory(root);
        string unchanged = Path.Combine(root, "state.json");
        string created = Path.Combine(root, "auth.json");
        string restored = Path.Combine(root, "config.toml");
        string data = Path.Combine(root, "data");
        File.WriteAllText(unchanged, "same");
        File.WriteAllText(restored, "before");
        ChangeSet.Commit(new List<FileChange> { ChangeSet.Prepare(unchanged, Bytes("same")), ChangeSet.Prepare(created, Bytes("new")), ChangeSet.Prepare(restored, Bytes("after")) }, data);
        File.WriteAllText(unchanged, "codex-updated-state");
        File.WriteAllText(restored, "before");
        RestorePlan plan = ChangeSet.PrepareRestore(data, Allowed(unchanged, created, restored));
        Assert(plan.ConflictingFiles.Length == 0, "No-op and already-restored files must not report false conflicts.");
        ChangeSet.CompleteRestore(plan, false);
        Assert(File.ReadAllText(unchanged) == "codex-updated-state" && File.ReadAllText(restored) == "before" && !File.Exists(created), "Restore must skip unchanged files and remove files created by configuration.");
    }

    static void TestControllerLifecycle(string root)
    {
        string home = Path.Combine(root, "home");
        string data = Path.Combine(root, "data");
        Directory.CreateDirectory(home);
        string config = Path.Combine(home, "config.toml");
        string auth = Path.Combine(home, "auth.json");
        string state = Path.Combine(home, ".codex-global-state.json");
        string original = "model = \"old-model\"\n";
        string savedOnExit = "# saved by Codex when closing\nmodel = \"old-model\"\n";
        string login = "{\"OPENAI_API_KEY\":\"sk-test-old-login\",\"account_id\":\"fictional\"}";
        byte[] loginBytes = Bytes(login);
        byte[] stateBytes = Bytes("{\"locale\":\"zh-CN\",\"theme\":\"system\"}");
        File.WriteAllText(config, original);
        File.WriteAllBytes(auth, loginBytes);
        File.WriteAllBytes(state, stateBytes);
        var controller = new AppController(data, home);
        var oldClose = CodexDesktopRestart.CloseAndWaitOverride;
        var oldFind = CodexDesktopRestart.FindOverride;
        var oldExecutable = CodexDesktopRestart.ExecutableOverride;
        try
        {
            int closes = 0;
            int closedChecks = 0;
            CodexDesktopRestart.ExecutableOverride = delegate { return "fixture.exe"; };
            CodexDesktopRestart.FindOverride = delegate(string path) { closedChecks++; return new List<CodexProcessAdapter>(); };
            CodexDesktopRestart.CloseAndWaitOverride = delegate(CancellationToken token) { closes++; };
            ExpectFailure(delegate { controller.ApplyAsync(new UserSettings(), "invalid key").GetAwaiter().GetResult(); });
            ExpectFailure(delegate { controller.PrepareRestore(); });
            Assert(closes == 0, "Invalid input and missing backups must not close Codex.");
            CodexDesktopRestart.CloseAndWaitOverride = delegate(CancellationToken token) { closes++; throw new TimeoutException("test close timeout"); };
            ExpectFailure(delegate { controller.ApplyAsync(new UserSettings(), "sk-test-new").GetAwaiter().GetResult(); });
            Assert(File.ReadAllText(config) == original && File.ReadAllBytes(auth).SequenceEqual(loginBytes) &&
                File.ReadAllBytes(state).SequenceEqual(stateBytes) && !Directory.Exists(data),
                "Close failure must precede all configuration writes.");
            CodexDesktopRestart.CloseAndWaitOverride = delegate(CancellationToken token) { closes++; File.WriteAllText(config, savedOnExit); };
            controller.ApplyAsync(new UserSettings(), "sk-test-new").GetAwaiter().GetResult();
            Assert(File.ReadAllText(config).Contains(AppController.OfficialRelayUrl) && closedChecks > 0, "Apply must write the relay after verifying desktop exit.");
            Assert(File.ReadAllBytes(auth).SequenceEqual(loginBytes), "Apply must preserve auth.json byte for byte.");
            Assert(File.ReadAllBytes(state).SequenceEqual(stateBytes), "Apply must preserve global state byte for byte.");
            CodexDesktopRestart.CloseAndWaitOverride = delegate(CancellationToken token) { closes++; };
            RestorePlan plan = controller.PrepareRestore();
            Assert(plan.ConflictingFiles.Length == 0, "A normal apply/restore cycle must not conflict.");
            CodexDesktopRestart.FindOverride = delegate(string path) { return new List<CodexProcessAdapter> { new CodexProcessAdapter(delegate { return false; }, delegate { return IntPtr.Zero; }, delegate { throw new Exception("Must not close in final verification."); }, null, null) }; };
            ExpectFailure(delegate { controller.CompleteRestore(plan, false); });
            Assert(File.ReadAllText(config).Contains(AppController.OfficialRelayUrl), "Restart during confirmation must block restoration.");
            CodexDesktopRestart.FindOverride = delegate(string path) { return new List<CodexProcessAdapter>(); };
            controller.CompleteRestore(plan, false);
            string restoredConfig = File.ReadAllText(config);
            Assert(restoredConfig.Contains(savedOnExit.Trim()) && restoredConfig.Contains("[model_providers.custom]") &&
                restoredConfig.Contains(AppController.OfficialRelayUrl) && File.ReadAllBytes(auth).SequenceEqual(loginBytes) &&
                File.ReadAllBytes(state).SequenceEqual(stateBytes),
                "Restore must recover the saved config fields, retain the relay provider, and preserve desktop account state.");
            Assert(!File.Exists(Path.Combine(data, "settings.json")) && !File.Exists(Path.Combine(data, "credential.bin")), "Files newly created by apply must be removed on restore.");
            Assert(closes == 3, "Apply and restore must each request closure once after validation.");
        }
        finally
        {
            CodexDesktopRestart.CloseAndWaitOverride = oldClose;
            CodexDesktopRestart.FindOverride = oldFind;
            CodexDesktopRestart.ExecutableOverride = oldExecutable;
        }
    }

    static void TestCredentialReusePreservesDesktopState(string root)
    {
        string home = Path.Combine(root, "home");
        string data = Path.Combine(root, "data");
        Directory.CreateDirectory(home);
        string auth = Path.Combine(home, "auth.json");
        string state = Path.Combine(home, ".codex-global-state.json");
        byte[] authBytes = Bytes("{\"auth_mode\":\"chatgpt\",\"account_id\":\"fixture-account\"}");
        byte[] stateBytes = Bytes("{\"locale\":\"zh-CN\",\"theme\":\"dark\"}");
        File.WriteAllBytes(auth, authBytes);
        File.WriteAllBytes(state, stateBytes);
        var controller = new AppController(data, home);
        Action<CancellationToken> oldClose = CodexDesktopRestart.CloseAndWaitOverride;
        Func<string, List<CodexProcessAdapter>> oldFind = CodexDesktopRestart.FindOverride;
        Func<string> oldExecutable = CodexDesktopRestart.ExecutableOverride;
        try
        {
            int closes = 0;
            CodexDesktopRestart.ExecutableOverride = delegate { return "fixture.exe"; };
            CodexDesktopRestart.FindOverride = delegate(string path) { return new List<CodexProcessAdapter>(); };
            CodexDesktopRestart.CloseAndWaitOverride = delegate(CancellationToken token) { closes++; };

            Assert(!controller.HasCredential, "ChatGPT auth alone must not count as a relay credential.");
            ExpectFailure(delegate { controller.ApplyAsync(new UserSettings(), "").GetAwaiter().GetResult(); });
            Assert(closes == 0 && !Directory.Exists(data),
                "An empty first-run key must fail before closing Codex or writing companion data.");

            controller.ApplyAsync(new UserSettings(), "sk-test-reusable").GetAwaiter().GetResult();
            Assert(controller.HasCredential, "A successful activation must save the relay credential.");
            controller.ApplyAsync(new UserSettings(), "").GetAwaiter().GetResult();
            Assert(closes == 2, "A repeated activation with an empty key must reuse the saved credential.");
            Assert(File.ReadAllBytes(auth).SequenceEqual(authBytes), "Repeated activation must preserve auth.json byte for byte.");
            Assert(File.ReadAllBytes(state).SequenceEqual(stateBytes), "Repeated activation must preserve global state byte for byte.");
        }
        finally
        {
            CodexDesktopRestart.CloseAndWaitOverride = oldClose;
            CodexDesktopRestart.FindOverride = oldFind;
            CodexDesktopRestart.ExecutableOverride = oldExecutable;
        }
    }

    static void TestAppearanceMigration(string root)
    {
        string data = Path.Combine(root, "data");
        string home = Path.Combine(root, "home");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(data, "settings.json"), Json.Write(new UserSettings {
            BaseUrl = AppController.OfficialRelayUrl, Model = AppController.RelayModel,
            Theme = "dracula", Skin = "qq"
        }));
        var controller = new AppController(data, home);
        AppearanceSettings migrated = controller.LoadAppearanceSettings();
        Assert(migrated.Theme == "dracula" && migrated.Skin == "qq",
            "Missing appearance-settings.json must migrate the legacy appearance values in memory.");

        File.WriteAllText(Path.Combine(data, "appearance-settings.json"), Json.Write(new AppearanceSettings {
            Theme = "codex-light", Skin = "native"
        }));
        AppearanceSettings independent = controller.LoadAppearanceSettings();
        Assert(independent.Theme == "codex-light" && independent.Skin == "native",
            "The independent appearance file must take precedence over legacy system settings.");
    }

    static void TestAppearanceBackupRollback(string root)
    {
        string data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        string systemRestore = Path.Combine(data, "latest-restore.bin");
        File.WriteAllText(systemRestore, "system-restore-fixture");
        string systemDigest = PrivateFiles.Digest(File.ReadAllBytes(systemRestore));
        string appearance = Path.Combine(root, "appearance-settings.json");
        string blocked = Path.Combine(root, "blocked-target");
        Directory.CreateDirectory(blocked);
        var changes = new List<FileChange> {
            ChangeSet.Prepare(appearance, Bytes("appearance-after")),
            ChangeSet.Prepare(blocked, Bytes("cannot-replace-a-directory"))
        };
        ExpectFailure(delegate { ChangeSet.Commit(changes, data, "latest-appearance-restore.bin"); });
        Assert(!File.Exists(appearance), "A failed appearance commit must roll back files already written.");
        Assert(!File.Exists(Path.Combine(data, "latest-appearance-restore.bin")), "A failed appearance commit must not publish a restore pointer.");
        Assert(PrivateFiles.Digest(File.ReadAllBytes(systemRestore)) == systemDigest,
            "A failed appearance commit must not modify the system restore pointer.");
    }

    static void TestSystemRestorePreservesAppearance(string root)
    {
        string home = Path.Combine(root, "home");
        string data = Path.Combine(root, "data");
        Directory.CreateDirectory(home);
        string config = Path.Combine(home, "config.toml");
        string auth = Path.Combine(home, "auth.json");
        string originalConfig = "# fixture with an existing custom provider\n[projects.'C:\\\\fixture']\ntrusted = true\n" +
            "[model_providers.custom]\nname = \"Original Custom\"\nbase_url = \"https://original.example/v1\"\nwire_api = \"responses\"\n";
        string originalAuth = "{\"OPENAI_API_KEY\":\"sk-test-old-login\",\"account_id\":\"fixture\"}";
        File.WriteAllText(config, originalConfig);
        File.WriteAllText(auth, originalAuth);
        var controller = new AppController(data, home);
        Action<CancellationToken> oldClose = CodexDesktopRestart.CloseAndWaitOverride;
        Func<string, List<CodexProcessAdapter>> oldFind = CodexDesktopRestart.FindOverride;
        Func<string> oldExecutable = CodexDesktopRestart.ExecutableOverride;
        try
        {
            int closes = 0;
            CodexDesktopRestart.ExecutableOverride = delegate { return "fixture.exe"; };
            CodexDesktopRestart.FindOverride = delegate(string path) { return new List<CodexProcessAdapter>(); };
            CodexDesktopRestart.CloseAndWaitOverride = delegate(CancellationToken token) { closes++; };

            controller.ApplyAsync(new UserSettings(), "sk-test-new").GetAwaiter().GetResult();
            string systemRestore = Path.Combine(data, "latest-restore.bin");
            string systemRestoreDigest = PrivateFiles.Digest(File.ReadAllBytes(systemRestore));
            controller.ApplyAppearanceAsync(new AppearanceSettings { Theme = "dracula", Skin = "qq" }).GetAwaiter().GetResult();
            Assert(PrivateFiles.Digest(File.ReadAllBytes(systemRestore)) == systemRestoreDigest,
                "Appearance save must not replace the system restore pointer.");
            Assert(File.Exists(Path.Combine(data, "latest-appearance-restore.bin")),
                "Appearance save must publish its own restore pointer.");

            string combined = File.ReadAllText(config);
            Assert(combined.Contains(AppController.OfficialRelayUrl) && combined.Contains("dracula"),
                "The shared config must contain both system and appearance edits before restore.");
            RestorePlan plan = controller.PrepareRestore();
            Assert(plan.ConflictingFiles.Length == 0,
                "Appearance-only edits in the shared config must not conflict with system-owned restore fields.");
            File.AppendAllText(config, "# changed during restore confirmation\n");
            ExpectFailure(delegate { controller.CompleteRestore(plan, false); });
            Assert(File.ReadAllText(config).Contains("changed during restore confirmation") &&
                PrivateFiles.Digest(File.ReadAllBytes(systemRestore)) == systemRestoreDigest,
                "A config change during confirmation must block restore without consuming its pointer.");
            plan = controller.PrepareRestore();
            Assert(plan.ConflictingFiles.Length == 0,
                "A non-system config change must remain mergeable when restore is prepared again.");
            controller.CompleteRestore(plan, false);

            string restored = File.ReadAllText(config);
            Assert(restored.Contains("dracula") && restored.Contains("appearanceTheme"),
                "System restore must preserve appearance keys written later.");
            Dictionary<string, object> restoredUserConfig = CodexConfig.ReadUserConfig(CodexInstall.Cli(), data, File.ReadAllBytes(config));
            foreach (string removed in new[] { "model_provider", "model", "model_reasoning_effort", "disable_response_storage" })
                Assert(!restoredUserConfig.ContainsKey(removed),
                    "A top-level system field missing before configuration must be deleted during restore: " + removed);
            Assert(restored.Contains("[model_providers.custom]") && restored.Contains("Original Custom") &&
                restored.Contains("https://original.example/v1") && !restored.Contains("Codex Relay"),
                "Restore must recover an existing custom provider while preserving later appearance edits.");
            Assert(File.ReadAllBytes(auth).SequenceEqual(Bytes(originalAuth)),
                "System restore must preserve auth.json byte for byte independently of appearance.");
            AppearanceSettings appearance = controller.LoadAppearanceSettings();
            Assert(appearance.Theme == "dracula" && appearance.Skin == "qq",
                "System restore must not roll back the independent appearance selection.");
            Assert(File.Exists(Path.Combine(data, "latest-appearance-restore.bin")),
                "System restore must not consume the appearance restore pointer.");
            Assert(closes == 4, "System save, appearance save and both restore preparations must request normal closure.");
        }
        finally
        {
            CodexDesktopRestart.CloseAndWaitOverride = oldClose;
            CodexDesktopRestart.FindOverride = oldFind;
            CodexDesktopRestart.ExecutableOverride = oldExecutable;
        }
    }

    static void TestLegacySystemRestorePreservesAppearance(string root)
    {
        string home = Path.Combine(root, "home");
        string data = Path.Combine(root, "data");
        Directory.CreateDirectory(home);
        PrivateFiles.EnsureDirectory(data);
        string config = Path.Combine(home, "config.toml");
        byte[] original = Bytes("# legacy restore fixture without relay fields\n");
        File.WriteAllBytes(config, original);
        string auth = Path.Combine(home, "auth.json");
        byte[] oldAuth = Bytes("{\"auth_mode\":\"chatgpt\",\"account_id\":\"old-account\"}");
        byte[] currentAuth = Bytes("{\"auth_mode\":\"chatgpt\",\"account_id\":\"current-account\"}");
        File.WriteAllBytes(auth, oldAuth);
        string cli = CodexInstall.Cli();
        byte[] relay = CodexConfig.Stage(cli, data, original, AppController.BuildRelayConfigEdits("sk-test-legacy"));
        FileChange legacyChange = ChangeSet.Prepare(config, relay);
        Assert(legacyChange.ConfigBefore == null && legacyChange.ConfigAfter == null,
            "Legacy restore fixture must not contain field ownership metadata.");
        ChangeSet.Commit(new List<FileChange> { legacyChange, ChangeSet.Prepare(auth, Bytes("{\"OPENAI_API_KEY\":\"legacy-relay\"}")) }, data);
        File.WriteAllBytes(auth, currentAuth);
        byte[] withAppearance = CodexConfig.Stage(cli, data, relay, new List<object> {
            CodexConfig.Edit("desktop.appearanceTheme", "dark"),
            CodexConfig.Edit("desktop.appearanceDarkCodeThemeId", "dracula")
        });
        File.WriteAllBytes(config, withAppearance);

        var controller = new AppController(data, home);
        Action<CancellationToken> oldClose = CodexDesktopRestart.CloseAndWaitOverride;
        Func<string, List<CodexProcessAdapter>> oldFind = CodexDesktopRestart.FindOverride;
        Func<string> oldExecutable = CodexDesktopRestart.ExecutableOverride;
        try
        {
            CodexDesktopRestart.ExecutableOverride = delegate { return "fixture.exe"; };
            CodexDesktopRestart.FindOverride = delegate(string path) { return new List<CodexProcessAdapter>(); };
            CodexDesktopRestart.CloseAndWaitOverride = delegate(CancellationToken token) { };
            RestorePlan plan = controller.PrepareRestore();
            Assert(plan.ConflictingFiles.Contains("config.toml"),
                "A changed config from a legacy full-file backup must still request overwrite confirmation.");
            controller.CompleteRestore(plan, true);
            string restored = File.ReadAllText(config);
            Assert(restored.Contains("appearanceTheme") && restored.Contains("dracula"),
                "Confirmed legacy restore must merge onto and preserve current appearance keys.");
            Dictionary<string, object> restoredUserConfig = CodexConfig.ReadUserConfig(cli, data, File.ReadAllBytes(config));
            foreach (string removed in new[] { "model_provider", "model", "model_reasoning_effort", "disable_response_storage" })
                Assert(!restoredUserConfig.ContainsKey(removed),
                    "Legacy restore must delete top-level system fields that were absent before configuration: " + removed);
            Assert(restored.Contains("[model_providers.custom]") && restored.Contains("Codex Relay"),
                "Legacy restore must retain the relay provider introduced for existing conversations.");
            Assert(File.ReadAllBytes(auth).SequenceEqual(oldAuth),
                "Confirmed legacy restore must remain compatible with backups that include auth.json.");
        }
        finally
        {
            CodexDesktopRestart.CloseAndWaitOverride = oldClose;
            CodexDesktopRestart.FindOverride = oldFind;
            CodexDesktopRestart.ExecutableOverride = oldExecutable;
        }
    }

    static byte[] Bytes(string text) { return PrivateFiles.Utf8.GetBytes(text); }
    static HashSet<string> Allowed(params string[] paths) { return new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase); }
    static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static void ExpectFailure(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; } catch (IOException) { return; } catch (InvalidOperationException) { return; } catch (ArgumentException) { return; } catch (TimeoutException) { return; }
        throw new InvalidOperationException("The operation should have been rejected.");
    }
}
