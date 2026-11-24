namespace MailViewer.Core;

internal static class MailPropertyNames
{
    public static IReadOnlyList<(ushort Id, string Name)> Addresses { get; } =
    [
        (0x0042, "Sent representing name"),
        (0x0065, "Sent representing address"),
        (0x0C1A, "Sender name"),
        (0x0C1F, "Sender address"),
        (0x5D01, "Sender SMTP address"),
        (0x5D02, "Sent representing SMTP address"),
        (0x0E04, "Display To"),
        (0x0E03, "Display Cc"),
        (0x0E02, "Display Bcc"),
        (0x1035, "Internet Message-ID")
    ];
}
