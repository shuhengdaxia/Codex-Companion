using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Restart regressions use the production orchestration with its internal
// process-close and launch delegates. They never enumerate or launch a real
// desktop process.
internal static class RestartTests
{
    public static int Run()
    {
        TestValidationDoesNotTouchProcesses();
        TestInvalidPackageDoesNotClose();
        TestCloseTimeoutDoesNotLaunch();
        TestCloseWaitProcessBoundaries();
        TestQuitMenuBoundaries();
        NativeQuitTests.Run();
        TestQuitConfirmationIsNotRepeated();
        TestBackgroundActivationBeforeQuit();
        TestStartingDesktopAndNewProcesses();
        TestLaunchVerificationFailureIsFailure();
        TestCancellationAfterClosePreventsLaunch();
        TestSuccessfulRestartVerifiesLaunchResult();
        TestServiceRestartUsesPreparedPackage();
        TestPagedCardsAndBusyBoundaries();
        TestScrolledCardsTriggerPreviewLoad();
        TestPreviewSurvivesCardRebuild();
        TestFormClosingCancelsRestart();
        TestFormDisposeCancelsRestart();
        return 0;
    }

    private static void TestValidationDoesNotTouchProcesses()
    {
        int closeCalls = 0;
        int launchCalls = 0;
        ThemeApplyResult result = RunRuntime(
            "invalid", "", delegate { closeCalls++; },
            delegate(string themeId, string css, string mode, string scope, CancellationToken token)
            { launchCalls++; return new ThemeApplyResult { Success = true, ThemeId = themeId }; });

        Assert(result != null && !result.Success, "主题输入验证失败必须返回失败。");
        Assert(closeCalls == 0 && launchCalls == 0, "主题验证失败不得关闭或启动 Codex。");
    }

