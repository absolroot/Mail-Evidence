using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace MailViewer.App;

internal static class Appearance
{
    private static readonly (string Name, string Light, string Dark)[] Colors =
    [
        ("Canvas", "#FFFFFF", "#1F1F1F"), ("Panel", "#F6F7F9", "#242424"),
        ("TabRail", "#EAEFF4", "#282C31"), ("TabRule", "#CCD4DE", "#3B4249"),
        ("RowAlternate", "#F7F7F7", "#252525"),
        ("Inset", "#EBEEF2", "#242424"), ("Section", "#E8EDF3", "#2A3039"), ("RowRule", "#E0E4E9", "#383B40"),
        ("Text", "#242424", "#F0F0F0"), ("Muted", "#595D66", "#B8BDC6"),
        ("Disabled", "#686C74", "#A3A8B0"), ("Rule", "#C7CCD3", "#555962"),
        ("Hover", "#E7EDF4", "#32373F"), ("Pressed", "#D8E4F0", "#3C4654"),
        ("TabHover", "#D8E2EF", "#3D4958"), ("TabHoverText", "#242424", "#F0F0F0"),
        ("Selection", "#D9EAF8", "#274B68"), ("SelectionText", "#242424", "#F0F0F0"), ("Accent", "#005FB8", "#80BFFA"),
        ("AccentText", "#FFFFFF", "#101820"),
        ("FieldHighlight", "#D7EAF7", "#273F50"), ("Link", "#245681", "#8AC4FF"),
        ("WarningText", "#9C251B", "#FFB2A8"), ("WarningFill", "#FDE9E6", "#492624"), ("AnomalyMarker", "#906400", "#F4C66A")
    ];

    private static ResourceDictionary? _palette;
    private static bool _highContrast;

    public static bool Dark { get; private set; }

    public static bool MatchWindows() => Registry.GetValue(
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;

    public static void Apply(Application application, bool dark)
    {
        bool highContrast = SystemParameters.HighContrast;
        if (_palette is not null && Dark == dark && _highContrast == highContrast)
            return;

        Dark = dark;
        _highContrast = highContrast;
        var palette = new ResourceDictionary();
        foreach (var (name, light, night) in Colors)
        {
            SolidColorBrush brush = highContrast ? HighContrastBrush(name)
                : new((Color)ColorConverter.ConvertFromString(dark ? night : light));
            brush.Freeze();
            palette["Viewer." + name] = brush;
            palette["Desk." + name] = brush;
        }

        palette["Desk.Selected"] = palette["Viewer.Selection"];
        if (!highContrast)
            AddFluentBrushes(palette);

#pragma warning disable WPF0001
        if (_palette is null)
        {
            var startupTheme = dark ? ThemeMode.Dark : ThemeMode.Light;
            if (application.ThemeMode != startupTheme)
                application.ThemeMode = startupTheme;
        }
#pragma warning restore WPF0001
        foreach (Window window in application.Windows)
            ApplyToWindow(window);
        var dictionaries = application.Resources.MergedDictionaries;
        int index = _palette is null ? -1 : dictionaries.IndexOf(_palette);
        if (index >= 0)
            dictionaries[index] = palette;
        else
            dictionaries.Add(palette);
        _palette = palette;
    }

    public static void ApplyToWindow(Window window)
    {
#pragma warning disable WPF0001
        var theme = Dark ? ThemeMode.Dark : ThemeMode.Light;
        if (window.ThemeMode != theme)
            window.ThemeMode = theme;
#pragma warning restore WPF0001
    }

    private static void AddFluentBrushes(ResourceDictionary palette)
    {
        SetBrushes("Panel", "ButtonBackground", "ButtonBackgroundDisabled", "ComboBoxBackground",
            "ComboBoxBackgroundDisabled", "ComboBoxDropDownBackground", "DatePickerBackground", "DatePickerBackgroundDisabled",
            "DatePickerPopupBackground", "ExpanderHeaderBackground", "ExpanderContentBackground");
        SetBrushes("Hover", "ButtonBackgroundPointerOver", "ComboBoxBackgroundPointerOver", "ComboBoxItemBackgroundSelected",
            "DatePickerBackgroundPointerOver");
        SetBrushes("Canvas", "TextControlBackground", "TextControlBackgroundFocused", "DatePickerBackgroundFocused");
        SetBrushes("Pressed", "ButtonBackgroundPressed", "ComboBoxBackgroundPressed");
        SetBrushes("Rule", "ButtonBorderBrush", "ButtonBorderBrushDisabled", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed",
            "ComboBoxBorderBrush", "ComboBoxBorderBrushDisabled", "ComboBoxBorderBrushPointerOver", "ComboBoxBorderBrushPressed",
            "ComboBoxDropDownBorderBrush", "ExpanderHeaderBorderBrush", "ExpanderHeaderBorderPointerOverBrush");
        SetBrushes("Text", "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed", "ComboBoxForeground",
            "ComboBoxForegroundPointerOver", "ComboBoxForegroundPressed", "ComboBoxForegroundFocused", "ComboBoxForegroundFocusedPressed",
            "ComboBoxItemForegroundSelected", "ComboBoxDropDownGlyphForeground", "ComboBoxDropDownGlyphForegroundFocused",
            "ComboBoxDropDownGlyphForegroundFocusedPressed", "DatePickerTextBoxForeground", "ExpanderHeaderForeground",
            "CalendarViewForeground", "TextControlForeground", "TextControlForegroundFocused");
        SetBrushes("Disabled", "ButtonForegroundDisabled", "ComboBoxForegroundDisabled", "ComboBoxDropDownGlyphForegroundDisabled",
            "ExpanderHeaderDisabledForeground");
        SetBrushes("Accent", "ComboBoxItemPillFillBrush");

        void SetBrushes(string color, params string[] keys)
        {
            object brush = palette["Viewer." + color];
            foreach (string key in keys) palette[key] = brush;
        }
    }

    private static SolidColorBrush HighContrastBrush(string name) => name switch
    {
        "AccentText" or "Canvas" or "TabRail" or "Panel" or "Inset" or "Section" or "RowAlternate" or "WarningFill" => SystemColors.WindowBrush,
        "Selection" or "Hover" or "Pressed" or "TabHover" => SystemColors.HighlightBrush,
        "SelectionText" or "TabHoverText" => SystemColors.HighlightTextBrush,
        "Disabled" => SystemColors.GrayTextBrush,
        _ => SystemColors.WindowTextBrush
    };
}
