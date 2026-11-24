using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using MailViewer.App;

namespace JournalDesk;

// Adapter for the NTFS Tracer WPF controls shared by Mail Evidence.
public static class L
{
    private static readonly ResourceManager Captions = new("JournalDesk.UiText", typeof(L).Assembly);
    public static LanguageCatalog Text { get; } = new();
    public static string Code => MailText.Current.Code;
    public static bool IsEnglish => Code == "en";
    public static XmlLanguage XmlLanguage => XmlLanguage.GetLanguage(MailText.Culture.Name);

    internal static void Refresh() => Text.Refresh();

    public static string T(string korean, string english) => MailText.Get(korean, english);
    public static string T(string korean, LocalizedMessage english) => MailText.Get(korean, english);
    public static string T(string korean, string english, params object?[] values) =>
        string.Format(MailText.Culture, T(korean, english), values);

    internal static string Caption(string key) => MailText.Lookup("@ui:" + key) ??
        Captions.GetString(key, MailText.Culture) ??
        throw new MissingManifestResourceException($"Missing shared caption: {key}");
}

public sealed class LanguageCatalog : INotifyPropertyChanged
{
    public string this[string key] => L.Caption(key);
    public XmlLanguage Language { get; private set; } = XmlLanguage.GetLanguage("ko-KR");
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Refresh()
    {
        Language = L.XmlLanguage;
        PropertyChanged?.Invoke(this, new(Binding.IndexerName));
        PropertyChanged?.Invoke(this, new(nameof(Language)));
    }
}

[MarkupExtensionReturnType(typeof(object))]
public sealed class UiExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget { TargetProperty: System.Reflection.PropertyInfo property }
            && property.PropertyType == typeof(string))
            return L.Caption(key);
        return new Binding($"[{key}]") { Source = L.Text, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
    }
}