    private static void TestInvalidPackageDoesNotClose()
    {
        string root = Path.Combine(Path.GetTempPath(), "midweb-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Action<CancellationToken> previousClose = CodexDesktopRestart.CloseAndWaitOverride;
        int closeCalls = 0;
        try
        {
            var transport = new RestartTransport(delegate(Uri uri)
            {
                if (uri.Query.IndexOf("page=", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var theme = new Dictionary<string, object> {
                        { "id", "broken" }, { "name", "broken" }, { "mode", "dark" },
                        { "kind", "theme" }, { "installable", true },
                        { "downloadUrl", "https://codexthemes.ai/api/themes/broken/download" }
                    };
                    return JsonResponse(new Dictionary<string, object> {
                        { "themes", new object[] { theme } }, { "total", 1 }
                    });
                }
                byte[] invalid = Utf8.GetBytes(new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                    { "format", "zip" }, { "schemaVersion", 1 },
                    { "manifest", new Dictionary<string, object> { { "id", "broken" }, { "mode", "dark" } } },
                    { "css", "body{}" }
                }));
                return new GalleryHttpResponse(200, invalid, "application/json", invalid.Length, null);
            });
            var service = new CodexThemeGalleryService(new GalleryCatalog(root, transport, Path.Combine(root, "no-bundled")));
            service.LoadThemesAsync(CancellationToken.None).GetAwaiter().GetResult();
            CodexDesktopRestart.CloseAndWaitOverride = delegate(CancellationToken token) { closeCalls++; };
            ThemeApplyResult result = service.RestartAsync("broken", CancellationToken.None).GetAwaiter().GetResult();
            Assert(result != null && !result.Success, "损坏主题包必须返回失败。");
        }
        finally
        {
            CodexDesktopRestart.CloseAndWaitOverride = previousClose;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        Assert(closeCalls == 0, "损坏主题包不得关闭 Codex。");
    }

    private static void TestCloseTimeoutDoesNotLaunch()
    {
        int launchCalls = 0;
        ThemeApplyResult result = RunRuntime(
            "theme", "body{}", delegate { throw new TimeoutException("timeout"); },
            delegate(string themeId, string css, string mode, string scope, CancellationToken token)
            { launchCalls++; return new ThemeApplyResult { Success = true, ThemeId = themeId }; });

        Assert(result != null && !result.Success, "优雅退出超时必须返回失败。");
        Assert(launchCalls == 0, "退出超时不得启动新 Codex。");
    }

    private static void TestCloseWaitProcessBoundaries()
    {
        Func<string, List<CodexProcessAdapter>> previousFind = CodexDesktopRestart.FindOverride;
        try
        {
            bool mainExited = false;
            int rendererPolls = 0;
            int mainCloseCalls = 0;
            int rendererCloseCalls = 0;
            CodexDesktopRestart.FindOverride = delegate(string executable)
            {
                return new List<CodexProcessAdapter> {
                    new CodexProcessAdapter(
                        delegate { return mainExited; },
                        delegate { return new IntPtr(1); },
                        delegate { mainCloseCalls++; mainExited = true; return true; },
                        delegate { }, delegate { }),
                    new CodexProcessAdapter(
                        delegate { rendererPolls++; return rendererPolls >= 2; },
                        delegate { return IntPtr.Zero; },
                        delegate { rendererCloseCalls++; return false; },
                        delegate { }, delegate { })
                };
            };
            CodexDesktopRestart.CloseAndWaitCore("fixture.exe", CancellationToken.None, 2000);
            Assert(mainCloseCalls == 1, "有主窗口的 Codex 主进程必须请求一次优雅退出。");
            Assert(rendererCloseCalls == 0, "无主窗口 renderer 子进程不得调用 CloseMainWindow。");

            int noWindowCloseCalls = 0;
            CodexDesktopRestart.FindOverride = delegate(string executable)
            {
                return new List<CodexProcessAdapter> {
                    new CodexProcessAdapter(
                        delegate { return false; },
                        delegate { return IntPtr.Zero; },
                        delegate { noWindowCloseCalls++; return true; },
                        delegate { }, delegate { })
                };
            };
            ExpectCloseFailure("没有可请求正常退出的主窗口", "全无主窗口的运行进程必须失败且不得关闭。");
            Assert(noWindowCloseCalls == 0, "全无主窗口时不得尝试关闭进程。");

            int refusedCloseCalls = 0;
            CodexDesktopRestart.FindOverride = delegate(string executable)
            {
                return new List<CodexProcessAdapter> {
                    new CodexProcessAdapter(
                        delegate { return false; },
                        delegate { return new IntPtr(1); },
                        delegate { refusedCloseCalls++; return false; },
                        delegate { }, delegate { })
                };
            };
            ExpectCloseFailure("无法请求官方 Codex 优雅退出", "主窗口拒绝优雅退出时必须失败。");
            Assert(refusedCloseCalls == 1, "主窗口拒绝退出时只应请求一次关闭。");
        }
        finally { CodexDesktopRestart.FindOverride = previousFind; }
    }

    private static void ExpectCloseFailure(string expectedMessage, string assertion)
    {
        try
        {
            CodexDesktopRestart.CloseAndWaitCore("fixture.exe", CancellationToken.None, 200);
            throw new InvalidOperationException(assertion);
        }
        catch (InvalidOperationException error)
        {
            Assert(error.Message.IndexOf(expectedMessage, StringComparison.Ordinal) >= 0, assertion + "错误信息不准确。");
        }
    }

    private static void TestQuitMenuBoundaries()
    {
        IntPtr window = new IntPtr(42);
        var desktop = new FakeQuitDesktop { Window = window };
        Assert(CodexDesktopQuit.RequestQuit(window, 123, desktop) && desktop.QuitCount == 1,
            "Verified Codex window must request exactly one official exit menu action.");
        foreach (var blocked in new[] {
            new FakeQuitDesktop { Window = window, WrongOwner = true },
            new FakeQuitDesktop { Window = window, Disabled = true }
        })
        {
            Assert(!CodexDesktopQuit.RequestQuit(window, 123, blocked) && blocked.QuitCount == 0,
                "Wrong owner or a modal prompt must prevent exit requests.");
        }
        Assert(!CodexDesktopQuit.RequestQuit(IntPtr.Zero, 123, new FakeQuitDesktop()), "No window must not send keys.");
        var rejected = new FakeQuitDesktop { Window = window, RejectInput = true };
        Assert(!CodexDesktopQuit.RequestQuit(window, 123, rejected), "Rejected input must not report success.");
        foreach (string label in new[] { "E&xit", "Quit Codex", "Exit ChatGPT", "退出(&X)", "退出 Codex", "結束" })
            Assert(CodexTrayMenu.IsExitLabel(label), "The official English or Chinese exit label must be recognized.");
        foreach (string label in new[] { "Exit project discussion", "Close window", "Delete", "退出后清理所有文件", "" })
            Assert(!CodexTrayMenu.IsExitLabel(label), "Non-exit menu items must never be invoked.");
    }

    private static void TestQuitConfirmationIsNotRepeated()
    {
        var previousFind = CodexDesktopRestart.FindOverride;
        try
        {
            int quitCalls = 0;
            int snapshots = 0;
            CodexDesktopRestart.FindOverride = delegate(string executable) {
                snapshots++;
                return snapshots >= 4 ? new List<CodexProcessAdapter>() : new List<CodexProcessAdapter> {
                    new CodexProcessAdapter(delegate { return false; }, delegate { return new IntPtr(snapshots); },
                        delegate { quitCalls++; return true; }, null, null, 123)
                };
            };
            CodexDesktopRestart.CloseAndWaitCore("fixture.exe", CancellationToken.None, 2000);
            Assert(quitCalls == 1, "A new confirmation window in the same process must not receive another quit shortcut.");
            quitCalls = 0;
            CodexDesktopRestart.FindOverride = delegate(string executable) {
                return new List<CodexProcessAdapter> {
                    new CodexProcessAdapter(delegate { return false; }, delegate { return new IntPtr(42); },
                        delegate { quitCalls++; return true; }, null, null, 123)
                };
            };
            try
            {
                CodexDesktopRestart.CloseAndWaitCore("fixture.exe", CancellationToken.None, 250);
                throw new InvalidOperationException("Cancelled or unanswered quit must not allow configuration writes.");
            }
            catch (TimeoutException) { Assert(quitCalls == 1, "Do not force or retry a cancelled quit."); }
        }
        finally { CodexDesktopRestart.FindOverride = previousFind; }
    }

    private sealed class FakeQuitDesktop : ICodexQuitDesktop
    {
        internal IntPtr Window;
        internal bool WrongOwner, Disabled, RejectInput;
        internal int QuitCount;
        public bool IsExpectedWindow(IntPtr window, int processId) { return !WrongOwner && window == Window && processId == 123; }
        public bool IsEnabled(IntPtr window) { return !Disabled; }
        public bool RequestExitMenu(IntPtr window, int processId) { QuitCount++; return !RejectInput; }
    }

    private static void TestStartingDesktopAndNewProcesses()
    {
        var previousFind = CodexDesktopRestart.FindOverride;
        try
        {
            int polls = 0;
            int closeCalls = 0;
            bool exited = false;
            CodexDesktopRestart.FindOverride = delegate(string executable)
            {
                polls++;
                if (exited) return new List<CodexProcessAdapter>();
                return new List<CodexProcessAdapter> { new CodexProcessAdapter(
                    delegate { return exited; }, delegate { return polls >= 3 ? new IntPtr(3) : IntPtr.Zero; },
                    delegate { closeCalls++; exited = true; return true; }, null, null) };
            };
            CodexDesktopRestart.CloseAndWaitCore("fixture.exe", CancellationToken.None, 2000);
            Assert(closeCalls == 1 && polls >= 4, "启动中的窗口必须等出现后关闭并再次确认进程退出。");

            polls = 0;
            exited = false;
            closeCalls = 0;
            CodexDesktopRestart.FindOverride = delegate(string executable)
            {
                polls++;
                if (polls == 1 || exited) return new List<CodexProcessAdapter>();
                return new List<CodexProcessAdapter> { new CodexProcessAdapter(
                    delegate { return exited; }, delegate { return new IntPtr(4); },
                    delegate { closeCalls++; exited = true; return true; }, null, null) };
            };
            CodexDesktopRestart.CloseAndWaitCore("fixture.exe", CancellationToken.None, 2000);
            Assert(closeCalls == 1, "首次扫描后出现的 Codex 进程也必须等待退出。");

            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                int previousPolls = polls;
                try { CodexDesktopRestart.CloseAndWaitCore("fixture.exe", cancellation.Token, 2000); throw new Exception("Expected cancellation."); }
                catch (OperationCanceledException) { }
                Assert(polls == previousPolls, "取消操作后不得查询或关闭进程。");
            }
        }
        finally { CodexDesktopRestart.FindOverride = previousFind; }
    }

