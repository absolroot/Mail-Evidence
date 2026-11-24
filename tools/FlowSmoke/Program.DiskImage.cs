using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using DiscUtils;
using DiscUtils.Ntfs;
using EvidenceImage;
using MailViewer.App;
using MailViewer.Core;

namespace MailViewer.FlowSmoke;

public static partial class Program
{
    private static async Task RunDiskImage(string workspace, string output, string? theme)
    {
        string image = Path.Combine(output, "mail-fixture.raw");
        string eml = Path.Combine(workspace, "tests", "fixtures", "conversation-history", "01-original.eml");
        string pst = Path.Combine(workspace, "tests", "fixtures", "pst", "libyal-outlook.pst");
        const string inbox = @"\Users\Analyst\AppData\Local\Microsoft\Outlook\Inbox.pst";
        const string archive = @"\Archive\사건 메일.eml";
        const long capacity = 32 * 1024 * 1024;
        using (var imageFile = File.Create(image))
        {
            imageFile.SetLength(capacity);
            var bootSector = new byte[512];
            bootSector[0] = 0xEB;
            bootSector[1] = 0x52;
            bootSector[2] = 0x90;
            bootSector[510] = 0x55;
            bootSector[511] = 0xAA;
            using var fs = NtfsFileSystem.Format(imageFile, "QA mail", Geometry.FromCapacity(capacity), 0, capacity / 512, bootSector);
            fs.CreateDirectory(Path.GetDirectoryName(inbox)!);
            fs.CreateDirectory(Path.GetDirectoryName(archive)!);
            WriteEvidence(fs, pst, inbox);
            WriteEvidence(fs, eml, archive);
            fs.SetAttributes(archive, FileAttributes.Hidden | FileAttributes.System);
            WriteEvidence(fs, eml, @"\Archive\ignored.txt");
        }
        string initialHash = Hash(image);
        ImageVolume volume = (await ImageEvidence.ProbeAsync(image)).Volumes.Single();
        using (var source = File.OpenRead(image))
        using (var fs = new NtfsFileSystem(source))
        {
            ImageFileSearchResult result = await ImageEvidence.SearchNtfsFilesAsync(image, volume.Id, [".pst", ".eml"]);
            fs.NtfsOptions.HideHiddenFiles = false;
            fs.NtfsOptions.HideSystemFiles = false;
            string[] paths = fs.GetFiles(@"\", "*", SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path) is ".pst" or ".eml")
                .Select(path => path.StartsWith('\\') ? path : "\\" + path).ToArray();
            Require(result.UnreadEntries == 0 && result.Files.Select(file => file.Path).ToHashSet().SetEquals(paths) &&
                result.Files.All(file => file.FileReference == unchecked((ulong)fs.GetFileId(file.Path))),
                "MFT candidate paths and references must agree with a directory walk");
        }
        try
        {
            _ = await ImageEvidence.ExtractArtifactsAsync(image, volume.Id, output,
                [new ArtifactRequest("wrong", archive, null, "wrong.eml") { ExpectedFileReference = 1 }]);
            throw new InvalidOperationException("A stale NTFS reference was accepted.");
        }
        catch (InvalidDataException error) when (error.Message.Contains("reference", StringComparison.OrdinalIgnoreCase)) { }

        var owner = new MainWindow([], Path.Combine(output, "marks")) { ShowInTaskbar = false };
        owner.Show();
        await Idle();
        ClickMenu(owner, "KoreanLanguageMenuItem");
        if (theme is not null) ClickMenu(owner, theme == "--dark" ? "DarkThemeMenuItem" : "LightThemeMenuItem");

        var complete = new TaskCompletionSource();
        _ = owner.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            ImageSourcesWindow? modal = null;
            try
            {
                await WaitFor(() => (modal = owner.OwnedWindows.OfType<ImageSourcesWindow>().SingleOrDefault(window => window.IsVisible))
                    is not null, "disk image discovery modal");
                var dialog = modal!;
                dialog.Width = dialog.MinWidth;
                dialog.Height = dialog.MinHeight;
                var grid = (DataGrid)dialog.FindName("Candidates");
                var whole = (Button)dialog.FindName("WholeVolumeButton");
                var extract = (Button)dialog.FindName("ExtractButton");
                await WaitFor(() => whole.IsEnabled && grid.Items.Count == 1, "Outlook standard folders candidate");
                Require(PathOf(grid.Items[0]) == inbox, "the extra archive must stay outside the initial scope");
                Capture(dialog, output, "01-image-quick-search.png");
                whole.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, whole));
                await WaitFor(() => grid.Items.Count == 2, "whole volume candidate list");
                var item = grid.Items.Cast<object>().Single(row => PathOf(row) == archive);
                item.GetType().GetProperty("Checked")!.SetValue(item, true);
                Require(extract.IsEnabled, "checked archive is ready to extract");
                Capture(dialog, output, "02-image-whole-volume.png");

                var selection = ((ComboBox)dialog.FindName("Volumes")).SelectedItem;
                var candidate = (ImageFileEntry)item.GetType().GetProperty("File")!.GetValue(item)!;
                string destination = Directory.CreateDirectory(Path.Combine(output, "Extraction")).FullName;
                var task = (Task)typeof(ImageSourcesWindow).GetMethod("Extract", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(dialog, [destination, selection, new[] { candidate }])!;
                await task;
                complete.SetResult();
            }
            catch (Exception error)
            {
                if (modal?.IsVisible == true) modal.Close();
                complete.SetException(error);
            }
        }));
        typeof(MainWindow).GetMethod("OpenDiskImages", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(owner, [new[] { image }]);
        await complete.Task;
        var pending = Field<DataGrid>(owner, "CandidateGrid");
        Require(pending.Items.Count == 1, "the extracted, selected mail alone reaches the intake list");
        string extracted = PathOf(pending.Items[0]);
        Require(Hash(extracted) == Hash(eml) && Hash(image) == initialHash, "the extracted and original bytes must match the fixture");
        string manifest = Path.Combine(Path.GetDirectoryName(extracted)!, "manifest.json");
        using var proof = JsonDocument.Parse(File.ReadAllBytes(manifest));
        Require(proof.RootElement.GetProperty("provenance").GetProperty("streamReferences").EnumerateArray().Single()
            .GetProperty("ntfsPath").GetString() == archive, "the image manifest should identify the actual NTFS path");

        Click(owner, "AnalyzeButton");
        var results = Field<DataGrid>(owner, "ResultsGrid");
        await WaitFor(() => results.Items.Count == 1, "mail body from the selected extracted file");
        var session = (EvidenceSession)typeof(MainWindow).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(owner)!;
        Require(session.Sources.Single().ImageOrigin is { } origin && origin.FilePath == archive &&
            origin.ImagePath == image && origin.ExtractedSha256 == Hash(eml), "analysis must retain image provenance");
        var sourceTab = Field<TabControl>(owner, "DetailTabs");
        sourceTab.SelectedItem = sourceTab.Items.OfType<TabItem>()
            .Single(tab => AutomationProperties.GetAutomationId(tab) == "MailInfoTab");
        await Idle();
        var overview = Field<DataGrid>(owner, "OverviewFields");
        Require(overview.Items.Cast<object>().Any(row => (string?)row.GetType().GetProperty("Value")?.GetValue(row) == archive),
            "mail source details should show the original image path");
        Capture(owner, output, "03-image-source-details.png");
        owner.Close();
        File.WriteAllText(Path.Combine(output, "flow-result.json"), JsonSerializer.Serialize(new
        {
            QuickSearch = 1, WholeVolume = 2, Extracted = 1, Analyzed = 1,
            IdenticalFileReference = true, OriginalImageUnchanged = true, Provenance = true
        }));

        static void WriteEvidence(NtfsFileSystem fs, string original, string path)
        {
            using var from = File.OpenRead(original);
            using var to = fs.OpenFile(path, FileMode.CreateNew, FileAccess.Write);
            from.CopyTo(to);
        }
        static string PathOf(object item) => (string)item.GetType().GetProperty("Path")!.GetValue(item)!;
    }
}
