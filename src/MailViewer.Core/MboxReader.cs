using System.Buffers;
using System.Buffers.Text;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace MailViewer.Core;

internal static partial class MboxReader
{
    public const string BoundaryRule = "mboxrd-ctime-blank-line-v1";
    private const int MarkerLimit = 512;
    private const int HeaderProbeLimit = 128 * 1024;

    internal readonly record struct Entry(ByteRange Envelope, ByteRange Message, ByteRange Separator);
    private readonly record struct Marker(long Start, long End, long? PreviousBlank, bool Confirmed,
        long? DeclaredContentEnd = null, bool InvalidContentLength = false);

    public static bool HasEnvelopePrefix(SafeFileHandle handle)
    {
        Span<byte> prefix = stackalloc byte[5];
        return RandomAccess.Read(handle, prefix, 0) == prefix.Length && prefix.SequenceEqual("From "u8);
    }

    public static IReadOnlyList<Entry> Scan(
        SourceInfo source, SafeFileHandle handle, List<ReadIssue> issues, CancellationToken ct)
    {
        var markers = new List<Marker>();
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        var headerProbe = ArrayPool<byte>.Shared.Rent(HeaderProbeLimit);
        Span<byte> linePrefix = stackalloc byte[MarkerLimit];
        long lineStart = 0;
        long fileOffset = 0;
        long? previousBlank = null;
        var prefixLength = 0;

        try
        {
            while (fileOffset < source.Length)
            {
                ct.ThrowIfCancellationRequested();
                var count = RandomAccess.Read(handle, buffer.AsSpan(0, (int)Math.Min(buffer.Length, source.Length - fileOffset)), fileOffset);
                if (count == 0)
                    throw new EndOfStreamException("MBOX의 길이가 판독 중 달라졌습니다.");

                var slice = buffer.AsSpan(0, count);
                while (!slice.IsEmpty)
                {
                    var newline = slice.IndexOf((byte)'\n');
                    var pieceLength = newline >= 0 ? newline + 1 : slice.Length;
                    var copyCount = Math.Min(pieceLength, linePrefix.Length - prefixLength);
                    slice[..copyCount].CopyTo(linePrefix[prefixLength..]);
                    prefixLength += copyCount;
                    slice = slice[pieceLength..];

                    if (newline < 0)
                        continue;

                    var lineEnd = fileOffset + count - slice.Length;
                    var line = linePrefix[..prefixLength];
                    var lineLength = lineEnd - lineStart;
                    if (line.StartsWith("From "u8) && lineLength < MarkerLimit && IsEnvelope(line))
                    {
                        long? declaredContentEnd = null;
                        var invalidLength = false;
                        var confirmed = (lineStart == 0 || previousBlank.HasValue)
                            && ProbeHeaders(handle, source.Length, lineEnd, headerProbe, out declaredContentEnd, out invalidLength);
                        markers.Add(new Marker(lineStart, lineEnd, previousBlank, confirmed, declaredContentEnd, invalidLength));
                    }

                    previousBlank = line.SequenceEqual("\n"u8) || line.SequenceEqual("\r\n"u8)
                        ? lineStart
                        : null;
                    lineStart = lineEnd;
                    prefixLength = 0;
                }

                fileOffset += count;
            }

            if (lineStart < source.Length && prefixLength < MarkerLimit
                && linePrefix[..prefixLength].StartsWith("From "u8)
                && IsEnvelope(linePrefix[..prefixLength]))
            {
                markers.Add(new Marker(lineStart, source.Length, previousBlank, false));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<byte>.Shared.Return(headerProbe);
        }

        var entries = new List<Entry>(markers.Count);
        if (markers.Count == 0 || markers[0].Start > 0)
        {
            var prefixLength64 = markers.Count == 0 ? source.Length : markers[0].Start;
            issues.Add(new ReadIssue(source.Id, "mbox-envelope", "지원하는 MBOX 봉투를 찾지 못한 구간입니다. 메시지 수는 알 수 없습니다.",
                new ByteRange(0, prefixLength64)));
        }

        long quarantineUntil = 0;
        for (var index = 0; index < markers.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var marker = markers[index];
            var next = index + 1 < markers.Count ? markers[index + 1] : (Marker?)null;
            var limit = next?.Start ?? source.Length;
            if (marker.Start < quarantineUntil)
            {
                issues.Add(new ReadIssue(source.Id, "mbox-content-length",
                    "앞선 Content-Length가 이 구간을 본문으로 포함해 봉투 경계를 확정할 수 없습니다. 메시지 수는 알 수 없습니다.",
                    new ByteRange(marker.Start, limit - marker.Start)));
                continue;
            }
            if (!marker.Confirmed || next is { Confirmed: false })
            {
                issues.Add(new ReadIssue(source.Id, "mbox-boundary",
                    "봉투와 헤더 또는 분리 줄이 맞지 않아 메시지 경계를 확정할 수 없습니다. 메시지 수는 알 수 없습니다.",
                    new ByteRange(marker.Start, limit - marker.Start)));
                continue;
            }

            var separatorStart = next.HasValue ? next.Value.PreviousBlank : previousBlank;
            var messageEnd = Math.Clamp(separatorStart ?? limit, marker.End, limit);
            if (marker.InvalidContentLength || (marker.DeclaredContentEnd is { } declaredEnd && declaredEnd != messageEnd))
            {
                // A conflicting length can include subsequent apparent envelopes inside the claimed body.
                quarantineUntil = marker.DeclaredContentEnd ?? 0;
                issues.Add(new ReadIssue(source.Id, "mbox-content-length",
                    "Content-Length와 MBOX 메시지 경계가 일치하지 않습니다. 메시지 수는 알 수 없습니다.",
                    new ByteRange(marker.Start, limit - marker.Start)));
                continue;
            }
            entries.Add(new Entry(new ByteRange(marker.Start, marker.End - marker.Start),
                new ByteRange(marker.End, messageEnd - marker.End), new ByteRange(messageEnd, limit - messageEnd)));
        }

        return entries;
    }

    private static bool ProbeHeaders(SafeFileHandle handle, long fileLength, long offset, byte[] buffer,
        out long? declaredContentEnd, out bool invalidLength)
    {
        declaredContentEnd = null;
        invalidLength = false;
        var limit = (int)Math.Min(fileLength - offset, HeaderProbeLimit);
        var count = 0;
        var lineStart = 0;
        var searchStart = 0;
        var sawField = false;
        long? bodyLength = null;
        var previousWasLength = false;
        while (count < limit)
        {
            var read = RandomAccess.Read(handle, buffer.AsSpan(count, Math.Min(limit - count, 4096)), offset + count);
            if (read == 0)
                break;
            count += read;
            while (searchStart < count)
            {
                var found = buffer.AsSpan(searchStart, count - searchStart).IndexOf((byte)'\n');
                if (found < 0)
                {
                    searchStart = count;
                    break;
                }
                var newline = searchStart + found;
                var line = buffer.AsSpan(lineStart, newline - lineStart).TrimEnd((byte)'\r');
                searchStart = lineStart = newline + 1;
                if (line.IsEmpty)
                {
                    if (bodyLength is { } value)
                    {
                        var bodyOffset = offset + lineStart;
                        declaredContentEnd = value > long.MaxValue - bodyOffset ? long.MaxValue : bodyOffset + value;
                    }
                    return sawField;
                }
                if (line[0] is (byte)' ' or (byte)'\t')
                {
                    if (!sawField)
                        return false;
                    if (previousWasLength)
                        invalidLength = true;
                    continue;
                }
                var colon = line.IndexOf((byte)':');
                if (colon <= 0 || !IsFieldName(line[..colon]))
                    return false;
                sawField = true;
                previousWasLength = IsContentLength(line[..colon]);
                if (!previousWasLength)
                    continue;

                var valueBytes = TrimHorizontalWhitespace(line[(colon + 1)..]);
                if (bodyLength.HasValue
                    || !Utf8Parser.TryParse(valueBytes, out long followingLength, out var consumed)
                    || consumed != valueBytes.Length || followingLength < 0)
                {
                    invalidLength = true;
                }
                else
                {
                    bodyLength = followingLength;
                }
            }
        }

        return false;
    }

    private static ReadOnlySpan<byte> TrimHorizontalWhitespace(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty && bytes[0] is (byte)' ' or (byte)'\t')
            bytes = bytes[1..];
        while (!bytes.IsEmpty && bytes[^1] is (byte)' ' or (byte)'\t')
            bytes = bytes[..^1];
        return bytes;
    }

    private static bool IsContentLength(ReadOnlySpan<byte> name)
    {
        ReadOnlySpan<byte> expected = "content-length"u8;
        if (name.Length != expected.Length)
            return false;
        for (var index = 0; index < expected.Length; index++)
            if ((name[index] | 0x20) != expected[index])
                return false;
        return true;
    }

    private static bool IsFieldName(ReadOnlySpan<byte> name)
    {
        foreach (var ch in name)
            if (ch is < 33 or > 126)
                return false;
        return true;
    }

    private static bool IsEnvelope(ReadOnlySpan<byte> line) =>
        EnvelopePattern().IsMatch(Encoding.ASCII.GetString(line).TrimEnd('\r', '\n'));

    [GeneratedRegex(@"^From [^\s]+ (?:Mon|Tue|Wed|Thu|Fri|Sat|Sun) (?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) [ ]?[0-9]{1,2} [0-9]{2}:[0-9]{2}:[0-9]{2}(?: (?:[+-][0-9]{4}|[A-Z]{1,5}))? [0-9]{4}(?: (?:[+-][0-9]{4}|[A-Z]{1,5}))?(?: remote from [^\s]+)?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex EnvelopePattern();
}
