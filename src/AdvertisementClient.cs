using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal sealed class Advertisement
{
    internal string Content { get; private set; }
    internal string LinkUrl { get; private set; }

    internal Advertisement(string content, string linkUrl)
    {
        Content = content;
        LinkUrl = NormalizeLink(linkUrl);
    }

    internal static string NormalizeLink(string value)
    {
        if (String.IsNullOrWhiteSpace(value) || value.Length > 2048) return null;
        foreach (char character in value) if (Char.IsControl(character)) return null;
        Uri uri;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            uri.UserInfo.Length != 0 || String.IsNullOrWhiteSpace(uri.Host)) return null;
        return uri.AbsoluteUri;
    }
}

// Public, credential-free feed. Never shares the relay's API Key or browser cookies.
internal sealed class AdvertisementClient
{
    internal const int MaxResponseBytes = 1024 * 1024;
    private readonly HttpMessageHandler handler;
    private readonly TimeSpan timeout;

    internal AdvertisementClient() : this(null, TimeSpan.FromSeconds(8)) { }
    internal AdvertisementClient(HttpMessageHandler handler, TimeSpan timeout)
    {
        this.handler = handler;
        this.timeout = timeout;
    }

    internal static HttpClientHandler CreateHandler()
    {
        // This csc-built application otherwise inherits legacy SSL3/TLS 1.0 defaults.
        // Scope TLS to the public feed; leave process-wide protocols and certificate validation unchanged.
        return new HttpClientHandler {
            AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false,
            SslProtocols = SslProtocols.Tls12
        };
    }

    internal async Task<Advertisement[]> FetchAsync(CancellationToken cancellationToken)
    {
        using (var client = new HttpClient(handler ?? CreateHandler()))
        {
            client.Timeout = timeout;
            client.MaxResponseContentBufferSize = MaxResponseBytes;
            using (HttpResponseMessage response = await client.GetAsync(
                AppController.OfficialWebsiteUrl + "/api/advertisements/companion", cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                return Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            }
        }
    }

    internal static Advertisement[] Parse(string json)
    {
        Dictionary<string, object> body;
        try
        {
            body = new JavaScriptSerializer { MaxJsonLength = MaxResponseBytes, RecursionLimit = 16 }
                .DeserializeObject(json) as Dictionary<string, object>;
        }
        catch (ArgumentException error) { throw new InvalidDataException("广告数据格式无效。", error); }
        catch (InvalidOperationException error) { throw new InvalidDataException("广告数据格式无效。", error); }
        object rawItems;
        if (body == null || !body.TryGetValue("items", out rawItems) || !(rawItems is object[]))
            throw new InvalidDataException("广告接口未返回有效列表。");

        var result = new List<Advertisement>();
        foreach (object rawItem in (object[])rawItems)
        {
            var item = rawItem as Dictionary<string, object>;
            string content = item == null ? null : Json.Text(item, "content");
            if (String.IsNullOrWhiteSpace(content) || content.Length > 1000)
                throw new InvalidDataException("广告文字无效。");
            result.Add(new Advertisement(content.Trim(), Json.Text(item, "linkUrl")));
        }
        return result.ToArray();
    }
}
