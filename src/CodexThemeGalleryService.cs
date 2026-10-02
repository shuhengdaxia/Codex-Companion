using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

// Bridges the validated gallery catalog to the UI and the direct CDP runtime.
// It does not load, save, or pass through API keys, models, or Codex config.
public sealed class CodexThemeGalleryService : IThemeGalleryService
{
    private readonly GalleryCatalog catalog;
    private readonly object sync = new object();
    private readonly Dictionary<string, GalleryTheme> themesById = new Dictionary<string, GalleryTheme>(StringComparer.Ordinal);
    private readonly Dictionary<string, CodexThemePackage> packagesById = new Dictionary<string, CodexThemePackage>(StringComparer.Ordinal);
    private IList<ThemeGalleryItem> loadedItems;
    private Task<IList<ThemeGalleryItem>> loadTask;

    public CodexThemeGalleryService(GalleryCatalog catalog)
    {
        if (catalog == null) throw new ArgumentNullException("catalog");
        this.catalog = catalog;
    }

    public Task<IList<ThemeGalleryItem>> LoadThemesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (loadedItems != null) return Task.FromResult<IList<ThemeGalleryItem>>(new List<ThemeGalleryItem>(loadedItems));
            if (loadTask == null) loadTask = LoadThemesCoreAsync(cancellationToken);
            return loadTask;
        }
    }

    private async Task<IList<ThemeGalleryItem>> LoadThemesCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Ensure the in-flight task is published before a synchronous offline
            // catalog read can complete, so failures remain retryable.
            await Task.Yield();
            IList<GalleryTheme> themes = (await Task.Run(async delegate
            {
                return await catalog.LoadAllAsync(cancellationToken);
            }, cancellationToken)).Where(IsGalleryTheme).ToList();
            var items = themes.Select(ToItem).ToList();
            lock (sync)
            {
                themesById.Clear();
                packagesById.Clear();
                foreach (GalleryTheme theme in themes)
                {
                    if (theme != null && !String.IsNullOrWhiteSpace(theme.Id)) themesById[theme.Id] = theme;
                }
                loadedItems = items;
                loadTask = null;
                return new List<ThemeGalleryItem>(loadedItems);
            }
        }
        catch
        {
            lock (sync) loadTask = null;
            throw;
        }
    }

    public async Task<ThemePreviewResult> LoadPreviewAsync(string themeId, CancellationToken cancellationToken)
    {
        GalleryTheme theme = FindTheme(themeId);
        if (theme == null) return new ThemePreviewResult { Error = "主题目录中没有该条目。", State = "Missing" };
        if (theme.PreviewState == GalleryPreviewState.None)
            return new ThemePreviewResult { Error = "该条目没有公开预览图。", State = theme.PreviewState.ToString() };
        GalleryPreview preview = await Task.Run(async delegate
        {
            return await catalog.PreviewAsync(theme, cancellationToken);
        }, cancellationToken);
        if (preview == null) return new ThemePreviewResult { Error = "主题预览返回为空。", State = "Failed" };
        return new ThemePreviewResult
        {
            Success = preview.IsUsable,
            Bytes = preview.Bytes,
            State = preview.State.ToString(),
            Error = preview.Error
        };
    }

    public string GetCurrentThemeId()
    {
        return null;
    }

    public async Task<ThemeApplyResult> ApplyAsync(string themeId)
    {
        try
        {
            CodexThemePackage package = await GetPackageAsync(themeId, CancellationToken.None);
            return await Task.Run(delegate
            {
                return ThemeRuntime.TryApplyToRunning(themeId, package.AppliedCss, EffectiveMode(package), EffectiveBackgroundScope(package));
            });
        }
        catch (Exception ex)
        {
            return Failure(ex.Message, themeId);
        }
    }

    public async Task<ThemeApplyResult> LaunchAsync(string themeId)
    {
        try
        {
            CodexThemePackage package = await GetPackageAsync(themeId, CancellationToken.None);
            return await Task.Run(delegate
            {
                return ThemeRuntime.LaunchWithDebugging(themeId, package.AppliedCss, EffectiveMode(package), EffectiveBackgroundScope(package));
            });
        }
        catch (Exception ex)
        {
            return Failure(ex.Message, themeId);
        }
    }

    public async Task<ThemeApplyResult> RestartAsync(string themeId, CancellationToken cancellationToken)
    {
        try
        {
            // DownloadAsync parses and validates the package before any desktop
            // process is asked to close, so a failed package never interrupts work.
            CodexThemePackage package = await GetPackageAsync(themeId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(delegate
            {
                return ThemeRuntime.RestartWithDebugging(themeId, package.AppliedCss, EffectiveMode(package), EffectiveBackgroundScope(package), cancellationToken);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Failure("已取消重启，未关闭或启动 Codex。", themeId);
        }
        catch (Exception ex)
        {
            return Failure(ex.Message, themeId);
        }
    }

    public Task<ThemeApplyResult> RestoreAsync()
    {
        return Task.Run(delegate { return ThemeRuntime.RestoreRunning(); });
    }

    private async Task<CodexThemePackage> GetPackageAsync(string themeId, CancellationToken cancellationToken)
    {
        CodexThemePackage package;
        lock (sync) if (packagesById.TryGetValue(themeId ?? "", out package)) return package;
        GalleryTheme theme = FindTheme(themeId);
        if (theme == null)
        {
            IList<GalleryTheme> loaded = await catalog.LoadAllAsync(cancellationToken);
            lock (sync)
            {
                themesById.Clear();
                packagesById.Clear();
                foreach (GalleryTheme item in loaded.Where(IsGalleryTheme))
                    if (item != null && !String.IsNullOrWhiteSpace(item.Id)) themesById[item.Id] = item;
                theme = themesById.ContainsKey(themeId ?? "") ? themesById[themeId ?? ""] : null;
            }
        }
        if (theme == null) throw new KeyNotFoundException("主题目录中没有该条目。");
        package = await catalog.DownloadAsync(theme, cancellationToken);
        if (String.IsNullOrWhiteSpace(package.Mode)) package.Mode = theme.Mode;
        if (String.IsNullOrWhiteSpace(package.Mode)) throw new InvalidDataException("主题包缺少 mode，未应用以避免误判浅色/深色布局。");
        lock (sync) packagesById[theme.Id] = package;
        return package;
    }

    private GalleryTheme FindTheme(string themeId)
    {
        lock (sync)
        {
            GalleryTheme theme;
            return themesById.TryGetValue(themeId ?? "", out theme) ? theme : null;
        }
    }

    private static bool IsGalleryTheme(GalleryTheme theme)
    {
        return theme != null && !String.Equals(theme.Kind, "skin", StringComparison.OrdinalIgnoreCase);
    }

    private ThemeGalleryItem ToItem(GalleryTheme theme)
    {
        bool installable = theme.CanInstall;
        string availability;
        string detail;
        if (installable)
        {
            if (theme.OfflinePackageStatus == "ready")
            {
                availability = "可应用（应用时校验）";
                detail = "已提供离线主题包，应用前会重新校验包内容";
            }
            else
            {
                availability = "可直接应用";
                detail = "已提供并可验证主题包";
            }
        }
        else if (theme.Installable && !String.IsNullOrEmpty(theme.OfflinePackageStatus))
        {
            availability = "离线资源不可用";
            detail = "内置主题包状态：" + theme.OfflinePackageStatus +
                (String.IsNullOrWhiteSpace(theme.OfflinePackageError) ? "" : "，" + theme.OfflinePackageError);
        }
        else
        {
            availability = "网站参考，当前不可直接应用";
            detail = String.IsNullOrWhiteSpace(theme.Kind) ? "没有公开可安装主题包" : "kind=" + theme.Kind + "，没有公开可安装主题包";
        }
        return new ThemeGalleryItem
        {
            Id = theme.Id,
            Name = theme.Name,
            Author = theme.Author,
            Summary = theme.Description,
            DetailUrl = theme.Url,
            PreviewUrl = theme.Image,
            Installable = installable,
            Availability = availability,
            AvailabilityDetail = detail,
            PreviewState = theme.PreviewState.ToString()
        };
    }

    private string EffectiveMode(CodexThemePackage package)
    {
        if (package == null || String.IsNullOrWhiteSpace(package.Mode)) throw new InvalidDataException("主题包缺少 mode，未应用以避免误判浅色/深色布局。");
        return package.Mode;
    }

    private string EffectiveBackgroundScope(CodexThemePackage package)
    {
        object value = null;
        if (package != null && package.Manifest != null)
        {
            package.Manifest.TryGetValue("backgroundScope", out value);
            if (value == null)
            {
                object design;
                if (package.Manifest.TryGetValue("design", out design) && design is IDictionary<string, object>)
                    ((IDictionary<string, object>)design).TryGetValue("backgroundScope", out value);
            }
        }
        string scope = value as string;
        if (String.Equals(scope, "workspace", StringComparison.OrdinalIgnoreCase)) return "workspace";
        return "home";
    }

    private ThemeApplyResult Failure(string message, string themeId)
    {
        return new ThemeApplyResult { Success = false, ThemeId = themeId, Message = message ?? "主题操作失败。" };
    }
}

// Offline-only data for deterministic --gallery-preview screenshots. It has no
// apply or restore path and is never used by the normal application launch.
public sealed class PreviewThemeGalleryService : IThemeGalleryService
{
    private readonly IList<ThemeGalleryItem> items;

    public PreviewThemeGalleryService()
    {
        items = new List<ThemeGalleryItem>
        {
            new ThemeGalleryItem { Id = "codex-light", Name = "Codex · 经典浅色", Author = "Codex", Summary = "清爽的浅色工作区预览。", Installable = true, AvailabilityDetail = "离线预览样例", Background = "#FFFFFF", Accent = "#0285FF", Foreground = "#0D0D0D" },
            new ThemeGalleryItem { Id = "dracula", Name = "Dracula · 霓虹紫", Author = "Community", Summary = "深色背景与高对比紫色强调。", Installable = true, AvailabilityDetail = "离线预览样例", Background = "#282A36", Accent = "#FF79C6", Foreground = "#F8F8F2" },
            new ThemeGalleryItem { Id = "nord", Name = "Nord · 北欧蓝", Author = "Community", Summary = "柔和蓝灰色调，适合长时间使用。", Installable = true, AvailabilityDetail = "离线预览样例", Background = "#2E3440", Accent = "#88C0D0", Foreground = "#ECEFF4" },
            new ThemeGalleryItem { Id = "reference-skin", Name = "示例网站皮肤", Author = "Gallery", Summary = "仅提供网站参考，当前没有公开可安装包。", Installable = false, AvailabilityDetail = "没有公开可安装主题包", Background = "#F4F6EE", Accent = "#5B8A57", Foreground = "#34413A" }
        };
    }

    public Task<IList<ThemeGalleryItem>> LoadThemesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(items);
    }

    public Task<ThemePreviewResult> LoadPreviewAsync(string themeId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ThemePreviewResult { Success = false, State = "Fixture", Error = "离线截图使用色块预览。" });
    }

    public string GetCurrentThemeId() { return null; }
    public Task<ThemeApplyResult> ApplyAsync(string themeId) { return Task.FromResult(new ThemeApplyResult { Success = false, ThemeId = themeId, Message = "离线预览不会写入或应用主题。" }); }
    public Task<ThemeApplyResult> LaunchAsync(string themeId) { return ApplyAsync(themeId); }
    public Task<ThemeApplyResult> RestartAsync(string themeId, CancellationToken cancellationToken)
    { return ApplyAsync(themeId); }
    public Task<ThemeApplyResult> RestoreAsync() { return Task.FromResult(new ThemeApplyResult { Success = false, Message = "离线预览不会写入或恢复主题。" }); }
}
