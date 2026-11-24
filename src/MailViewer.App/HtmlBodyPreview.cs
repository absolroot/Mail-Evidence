using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Text;
using HtmlAgilityPack;
using MailViewer.Core;

namespace MailViewer.App;

internal sealed class HtmlBodyPreview
{
    internal const string Origin = "https://mail-preview.invalid";
    internal const string ContentPolicy = "default-src 'none'; img-src https://mail-preview.invalid; style-src 'unsafe-inline'; "
        + "base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
    private const int ImageLimit = 2 * 1024 * 1024;
    private const int TotalImageLimit = 16 * 1024 * 1024;
    private const int MaxImages = 48;

    private static readonly HashSet<string> Tags = new(("a abbr b bdi bdo blockquote br caption center cite code col colgroup dd del div dl dt em font "
        + "h1 h2 h3 h4 h5 h6 hr i img ins kbd li mark ol p pre q s samp small span strike strong sub sup table tbody td tfoot th thead tr tt u ul var wbr")
        .Split(' '), StringComparer.Ordinal);
    private static readonly HashSet<string> VoidTags = new("br col hr img wbr".Split(' '), StringComparer.Ordinal);
    private static readonly HashSet<string> Attributes = new(("align alt bgcolor border bordercolor cellpadding cellspacing class clear "
        + "color colspan dir face height id lang role rowspan scope size span start style title type valign width")
        .Split(' '), StringComparer.Ordinal);

    private static readonly HttpClient RemoteClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectCallback = ConnectToPublicHostAsync,
        ConnectTimeout = TimeSpan.FromSeconds(5)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    internal sealed record ImageData(byte[] Bytes, string MediaType);
    internal sealed record LinkTarget(string Address, string Host);
    private readonly Dictionary<string, ImageData> _local = [];
    private readonly Dictionary<string, Uri> _remote = [];
    private readonly Dictionary<string, LinkTarget> _links = new(StringComparer.Ordinal);

    private HtmlBodyPreview(string token) => BodyUri = $"{Origin}/{token}/body.html";

    public string BodyUri { get; }
    public byte[] Html { get; private set; } = [];
    public bool ActiveContentRemoved { get; private set; }
    public bool OtherResourcesBlocked { get; private set; }
    public int RemoteImageCount => _remote.Count;
    public IReadOnlyDictionary<string, ImageData> LocalImages => _local;
    internal bool TryGetLink(string previewUri, out LinkTarget? target) => _links.TryGetValue(previewUri, out target);

    public static HtmlBodyPreview Create(string html, EvidenceSession session, string recordId, CancellationToken ct)
    {
        var result = new HtmlBodyPreview(Guid.NewGuid().ToString("N"));
        var document = new HtmlDocument { OptionMaxNestedChildNodes = 96 };
        document.LoadHtml(html);
        result.ActiveContentRemoved = document.DocumentNode.Descendants().Any(node =>
            node.Name is "script" or "iframe" or "frame" or "object" or "embed" or "form" or "svg" or "math");
        var markup = new StringBuilder(html.Length + 512);
        markup.Append("""
            <!doctype html><html lang="ko"><head><meta charset="utf-8"><meta name="referrer" content="no-referrer">
            <style>html{color:#242424;background:#fff}body{margin:0;font:14px/1.4 'Segoe UI',sans-serif;overflow-wrap:anywhere}table,img{max-width:100%}img{height:auto}pre{white-space:pre-wrap}a{color:inherit}a[href]{cursor:pointer}</style>
            """);

        foreach (HtmlNode node in document.DocumentNode.Descendants("style"))
        {
            ct.ThrowIfCancellationRequested();
            string css = node.InnerHtml;
            if (!css.Contains('<') && css.Length <= 128 * 1024)
            {
                markup.Append("<style>").Append(css).Append("</style>");
                if (css.Contains("url(", StringComparison.OrdinalIgnoreCase) || css.Contains("@import", StringComparison.OrdinalIgnoreCase))
                    result.OtherResourcesBlocked = true;
            }
        }

        var body = document.DocumentNode.Descendants("body").FirstOrDefault();
        markup.Append("</head><body");
        if (body is not null) result.WriteAttributes(body, markup);
        markup.Append('>');
        foreach (HtmlNode node in (body ?? document.DocumentNode).ChildNodes)
            result.WriteNode(node, markup, session, recordId, ct);
        markup.Append("</body></html>");
        result.Html = Encoding.UTF8.GetBytes(markup.ToString());
        return result;
    }

