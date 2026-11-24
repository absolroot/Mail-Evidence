using System.Buffers;
using System.Buffers.Binary;

namespace MailViewer.Core;

public enum AttachmentBoundaryKind { HeaderMismatch, StructureMismatch, MissingEnd, TrailingBytes }

public sealed record AttachmentBoundary(AttachmentBoundaryKind Kind, string Format, string Expected, string Observed,
    ByteRange Range)
{
    public string? ObservedFormat { get; init; }
    public AttachmentLengthComparison? LengthComparison { get; init; }
    public AttachmentBytePreview? Preview { get; init; }
}

public sealed record AttachmentLengthComparison(string? Chunk, uint Declared, int Available);
public sealed record AttachmentBytePreview(long Offset, ReadOnlyMemory<byte> Bytes);

internal static class AttachmentSignatures
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    private const int StructureLimit = 8 * 1024 * 1024;
    private static readonly Dictionary<string, Signature> Formats = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<Signature> Known = [];

    private sealed record Signature(string Name, string Header, Func<byte[], int, bool> Matches,
        Func<ReadOnlyMemory<byte>, AttachmentBoundary?>? Inspect = null);

    static AttachmentSignatures()
    {
        Add(new("PNG", "89 50 4E 47 0D 0A 1A 0A", (b, n) => b.AsSpan(0, n).StartsWith(PngSignature), Png), ".png");
        Add(new("JPEG", "FF D8 FF", (b, n) => n >= 3 && b[0] == 0xff && b[1] == 0xd8 && b[2] == 0xff, Jpeg),
            ".jpg", ".jpeg", ".jpe", ".jfif");
        Add(new("GIF", "GIF87a / GIF89a", (b, n) => n >= 6 && (b.AsSpan(0, 6).SequenceEqual("GIF87a"u8) ||
            b.AsSpan(0, 6).SequenceEqual("GIF89a"u8)), Gif), ".gif");
        Add(new("PDF", "%PDF-n.n", PdfHeader, Pdf), ".pdf");
        Add(new("ZIP", "50 4B 03 04 / 50 4B 05 06", (b, n) => n >= 4 &&
            BinaryPrimitives.ReadUInt32LittleEndian(b) is 0x04034b50 or 0x06054b50, Zip),
            ".zip", ".docx", ".xlsx", ".pptx", ".hwpx", ".docm", ".xlsm", ".pptm", ".odt", ".ods", ".odp", ".epub");
        Add(new("WebP", "RIFF · WEBP", (b, n) => RiffHeader(b, n, "WEBP"u8), b => Riff(b, "WebP")), ".webp");
        Add(new("WAVE", "RIFF · WAVE / RF64 · WAVE", (b, n) => RiffHeader(b, n, "WAVE"u8) ||
            n >= 12 && b.AsSpan(0, 4).SequenceEqual("RF64"u8) && b.AsSpan(8, 4).SequenceEqual("WAVE"u8),
            b => Riff(b, "WAVE")), ".wav", ".wave");
        Add(new("CFB/HWP", "D0 CF 11 E0 A1 B1 1A E1 / HWP Document File", (b, n) =>
            n >= 8 && BinaryPrimitives.ReadUInt64LittleEndian(b) == 0xE11AB1A1E011CFD0UL ||
            b.AsSpan(0, n).StartsWith("HWP Document File V"u8)), ".hwp");
        Add(new("7z", "37 7A BC AF 27 1C", (b, n) => n >= 6 && b.AsSpan(0, 6).SequenceEqual(
            new byte[] { 0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c })), ".7z");
        Add(new("GZIP", "1F 8B 08", (b, n) => n >= 3 && b[0] == 0x1f && b[1] == 0x8b && b[2] == 8), ".gz", ".tgz");
        Add(new("RAR", "52 61 72 21 1A 07 00 / 01 00", (b, n) => n >= 7 &&
            b.AsSpan(0, 6).SequenceEqual("Rar!\x1a\x07"u8) && (b[6] == 0 || n >= 8 && b[6] == 1 && b[7] == 0)), ".rar");
        Add(new("TIFF", "49 49 2A 00 / 4D 4D 00 2A · BigTIFF 43", (b, n) => n >= 4 &&
            BinaryPrimitives.ReadUInt32LittleEndian(b) is 0x002a4949 or 0x2a004d4d or 0x002b4949 or 0x2b004d4d), ".tif", ".tiff");
        Add(new("BMP", "42 4D", (b, n) => n >= 2 && b.AsSpan(0, 2).SequenceEqual("BM"u8)), ".bmp");
        Add(new("CFB", "D0 CF 11 E0 A1 B1 1A E1", (b, n) => n >= 8 &&
            BinaryPrimitives.ReadUInt64LittleEndian(b) == 0xE11AB1A1E011CFD0UL));
    }

    private static void Add(Signature signature, params string[] extensions)
    {
        Known.Add(signature);
        foreach (string extension in extensions) Formats.Add(extension, signature);
    }

    internal static (AttachmentBoundary? Boundary, bool Limited) Inspect(string name, Stream? stream)
    {
        if (!Formats.TryGetValue(Path.GetExtension(name.TrimEnd(' ', '.')), out var expected)) return default;
        if (stream is null || !stream.CanSeek) return (null, true);

        byte[] header = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            int size = (int)Math.Min(stream.Length, expected.Name == "PDF" ? 1024 : 32);
            stream.Position = 0;
            stream.ReadExactly(header.AsSpan(0, size));
            if (!expected.Matches(header, size))
            {
                string seen = Known.FirstOrDefault(signature => signature.Matches(header, size))?.Name ?? "";
                int headerLength = Math.Min(size, 16);
                var mismatch = new AttachmentBoundary(AttachmentBoundaryKind.HeaderMismatch, expected.Name, expected.Header,
                    seen.Length == 0 ? Hex(header.AsSpan(0, headerLength)) : $"{seen} · {Hex(header.AsSpan(0, headerLength))}",
                    new(0, headerLength)) { ObservedFormat = seen.Length > 0 ? seen : null };
                return (WithPreview(mismatch, header.AsSpan(0, size)), false);
            }
            if (expected.Inspect is null) return default;
            if (stream.Length > StructureLimit) return (null, true);
            int length = (int)stream.Length;
            byte[] data = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                stream.Position = 0;
                stream.ReadExactly(data.AsSpan(0, length));
                var boundary = expected.Inspect(data.AsMemory(0, length));
                return (boundary is null ? null : WithPreview(boundary, data.AsSpan(0, length)), false);
            }
            finally { ArrayPool<byte>.Shared.Return(data, clearArray: true); }
        }
        catch (IOException) { return (null, true); }
        finally
        {
            stream.Position = 0;
            ArrayPool<byte>.Shared.Return(header, clearArray: true);
        }
    }

    private static AttachmentBoundary WithPreview(AttachmentBoundary boundary, ReadOnlySpan<byte> bytes)
    {
        int start = (int)Math.Max(0, boundary.Range.Offset - 8);
        int end = (int)Math.Min(bytes.Length, boundary.Range.Offset + 16);
        return boundary with { Preview = new(start, bytes[start..end].ToArray()) };
    }

    private static bool PdfHeader(byte[] b, int n)
    {
        var header = b.AsSpan(0, n);
        int start = header.IndexOf("%PDF-"u8);
        return start >= 0 && n - start >= 8 && Digit(header[start + 5]) &&
            header[start + 6] == (byte)'.' && Digit(header[start + 7]);
    }

    private static bool Digit(byte value) => value is >= (byte)'0' and <= (byte)'9';

    private static bool RiffHeader(byte[] b, int n, ReadOnlySpan<byte> type) =>
        n >= 12 && b.AsSpan(0, 4).SequenceEqual("RIFF"u8) && b.AsSpan(8, 4).SequenceEqual(type);

    private static AttachmentBoundary? Png(ReadOnlyMemory<byte> input)
    {
        ReadOnlySpan<byte> data = input.Span;
        int offset = 8;
        while (offset <= data.Length - 12)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
            var kind = data.Slice(offset + 4, 4);
            if (offset == 8 && (length != 13 || !kind.SequenceEqual("IHDR"u8)))
                return Structure("PNG", "00 00 00 0D · IHDR", data, offset, 8);
            int available = data.Length - offset - 12;
            if (length > available)
                return Structure("PNG", $"PNG chunk length ≤ {available} bytes", data, offset, 4) with
                { LengthComparison = new(PngChunkName(kind), length, available) };
            int next = offset + (int)length + 12;
            if (kind.SequenceEqual("IEND"u8))
            {
                if (length != 0) return Structure("PNG", "00 00 00 00 · IEND", data, offset, 4);
                if (BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 8)..]) != 0xae426082)
                    return Structure("PNG", "AE 42 60 82 · IEND CRC", data, offset + 8, 4);
                return After("PNG", "IEND", data, next);
            }
            offset = next;
        }
        return Missing("PNG", "IEND · 00 00 00 00 49 45 4E 44 AE 42 60 82", data.Length);
    }

    private static string? PngChunkName(ReadOnlySpan<byte> kind)
    {
        foreach (byte value in kind)
            if (value is not (>= (byte)'A' and <= (byte)'Z') and not (>= (byte)'a' and <= (byte)'z')) return null;
        return System.Text.Encoding.ASCII.GetString(kind);
    }

    private static AttachmentBoundary? Jpeg(ReadOnlyMemory<byte> input)
    {
        ReadOnlySpan<byte> data = input.Span;
        int offset = 2;
        bool scan = false, multiPicture = false;
        while (offset < data.Length)
        {
            if (scan)
            {
                int next = data[offset..].IndexOf((byte)0xff);
                if (next < 0) break;
                offset += next;
            }
            int marker = offset;
            if (data[offset++] != 0xff)
                return Structure("JPEG", "FF · Marker", data, marker, 1);
            while (offset < data.Length && data[offset] == 0xff) offset++;
            if (offset == data.Length) break;
            byte code = data[offset++];
            if (code == 0xd9)
                return multiPicture ? null : After("JPEG", "EOI · FF D9", data, offset);
            if (scan && (code == 0 || code is >= 0xd0 and <= 0xd7)) continue;
            bool inScan = scan;
            scan = false;
            if (code == 0x01 || code is >= 0xd0 and <= 0xd7) continue;
            if (code is 0 or 0xd8) return Structure("JPEG", "JPEG marker", data, marker, offset - marker);
            if (offset > data.Length - 2) break;
            int size = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            if (size < 2 || size > data.Length - offset)
                return Structure("JPEG", "JPEG segment length", data, offset, 2);
            if (code == 0xe2 && size >= 6 && data.Slice(offset + 2, 4).SequenceEqual("MPF\0"u8)) multiPicture = true;
            scan = code == 0xda || code == 0xdc && inScan;
            offset += size;
        }
        return Missing("JPEG", "EOI · FF D9", data.Length);
    }

    private static AttachmentBoundary? Gif(ReadOnlyMemory<byte> input)
    {
        ReadOnlySpan<byte> data = input.Span;
        if (data.Length < 13) return Missing("GIF", "Trailer · 3B", data.Length);
        int offset = 13 + ColorTable(data[10]);
        while (offset < data.Length)
        {
            int at = offset;
            switch (data[offset++])
            {
                case 0x3b: return After("GIF", "Trailer · 3B", data, offset);
                case 0x21:
                    if (offset >= data.Length) return Missing("GIF", "Trailer · 3B", data.Length);
                    offset++;
                    break;
                case 0x2c:
                    if (offset > data.Length - 9) return Missing("GIF", "Trailer · 3B", data.Length);
                    offset += 9 + ColorTable(data[offset + 8]);
                    if (offset >= data.Length) return Missing("GIF", "Trailer · 3B", data.Length);
                    offset++;
                    break;
                default: return Structure("GIF", "21 / 2C / 3B · Block", data, at, 1);
            }
            while (offset < data.Length)
            {
                int subLength = data[offset++];
                if (subLength == 0) break;
                if (subLength > data.Length - offset)
                    return Structure("GIF", "GIF sub-block length", data, offset - 1, 1);
                offset += subLength;
            }
        }
        return Missing("GIF", "Trailer · 3B", data.Length);

        static int ColorTable(byte field) => (field & 0x80) == 0 ? 0 : 3 << ((field & 7) + 1);
    }

    private static AttachmentBoundary? Pdf(ReadOnlyMemory<byte> input)
    {
        ReadOnlySpan<byte> data = input.Span;
        var tail = data[Math.Max(0, data.Length - 131072)..];
        int baseOffset = data.Length - tail.Length;
        for (int i = tail.Length - 5; i >= 0; i--)
        {
            if (!tail[i..].StartsWith("%%EOF"u8) || i == 0 || !Space(tail[i - 1])) continue;
            int j = i - 1;
            while (j >= 0 && Space(tail[j])) j--;
            int lastDigit = j;
            while (j >= 0 && Digit(tail[j])) j--;
            if (j == lastDigit) continue;
            while (j >= 0 && Space(tail[j])) j--;
            if (j < 8 || !tail.Slice(j - 8, 9).SequenceEqual("startxref"u8)) continue;
            int end = baseOffset + i + 5;
            while (end < data.Length && Space(data[end])) end++;
            return After("PDF", "startxref · %%EOF", data, end);
        }
        return Missing("PDF", "startxref · %%EOF", data.Length);
    }

    private static bool Space(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    private static AttachmentBoundary? Zip(ReadOnlyMemory<byte> input)
    {
        ReadOnlySpan<byte> data = input.Span;
        int lower = Math.Max(0, data.Length - (22 + 65535 + 131072));
        int trailing = -1;
        for (int i = data.Length - 22; i >= lower; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data[i..]) != 0x06054b50) continue;
            int end = i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(data[(i + 20)..]);
            if (end > data.Length) continue;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 12)..]);
            uint central = BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 16)..]);
            ushort entries = BinaryPrimitives.ReadUInt16LittleEndian(data[(i + 10)..]);
            bool zip64 = i >= 20 && BinaryPrimitives.ReadUInt32LittleEndian(data[(i - 20)..]) == 0x07064b50;
            bool split = BinaryPrimitives.ReadUInt16LittleEndian(data[(i + 4)..]) != 0;
            if (zip64 || split || (ulong)central + size <= (ulong)i &&
                (entries == 0 && size == 0 && i == 0 || size >= 46 && central <= i - 4 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(data[(int)central..]) == 0x02014b50))
            {
                if (end == data.Length) return null;
                if (trailing < 0) trailing = end;
            }
        }
        return trailing >= 0 ? After("ZIP", "EOCD · 50 4B 05 06", data, trailing)
            : Missing("ZIP", "EOCD · 50 4B 05 06", data.Length);
    }

    private static AttachmentBoundary? Riff(ReadOnlyMemory<byte> input, string format)
    {
        var data = input.Span;
        if (!data.StartsWith("RIFF"u8)) return null; // RF64 carries its size in ds64.
        long size = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) + 8L;
        if (size < 12 || size > data.Length) return Structure(format, "RIFF size", data, 4, 4);
        return After(format, "RIFF size", data, (int)size);
    }

    private static AttachmentBoundary Structure(string format, string expected, ReadOnlySpan<byte> bytes, int start, int length) =>
        new(AttachmentBoundaryKind.StructureMismatch, format, expected, Hex(bytes.Slice(start, length)), new(start, length));

    private static AttachmentBoundary Missing(string format, string expected, int end) =>
        new(AttachmentBoundaryKind.MissingEnd, format, expected, "", new(end, 0));

    private static AttachmentBoundary? After(string format, string expected, ReadOnlySpan<byte> bytes, int end) =>
        end == bytes.Length ? null : new(AttachmentBoundaryKind.TrailingBytes, format, expected,
            Hex(bytes.Slice(end, Math.Min(16, bytes.Length - end))), new(end, bytes.Length - end));

    private static string Hex(ReadOnlySpan<byte> value) => BitConverter.ToString(value.ToArray()).Replace('-', ' ');
}

