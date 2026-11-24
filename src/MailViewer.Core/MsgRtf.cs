using System.Buffers.Binary;

namespace MailViewer.Core;

internal static class MsgRtf
{
    private const uint Compressed = 0x75465A4C;
    private const uint Uncompressed = 0x414C454D;
    private const int HeaderLength = 16;
    private const int DictionaryLength = 4096;

    // MS-OXRTFCP section 2.1.2.1 defines the initial LZFu dictionary.
    private static ReadOnlySpan<byte> InitialDictionary =>
        "{\\rtf1\\ansi\\mac\\deff0\\deftab720{\\fonttbl;}{\\f0\\fnil \\froman \\fswiss \\fmodern \\fscript \\fdecor MS Sans SerifSymbolArialTimes New RomanCourier{\\colortbl\\red0\\green0\\blue0\r\n\\par \\pard\\plain\\f0\\fs20\\b\\i\\u\\tab\\tx"u8;

    public static byte[] Decompress(ReadOnlySpan<byte> source, int maxLength, CancellationToken ct)
    {
        if (source.Length < HeaderLength)
            throw new InvalidDataException("압축된 RTF의 헤더가 잘렸습니다.");
        var compressedLength = BinaryPrimitives.ReadUInt32LittleEndian(source);
        var rawLength = BinaryPrimitives.ReadUInt32LittleEndian(source[4..]);
        var compressionKind = BinaryPrimitives.ReadUInt32LittleEndian(source[8..]);
        var checksum = BinaryPrimitives.ReadUInt32LittleEndian(source[12..]);
        var contents = source[HeaderLength..];
        if (compressedLength != contents.Length + 12 || rawLength > maxLength)
            throw new InvalidDataException("압축된 RTF의 입력 또는 출력 길이가 올바르지 않습니다.");
        if (compressionKind == Uncompressed && checksum == 0 && rawLength == contents.Length)
            return contents.ToArray();
        if (compressionKind != Compressed || CalculateCrc(contents, ct) != checksum)
            throw new InvalidDataException("압축된 RTF의 형식 또는 CRC를 확인할 수 없습니다.");

        var output = new byte[checked((int)rawLength)];
        var dictionary = new byte[DictionaryLength];
        InitialDictionary.CopyTo(dictionary);
        var writeOffset = InitialDictionary.Length;
        var written = 0;
        var consumed = 0;
        while (consumed < contents.Length)
        {
            ct.ThrowIfCancellationRequested();
            var flags = contents[consumed++];
            for (var bit = 0; bit < 8; bit++)
            {
                if ((flags & (1 << bit)) == 0)
                {
                    if (consumed == contents.Length)
                        throw new InvalidDataException("압축된 RTF 리터럴이 잘렸습니다.");
                    Write(contents[consumed++]);
                    continue;
                }

                if (contents.Length - consumed < 2)
                    throw new InvalidDataException("압축된 RTF 사전 참조가 잘렸습니다.");
                var reference = BinaryPrimitives.ReadUInt16BigEndian(contents[consumed..]);
                consumed += 2;
                var readOffset = reference >> 4;
                if (readOffset == writeOffset)
                {
                    if (written == output.Length)
                        return output;
                    throw new InvalidDataException("압축된 RTF의 실제 출력 길이가 선언과 다릅니다.");
                }
                var count = (reference & 15) + 2;
                for (var j = 0; j < count; j++)
                {
                    if (readOffset >= InitialDictionary.Length + written)
                        throw new InvalidDataException("압축된 RTF의 사전 참조가 초기화 범위를 벗어났습니다.");
                    Write(dictionary[readOffset]);
                    readOffset = (readOffset + 1) % DictionaryLength;
                }
            }
        }
        throw new InvalidDataException("압축된 RTF의 종료 참조를 찾을 수 없습니다.");

        void Write(byte value)
        {
            if (written == output.Length)
                throw new InvalidDataException("압축된 RTF 출력이 선언된 길이를 넘습니다.");
            output[written++] = value;
            dictionary[writeOffset] = value;
            writeOffset = (writeOffset + 1) % DictionaryLength;
        }
    }

    private static uint CalculateCrc(ReadOnlySpan<byte> contents, CancellationToken ct)
    {
        uint crc = 0;
        for (var i = 0; i < contents.Length; i++)
        {
            if ((i & 0x3FFF) == 0)
                ct.ThrowIfCancellationRequested();
            crc ^= contents[i];
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320u);
        }
        return crc;
    }
}
