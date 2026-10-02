using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class AdvertisementTests
{
    internal static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        SecurityProtocolType processProtocols = ServicePointManager.SecurityProtocol;
        using (var transport = AdvertisementClient.CreateHandler())
        {
            Assert(transport.SslProtocols == SslProtocols.Tls12, "Advertisement transport must negotiate TLS 1.2 even under legacy application defaults.");
            Assert(!transport.UseCookies && !transport.UseDefaultCredentials && !transport.AllowAutoRedirect,
                "TLS repair must preserve the anonymous, redirect-free feed boundary.");
            Assert(transport.ServerCertificateCustomValidationCallback == null, "TLS repair must retain normal server certificate validation.");
        }
        Assert(ServicePointManager.SecurityProtocol == processProtocols, "Advertisement TLS settings must not change other application requests.");
        Assert(AdvertisementClient.Parse("{\"items\":[]}").Length == 0, "Empty feed must contain no ads.");
        ExpectFailure(delegate { AdvertisementClient.Parse("{}"); }, "Missing items must fail.");
        ExpectFailure(delegate { AdvertisementClient.Parse("{\"items\":[{}]}"); }, "Missing content must fail.");
        ExpectFailure(delegate { AdvertisementClient.Parse("{\"items\":[{\"content\":\" \"}]}"); }, "Blank content must fail.");
        ExpectFailure(delegate { AdvertisementClient.Parse("{broken}"); }, "Malformed JSON must fail.");
        string longText = new string('文', 1000);
        Assert(AdvertisementClient.Parse(Json.Write(new { items = new[] { new { content = longText } } }))[0].Content.Length == 1000,
            "Maximum supported copy must remain available.");
        ExpectFailure(delegate { AdvertisementClient.Parse(Json.Write(new { items = new[] { new { content = longText + "字" } } })); },
            "Oversize copy must fail.");

        foreach (string link in new[] { "javascript:alert(1)", "file:///C:/Windows/notepad.exe", "shell:startup", "data:text/html,test", "https://user:pass@example.com/", "/relative", "https://example.com/\n" })
            Assert(Advertisement.NormalizeLink(link) == null, "Unsafe link accepted.");
        Assert(Advertisement.NormalizeLink("https://xai-tools.cn/#recharge") == AppController.OfficialRechargeUrl, "Recharge link must be valid.");
        Assert(Advertisement.NormalizeLink("http://example.com/promotion") != null, "HTTP web links remain compatible with the feed.");
        Advertisement[] invalidLink = AdvertisementClient.Parse("{\"items\":[{\"content\":\"文本仍可展示\",\"linkUrl\":\"file:///C:/test\"}]}");
        Assert(invalidLink.Length == 1 && invalidLink[0].LinkUrl == null, "Unsafe link must not discard safe text.");

        var handler = new FeedHandler(HttpStatusCode.OK, "{\"items\":[]}");
        Assert(new AdvertisementClient(handler, TimeSpan.FromSeconds(1)).FetchAsync(CancellationToken.None).GetAwaiter().GetResult().Length == 0,
            "Valid HTTP feed must load.");
        Assert(handler.Url == "https://xai-tools.cn/api/advertisements/companion" && handler.WasAnonymous, "Feed must use the companion subroute without credentials.");
        ExpectFailure(delegate { new AdvertisementClient(new FeedHandler(HttpStatusCode.ServiceUnavailable, "{}"), TimeSpan.FromSeconds(1))
            .FetchAsync(CancellationToken.None).GetAwaiter().GetResult(); }, "HTTP errors must fail.");
        ExpectFailure(delegate { new AdvertisementClient(new FeedHandler(HttpStatusCode.Found, "{}"), TimeSpan.FromSeconds(1))
            .FetchAsync(CancellationToken.None).GetAwaiter().GetResult(); }, "Redirects must not masquerade as a feed.");
        ExpectFailure(delegate { new AdvertisementClient(new FeedHandler(HttpStatusCode.OK, new string('x', AdvertisementClient.MaxResponseBytes + 1)), TimeSpan.FromSeconds(1))
            .FetchAsync(CancellationToken.None).GetAwaiter().GetResult(); }, "Oversize HTTP payload must fail.");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            ExpectFailure(delegate { new AdvertisementClient(new FeedHandler(HttpStatusCode.OK, "{}"), TimeSpan.FromSeconds(1))
                .FetchAsync(cancelled.Token).GetAwaiter().GetResult(); }, "Closing must cancel the request.");
        }

        var items = new[] {
            new Advertisement("连接 XAI Tools，开启高效创作。访问充值中心，随时管理账户余额。", AppController.OfficialRechargeUrl),
            new Advertisement("第二条推广：" + longText, "https://example.com/second")
        };
        using (var form = new MainForm(new AppController(Path.Combine(directory, "data"), Path.Combine(directory, "codex")), true, new PreviewThemeGalleryService()))
        {
            form.ShowInTaskbar = false;
            form.Opacity = 0;
            form.Show();
            form.Location = new Point(-20000, -20000);
            Application.DoEvents();
            Size normalSize = form.ClientSize;
            TableLayoutPanel rootLayout = Field<TableLayoutPanel>(form, "rootLayout");
            Control connection = Field<TextBox>(form, "apiKeyTextBox");
            while (connection.Parent != null && connection.Parent != rootLayout) connection = connection.Parent;
            Assert(connection.Parent == rootLayout, "The compact configuration section must be present.");
            Point originalConnection = connection.PointToScreen(Point.Empty);
            var banner = FindBanner(form);
            Assert(banner != null && !banner.Visible, "Banner must initially be hidden.");
            form.SetAdvertisements(new Advertisement[0]);
            Assert(form.ClientSize == normalSize && connection.PointToScreen(Point.Empty) == originalConnection,
                "Empty feed must keep the original layout.");
            form.SavePreview(Path.Combine(directory, "without-ads.png"));
            form.SetAdvertisements(items);
            Application.DoEvents();
            Assert(banner.Visible && banner.Current.LinkUrl == items[0].LinkUrl, "First ad must show its own destination.");
            Assert(connection.PointToScreen(Point.Empty).Y > originalConnection.Y, "Ad banner must move connection section down.");
            var text = Field<Label>(banner, "textLabel");
            var outgoing = Field<Label>(banner, "outgoingLabel");
            var paging = Field<TableLayoutPanel>(banner, "paging");
            var textViewport = Field<Panel>(banner, "textViewport");
            var details = Field<Button>(banner, "detailsButton");
            Assert(banner.Width >= 400 && textViewport.Height >= 18 && ContainsControl(banner, paging) && ContainsControl(banner, details),
                "Compact banner content and actions must remain readable and unclipped.");
            var rotation = Field<System.Windows.Forms.Timer>(banner, "rotationTimer");
            var animation = Field<System.Windows.Forms.Timer>(banner, "animationTimer");
            Assert(paging.Visible && rotation.Enabled, "Multiple ads must show paging and start rotation.");
            form.SavePreview(Path.Combine(directory, "with-ads.png"));
            banner.MoveSelection(1);
            Assert(banner.Current.Content == items[1].Content && banner.Current.LinkUrl == items[1].LinkUrl,
                "Paging must keep each ad paired with its URL and full copy.");
            Assert(animation.Enabled && outgoing.Visible && text.Left > 0 && outgoing.Left == 0,
                "Next ad must enter horizontally from the right.");
            PumpEvents(80);
            Assert(outgoing.Left < 0 && text.Left > 0, "Both advertisements must move inside the clipped viewport.");
            form.SavePreview(Path.Combine(directory, "sliding-ad.png"));
            PumpEvents(300);
            Assert(!animation.Enabled && !outgoing.Visible && text.Left == 0 && text.Bounds == text.Parent.ClientRectangle,
                "Animation must finish with exactly one correctly sized advertisement.");
            form.SetAdvertisements(items);
            Assert(banner.Current.LinkUrl == items[1].LinkUrl, "Refresh must preserve current selection.");
            form.SavePreview(Path.Combine(directory, "long-ad.png"));
            banner.MoveSelection(1);
            Assert(banner.Current.LinkUrl == items[0].LinkUrl, "Paging must wrap.");
            banner.MoveSelection(-1);
            Assert(banner.Current.LinkUrl == items[1].LinkUrl && text.Left < 0, "Previous ad must enter from the left.");
            form.Width += 40;
            Application.DoEvents();
            Assert(!animation.Enabled && text.Bounds == text.Parent.ClientRectangle, "Resize must settle an active slide.");
            form.Width -= 40;
            form.SetAdvertisements(new[] { items[0] });
            Assert(!paging.Visible && !rotation.Enabled && !animation.Enabled, "One advertisement must not show paging or run timers.");
            banner.MoveSelection(1);
            Assert(banner.Current == items[0], "A single ad must remain selected.");
            form.SavePreview(Path.Combine(directory, "single-ad.png"));
            form.SetAdvertisements(items);
            rotation.Interval = 40;
            PumpUntil(delegate { return banner.Current == items[1]; }, 1500);
            rotation.Stop();
            Assert(banner.Current == items[1], "Multiple advertisements must advance automatically.");
            PumpEvents(300);
            var wheel = new HandledMouseEventArgs(MouseButtons.None, 0, 10, 10, 120);
            typeof(Control).GetMethod("OnMouseWheel", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(text, new object[] { wheel });
            Assert(wheel.Handled && banner.Current == items[0], "Wheel navigation must switch once and consume the event.");
            Field<Button>(banner, "nextButton").Focus();
            Assert(banner.ContainsFocus, "Paging must remain keyboard accessible.");
            Advertisement focusedItem = banner.Current;
            PumpEvents(120);
            Assert(banner.Current == focusedItem, "Automatic rotation must pause during keyboard interaction.");
            Message keyMessage = new Message();
            object[] keyArguments = { keyMessage, Keys.Right };
            Assert((bool)typeof(AdvertisementBanner).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(banner, keyArguments) && banner.Current == items[1], "Right arrow must switch the focused banner.");
            form.SetAdvertisements(new Advertisement[0]);
            Application.DoEvents();
            Assert(!banner.Visible && form.ClientSize == normalSize && connection.PointToScreen(Point.Empty) == originalConnection,
                "Removing all ads must restore the layout.");
            Assert(!rotation.Enabled && !animation.Enabled, "Clearing ads during a slide must stop both timers.");
            form.ClientSize = new Size(normalSize.Width, Math.Max(normalSize.Height, Screen.FromControl(form).WorkingArea.Height - (form.Height - form.ClientSize.Height)));
            Size constrainedSize = form.ClientSize;
            form.SetAdvertisements(items);
            form.SetAdvertisements(new Advertisement[0]);
            Assert(form.ClientSize == constrainedSize, "A screen-constrained window must not shrink after an ad visibility cycle.");
            form.ClientSize = normalSize;
            form.SetAdvertisements(items);
            form.SetAdvertisements(new Advertisement[0]);
            Assert(form.ClientSize == normalSize, "Repeated visibility cycles must not accumulate height changes.");
            form.SetAdvertisements(items);
            form.WindowState = FormWindowState.Maximized;
            Application.DoEvents();
            form.SetAdvertisements(new Advertisement[0]);
            form.WindowState = FormWindowState.Normal;
            Application.DoEvents();
            Assert(form.ClientSize == normalSize && !banner.Visible,
                "Removing ads while maximized must restore the original normal window size. Expected " + normalSize + ", actual " + form.ClientSize + ", state " + form.WindowState);
            form.WindowState = FormWindowState.Maximized;
            Application.DoEvents();
            form.SetAdvertisements(items);
            form.WindowState = FormWindowState.Normal;
            Application.DoEvents();
            Assert(banner.Visible && connection.PointToScreen(Point.Empty).Y > originalConnection.Y,
                "Ads received while maximized must retain their layout after restoring the window.");
            form.SetAdvertisements(new Advertisement[0]);
            Assert(form.ClientSize == normalSize, "Hiding ads received while maximized must reclaim their added height.");
            form.Close();
            Assert(!rotation.Enabled && !animation.Enabled, "Closing the window must stop advertisement timers.");
        }
        Console.WriteLine("Advertisement subroute, link safety, cancellation, horizontal rotation, input navigation and layout checks passed.");
    }

    private static T Field<T>(object target, string name)
    {
        return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
    }

    private static void PumpEvents(int milliseconds)
    {
        PumpUntil(delegate { return false; }, milliseconds);
    }

    private static void PumpUntil(Func<bool> done, int milliseconds)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < milliseconds && !done())
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    private static AdvertisementBanner FindBanner(Control root)
    {
        foreach (Control control in root.Controls)
        {
            if (control is AdvertisementBanner) return (AdvertisementBanner)control;
            AdvertisementBanner nested = FindBanner(control);
            if (nested != null) return nested;
        }
        return null;
    }

    private static bool ContainsControl(Control parent, Control child)
    {
        Rectangle bounds = parent.RectangleToClient(child.RectangleToScreen(child.ClientRectangle));
        return parent.ClientRectangle.Contains(bounds);
    }

    private static Control FindText(Control root, string text)
    {
        if (root.Text == text) return root;
        foreach (Control control in root.Controls)
        {
            Control found = FindText(control, text);
            if (found != null) return found;
        }
        return null;
    }

    private static void Assert(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    private static void ExpectFailure(Action action, string message)
    {
        try { action(); } catch { return; }
        throw new InvalidOperationException(message);
    }

    private sealed class FeedHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode status;
        private readonly string body;
        internal string Url;
        internal bool WasAnonymous;
        internal FeedHandler(HttpStatusCode status, string body) { this.status = status; this.body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Url = request.RequestUri.AbsoluteUri;
            WasAnonymous = !request.Headers.Any() && request.Content == null && request.Method == HttpMethod.Get;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
