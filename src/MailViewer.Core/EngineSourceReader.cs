using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using MimeKit;
using MimeKit.Text;

namespace MailViewer.Core;

public sealed record ContainerEvidence(string Kind, string Description, long Length, bool CanRead,
    ByteRange? FileRange, string? Sha256)
{
    [System.Text.Json.Serialization.JsonInclude]
    public JsonElement Address { get; internal init; }
}

internal sealed record EngineAttachment(int Index, string Representation, ContainerEvidence Message);

internal static class EngineSourceReader
{
    private const string Schema = "mailviewer-windows/1";
    private const int MessageLimit = 128 * 1024 * 1024;
    private static readonly JsonSerializerOptions Protocol = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly Lazy<string> Worker = new(UnpackWorker);

    public static IReadOnlyList<string> Extensions { get; } = Array.AsReadOnly(new[]
    {
        ".eml", ".mbox", ".msg", ".pst", ".ost", ".nws", ".mime", ".rfc822", ".emlx", ".mmdf", ".babyl", ".rmail",
        ".dbx", ".mbx", ".idx", ".nch", ".msf", ".olm", ".zip", ".tnef", ".p7m", ".p7s", ".ics", ".vcf", ".edb", ".nsf"
    });

    internal static bool CanUseWorker => OperatingSystem.IsWindows() &&
        Assembly.GetExecutingAssembly().GetManifestResourceInfo("MailViewer.Core.FormatWorker") is not null;

    internal static bool LooksLikeMime(string path) => Path.GetExtension(path).ToLowerInvariant() is ".eml" or ".nws" or ".mime" or ".rfc822";

    internal static ParsedSource Inspect(string path, long length, string hash, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(Run("viewer-inspect", path, null, 320 * 1024 * 1024, ct));
        var protocol = doc.RootElement;
        if (protocol.GetProperty("schema").GetString() != Schema)
            throw new InvalidDataException("Unsupported mail format reader protocol.");
        var entry = protocol.GetProperty("entry");
        var info = entry.GetProperty("source");
        if (Long(info, "bytes") != length || !string.Equals(Text(info, "sha256"), hash, StringComparison.Ordinal))
            throw new InvalidDataException("The format reader inspected a different source.");
        var identification = info.GetProperty("identification");
        string detected = Text(identification, "format");
        MailFormat? format = detected switch
        {
            "eml" => MailFormat.Eml, "mbox" => MailFormat.Mbox, "msg" => MailFormat.Msg, "pst" => MailFormat.Pst,
            "ost" => MailFormat.Ost, "emlx" => MailFormat.Emlx, "mmdf" => MailFormat.Mmdf, "babyl" => MailFormat.Babyl,
            "dbx" => MailFormat.Dbx, "outlook_express_mbx" => MailFormat.Mbx,
            "olm" => MailFormat.Olm, "zip" => MailFormat.Zip, "tnef" => MailFormat.Tnef, "p7m" => MailFormat.Cms,
            "ics" => MailFormat.Ics, "vcf" => MailFormat.Vcf, "edb" => MailFormat.Edb, "nsf" => MailFormat.Nsf,
            "dbx_folders" or "dbx_metadata" or "outlook_express_idx" or "outlook_express_nch" or "mh_sequences"
                or "thunderbird_index" => MailFormat.MailboxIndex,
            _ => null
        };
        StoreLayoutInfo? ndb = Property(identification, "ndb") is { } layout
            ? new((ushort)Long(layout, "version"), (int)Long(layout, "page_size"),
                Property(layout, "block_encoding") is { } b ? b.GetByte() : null)
            : null;
        return new(format, detected, Text(entry, "status"), ndb, Property(entry, "inspection")?.Clone());
    }

    internal sealed record ParsedSource(MailFormat? Format, string Detected, string Status, StoreLayoutInfo? StoreLayout, JsonElement? Inspection)
    {
        internal MailIndexSummary? IndexSummary()
        {
            if (Inspection is not { } view || Format != MailFormat.MailboxIndex) return null;
            var folderRows = Items(view, "express_folders").ToArray();
            var sequenceRows = Items(view, "mh_sequences").ToArray();
            int entryCount = Items(view, "idx_entries").Count();
            var names = folderRows.Select(row => Text(row, "display_name"))
                .Concat(sequenceRows.Select(row => Text(row, "name"))).Where(name => name.Length > 0).Take(40).ToArray();
            return new(folderRows.Length, entryCount, sequenceRows.Length, names);
        }

