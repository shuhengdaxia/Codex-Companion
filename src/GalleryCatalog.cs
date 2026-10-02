using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

public enum GalleryPreviewState
{
    None,
    Available,
    WebpUnsupported,
    UnsupportedFormat,
    Offline,
    Failed
}

public sealed class GalleryTheme
{
    public string Id { get; internal set; }
    public string Name { get; internal set; }
    public string Description { get; internal set; }
    public string Author { get; internal set; }
    public string Mode { get; internal set; }
    public string Image { get; internal set; }
    public string Url { get; internal set; }
    public string Kind { get; internal set; }
    public bool Installable { get; internal set; }
    public string DownloadUrl { get; internal set; }
    public string OfflinePackageStatus { get; internal set; }
    public string OfflinePackageFile { get; internal set; }
    public string OfflinePackageSha256 { get; internal set; }
    public string OfflinePackageManifestId { get; internal set; }
    public long? OfflinePackageBytes { get; internal set; }
    public string OfflinePackageError { get; internal set; }
    public string OfflinePreviewStatus { get; internal set; }
    public string OfflinePreviewFile { get; internal set; }
    public string OfflinePreviewSha256 { get; internal set; }
    public long? OfflinePreviewBytes { get; internal set; }
    public string OfflinePreviewMimeType { get; internal set; }
    public string OfflinePreviewError { get; internal set; }
    public GalleryPreviewState PreviewState { get; internal set; }
    internal bool RequiresBundledPackage { get; set; }
    public bool CanInstall
    {
        get
        {
            if (!Installable || String.IsNullOrEmpty(DownloadUrl) || OfflinePackageStatus == "invalid" || OfflinePackageStatus == "unavailable") return false;
            return !RequiresBundledPackage || OfflinePackageStatus == "ready";
        }
    }
    public bool CanPreview { get { return PreviewState == GalleryPreviewState.Available; } }
}

// Optional bundled alias allow-list in theme-gallery/package-bindings.json.
// Raw catalog pages remain the server format; package and preview files are
// addressed by SHA256(UTF8(source URL)) and are never addressed by raw IDs.
sealed class OfflinePackageBinding
{
    public string GalleryId;
    public string PackageId;
    public string DownloadUrl;
    public string Sha256;
}

sealed class OfflinePackageRepair
{
    public string GalleryId;
    public string Sha256;
    public string ArtMimeType;
}

sealed class OfflinePackageStatusRecord
{
    public string GalleryId;
    public string SourceUrl;
    public string File;
    public string Sha256;
    public long Bytes;
    public string Status;
    public string ManifestId;
    public string Error;
}

public sealed class GalleryPage
{
    public int Page { get; internal set; }
    public int Limit { get; internal set; }
    public int Total { get; internal set; }
    public IList<GalleryTheme> Themes { get; internal set; }
}

public sealed class GalleryPreview
{
    public GalleryPreviewState State { get; internal set; }
    public string MimeType { get; internal set; }
    public byte[] Bytes { get; internal set; }
    public string Error { get; internal set; }
    public bool IsUsable { get { return State == GalleryPreviewState.Available && Bytes != null && Bytes.Length > 0; } }
}

public sealed class CodexThemePackage
{
    public string Id { get; internal set; }
    public int SchemaVersion { get; internal set; }
    public string Mode { get; internal set; }
    public string Css { get; internal set; }
    public string AppliedCss { get; internal set; }
    public string ArtFilename { get; internal set; }
    public string ArtMimeType { get; internal set; }
    public byte[] ArtBytes { get; internal set; }
    public IDictionary<string, object> Manifest { get; internal set; }
    public IDictionary<string, object> Preview { get; internal set; }
}

public sealed class GalleryHttpResponse
{
    public int StatusCode { get; private set; }
    public string ContentType { get; private set; }
    public long? ContentLength { get; private set; }
    public Uri Location { get; private set; }
    public byte[] Body { get; private set; }

    public GalleryHttpResponse(int statusCode, byte[] body, string contentType, long? contentLength, Uri location)
    {
        StatusCode = statusCode;
        Body = body ?? new byte[0];
        ContentType = contentType;
        ContentLength = contentLength;
        Location = location;
    }
}

public interface IGalleryTransport
{
    Task<GalleryHttpResponse> GetAsync(Uri uri, int maxBytes, CancellationToken cancellationToken);
}

public sealed class GalleryPackageValidationException : Exception
{
    public GalleryPackageValidationException(string message) : base(message) { }
}

public sealed class GalleryCatalog
{
    public const string CatalogHost = "codexthemes.ai";
    public const string CdnHost = "cdn.codexthemes.ai";
    public const int DefaultPageLimit = 50;
    public const int MaxPackageBytes = 30 * 1024 * 1024;
    public const int MaxPreviewBytes = 8 * 1024 * 1024;

