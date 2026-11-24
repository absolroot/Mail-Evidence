using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit.Text;

namespace MailViewer.Core;

public sealed record EvidenceLocation(ByteRange Range, CompoundStreamLocator? Stream = null,
    PstPropertyLocator? Property = null, ContainerEvidence? Container = null);
public sealed record ObservedLink(string Target, string Display, string Key, string Host,
    EvidenceLocation Location, int Line, int Column, string Part, bool DomainMismatch, bool DomainComparisonUnavailable);
public sealed record AttachmentObservation(EvidenceLocation? NameLocation, string NameField,
    bool DoubleExtension, bool DirectionControl, ByteRange? PeHeader, bool PeUnreadable)
{
    public ushort? PeOptionalMagic { get; init; }
    public AttachmentBoundary? Boundary { get; init; }
    public bool BoundaryLimited { get; init; }
}

internal static partial class InvestigationArtifacts
{
    private static readonly Lazy<SuffixRules> Suffixes = new(LoadSuffixes);
    private static readonly HashSet<string> Decoys = new(StringComparer.OrdinalIgnoreCase)
        { "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "rtf", "txt", "csv", "odt", "ods",
          "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "webp", "svg", "heic" };
    private static readonly HashSet<string> Executables = new(StringComparer.OrdinalIgnoreCase)
        { "exe", "com", "scr", "pif", "dll", "msi", "msp", "bat", "cmd", "ps1", "vbs", "vbe",
          "js", "jse", "wsf", "wsh", "hta", "lnk", "cpl" };

    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlainUrls();

    public static (IReadOnlyList<ObservedLink> Links, bool Limited) ReadLinks(string text, string mediaType, string part,
        EvidenceLocation location, CancellationToken ct)
    {
        const int maximumLinks = 4096;
        var links = new List<ObservedLink>();
        if (mediaType == "text/plain")
        {
            int line = 1, lineStart = 0, scanned = 0;
            foreach (Match match in PlainUrls().Matches(text))
            {
                ct.ThrowIfCancellationRequested();
                while (scanned < match.Index)
                {
                    if (text[scanned++] == '\n') { line++; lineStart = scanned; }
                }
                Add(match.Value.TrimEnd('.', ',', ';'), "", line, match.Index - lineStart + 1);
                if (links.Count >= maximumLinks) return (links.AsReadOnly(), true);
            }
        }
        else if (mediaType == "text/html")
        {
            var tokenizer = new HtmlTokenizer(new StringReader(text)) { DecodeCharacterReferences = true };
            var label = new StringBuilder();
            string? href = null;
            int line = 1, column = 1;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int nextLine = tokenizer.LineNumber, nextColumn = tokenizer.LinePosition;
                if (!tokenizer.ReadNextToken(out var token)) break;
                if (token is HtmlTagToken tag && tag.Name.Equals("a", StringComparison.OrdinalIgnoreCase))
                {
                    if (href is not null) Add(href, label.ToString().Trim(), line, column);
                    if (links.Count >= maximumLinks) return (links.AsReadOnly(), true);
                    label.Clear();
                    href = tag.IsEndTag || tag.IsEmptyElement ? null : tag.Attributes
                        .FirstOrDefault(a => a.Name.Equals("href", StringComparison.OrdinalIgnoreCase))?.Value;
                    (line, column) = (nextLine, nextColumn);
                }
                else if (href is not null && token is HtmlDataToken data && token.Kind == HtmlTokenKind.Data)
                {
                    const int maximumLabel = 4096;
                    int remaining = maximumLabel - label.Length;
                    if (remaining > 0) label.Append(data.Data.AsSpan(0, Math.Min(remaining, data.Data.Length)));
                }
            }
            if (href is not null) Add(href, label.ToString().Trim(), line, column);
        }
        return (links.AsReadOnly(), false);

        void Add(string target, string display, int line, int column)
        {
            if (!TryUrl(target, out var key, out var host)) return;
            string? shownHost = DisplayHost(display);
            string? actual = shownHost is null ? null : ComparisonDomain(host);
            string? shown = shownHost is null ? null : ComparisonDomain(shownHost);
            links.Add(new ObservedLink(target, display, key, host, location, line, column, part,
                actual is not null && shown is not null && actual != shown,
                shownHost is not null && (actual is null || shown is null)));
        }
    }

    private static string? DisplayHost(string value)
    {
        if (TryUrl(value, out _, out var host)) return host;
        return value.Contains('.') && Uri.CheckHostName(value) is UriHostNameType.Dns or UriHostNameType.IPv4
            ? Host(value) : null;
    }

