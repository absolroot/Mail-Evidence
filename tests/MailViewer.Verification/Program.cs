using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MailViewer.Core;
using Microsoft.VisualBasic.FileIO;

namespace MailViewer.Verification;

internal static partial class Program
{
    private const string SyntheticEmlSha = "9a8106fd358cd0be272e515248196d4fd6cb31a21baae41607bc5360059c61ad";
    private const string SyntheticMboxSha = "390e94e0e8755c5230eadc30f488550d99700ddc70296f27a418f9a7ade36b95";
    private const string TikaEmlSha = "86e5feb18e16178359d6aaa704bd74ceebe7a41f302df5b656f5e6fe0ea34cc5";
    private const string ApacheMboxSha = "76f15052d9cd1a3c8157d945f2a3778f469f36d550bffd27c986bd6f0df8bfe7";
    private const string SyntheticAttachmentSha = "d38ed83ebf9c4794237889e647f15c54dfdc9a9c778c3193b055d9cc85b56ed0";
    private const string TikaAttachmentASha = "710b0ea810c12c93cae9982b60ca333e5302e72b8948b194f611513d4737c97d";
    private const string TikaAttachmentBSha = "41b47f8d34c25c3321a842944692ede16784b17e404531b97ade8ac19fcaa478";
    private static readonly List<ExportResult> CreatedPackages = [];

