using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

public static class GalleryTests
{
    static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

    public static int Run(string root)
    {
        return Run(root, false);
    }

    public static int Run(string root, bool live)
    {
        Directory.CreateDirectory(root);
        var results = new List<string>();
        bool failed = false;
        try { TestPaginationAndOffline(Path.Combine(root, "pagination")); results.Add("pagination: ok"); }
        catch (Exception error) { failed = true; results.Add("pagination: " + error.Message); }
        try { TestOfflineDirectoryUsesStaticPackageStatus(Path.Combine(root, "static-status")); results.Add("static-status: ok"); }
        catch (Exception error) { failed = true; results.Add("static-status: " + error.Message); }
        try { TestGalleryServiceCache(Path.Combine(root, "service-cache")); results.Add("service-cache: ok"); }
        catch (Exception error) { failed = true; results.Add("service-cache: " + error.Message); }
        try { TestGalleryServiceRunsCatalogWorkOffCallerThread(Path.Combine(root, "service-background")); results.Add("service-background: ok"); }
        catch (Exception error) { failed = true; results.Add("service-background: " + error.Message); }
        try { TestOfflineStatusHint(Path.Combine(root, "status-hint")); results.Add("status-hint: ok"); }
        catch (Exception error) { failed = true; results.Add("status-hint: " + error.Message); }
        try { TestMissingBundleFallsBackOnline(Path.Combine(root, "missing-fallback")); results.Add("missing-fallback: ok"); }
        catch (Exception error) { failed = true; results.Add("missing-fallback: " + error.Message); }
        try { TestPackageValidation(); results.Add("package: ok"); }
        catch (Exception error) { failed = true; results.Add("package: " + error.Message); }
        try { TestOfflineRepairHashMismatch(Path.Combine(root, "repair-mismatch")); results.Add("offline-repair: ok"); }
        catch (Exception error) { failed = true; results.Add("offline-repair: " + error.Message); }
        try { TestLargeInlineArt(); results.Add("large-art: ok"); }
        catch (Exception error) { failed = true; results.Add("large-art: " + error.Message); }
        try { TestLargeCustomPropertyArt(); results.Add("large-art-var: ok"); }
        catch (Exception error) { failed = true; results.Add("large-art-var: " + error.Message); }
        try { TestPreviewAndDamagedCache(Path.Combine(root, "preview")); results.Add("preview-cache: ok"); }
        catch (Exception error) { failed = true; results.Add("preview-cache: " + error.Message); }
        if (live)
        {
            try { results.Add("live: " + TestLive(Path.Combine(root, "live"))); }
            catch (Exception error) { failed = true; results.Add("live: " + error.Message); }
        }
        File.WriteAllLines(Path.Combine(root, "gallery-results.txt"), results.ToArray(), Utf8);
        return failed ? 1 : 0;
    }

    static void TestPaginationAndOffline(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var transport = new FakeTransport(delegate(Uri uri)
        {
            int page = uri.Query.IndexOf("page=2", StringComparison.Ordinal) >= 0 ? 2 : 1;
            var rows = new List<Dictionary<string, object>>();
            if (page == 1) { rows.Add(Theme("alpha", true)); rows.Add(Theme("archive", false)); }
            else rows.Add(Theme("beta", true));
            return Ok(JsonObject(new Dictionary<string, object> { { "themes", rows.ToArray() }, { "total", 3 } }));
        });
        GalleryCatalog catalog = new GalleryCatalog(root, transport);
        IList<GalleryTheme> themes = catalog.LoadAllAsync().GetAwaiter().GetResult();
        Assert(themes.Count == 3, "分页累计条目数量不正确。");
        Assert(themes.Any(x => x.Id == "archive" && !x.Installable), "不可安装条目被丢失。");
        Assert(transport.Requests.Count == 2, "分页请求数量不正确。");

        GalleryCatalog offline = new GalleryCatalog(root, new FakeTransport(delegate(Uri uri) { throw new WebException("offline"); }));
        IList<GalleryTheme> cached = offline.LoadAllAsync().GetAwaiter().GetResult();
        Assert(cached.Count == 3 && cached.Any(x => x.Id == "archive"), "离线缓存没有保留可用目录。");
    }

