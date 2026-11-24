using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace MailViewer.App;

internal sealed record MailLocale(string Code, string CultureName, string Name);

internal static class MailText
{
    private static readonly Dictionary<string, Dictionary<string, string>> Catalogs = new(StringComparer.Ordinal);
    private static readonly string PreferencePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MailEvidence", "language.txt");
    internal static readonly MailLocale[] Locales =
    [
        new("ko", "ko-KR", "한국어"),
        new("en", "en-US", "English"),
        new("ja", "ja-JP", "日本語"),
        new("de", "de-DE", "Deutsch"),
        new("es", "es-ES", "Español"),
        new("fr", "fr-FR", "Français"),
        new("id", "id-ID", "Bahasa Indonesia"),
        new("it", "it-IT", "Italiano"),
        new("pl", "pl-PL", "Polski"),
        new("pt-BR", "pt-BR", "Português (Brasil)"),
        new("tr", "tr-TR", "Türkçe"),
        new("vi", "vi-VN", "Tiếng Việt"),
        new("ru", "ru-RU", "Русский"),
        new("uk", "uk-UA", "Українська"),
        new("zh-Hans", "zh-CN", "简体中文"),
        new("zh-Hant", "zh-TW", "繁體中文")
    ];
    internal static MailLocale Current { get; private set; } = Locales[0];
    internal static CultureInfo Culture => CultureInfo.GetCultureInfo(Current.CultureName);

    internal static void LoadPreference()
    {
        try
        {
            string code = File.ReadAllText(PreferencePath).Trim();
            if (Locales.FirstOrDefault(locale => locale.Code == code) is { } preference)
            {
                Apply(preference, save: false);
                return;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        for (var culture = CultureInfo.CurrentUICulture; culture.Name.Length > 0; culture = culture.Parent)
        {
            if (Locales.FirstOrDefault(locale => locale.Code.Equals(culture.Name, StringComparison.OrdinalIgnoreCase) ||
                locale.CultureName.Equals(culture.Name, StringComparison.OrdinalIgnoreCase)) is { } match)
            {
                Apply(match, save: false);
                return;
            }
        }
        Apply(Locales[1], save: false);
    }

    internal static void Apply(MailLocale locale, bool save)
    {
        Current = locale;
        CultureInfo.CurrentUICulture = Culture;
        JournalDesk.L.Refresh();
        if (!save) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
            File.WriteAllText(PreferencePath, locale.Code);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static string? Lookup(string key)
    {
        lock (Catalogs)
        {
            if (!Catalogs.TryGetValue(Current.Code, out var catalog))
            {
                using var stream = typeof(MailText).Assembly.GetManifestResourceStream($"MailViewer.App.Locale.{Current.Code}.json");
                catalog = stream is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
                Catalogs.Add(Current.Code, catalog);
            }
            return catalog.GetValueOrDefault(key);
        }
    }

    internal static string Get(string korean, string english) => Current.Code == "ko"
        ? Lookup("@ko:" + korean) ?? korean : Lookup(english) ?? english;

    internal static string Get(string korean, LocalizedMessage english) =>
        Current.Code == "ko" ? korean : english.Translate();

    internal static string Month(DateTime date, bool includeYear) =>
        date.ToString(includeYear ? Culture.DateTimeFormat.YearMonthPattern : "MMMM", Culture);
}

[InterpolatedStringHandler]
public struct LocalizedMessage
{
    private readonly StringBuilder format;
    private readonly List<object?> arguments;

    public LocalizedMessage(int literalLength, int formattedCount)
    {
        format = new StringBuilder(literalLength);
        arguments = new List<object?>(formattedCount);
    }

    public void AppendLiteral(string value) => format.Append(value.Replace("{", "{{").Replace("}", "}}"));

    public void AppendFormatted<T>(T value, string? format = null) => AppendFormatted(value, 0, format);

    public void AppendFormatted<T>(T value, int alignment, string? format = null)
    {
        this.format.Append('{').Append(arguments.Count);
        if (alignment != 0) this.format.Append(',').Append(alignment);
        if (format is not null) this.format.Append(':').Append(format);
        this.format.Append('}');
        arguments.Add(value);
    }

    internal string Translate() => string.Format(MailText.Culture,
        MailText.Lookup(format.ToString()) ?? format.ToString(), arguments.ToArray());

    internal string English() => string.Format(CultureInfo.InvariantCulture, format.ToString(), arguments.ToArray());
}
