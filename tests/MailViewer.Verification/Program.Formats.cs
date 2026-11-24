using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MailViewer.Core;

namespace MailViewer.Verification;

internal static partial class Program
{
    private static void TestSharedFormatReaders(string root, string output)
    {
        string fixtures = Path.Combine(root, "tests", "fixtures");
        string extras = Path.Combine(fixtures, "rust-local");
        string path = Path.Combine(output, "format-readers");
        Directory.CreateDirectory(path);
        string message = "From: Test <from@example.test>\nTo: to@example.test\nDate: Mon, 23 Nov 2026 10:00:00 +0000\n"
            + "Subject: format reader control\nMessage-ID: <format-reader@example.test>\n"
            + "MIME-Version: 1.0\nContent-Type: multipart/mixed; boundary=control\n\n"
            + "--control\nContent-Type: text/plain; charset=utf-8\n\nKnown extended format body.\n"
            + "--control\nContent-Type: text/plain\nContent-Disposition: attachment; filename=check.txt\n"
            + "Content-Transfer-Encoding: base64\n\nb3JpZ2luYWwgYXR0YWNobWVudA==\n--control--\n";
        byte[] expectedAttachment = "original attachment"u8.ToArray();
        string zip = Path.Combine(path, "control.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("Inbox/control.eml").Open(), new UTF8Encoding(false)))
            writer.Write(message);

        string emlx = Write("control.emlx", $"{Encoding.UTF8.GetByteCount(message)}\n{message}<plist/>");
        string appleRoot = Directory.CreateDirectory(Path.Combine(path, "Apple")).FullName;
        Directory.CreateDirectory(Path.Combine(appleRoot, "Messages"));
        Directory.CreateDirectory(Path.Combine(appleRoot, "Attachments", "42", "2"));
        string partialText = message.Replace("Content-Transfer-Encoding: base64\n\nb3JpZ2luYWwgYXR0YWNobWVudA==",
            $"X-Apple-Content-Length: {expectedAttachment.Length}\n");
        string partial = Write(Path.Combine(appleRoot, "Messages", "42.partial.emlx"),
            $"{Encoding.UTF8.GetByteCount(partialText)}\n{partialText}<plist/>");
        string sidecar = Path.Combine(appleRoot, "Attachments", "42", "2", "check.txt");
        File.WriteAllBytes(sidecar, expectedAttachment);
        string babyl = Write("control.rmail", "BABYL OPTIONS:\nVersion: 5\nLabels:\n\u001f\f\n0, seen,,\n"
            + "*** EOOH ***\n" + message + "\u001f");
        string mmdf = Write("control.mmdf", "\u0001\u0001\u0001\u0001\n" + message + "\u0001\u0001\u0001\u0001\n");
        var newDir = Directory.CreateDirectory(Path.Combine(path, "maildir", "new")).FullName;
        Directory.CreateDirectory(Path.Combine(path, "maildir", "cur"));
        string maildir = Write(Path.Combine(newDir, "2026-11-23.machine"), message);
        string mh = Write("1", message);
        string seq = Write(".mh_sequences", "unseen: 1\n");
        string alias = Write("control.nws", message);
        string edb = Write("control.edb", "EDB identification only");
        string nsf = Write("control.nsf", "NSF identification only");
        string dbx = Directory.EnumerateFiles(Path.Combine(extras, "oe5-native"), "Drafts.dbx", SearchOption.AllDirectories).First();
        string mbx = Directory.EnumerateFiles(Path.Combine(extras, "oe4-native"), "Utkast.mbx", SearchOption.AllDirectories).First();
        string pstAnsi = Path.Combine(fixtures, "ost", "sdk-sample2.pst");
        string olm = Path.Combine(extras, "aspose-example.olm");
        string tnef = Path.Combine(extras, "tnef-one-file.tnef");
        string cms = Path.Combine(extras, "tika-signed.p7m");
        string[] paths = [zip, emlx, mmdf, babyl, partial, maildir, mh, seq, alias, edb, nsf, dbx, mbx, pstAnsi, olm, tnef, cms];
        var originals = paths.ToDictionary(item => item, item => Hash(File.ReadAllBytes(item)));
        using var session = EvidenceSession.Open(paths);
        Require(paths.All(item => session.Sources.Any(source => source.SelectedPath == item)),
            "all inputs have a source: " + string.Join(" | ", session.Issues.Select(issue => issue.Detail)));
        foreach (var (input, format) in new[]
        {
            (zip, MailFormat.Zip), (emlx, MailFormat.Emlx), (mmdf, MailFormat.Mmdf), (babyl, MailFormat.Babyl),
            (partial, MailFormat.Emlx), (maildir, MailFormat.Eml),
            (mh, MailFormat.Eml), (alias, MailFormat.Eml), (dbx, MailFormat.Dbx), (mbx, MailFormat.Mbx),
            (pstAnsi, MailFormat.Pst), (olm, MailFormat.Olm), (tnef, MailFormat.Tnef), (cms, MailFormat.Cms)
        })
        {
            var source = session.Sources.Single(item => item.SelectedPath == input);
            Equal(format, source.Format, $"format of {input}");
            Require(session.Records.Any(record => record.SourceId == source.Id), "listed records: " + input);
        }
        Require(session.Issues.Any(issue => issue.SourcePath == edb && issue.Code == "reader-unavailable")
            && session.Issues.Any(issue => issue.SourcePath == nsf && issue.Code == "reader-unavailable"),
            "identification-only sources report the limit");
        var archiveMail = session.Records.Single(record => record.Format == MailFormat.Zip);
        var item = archiveMail.Attachments.Single();
        SameBytes(expectedAttachment, session.ReadAttachment(archiveMail.Id, item.Id), "ZIP evidence attachment");
        Require(archiveMail.ContainerHeaders is { Kind: "archive_member", FileRange: null }, "archive header coordinates are member-relative");
        var raw = Encoding.UTF8.GetString(session.ReadRecordedHeader(archiveMail.Id, archiveMail.HeaderRange));
        Require(raw.Contains("Subject: format reader control", StringComparison.Ordinal), "raw archive header");
        Require(session.Search(new SearchRequest("Known extended format body.", SearchField.Body)).Hits.Count == 8,
            "shared search over ZIP, complete and partial EMLX, MMDF, Babyl, Maildir, MH and NWS");
        var partialRecord = session.Records.Single(item => item.SourcePath == partial);
        var sidecarAttachment = partialRecord.Attachments.Single();
        Require(sidecarAttachment.ContainerLocation is { Kind: "external_file" }, "partial EMLX records sidecar location");
        SameBytes(expectedAttachment, session.ReadAttachment(partialRecord.Id, sidecarAttachment.Id), "original Apple sidecar bytes");
        var folder = archiveMail.LogicalFolder ?? throw new InvalidDataException("Missing archive member folder");
        var scoped = session.Search(new SearchRequest(SourceId: archiveMail.SourceId, PstFolderNodeId: folder.NodeId));
        Equal(archiveMail.Id, scoped.Hits.Single().Record.Id, "archive folder scopes the result");
        var record = session.Records.First(row => row.Format == MailFormat.Dbx);
        Require(record.ContainerMessage is { Kind: "segments", FileRange: null }, "DBX pieces are not a file range");
        Require(session.ReadRecordedMessage(record.Id).Length == record.ContainerMessage!.Length, "DBX segmented bytes");
        var pst = session.Records.Single(record => record.SourcePath.Equals(pstAnsi, StringComparison.OrdinalIgnoreCase));
        Require(pst.PstMessage is not null && session.PstFolders.Any(folder => folder.SourceId == pst.SourceId), "ANSI PST folder and NID");
        var result = ExportService.Export(session,
            new ExportRequest(output, ExportKind.MessageBytes, ExportScope.SelectedMessages, [archiveMail.Id]));
        CreatedPackages.Add(result);
        Require(ExportService.Verify(result.PackageDirectory).Success, "stored archive member package verifies");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(result.PackageDirectory, "manifest.json")));
        Require(manifest.RootElement.GetProperty("items")[0].GetProperty("containerLocation").GetProperty("kind").GetString() == "archive_member",
            "manifest keeps the archive member address");
        foreach (var row in session.Records)
        {
            foreach (var attachment in row.Attachments.Where(a => a.Sha256 is not null && a.Issue is null))
                Equal(attachment.Sha256, Hash(session.ReadAttachment(row.Id, attachment.Id)), $"attachment evidence {row.Format}: {attachment.Name}");
        }
        foreach (var (file, hash) in originals)
            Equal(hash, Hash(File.ReadAllBytes(file)), "source kept read-only");

        string Write(string name, string text)
        {
            string file = Path.Combine(path, name);
            File.WriteAllText(file, text, new UTF8Encoding(false));
            return file;
        }
    }
}