    readonly string cacheDirectory;
    readonly string offlineDirectory;
    readonly IGalleryTransport transport;
    readonly IDictionary<string, OfflinePackageBinding> offlineBindings;
    readonly IDictionary<string, OfflinePackageRepair> offlineRepairs;
    readonly IDictionary<string, OfflinePackageStatusRecord> offlineStatuses;
    static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    static readonly Regex CssUrl = new Regex(@"url\s*\(\s*(?:(['""])(.*?)\1|([^)]*))\s*\)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex CssComment = new Regex(@"/\*[\s\S]*?\*/", RegexOptions.Compiled);
    static readonly Regex CssEscape = new Regex(@"\\([0-9a-fA-F]{1,6})(?:[ \t\r\n])?|\\(.)", RegexOptions.Compiled);

    public GalleryCatalog() : this(null, null, null) { }

    public GalleryCatalog(string cacheDirectory) : this(cacheDirectory, null, null) { }

    public GalleryCatalog(string cacheDirectory, IGalleryTransport transport)
        : this(cacheDirectory, transport, null) { }

    public GalleryCatalog(string cacheDirectory, IGalleryTransport transport, string offlineDirectory)
    {
        this.cacheDirectory = Path.GetFullPath(String.IsNullOrWhiteSpace(cacheDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MidWebCodex", "themes")
            : cacheDirectory);
        string bundled = String.IsNullOrWhiteSpace(offlineDirectory)
            ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "theme-gallery")
            : offlineDirectory;
        this.offlineDirectory = Path.GetFullPath(bundled);
        this.transport = transport ?? new HttpGalleryTransport();
        this.offlineBindings = LoadOfflineBindings();
        this.offlineRepairs = LoadOfflineRepairs();
        this.offlineStatuses = LoadOfflineStatuses();
    }

    public string CacheDirectory { get { return cacheDirectory; } }
    public string OfflineDirectory { get { return offlineDirectory; } }

    public Task<GalleryPage> LoadPageAsync(int page, int limit)
    {
        return LoadPageAsync(page, limit, CancellationToken.None);
    }

    public async Task<GalleryPage> LoadPageAsync(int page, int limit, CancellationToken cancellationToken)
    {
        if (page < 1) throw new ArgumentOutOfRangeException("page");
        if (limit < 1 || limit > DefaultPageLimit) throw new ArgumentOutOfRangeException("limit");
        cancellationToken.ThrowIfCancellationRequested();
        string offlinePath = Path.Combine(offlineDirectory, "catalog-" + page.ToString() + "-" + limit.ToString() + ".json");
        if (File.Exists(offlinePath))
        {
            byte[] bundled = ReadCache(offlinePath);
            if (bundled == null) throw new InvalidDataException("内置主题目录无法读取：" + Path.GetFileName(offlinePath));
            try
            {
                GalleryPage offlinePage = ParsePage(bundled, page, limit);
                ApplyBundledStatus(offlinePage);
                return offlinePage;
            }
            catch (Exception error) { throw new InvalidDataException("内置主题目录损坏，未返回不可信目录。", error); }
        }
        string cachePath = Path.Combine(cacheDirectory, "catalog-" + page.ToString() + "-" + limit.ToString() + ".json");
        Uri uri = new Uri("https://" + CatalogHost + "/api/themes?page=" + page.ToString() + "&limit=" + limit.ToString());
        try
        {
            byte[] body = await FetchBytesAsync(uri, 2 * 1024 * 1024, cancellationToken);
            GalleryPage result = ParsePage(body, page, limit);
            WriteCache(cachePath, body);
            return result;
        }
        catch (Exception networkError)
        {
            if (networkError is OperationCanceledException) throw;
            if (!IsRecoverable(networkError)) throw;
            byte[] cached = ReadCache(cachePath);
            if (cached == null) throw new InvalidOperationException("主题图库离线且没有可用的第 " + page + " 页缓存。", networkError);
            try { return ParsePage(cached, page, limit); }
            catch (Exception cacheError) { throw new InvalidDataException("主题图库缓存损坏，未返回不可信目录。", cacheError); }
        }
    }

    public Task<GalleryPage> LoadPageAsync(int page)
    {
        return LoadPageAsync(page, DefaultPageLimit, CancellationToken.None);
    }

    public async Task<IList<GalleryTheme>> LoadAllAsync()
    {
        return await LoadAllAsync(CancellationToken.None);
    }

    public async Task<IList<GalleryTheme>> LoadAllAsync(CancellationToken cancellationToken)
    {
        var all = new List<GalleryTheme>();
        int page = 1;
        int total = Int32.MaxValue;
        while (all.Count < total)
        {
            if (page > 1000) throw new InvalidDataException("主题图库分页数量异常，已停止加载。");
            GalleryPage result = await LoadPageAsync(page, DefaultPageLimit, cancellationToken);
            total = result.Total;
            all.AddRange(result.Themes);
            if (result.Themes.Count == 0)
                throw new InvalidDataException("主题图库分页不完整：目录声称有 " + total + " 项，但第 " + page + " 页为空。");
            page++;
        }
        return all;
    }

    public async Task<CodexThemePackage> DownloadAsync(GalleryTheme theme)
    {
        return await DownloadAsync(theme, CancellationToken.None);
    }

    public async Task<CodexThemePackage> DownloadAsync(GalleryTheme theme, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (theme == null) throw new ArgumentNullException("theme");
        if (!theme.Installable) throw new InvalidOperationException("主题“" + theme.Name + "”不可安装（kind=" + theme.Kind + "）。");
        if (String.IsNullOrWhiteSpace(theme.DownloadUrl)) throw new InvalidOperationException("主题“" + theme.Name + "”没有可用下载地址。");
        Uri uri = RequireAllowedUri(theme.DownloadUrl, "downloadUrl");
        if (HasBundledCatalog())
        {
            if (theme.OfflinePackageStatus == "invalid" || theme.OfflinePackageStatus == "unavailable" ||
                (theme.RequiresBundledPackage && theme.OfflinePackageStatus != "ready"))
                return ReadBundledPackage(theme, uri);
            if (theme.OfflinePackageStatus == "ready" && File.Exists(BundledPackagePath(uri)))
                return ReadBundledPackage(theme, uri);
        }
        string cachePath = Path.Combine(cacheDirectory, "package-" + Digest(uri.AbsoluteUri) + ".codex-theme");
        byte[] bytes = ReadCache(cachePath);
        if (bytes != null)
        {
            try { return ParsePackage(bytes, theme.Id); }
            catch (Exception error)
            {
                if (!(error is InvalidDataException || error is GalleryPackageValidationException || error is FormatException || error is ArgumentException)) throw;
                bytes = null;
            }
        }
        try { bytes = await FetchBytesAsync(uri, MaxPackageBytes, cancellationToken); }
        catch (Exception error)
        {
            if (error is OperationCanceledException || !IsRecoverable(error)) throw;
            throw new InvalidOperationException("主题包下载失败且没有可用缓存：" + theme.Id, error);
        }
        CodexThemePackage package = ParsePackage(bytes, theme.Id);
        WriteCache(cachePath, bytes);
        return package;
    }

    public async Task<CodexThemePackage> DownloadAsync(string id, CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(id)) throw new ArgumentException("主题 ID 不能为空。", "id");
        IList<GalleryTheme> themes = await LoadAllAsync(cancellationToken);
        GalleryTheme theme = themes.FirstOrDefault(t => String.Equals(t.Id, id, StringComparison.Ordinal));
        if (theme == null) throw new KeyNotFoundException("主题图库没有精确匹配的 ID：“" + id + "”。");
        return await DownloadAsync(theme, cancellationToken);
    }

    public Task<CodexThemePackage> DownloadAsync(string id)
    {
        return DownloadAsync(id, CancellationToken.None);
    }