    static void TestPackageValidation()
    {
        byte[] png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0 };
        string encoded = Convert.ToBase64String(png);
        string css = "/* url(https://example.invalid/comment.css) */ body{background:url(\"art.png\")}";
        byte[] package = JsonBytes(new Dictionary<string, object> {
            { "format", "codex-theme" }, { "schemaVersion", 1 },
            { "manifest", new Dictionary<string, object> { { "id", "alpha" }, { "mode", "dark" } } },
            { "css", css }, { "art", new Dictionary<string, object> { { "filename", "art.png" }, { "mimeType", "image/png" }, { "base64", encoded } } }
        });
        CodexThemePackage parsed = GalleryCatalog.ParsePackage(package, "alpha");
        Assert(parsed.AppliedCss.IndexOf("data:image/png;base64,", StringComparison.Ordinal) >= 0, "art 没有内联到 CSS。");
        Assert(parsed.AppliedCss.IndexOf("url(\"art.png\")", StringComparison.Ordinal) < 0, "art 文件名仍作为外部 CSS 资源保留。");

        byte[] escapedLegal = JsonBytes(new Dictionary<string, object> {
            { "format", "codex-theme" }, { "schemaVersion", 1 },
            { "manifest", new Dictionary<string, object> { { "id", "escaped-legal" } } },
            { "css", ".\\2a{content:\"\\4e2d\\6587\"}" }
        });
        Assert(GalleryCatalog.ParsePackage(escapedLegal, "escaped-legal") != null, "合法 CSS 选择器或 Unicode 转义被错误拒绝。");

