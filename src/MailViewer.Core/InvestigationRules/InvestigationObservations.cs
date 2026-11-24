using static MailViewer.Core.InvestigationHeaderValues;

namespace MailViewer.Core;

internal static class InvestigationObservations
{
    private static readonly HashSet<string> AddressFields = new(StringComparer.OrdinalIgnoreCase)
        { "From", "Sender", "Reply-To", "To", "Cc", "Bcc", "Return-Path" };

    internal static void AddMetadata(MailRecord record, InvestigationEvaluation result, CancellationToken ct)
    {
        foreach (var header in record.Headers)
        {
            ct.ThrowIfCancellationRequested();
            if (AddressFields.Contains(header.Name))
            {
                foreach (var address in Addresses(header.Value))
                {
                    if (AddressHost(address) is not { } host) continue;
                    Header(header, header.Name, address.Address, AddressKey(address, host));
                    Header(header, header.Name, host, new(InvestigationKeyKind.Host, host));
                }
            }
            else if (header.Name.Equals("Message-ID", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string id in ParseIds(header.Value))
                    Header(header, "Message-ID", id, new(InvestigationKeyKind.MessageId, id));
            }
            else if (header.Name.Equals("References", StringComparison.OrdinalIgnoreCase) ||
                     header.Name.Equals("In-Reply-To", StringComparison.OrdinalIgnoreCase))
            {
                var ids = ParseIds(header.Value).ToArray();
                for (int i = 0; i < ids.Length; i++)
                    Header(header, header.Name.Equals("In-Reply-To", StringComparison.OrdinalIgnoreCase)
                        ? "In-Reply-To" : i == ids.Length - 1 ? "Last reference" : "References",
                        ids[i], new(InvestigationKeyKind.MessageId, ids[i]));
            }
            else if (header.Name.Equals("Authentication-Results", StringComparison.OrdinalIgnoreCase) ||
                     header.Name.Equals("Received-SPF", StringComparison.OrdinalIgnoreCase) ||
                     header.Name.Equals("DKIM-Signature", StringComparison.OrdinalIgnoreCase))
            {
                // Keep each assertion with the header that contains its producer and properties.
                string value = Unfold(header.Value).Trim();
                if (header.Name.Equals("Authentication-Results", StringComparison.OrdinalIgnoreCase))
                {
                    int start = 0;
                    bool emitted = false;
                    string? producer = null;
                    foreach (var split in TopLevelSemicolons(value).Append(value.Length))
                    {
                        string clause = value[start..split].Trim();
                        if (producer is null) producer = clause;
                        else if (clause.Length > 0)
                        {
                            Header(header, "Authentication-Results", $"{producer} · {clause}");
                            emitted = true;
                        }
                        start = split + 1;
                    }
                    if (!emitted)
                        Header(header, "Authentication-Results", value);
                }
                else if (header.Name.Equals("DKIM-Signature", StringComparison.OrdinalIgnoreCase))
                {
                    var tags = value.Split(';').Select(tag => tag.Trim()).Where(tag =>
                        tag.Split('=', 2)[0].Trim() is "d" or "s");
                    Header(header, "DKIM-Signature", string.Join("; ", tags));
                }
                else Header(header, "Received-SPF", value);
            }
        }

        foreach (var field in record.StoredFields)
        {
            ct.ThrowIfCancellationRequested();
            if (field.Name == "Internet Message-ID")
                foreach (string id in ParseIds(field.Value))
                    result.Add(new("Message-ID", id, StoredLocation(field), new(InvestigationKeyKind.MessageId, id)));
            if (IsStoredAddress(field))
                foreach (var address in Addresses(field.Value))
                {
                    if (AddressHost(address) is not { } host) continue;
                    var location = StoredLocation(field);
                    result.Add(new(field.Name, address.Address, location, AddressKey(address, host)));
                    result.Add(new(field.Name, host, location, new(InvestigationKeyKind.Host, host)));
                }
        }

        void Header(MailHeader header, string field, string value, InvestigationKey? key = null) =>
            result.AddHeader(record, header, field, value, key);
    }

    internal static bool Matches(MailRecord record, InvestigationKey key, CancellationToken ct)
    {
        if (key.Value is null) return false;
        switch (key.Kind)
        {
            case InvestigationKeyKind.AttachmentSha256:
                return record.Attachments.Any(attachment => attachment.Sha256 == key.Value);
            case InvestigationKeyKind.Url:
                foreach (var part in record.BodyParts)
                    foreach (var link in part.Links)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (link.Key == key.Value ||
                            InvestigationArtifacts.TryUrl(link.Display, out string displayed, out _) && displayed == key.Value)
                            return true;
                    }
                return false;
            case InvestigationKeyKind.MessageId:
                foreach (var header in record.Headers)
                {
                    ct.ThrowIfCancellationRequested();
                    if (header.Name.Equals("Message-ID", StringComparison.OrdinalIgnoreCase) && MatchId(header.Value))
                        return true;
                }
                return record.StoredFields.Any(field => field.Name == "Internet Message-ID" && MatchId(field.Value));
            case InvestigationKeyKind.Address:
            case InvestigationKeyKind.Host:
                foreach (var header in record.Headers)
                {
                    ct.ThrowIfCancellationRequested();
                    if (AddressFields.Contains(header.Name) && MatchAddress(header.Value)) return true;
                }
                foreach (var field in record.StoredFields)
                {
                    ct.ThrowIfCancellationRequested();
                    if (IsStoredAddress(field) && MatchAddress(field.Value)) return true;
                }
                return key.Kind == InvestigationKeyKind.Host &&
                    record.BodyParts.Any(part => part.Links.Any(link => link.Host == key.Value));
            default:
                return false;
        }

        bool MatchId(string value) => ParseIds(value).Contains(key.Value, StringComparer.Ordinal);

        bool MatchAddress(string value)
        {
            foreach (var address in Addresses(value))
            {
                if (AddressHost(address) is not { } host) continue;
                if (key.Kind == InvestigationKeyKind.Host)
                {
                    if (host == key.Value) return true;
                    continue;
                }
                int localEnd = address.Address.LastIndexOf('@') + 1;
                if (key.Value.Length == localEnd + host.Length &&
                    key.Value.AsSpan(0, localEnd).SequenceEqual(address.Address.AsSpan(0, localEnd)) &&
                    key.Value.AsSpan(localEnd).SequenceEqual(host)) return true;
            }
            return false;
        }
    }

    private static bool IsStoredAddress(StoredMailField field) =>
        field.Name is "Sender SMTP address" or "Sent representing SMTP address"
            or "To · SMTP address" or "Cc · SMTP address" or "Bcc · SMTP address" or "Recipient · SMTP address";

    private static EvidenceLocation? StoredLocation(StoredMailField field) =>
        field.Stream is { } stream ? new(new ByteRange(0, stream.Length), Stream: stream) :
        field.Property is { } property ? new(new ByteRange(0, property.Length), Property: property) : null;
}