    public async Task<GalleryPreview> PreviewAsync(GalleryTheme theme, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (theme == null) throw new ArgumentNullException("theme");
        if (String.IsNullOrWhiteSpace(theme.Image)) return new GalleryPreview { State = GalleryPreviewState.None };
        Uri uri = RequireAllowedUri(theme.Image, "image");
        if (IsWebp(uri.AbsolutePath)) return new GalleryPreview { State = GalleryPreviewState.WebpUnsupported, Error = "WinForms 内置解码器不支持 WebP。" };
        if (HasBundledCatalog() && theme.OfflinePreviewStatus == "ready" && File.Exists(BundledPreviewPath(uri)))
            return ReadBundledPreview(theme, uri);
        string cachePath = Path.Combine(cacheDirectory, "preview-" + Digest(uri.AbsoluteUri) + ".bin");
        byte[] cached = ReadCache(cachePath);
        string cachedMime = DetectImageMime(cached);
        if (cachedMime == "image/webp") return new GalleryPreview { State = GalleryPreviewState.WebpUnsupported, Error = "WinForms 内置解码器不支持 WebP。" };
        if (cachedMime != null) return new GalleryPreview { State = GalleryPreviewState.Available, MimeType = cachedMime, Bytes = cached };
        try
        {
            GalleryHttpResponse response = await FetchResponseAsync(uri, MaxPreviewBytes, cancellationToken);
            string mime = DetectImageMime(response.Body);
            if (mime == null) return new GalleryPreview { State = GalleryPreviewState.UnsupportedFormat, Error = "预览不是 PNG、JPEG 或 GIF。" };
            if (mime == "image/webp") return new GalleryPreview { State = GalleryPreviewState.WebpUnsupported, Error = "WinForms 内置解码器不支持 WebP。" };
            WriteCache(cachePath, response.Body);
            return new GalleryPreview { State = GalleryPreviewState.Available, MimeType = mime, Bytes = response.Body };
        }
        catch (Exception error)
        {
            if (error is OperationCanceledException) throw;
            if (!IsRecoverable(error)) throw;
            cached = ReadCache(cachePath);
            string mime = DetectImageMime(cached);
            if (mime == "image/webp") return new GalleryPreview { State = GalleryPreviewState.WebpUnsupported, Error = "WinForms 内置解码器不支持 WebP。" };
            if (mime != null) return new GalleryPreview { State = GalleryPreviewState.Offline, MimeType = mime, Bytes = cached, Error = error.Message };
            return new GalleryPreview { State = GalleryPreviewState.Failed, Error = error.Message };
        }
    }

    public Task<GalleryPreview> PreviewAsync(GalleryTheme theme)
    {
        return PreviewAsync(theme, CancellationToken.None);
    }

    CodexThemePackage ReadBundledPackage(GalleryTheme theme, Uri source)
    {
        if (!String.IsNullOrEmpty(theme.OfflinePackageStatus) && theme.OfflinePackageStatus != "ready")
            throw new InvalidOperationException("主题“" + theme.Name + "”的离线主题包不可用：" + (theme.OfflinePackageError ?? theme.OfflinePackageStatus) + "。");
        OfflinePackageBinding binding = GetOfflineBinding(theme, source);
        OfflinePackageRepair repair = GetOfflineRepair(theme);
        if (binding != null && repair != null) throw new InvalidDataException("主题“" + theme.Id + "”同时存在包 alias 和 MIME 修复绑定。");
        string path = BundledPackagePath(source);
        byte[] bytes = ReadCache(path);
        if (bytes == null) throw new InvalidOperationException("主题“" + theme.Name + "”的离线主题包缺失，请更新软件资源后重试。");
        VerifyBundledBytes(bytes, repair == null ? (binding == null ? theme.OfflinePackageSha256 : binding.Sha256) : repair.Sha256, theme.OfflinePackageBytes, MaxPackageBytes, "主题包", theme.Id);
        string packageId = binding == null ? (theme.OfflinePackageManifestId ?? theme.Id) : binding.PackageId;
        try { return ParseBundledPackage(bytes, packageId, repair); }
        catch (Exception error) { throw new InvalidDataException("主题“" + theme.Name + "”的内置主题包校验失败。", error); }
    }

    GalleryPreview ReadBundledPreview(GalleryTheme theme, Uri source)
    {
        if (!String.IsNullOrEmpty(theme.OfflinePreviewStatus) && theme.OfflinePreviewStatus != "ready")
            return new GalleryPreview { State = GalleryPreviewState.Failed, Error = theme.OfflinePreviewError ?? "内置预览不可用。" };
        string path = BundledPreviewPath(source);
        byte[] bytes = ReadCache(path);
        if (bytes == null) return new GalleryPreview { State = GalleryPreviewState.Failed, Error = "内置预览文件缺失。" };
        try { VerifyBundledBytes(bytes, theme.OfflinePreviewSha256, theme.OfflinePreviewBytes, MaxPreviewBytes, "预览", theme.Id); }
        catch (Exception error) { return new GalleryPreview { State = GalleryPreviewState.Failed, Error = error.Message }; }
        string mime = DetectImageMime(bytes);
        if (mime == "image/webp") return new GalleryPreview { State = GalleryPreviewState.WebpUnsupported, Error = "WinForms 内置解码器不支持 WebP。" };
        if (mime == null) return new GalleryPreview { State = GalleryPreviewState.UnsupportedFormat, Error = "内置预览不是 PNG、JPEG 或 GIF。" };
        return new GalleryPreview { State = GalleryPreviewState.Available, MimeType = mime, Bytes = bytes };
    }

    bool HasBundledCatalog()
    {
        return File.Exists(Path.Combine(offlineDirectory, "catalog-1-" + DefaultPageLimit.ToString() + ".json"));
    }

    void ApplyBundledStatus(GalleryPage page)
    {
        if (page == null || page.Themes == null) return;
        foreach (GalleryTheme theme in page.Themes)
        {
            if (theme == null) continue;
            if (theme.Installable && !String.IsNullOrWhiteSpace(theme.DownloadUrl))
            {
                Uri source = RequireAllowedUri(theme.DownloadUrl, "downloadUrl");
                string path = BundledPackagePath(source);
                theme.RequiresBundledPackage = offlineBindings.ContainsKey(theme.Id) || offlineRepairs.ContainsKey(theme.Id);
                bool hasStatusHint = ApplyOfflineStatusHint(theme, source, path);
                OfflinePackageBinding binding;
                if (!hasStatusHint && offlineBindings.TryGetValue(theme.Id, out binding))
                {
                    theme.OfflinePackageSha256 = binding.Sha256;
                    if (File.Exists(path))
                    {
                        byte[] packageBytes = ReadCache(path);
                        if (packageBytes == null) { theme.OfflinePackageStatus = "invalid"; theme.OfflinePackageError = "内置主题包无法读取。"; }
                        else if (!String.Equals(Digest(packageBytes), binding.Sha256, StringComparison.OrdinalIgnoreCase)) { theme.OfflinePackageStatus = "invalid"; theme.OfflinePackageError = "内置主题包哈希校验失败。"; }
                        else ValidateBundledPackage(theme, packageBytes, binding == null ? (theme.OfflinePackageManifestId ?? theme.Id) : binding.PackageId, GetOfflineRepair(theme));
                    }
                    else if (String.IsNullOrEmpty(theme.OfflinePackageStatus)) { theme.OfflinePackageStatus = "missing"; theme.OfflinePackageError = "内置主题包文件缺失。"; }
                }
                else if (!hasStatusHint && String.IsNullOrEmpty(theme.OfflinePackageStatus))
                {
                    if (File.Exists(path))
                    {
                        byte[] packageBytes = ReadCache(path);
                        if (packageBytes == null) { theme.OfflinePackageStatus = "invalid"; theme.OfflinePackageError = "内置主题包无法读取。"; }
                        else ValidateBundledPackage(theme, packageBytes, theme.OfflinePackageManifestId ?? theme.Id, GetOfflineRepair(theme));
                    }
                    else { theme.OfflinePackageStatus = "missing"; theme.OfflinePackageError = "内置主题包文件缺失。"; }
                }
            }
            if (!String.IsNullOrWhiteSpace(theme.Image) && !IsWebp(new Uri(theme.Image).AbsolutePath))
            {
                Uri source = RequireAllowedUri(theme.Image, "image");
                string path = BundledPreviewPath(source);
                theme.OfflinePreviewStatus = File.Exists(path) ? "ready" : "missing";
                if (theme.OfflinePreviewStatus == "missing") theme.OfflinePreviewError = "内置预览文件缺失。";
            }
        }
    }

