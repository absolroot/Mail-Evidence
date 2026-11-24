using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using JournalDesk;
using MailViewer.App;
using MailViewer.Core;

namespace MailViewer.FlowSmoke;

public static partial class Program
{
    private static async Task RunAttachmentBoundaries(string workspace, string output, string? theme)
    {
        var fixtures = Path.Combine(workspace, "tests", "fixtures", "attachment-boundaries");
        string[] paths = Directory.GetFiles(fixtures, "*.eml").Order().ToArray();
        string[] hashes = paths.Select(Hash).ToArray();
        using var session = EvidenceSession.Open(paths);
        var request = new SearchRequest { Clues = InvestigationClue.AttachmentBoundaryMismatch };
        var search = session.Search(request);
        Require(search.Hits.Count == 2, "Expected only the two messages with observed boundary differences.");
        var mixed = search.Hits.Select(hit => hit.Record).Single(record => record.SourcePath.EndsWith("02-mixed.eml"));
        var expected = new Dictionary<string, AttachmentBoundaryKind>
        {
            ["starts-as-gif.png"] = AttachmentBoundaryKind.HeaderMismatch,
            ["invalid-idat.png"] = AttachmentBoundaryKind.StructureMismatch,
            ["missing-iend.png"] = AttachmentBoundaryKind.MissingEnd,
            ["extra-after-eoi.jpg"] = AttachmentBoundaryKind.TrailingBytes
        };
        var observations = session.Inspect(mixed.Id).Observations.Where(item => item.Boundary is not null).ToArray();
        Require(observations.Length == expected.Count && observations.All(item =>
            expected.GetValueOrDefault(item.Value) == item.Boundary!.Kind && item.DecodedAttachmentId is not null),
            "Each mixed-sample attachment must produce its assigned finding.");
        var containers = search.Hits.Select(hit => hit.Record).Single(record => record.SourcePath.EndsWith("03-container-boundaries.eml"));
        Require(session.Inspect(containers.Id).Observations.Count(item => item.Boundary?.Kind == AttachmentBoundaryKind.TrailingBytes) == 2,
            "ZIP comment and PDF whitespace are part of their valid endings.");

        var window = new MainWindow(paths) { ShowInTaskbar = false, Width = 1190, Height = 795 };
        window.Show();
        await Idle();
        if (theme is not null) ClickMenu(window, theme == "--dark" ? "DarkThemeMenuItem" : "LightThemeMenuItem");
        Click(window, "AnalyzeButton");
        var grid = Field<DataGrid>(window, "ResultsGrid");
        var tabs = Field<TabControl>(window, "WorkspaceTabs");
        var anomaly = Field<TabItem>(window, "AnomalyMailboxTab");
        var details = Field<TabControl>(window, "DetailTabs");
        var findings = Field<DataGrid>(window, "InvestigationFindings");
        var raw = Field<HexEvidenceView>(window, "RawBytesBox");
        await WaitFor(() => grid.Items.Count == 3 && anomaly.Header as string == "이상 징후 (2건)", "new anomaly tab count");
        tabs.SelectedItem = anomaly;
        await WaitFor(() => grid.Items.Count == 2 && !Field<Button>(window, "CancelWorkButton").IsEnabled, "two anomalies");
        grid.SelectedItem = grid.Items.Cast<object>().Single(item => RecordFromRow(item).SourcePath.EndsWith("02-mixed.eml"));
        var clues = Field<TabItem>(window, "InvestigationCluesTab");
        details.SelectedItem = clues;
        await Idle();
        Require(findings.Items.Count == 4, "The four file findings must remain individually selectable.");
        Require(Equals(findings.Columns[0].Header, "이상 징후") && Equals(findings.Columns[1].Header, "대상"),
            "Finding names and affected files appear in separate columns.");
        var visibleRows = Enumerable.Range(0, findings.Items.Count)
            .Select(index => (DataGridRow)findings.ItemContainerGenerator.ContainerFromIndex(index)).ToArray();
        var outcome = Field<TextBlock>(window, "FindingOutcome");
        var preview = Field<TextBlock>(window, "FindingBytePreview");
        var position = Field<TextBlock>(window, "FindingBytePosition");
        var hexButton = Field<Button>(window, "OpenFindingSourceButton");
        Require(visibleRows.All(row => row.IsVisible && row.ActualWidth > 100) &&
            visibleRows.Zip(visibleRows.Skip(1)).All(pair =>
                pair.First.TranslatePoint(new Point(), window).Y < pair.Second.TranslatePoint(new Point(), window).Y) &&
            visibleRows[^1].TranslatePoint(new Point(0, visibleRows[^1].ActualHeight), window).Y <
                outcome.TranslatePoint(new Point(), window).Y,
            "All four rows sit above the fixed evidence area.");

        foreach (var item in observations)
        {
            var boundary = item.Boundary!;
            object selected = findings.Items.Cast<object>().Single(finding =>
                Equals(finding.GetType().GetProperty("Target")!.GetValue(finding), item.Value));
            findings.SelectedItem = selected;
            await Idle();
            Require(position.Text.Contains($"0x{boundary.Range.Offset:X}") && preview.IsVisible &&
                hexButton.IsVisible && !Field<DataGrid>(window, "InvestigationGrid").IsVisible &&
                !Field<TextBlock>(window, "FindingDescription").IsVisible,
                "The observed bytes precede the drill-down and the reading criteria stay collapsed.");
            byte[] bytes = session.ReadAttachment(mixed.Id, item.DecodedAttachmentId!);
            var excerpt = boundary.Preview!;
            Require(excerpt.Bytes.Length <= 24 && bytes.AsSpan((int)excerpt.Offset, excerpt.Bytes.Length).SequenceEqual(excerpt.Bytes.Span),
                "The inline excerpt must contain the exact decoded bytes.");
            var tokens = preview.Inlines.OfType<Run>().Where(run => run.Text.Length == 2).ToArray();
            Require(tokens.Select(run => Convert.ToByte(run.Text, 16)).SequenceEqual(excerpt.Bytes.ToArray()) &&
                tokens.Count(run => run.FontWeight == FontWeights.Bold) ==
                Math.Max(0, Math.Min(excerpt.Offset + excerpt.Bytes.Length, boundary.Range.End) - boundary.Range.Offset),
                "Only the observed range must be highlighted in the excerpt.");
            if (boundary.Kind == AttachmentBoundaryKind.StructureMismatch)
            {
                var facts = Field<ItemsControl>(window, "FindingFacts").Items.Cast<object>().Select(item =>
                    (string)item.GetType().GetProperty("Value")!.GetValue(item)!).ToArray();
                Require(outcome.Text.Contains("IDAT") && facts.SequenceEqual(["8,200바이트", "최대 37바이트"]),
                    "PNG compares the declared length with the capacity rather than displaying opaque bytes alone.");
                Capture(window, output, "png-length-preview.png");
            }
            if (boundary.Kind == AttachmentBoundaryKind.TrailingBytes) Capture(window, output, "findings.png");
            if (boundary.Kind is AttachmentBoundaryKind.HeaderMismatch or AttachmentBoundaryKind.MissingEnd)
                Capture(window, output, $"preview-{boundary.Kind}.png");
            Click(window, "OpenFindingSourceButton");
            await WaitFor(() => details.SelectedItem == Field<TabItem>(window, "RawTab") && raw.ByteCount > 0 &&
                !Field<Button>(window, "CancelWorkButton").IsEnabled, "decoded attachment HEX");
            Require(raw.AddressLabel.Contains("디코딩") && Field<TextBlock>(window, "RawSignatureText").Text.Contains($"0x{boundary.Range.Offset:X}"),
                "The HEX banner did not identify the decoded range and expected boundary.");
            if (boundary.Range.Length > 0)
            {
                Require(raw.SelectedField is { IsWarning: true } marked && raw.BaseOffset + marked.Start == boundary.Range.Offset,
                    "The marked start is not the byte observed in the decoded attachment.");
                Require(raw.SelectedHex == bytes[boundary.Range.Offset].ToString("X2"), "The displayed byte differs from the decoded attachment.");
                if (boundary.Kind == AttachmentBoundaryKind.TrailingBytes)
                {
                    Click(window, "RawNextButton");
                    await Idle();
                    Require(raw.BaseOffset == 4096 && raw.SelectedField is { IsWarning: true }, "The mark stops incorrectly at the next page.");
                    Click(window, "RawPreviousButton");
                }
            }
            else
                Require(raw.SelectedField is null && raw.BaseOffset + raw.ByteCount == boundary.Range.Offset,
                    "An absent marker must show the file end without marking any bytes as observed.");
            Capture(window, output, $"hex-{boundary.Kind}.png");
            Click(window, "RawBackButton");
            Require(details.SelectedItem == clues && ReferenceEquals(findings.SelectedItem, selected), "Return to the same finding.");
        }
        var package = await Export(window, "ExportSearchButton", output, ExportKind.Csv);
        Require(File.ReadAllLines(Path.Combine(package, "messages.csv")).Length == 3, "Only the two anomalous messages should be exported.");
        Require(paths.Select(Hash).SequenceEqual(hashes), "The test session modified an input sample.");
        File.WriteAllText(Path.Combine(output, "flow-result.json"), JsonSerializer.Serialize(new
        {
            Normal = 1, Anomalous = search.Hits.Count, Findings = observations.Select(item => item.Boundary).ToArray(),
            StackedFindings = true, InlineEvidenceVerified = true, ExportVerified = true
        }));
        window.Close();
    }
}

