using System.Windows;
using System.Windows.Controls;

namespace MailViewer.App;

public partial class MainWindow
{
    private bool _syncingDisplaySettings;

    private void RefreshDisplaySettings()
    {
        _syncingDisplaySettings = true;
        try
        {
            SettingsThemeBox.SelectedIndex = Appearance.Dark ? 1 : 0;
            SettingsLanguageBox.SelectedItem = MailText.Current;
            foreach (MenuItem item in DisplayLanguageMenu.Items)
                item.IsChecked = Equals(item.Tag, MailText.Current);
            SourcesTab.ToolTip = L("설정 (Ctrl+,)", "Settings (Ctrl+,)");
        }
        finally
        {
            _syncingDisplaySettings = false;
        }
    }

    private void ChangeSettingsTheme(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingDisplaySettings && SettingsThemeBox is { SelectedIndex: >= 0 })
            SetDisplayTheme(SettingsThemeBox.SelectedIndex == 1);
    }

    private void ChangeSettingsLanguage(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingDisplaySettings && SettingsLanguageBox?.SelectedItem is MailLocale locale && CanChooseSources)
            SetDisplayLanguage(locale);
    }

    private void SetDisplayTheme(bool dark)
    {
        Appearance.Apply(Application.Current, dark);
        RefreshDisplaySettings();
    }
}