    bool ApplyOfflineStatusHint(GalleryTheme theme, Uri source, string path)
    {
        OfflinePackageStatusRecord status;
        if (!offlineStatuses.TryGetValue(theme.Id, out status)) return false;
        if (!String.Equals(status.SourceUrl, source.AbsoluteUri, StringComparison.Ordinal)) return false;
        theme.OfflinePackageFile = status.File;
        theme.OfflinePackageSha256 = status.Sha256;
        theme.OfflinePackageManifestId = status.ManifestId;
        theme.OfflinePackageBytes = status.Bytes;
        if (!File.Exists(path))
        {
            theme.OfflinePackageStatus = "missing";
            theme.OfflinePackageError = "内置主题包文件缺失。";
            return true;
        }
        try
        {
            if (new FileInfo(path).Length != status.Bytes)
            {
                theme.OfflinePackageStatus = "invalid";
                theme.OfflinePackageError = "内置主题包大小与状态清单不匹配，需重新构建资源。";
                return true;
            }
        }
        catch (IOException error)
        {
            theme.OfflinePackageStatus = "invalid";
            theme.OfflinePackageError = "内置主题包文件状态无法读取：" + error.Message;
            return true;
        }
        theme.OfflinePackageStatus = status.Status;
        theme.OfflinePackageError = status.Error;
        return true;
    }

    OfflinePackageBinding GetOfflineBinding(GalleryTheme theme, Uri source)
    {
        OfflinePackageBinding binding;
        if (!offlineBindings.TryGetValue(theme.Id, out binding)) return null;
        if (!String.Equals(binding.DownloadUrl, source.AbsoluteUri, StringComparison.Ordinal))
            throw new InvalidDataException("主题“" + theme.Id + "”的离线包绑定来源 URL 不匹配。");
        return binding;
    }

    OfflinePackageRepair GetOfflineRepair(GalleryTheme theme)
    {
        OfflinePackageRepair repair;
        return offlineRepairs.TryGetValue(theme.Id, out repair) ? repair : null;
    }

    void ValidateBundledPackage(GalleryTheme theme, byte[] bytes, string packageId, OfflinePackageRepair repair)
    {
        try
        {
            ParseBundledPackage(bytes, packageId, repair);
            if (String.IsNullOrEmpty(theme.OfflinePackageStatus)) theme.OfflinePackageStatus = "ready";
        }
        catch (Exception error)
        {
            theme.OfflinePackageStatus = "invalid";
            theme.OfflinePackageError = error.Message;
        }
    }

