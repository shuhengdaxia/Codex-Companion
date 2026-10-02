using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Authentication;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal sealed class UpdateRelease
{
    public string tag_name { get; set; }
    public bool draft { get; set; }
    public bool prerelease { get; set; }
    public UpdateAsset[] assets { get; set; }
}

internal sealed class UpdateAsset
{
    public string name { get; set; }
    public string browser_download_url { get; set; }
    public long size { get; set; }
}

internal sealed class PendingUpdate
{
    public Version Version;
    public string FilePath;
    public string Sha256;
}

internal static class AutoUpdater
{
    internal const string ExecutableName = "CodexCompanion.exe";
    internal const string HashName = "CodexCompanion.exe.sha256";
    internal const string Repository = "shuhengdaxia/Codex-Companion";
    internal const string ReleaseApi = "https://api.github.com/repos/" + Repository + "/releases/latest";
    private const long MaximumExecutableBytes = 64L * 1024 * 1024;

    internal static Version CurrentVersion { get { return Assembly.GetExecutingAssembly().GetName().Version; } }

    internal static Version ParseReleaseVersion(string tag)
    {
        if (String.IsNullOrWhiteSpace(tag) || tag[0] != 'v') throw new InvalidDataException("发布版本标签无效。");
        Version version;
        if (!Version.TryParse(tag.Substring(1), out version) || version.Build < 0 || version.Revision >= 0)
            throw new InvalidDataException("发布版本标签无效。");
        return version;
    }