    private static void TestBackgroundActivationBeforeQuit()
    {
        Func<string, List<CodexProcessAdapter>> previousFind = CodexDesktopRestart.FindOverride;
        Action<string> previousActivate = CodexDesktopRestart.ActivateOverride;
        try
        {
            bool activated = false;
            bool exited = false;
            int activationCalls = 0;
            int closeCalls = 0;
            CodexDesktopRestart.ActivateOverride = delegate(string executable)
            { activationCalls++; activated = true; };
            CodexDesktopRestart.FindOverride = delegate(string executable)
            {
                if (exited) return new List<CodexProcessAdapter>();
                return new List<CodexProcessAdapter> { new CodexProcessAdapter(
                    delegate { return exited; }, delegate { return activated ? new IntPtr(41) : IntPtr.Zero; },
                    delegate { closeCalls++; exited = true; return true; }, null, null, 401) };
            };
            CodexDesktopRestart.CloseAndWaitCore("fixture.exe", CancellationToken.None, 2000);
            Assert(activationCalls == 1, "后台运行且没有主窗口时必须只激活一次官方 Codex。");
            Assert(closeCalls == 1, "激活后出现的主窗口必须请求一次正常退出。");
        }
        finally
        {
            CodexDesktopRestart.FindOverride = previousFind;
            CodexDesktopRestart.ActivateOverride = previousActivate;
        }
    }