    CodexThemePackage ParseBundledPackage(byte[] bytes, string packageId, OfflinePackageRepair repair)
    {
        if (repair == null) return ParsePackage(bytes, packageId);
        if (!String.Equals(Digest(bytes), repair.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new GalleryPackageValidationException("主题包修复绑定 SHA-256 不匹配。");
        Dictionary<string, object> root = ReadObject(bytes, 64 * 1024 * 1024);
        Dictionary<string, object> art = RequiredObject(root, "art");
        string originalMime = RequiredString(art, "mimeType").ToLowerInvariant();
        if (originalMime != "image/png") throw new GalleryPackageValidationException("主题包修复只允许修复原始 PNG MIME。");
        string encoded = RequiredString(art, "base64");
        byte[] artBytes;
        try { artBytes = Convert.FromBase64String(encoded); }
        catch (FormatException error) { throw new GalleryPackageValidationException("主题包修复 art base64 无效：" + error.Message); }
        ValidateRepairedJpeg(artBytes);
        art["mimeType"] = repair.ArtMimeType;
        byte[] repaired = Utf8.GetBytes(new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 }.Serialize(root));
        return ParsePackage(repaired, packageId);
    }

    static void ValidateRepairedJpeg(byte[] bytes)
    {
        if (DetectImageMime(bytes) != "image/jpeg" || bytes.Length < 4 || bytes[bytes.Length - 2] != 0xFF || bytes[bytes.Length - 1] != 0xD9)
            throw new GalleryPackageValidationException("主题包修复 art 不是完整 JPEG 图像。");
        try
        {
            using (var stream = new MemoryStream(bytes))
            using (Image image = Image.FromStream(stream, true, true))
            {
                if (image.RawFormat == null || image.RawFormat.Guid != ImageFormat.Jpeg.Guid)
                    throw new GalleryPackageValidationException("主题包修复 art JPEG 解码失败。");
            }
        }
        catch (ArgumentException error)
        {
            throw new GalleryPackageValidationException("主题包修复 art JPEG 解码失败：" + error.Message);
        }
    }

    IDictionary<string, OfflinePackageRepair> LoadOfflineRepairs()
    {
        var result = new Dictionary<string, OfflinePackageRepair>(StringComparer.Ordinal);
        string path = Path.Combine(offlineDirectory, "bundle-repairs.json");
        if (!File.Exists(path)) return result;
        byte[] bytes = ReadCache(path);
        if (bytes == null) throw new InvalidDataException("内置主题包修复清单无法读取。");
        Dictionary<string, object> root = ReadObject(bytes, 2 * 1024 * 1024);
        if (RequiredInt(root, "schemaVersion") != 1) throw new InvalidDataException("内置主题包修复清单版本不支持。");
        object raw;
        if (!root.TryGetValue("repairs", out raw) || !(raw is object[])) throw new InvalidDataException("内置主题包修复清单缺少 repairs 数组。");
        foreach (object value in (object[])raw)
        {
            Dictionary<string, object> row = value as Dictionary<string, object>;
            if (row == null) throw new InvalidDataException("内置主题包修复条目无效。");
            string galleryId = RequiredString(row, "galleryId");
            string sha256 = RequiredString(row, "sha256");
            string artMimeType = RequiredString(row, "artMimeType").ToLowerInvariant();
            ValidateIdentifier(galleryId, "repairs.galleryId");
            ValidateSha256(sha256, "repairs.sha256");
            if (artMimeType != "image/jpeg") throw new InvalidDataException("内置主题包修复只允许 JPEG art MIME。");
            if (result.ContainsKey(galleryId)) throw new InvalidDataException("内置主题包修复清单含有重复 galleryId。");
            result.Add(galleryId, new OfflinePackageRepair { GalleryId = galleryId, Sha256 = sha256, ArtMimeType = artMimeType });
        }
        return result;
    }

    IDictionary<string, OfflinePackageStatusRecord> LoadOfflineStatuses()
    {
        var result = new Dictionary<string, OfflinePackageStatusRecord>(StringComparer.Ordinal);
        string path = Path.Combine(offlineDirectory, "bundle-status.json");
        if (!File.Exists(path)) return result;
        byte[] bytes = ReadCache(path);
        if (bytes == null) throw new InvalidDataException("内置主题包状态清单无法读取。");
        Dictionary<string, object> root = ReadObject(bytes, 4 * 1024 * 1024);
        if (RequiredInt(root, "schemaVersion") != 1) throw new InvalidDataException("内置主题包状态清单版本不支持。");
        object raw;
        if (!root.TryGetValue("packages", out raw) || !(raw is object[])) throw new InvalidDataException("内置主题包状态清单缺少 packages 数组。");
        foreach (object value in (object[])raw)
        {
            Dictionary<string, object> row = value as Dictionary<string, object>;
            if (row == null) throw new InvalidDataException("内置主题包状态条目无效。");
            string galleryId = RequiredString(row, "galleryId");
            string sourceUrl = RequiredString(row, "sourceUrl");
            string file = RequiredString(row, "file").Replace('\\', '/');
            string sha256 = RequiredString(row, "sha256");
            long packageBytes = RequiredInt(row, "bytes");
            string status = RequiredString(row, "status").Trim().ToLowerInvariant();
            string manifestId = RequiredString(row, "manifestId");
            string error = OptionalString(row, "error");
            ValidateIdentifier(galleryId, "packages.galleryId");
            Uri source = RequireAllowedUri(sourceUrl, "packages.sourceUrl");
            ValidateSha256(sha256, "packages.sha256");
            ValidateIdentifier(manifestId, "packages.manifestId");
            if (status != "ready" && status != "invalid" && status != "missing" && status != "unavailable")
                throw new InvalidDataException("内置主题包状态无效：" + status + "。");
            if (packageBytes < 1 || packageBytes > MaxPackageBytes) throw new InvalidDataException("内置主题包状态大小无效。");
            string expected = "package-" + Digest(source.AbsoluteUri) + ".codex-theme";
            if (file != expected && file != "packages/" + expected)
                throw new InvalidDataException("内置主题包状态文件名与来源 URL 不匹配。");
            if (result.ContainsKey(galleryId)) throw new InvalidDataException("内置主题包状态清单含有重复 galleryId。");
            result.Add(galleryId, new OfflinePackageStatusRecord { GalleryId = galleryId, SourceUrl = source.AbsoluteUri,
                File = file, Sha256 = sha256, Bytes = packageBytes, Status = status, ManifestId = manifestId, Error = error });
        }
        return result;
    }

    IDictionary<string, OfflinePackageBinding> LoadOfflineBindings()
    {
        var result = new Dictionary<string, OfflinePackageBinding>(StringComparer.Ordinal);
        string path = Path.Combine(offlineDirectory, "package-bindings.json");
        if (!File.Exists(path)) return result;
        byte[] bytes = ReadCache(path);
        if (bytes == null) throw new InvalidDataException("内置主题包绑定清单无法读取。");
        Dictionary<string, object> root = ReadObject(bytes, 2 * 1024 * 1024);
        int schema = RequiredInt(root, "schemaVersion");
        if (schema != 1) throw new InvalidDataException("内置主题包绑定清单版本不支持。");
        object raw;
        if (!root.TryGetValue("bindings", out raw) || !(raw is object[])) throw new InvalidDataException("内置主题包绑定清单缺少 bindings 数组。");
        foreach (object value in (object[])raw)
        {
            Dictionary<string, object> row = value as Dictionary<string, object>;
            if (row == null) throw new InvalidDataException("内置主题包绑定条目无效。");
            string galleryId = RequiredString(row, "galleryId");
            string packageId = RequiredString(row, "packageId");
            string downloadUrl = RequiredString(row, "downloadUrl");
            string sha256 = RequiredString(row, "sha256");
            ValidateIdentifier(galleryId, "bindings.galleryId");
            ValidateIdentifier(packageId, "bindings.packageId");
            Uri source = RequireAllowedUri(downloadUrl, "bindings.downloadUrl");
            ValidateSha256(sha256, "bindings.sha256");
            if (result.ContainsKey(galleryId)) throw new InvalidDataException("内置主题包绑定清单含有重复 galleryId。");
            result.Add(galleryId, new OfflinePackageBinding { GalleryId = galleryId, PackageId = packageId, DownloadUrl = source.AbsoluteUri, Sha256 = sha256 });
        }
        return result;
    }

    static void VerifyBundledBytes(byte[] bytes, string expectedSha256, long? expectedLength, int maxBytes, string kind, string id)
    {
        if (bytes == null || bytes.Length == 0 || bytes.Length > maxBytes)
            throw new InvalidDataException("主题“" + id + "”的内置" + kind + "大小无效。");
        if (expectedLength.HasValue && expectedLength.Value != bytes.Length)
            throw new InvalidDataException("主题“" + id + "”的内置" + kind + "大小校验失败。");
        if (!String.IsNullOrEmpty(expectedSha256) && !String.Equals(Digest(bytes), expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("主题“" + id + "”的内置" + kind + "哈希校验失败。");
    }

    string BundledAssetPath(string directory, string filename)
    {
        string nested = Path.Combine(offlineDirectory, directory, filename);
        return Directory.Exists(Path.Combine(offlineDirectory, directory)) ? nested : Path.Combine(offlineDirectory, filename);
    }

    string BundledPackagePath(Uri source)
    {
        return BundledAssetPath("packages", "package-" + Digest(source.AbsoluteUri) + ".codex-theme");
    }

    string BundledPreviewPath(Uri source)
    {
        return BundledAssetPath("previews", "preview-" + Digest(source.AbsoluteUri) + ".bin");
    }

    public static GalleryPage ParsePage(byte[] bytes, int page, int limit)
    {
        if (bytes == null || bytes.Length == 0) throw new InvalidDataException("主题目录响应为空。");
        Dictionary<string, object> root = ReadObject(bytes, 2 * 1024 * 1024);
        int total = RequiredInt(root, "total");
        if (total < 0) throw new InvalidDataException("主题目录 total 无效。");
        object raw;
        if (!root.TryGetValue("themes", out raw) || !(raw is object[])) throw new InvalidDataException("主题目录缺少 themes 数组。");
        var themes = new List<GalleryTheme>();
        foreach (object value in (object[])raw)
        {
            Dictionary<string, object> row = value as Dictionary<string, object>;
            if (row == null) throw new InvalidDataException("主题目录包含无效条目。");
            themes.Add(ParseTheme(row));
        }
        return new GalleryPage { Page = page, Limit = limit, Total = total, Themes = themes };
    }

    public static CodexThemePackage ParsePackage(byte[] bytes, string expectedId)
    {
        if (bytes == null || bytes.Length == 0 || bytes.Length > MaxPackageBytes) throw new GalleryPackageValidationException("主题包大小无效。");
        Dictionary<string, object> root = ReadObject(bytes, 64 * 1024 * 1024);
        string format = RequiredString(root, "format");
        if (!String.Equals(format, "codex-theme", StringComparison.Ordinal)) throw new GalleryPackageValidationException("主题包 format 必须是 codex-theme。");
        int schema = RequiredInt(root, "schemaVersion");
        if (schema != 1) throw new GalleryPackageValidationException("不支持的主题包 schemaVersion：" + schema + "。");
        Dictionary<string, object> manifest = RequiredObject(root, "manifest");
        string id = RequiredString(manifest, "id");
        ValidateIdentifier(id, "manifest.id");
        ValidateManifestPath(manifest, "css", null);
        ValidateManifestPath(manifest, "preview", null);
        if (!String.IsNullOrEmpty(expectedId) && !String.Equals(id, expectedId, StringComparison.Ordinal))
            throw new GalleryPackageValidationException("主题 ID 不匹配：目录 id=" + expectedId + "，包 manifest.id=" + id + "。");
        string css = RequiredString(root, "css");
        if (css.Length > 8 * 1024 * 1024) throw new GalleryPackageValidationException("主题 CSS 超过 8 MiB 限制。");
        Dictionary<string, object> art = OptionalObject(root, "art");
        string artFilename = null, artMime = null;
        byte[] artBytes = null;
        if (art != null)
        {
            artFilename = RequiredString(art, "filename");
            ValidateFilename(artFilename);
            artMime = RequiredString(art, "mimeType").ToLowerInvariant();
            if (artMime != "image/png" && artMime != "image/jpeg" && artMime != "image/gif" && artMime != "image/webp")
                throw new GalleryPackageValidationException("主题 art MIME 不受支持：" + artMime + "。");
            string encoded = RequiredString(art, "base64");
            try { artBytes = Convert.FromBase64String(encoded); }
            catch (FormatException error) { throw new GalleryPackageValidationException("主题 art base64 无效：" + error.Message); }
            if (artBytes.Length == 0 || artBytes.Length > MaxPackageBytes) throw new GalleryPackageValidationException("主题 art 大小无效。");
            string detected = DetectImageMime(artBytes);
            if (!String.Equals(detected, artMime, StringComparison.Ordinal)) throw new GalleryPackageValidationException("主题 art MIME 与图像签名不匹配。");
        }
        ValidateManifestPath(manifest, "art", artFilename);
        ValidateCss(css, artFilename);
        string appliedCss = InlineArt(css, artFilename, artMime, artBytes);
        ValidateCss(appliedCss, null);
        if (appliedCss.Length > ThemeRuntime.MaxCssBytes || Utf8.GetByteCount(appliedCss) > ThemeRuntime.MaxCssBytes)
            throw new GalleryPackageValidationException("主题 CSS（含内联 art）超过运行时 32 MiB 限制。");
        string mode = OptionalString(manifest, "mode");
        if (mode != null) ValidateIdentifier(mode, "manifest.mode");
        IDictionary<string, object> preview = OptionalObject(root, "preview");
        return new CodexThemePackage { Id = id, SchemaVersion = schema, Mode = mode, Css = css, AppliedCss = appliedCss,
            ArtFilename = artFilename, ArtMimeType = artMime, ArtBytes = artBytes, Manifest = manifest, Preview = preview };
    }

    static GalleryTheme ParseTheme(Dictionary<string, object> row)
    {
        string id = RequiredString(row, "id");
        ValidateIdentifier(id, "id");
        bool installable = OptionalBool(row, "installable");
        string image = OptionalString(row, "image");
        string url = OptionalString(row, "url");
        string download = OptionalString(row, "downloadUrl");
        if (image != null) RequireAllowedUri(image, "image");
        if (url != null) RequireAllowedUri(url, "url");
        if (download != null) RequireAllowedUri(download, "downloadUrl");
        if (installable && String.IsNullOrWhiteSpace(download)) throw new InvalidDataException("可安装主题“" + id + "”缺少 downloadUrl。");
        GalleryPreviewState preview = image == null ? GalleryPreviewState.None : (IsWebp(new Uri(image).AbsolutePath) ? GalleryPreviewState.WebpUnsupported : GalleryPreviewState.Available);
        GalleryTheme result = new GalleryTheme { Id = id, Name = OptionalString(row, "name") ?? id, Description = OptionalString(row, "description"),
            Author = OptionalString(row, "author"), Mode = OptionalString(row, "mode"), Image = image, Url = url, Kind = OptionalString(row, "kind") ?? "theme",
            Installable = installable, DownloadUrl = download, PreviewState = preview };
        ParseOfflineAsset(OptionalObject(row, "offlinePackage"), "package", result, id, download);
        ParseOfflineAsset(OptionalObject(row, "offlinePreview"), "preview", result, id, image);
        return result;
    }

    static void ParseOfflineAsset(Dictionary<string, object> asset, string kind, GalleryTheme theme, string id, string sourceUrl)
    {
        if (asset == null) return;
        string status = RequiredString(asset, "status").Trim().ToLowerInvariant();
        if (status != "ready" && status != "missing" && status != "invalid" && status != "unavailable")
            throw new InvalidDataException("主题“" + id + "”的离线" + kind + "状态无效。");
        string error = OptionalString(asset, "error");
        string file = OptionalString(asset, "file");
        string digest = OptionalString(asset, "sha256");
        long? bytes = OptionalLong(asset, "bytes");
        string mime = OptionalString(asset, "mimeType");
        string manifestId = OptionalString(asset, "manifestId");
        if (status == "ready")
        {
            if (String.IsNullOrWhiteSpace(sourceUrl)) throw new InvalidDataException("主题“" + id + "”的离线" + kind + "缺少来源 URL。");
            Uri source = RequireAllowedUri(sourceUrl, "offline." + kind + ".source");
            string expected = (kind == "package" ? "package-" + Digest(source.AbsoluteUri) + ".codex-theme" : "preview-" + Digest(source.AbsoluteUri) + ".bin");
            string normalized = (file ?? "").Replace('\\', '/');
            if (normalized != expected && normalized != (kind == "package" ? "packages/" : "previews/") + expected)
                throw new InvalidDataException("主题“" + id + "”的离线" + kind + "文件名与来源 URL 不匹配。");
            ValidateSha256(digest, "offline." + kind + ".sha256");
            if (!bytes.HasValue || bytes.Value < 1 || bytes.Value > (kind == "package" ? MaxPackageBytes : MaxPreviewBytes))
                throw new InvalidDataException("主题“" + id + "”的离线" + kind + "大小无效。");
            if (kind == "package")
            {
                if (String.IsNullOrWhiteSpace(manifestId)) manifestId = id;
                ValidateIdentifier(manifestId, "offline.package.manifestId");
            }
        }
        if (kind == "package")
        {
            theme.OfflinePackageStatus = status; theme.OfflinePackageFile = file; theme.OfflinePackageSha256 = digest; theme.OfflinePackageManifestId = manifestId;
            theme.OfflinePackageBytes = bytes; theme.OfflinePackageError = error;
        }
        else
        {
            theme.OfflinePreviewStatus = status; theme.OfflinePreviewFile = file; theme.OfflinePreviewSha256 = digest;
            theme.OfflinePreviewBytes = bytes; theme.OfflinePreviewMimeType = mime; theme.OfflinePreviewError = error;
        }
    }

    static void ValidateCss(string css, string artFilename)
    {
        if (css.IndexOf('\0') >= 0) throw new GalleryPackageValidationException("主题 CSS 含有非法空字符。");
        string active = CssComment.Replace(css, " ");
        string normalized = DecodeCssEscapes(active);
        if (ContainsTokenOutsideStrings(normalized, "@import") || ContainsTokenOutsideStrings(normalized, "<script") || ContainsTokenOutsideStrings(normalized, "expression(") || ContainsTokenOutsideStrings(normalized, "javascript:"))
            throw new GalleryPackageValidationException("主题 CSS 包含脚本、@import 或其他禁止内容。");
        if (ContainsCssFunctionOutsideStrings(normalized, "image-set") || ContainsCssFunctionOutsideStrings(normalized, "-webkit-image-set"))
            throw new GalleryPackageValidationException("主题 CSS 包含未支持的 image-set 外部资源。");
        foreach (Match match in CssUrl.Matches(normalized))
        {
            string value = (match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value).Trim();
            if (value.Length == 0 || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("#", StringComparison.Ordinal)) continue;
            if (artFilename != null && IsArtReference(value, artFilename)) continue;
            throw new GalleryPackageValidationException("主题 CSS 包含外部或路径资源：" + value);
        }
    }

    static string InlineArt(string css, string filename, string mime, byte[] bytes)
    {
        if (filename == null || bytes == null) return css;
        string data = "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
        return CssUrl.Replace(css, delegate(Match match)
        {
            string value = (match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value).Trim();
            return IsArtReference(value, filename) ? "url(\"" + data + "\")" : match.Value;
        });
    }
    static bool IsArtReference(string value, string filename)
    {
        if (String.IsNullOrWhiteSpace(value) || value.IndexOf('\\') >= 0 || value.IndexOf(':') >= 0 || value.StartsWith("/", StringComparison.Ordinal)) return false;
        string[] segments = value.Replace('\\', '/').Split('/');
        if (segments.Any(x => x == ".." || x.Length == 0 && segments.Length > 1 && x != ".")) return false;
        return String.Equals(Path.GetFileName(value), filename, StringComparison.Ordinal);
    }

    static string DecodeCssEscapes(string text)
    {
        return CssEscape.Replace(text, delegate(Match match)
        {
            if (!match.Groups[1].Success) return match.Groups[2].Value;
            int code = Int32.Parse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
            if (code == 0 || code > 0x10FFFF) return "\uFFFD";
            return Char.ConvertFromUtf32(code);
        });
    }

    async Task<byte[]> FetchBytesAsync(Uri uri, int maxBytes, CancellationToken cancellationToken)
    {
        GalleryHttpResponse response = await FetchResponseAsync(uri, maxBytes, cancellationToken);
        return response.Body;
    }

    async Task<GalleryHttpResponse> FetchResponseAsync(Uri uri, int maxBytes, CancellationToken cancellationToken)
    {
        Uri current = RequireAllowedUri(uri, "request");
        for (int redirects = 0; redirects <= 3; redirects++)
        {
            GalleryHttpResponse response = await transport.GetAsync(current, maxBytes, cancellationToken);
            if (response == null) throw new IOException("主题图库传输层返回空响应。");
            if (response.StatusCode >= 300 && response.StatusCode < 400)
            {
                if (response.Location == null) throw new IOException("主题图库重定向缺少 Location。");
                current = RequireAllowedUri(new Uri(current, response.Location), "redirect");
                continue;
            }
            if (response.StatusCode != 200) throw new WebException("主题图库请求失败，HTTP " + response.StatusCode + "。");
            if (response.ContentLength.HasValue && response.ContentLength.Value > maxBytes) throw new InvalidDataException("主题图库响应超过大小限制。");
            if (response.Body == null || response.Body.Length > maxBytes) throw new InvalidDataException("主题图库响应超过大小限制。");
            return response;
        }
        throw new WebException("主题图库重定向次数超过限制。");
    }

    static Uri RequireAllowedUri(string value, string field)
    {
        Uri uri;
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri)) throw new InvalidDataException(field + " 不是绝对 URL。");
        return RequireAllowedUri(uri, field);
    }

    static Uri RequireAllowedUri(Uri uri, string field)
    {
        if (uri == null || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || (uri.Port != -1 && uri.Port != 443))
            throw new InvalidDataException(field + " 必须使用白名单 HTTPS 地址。");
        string host = uri.Host.TrimEnd('.').ToLowerInvariant();
        if (host != CatalogHost && host != CdnHost) throw new InvalidDataException(field + " 主机不在 codexthemes.ai 白名单内。");
        return uri;
    }

    static bool IsRecoverable(Exception error)
    {
        return error is WebException || error is HttpRequestException || error is TaskCanceledException || error is IOException || error is InvalidOperationException;
    }

    static Dictionary<string, object> ReadObject(byte[] bytes, int maxJsonLength)
    {
        string text = Utf8.GetString(bytes);
        var parser = new JavaScriptSerializer { MaxJsonLength = maxJsonLength };
        Dictionary<string, object> value = parser.DeserializeObject(text) as Dictionary<string, object>;
        if (value == null) throw new InvalidDataException("主题响应不是 JSON 对象。");
        return value;
    }

    static string RequiredString(Dictionary<string, object> map, string key)
    {
        string value = OptionalString(map, key);
        if (String.IsNullOrWhiteSpace(value)) throw new InvalidDataException("主题响应缺少字符串字段：" + key + "。");
        return value;
    }

    static string OptionalString(Dictionary<string, object> map, string key)
    {
        object value;
        if (!map.TryGetValue(key, out value) || value == null) return null;
        string text = value as string;
        if (text == null || text.Length > 32 * 1024 * 1024) throw new InvalidDataException("主题字段无效：" + key + "。");
        return text;
    }

    static void ValidateIdentifier(string value, string field)
    {
        if (String.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(Char.IsControl))
            throw new InvalidDataException("主题字段无效：" + field + "。");
    }

    static int RequiredInt(Dictionary<string, object> map, string key)
    {
        object value;
        if (!map.TryGetValue(key, out value)) throw new InvalidDataException("主题响应缺少数字字段：" + key + "。");
        try { return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture); }
        catch { throw new InvalidDataException("主题数字字段无效：" + key + "。"); }
    }