    private static int Main(string[] args)
    {
        var root = FindRoot();
        var fixtures = Path.Combine(root, "tests", "fixtures", "synthetic");
        var syntheticEml = Path.Combine(fixtures, "01-original.eml");
        var syntheticMbox = Path.Combine(fixtures, "synthetic.mbox");
        var tikaEml = Path.Combine(root, "tests", "fixtures", "external",
            "tika-multipart-attachments.eml");
        var apacheMbox = Path.Combine(root, "tests", "fixtures", "external", "public-apache-tika.mbox");
        using var work = new TemporaryDirectory();
        var cases = new List<(string Name, Action Test)>
        {
            ("synthetic EML headers, original bytes, MIME search and attachment", () => TestSyntheticEml(syntheticEml)),
            ("synthetic MBOX boundaries, duplicate Message-ID and date filter", () => TestSyntheticMbox(syntheticMbox)),
            ("selected EML and MBOX exports, manifest, same-size tamper and missing output",
                () => TestMessageExport(syntheticEml, syntheticMbox, work.Path)),
            ("CSV spreadsheet protection and source path retention", () => TestCsvNeutralization(work.Path)),
            ("cancellation, missing source, no overwrite and Windows original lock",
                () => TestFailureAndSourceReplacement(syntheticEml, work.Path)),
        };
        cases.Add(("investigation filters, exact keys, evidence ranges and export", () => TestInvestigation(work.Path)));
        cases.Add(("conversation references, Reply-To history, append and verification", () => TestConversation(root, work.Path)));
        if (!args.Contains("--synthetic-only", StringComparer.Ordinal))
        {
            cases.Add(("public Apache Tika EML attachment names, source and decoding", () => TestTikaEml(tikaEml)));
            cases.Add(("public Apache mailing list MBOX count, query and original ranges", () => TestApacheMbox(apacheMbox)));
            cases.Add(("public EML attachment export and verification", () => TestAttachmentExport(tikaEml, work.Path)));
            cases.Add(("public MBOX query scope, CSV and record order", () => TestSearchExport(apacheMbox, work.Path)));
            AddMsgChecks(cases, root, work.Path);
            AddPstChecks(cases, root, work.Path);
            AddOstChecks(cases, root, work.Path, args.Contains("--ost", StringComparer.Ordinal));
            AddExpandedOstChecks(cases, root, work.Path, args.Contains("--ost-expanded", StringComparer.Ordinal));
        }
        if (args.Contains("--large", StringComparer.Ordinal))
            cases.Add(("MBOX message starting past the 4 GiB boundary", () => TestLargeMbox(syntheticEml, work.Path)));

        var failed = 0;
        foreach (var (name, test) in cases)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                test();
                Console.WriteLine($"PASS {name} ({timer.ElapsedMilliseconds:N0} ms)");
            }
            catch (Exception error)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {name}: {error.GetType().Name}: {error.Message}");
            }
        }

        Console.WriteLine($"{cases.Count - failed}/{cases.Count} checks passed.");
        return failed == 0 ? 0 : 1;
    }

    private static void TestSyntheticEml(string path)
    {
        var original = File.ReadAllBytes(path);
        Equal(SyntheticEmlSha, Hash(original), "EML fixture SHA-256");
        using var session = EvidenceSession.Open([path]);
        var source = session.Sources.Single();
        var record = session.Records.Single();
        Equal(SyntheticEmlSha, source.Sha256, "source SHA-256");
        Equal(new ByteRange(0, original.Length), record.MessageRange, "whole message range");
        SameBytes(original, session.ReadRange(source.Id, record.MessageRange), "whole EML read");
        Equal("사건 메일 A", record.Subject, "RFC 2047 subject");
        Equal(new DateTimeOffset(2026, 11, 23, 3, 0, 0, TimeSpan.Zero),
            record.Date?.ToUniversalTime(), "Date UTC");
        Require(record.DateRaw.EndsWith("+0900", StringComparison.Ordinal), "Date header keeps original offset");

        var first = "Received: from relay-b.example.test by collector.example.test;\r\n Mon, 23 Nov 2026 12:01:03 +0900";
        var received = record.Headers.Where(x => x.Name.Equals("Received", StringComparison.OrdinalIgnoreCase)).ToArray();
        Equal(2, received.Length, "duplicate Received count");
        Require(received[0].Range.Offset < received[1].Range.Offset, "duplicate Received order");
        var firstRaw = Encoding.ASCII.GetString(session.ReadRange(source.Id, received[0].Range));
        Equal(first, firstRaw.TrimEnd('\r', '\n'), "folded Received bytes");
        var duplicate = record.Headers.Where(x => x.Name.Equals("X-Case-Marker", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Value.Trim()).ToArray();
        Require(duplicate.SequenceEqual(new[] { "FIRST", "SECOND" }), "duplicate custom header order");

        foreach (var (token, mediaType) in new[] { ("BODY_ONLY_4C7F", "text/plain"), ("HTML_ONLY_63DE", "text/html") })
        {
            var hit = session.Search(new SearchRequest(token, SearchField.Body)).Hits.Single();
            Equal(record.Id, hit.Record.Id, "body matched record");
            var part = record.BodyParts.Single(x => x.MediaType.Equals(mediaType, StringComparison.OrdinalIgnoreCase));
            Require(part.Text.Contains(token, StringComparison.Ordinal), "MIME body text");
            Require(hit.Matches.Any(x => x.Field == SearchField.Body && x.PartPath == part.PartPath), "matching MIME path");
        }

        var attachment = record.Attachments.Single();
        Equal("증거.bin", attachment.Name, "RFC 2231 attachment name");
        AssertAttachment(session, record, attachment, 8, SyntheticAttachmentSha);
        Equal(0, session.Search(new SearchRequest("missing-value-oracle")).Hits.Count, "zero query");
        Equal(SyntheticEmlSha, FileHash(path), "EML source unchanged");
    }

    private static void TestSyntheticMbox(string path)
    {
        var original = File.ReadAllBytes(path);
        Equal(SyntheticMboxSha, Hash(original), "MBOX fixture SHA-256");
        using var session = EvidenceSession.Open([path]);
        var source = session.Sources.Single();
        Equal(SyntheticMboxSha, source.Sha256, "source SHA-256");
        Equal(3, session.Records.Count, "MBOX message count");
        var expected = new[]
        {
            (Offset: 50L, Length: 1149L, Sha: "af844a4e3f8f5c2d371eee9d6f5fc84d8ebf6cc06f19c775bd3458bec4708783"),
            (Offset: 1249L, Length: 304L, Sha: "daec20650907c63ba2a449d29b3a1c46888b94ea261a6e25e5e794a342f61728"),
            (Offset: 1603L, Length: 315L, Sha: "dc9cb4eee5a02eb47d61dcb5b50d9bf16790b4c7a8c6ae95f8b473e4be9a20cb"),
        };
        foreach (var (record, item) in session.Records.Zip(expected))
        {
            Equal(new ByteRange(item.Offset, item.Length), record.MessageRange, "message position and size");
            Equal(item.Sha, Hash(session.ReadRange(source.Id, record.MessageRange)), "original MBOX slice SHA-256");
            Equal(new ByteRange(item.Offset + item.Length, 2), record.SeparatorRange, "MBOX CRLF padding");
            SameBytes("\r\n"u8, session.ReadRange(source.Id, record.SeparatorRange!.Value), "separator bytes");
            Require(record.EnvelopeRange is { } envelope && envelope.End == item.Offset, "envelope range ends at message start");
        }

        var first = session.Records.First();
        Require(Encoding.ASCII.GetString(session.ReadRange(source.Id, first.MessageRange))
            .Contains("\r\n>From line-in-body", StringComparison.Ordinal), "MBOX escape is preserved in original bytes");
        foreach (var token in new[] { "BODY_ONLY_4C7F", "HTML_ONLY_63DE" })
            Equal(first.Id, session.Search(new SearchRequest(token, SearchField.Body)).Hits.Single().Record.Id, "MBOX MIME body match");

        var copies = session.Search(new SearchRequest("<fixture-b@example.test>", SearchField.MessageId)).Hits;
        Equal(2, copies.Count, "same Message-ID preserved");
        Equal(2, copies.Select(x => x.Record.Id).Distinct().Count(), "stable record IDs differ");
        var search = session.Search(new SearchRequest("case", SearchField.Subject, source.Id,
            DateTimeOffset.Parse("2026-11-23T03:02:00Z"), DateTimeOffset.Parse("2026-11-23T03:03:00Z"), false));
        Require(search.Hits.Select(x => x.Record.Id).SequenceEqual(session.Records.Skip(1).Select(x => x.Id)),
            "both Date headers at 03:02 are included even though the last envelope says 03:03");
        var exclusive = session.Search(new SearchRequest("case", SearchField.Subject, source.Id,
            ToDateExclusive: DateTimeOffset.Parse("2026-11-23T03:02:00Z")));
        Equal(0, exclusive.Hits.Count, "both records on the upper Date bound are excluded");
        Equal(SyntheticMboxSha, FileHash(path), "MBOX source unchanged");
    }

    private static void TestTikaEml(string path)
    {
        Equal(TikaEmlSha, FileHash(path), "public EML SHA-256");
        using var session = EvidenceSession.Open([path]);
        var record = session.Records.Single();
        Equal(2, record.Attachments.Count, "public attachment count");
        foreach (var (name, hash) in new[] { ("Test TxtA.txt", TikaAttachmentASha), ("Test TxtB.txt", TikaAttachmentBSha) })
        {
            var hit = session.Search(new SearchRequest(name, SearchField.AttachmentName)).Hits.Single();
            var attachment = record.Attachments.Single(x => x.Name.Equals(name, StringComparison.Ordinal));
            Equal(record.Id, hit.Record.Id, "public attachment matched record");
            Require(hit.Matches.Any(x => x.Field == SearchField.AttachmentName && x.PartPath == attachment.PartPath),
                "public attachment matching path");
            AssertAttachment(session, record, attachment, 33, hash);
        }

        Equal(null, record.Date, "missing public Date is not inferred");
        Equal(TikaEmlSha, FileHash(path), "public EML unchanged");
    }

    private static void TestApacheMbox(string path)
    {
        Equal(ApacheMboxSha, FileHash(path), "public MBOX SHA-256");
        using var session = EvidenceSession.Open([path]);
        Equal(36, session.Records.Count, "public MBOX record count");
        session.PrepareAnomalyCount();
        Equal(0, session.AnomalousMailCount, "declared list replies and small relay clock differences are not primary anomalies");
        Equal(19, session.Search(new SearchRequest("tika", SearchField.Subject)).Hits.Count, "public title tika count");
        var hits = session.Search(new SearchRequest("mime", SearchField.Subject)).Hits;
        Require(hits.Select(x => x.Record.MessageRange.Offset).SequenceEqual(new long[] { 32630, 35955, 39859, 44219 }),
            "public title mime record offsets");
        var raw = File.ReadAllBytes(path);
        var first = hits.First().Record;
        var bytes = session.ReadRange(first.SourceId, first.MessageRange);
        SameBytes(raw.AsSpan(32630, 3280), bytes, "independent public message slice");
        var headers = session.ReadRange(first.SourceId, first.HeaderRange);
        Equal(new ByteRange(32630, 2059), first.HeaderRange, "independent public header range through the blank line");
        SameBytes(raw.AsSpan(32630, 2059), headers, "public header slice");
        Equal(8, first.Headers.Count(x => x.Name.Equals("Received", StringComparison.OrdinalIgnoreCase)),
            "public Received fields from independent byte scanner");
        Equal(ApacheMboxSha, FileHash(path), "public MBOX unchanged");
    }

    private static void TestMessageExport(string eml, string mbox, string output)
    {
        using var session = EvidenceSession.Open([eml, mbox]);
        var plain = session.Records.Single(x => x.Format == MailFormat.Eml);
        var archived = session.Records.First(x => x.Format == MailFormat.Mbox);
        var request = new ExportRequest(output, ExportKind.MessageBytes, ExportScope.SelectedMessages,
            [plain.Id, archived.Id]);
        var package = ExportForTest(session, request);
        Equal(2, package.Items.Count, "saved source artifacts");
        ValidatePackage(package);
        foreach (var (record, expected) in new[] { (plain, SyntheticEmlSha), (archived,
            "af844a4e3f8f5c2d371eee9d6f5fc84d8ebf6cc06f19c775bd3458bec4708783") })
        {
            var item = package.Items.Single(x => x.RecordId == record.Id);
            Equal(record.MessageRange, item.InputRange, "raw export records input position");
            Equal(expected, item.Sha256, "raw export SHA-256");
        }

        var tampered = Path.Combine(package.PackageDirectory, package.Items[0].RelativePath);
        var originalLength = new FileInfo(tampered).Length;
        using (var stream = File.Open(tampered, FileMode.Open, FileAccess.ReadWrite))
        {
            var firstByte = stream.ReadByte();
            stream.Position = 0;
            stream.WriteByte((byte)(firstByte ^ 1));
        }

        Equal(originalLength, new FileInfo(tampered).Length, "same-length tamper");
        Require(!ExportService.Verify(package.PackageDirectory).Success, "same-length tamper is detected");
        File.Delete(tampered);
        Require(!ExportService.Verify(package.PackageDirectory).Success, "missing output is detected");
        Equal(SyntheticEmlSha, FileHash(eml), "export did not write EML source");
        Equal(SyntheticMboxSha, FileHash(mbox), "export did not write MBOX source");
    }

    private static void TestAttachmentExport(string path, string output)
    {
        using var session = EvidenceSession.Open([path]);
        var record = session.Records.Single();
        var attachment = record.Attachments.Single(x => x.Name == "Test TxtA.txt");
        var request = new ExportRequest(output, ExportKind.Attachments, ExportScope.SelectedAttachments,
            [record.Id], [new AttachmentSelection(record.Id, attachment.Id)]);
        var package = ExportForTest(session, request);
        ValidatePackage(package);
        var item = package.Items.Single();
        Equal(TikaAttachmentASha, item.Sha256, "saved attachment digest");
        Equal(33L, item.Length, "saved attachment length");
        Equal(attachment.Id, item.AttachmentId, "selected attachment identity");
        Equal(attachment.PartPath, item.PartPath, "saved attachment MIME part path");
        Equal(attachment.EncodedRange, item.InputRange, "saved attachment raw provenance");
        Equal(TikaEmlSha, FileHash(path), "attachment export did not write source");
    }

    private static void TestSearchExport(string path, string target)
    {
        using var session = EvidenceSession.Open([path]);
        var query = new SearchRequest("mime", SearchField.Subject);
        var search = session.Search(query);
        var frozen = search.Hits.Select(x => x.Record.Id).Reverse().ToArray();
        var request = new ExportRequest(target, ExportKind.Csv,
            ExportScope.SearchResults, frozen, Search: query, SearchIssues: search.Issues,
            ExaminedCount: search.ExaminedCount);
        Throws<InvalidOperationException>(() => ExportService.Export(session, request with { RecordIds = [frozen[0]] }),
            "search-result export rejects a scope missing matching rows");
        var exported = ExportForTest(session, request);
        ValidatePackage(exported);
        var item = exported.Items.Single();
        using var csv = OpenCsv(Path.Combine(exported.PackageDirectory, item.RelativePath));
        var header = csv.ReadFields()!;
        Equal(4, frozen.Length, "independent expected result count");
        var idColumn = Array.FindIndex(header, x => NormalizeColumn(x) == "recordid");
        Require(idColumn >= 0, "CSV contains record IDs for source correlation");
        foreach (var recordId in frozen)
            Equal(recordId, csv.ReadFields()?[idColumn], "CSV follows frozen result order");

        Require(csv.EndOfData, "CSV has only the frozen search results");
    }

    private static void TestCsvNeutralization(string path)
    {
        var sourcePath = Path.Combine(path, "spreadsheet.eml");
        File.WriteAllText(sourcePath, "From: test@example.test\r\nSubject: =1+1, 한글\r\n\r\nbody\r\n",
            new UTF8Encoding(false));
        using var session = EvidenceSession.Open([sourcePath]);
        var record = session.Records.Single();
        Equal("=1+1, 한글", record.Subject, "engine preserves subject");
        var package = ExportForTest(session, new ExportRequest(path, ExportKind.Csv,
            ExportScope.SelectedMessages, [record.Id]));
        ValidatePackage(package);
        using var csv = OpenCsv(Path.Combine(package.PackageDirectory, package.Items.Single().RelativePath));
        var header = csv.ReadFields()!;
        var fields = csv.ReadFields()!;
        var subjectColumn = Array.FindIndex(header, x => NormalizeColumn(x) == "subject");
        Require(subjectColumn >= 0, "CSV subject column");
        Equal("[text] =1+1, 한글", fields[subjectColumn], "CSV formula neutralization and quoted comma");
        Require(fields.Contains(sourcePath), "CSV includes exact origin path");
        Require(csv.EndOfData, "CSV contains one row");
    }

    private static void TestFailureAndSourceReplacement(string fixture, string path)
    {
        var source = Path.Combine(path, "replace.eml");
        File.Copy(fixture, source);
        var date = File.GetLastWriteTimeUtc(source);
        var replacement = File.ReadAllBytes(source);
        var marker = replacement.AsSpan().IndexOf("BODY_ONLY_4C7F"u8);
        Require(marker >= 0, "fixture has marker for same-size replacement");
        replacement[marker] = (byte)'X';
        using (var first = EvidenceSession.Open([source]))
        {
            Equal(1, first.Search(new SearchRequest("BODY_ONLY_4C7F", SearchField.Body)).Hits.Count, "initial search");
            if (OperatingSystem.IsWindows())
                Throws<IOException>(() => File.WriteAllBytes(source, replacement), "write denied on original handle");

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Throws<OperationCanceledException>(() => EvidenceSession.Open([source], cancelled.Token), "open honors cancellation");
            var exportCount = Directory.GetDirectories(path).Length;
            var request = new ExportRequest(path, ExportKind.MessageBytes, ExportScope.SelectedMessages,
                [first.Records.Single().Id]);
            Throws<OperationCanceledException>(() => ExportService.Export(first, request, cancelled.Token),
                "export honors cancellation");
            Equal(exportCount, Directory.GetDirectories(path).Length, "cancelled export publishes no package");
            var prior = ExportForTest(first, request);
            var next = ExportForTest(first, request);
            Require(prior.PackageDirectory != next.PackageDirectory, "repeat creates a unique package");
            ValidatePackage(prior);
            ValidatePackage(next);
        }

        File.WriteAllBytes(source, replacement);
        File.SetLastWriteTimeUtc(source, date);
        Equal(date, File.GetLastWriteTimeUtc(source), "mtime preserved");
        Equal(new FileInfo(fixture).Length, new FileInfo(source).Length, "length preserved");
        using var second = EvidenceSession.Open([source, Path.Combine(path, "missing.eml")]);
        Require(second.Issues.Count != 0, "missing source is reported");
        Equal(0, second.Search(new SearchRequest("BODY_ONLY_4C7F", SearchField.Body)).Hits.Count, "old query invalidated");
        Equal(1, second.Search(new SearchRequest("XODY_ONLY_4C7F", SearchField.Body)).Hits.Count, "replacement indexed");
        Equal(Hash(replacement), second.Sources.Single(x => x.Path == source).Sha256, "replacement content digest");
    }

    private static void AssertAttachment(EvidenceSession session, MailRecord record, MailAttachment attachment,
        long length, string sha)
    {
        var decoded = session.ReadAttachment(record.Id, attachment.Id);
        Equal(length, attachment.DecodedLength, "reported attachment length");
        Equal(length, decoded.LongLength, "actual attachment length");
        Equal(sha, attachment.Sha256, "reported attachment SHA-256");
        Equal(sha, Hash(decoded), "decoded attachment SHA-256");
        var encoded = attachment.EncodedRange ?? throw new InvalidOperationException("attachment has no raw MIME range");
        Require(record.MessageRange.Offset <= encoded.Offset && record.MessageRange.End >= encoded.End,
            "encoded attachment lies inside original message");
        var raw = File.ReadAllBytes(record.SourcePath);
        SameBytes(raw.AsSpan((int)encoded.Offset, (int)encoded.Length),
            session.ReadRange(record.SourceId, encoded), "attachment raw bytes match original file");
    }

    private static void ValidatePackage(ExportResult package)
    {
        var manifest = Path.Combine(package.PackageDirectory, "manifest.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifest));
        var serialized = document.RootElement.GetRawText();
        Require(serialized.Contains(package.JobId.ToString(), StringComparison.OrdinalIgnoreCase), "manifest contains job ID");
        foreach (var item in package.Items)
        {
            var path = Path.Combine(package.PackageDirectory, item.RelativePath);
            Equal(item.Length, new FileInfo(path).Length, "artifact length outside export service");
            Equal(item.Sha256, FileHash(path), "artifact SHA-256 outside export service");
            Require(serialized.Contains(item.Sha256, StringComparison.OrdinalIgnoreCase), "manifest contains artifact hash");
            if (item.RecordId is not null)
                Require(serialized.Contains(item.RecordId, StringComparison.Ordinal), "manifest contains source record ID");
        }

        var verified = ExportService.Verify(package.PackageDirectory);
        Require(verified.Success, "export verification failed: " + string.Join(", ", verified.Errors));
        Equal(package.JobId, verified.JobId, "verified job ID");
    }

    private static TextFieldParser OpenCsv(string path) => new(path, Encoding.UTF8)
    {
        Delimiters = [","],
        HasFieldsEnclosedInQuotes = true,
        TrimWhiteSpace = false,
    };

    private static string NormalizeColumn(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static ExportResult ExportForTest(EvidenceSession session, ExportRequest request)
    {
        var package = ExportService.Export(session, request);
        CreatedPackages.Add(package);
        return package;
    }

    private static string FindRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "MailViewer.slnx")))
                return dir.FullName;
        throw new DirectoryNotFoundException("Run from the Mail Evidence checkout.");
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void Require(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException(description);
    }

    private static void Equal<T>(T expected, T actual, string description)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{description}: expected {expected}, got {actual}");
    }

    private static void SameBytes(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string description) =>
        Require(expected.SequenceEqual(actual), description);

    private static void Throws<TException>(Action action, string description) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(description);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _parent = System.IO.Path.TrimEndingDirectorySeparator(
            System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
        public string Path { get; }

        public TemporaryDirectory() =>
            Path = Directory.CreateTempSubdirectory("MailViewer-Verification-").FullName;

        public void Dispose()
        {
            if (System.IO.Path.GetFullPath(System.IO.Path.GetDirectoryName(Path)!) == _parent &&
                System.IO.Path.GetFileName(Path).StartsWith("MailViewer-Verification-", StringComparison.Ordinal))
            {
                RemoveTestReceipts();
                Directory.Delete(Path, recursive: true);
            }
        }

        private void RemoveTestReceipts()
        {
            var jobs = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ForensicMailViewer", "jobs");
            foreach (var package in CreatedPackages)
            {
                if (!string.Equals(System.IO.Path.GetDirectoryName(package.PackageDirectory), Path,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                var receipt = System.IO.Path.Combine(jobs, $"{package.JobId:N}.json");
                if (!File.Exists(receipt))
                    continue;
                using var json = JsonDocument.Parse(File.ReadAllBytes(receipt));
                var payload = json.RootElement;
                if (payload.GetProperty("jobId").GetGuid() == package.JobId &&
                    payload.GetProperty("packageDirectory").GetString() == package.PackageDirectory)
                    File.Delete(receipt);
            }
        }
    }
}