    private static void TestLaunchVerificationFailureIsFailure()
    {
        int launchCalls = 0;
        ThemeApplyResult result = RunRuntime(
            "theme", "body{}", delegate { },
            delegate(string themeId, string css, string mode, string scope, CancellationToken token)
            {
                launchCalls++;
                return new ThemeApplyResult { Success = false, ThemeId = themeId, Message = "verify failed" };
            });

        Assert(result != null && !result.Success, "启动后主题验证失败不得伪造成功。");
        Assert(launchCalls == 1, "退出成功后必须执行一次启动与主题验证。");
    }

    private static void TestCancellationAfterClosePreventsLaunch()
    {
        using (var cancellation = new CancellationTokenSource())
        {
            int launchCalls = 0;
            ThemeApplyResult result = RunRuntime(
                "theme", "body{}", delegate { cancellation.Cancel(); },
                delegate(string themeId, string css, string mode, string scope, CancellationToken token)
                { launchCalls++; return new ThemeApplyResult { Success = true, ThemeId = themeId }; }, cancellation.Token);

            Assert(result != null && !result.Success, "关闭后取消必须返回失败。");
            Assert(launchCalls == 0, "关闭完成后取消不得晚启动 Codex。");
        }
    }

    private static void TestSuccessfulRestartVerifiesLaunchResult()
    {
        var sequence = new List<string>();
        ThemeApplyResult result = RunRuntime(
            "theme", "body{}", delegate { sequence.Add("close"); },
            delegate(string themeId, string css, string mode, string scope, CancellationToken token)
            {
                sequence.Add("launch-verify");
                return new ThemeApplyResult { Success = true, ThemeId = themeId };
            });

        Assert(result != null && result.Success, "关闭并验证成功后应返回成功。");
        Assert(sequence.Count == 2 && sequence[0] == "close" && sequence[1] == "launch-verify",
            "重启调用顺序必须是关闭后启动并验证。");
    }

