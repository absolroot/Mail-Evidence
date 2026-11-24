using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using MailViewer.App;

namespace MailViewer.FlowSmoke;

public static partial class Program
{
    private static async Task RunFindSources(string workspace, string output, string? theme)
    {
        var owner = new MainWindow([], Path.Combine(output, "marks")) { ShowInTaskbar = false };
        owner.Show();
        await Idle();
        bool english = theme == "--light";
        ClickMenu(owner, english ? "EnglishLanguageMenuItem" : "KoreanLanguageMenuItem");
        if (theme is not null) ClickMenu(owner, english ? "LightThemeMenuItem" : "DarkThemeMenuItem");

        Func<string, string, string> text = (ko, en) => english ? en : ko;
        var app = typeof(FindSourcesWindow).Assembly;
        var discovery = app.GetType("MailViewer.App.OutlookSourceDiscovery", throwOnError: true)!;
        var targetType = app.GetType("MailViewer.App.FolderSearchTarget", throwOnError: true)!;
        const BindingFlags hidden = BindingFlags.Static | BindingFlags.NonPublic;
        var discoveryWithFolders = discovery.GetMethods(hidden).Single(method => method.Name == "Find");
        var decodePath = discovery.GetMethod("DecodeProfilePath", hidden)!;
        string local = Path.Combine(output, "Local");
        string documents = Path.Combine(output, "Documents");
        string classic = Directory.CreateDirectory(Path.Combine(local, "Microsoft", "Outlook")).FullName;
        string olk = Directory.CreateDirectory(Path.Combine(local, "Microsoft", "Olk")).FullName;
        string webCache = Directory.CreateDirectory(Path.Combine(olk, "EBWebView")).FullName;
        string profile = Directory.CreateDirectory(Path.Combine(output, "Profile Archive")).FullName;
        string fixture = Path.Combine(workspace, "tests", "fixtures", "pst", "libyal-outlook.pst");
        string archive = Path.Combine(profile, "등록된 사서함.pst");
        string classicFile = Path.Combine(classic, "classic.PST");
        File.Copy(fixture, archive);
        File.Copy(fixture, classicFile);
        File.Copy(fixture, Path.Combine(olk, "export.pst"));
        File.Copy(fixture, Path.Combine(webCache, "not-a-data-root.pst"));
        File.WriteAllText(Path.Combine(classic, "manual.eml"), "Subject: Manual mail fixture\r\n\r\nhello");
        File.WriteAllText(Path.Combine(webCache, "cache.eml"), "Subject: Not in the Outlook file scope\r\n\r\nhello");
        string originalHash = Hash(archive);
        Require(originalHash == Hash(classicFile), "fixture copies must be identical");
        var decoded = (string?)decodePath.Invoke(null, [Encoding.Unicode.GetBytes(archive + "\0"), true]);
        Require(decoded == archive && decodePath.Invoke(null, [@"\\server\share\remote.ost", true]) is null &&
            decodePath.Invoke(null, ["broken\0.pst", false]) is null, "profile path parsing must validate type and local path");
        var registered = Array.CreateInstance(targetType, 3);
        registered.SetValue(Activator.CreateInstance(targetType, decoded, false, true), 0);
        registered.SetValue(Activator.CreateInstance(targetType, archive.ToUpperInvariant(), false, true), 1);
        registered.SetValue(Activator.CreateInstance(targetType, Path.Combine(profile, "missing.ost"), false, true), 2);
        var none = Array.CreateInstance(targetType, 0);

        int liveTargets = 0;
        int liveHits = 0;
        await WithDialog(new FindSourcesWindow(text), async dialog =>
        {
            var scope = Find<ComboBox>(dialog, "OutlookScope");
            var scan = Find<Button>(dialog, "ScanButton");
            liveTargets = scope.IsVisible ? scope.Items.Count - 1 : scan.IsEnabled ? 1 : 0;
            Capture(dialog, output, "01-this-pc.png");
            if (!scan.IsEnabled) return;
            ClickDialogButton(scan);
            await WaitFor(() => Find<RadioButton>(dialog, "CustomLocation").IsEnabled, "this PC's Outlook data-file scan");
            liveHits = Find<DataGrid>(dialog, "HitsGrid").Items.Count;
            Capture(dialog, output, "02-this-pc-results.png");
        });

        var multi = NewDialog(local, documents, registered);
        double popupHeight = 0;
        double popupWidth = 0;
        double scopeHeight = 0;
        double zoom = 1;
        await WithDialog(multi, async dialog =>
        {
            dialog.Width = dialog.MinWidth;
            dialog.Height = dialog.MinHeight;
            var scope = Find<ComboBox>(dialog, "OutlookScope");
            var scan = Find<Button>(dialog, "ScanButton");
            var hits = Find<DataGrid>(dialog, "HitsGrid");
            var accept = Find<Button>(dialog, "AcceptButton");
            Require(scope.IsVisible && scope.Items.Count == 4 && scope.SelectedIndex == 0,
                "all locations and three distinct scopes must be available");
            await Idle();
            scopeHeight = scope.ActualHeight;
            zoom = VisualTreeHelper.GetDpi(scope).DpiScaleY;
            double row = hits.TranslatePoint(new Point(), dialog).Y;
            Require(Math.Abs(scopeHeight - scan.ActualHeight) <= 1 / zoom && Math.Abs(scopeHeight - 30) <= 1 / zoom,
                $"scope and scan button should share the 30 DIP row ({scopeHeight:F2}, {scan.ActualHeight:F2})");
            var peer = (ComboBoxAutomationPeer)UIElementAutomationPeer.CreatePeerForElement(scope);
            var dropdown = (IExpandCollapseProvider)peer.GetPattern(PatternInterface.ExpandCollapse);
            dropdown.Expand();
            await WaitFor(() => scope.IsDropDownOpen && scope.Template.FindName("PART_Popup", scope) is Popup
                { Child: FrameworkElement { ActualHeight: > 0 } }, "the populated native popup");
            await Task.Delay(240);
            var popup = (Popup)scope.Template.FindName("PART_Popup", scope);
            var popupBody = (FrameworkElement)popup.Child;
            popupHeight = popupBody.ActualHeight;
            popupWidth = popupBody.ActualWidth;
            Require(popupHeight is > 0 and <= 200 && Math.Abs(popupWidth - scope.ActualWidth) < 3 &&
                Math.Abs(hits.TranslatePoint(new Point(), dialog).Y - row) < 1,
                "the popup fits the field and opening it leaves the results in place");
            CaptureSurface(popupBody, output, "03-populated-dropdown.png");

            var item = (ComboBoxItem)scope.ItemContainerGenerator.ContainerFromIndex(2);
            Require(item is not null, "the classic Outlook folder must have a selectable dropdown row");
            item!.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonUpEvent
            });
            Require(scope.SelectedIndex == 2, "selection on the dropdown must change the search scope");
            if (scope.IsDropDownOpen) dropdown.Collapse();
            ClickDialogButton(scan);
            await WaitFor(() => hits.Items.Count == 1, "one Outlook result in the selected location");
            Require(ResultPath(hits.Items[0]) == classicFile, "selecting the classic folder should scope the results");
            Require(!accept.IsEnabled, "search results must be checked before adding them");

            scope.SelectedIndex = 0;
            Require(hits.Items.Count == 0, "changing location clears the previous scope");
            ClickDialogButton(scan);
            await WaitFor(() => hits.Items.Count == 3, "all Outlook targets");
            Require(hits.Items.Cast<object>().Select(ResultPath).ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals([archive, classicFile, Path.Combine(olk, "export.pst")]), "all scopes should include only the expected data files");
            Capture(dialog, output, "04-scoped-results.png");

            var custom = Find<RadioButton>(dialog, "CustomLocation");
            var resultWindow = typeof(FindSourcesWindow);
            resultWindow.GetField("location", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(dialog, classic);
            custom.IsChecked = true;
            await Idle();
            Require(!scope.IsVisible && scan.IsEnabled, "switching to a manual location must expose the manual search");
            ClickDialogButton(scan);
            await WaitFor(() => hits.Items.Count == 2, "manual search includes EML along with Outlook types");
            var selectedHit = hits.Items.Cast<object>().Single(hit => ResultPath(hit) == classicFile);
            selectedHit.GetType().GetProperty("Included")!.SetValue(selectedHit, true);
            ClickDialogButton(accept);
        }, close: false);
        var accepted = (System.Collections.IEnumerable)typeof(FindSourcesWindow)
            .GetProperty("SelectedSources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(multi)!;
        Require(accepted.Cast<object>().Select(ResultPath).SequenceEqual([classicFile]) && Hash(archive) == originalHash,
            "add returns only the checked path and the original sample stays intact");

        string emptyLocal = Path.Combine(output, "EmptyLocal");
        await WithDialog(NewDialog(emptyLocal, documents, none), async dialog =>
        {
            Require(!Find<ComboBox>(dialog, "OutlookScope").IsVisible && !Find<Button>(dialog, "ScanButton").IsEnabled &&
                Find<TextBlock>(dialog, "SingleOutlookLocation").IsVisible, "no Outlook locations must show a static message");
            Capture(dialog, output, "05-no-locations.png");
            await Idle();
        });
        Directory.CreateDirectory(Path.Combine(emptyLocal, "Microsoft", "Olk"));
        await WithDialog(NewDialog(emptyLocal, documents, none), async dialog =>
        {
            var scan = Find<Button>(dialog, "ScanButton");
            Require(!Find<ComboBox>(dialog, "OutlookScope").IsVisible && scan.IsEnabled, "a single location does not need a dropdown");
            ClickDialogButton(scan);
            await WaitFor(() => Find<TextBlock>(dialog, "ScanStatus").Text.Contains(text("내보내기", "Export"), StringComparison.Ordinal),
                "new Outlook export guidance for a completed empty search");
            Require(!Find<Button>(dialog, "AcceptButton").IsEnabled, "there is nothing to add from an empty search");
            Capture(dialog, output, "06-new-outlook-empty.png");
        });

        owner.Close();
        File.WriteAllText(Path.Combine(output, "flow-result.json"), JsonSerializer.Serialize(new
        {
            LiveOutlookLocations = liveTargets, LiveOutlookFiles = liveHits,
            ScalePercent = zoom * 100, ScopeHeightDip = scopeHeight, PopupHeightDip = popupHeight, PopupWidthDip = popupWidth,
            ScopedResults = true, ManualSearch = true, SelectedFileOnly = true, EmptyStates = true
        }));

        FindSourcesWindow NewDialog(string appData, string doc, Array entries)
        {
            object locations = discoveryWithFolders.Invoke(null, [appData, doc, entries])!;
            return (FindSourcesWindow)Activator.CreateInstance(typeof(FindSourcesWindow),
                BindingFlags.Instance | BindingFlags.NonPublic, null, [text, locations], null)!;
        }

        async Task WithDialog(FindSourcesWindow dialog, Func<FindSourcesWindow, Task> action, bool close = true)
        {
            dialog.Owner = owner;
            var result = new TaskCompletionSource();
            _ = dialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
            {
                try
                {
                    await action(dialog);
                    if (close) dialog.Close();
                    result.SetResult();
                }
                catch (Exception error)
                {
                    if (dialog.IsVisible) dialog.Close();
                    result.SetException(error);
                }
            }));
            dialog.ShowDialog();
            await result.Task;
        }

        static T Find<T>(Window dialog, string name) where T : FrameworkElement => (T)dialog.FindName(name);
        static string ResultPath(object item) => (string)item.GetType().GetProperty("Path")!.GetValue(item)!;
        static void ClickDialogButton(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
    }
}