    private void WriteNode(HtmlNode node, StringBuilder markup, EvidenceSession session, string recordId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (node.NodeType == HtmlNodeType.Text)
        {
            markup.Append(WebUtility.HtmlEncode(HtmlEntity.DeEntitize(((HtmlTextNode)node).Text)));
            return;
        }
        if (node.NodeType != HtmlNodeType.Element) return;
        string tag = node.Name;
        if (tag is "link" or "video" or "audio" or "source" or "picture")
            OtherResourcesBlocked = true;

        if (!Tags.Contains(tag))
        {
            if (tag is "html" or "picture" or "section" or "article" or "main" or "header" or "footer")
                foreach (HtmlNode child in node.ChildNodes) WriteNode(child, markup, session, recordId, ct);
            return;
        }

        markup.Append('<').Append(tag);
        WriteAttributes(node, markup);
        if (tag == "img")
            WriteImage(node, markup, session, recordId, ct);
        else if (tag == "a")
            WriteLink(node, markup);
        markup.Append('>');
        if (VoidTags.Contains(tag)) return;
        foreach (HtmlNode child in node.ChildNodes) WriteNode(child, markup, session, recordId, ct);
        markup.Append("</").Append(tag).Append('>');
    }

    private void WriteLink(HtmlNode node, StringBuilder output)
    {
        string address = HtmlEntity.DeEntitize(node.GetAttributeValue("href", "")).Trim();
        if (address.Length is 0 or > 8192 || address.Any(char.IsControl) ||
            !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(uri.Host)) return;
        string key = BodyUri[..BodyUri.LastIndexOf('/')] + $"/link/{_links.Count}";
        _links.Add(key, new LinkTarget(address, uri.IdnHost));
        WriteAttribute(output, "href", key);
    }

    private void WriteAttributes(HtmlNode node, StringBuilder output)
    {
        foreach (HtmlAttribute attribute in node.Attributes)
        {
            string name = attribute.Name;
            if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                ActiveContentRemoved = true;
            if (name is "srcset" or "background" ||
                (name == "style" && attribute.Value?.Contains("url(", StringComparison.OrdinalIgnoreCase) == true))
                OtherResourcesBlocked = true;
            if (!Attributes.Contains(name)) continue;
            string value = HtmlEntity.DeEntitize(attribute.Value ?? "");
            WriteAttribute(output, name, value);
        }
    }

    private void WriteImage(HtmlNode node, StringBuilder markup, EvidenceSession session, string recordId, CancellationToken ct)
    {
        string source = HtmlEntity.DeEntitize(node.GetAttributeValue("src", "")).Trim();
        if (source.Length == 0) return;
        if (_local.Count + _remote.Count >= MaxImages)
        {
            OtherResourcesBlocked = true;
            return;
        }
        string resource = BodyUri[..BodyUri.LastIndexOf('/')] + $"/image/{_local.Count + _remote.Count}";
        if (Uri.TryCreate(source, UriKind.Absolute, out Uri? remote) && remote.Scheme is "http" or "https")
        {
            if (remote.Scheme == "https" && remote.IsDefaultPort && remote.UserInfo.Length == 0 &&
                remote.HostNameType == UriHostNameType.Dns && remote.IdnHost.Contains('.') && !remote.IsLoopback)
            {
                string? existing = _remote.FirstOrDefault(image => image.Value.AbsoluteUri == remote.AbsoluteUri).Key;
                if (existing is not null) resource = existing;
                else _remote.Add(resource, remote);
                WriteAttribute(markup, "src", resource);
            }
            else
                OtherResourcesBlocked = true;
            return;
        }

        byte[]? data = ReadLocalImage(source, session, recordId, ImageLimit, ct);
        if (data is { Length: > 0 and <= ImageLimit } && ImageType(data) is { } mediaType &&
            _local.Values.Sum(value => value.Bytes.Length) + data.Length <= TotalImageLimit)
        {
            _local.Add(resource, new ImageData(data, mediaType));
            WriteAttribute(markup, "src", resource);
        }
        else
            OtherResourcesBlocked = true;
    }

