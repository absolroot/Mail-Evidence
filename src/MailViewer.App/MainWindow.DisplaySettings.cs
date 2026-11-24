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
            SettingsLanguageBox.SelectedIndex = _english ? 1 : 0;
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
        if (!_syncingDisplaySettings && SettingsLanguageBox is { SelectedIndex: >= 0 } && CanChooseSources)
            SetDisplayLanguage(SettingsLanguageBox.SelectedIndex == 1);
    }

    private void SetDisplayTheme(bool dark)
    {
        Appearance.Apply(Application.Current, dark);
        RefreshDisplaySettings();
    }
}