        internal void AddRecords(SourceInfo source, List<MailRecord> records, List<PstFolder> folders, List<ReadIssue> issues,
            CancellationToken ct, bool embeddedOnly = false)
        {
            if (Inspection is not { } view)
            {
                issues.Add(new(source.Id, "reader-unavailable", $"{Detected}: format identified, message reader unavailable.") { SourcePath = source.Path });
                return;
            }
            foreach (var issue in Items(view, "issues"))
                issues.Add(new(source.Id, Text(issue, "code"), Text(issue, "detail"),
                    Location(Property(issue, "location"))?.FileRange) { SourcePath = source.Path, StoreLayout = source.StoreLayout });
            if (Long(view, "omitted_issue_count") > 0)
                issues.Add(new(source.Id, "issue-limit", $"Additional format issues: {Long(view, "omitted_issue_count")}.")
                    { SourcePath = source.Path });

            var nodes = Items(view, "folders").ToDictionary(item => (uint)Long(item, "nid"));
            string NodePath(uint id)
            {
                var names = new Stack<string>();
                var visited = new HashSet<uint>();
                while (nodes.TryGetValue(id, out var node) && visited.Add(id))
                {
                    string name = Text(node, "name");
                    names.Push(name.Length == 0 ? $"0x{id:X8}" : name);
                    if (Property(node, "parent_nid") is not { } parent) break;
                    id = parent.GetUInt32();
                }
                return "/" + string.Join("/", names);
            }
            foreach (var (id, node) in nodes)
            {
                string path = NodePath(id);
                folders.Add(new(source.Id, id, Property(node, "parent_nid")?.GetUInt32(), path[(path.LastIndexOf('/') + 1)..], path));
            }

            var memberFolders = new Dictionary<string, PstFolder>(StringComparer.Ordinal);
            PstFolder? ArchiveFolder(ContainerEvidence evidence)
            {
                if (evidence.Kind != "archive_member") return null;
                string member = Text(evidence.Address, "name");
                var segments = member.Split('/');
                PstFolder? folder = null;
                string path = "";
                foreach (string name in segments.Take(segments.Length - 1))
                {
                    if (name is "" or "." or "..") continue;
                    path += "/" + name;
                    if (!memberFolders.TryGetValue(path, out var next))
                    {
                        next = new(source.Id, (uint)memberFolders.Count + 1, folder?.NodeId, name, path) { IsLogicalPath = true };
                        memberFolders.Add(path, next);
                        folders.Add(next);
                    }
                    folder = next;
                }
                return folder;
            }

            int index = 0;
            foreach (var root in Items(view, "records"))
            {
                if (embeddedOnly)
                {
                    foreach (var item in Items(root, "embedded_messages")) Add(item);
                }
                else Add(root);
            }

            void Add(JsonElement row)
            {
                ct.ThrowIfCancellationRequested();
                if (Location(Property(row, "location")) is not { } location) return;
                var address = location.Address;
                var archiveFolder = ArchiveFolder(location);
                var id = $"{source.Id}:rust:{++index}";
                PstMessageLocator? pst = location.Kind == "store_item"
                    ? new((uint)Long(address, "folder_nid"), (uint)Long(address, "message_nid"), NodePath((uint)Long(address, "folder_nid")))
                    : null;
                var header = Property(row, "headers");
                var headerLocation = header is { } h ? Location(Property(h, "location"), Long(h, "bytes"), Text(h, "sha256")) : null;
                var headerRange = headerLocation?.FileRange ?? new ByteRange(0, headerLocation?.Length ?? 0);
                var headers = header is { } block
                    ? Items(block, "fields").Select(value =>
                    {
                        var relative = Range(Property(value, "relative_range"));
                        var range = relative is { } r && r.Offset <= headerRange.Length && r.Length <= headerRange.Length - r.Offset
                            ? new ByteRange(headerRange.Offset + r.Offset, r.Length) : default;
                        return new MailHeader(Text(value, "name"), Text(value, "value"), range);
                    }).ToArray()
                    : [];
                string Header(string name) => headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value ?? "";
                string DateHeader()
                {
                    var raw = Text(row, "date_raw");
                    return raw.Length > 0 ? raw : Header("Date");
                }
                string date = DateHeader();
                DateTimeOffset? timestamp = MimeMessageReader.ParseTransportDate(headers, date);
                if (timestamp is null && source.Format == MailFormat.Olm &&
                    DateTimeOffset.TryParseExact(date, ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"],
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso) &&
                    (date.EndsWith('Z') || date.Length > 6 && date[^6] is '+' or '-'))
                    timestamp = iso;

                var bodies = new List<MailBodyPart>();
                foreach (var body in Items(row, "bodies"))
                {
                    string mediaType = Text(body, "media_type");
                    string text = Text(body, "text");
                    string part = (bodies.Count + 1).ToString(CultureInfo.InvariantCulture);
                    var (links, limited) = InvestigationArtifacts.ReadLinks(text, mediaType, part, new EvidenceLocation(default), ct);
                    bodies.Add(new(part, mediaType, mediaType == "text/html" ? new HtmlToText().Convert(text) : text)
                    {
                        Links = links, EngineHtml = mediaType == "text/html" ? text : null
                    });
                    if (limited || Property(body, "truncated")?.GetBoolean() == true)
                        issues.Add(new(source.Id, "body-limit", $"Part {part}: body inspection limit reached.", RecordId: id, Field: SearchField.Body));
                }

                var attachments = Items(row, "attachments").Select((item, ordinal) =>
                {
                    string? hash = Property(item, "sha256")?.GetString();
                    long? size = Property(item, "bytes")?.GetInt64();
                    string representation = Text(item, "representation");
                    var itemLocation = Location(Property(item, "location"),
                        representation == "decoded_mime_part" ? null : size);
                    string name = Text(item, "name");
                    string part = (ordinal + 1).ToString(CultureInfo.InvariantCulture);
                    bool available = itemLocation is not null && hash is not null && size is >= 0 and <= MessageLimit &&
                        representation is "decoded_mime_part" or "stored_emlx_sidecar" or "stored_cfb_stream" or "decoded_mapi_property"
                            or "stored_archive_member" or "stored_tnef_attribute";
                    var observation = name.Length == 0 ? null : InvestigationArtifacts.InspectAttachment(
                        name, new EvidenceLocation(default), "Filename", null);
                    return new MailAttachment($"{id}:attachment:{part}", part, name.Length == 0 ? $"attachment-{part}" : name,
                        Property(item, "media_type")?.GetString() ?? MimeTypes.GetMimeType(name.Length == 0 ? "attachment.bin" : name),
                        "", itemLocation?.FileRange, size, hash, available ? null : representation)
                    {
                        ContainerLocation = itemLocation,
                        EngineAttachment = available ? new(ordinal, representation, location) : null,
                        ContentId = Property(item, "content_id")?.GetString(),
                        Analysis = observation
                    };
                }).ToArray();
                var fields = new List<StoredMailField> { new("Source location", location.Description) };
                if (Property(row, "mailbox_labels") is { } labels && Property(labels, "values") is { } list)
                    fields.Add(new("Babyl labels", string.Join(", ", list.EnumerateArray().Select(label => label.GetString()))));
                if (Property(row, "mailbox_entry_offset") is { } entryOffset)
                    fields.Add(new("Mailbox entry", $"0x{entryOffset.GetUInt64():X}"));
                string subject = Text(row, "subject");
                if (subject.Length == 0 && Text(row, "class") != "mail")
                    subject = Path.GetFileName(source.Path);
                string messageId = Text(row, "message_id");
                if (messageId.Length == 0) messageId = Header("Message-ID");

                records.Add(new(id, source.Id, source.Path, source.Format,
                    location.FileRange ?? default, headerRange,
                    Location(Property(row, "envelope"))?.FileRange, Location(Property(row, "separator"))?.FileRange,
                    subject, Text(row, "from"), Text(row, "to"), Header("Cc"), Header("Bcc"), messageId, date, timestamp,
                    headers, bodies, attachments, TransportHeaderState: headerLocation is null ? HeaderEvidenceState.Missing
                        : headerRange.Length == 0 ? HeaderEvidenceState.Empty : HeaderEvidenceState.Present, PstMessage: pst)
                {
                    ContainerMessage = location,
                    ContainerHeaders = headerLocation,
                    TransportHeaderCodePage = header is { } headerBlock ? Property(headerBlock, "code_page")?.GetInt32() : null,
                    StoredFields = fields,
                    LogicalFolder = archiveFolder
                });
                foreach (var nested in Items(row, "embedded_messages")) Add(nested);
            }
        }
    }

