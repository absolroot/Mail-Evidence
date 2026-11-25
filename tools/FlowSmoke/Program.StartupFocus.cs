using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using MailViewer.App;

namespace MailViewer.FlowSmoke;

public static partial class Program
{
    private static async Task RunStartupFocus(string workspace, string output, string? theme)
    {
        var window = new MainWindow([], Path.Combine(output, "marks")) { ShowInTaskbar = false };
        window.Show();
        await Idle();
        ClickMenu(window, "KoreanLanguageMenuItem");
        if (theme is not null) ClickMenu(window, theme == "--dark" ? "DarkThemeMenuItem" : "LightThemeMenuItem");

        var settings = Field<TabItem>(window, "SourcesTab");
        var workspaceTabs = Field<TabControl>(window, "WorkspaceTabs");
        var intakeScroll = Field<ScrollViewer>(window, "IntakeScroll");
        var openFiles = Field<Button>(window, "OpenFilesButton");
        var openImage = Field<Button>(window, "OpenDiskImageButton");
        var findSources = Field<Button>(window, "FindSourcesButton");
        var sourceActions = Field<StackPanel>(window, "IntakeButtons");
        var display = Field<StackPanel>(window, "DisplaySettingsSection");
        Require(!intakeScroll.Focusable, "the settings scroll surface must not acquire keyboard focus");

        await WithOwnedDialog(() => CallIntake("ShowStartupIntake"), async dialog =>
        {
            Require(dialog.Title == "메일 자료 열기" && !display.IsVisible &&
                ReferenceEquals(FocusManager.GetFocusedElement(dialog), openFiles),
                "the startup modal gives focus to file selection");
            dialog.Width = dialog.MinWidth;
            await Idle();
            Point imageBottom = openImage.TranslatePoint(new Point(0, openImage.ActualHeight), sourceActions);
            Point finderTop = findSources.TranslatePoint(new Point(), sourceActions);
            Require(findSources.IsVisible && finderTop.Y > imageBottom.Y && finderTop.X <= imageBottom.X &&
                findSources.Content as string == "이 PC에서 메일 파일 찾기…",
                "mail discovery should occupy its own left-aligned row below the source buttons at minimum width");
            sourceActions.IsEnabled = false;
            Require(!openFiles.IsEnabled && !findSources.IsEnabled, "both source rows must be disabled together while work is active");
            sourceActions.IsEnabled = true;
            Capture(dialog, output, "01-startup-modal.png");
            Click(window, "CancelIntakeButton");
            await Idle();
        });
        Require(workspaceTabs.SelectedItem == settings && display.IsVisible && openFiles.IsVisible,
            "closing the dialog restores the normal settings panel");
        await CheckPointerFocus(window, Field<ComboBox>(window, "SettingsThemeBox"));

        string path = Path.Combine(workspace, "Mail-Viewer", "tests", "fixtures", "conversation-history", "01-original.eml");
        var grid = Field<DataGrid>(window, "ResultsGrid");
        await WithOwnedDialog(() => CallIntake("ShowStartupIntake"), async dialog =>
        {
            CallIntake("AddCandidates", new[] { path }, null, false);
            await Idle();
            var candidates = Field<DataGrid>(window, "CandidateGrid");
            Require(candidates.IsVisible && candidates.Items.Count == 1, "the normal intake controls work inside the startup modal");
            Capture(dialog, output, "02-startup-source.png");
            Click(window, "AnalyzeButton");
            await WaitFor(() => !dialog.IsVisible && grid.Items.Count == 1, "startup analysis opens the mailbox");
        });
        Require(workspaceTabs.SelectedItem == Field<TabItem>(window, "InvestigationTab") && RecordAt(grid, 0).SourcePath == path,
            "analysis closes startup intake and displays the chosen file");

        var expander = Field<Expander>(window, "InvestigationFilters");
        var scroll = Field<ScrollViewer>(window, "FilterScroll");
        var attachment = Field<ComboBox>(window, "SearchAttachmentBox");
        var choices = Field<StackPanel>(window, "InvestigationFilterChoices").Children.OfType<CheckBox>().ToArray();
        var filter = Field<ToggleButton>(window, "AddressFiltersToggle");
        await WithOwnedDialog(() => filter.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, filter)), async dialog =>
        {
            dialog.Height = dialog.MinHeight;
            await Idle();
            Require(!scroll.Focusable && attachment.FocusVisualStyle == window.FindResource("Desk.KeyboardOutline"),
                "the filter scroll container is skipped and the dropdown uses the keyboard outline");
            expander.IsExpanded = true;
            await WaitFor(() => scroll.VerticalOffset > 0 && InViewport(choices[0]) && InViewport(choices[1]),
                "expanding makes anomaly choices visible without manual scrolling");
            var header = (ToggleButton)expander.Template.FindName("HeaderSite", expander);
            Require(header.FocusVisualStyle == window.FindResource("Desk.KeyboardOutline") &&
                Math.Abs(header.ActualWidth - expander.ActualWidth) < 2, "the disclosure fills its column and has a keyboard outline");
            Capture(dialog, output, "03-filter-expanded.png");
            await CheckPointerFocus(dialog, attachment);
            Click(window, "CancelFilterButton");
        });
        await WithOwnedDialog(() => filter.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, filter)), async dialog =>
        {
            await Idle();
            Require(!expander.IsExpanded && scroll.VerticalOffset == 0 && !attachment.IsKeyboardFocusWithin,
                "reopening the filter resets its viewport and does not revive the old focus");
            Click(window, "CancelFilterButton");
        });
        window.Close();
        var verified = new { StartupModal = true, IntakeRestored = true, AnalyzeFromModal = true,
            FiltersRevealChoices = true, PointerFocusReleased = true, KeyboardOutlinePreserved = true, SeparateSourceFinder = true };
        File.WriteAllText(Path.Combine(output, "flow-result.json"), JsonSerializer.Serialize(verified));

        void CallIntake(string method, params object?[] parameters) =>
            typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, parameters);

        bool InViewport(FrameworkElement element)
        {
            if (!element.IsVisible) return false;
            double top = element.TranslatePoint(new Point(), scroll).Y;
            return top >= 0 && top + element.ActualHeight <= scroll.ViewportHeight + 1;
        }

        async Task CheckPointerFocus(Window owner, Control prior)
        {
            prior.Focus();
            Require(ReferenceEquals(FocusManager.GetFocusedElement(owner), prior), "the dropdown accepts keyboard focus");
            var blank = (UIElement)owner.Content;
            blank.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent
            });
            await Idle();
            Require(!prior.IsKeyboardFocusWithin && ReferenceEquals(FocusManager.GetFocusedElement(owner), blank),
                "a click on the background releases the old dropdown focus");
        }

        async Task WithOwnedDialog(Action open, Func<Window, Task> act)
        {
            var completion = new TaskCompletionSource();
            _ = window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
            {
                Window? dialog = null;
                try
                {
                    await WaitFor(() => (dialog = window.OwnedWindows.OfType<Window>().FirstOrDefault(item => item.IsVisible)) is not null,
                        "the expected owned modal");
                    await act(dialog!);
                    completion.SetResult();
                }
                catch (Exception error)
                {
                    completion.SetException(error);
                    dialog?.Close();
                }
            }));
            open();
            await completion.Task;
        }
    }
}
