using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using MimeKit;
using MimeKit.Utils;

namespace MailViewer.Core;

internal static partial class InvestigationHeaderValues
{
    private static readonly ParserOptions StrictAddresses = new() { AddressParserComplianceMode = RfcComplianceMode.Strict };

    [GeneratedRegex(@"^[^@\s<>]+@(?:[^@\s<>]+|\[[^\]\r\n]+\])$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    internal static MailboxAddress? SingleAddress(IReadOnlyList<MailHeader> headers) =>
        headers.Count == 1 ? SingleAddress(headers[0].Value) : null;

    internal static MailboxAddress? SingleAddress(string value)
    {
        using var addresses = Addresses(value).GetEnumerator();
        if (!addresses.MoveNext()) return null;
        var first = addresses.Current;
        return addresses.MoveNext() ? null : first;
    }

    internal static bool TryMailbox(string text, [NotNullWhen(true)] out MailboxAddress? address) =>
        MailboxAddress.TryParse(StrictAddresses, text, out address);

    internal static IEnumerable<MailboxAddress> Addresses(string text) =>
        InternetAddressList.TryParse(StrictAddresses, Unfold(text), out var addresses) ? addresses.Mailboxes : [];

    internal static string? AddressHost(MailboxAddress address)
    {
        int at = address.Address.LastIndexOf('@');
        return at > 0 ? InvestigationArtifacts.Host(address.Address[(at + 1)..]) : null;
    }

    internal static InvestigationKey? AddressKey(MailboxAddress address)
    {
        return AddressHost(address) is { } domain ? AddressKey(address, domain) : null;
    }

    internal static InvestigationKey AddressKey(MailboxAddress address, string host) =>
        new(InvestigationKeyKind.Address, address.Address[..(address.Address.LastIndexOf('@') + 1)] + host);

    internal static IEnumerable<string> ParseIds(string input)
    {
        string value = Unfold(input).Trim();
        foreach (string reference in MimeUtils.EnumerateReferences(value))
        {
            string id = reference.Trim('<', '>');
            int at = id.LastIndexOf('@');
            if (at <= 0) continue;
            if (id[0] == '"' && id[at - 1] == '"')
                id = Regex.Replace(id[1..(at - 1)], @"\\(.)", "$1") + id[at..];
            if (IdPattern().IsMatch(id)) yield return $"<{id}>";
        }
        if (!value.Contains('<') && IdPattern().IsMatch(value)) yield return $"<{value}>";
    }

    internal static string Unfold(string text) => text.Replace("\r", "").Replace('\n', ' ');

    internal static IEnumerable<int> TopLevelSemicolons(string text)
    {
        int comments = 0;
        char closing = '\0';
        bool escape = false;
        for (int i = 0; i < text.Length; i++)
        {
            char value = text[i];
            if (escape) { escape = false; continue; }
            if ((comments > 0 || closing != '\0') && value == '\\') { escape = true; continue; }
            if (comments > 0)
            {
                if (value == '(') comments++;
                else if (value == ')') comments--;
                continue;
            }
            if (closing != '\0') { if (value == closing) closing = '\0'; continue; }
            switch (value)
            {
                case '(': comments++; break;
                case '"': closing = '"'; break;
                case '[': closing = ']'; break;
                case '<': closing = '>'; break;
                case ';': yield return i; break;
            }
        }
    }
}
