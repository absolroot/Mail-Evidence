using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MailViewer.App;
using MailViewer.Core;

namespace MailViewer.FlowSmoke;

public static partial class Program
{
    private static async Task RunMailboxFolders(string output, string? theme)
    {
        string root = Path.Combine(output, "Imported-mail");
        string branch = Path.Combine(root, "2026", "November");
        Directory.CreateDirectory(branch);
        string outside = Path.Combine(output, "outside.eml");
        WriteSample(outside, "Separate file");
        WriteSample(Path.Combine(root, "root.eml"), "At the root");
        WriteSample(Path.Combine(branch, "one.eml"), "November first");
        WriteSample(Path.Combine(branch, "two.eml"), "November second");

        var window = new MainWindow([root, outside]) { ShowInTaskbar = false, Width = 1190, Height = 795 };
        window.Show();
        await Idle();
        ClickMenu(window, "KoreanLanguageMenuItem");
        if (theme is not null) ClickMenu(window, theme == "--dark" ? "DarkThemeMenuItem" : "LightThemeMenuItem");
        var grid = Field<DataGrid>(window, "ResultsGrid");
        var tree = Field<TreeView>(window, "PstFolderTree");
        var busy = Field<Button>(window, "CancelWorkButton");
        await WaitFor(() => !busy.IsEnabled && Field<DataGrid>(window, "CandidateGrid").Items.Count == 4, "directory scan");
        Click(window, "AnalyzeButton");
        await WaitFor(() => !busy.IsEnabled && grid.Items.Count == 4 && tree.IsVisible, "indexed folder and separate file");

        var rootNode = FindFolderRow(root);
        var middle = FindFolderRow(Path.Combine(root, "2026"));
        var leaf = FindFolderRow(branch);
        var paneToggle = Field<ToggleButton>(window, "PstFoldersToggle");
        var all = Field<ToggleButton>(window, "AllMailScopeButton");
        Require(rootNode.IsExpanded && !middle.IsExpanded && !leaf.IsVisible,
            "The first load must reveal one folder level.");
        Require(paneToggle.TranslatePoint(new Point(paneToggle.ActualWidth, 0), window).X <= all.TranslatePoint(new Point(), window).X,
            "The pane button must be located to the left of All mail.");
        Capture(window, output, "one-level.png");
        paneToggle.IsChecked = false;
        await Idle();
        Require(paneToggle.IsVisible && !all.IsVisible && !tree.IsVisible && grid.Items.Count == 4,
            "Collapsing the sidebar should retain its button and the current results.");
        paneToggle.IsChecked = true;
        await Idle();
        Require(tree.IsVisible && rootNode.IsExpanded && !middle.IsExpanded && all.IsVisible,
            "Reopening the sidebar changes the initial folder depth.");
        var arrow = (ToggleButton)middle.Template.FindName("FolderArrow", middle);
        var chevron = (ContentControl)arrow.Template.FindName("Chevron", arrow);
        middle.IsExpanded = true;
        await WaitFor(() => chevron.RenderTransform is RotateTransform { Angle: > 89 } && leaf.IsVisible,
            "expanded chevron and child reveal");
        middle.IsExpanded = false;
        await WaitFor(() => chevron.RenderTransform is RotateTransform { Angle: < 1 } && !leaf.IsVisible,
            "collapsed chevron and hidden children");
        Require(MainWindow.GetMailboxCount(rootNode) == "[3]" && MainWindow.GetMailboxCount(middle) == "[2]" &&
            MainWindow.GetMailboxCount(leaf) == "[2]", "The navigator counted a source from outside the selected branch.");
        var label = (TextBlock)middle.Template.FindName("FolderCount", middle);
        Require(label.IsVisible && label.Text == "[2]" &&
            label.TranslatePoint(new Point(0, 0), middle).X > middle.ActualWidth / 2,
            "The bracketed count is not visible on the right of the middle folder.");
        Select(middle);
        await WaitFor(() => middle.IsSelected && grid.Items.Count == 2 && !busy.IsEnabled, "selected middle folder includes descendants");
        Require(grid.Items.Cast<object>().All(item => RecordFromRow(item).SourcePath.StartsWith(branch, StringComparison.OrdinalIgnoreCase)),
            "The middle folder's list contains mail outside its subtree.");
        Capture(window, output, "folder-counts.png");

        var query = Field<TextBox>(window, "SearchQueryBox");
        var clear = Field<Button>(window, "ClearSearchButton");
        Require(!clear.IsVisible, "The clear button is visible with an empty query.");
        query.Text = "November first";
        await WaitFor(() => clear.IsVisible && grid.Items.Count == 1, "live search within the selected folder");
        Require(clear.Focusable && clear.IsTabStop, "The clear button must support keyboard focus.");
        Capture(window, output, "search-clear.png");
        var clearPeer = new ButtonAutomationPeer(clear);
        ((IInvokeProvider)clearPeer.GetPattern(PatternInterface.Invoke)).Invoke();
        await WaitFor(() => query.Text.Length == 0 && !clear.IsVisible && middle.IsSelected &&
            grid.Items.Count == 2 && System.Windows.Input.FocusManager.GetFocusedElement(window) == query,
            "clearing search restores folder results and input focus");


        query.Text = "November second";
        await WaitFor(() => grid.Items.Count == 1, "second live search within the selected folder");
        query.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(query), 0, System.Windows.Input.Key.Escape)
            { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
        await WaitFor(() => query.Text.Length == 0 && middle.IsSelected && grid.Items.Count == 2,
            "Escape clears the query without changing folder scope");

        var exported = await Export(window, "ExportSearchButton", output, ExportKind.Csv);
        Require(File.ReadAllLines(Path.Combine(exported, "messages.csv")).Length == 3,
            "The saved results include mail outside the folder.");
        await WaitFor(() => middle.IsSelected && grid.Items.Count == 2, "export return restores folder scope");
        using var exportedManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(exported, "manifest.json")));
        var manifestRoot = exportedManifest.RootElement;
        Require(manifestRoot.GetProperty("sources").GetArrayLength() == 2 &&
            manifestRoot.GetProperty("search").GetProperty("sourceGroup").GetProperty("sourceIds").GetArrayLength() == 2,
            "The export manifest did not retain the two sources in the selected directory.");

        double gap = -1;
        InFilterDialog(window, dialog =>
        {
            dialog.Height = dialog.MinHeight;
            Field<Expander>(window, "InvestigationFilters").IsExpanded = true;
            var scroll = Field<ScrollViewer>(window, "FilterScroll");
            dialog.UpdateLayout();
            var scrollBar = (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
            var input = Field<ComboBox>(window, "SearchFieldBox");
            Require(scrollBar.IsVisible && scroll.ScrollableHeight > 0, "The filter fixture did not force a vertical scrollbar.");
            gap = scrollBar.TranslatePoint(new Point(), scroll).X - input.TranslatePoint(new Point(input.ActualWidth, 0), scroll).X;
            Require(gap >= 10, $"Filter control overlaps the scrollbar. Gap: {gap:F2} DIP.");
            scroll.ScrollToVerticalOffset(120);
            dialog.UpdateLayout();
            Capture(dialog, output, "filter-scroll-gap.png");
        }, apply: false);
        Require(middle.IsSelected && grid.Items.Count == 2, "Cancelling filters lost the selected folder.");

        var hover = (Border)middle.Template.FindName("FolderHoverLayer", middle);
        var candidate = (Border)rootNode.Template.FindName("FolderHoverLayer", rootNode);
        Require(!hover.IsVisible && candidate.IsVisible, "The selected folder's highlight is covered by hover.");
        InteractionFeedback.SetHoverVisible(candidate, true);
        await WaitFor(() => candidate.Opacity > 0.99, "hover settled");
        InteractionFeedback.SetHoverVisible(candidate, false);
        await WaitFor(() => candidate.Opacity < 0.01, "hover cleared");
        candidate.ClearValue(InteractionFeedback.HoverVisibleProperty);

        Select(rootNode);
        await WaitFor(() => rootNode.IsSelected && grid.Items.Count == 3 && !busy.IsEnabled, "root folder separate from ungrouped source");
        middle.IsExpanded = true;
        Select(leaf);
        await WaitFor(() => leaf.IsSelected && grid.Items.Count == 2 && !busy.IsEnabled, "leaf folder can be selected");
        all.IsChecked = true;
        all.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, all));
        await WaitFor(() => all.IsChecked == true && grid.Items.Count == 4 && !busy.IsEnabled,
            "All mail returns from a deep folder without changing its expansion");
        Require(middle.IsExpanded, "Returning to all mail collapsed a branch the user opened.");
        Select(leaf);
        await WaitFor(() => leaf.IsSelected && grid.Items.Count == 2 && !busy.IsEnabled, "restore leaf before append");
        string newPath = Path.Combine(branch, "three.eml");
        WriteSample(newPath, "November third");
        Field<TabControl>(window, "WorkspaceTabs").SelectedItem = Field<TabItem>(window, "SourcesTab");
        var addFolder = typeof(MainWindow).GetMethod("AddFoldersAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await (Task)addFolder.Invoke(window, new object[] { new[] { root } })!;
        Click(window, "AnalyzeButton");
        await WaitFor(() => !busy.IsEnabled && tree.IsVisible && grid.Items.Count == 3 &&
            FindFolderRow(branch).IsSelected, "appended descendant joins the current folder");
        Require(MainWindow.GetMailboxCount(FindFolderRow(branch)) == "[3]" &&
            grid.Items.Cast<object>().Any(item => RecordFromRow(item).SourcePath == newPath),
            "Adding a third descendant did not update the selected scope and count.");
        File.WriteAllText(Path.Combine(output, "flow-result.json"), JsonSerializer.Serialize(new
        {
            MiddleFolderCount = 2, AppendedFolderCount = 3, FilterGapDip = gap,
            ExportScopeVerified = true, SelectionRestored = true, HoverEndpointVerified = true,
            OneLevel = true, SidebarButtonOnLeft = true, ChevronSettles = true
        }));
        window.Close();

        TreeViewItem FindFolderRow(string path)
        {
            static IEnumerable<TreeViewItem> Walk(ItemsControl node) => node.Items.OfType<TreeViewItem>()
                .SelectMany(item => new[] { item }.Concat(Walk(item)));
            return Walk(tree).Single(item => string.Equals(item.ToolTip as string, path, StringComparison.OrdinalIgnoreCase));
        }

        static void Select(TreeViewItem item)
        {
            var peer = new TreeViewItemAutomationPeer(item);
            ((ISelectionItemProvider)peer.GetPattern(PatternInterface.SelectionItem)!).Select();
        }

        static void WriteSample(string path, string subject) => File.WriteAllText(path,
            $"From: sample@example.test\r\nTo: investigator@example.test\r\nSubject: {subject}\r\n\r\nMail folder verification.\r\n");
    }
}