    private static void TestServiceRestartUsesPreparedPackage()
    {
        string root = Path.Combine(Path.GetTempPath(), "midweb-service-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Action<CancellationToken> previousClose = CodexDesktopRestart.CloseAndWaitOverride;
        Func<string, string, string, string, CancellationToken, ThemeApplyResult> previousLaunch = ThemeRuntime.LaunchWithDebuggingOverride;
        string preparedCss = null;
        string preparedMode = null;
        try
        {
            byte[] package = Utf8.GetBytes(new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                { "format", "codex-theme" }, { "schemaVersion", 1 },
                { "manifest", new Dictionary<string, object> { { "id", "service-theme" }, { "mode", "dark" } } },
                { "css", "body{--service-prepared:1;}" }
            }));
            var transport = new RestartTransport(delegate(Uri uri)
            {
                if (uri.Query.IndexOf("page=", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var theme = new Dictionary<string, object> {
                        { "id", "service-theme" }, { "name", "service-theme" }, { "mode", "dark" },
                        { "kind", "theme" }, { "installable", true },
                        { "downloadUrl", "https://codexthemes.ai/api/themes/service-theme/download" }
                    };
                    return JsonResponse(new Dictionary<string, object> { { "themes", new object[] { theme } }, { "total", 1 } });
                }
                return new GalleryHttpResponse(200, package, "application/json", package.Length, null);
            });
            var service = new CodexThemeGalleryService(new GalleryCatalog(root, transport, Path.Combine(root, "no-bundled")));
            service.LoadThemesAsync(CancellationToken.None).GetAwaiter().GetResult();
            CodexDesktopRestart.CloseAndWaitOverride = delegate(CancellationToken token) { };
            ThemeRuntime.LaunchWithDebuggingOverride = delegate(string themeId, string css, string mode, string scope, CancellationToken token)
            {
                preparedCss = css;
                preparedMode = mode;
                return new ThemeApplyResult { Success = true, ThemeId = themeId };
            };
            ThemeApplyResult result = service.RestartAsync("service-theme", CancellationToken.None).GetAwaiter().GetResult();
            Assert(result != null && result.Success, "服务重启应在包解析和准备成功后返回成功。");
            Assert(preparedCss == "body{--service-prepared:1;}" && preparedMode == "dark",
                "服务重启必须把严格解析后的 AppliedCss 和 mode 交给真实重启编排。");
        }
        finally
        {
            CodexDesktopRestart.CloseAndWaitOverride = previousClose;
            ThemeRuntime.LaunchWithDebuggingOverride = previousLaunch;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static ThemeApplyResult RunRuntime(
        string themeId, string css, Action close,
        Func<string, string, string, string, CancellationToken, ThemeApplyResult> launch)
    { return RunRuntime(themeId, css, close, launch, CancellationToken.None); }

    private static ThemeApplyResult RunRuntime(
        string themeId, string css, Action close,
        Func<string, string, string, string, CancellationToken, ThemeApplyResult> launch,
        CancellationToken cancellationToken)
    {
        Action<CancellationToken> previousClose = CodexDesktopRestart.CloseAndWaitOverride;
        Func<string, string, string, string, CancellationToken, ThemeApplyResult> previousLaunch = ThemeRuntime.LaunchWithDebuggingOverride;
        CodexDesktopRestart.CloseAndWaitOverride = delegate(CancellationToken token) { close(); };
        ThemeRuntime.LaunchWithDebuggingOverride = launch;
        try { return ThemeRuntime.RestartWithDebugging(themeId, css, "dark", "home", cancellationToken); }
        finally
        {
            CodexDesktopRestart.CloseAndWaitOverride = previousClose;
            ThemeRuntime.LaunchWithDebuggingOverride = previousLaunch;
        }
    }

    private static void TestFormClosingCancelsRestart()
    {
        using (var cancellation = new CancellationTokenSource())
        using (var form = new ThemeGalleryForm(new FakeGalleryService(), false))
        {
            SetPrivateField(form, "restartCancellation", cancellation);
            SetPrivateField(form, "actionBusy", true);
            FormClosingEventArgs args = new FormClosingEventArgs(CloseReason.UserClosing, false);
            MethodInfo handler = typeof(ThemeGalleryForm).GetMethod(
                "ThemeGalleryFormClosing", BindingFlags.Instance | BindingFlags.NonPublic);
            if (handler == null) throw new InvalidOperationException("主题窗口缺少关闭处理器。");
            handler.Invoke(form, new object[] { form, args });
            Assert(args.Cancel, "关闭进行中的主题重启必须被取消。");
            Assert(cancellation.IsCancellationRequested, "关闭窗口必须取消重启令牌。");
        }
    }

    private static void TestPagedCardsAndBusyBoundaries()
    {
        var items = new List<ThemeGalleryItem>();
        for (int index = 0; index < 25; index++)
            items.Add(new ThemeGalleryItem { Id = "theme-" + index.ToString(), Name = "Theme " + index.ToString(), Installable = true });
        using (var form = new ThemeGalleryForm(new PaginationGalleryService(items), false))
        {
            form.SelectGalleryTab();
            FlowLayoutPanel cards = (FlowLayoutPanel)GetPrivateField(form, "cardsPanel");
            Label page = (Label)GetPrivateField(form, "pageLabel");
            Button next = (Button)GetPrivateField(form, "nextPageButton");
            Button previous = (Button)GetPrivateField(form, "previousPageButton");
            Assert(cards.Controls.Count <= 12 && page.Text.IndexOf("第 1/3 页（25项）", StringComparison.Ordinal) >= 0,
                "分页首屏必须最多创建 12 张卡片并显示总页数。");
            InvokePrivate(form, "NextPage", (object)null, EventArgs.Empty);
            InvokePrivate(form, "NextPage", (object)null, EventArgs.Empty);
            Assert(page.Text.IndexOf("第 3/3 页（25项）", StringComparison.Ordinal) >= 0 && !next.Enabled,
                "下一页必须停在最后一页并禁用按钮。");
            InvokePrivate(form, "NextPage", (object)null, EventArgs.Empty);
            Assert(page.Text.IndexOf("第 3/3 页（25项）", StringComparison.Ordinal) >= 0,
                "最后一页再次下一页不得越界。");
            InvokePrivate(form, "PreviousPage", (object)null, EventArgs.Empty);
            Assert(page.Text.IndexOf("第 2/3 页（25项）", StringComparison.Ordinal) >= 0 && previous.Enabled,
                "上一页必须返回相邻页并保持启用状态。");
            TextBox search = (TextBox)GetPrivateField(form, "searchBox");
            search.Text = "Theme 24";
            InvokePrivate(form, "RefreshCards");
            Assert(cards.Controls.Count == 1 && page.Text.IndexOf("第 1/1 页（1项）", StringComparison.Ordinal) >= 0,
                "筛选后必须回到第一页且只创建匹配卡片。");
            InvokePrivate(form, "SetActionBusy", true, "busy");
            Assert(!next.Enabled && !previous.Enabled, "busy 操作期间分页按钮必须禁用。");
            InvokePrivate(form, "SetActionBusy", false, "done");
        }
    }

    private static void TestScrolledCardsTriggerPreviewLoad()
    {
        var items = new List<ThemeGalleryItem>();
        for (int index = 0; index < 12; index++)
            items.Add(new ThemeGalleryItem { Id = "scroll-" + index.ToString(), Name = "Scroll " + index.ToString(), Installable = true });
        var service = new ScrollingPreviewGalleryService(items);
        using (var form = new ThemeGalleryForm(service, false))
        {
            form.SelectGalleryTab();
            FlowLayoutPanel cards = (FlowLayoutPanel)GetPrivateField(form, "cardsPanel");
            form.CreateControl();
            cards.CreateControl();
            form.ShowInTaskbar = false;
            form.Opacity = 0;
            form.Show();
            Application.DoEvents();
            InvokePrivate(form, "EnsureVisiblePreviews");
            Assert(service.PreviewIds.Count > 0 && service.PreviewIds.Count < items.Count,
                "首屏预览测试必须只加载当前可见的部分卡片。");
            var firstScreenIds = new HashSet<string>(service.PreviewIds, StringComparer.Ordinal);
            Assert(cards.DisplayRectangle.Height > cards.ClientSize.Height,
                "四行卡片的内容高度必须进入滚动范围：min=" + cards.AutoScrollMinSize.Height.ToString() + ", client=" + cards.ClientSize.Height.ToString() + ", display=" + cards.DisplayRectangle.Height.ToString());

            cards.AutoScrollPosition = new Point(0, 10000);
            cards.PerformLayout();
            Application.DoEvents();
            InvokePrivate(form, "EnsureVisiblePreviews");
            Assert(cards.VerticalScroll.Value > 0, "滚动到底部必须实际改变垂直滚动位置：value=" + cards.VerticalScroll.Value.ToString() + ", max=" + cards.VerticalScroll.Maximum.ToString());
            Assert(service.PreviewIds.Count > firstScreenIds.Count,
                "滚动到首屏之外后，可见卡片必须触发预览加载。");
            bool foundNewVisibleCard = false;
            foreach (Control card in cards.Controls)
            {
                if (!card.Bounds.IntersectsWith(cards.ClientRectangle)) continue;
                FieldInfo itemField = card.GetType().GetField("Item", BindingFlags.Instance | BindingFlags.Public);
                ThemeGalleryItem item = itemField == null ? null : itemField.GetValue(card) as ThemeGalleryItem;
                if (item == null || firstScreenIds.Contains(item.Id)) continue;
                foundNewVisibleCard = true;
                Assert(service.PreviewIds.Contains(item.Id), "滚动后的可见卡片没有请求预览：" + item.Id);
            }
            Assert(foundNewVisibleCard, "滚动后没有识别到首屏之外的可见卡片。");
        }
    }

    private static void TestPreviewSurvivesCardRebuild()
    {
        var items = new List<ThemeGalleryItem>();
        for (int index = 0; index < 13; index++)
            items.Add(new ThemeGalleryItem { Id = "rebuild-" + index.ToString(), Name = "Rebuild " + index.ToString(), Installable = true });
        var service = new ImagePreviewGalleryService(items);
        using (var form = new ThemeGalleryForm(service, false))
        {
            form.SelectGalleryTab();
            form.ShowInTaskbar = false;
            form.Opacity = 0;
            form.Show();
            FlowLayoutPanel cards = (FlowLayoutPanel)GetPrivateField(form, "cardsPanel");
            WaitFor(delegate { return cards.Controls.Count == 12 && FirstCardHasImage(cards); },
                "首屏真实 PNG 预览未显示。");
            Control firstCard = cards.Controls[0];
            PropertyInfo previewBytes = firstCard.GetType().GetProperty("PreviewBytes", BindingFlags.Instance | BindingFlags.Public);
            Assert(previewBytes != null && previewBytes.GetValue(firstCard, null) != null,
                "卡片必须在当前页保留详情预览数据。");

            InvokePrivate(form, "NextPage", (object)null, EventArgs.Empty);
            WaitFor(delegate { return cards.Controls.Count == 1; }, "未进入图库第二页。");
            Assert(previewBytes.GetValue(firstCard, null) == null,
                "离页卡片销毁后仍持有原始预览数据。");
            InvokePrivate(form, "PreviousPage", (object)null, EventArgs.Empty);
            WaitFor(delegate { return cards.Controls.Count == 12 && FirstCardHasImage(cards); },
                "翻页返回后重建的卡片丢失预览图。");

            TextBox search = (TextBox)GetPrivateField(form, "searchBox");
            search.Text = "Rebuild 0";
            InvokePrivate(form, "RefreshCards");
            WaitFor(delegate { return cards.Controls.Count == 1 && FirstCardHasImage(cards); },
                "搜索重建卡片后丢失预览图。");
        }
    }

    private static bool FirstCardHasImage(FlowLayoutPanel cards)
    {
        if (cards == null || cards.Controls.Count == 0) return false;
        foreach (Control child in cards.Controls[0].Controls)
        {
            PictureBox picture = child as PictureBox;
            if (picture != null) return picture.Image != null;
        }
        return false;
    }

    private static void WaitFor(Func<bool> condition, string failure)
    {
        int deadline = Environment.TickCount + 3000;
        while (!condition() && Environment.TickCount - deadline < 0)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        Application.DoEvents();
        Assert(condition(), failure);
    }

    private static void TestFormDisposeCancelsRestart()
    {
        var form = new ThemeGalleryForm(new FakeGalleryService(), false);
        var cancellation = new CancellationTokenSource();
        SetPrivateField(form, "restartCancellation", cancellation);
        SetPrivateField(form, "actionBusy", true);
        form.Dispose();
        Assert(cancellation.IsCancellationRequested, "窗口销毁时必须取消进行中的主题重启。");
        cancellation.Dispose();
    }

    private static void SetPrivateField(object target, string name, object value)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null) throw new InvalidOperationException("缺少私有字段：" + name);
        field.SetValue(target, value);
    }