    internal static byte[]? ReadFirstLocalImage(EvidenceSession session, string recordId, string partPath, CancellationToken ct)
    {
        string? html = session.ReadHtmlBody(recordId, partPath, ct);
        if (html is null || !html.Contains("<img", StringComparison.OrdinalIgnoreCase)) return null;
        var document = new HtmlDocument { OptionMaxNestedChildNodes = 96 };
        document.LoadHtml(html);
        foreach (HtmlNode node in document.DocumentNode.Descendants("img").Take(MaxImages))
        {
            ct.ThrowIfCancellationRequested();
            string source = HtmlEntity.DeEntitize(node.GetAttributeValue("src", "")).Trim();
            byte[]? bytes = ReadLocalImage(source, session, recordId, ImageLimit, ct);
            if (bytes is { Length: > 0 and <= ImageLimit } && ImageType(bytes) is "image/jpeg" or "image/png")
                return bytes;
        }
        return null;
    }

    private static byte[]? ReadLocalImage(string source, EvidenceSession session, string recordId, int limit, CancellationToken ct)
    {
        byte[]? data = null;
        if (source.StartsWith("cid:", StringComparison.OrdinalIgnoreCase) && source.Length < 1024)
        {
            try { data = session.ReadInlineBodyImage(recordId, Uri.UnescapeDataString(source[4..]), limit, ct); }
            catch (Exception error) when (error is IOException or FormatException or ArgumentException or NotSupportedException) { }
        }
        else if (source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && source.Length <= limit * 2)
        {
            int separator = source.IndexOf(',');
            if (separator > 0 && source.AsSpan(0, separator).EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            {
                try { data = Convert.FromBase64String(source[(separator + 1)..]); }
                catch (FormatException) { }
            }
        }
        return data;
    }

    public async Task<IReadOnlyDictionary<string, ImageData>> LoadRemoteImagesAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, ImageData>(_local);
        int remaining = TotalImageLimit - _local.Values.Sum(value => value.Bytes.Length);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(18));
        try
        {
            foreach (var (resource, target) in _remote)
            {
                budget.Token.ThrowIfCancellationRequested();
                if (remaining <= 0) break;
                ImageData? image = await DownloadImageAsync(target, Math.Min(remaining, ImageLimit), budget.Token).ConfigureAwait(false);
                if (image is null) continue;
                remaining -= image.Bytes.Length;
                result.Add(resource, image);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        return result;
    }

    private static async Task<ImageData?> DownloadImageAsync(Uri target, int limit, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            using var response = await RemoteClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > limit ||
                response.Content.Headers.ContentType?.MediaType is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/bmp"))
                return null;
            using var output = new MemoryStream();
            using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var buffer = new byte[8192];
            int length;
            while ((length = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (output.Length + length > limit) return null;
                output.Write(buffer, 0, length);
            }
            byte[] bytes = output.ToArray();
            return ImageType(bytes) is { } type ? new ImageData(bytes, type) : null;
        }
        catch (Exception error) when (error is HttpRequestException or IOException || (error is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    private static async ValueTask<Stream> ConnectToPublicHostAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
        foreach (IPAddress address in addresses.Where(IsPublicAddress))
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("The image host did not resolve to an available public IPv4 address.");
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        return b.Length == 4 && b[0] is > 0 and < 224 and not (10 or 127) &&
            !(b[0] == 100 && b[1] is >= 64 and <= 127) &&
            !(b[0] == 169 && b[1] == 254) &&
            !(b[0] == 172 && b[1] is >= 16 and <= 31) &&
            !(b[0] == 192 && (b[1] == 168 || b[1] == 0 && b[2] is 0 or 2 || b[1] == 88 && b[2] == 99)) &&
            !(b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100)) &&
            !(b[0] == 203 && b[1] == 0 && b[2] == 113);
    }

    private static string? ImageType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a })) return "image/png";
        if (bytes.StartsWith(new byte[] { 0xff, 0xd8, 0xff })) return "image/jpeg";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return "image/gif";
        if (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..].StartsWith("WEBP"u8)) return "image/webp";
        if (bytes.StartsWith("BM"u8)) return "image/bmp";
        return null;
    }

    private static void WriteAttribute(StringBuilder output, string name, string value) =>
        output.Append(' ').Append(name).Append("=\"").Append(WebUtility.HtmlEncode(value)).Append('"');
}