        ExpectPackageFailure(package, "beta", "ID 不匹配");
        ExpectPackageFailure(JsonBytes(new Dictionary<string, object> { { "format", "zip" }, { "schemaVersion", 1 }, { "manifest", new Dictionary<string, object> { { "id", "alpha" } } }, { "css", "" } }), "alpha", "format");
        ExpectPackageFailure(JsonBytes(new Dictionary<string, object> { { "format", "codex-theme" }, { "schemaVersion", 2 }, { "manifest", new Dictionary<string, object> { { "id", "alpha" } } }, { "css", "" } }), "alpha", "schemaVersion");
        string[] badCss = new[] { "@import url(https://evil.invalid/a.css);", "@\\69mport url(https://evil.invalid/a.css);", "a{background:url(https://evil.invalid/a.png)}", "a{background:url(../art.png)}", "a{background:u\\72l(https://evil.invalid/a.png)}", "a{background:image-set(\"https://evil.invalid/a.png\" 1x)}", "a{background:-webkit-image-set(\"https://evil.invalid/a.png\" 1x)}" };
        foreach (string value in badCss)
        {
            byte[] bad = JsonBytes(new Dictionary<string, object> { { "format", "codex-theme" }, { "schemaVersion", 1 }, { "manifest", new Dictionary<string, object> { { "id", "alpha" } } }, { "css", value } });
            ExpectPackageFailure(bad, "alpha", "禁止 CSS");
        }
        byte[] escapedArt = JsonBytes(new Dictionary<string, object> {
            { "format", "codex-theme" }, { "schemaVersion", 1 },
            { "manifest", new Dictionary<string, object> { { "id", "escaped-art" } } },
            { "css", "body{background:u\\72l(\"art.png\")}" },
            { "art", new Dictionary<string, object> { { "filename", "art.png" }, { "mimeType", "image/png" }, { "base64", encoded } } }
        });
        ExpectPackageFailure(escapedArt, "escaped-art", "转义 art 外链");
        byte[] traversal = JsonBytes(new Dictionary<string, object> { { "format", "codex-theme" }, { "schemaVersion", 1 }, { "manifest", new Dictionary<string, object> { { "id", "alpha" } } }, { "css", "" }, { "art", new Dictionary<string, object> { { "filename", "..\\secret.png" }, { "mimeType", "image/png" }, { "base64", encoded } } } });
        ExpectPackageFailure(traversal, "alpha", "路径");
        byte[] mismatch = JsonBytes(new Dictionary<string, object> { { "format", "codex-theme" }, { "schemaVersion", 1 }, { "manifest", new Dictionary<string, object> { { "id", "alpha" } } }, { "css", "" }, { "art", new Dictionary<string, object> { { "filename", "art.png" }, { "mimeType", "image/jpeg" }, { "base64", encoded } } } });
        ExpectPackageFailure(mismatch, "alpha", "MIME");
    }

    static void TestOfflineDirectoryUsesStaticPackageStatus(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        string id = "static-status";
        string downloadUrl = "https://codexthemes.ai/api/themes/" + id + "/download";
        byte[] damaged = new byte[] { 1, 2, 3, 4 };
        string packageFile = "package-" + Digest(downloadUrl) + ".codex-theme";
        File.WriteAllBytes(Path.Combine(root, packageFile), damaged);
        var theme = Theme(id, true);
        theme["offlinePackage"] = new Dictionary<string, object> {
            { "status", "ready" }, { "file", packageFile }, { "sha256", Digest(damaged) },
            { "bytes", damaged.Length }, { "manifestId", id }
        };
        File.WriteAllText(Path.Combine(root, "catalog-1-50.json"), JsonObject(new Dictionary<string, object> {
            { "themes", new object[] { theme } }, { "total", 1 }
        }), Utf8);
        File.WriteAllText(Path.Combine(root, "bundle-status.json"), JsonObject(new Dictionary<string, object> {
            { "schemaVersion", 1 },
            { "packages", new object[] { new Dictionary<string, object> {
                { "galleryId", id }, { "sourceUrl", downloadUrl }, { "file", packageFile },
                { "sha256", Digest(damaged) }, { "bytes", damaged.Length }, { "status", "ready" },
                { "manifestId", id }
            } } }
        }), Utf8);
        var transport = new FakeTransport(delegate(Uri uri) { throw new WebException("offline transport must not be used"); });
        GalleryCatalog catalog = new GalleryCatalog(Path.Combine(root, "cache"), transport, root);
        IList<GalleryTheme> loaded = catalog.LoadAllAsync(CancellationToken.None).GetAwaiter().GetResult();
        GalleryTheme loadedTheme = loaded.Single();
        Assert(loadedTheme.OfflinePackageStatus == "ready", "目录加载应使用静态包状态，不应读取或解析全部包。");
        Assert(transport.Requests.Count == 0, "离线目录加载不应触发 HTTP 请求。");
        try
        {
            catalog.DownloadAsync(loadedTheme, CancellationToken.None).GetAwaiter().GetResult();
            throw new InvalidOperationException("应用前损坏主题包错误地返回成功。");
        }
        catch (GalleryPackageValidationException) { }
        catch (InvalidDataException) { }
    }

    static void TestGalleryServiceCache(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        string catalogPath = Path.Combine(root, "catalog-1-50.json");
        File.WriteAllText(catalogPath, JsonObject(new Dictionary<string, object> {
            { "themes", new object[] { Theme("alpha", false) } }, { "total", 1 }
        }), Utf8);
        CodexThemeGalleryService service = new CodexThemeGalleryService(new GalleryCatalog(Path.Combine(root, "cache"), null, root));
        IList<ThemeGalleryItem> first = service.LoadThemesAsync(CancellationToken.None).GetAwaiter().GetResult();
        Assert(first.Count == 1 && first[0].Id == "alpha", "首次图库加载结果不正确。");
        File.WriteAllText(catalogPath, JsonObject(new Dictionary<string, object> {
            { "themes", new object[] { Theme("beta", false) } }, { "total", 1 }
        }), Utf8);
        IList<ThemeGalleryItem> second = service.LoadThemesAsync(CancellationToken.None).GetAwaiter().GetResult();
        Assert(second.Count == 1 && second[0].Id == "alpha", "重复打开重新读取目录，未复用进程内缓存。");
    }

    static void TestGalleryServiceRunsCatalogWorkOffCallerThread(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        int callerThread = Thread.CurrentThread.ManagedThreadId;
        int catalogThread = callerThread;
        int previewThread = callerThread;
        byte[] preview = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0 };
        var transport = new FakeTransport(delegate(Uri uri)
        {
            if (uri.AbsolutePath.IndexOf("preview.png", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                previewThread = Thread.CurrentThread.ManagedThreadId;
                return Ok(preview);
            }
            catalogThread = Thread.CurrentThread.ManagedThreadId;
            Dictionary<string, object> theme = Theme("background", false);
            theme["image"] = "https://cdn.codexthemes.ai/preview.png";
            return Ok(JsonObject(new Dictionary<string, object> {
                { "themes", new object[] { theme } }, { "total", 1 }
            }));
        });
        var service = new CodexThemeGalleryService(new GalleryCatalog(root, transport, Path.Combine(root, "no-bundled")));
        IList<ThemeGalleryItem> items = service.LoadThemesAsync(CancellationToken.None).GetAwaiter().GetResult();
        ThemePreviewResult loaded = service.LoadPreviewAsync(items[0].Id, CancellationToken.None).GetAwaiter().GetResult();
        Assert(catalogThread != callerThread, "主题目录读取仍在调用线程执行。");
        Assert(previewThread != callerThread, "主题预览读取仍在调用线程执行。");
        Assert(loaded.Success, "后台预览读取没有返回图片。");
    }

    static void TestOfflineStatusHint(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        string downloadUrl = "https://codexthemes.ai/api/themes/alpha/download";
        string file = "package-" + Digest(downloadUrl) + ".codex-theme";
        File.WriteAllBytes(Path.Combine(root, file), new byte[] { 1, 2, 3 });
        File.WriteAllText(Path.Combine(root, "catalog-1-50.json"), JsonObject(new Dictionary<string, object> {
            { "themes", new object[] { Theme("alpha", true) } }, { "total", 1 }
        }), Utf8);
        File.WriteAllText(Path.Combine(root, "bundle-status.json"), JsonObject(new Dictionary<string, object> {
            { "schemaVersion", 1 },
            { "packages", new object[] { new Dictionary<string, object> {
                { "galleryId", "alpha" }, { "sourceUrl", downloadUrl }, { "file", file },
                { "sha256", new string('0', 64) }, { "bytes", 3 }, { "status", "ready" }, { "manifestId", "alpha" }
            } } }
        }), Utf8);
        GalleryCatalog catalog = new GalleryCatalog(Path.Combine(root, "cache"), null, root);
        GalleryTheme theme = catalog.LoadAllAsync().GetAwaiter().GetResult().Single();
        Assert(theme.CanInstall && theme.OfflinePackageStatus == "ready", "状态清单没有提供轻量可用提示。");
        try { catalog.DownloadAsync(theme).GetAwaiter().GetResult(); throw new InvalidOperationException("应用前没有重新校验离线包哈希。"); }
        catch (InvalidDataException) { }
    }

    static void TestMissingBundleFallsBackOnline(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        Dictionary<string, object> theme = Theme("online-fallback", true);
        theme["image"] = "https://cdn.codexthemes.ai/online-fallback.png";
        theme["offlinePackage"] = new Dictionary<string, object> { { "status", "missing" }, { "error", "not bundled" } };
        theme["offlinePreview"] = new Dictionary<string, object> { { "status", "missing" }, { "error", "not bundled" } };
        File.WriteAllText(Path.Combine(root, "catalog-1-50.json"), JsonObject(new Dictionary<string, object> {
            { "themes", new object[] { theme } }, { "total", 1 }
        }), Utf8);
        byte[] package = JsonBytes(new Dictionary<string, object> {
            { "format", "codex-theme" }, { "schemaVersion", 1 },
            { "manifest", new Dictionary<string, object> { { "id", "online-fallback" }, { "mode", "dark" } } },
            { "css", "body{color:white}" }
        });
        byte[] png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0 };
        var transport = new FakeTransport(delegate(Uri uri) {
            return uri.AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? Ok(png) : Ok(package);
        });
        GalleryCatalog catalog = new GalleryCatalog(Path.Combine(root, "cache"), transport, root);
        GalleryTheme loaded = catalog.LoadAllAsync().GetAwaiter().GetResult().Single();
        Assert(loaded.CanInstall, "普通在线主题不应因内置包缺失而禁用。");
        Assert(catalog.DownloadAsync(loaded).GetAwaiter().GetResult().Id == loaded.Id, "内置包缺失时没有回退在线主题包。");
        Assert(catalog.PreviewAsync(loaded).GetAwaiter().GetResult().IsUsable, "内置预览缺失时没有回退在线预览。");
        int requestsAfterFirstLoad = transport.Requests.Count;
        Assert(catalog.DownloadAsync(loaded).GetAwaiter().GetResult().Id == loaded.Id, "主题包缓存未通过重新校验。");
        Assert(catalog.PreviewAsync(loaded).GetAwaiter().GetResult().IsUsable, "预览缓存未被复用。");
        Assert(transport.Requests.Count == requestsAfterFirstLoad, "合法缓存存在时仍重复请求网络。");

        loaded.OfflinePackageStatus = "unavailable";
        Assert(!loaded.CanInstall, "明确不可用的主题不应开放在线应用。");
        loaded.OfflinePackageStatus = "missing";
        loaded.RequiresBundledPackage = true;
        Assert(!loaded.CanInstall, "需要本地适配的主题缺包时不应开放在线应用。");
        loaded.RequiresBundledPackage = false;
        loaded.OfflinePackageStatus = "invalid";
        Assert(!loaded.CanInstall, "校验失败的内置主题不应开放在线应用。");
    }

    static void TestPreviewAndDamagedCache(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var transport = new FakeTransport(delegate(Uri uri) { return Ok(new byte[] { 1, 2, 3 }); });
        GalleryCatalog catalog = new GalleryCatalog(root, transport);
        GalleryTheme webp = new GalleryTheme { Id = "webp", Name = "webp", Image = "https://cdn.codexthemes.ai/a.webp", Installable = false, Kind = "skin", PreviewState = GalleryPreviewState.WebpUnsupported };
        GalleryPreview preview = catalog.PreviewAsync(webp).GetAwaiter().GetResult();
        Assert(preview.State == GalleryPreviewState.WebpUnsupported, "WebP 没有明确标记为不可预览。");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "catalog-1-50.json"), "{broken", Utf8);
        GalleryCatalog offline = new GalleryCatalog(root, new FakeTransport(delegate(Uri uri) { throw new WebException("offline"); }));
        try { offline.LoadPageAsync(1, 50).GetAwaiter().GetResult(); throw new InvalidOperationException("损坏缓存错误地返回成功。"); }
        catch (InvalidDataException) { }
        catch (AggregateException error) { Assert(error.InnerException is InvalidDataException, "损坏缓存错误类型不准确。"); }
    }

    static void TestOfflineRepairHashMismatch(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        string downloadUrl = "https://codexthemes.ai/api/themes/repair-check/download";
        string packageFile = "package-" + Digest(downloadUrl) + ".codex-theme";
        File.WriteAllBytes(Path.Combine(root, packageFile), new byte[] { 1, 2, 3 });
        File.WriteAllText(Path.Combine(root, "catalog-1-50.json"), JsonObject(new Dictionary<string, object> {
            { "themes", new object[] { Theme("repair-check", true) } }, { "total", 1 }
        }), Utf8);
        File.WriteAllText(Path.Combine(root, "bundle-repairs.json"), JsonObject(new Dictionary<string, object> {
            { "schemaVersion", 1 },
            { "repairs", new object[] { new Dictionary<string, object> {
                { "galleryId", "repair-check" }, { "sha256", new string('0', 64) }, { "artMimeType", "image/jpeg" }
            } } }
        }), Utf8);
        GalleryCatalog catalog = new GalleryCatalog(Path.Combine(root, "cache"), new FakeTransport(delegate(Uri uri) { throw new WebException("network"); }), root);
        GalleryTheme theme = catalog.LoadAllAsync().GetAwaiter().GetResult().Single();
        Assert(theme.OfflinePackageStatus == "invalid" && !theme.CanInstall, "离线修复 SHA 不匹配时错误地标记为可安装。");
        try { catalog.DownloadAsync(theme).GetAwaiter().GetResult(); throw new InvalidOperationException("离线修复 SHA 不匹配时错误地返回成功。"); }
        catch (InvalidOperationException) { }
    }

    static void TestLargeInlineArt()
    {
        const int artSize = 4 * 1024 * 1024;
        byte[] art = new byte[artSize];
        Buffer.BlockCopy(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, art, 0, 8);
        string encoded = Convert.ToBase64String(art);
        byte[] package = JsonBytes(new Dictionary<string, object> {
            { "format", "codex-theme" }, { "schemaVersion", 1 },
            { "manifest", new Dictionary<string, object> { { "id", "large-art" }, { "mode", "dark" } } },
            { "css", "body{background:url(\"art.png\")}" },
            { "art", new Dictionary<string, object> { { "filename", "art.png" }, { "mimeType", "image/png" }, { "base64", encoded } } }
        });
        CodexThemePackage parsed = GalleryCatalog.ParsePackage(package, "large-art");
        int appliedBytes = Utf8.GetByteCount(parsed.AppliedCss);
        Assert(appliedBytes > 4 * 1024 * 1024, "large inline art regression did not exercise a payload above 4 MiB");
        Assert(appliedBytes <= ThemeRuntime.MaxCssBytes, "large inline art exceeded the shared runtime CSS limit");

        var repeatedCss = new StringBuilder();
        for (int i = 0; i < 7; i++) repeatedCss.Append(".art-").Append(i.ToString()).Append("{background:url(\"art.png\")}\n");
        byte[] oversized = JsonBytes(new Dictionary<string, object> {
            { "format", "codex-theme" }, { "schemaVersion", 1 },
            { "manifest", new Dictionary<string, object> { { "id", "oversized-art" }, { "mode", "dark" } } },
            { "css", repeatedCss.ToString() },
            { "art", new Dictionary<string, object> { { "filename", "art.png" }, { "mimeType", "image/png" }, { "base64", encoded } } }
        });
        ExpectPackageFailure(oversized, "oversized-art", "重复内联 art 超过 32 MiB");
    }

    static void TestLargeCustomPropertyArt()
    {
        const int artSize = 2 * 1024 * 1024;
        byte[] art = new byte[artSize];
        Buffer.BlockCopy(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, art, 0, 8);
        CodexThemePackage parsed = ParseCustomPropertyArt("large-art-var", ":root{--art:url(\"art.png\")} .hero{background-image:var(--art)}", art);
        Assert(parsed.AppliedCss.IndexOf("data:image/png;base64,", StringComparison.Ordinal) >= 0,
            "大图 custom property 的 data URL 没有保留在已校验 CSS 中。");
        Assert(parsed.AppliedCss.IndexOf("var(--art)", StringComparison.Ordinal) >= 0,
            "大图 custom property 的 var 使用点不应在包解析阶段展开。");

        CodexThemePackage repeated = ParseCustomPropertyArt("repeated-art-var", ":root{--art:url(\"art.png\")}.hero-a{background:var(--art)}.hero-b{background-image:var(--art)}", art);
        Assert(CountOccurrences(repeated.AppliedCss, "data:image/png;base64,") == 1,
            "重复 var 使用点不应复制 data URL 声明。");
        Assert(CountOccurrences(repeated.AppliedCss, "var(--art)") == 2,
            "重复 var 使用点在包解析后丢失。");

        Assert(ParseCustomPropertyArt("fallback-art-var", ":root{--art:url(\"art.png\")}.hero{background:var(--art,linear-gradient(red,blue))}", art).AppliedCss.Contains("var(--art,linear-gradient(red,blue))"),
            "带 fallback 的 var 使用点不应被破坏。");

        byte[] smallArt = new byte[32 * 1024];
        Buffer.BlockCopy(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, smallArt, 0, 8);
        Assert(ParseCustomPropertyArt("small-art-var", ":root{--art:url(\"art.png\")}.hero{background:var(--art)}", smallArt).AppliedCss.Contains("var(--art)"),
            "小图不应触发大图 custom property 替换路径。");
    }

    static int CountOccurrences(string value, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    static CodexThemePackage ParseCustomPropertyArt(string id, string css, byte[] art)
    {
        byte[] package = JsonBytes(new Dictionary<string, object> {
            { "format", "codex-theme" }, { "schemaVersion", 1 },
            { "manifest", new Dictionary<string, object> { { "id", id }, { "mode", "dark" } } },
            { "css", css },
            { "art", new Dictionary<string, object> { { "filename", "art.png" }, { "mimeType", "image/png" }, { "base64", Convert.ToBase64String(art) } } }
        });
        return GalleryCatalog.ParsePackage(package, id);
    }

    static string TestLive(string root)
    {
        GalleryCatalog catalog = new GalleryCatalog(root);
        IList<GalleryTheme> themes = catalog.LoadAllAsync().GetAwaiter().GetResult();
        int installable = themes.Count(x => x.Installable && x.Kind == "theme");
        int unavailableThemes = themes.Count(x => !x.Installable && x.Kind == "theme");
        int skins = themes.Count(x => x.Kind == "skin");
        GalleryTheme sample = themes.FirstOrDefault(x => x.Id == "gpt");
        if (sample == null) throw new InvalidDataException("公开样本 gpt 缺失。");
        CodexThemePackage package = catalog.DownloadAsync(sample).GetAwaiter().GetResult();
        if (package.Id != sample.Id || package.AppliedCss.IndexOf("data:", StringComparison.OrdinalIgnoreCase) < 0) throw new InvalidDataException("公开样本 gpt 包解析或 CSS 应用失败。");
        return "total=" + themes.Count + ", installableTheme=" + installable + ", unavailableTheme=" + unavailableThemes + ", skin=" + skins + ", package=" + package.Id;
    }

    static Dictionary<string, object> Theme(string id, bool installable)
    {
        return new Dictionary<string, object> { { "id", id }, { "name", id }, { "description", "test" }, { "author", "test" }, { "mode", "dark" }, { "url", "https://codexthemes.ai/themes/" + id }, { "kind", "theme" }, { "installable", installable }, { "downloadUrl", installable ? "https://codexthemes.ai/api/themes/" + id + "/download" : null } };
    }

    static GalleryHttpResponse Ok(byte[] body) { return new GalleryHttpResponse(200, body, "application/json", body.Length, null); }
    static GalleryHttpResponse Ok(string body) { return Ok(Utf8.GetBytes(body)); }
    static string JsonObject(Dictionary<string, object> value) { return new JavaScriptSerializer { MaxJsonLength = SkinBridge.MaxCdpMessageBytes }.Serialize(value); }
    static byte[] JsonBytes(Dictionary<string, object> value) { return Utf8.GetBytes(JsonObject(value)); }

    static string Digest(string value)
    {
        using (SHA256 sha = SHA256.Create())
        {
            return String.Concat(sha.ComputeHash(Utf8.GetBytes(value)).Select(b => b.ToString("x2")));
        }
    }

    static string Digest(byte[] value)
    {
        using (SHA256 sha = SHA256.Create())
        {
            return String.Concat(sha.ComputeHash(value).Select(b => b.ToString("x2")));
        }
    }

    static void ExpectPackageFailure(byte[] bytes, string id, string name)
    {
        try { GalleryCatalog.ParsePackage(bytes, id); throw new InvalidOperationException(name + " 未拒绝。"); }
        catch (GalleryPackageValidationException) { }
        catch (InvalidDataException) { }
    }

    static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    sealed class FakeTransport : IGalleryTransport
    {
        readonly Func<Uri, GalleryHttpResponse> handler;
        public readonly List<Uri> Requests = new List<Uri>();
        public FakeTransport(Func<Uri, GalleryHttpResponse> handler) { this.handler = handler; }
        public Task<GalleryHttpResponse> GetAsync(Uri uri, int maxBytes, CancellationToken cancellationToken)
        { Requests.Add(uri); return Task.FromResult(handler(uri)); }
    }
}