    private static object GetPrivateField(object target, string name)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null) throw new InvalidOperationException("缺少私有字段：" + name);
        return field.GetValue(target);
    }

    private static void InvokePrivate(object target, string name, params object[] args)
    {
        MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (method == null) throw new InvalidOperationException("缺少私有方法：" + name);
        method.Invoke(target, args);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeGalleryService : IThemeGalleryService
    {
        public Task<IList<ThemeGalleryItem>> LoadThemesAsync(CancellationToken cancellationToken)
        { return Task.FromResult<IList<ThemeGalleryItem>>(new List<ThemeGalleryItem>()); }
        public Task<ThemePreviewResult> LoadPreviewAsync(string themeId, CancellationToken cancellationToken)
        { return Task.FromResult(new ThemePreviewResult()); }
        public string GetCurrentThemeId() { return null; }
        public Task<ThemeApplyResult> ApplyAsync(string themeId) { return Task.FromResult(new ThemeApplyResult()); }
        public Task<ThemeApplyResult> LaunchAsync(string themeId) { return Task.FromResult(new ThemeApplyResult()); }
        public Task<ThemeApplyResult> RestartAsync(string themeId, CancellationToken cancellationToken)
        { return Task.FromResult(new ThemeApplyResult { Success = false, ThemeId = themeId }); }
        public Task<ThemeApplyResult> RestoreAsync() { return Task.FromResult(new ThemeApplyResult()); }
    }

    private sealed class PaginationGalleryService : IThemeGalleryService
    {
        private readonly IList<ThemeGalleryItem> items;
        public PaginationGalleryService(IList<ThemeGalleryItem> items) { this.items = items; }
        public Task<IList<ThemeGalleryItem>> LoadThemesAsync(CancellationToken cancellationToken)
        { return Task.FromResult(items); }
        public Task<ThemePreviewResult> LoadPreviewAsync(string themeId, CancellationToken cancellationToken)
        { return Task.FromResult(new ThemePreviewResult { Success = false, State = "Fixture" }); }
        public string GetCurrentThemeId() { return null; }
        public Task<ThemeApplyResult> ApplyAsync(string themeId) { return Task.FromResult(new ThemeApplyResult()); }
        public Task<ThemeApplyResult> LaunchAsync(string themeId) { return Task.FromResult(new ThemeApplyResult()); }
        public Task<ThemeApplyResult> RestartAsync(string themeId, CancellationToken cancellationToken)
        { return Task.FromResult(new ThemeApplyResult()); }
        public Task<ThemeApplyResult> RestoreAsync() { return Task.FromResult(new ThemeApplyResult()); }
    }

    private sealed class ScrollingPreviewGalleryService : IThemeGalleryService
    {
        private readonly IList<ThemeGalleryItem> items;
        public readonly HashSet<string> PreviewIds = new HashSet<string>(StringComparer.Ordinal);

        public ScrollingPreviewGalleryService(IList<ThemeGalleryItem> items) { this.items = items; }
        public Task<IList<ThemeGalleryItem>> LoadThemesAsync(CancellationToken cancellationToken)
        { return Task.FromResult(items); }
        public Task<ThemePreviewResult> LoadPreviewAsync(string themeId, CancellationToken cancellationToken)
        {
            PreviewIds.Add(themeId);
            return Task.FromResult(new ThemePreviewResult { Success = false, State = "Fixture", Error = "fixture" });
        }
        public string GetCurrentThemeId() { return null; }
        public Task<ThemeApplyResult> ApplyAsync(string themeId) { return Task.FromResult(new ThemeApplyResult()); }
        public Task<ThemeApplyResult> LaunchAsync(string themeId) { return Task.FromResult(new ThemeApplyResult()); }
        public Task<ThemeApplyResult> RestartAsync(string themeId, CancellationToken cancellationToken)
        { return Task.FromResult(new ThemeApplyResult()); }
        public Task<ThemeApplyResult> RestoreAsync() { return Task.FromResult(new ThemeApplyResult()); }
    }

    private sealed class ImagePreviewGalleryService : IThemeGalleryService
    {
        private readonly IList<ThemeGalleryItem> items;
        private readonly byte[] png;

        public ImagePreviewGalleryService(IList<ThemeGalleryItem> items)
        {
            this.items = items;
            using (var bitmap = new Bitmap(2, 2))
            using (var stream = new MemoryStream())
            {
                bitmap.SetPixel(0, 0, Color.Blue);
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                png = stream.ToArray();
            }
        }

        public Task<IList<ThemeGalleryItem>> LoadThemesAsync(CancellationToken cancellationToken)
        { return Task.FromResult(items); }
        public Task<ThemePreviewResult> LoadPreviewAsync(string themeId, CancellationToken cancellationToken)
        { return Task.FromResult(new ThemePreviewResult { Success = true, State = "Available", Bytes = png }); }
        public string GetCurrentThemeId() { return null; }
        public Task<ThemeApplyResult> ApplyAsync(string themeId) { return Task.FromResult(new ThemeApplyResult()); }
        public Task<ThemeApplyResult> LaunchAsync(string themeId) { return Task.FromResult(new ThemeApplyResult()); }
        public Task<ThemeApplyResult> RestartAsync(string themeId, CancellationToken cancellationToken)
        { return Task.FromResult(new ThemeApplyResult()); }
        public Task<ThemeApplyResult> RestoreAsync() { return Task.FromResult(new ThemeApplyResult()); }
    }

    private static GalleryHttpResponse JsonResponse(Dictionary<string, object> value)
    {
        byte[] bytes = Utf8.GetBytes(new JavaScriptSerializer().Serialize(value));
        return new GalleryHttpResponse(200, bytes, "application/json", bytes.Length, null);
    }

    private static readonly System.Text.UTF8Encoding Utf8 = new System.Text.UTF8Encoding(false, true);

    private sealed class RestartTransport : IGalleryTransport
    {
        private readonly Func<Uri, GalleryHttpResponse> handler;
        public RestartTransport(Func<Uri, GalleryHttpResponse> handler) { this.handler = handler; }
        public Task<GalleryHttpResponse> GetAsync(Uri uri, int maxBytes, CancellationToken cancellationToken)
        { return Task.FromResult(handler(uri)); }
    }
}