    static bool OptionalBool(Dictionary<string, object> map, string key)
    {
        object value;
        if (!map.TryGetValue(key, out value) || value == null) return false;
        if (!(value is bool)) throw new InvalidDataException("主题布尔字段无效：" + key + "。");
        return (bool)value;
    }

    static long? OptionalLong(Dictionary<string, object> map, string key)
    {
        object value;
        if (!map.TryGetValue(key, out value) || value == null) return null;
        try
        {
            long result = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
            return result;
        }
        catch { throw new InvalidDataException("主题数字字段无效：" + key + "。"); }
    }

    static void ValidateSha256(string value, string field)
    {
        if (String.IsNullOrWhiteSpace(value) || value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("主题字段无效：" + field + "。");
    }

    static Dictionary<string, object> RequiredObject(Dictionary<string, object> map, string key)
    {
        Dictionary<string, object> value = OptionalObject(map, key);
        if (value == null) throw new InvalidDataException("主题响应缺少对象字段：" + key + "。");
        return value;
    }

    static Dictionary<string, object> OptionalObject(Dictionary<string, object> map, string key)
    {
        object value;
        if (!map.TryGetValue(key, out value) || value == null) return null;
        Dictionary<string, object> result = value as Dictionary<string, object>;
        if (result == null) throw new InvalidDataException("主题对象字段无效：" + key + "。");
        return result;
    }

    static void ValidateFilename(string value)
    {
        if (String.IsNullOrWhiteSpace(value) || value == "." || value == ".." || value.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || Path.GetFileName(value) != value || value.Any(Char.IsControl))
            throw new GalleryPackageValidationException("主题 art filename 含有路径穿越或非法字符。");
    }

    static void ValidateManifestPath(Dictionary<string, object> manifest, string key, string expectedFilename)
    {
        string value = OptionalString(manifest, key);
        if (value == null) return;
        if (value.IndexOf('\\') >= 0 || value.IndexOf(':') >= 0 || value.StartsWith("/", StringComparison.Ordinal) || value.Split('/').Any(x => x == ".." || x.Length == 0))
            throw new GalleryPackageValidationException("主题 manifest." + key + " 含有路径穿越或外部资源。");
        if (expectedFilename != null && !String.Equals(Path.GetFileName(value), expectedFilename, StringComparison.Ordinal))
            throw new GalleryPackageValidationException("主题 manifest." + key + " 与 art.filename 不匹配。");
    }

    static string DetectImageMime(byte[] bytes)
    {
        if (bytes == null) return null;
        if (bytes.Length >= 8 && bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71 && bytes[4] == 13 && bytes[5] == 10 && bytes[6] == 26 && bytes[7] == 10) return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255) return "image/jpeg";
        if (bytes.Length >= 6 && ((bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F') || (bytes[0] == (byte)'g' && bytes[1] == (byte)'i' && bytes[2] == (byte)'f'))) return "image/gif";
        if (bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P') return "image/webp";
        return null;
    }

    static bool IsWebp(string path) { return path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase); }

    static string Digest(string text)
    {
        using (SHA256 sha = SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(Utf8.GetBytes(text));
            return String.Concat(hash.Select(b => b.ToString("x2")));
        }
    }

    static string Digest(byte[] bytes)
    {
        using (SHA256 sha = SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(bytes);
            return String.Concat(hash.Select(b => b.ToString("x2")));
        }
    }

    static byte[] ReadCache(string path)
    {
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch { return null; }
    }

    static void WriteCache(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    static bool ContainsTokenOutsideStrings(string text, string token)
    {
        char quote = '\0';
        for (int i = 0; i <= text.Length - token.Length; i++)
        {
            char current = text[i];
            if (quote != '\0')
            {
                if (current == '\\') { i++; continue; }
                if (current == quote) quote = '\0';
                continue;
            }
            if (current == '\'' || current == '\"') { quote = current; continue; }
            if (String.Compare(text, i, token, 0, token.Length, StringComparison.OrdinalIgnoreCase) == 0) return true;
        }
        return false;
    }

    static bool ContainsCssFunctionOutsideStrings(string text, string function)
    {
        char quote = '\0';
        for (int i = 0; i <= text.Length - function.Length; i++)
        {
            char current = text[i];
            if (quote != '\0')
            {
                if (current == '\\') { i++; continue; }
                if (current == quote) quote = '\0';
                continue;
            }
            if (current == '\'' || current == '"') { quote = current; continue; }
            if (String.Compare(text, i, function, 0, function.Length, StringComparison.OrdinalIgnoreCase) != 0) continue;
            int next = i + function.Length;
            while (next < text.Length && Char.IsWhiteSpace(text[next])) next++;
            if (next < text.Length && text[next] == '(') return true;
        }
        return false;
    }

    sealed class HttpGalleryTransport : IGalleryTransport
    {
        public async Task<GalleryHttpResponse> GetAsync(Uri uri, int maxBytes, CancellationToken cancellationToken)
        {
            using (var handler = new HttpClientHandler { AllowAutoRedirect = false, Proxy = null, UseProxy = false })
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = maxBytes })
            using (HttpResponseMessage response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                Uri location = response.Headers.Location;
                long? length = response.Content.Headers.ContentLength;
                if (response.StatusCode >= HttpStatusCode.MultipleChoices && response.StatusCode < HttpStatusCode.BadRequest)
                    return new GalleryHttpResponse((int)response.StatusCode, new byte[0], null, length, location);
                if (length.HasValue && length.Value > maxBytes) throw new InvalidDataException("主题图库响应超过大小限制。");
                using (Stream input = await response.Content.ReadAsStreamAsync())
                using (MemoryStream output = new MemoryStream())
                {
                    byte[] buffer = new byte[81920];
                    int read;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                    {
                        if (output.Length + read > maxBytes) throw new InvalidDataException("主题图库响应超过大小限制。");
                        output.Write(buffer, 0, read);
                    }
                    return new GalleryHttpResponse((int)response.StatusCode, output.ToArray(), response.Content.Headers.ContentType == null ? null : response.Content.Headers.ContentType.MediaType, length, location);
                }
            }
        }
    }
}