    internal static byte[] Read(SourceInfo source, ContainerEvidence location, ByteRange relative, CancellationToken ct)
    {
        if (!location.CanRead || relative.Offset < 0 || relative.Length < 0 ||
            relative.Offset > location.Length || relative.Length > location.Length - relative.Offset)
            throw new InvalidDataException("The requested range is outside the recorded location.");
        return ReadWorker(source, location, relative, location.Length, location.Sha256, null, ct);
    }

    internal static byte[] ReadAttachment(SourceInfo source, MailAttachment attachment, CancellationToken ct) =>
        ReadWorker(source, attachment.ContainerLocation!, null, attachment.DecodedLength!.Value, attachment.Sha256,
            attachment.EngineAttachment, ct);

    private static byte[] ReadWorker(SourceInfo source, ContainerEvidence location, ByteRange? range, long length,
        string? sha256, EngineAttachment? attachment, CancellationToken ct)
    {
        var request = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema, SourceLength = source.Length, Location = location.Address, Range = range, ExpectedLength = length,
            ExpectedSha256 = sha256, Attachment = attachment is null ? null : new
            {
                attachment.Representation, Message = attachment.Message.Address, attachment.Index
            }
        }, Protocol);
        long expected = range?.Length ?? length;
        if (expected > MessageLimit) throw new InvalidDataException("The reader's evidence size limit was reached.");
        byte[] bytes = Run("viewer-bytes", source.Path, request, (int)expected, ct);
        if (bytes.LongLength != expected) throw new InvalidDataException("The reader returned an incomplete evidence range.");
        return bytes;
    }

    private static ContainerEvidence? Location(JsonElement? input, long? length = null, string? sha = null)
    {
        if (input is not { } value) return null;
        string kind = Text(value, "kind");
        var range = Range(Property(value, "range") ?? Property(value, "logical_range"));
        long bytes = length ?? range?.Length ?? 0;
        string description = kind switch
        {
            "file" => range?.ToString() ?? "",
            "segments" => $"DBX · index 0x{Long(value, "index_offset"):X} · {range}",
            "archive_member" => $"ZIP #{Long(value, "index")} · {Text(value, "name")} · {range}",
            "external_file" => $"{Text(value, "path")} · {range} · SHA-256 {Text(value, "sha256")}",
            "store_item" => $"Folder NID 0x{Long(value, "folder_nid"):X8} · Message NID 0x{Long(value, "message_nid"):X8}",
            "store_property" => $"Message NID 0x{Long(value, "message_nid"):X8} · MAPI 0x{Long(value, "property_tag"):X8}"
                + (Property(value, "attachment_nid") is { } nid ? $" · Attachment NID 0x{nid.GetUInt32():X8}" : ""),
            "compound_storage" or "compound_stream" => $"CFB · {Text(value, "path")} · MAPI 0x{Long(value, "property_tag"):X8}",
            _ => kind
        };
        return new(kind, description, bytes, bytes is >= 0 and <= MessageLimit && kind is
            "file" or "archive_member" or "segments" or "external_file" or "store_property" or "compound_stream",
            kind == "file" ? range : null, sha) { Address = value.Clone() };
    }

    private static JsonElement? Property(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;
    private static IEnumerable<JsonElement> Items(JsonElement parent, string name) =>
        Property(parent, name) is { ValueKind: JsonValueKind.Array } list ? list.EnumerateArray().ToArray() : [];
    private static string Text(JsonElement parent, string name) => Property(parent, name)?.GetString() ?? "";
    private static long Long(JsonElement parent, string name) => Property(parent, name)?.GetInt64() ?? 0;
    private static ByteRange? Range(JsonElement? value) =>
        value is { } range ? new(Long(range, "offset"), Long(range, "length")) : null;

    private static string UnpackWorker()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MailViewer.Core.FormatWorker")
            ?? throw new IOException("The extended mail format reader is missing from this build.");
        var directory = Directory.CreateTempSubdirectory("mail-evidence-reader-");
        var file = Path.Combine(directory.FullName, "mailviewer-engine.exe");
        using (var target = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.CopyTo(target);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { File.Delete(file); directory.Delete(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        };
        return file;
    }

    private static byte[] Run(string command, string path, byte[]? input, int maximumBytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { return RunAsync(command, path, input, maximumBytes, ct).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or JsonException)
        { throw new IOException("The extended mail format reader could not run.", error); }
    }

    private static async Task<byte[]> RunAsync(string command, string path, byte[]? input, int maximumBytes, CancellationToken ct)
    {
        string executable = Worker.Value;
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = input is not null, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(command);
        start.ArgumentList.Add(path);
        using var process = Process.Start(start) ?? throw new IOException("Could not start the extended mail format reader.");
        using var cancel = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
        var errors = process.StandardError.ReadToEndAsync(ct);
        try
        {
            if (input is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(input, ct).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            using var result = new MemoryStream();
            byte[] buffer = new byte[65536];
            int count;
            while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
            {
                if (result.Length + count > maximumBytes)
                    throw new InvalidDataException("The extended reader output exceeded its limit.");
                result.Write(buffer, 0, count);
            }
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new IOException((await errors.ConfigureAwait(false)).Trim());
            return result.ToArray();
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
    }
}
