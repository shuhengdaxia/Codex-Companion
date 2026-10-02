using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static class AutoUpdaterTests
{
    internal static void Run(string root)
    {
        Directory.CreateDirectory(root);
        Assert(AutoUpdater.ParseReleaseVersion("v1.2.3") == new Version(1, 2, 3), "stable tag");
        Reject(delegate { AutoUpdater.ParseReleaseVersion("1.2.3"); });
        Reject(delegate { AutoUpdater.ParseReleaseVersion("v1.2.3.4"); });
        string hash = new String('a', 64);
        Assert(AutoUpdater.ParseHash(hash + "  CodexCompanion.exe") == hash, "hash parse");
        Reject(delegate { AutoUpdater.ParseHash(hash + "  another.exe"); });
        Reject(delegate { AutoUpdater.ParseHash("abc  CodexCompanion.exe"); });
        string url = "https://github.com/shuhengdaxia/Codex-Companion/releases/download/v1.2.3/CodexCompanion.exe";
        AutoUpdater.ValidateAssetUrl(url, "v1.2.3", AutoUpdater.ExecutableName);
        Reject(delegate { AutoUpdater.ValidateAssetUrl(url.Replace("shuhengdaxia", "AhaRui-888"), "v1.2.3", AutoUpdater.ExecutableName); });
        Reject(delegate { AutoUpdater.ValidateAssetUrl(url.Replace("github.com", "example.com"), "v1.2.3", AutoUpdater.ExecutableName); });
        Reject(delegate { AutoUpdater.ValidateAssetUrl(url + "?other=1", "v1.2.3", AutoUpdater.ExecutableName); });
        Reject(delegate { AutoUpdater.ValidateAssetUrl(url.Replace("v1.2.3", "v1.2.4"), "v1.2.3", AutoUpdater.ExecutableName); });
        Assert(AutoUpdater.ParseReleasePageUrl(new Uri("https://github.com/shuhengdaxia/Codex-Companion/releases/tag/v1.2.3")) == new Version(1, 2, 3), "release page URL");
        Reject(delegate { AutoUpdater.ParseReleasePageUrl(new Uri("https://github.com/other/Codex-Companion/releases/tag/v1.2.3")); });
        Reject(delegate { AutoUpdater.ParseReleasePageUrl(new Uri("https://example.com/shuhengdaxia/Codex-Companion/releases/tag/v1.2.3")); });
        Reject(delegate { AutoUpdater.ParseReleasePageUrl(new Uri("https://github.com/shuhengdaxia/Codex-Companion/releases/tag/v1.2.3?x=1")); });
        Reject(delegate { AutoUpdater.ParseReleasePageUrl(new Uri("https://github.com/shuhengdaxia/Codex-Companion/releases/tag/1.2.3")); });
        UpdateRelease release = new UpdateRelease { tag_name = "v1.2.3", assets = new[] {
            new UpdateAsset { name = AutoUpdater.ExecutableName, browser_download_url = url, size = 1024 }
        } };
        Assert(AutoUpdater.FindAsset(release, AutoUpdater.ExecutableName).size == 1024, "release asset");
        release.assets = new[] { release.assets[0], release.assets[0] };
        Reject(delegate { AutoUpdater.FindAsset(release, AutoUpdater.ExecutableName); });
        release.prerelease = true;
        Reject(delegate { AutoUpdater.FindAsset(release, AutoUpdater.ExecutableName); });
        byte[] payload = Encoding.ASCII.GetBytes("verified update payload");
        string digest;
        using (SHA256 sha = SHA256.Create()) digest = AutoUpdater.ToHex(sha.ComputeHash(payload));
        string destination = Path.Combine(root, "download.exe");
        using (HttpClient client = new HttpClient(new BytesHandler(payload)))
            AutoUpdater.DownloadVerifiedAsync(client, "https://example.test/file", destination, payload.Length, digest).GetAwaiter().GetResult();
        Assert(File.ReadAllBytes(destination).Length == payload.Length, "verified download");
        using (HttpClient client = new HttpClient(new BytesHandler(payload)))
            Reject(delegate { AutoUpdater.DownloadVerifiedAsync(client, "https://example.test/file", destination, payload.Length + 1, digest).GetAwaiter().GetResult(); });
        using (HttpClient client = new HttpClient(new BytesHandler(payload)))
            Reject(delegate { AutoUpdater.DownloadVerifiedAsync(client, "https://example.test/file", destination, payload.Length, new String('0', 64)).GetAwaiter().GetResult(); });
        string unknownDestination = Path.Combine(root, "unknown-size.exe");
        using (HttpClient client = new HttpClient(new UnknownLengthBytesHandler(payload)))
            AutoUpdater.DownloadVerifiedAsync(client, "https://example.test/file", unknownDestination, -1, digest).GetAwaiter().GetResult();
        Assert(File.ReadAllBytes(unknownDestination).Length == payload.Length, "unknown-size verified download");
        using (HttpClient client = new HttpClient(new UnknownLengthBytesHandler(payload)))
            Reject(delegate { AutoUpdater.DownloadVerifiedAsync(client, "https://example.test/file", unknownDestination, -1, new String('0', 64)).GetAwaiter().GetResult(); });
        string emptyDigest;
        using (SHA256 sha = SHA256.Create()) emptyDigest = AutoUpdater.ToHex(sha.ComputeHash(new byte[0]));
        using (HttpClient client = new HttpClient(new UnknownLengthBytesHandler(new byte[0])))
            Reject(delegate { AutoUpdater.DownloadVerifiedAsync(client, "https://example.test/file", unknownDestination, -1, emptyDigest).GetAwaiter().GetResult(); });
        string unknownStageRoot = Path.Combine(root, "unknown-size-stage");
        var unknownHandler = new UnknownLengthBytesHandler(payload);
        using (HttpClient client = new HttpClient(unknownHandler))
        {
            AutoUpdater.StageVerifiedAsync(client, "https://example.test/file", unknownStageRoot, -1, digest).GetAwaiter().GetResult();
            AutoUpdater.StageVerifiedAsync(client, "https://example.test/file", unknownStageRoot, -1, digest).GetAwaiter().GetResult();
            Assert(unknownHandler.RequestCount == 1, "unknown-size verified destination reuse");
        }
        string concurrentRoot = Path.Combine(root, "concurrent");
        var handler = new CountingBytesHandler(payload);
        using (HttpClient client = new HttpClient(handler))
        {
            Task<string> first = AutoUpdater.StageVerifiedAsync(client, "https://example.test/file", concurrentRoot, payload.Length, digest);
            Task<string> second = AutoUpdater.StageVerifiedAsync(client, "https://example.test/file", concurrentRoot, payload.Length, digest);
            Task.WaitAll(first, second);
            Assert(first.Result == second.Result, "concurrent staging destination");
            Assert(handler.RequestCount == 1, "concurrent staging reuses verified destination");
            Assert(File.ReadAllBytes(first.Result).Length == payload.Length, "concurrent staged file");
            Assert(Directory.GetFiles(concurrentRoot, "*.download").Length == 0, "unique partial cleanup");
            AutoUpdater.StageVerifiedAsync(client, "https://example.test/file", concurrentRoot, payload.Length, digest).GetAwaiter().GetResult();
            Assert(handler.RequestCount == 1, "existing verified destination reuse");
        }
        Console.WriteLine("Updater: version, digest, release asset, URL, streamed download, and concurrent staging validation passed.");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid update input was accepted.");
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Update test failed: " + name);
    }

    private sealed class BytesHandler : HttpMessageHandler
    {
        private readonly byte[] body;
        internal BytesHandler(byte[] body) { this.body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    private sealed class CountingBytesHandler : HttpMessageHandler
    {
        private readonly byte[] body;
        private int requestCount;
        internal CountingBytesHandler(byte[] body) { this.body = body; }
        internal int RequestCount { get { return requestCount; } }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestCount);
            await Task.Delay(100, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        }
    }

    private sealed class UnknownLengthBytesHandler : HttpMessageHandler
    {
        private readonly byte[] body;
        private int requestCount;
        internal UnknownLengthBytesHandler(byte[] body) { this.body = body; }
        internal int RequestCount { get { return requestCount; } }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new UnknownLengthContent(body)
            });
        }
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] body;
        internal UnknownLengthContent(byte[] body) { this.body = body; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
        {
            return stream.WriteAsync(body, 0, body.Length);
        }
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