    public static bool TryUrl(string value, out string key, out string host)
    {
        key = host = "";
        value = value.Trim();
        int separator = value.IndexOf("://", StringComparison.Ordinal);
        if (separator < 0 || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") || Host(uri.Host) is not { } normalized)
            return false;
        int authorityStart = separator + 3;
        int end = value.IndexOfAny(['/', '?', '#'], authorityStart);
        string authority = end < 0 ? value[authorityStart..] : value[authorityStart..end];
        int userInfoEnd = authority.LastIndexOf('@');
        string userInfo = userInfoEnd >= 0 ? authority[..(userInfoEnd + 1)] : "";
        string port = uri.IsDefaultPort ? "" : $":{uri.Port.ToString(CultureInfo.InvariantCulture)}";
        host = normalized;
        string bracketed = normalized.Contains(':') ? $"[{normalized}]" : normalized;
        key = $"{uri.Scheme}://{userInfo}{bracketed}{port}{(end < 0 ? "" : value[end..])}";
        return true;
    }

    public static string? Host(string value)
    {
        value = value.TrimEnd('.');
        string literal = value.Trim('[', ']');
        if (IPAddress.TryParse(literal, out var ip)) return ip.ToString();
        if (Uri.CheckHostName(value) != UriHostNameType.Dns) return null;
        try { return new IdnMapping { UseStd3AsciiRules = true }.GetAscii(value).ToLowerInvariant(); }
        catch (ArgumentException) { return null; }
    }

    private static string? ComparisonDomain(string host)
    {
        if (IPAddress.TryParse(host, out _)) return host;
        var rules = Suffixes.Value;
        var labels = host.Split('.');
        int suffix = 0;
        for (int i = 0; i < labels.Length; i++)
        {
            string tail = string.Join('.', labels, i, labels.Length - i);
            int length = labels.Length - i;
            if (rules.Exception.Contains(tail)) { suffix = length - 1; break; }
            if (rules.Exact.Contains(tail)) suffix = Math.Max(suffix, length);
            if (i > 0 && rules.Wildcard.Contains(tail)) suffix = Math.Max(suffix, length + 1);
        }
        return suffix == 0 || suffix >= labels.Length ? null :
            string.Join('.', labels, labels.Length - suffix - 1, suffix + 1);
    }

    private static SuffixRules LoadSuffixes()
    {
        using var input = new StreamReader(typeof(InvestigationArtifacts).Assembly
            .GetManifestResourceStream("MailViewer.Core.public_suffix_list.dat")!);
        var rules = new SuffixRules([], [], []);
        while (input.ReadLine() is { } line)
        {
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line.StartsWith("*.", StringComparison.Ordinal))
                rules.Wildcard.Add(Host(line[2..]) ?? line[2..]);
            else if (line.StartsWith('!'))
                rules.Exception.Add(Host(line[1..]) ?? line[1..]);
            else
                rules.Exact.Add(Host(line) ?? line);
        }
        return rules;
    }

    private sealed record SuffixRules(HashSet<string> Exact, HashSet<string> Wildcard, HashSet<string> Exception);

    public static AttachmentObservation? InspectAttachment(string name, EvidenceLocation? location,
        string field, Stream? data)
    {
        if (location is null) return null;
        string[] parts = name.TrimEnd(' ', '.').Split('.');
        bool decoy = parts.Length > 1 && Decoys.Contains(parts[^1]);
        bool doubled = parts.Length > 2 && Decoys.Contains(parts[^2]) && Executables.Contains(parts[^1]);
        bool direction = name.Any(c => c is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069');
        ByteRange? pe = null;
        ushort? magic = null;
        bool unreadable = false;
        if (decoy)
        {
            if (data is null) unreadable = true;
            else (pe, magic, unreadable) = InspectPe(data);
        }
        var (boundary, limited) = pe is null ? AttachmentSignatures.Inspect(name, data) : (null, false);
        return new AttachmentObservation(location, field, doubled, direction, pe, unreadable)
            { Boundary = boundary, BoundaryLimited = limited, PeOptionalMagic = magic };
    }

    private static (ByteRange? Range, ushort? Magic, bool Unreadable) InspectPe(Stream data)
    {
        try
        {
            Span<byte> dos = stackalloc byte[64];
            if (data.Length < 2) return (null, null, false);
            data.ReadExactly(dos[..2]);
            if (dos[0] != 'M' || dos[1] != 'Z') return (null, null, false);
            if (data.Length < dos.Length) return (null, null, true);
            data.ReadExactly(dos[2..]);
            long offset = BinaryPrimitives.ReadUInt32LittleEndian(dos[60..]);
            Span<byte> header = stackalloc byte[26];
            if (offset < dos.Length || offset > data.Length - header.Length) return (null, null, true);
            data.Position = offset;
            data.ReadExactly(header);
            if (!header[..4].SequenceEqual("PE\0\0"u8)) return (null, null, false);
            int size = BinaryPrimitives.ReadUInt16LittleEndian(header[20..]);
            ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(header[24..]);
            int minimum = magic switch
            {
                0x10b => 96,
                0x20b => 112,
                _ => int.MaxValue
            };
            return size >= minimum && data.Length - offset - 24 >= size
                ? (new ByteRange(offset, 24 + size), magic, false) : (null, null, true);
        }
        catch (IOException) { return (null, null, true); }
        finally { data.Position = 0; }
    }
}
