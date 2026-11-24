using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MailViewer.App;

namespace MailViewer.FlowSmoke;

public static partial class Program
{
    private static async Task RunLanguages(string workspace, string output)
    {
        var path = Path.Combine(workspace, "tests", "fixtures", "attachment-boundaries", "02-mixed.eml");
        var window = new MainWindow([path], Path.Combine(output, "marks")) { ShowInTaskbar = false, Width = 1030 };
        window.Show();
        try
        {
            await Idle();
            var languageBox = Field<ComboBox>(window, "SettingsLanguageBox");
            var settings = Field<TabItem>(window, "SourcesTab");
            var workspaceTabs = Field<TabControl>(window, "WorkspaceTabs");
            var reader = Field<TabControl>(window, "DetailTabs");
            var anomalies = Field<TabItem>(window, "InvestigationCluesTab");
            var findings = Field<DataGrid>(window, "InvestigationFindings");
            var results = Field<DataGrid>(window, "ResultsGrid");
            Require(languageBox.Items.Count == 16, "Expected the same language count as NTFS Tracer");
            Click(window, "AnalyzeButton");
            await WaitFor(() => results.Items.Count == 1 && results.SelectedItem is not null, "opened fixture");
            var languages = new Dictionary<string, string>();
            string[] codes = ["ko", "en", "ja", "de", "es", "fr", "id", "it", "pl", "pt-BR", "tr", "vi", "ru", "uk", "zh-Hans", "zh-Hant"];
            string[] labels = ["이상 징후", "Anomalies", "異常の兆候", "Auffälligkeiten", "Anomalías", "Anomalies", "Anomali",
                "Anomalie", "Anomalie", "Anomalias", "Anormallikler", "Dấu hiệu bất thường", "Аномалии", "Аномалії", "异常迹象", "異常跡象"];
            for (int index = 0; index < codes.Length; index++)
            {
                string code = codes[index];
                workspaceTabs.SelectedItem = settings;
                languageBox.SelectedIndex = index;
                await Idle();
                Capture(window, output, $"settings-{code}.png");
                workspaceTabs.SelectedIndex = 0;
                reader.SelectedItem = anomalies;
                await Idle();
                Require(anomalies.Header.ToString()!.Contains(labels[index], StringComparison.Ordinal) &&
                    anomalies.Header.ToString()!.Contains('4'), $"Missing localized anomaly count for {code}");
                findings.SelectedIndex = 1;
                await Idle();
                string evidence = Field<TextBlock>(window, "FindingOutcome").Text;
                string offset = Field<TextBlock>(window, "FindingBytePosition").Text;
                Require(evidence.Contains("IDAT", StringComparison.Ordinal) &&
                    !evidence.Contains("declared length", StringComparison.Ordinal) == (code != "en") &&
                    offset.Contains("0x21", StringComparison.Ordinal) && !evidence.Contains("{0}"),
                    $"Localized evidence is missing or changed the source value for {code}");
                languages.Add(code, CultureInfo.CurrentUICulture.Name);
                Capture(window, output, $"evidence-{code}.png");
            }

            ClickMenu(window, "KoreanLanguageMenuItem");
            Require((string)settings.Header == "설정" && anomalies.Header.ToString()!.Contains("이상 징후"),
                "Switching back to Korean must restore the original static and dynamic labels");
            Require(RecordAt(results, 0).SourcePath == path, "Switching languages changed the investigated source");
            File.WriteAllText(Path.Combine(output, "flow-result.json"), JsonSerializer.Serialize(languages));
        }
        finally
        {
            window.Close();
        }
    }
}