    internal static string ParseHash(string body)
    {
        if (body == null) throw new InvalidDataException("缺少更新校验文件。");
        string[] parts = body.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[1] != ExecutableName || parts[0].Length != 64)
            throw new InvalidDataException("更新校验文件格式无效。");
        foreach (char c in parts[0])
            if (!Uri.IsHexDigit(c)) throw new InvalidDataException("更新校验值无效。");
        return parts[0].ToLowerInvariant();
    }

    internal static UpdateAsset FindAsset(UpdateRelease release, string name)
    {
        if (release == null || release.draft || release.prerelease || release.assets == null)
            throw new InvalidDataException("没有可用的稳定版本。");
        UpdateAsset found = null;
        foreach (UpdateAsset asset in release.assets)
        {
            if (asset.name != name) continue;
            if (found != null) throw new InvalidDataException("更新资产名称重复。");
            ValidateAssetUrl(asset.browser_download_url, release.tag_name, name);
            found = asset;
        }
        if (found == null) throw new InvalidDataException("发布版本缺少 " + name + "。");
        return found;
    }

    internal static void ValidateAssetUrl(string address, string tag, string name)
    {
        Uri uri;
        if (!Uri.TryCreate(address, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host != "github.com" || uri.Port != 443 || !String.IsNullOrEmpty(uri.UserInfo) ||
            !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath != "/" + Repository + "/releases/download/" + tag + "/" + name)
            throw new InvalidDataException("更新资产地址不属于指定仓库。");
    }

    internal static async Task<PendingUpdate> CheckAndStageAsync()
    {
        using (var client = NewClient())
        {
            string json = await GetTextAsync(client, ReleaseApi, 512 * 1024);
            UpdateRelease release = new JavaScriptSerializer().Deserialize<UpdateRelease>(json);
            Version version = ParseReleaseVersion(release == null ? null : release.tag_name);
            if (version <= CurrentVersion) return null;
            UpdateAsset executable = FindAsset(release, ExecutableName);
            UpdateAsset hashAsset = FindAsset(release, HashName);
            if (executable.size < 1 || executable.size > MaximumExecutableBytes || hashAsset.size < 1 || hashAsset.size > 256)
                throw new InvalidDataException("更新资产大小无效。");
            string expectedHash = ParseHash(await GetTextAsync(client, hashAsset.browser_download_url, 256));
            string staging = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexCompanion", "updates", version.ToString());
            Directory.CreateDirectory(staging);
            string destination = await StageVerifiedAsync(
                client, executable.browser_download_url, staging, executable.size, expectedHash);
            return new PendingUpdate { Version = version, FilePath = destination, Sha256 = expectedHash };
        }
    }

    private static HttpClient NewClient()
    {
        var client = new HttpClient(new HttpClientHandler {
            AllowAutoRedirect = true, UseCookies = false, UseDefaultCredentials = false,
            SslProtocols = SslProtocols.Tls12
        });
        client.Timeout = TimeSpan.FromMinutes(3);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CodexCompanion-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static async Task<string> GetTextAsync(HttpClient client, string url, int limit)
    {
        using (HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("更新元数据过大。");
            using (Stream stream = await response.Content.ReadAsStreamAsync())
            using (var memory = new MemoryStream())
            {
                byte[] buffer = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    if (memory.Length + read > limit) throw new InvalidDataException("更新元数据过大。");
                    memory.Write(buffer, 0, read);
                }
                return new UTF8Encoding(false, true).GetString(memory.ToArray());
            }
        }
    }

    internal static async Task DownloadVerifiedAsync(HttpClient client, string url, string path, long expectedBytes, string expectedHash)
    {
        using (HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != expectedBytes)
                throw new InvalidDataException("更新文件大小与发布信息不符。");
            using (Stream input = await response.Content.ReadAsStreamAsync())
            using (FileStream output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] buffer = new byte[65536];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    total += count;
                    if (total > MaximumExecutableBytes || total > expectedBytes)
                        throw new InvalidDataException("更新文件超出发布大小。");
                    sha.TransformBlock(buffer, 0, count, buffer, 0);
                    await output.WriteAsync(buffer, 0, count);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                if (total != expectedBytes || !String.Equals(ToHex(sha.Hash), expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("更新文件校验失败。");
            }
        }
    }

    internal static async Task<string> StageVerifiedAsync(HttpClient client, string url, string staging, long expectedBytes, string expectedHash)
    {
        Directory.CreateDirectory(staging);
        string destination = Path.Combine(staging, ExecutableName);
        string partial = destination + "." + Guid.NewGuid().ToString("N") + ".download";
        using (FileStream publishLock = await AcquirePublishLockAsync(destination + ".lock"))
        {
            if (IsVerifiedFile(destination, expectedBytes, expectedHash)) return destination;
            try
            {
                await DownloadVerifiedAsync(client, url, partial, expectedBytes, expectedHash);
                if (File.Exists(destination)) File.Delete(destination);
                File.Move(partial, destination);
                return destination;
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }
    }

    private static async Task<FileStream> AcquirePublishLockAsync(string path)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (true)
        {
            FileStream result = null;
            try { result = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                if (timeout.Elapsed >= TimeSpan.FromMinutes(3)) throw new IOException("等待更新文件发布超时。");
            }
            if (result != null) return result;
            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    private static bool IsVerifiedFile(string path, long expectedBytes, string expectedHash)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length != expectedBytes) return false;
        using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (SHA256 sha = SHA256.Create())
            return String.Equals(ToHex(sha.ComputeHash(input)), expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    internal static string ToHex(byte[] bytes)
    {
        var result = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes) result.Append(value.ToString("x2"));
        return result.ToString();
    }

    internal static void LaunchInstaller(PendingUpdate update)
    {
        if (update == null || !File.Exists(update.FilePath)) throw new InvalidOperationException("更新文件不存在，请重新检查更新。");
        string helper = Path.Combine(Path.GetDirectoryName(update.FilePath), "CodexCompanion.Updater.exe");
        using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("CodexCompanionUpdater.exe"))
        {
            if (resource == null) throw new InvalidOperationException("更新程序未嵌入当前版本。");
            using (FileStream file = new FileStream(helper, FileMode.Create, FileAccess.Write, FileShare.None)) resource.CopyTo(file);
        }
        string target = Assembly.GetExecutingAssembly().Location;
        if (!String.Equals(Path.GetFileName(target), ExecutableName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请从发布包中的 CodexCompanion.exe 启动，以便自动更新。");
        var start = new ProcessStartInfo(helper) {
            Arguments = Process.GetCurrentProcess().Id + " \"" + target + "\" \"" + update.FilePath + "\" " + update.Sha256,
            UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target), CreateNoWindow = true
        };
        Process.Start(start);
    }
}
