using System.Globalization;
using System.Text;

namespace MailViewer.App;

internal enum AttachmentTextKind { Plain, Archive }

internal sealed record AttachmentTextPreview(string Text, string? EncodingName, int BytesShown, bool Truncated,
    AttachmentTextKind Kind = AttachmentTextKind.Plain, bool Encrypted = false)
{
    public static AttachmentTextPreview CreateArchiveListing(string text, bool encrypted, bool truncated) =>
        new(EscapeControls(text), null, 0, truncated,
            AttachmentTextKind.Archive, encrypted);

    public static AttachmentTextPreview Create(byte[] source)
    {
        const int textLimit = 32 * 1024;
        int count = Math.Min(source.Length, textLimit);
        ReadOnlySpan<byte> prefix = source.AsSpan(0, count);
        bool truncated = count < source.Length;
        (Encoding encoding, int preamble) = DetectEncoding(prefix);
        string? text = Decode(prefix[preamble..], encoding, truncated);
        if (text is not null && (preamble > 0 || LooksLikeText(text)))
            return new(EscapeControls(text), encoding.WebName, count, truncated);

        if (preamble == 0)
        {
            Encoding? korean = CodePagesEncodingProvider.Instance.GetEncoding(949,
                EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            if (korean is not null && Decode(prefix, korean, truncated) is { } decoded && LooksLikeText(decoded))
                return new(EscapeControls(decoded), "CP949", count, truncated);
        }

        const int projectionLimit = 8 * 1024;
        count = Math.Min(source.Length, projectionLimit);
        var characters = new StringBuilder(count + count / 64);
        for (int i = 0; i < count; i++)
        {
            if (i > 0 && i % 64 == 0) characters.Append('\n');
            byte value = source[i];
            characters.Append(value is >= 0x20 and <= 0x7e ? (char)value : '·');
        }
        return new(characters.ToString(), null, count, count < source.Length);
    }

    private static (Encoding Encoding, int Preamble) DetectEncoding(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0x00, 0x00, 0xfe, 0xff })) return (new UTF32Encoding(true, false, true), 4);
        if (bytes.StartsWith(new byte[] { 0xff, 0xfe, 0x00, 0x00 })) return (new UTF32Encoding(false, false, true), 4);
        if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) return (new UTF8Encoding(false, true), 3);
        if (bytes.StartsWith(new byte[] { 0xfe, 0xff })) return (new UnicodeEncoding(true, false, true), 2);
        if (bytes.StartsWith(new byte[] { 0xff, 0xfe })) return (new UnicodeEncoding(false, false, true), 2);
        return (new UTF8Encoding(false, true), 0);
    }

    private static string? Decode(ReadOnlySpan<byte> bytes, Encoding encoding, bool truncated)
    {
        var chars = new char[encoding.GetMaxCharCount(bytes.Length)];
        try
        {
            int length = encoding.GetDecoder().GetChars(bytes, chars, flush: !truncated);
            return new string(chars, 0, length);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static bool LooksLikeText(string value)
    {
        if (value.Contains('\0')) return false;
        int controls = value.Count(ch => char.IsControl(ch) && ch is not ('\r' or '\n' or '\t'));
        return controls == 0 || controls * 50 <= value.Length;
    }

    private static string EscapeControls(string value)
    {
        var display = new StringBuilder(value.Length);
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (rune.Value is not ('\r' or '\n' or '\t') &&
                Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                display.Append("\\u{").Append(rune.Value.ToString("X4", CultureInfo.InvariantCulture)).Append('}');
            }
            else
            {
                display.Append(rune.ToString());
            }
        }
        return display.ToString();
    }
}
