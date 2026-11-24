using MimeKit;
using MailViewer.Core;

namespace MailViewer.App;

public partial class MainWindow
{
    private static string? MailAddressLabel(string field) => field switch
    {
        "FROM" => L("작성자", "From"),
        "SENDER" => L("전송자", "Sender"),
        "REPLY-TO" => L("회신 대상", "Reply-to"),
        "TO" => L("받는 사람", "To"),
        "CC" => L("참조", "Cc"),
        "BCC" => L("숨은 참조", "Bcc"),
        _ => null
    };

    private static string EmailLabel(string label) => L($"{label} 이메일", $"{label} email");

    private static IEnumerable<MailInvestigationField> HeaderAddressFields(
        string label, string value, string source, MailHeader header)
    {
        var mailboxes = InternetAddressList.TryParse(value, out var addresses) ? addresses.Mailboxes.ToArray() : [];
        if (mailboxes.Length == 0 || mailboxes.Any(address => address.Address.LastIndexOf('@') < 1))
        {
            yield return new(L($"{label} 원문", $"{label} (raw)"), value, source, header);
            yield break;
        }

        string email = EmailLabel(label);
        for (int i = 0; i < mailboxes.Length; i++)
        {
            var mailbox = mailboxes[i];
            string location = mailboxes.Length == 1 ? source : $"{source} · {i + 1}/{mailboxes.Length}";
            yield return new(label, mailbox.Name ?? "", location, header);
            yield return new(email, mailbox.Address, location, header, SearchKind: InvestigationKeyKind.Address);
        }
    }

    private static int StoredAddressOrder(StoredMailField field) => field.Name switch
    {
        "Sent representing name" => 0,
        "Sent representing SMTP address" => 1,
        "Sent representing address" => 2,
        "Sender name" => 3,
        "Sender SMTP address" => 4,
        "Sender address" => 5,
        _ => 6
    };

    private static MailInvestigationField StoredAddressField(StoredMailField field)
    {
        string? tag = field.Stream?.TagHex ?? field.Property?.TagHex;
        string source = tag is null ? L("저장 속성", "Stored property") : $"MAPI {tag}";
        if (field.Stream is { StoragePath.Count: > 0 } stream && stream.StoragePath[^1] is { } storage)
        {
            int separator = storage.LastIndexOf('#');
            if (separator >= 0) source += $" · #{storage[(separator + 1)..]}";
        }

        var (name, kind) = field.Name switch
        {
            "Sent representing name" => (L("작성자", "From"), (InvestigationKeyKind?)null),
            "Sender name" => (L("전송자", "Sender"), null),
            "Sent representing SMTP address" => (L("작성자 이메일", "From email"), InvestigationKeyKind.Address),
            "Sender SMTP address" => (L("전송자 이메일", "Sender email"), InvestigationKeyKind.Address),
            "Sent representing address" => (L("작성자 주소", "From address"), null),
            "Sender address" => (L("전송자 주소", "Sender address"), null),
            "Display To" => (L("받는 사람 표시", "Display To"), null),
            "Display Cc" => (L("참조 표시", "Display Cc"), null),
            "Display Bcc" => (L("숨은 참조 표시", "Display Bcc"), null),
            "Internet Message-ID" => ("Message-ID", InvestigationKeyKind.MessageId),
            _ => RecipientField(field.Name)
        };
        return new(name, field.Value, source, Stored: field, Group: 4, SearchKind: kind);
    }

    private static (string, InvestigationKeyKind?) RecipientField(string field)
    {
        int separator = field.IndexOf(" · ", StringComparison.Ordinal);
        if (separator < 0) return (field, null);
        string role = field[..separator];
        string label = MailAddressLabel(role.ToUpperInvariant()) ?? L("수신자", "Recipient");
        return field[(separator + 3)..] switch
        {
            "display name" => (label, null),
            "SMTP address" => (EmailLabel(label), InvestigationKeyKind.Address),
            "address" => (L($"{label} 주소", $"{label} address"), null),
            _ => (field, null)
        };
    }
}
