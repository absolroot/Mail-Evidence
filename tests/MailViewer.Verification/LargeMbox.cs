using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using MailViewer.Core;
using Microsoft.Win32.SafeHandles;

namespace MailViewer.Verification;

internal static partial class Program
{
    private static void TestLargeMbox(string fixture, string output)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The large fixture uses a Windows sparse file.");

        const long secondEnvelopeOffset = 4L * 1024 * 1024 * 1024 + 128;
        var firstMessage = "From first@example.test Mon Nov 23 03:00:00 2026\r\n"u8;
        var secondEnvelope = "From second@example.test Mon Nov 23 03:01:00 2026\r\n"u8;
        var raw = Encoding.ASCII.GetString(File.ReadAllBytes(fixture));
        var expected = Encoding.ASCII.GetBytes(raw.Replace("\r\nFrom ", "\r\n>From ", StringComparison.Ordinal));
        var path = Path.Combine(output, "large-sparse.mbox");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            const uint setSparse = 0x000900c4;
            if (!DeviceIoControl(stream.SafeFileHandle, setSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to mark the test fixture sparse.");

            stream.Write(firstMessage);
            stream.Write("From: first@example.test\r\nSubject: skipped oversized message\r\n\r\n"u8);
            stream.Position = secondEnvelopeOffset - 4;
            stream.Write("\r\n\r\n"u8);
            stream.Write(secondEnvelope);
            stream.Write(expected);
            stream.Write("\r\n"u8);
        }

        using var session = EvidenceSession.Open([path]);
        var source = session.Sources.Single();
        var offset = secondEnvelopeOffset + secondEnvelope.Length;
        var range = new ByteRange(offset, expected.Length);
        var record = session.Records.Single(x => x.MessageRange.Offset == offset);
        Equal(range, record.MessageRange, "64-bit message range");
        SameBytes(expected, session.ReadRange(source.Id, range), "64-bit range read");
        Require(session.Issues.Count != 0, "oversized first message is represented as a read issue");
        var search = session.Search(new SearchRequest("BODY_ONLY_4C7F", SearchField.Body));
        Equal(record.Id, search.Hits.Single().Record.Id, "search across partial MBOX");
        var export = ExportForTest(session, new ExportRequest(output, ExportKind.MessageBytes,
            ExportScope.SearchResults, [record.Id], Search: new SearchRequest("BODY_ONLY_4C7F", SearchField.Body)));
        ValidatePackage(export);
        Equal(Hash(expected), export.Items.Single().Sha256, "export preserves the 64-bit original slice");
        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(export.PackageDirectory, "manifest.json")));
        var issues = manifest.RootElement.GetProperty("searchIssues");
        Require(issues.EnumerateArray().Any(x => x.GetProperty("code").GetString() == "message-too-large"),
            "partial search manifest records the skipped oversized region");
        Console.WriteLine($"    file size {source.Length:N0}, original offset {offset:N0}, exported {range.Length:N0} bytes");
    }

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode,
        IntPtr inputBuffer, uint inputLength, IntPtr outputBuffer, uint outputLength,
        out uint bytesReturned, IntPtr overlapped);
}
