using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using MailViewer.Core;

namespace MailViewer.App;

public partial class MainWindow
{
    private PstFolder? _selectedPstFolder;
    private string? _selectedPstFile;
    private MailSourceGroup? _selectedSourceGroup;
    private EvidenceSession? _folderCountSession;
    private readonly Dictionary<string, int> _sourceMailCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<(string SourceId, uint NodeId), int> _directMailCounts = [];
    private readonly Dictionary<PstFolder, FolderMailCount> _folderMailCounts = [];

    public static readonly DependencyProperty MailboxCountProperty = DependencyProperty.RegisterAttached(
        "MailboxCount", typeof(string), typeof(MainWindow), new PropertyMetadata(""));
    public static string GetMailboxCount(DependencyObject element) => (string)element.GetValue(MailboxCountProperty);
    public static void SetMailboxCount(DependencyObject element, string value) => element.SetValue(MailboxCountProperty, value);

    private readonly record struct FolderMailCount(int Direct, int Total)
    {
        public bool IncludesChildrenByDefault => Direct == 0 && Total > 0;
        public int Displayed => IncludesChildrenByDefault ? Total : Direct;
    }
    private bool _syncingPstTree;

    private string? SelectedSourceId => (SearchSourceBox.SelectedItem as Choice<string?>)?.Value;

    private void RebuildPstFolderTree()
    {
        if (_syncingPstTree) return;
        var expanded = MailboxNodes().Where(entry => entry.Node.HasItems)
            .ToDictionary(entry => MailboxNodeKey(entry.Node), entry => entry.Node.IsExpanded);
        var sources = _session?.Sources ?? [];
        PrepareMailboxCounts();
        _folderMailCounts.Clear();
        var folders = (_session?.PstFolders ?? []).ToLookup(folder => folder.SourceId);
        _syncingPstTree = true;
        try
        {
            PstFolderTree.Items.Clear();
            _selectedPstFolder = null;
            _selectedPstFile = null;
            _selectedSourceGroup = null;
            PstSubfoldersBox.IsChecked = false;

            if (sources.Count == 0) return;

            var directories = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase);
            TreeViewItem? loose = null;
            foreach (SourceInfo source in sources)
            {
                bool store = source.Format.IsFolderStore();
                string title = store ? $"{StoreFormatName(source.Format)} · {ScopeSourceName(source)}" : ScopeSourceName(source);
                var file = CreateFolderItem(title, source.Path, new PstFileScope(source.Id), expanded: false,
                    icon: source.Format is MailFormat.Eml or MailFormat.Msg ? "Lucide.Mail" : "Lucide.Archive");
                string path = source.SelectedPath ?? source.Path;
                if (source.Format is not (MailFormat.Eml or MailFormat.Msg)) SetCount(file, _sourceMailCounts.GetValueOrDefault(source.Id));
                if (_intakeFolders.TryGetValue(path, out string? directory))
                {
                    string? parent = Path.GetDirectoryName(path);
                    DirectoryBranch(directories, directory, parent ?? directory, source.Id).Items.Add(file);
                }
                else if (source.Format is MailFormat.Eml or MailFormat.Msg && sources.Count > 1)
                {
                    if (loose is null)
                    {
                        loose = CreateNavigationGroup(L("개별 메일", "Message files"), L("개별 메일", "Message files"), looseFiles: true);
                        PstFolderTree.Items.Add(loose);
                    }
                    loose.Items.Add(file);
                    ((MailboxNavigationGroup)loose.DataContext).SourceIds.Add(source.Id);
                }
                else
                {
                    PstFolderTree.Items.Add(file);
                }
                AddStoreFolders(file, source, folders[source.Id].ToArray());
            }
            foreach (TreeViewItem item in directories.Values)
                SetNavigationGroupCount(item);
            if (loose is not null) SetNavigationGroupCount(loose);
            foreach (var (node, depth) in MailboxNodes())
                if (node.HasItems)
                    node.IsExpanded = expanded.GetValueOrDefault(MailboxNodeKey(node), depth == 0);
        }
        finally
        {
            _syncingPstTree = false;
            RefreshPstControls();
        }
    }

    private IEnumerable<(TreeViewItem Node, int Depth)> MailboxNodes()
    {
        var pending = new Stack<(TreeViewItem, int)>(PstFolderTree.Items.OfType<TreeViewItem>().Select(node => (node, 0)));
        while (pending.TryPop(out var entry))
        {
            yield return entry;
            foreach (TreeViewItem child in entry.Item1.Items.OfType<TreeViewItem>())
                pending.Push((child, entry.Item2 + 1));
        }
    }

    private static (string? Source, uint? Folder, string? Directory) MailboxNodeKey(TreeViewItem node) => node.DataContext switch
    {
        PstFolder folder => (folder.SourceId, folder.NodeId, null),
        PstFileScope file => (file.SourceId, null, null),
        MailboxNavigationGroup group => (null, null, group.DirectoryPath?.ToUpperInvariant()),
        _ => throw new InvalidOperationException("Unexpected mailbox node.")
    };

    private TreeViewItem DirectoryBranch(Dictionary<string, TreeViewItem> directories, string root, string directory, string sourceId)
    {
        string relative = Path.GetRelativePath(root, directory);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            relative = ".";
        }
        string path = root;
        if (!directories.TryGetValue(path, out TreeViewItem? node))
        {
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
            node = CreateNavigationGroup(string.IsNullOrEmpty(name) ? root : name, root);
            directories.Add(path, node);
            PstFolderTree.Items.Add(node);
        }
        ((MailboxNavigationGroup)node.DataContext).SourceIds.Add(sourceId);
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            path = Path.Combine(path, segment);
            if (!directories.TryGetValue(path, out TreeViewItem? child))
            {
                child = CreateNavigationGroup(segment, path);
                directories.Add(path, child);
                node.Items.Add(child);
            }
            node = child;
            ((MailboxNavigationGroup)node.DataContext).SourceIds.Add(sourceId);
        }
        return node;
    }

    private static TreeViewItem CreateNavigationGroup(string name, string path, bool looseFiles = false)
    {
        return CreateFolderItem(name, path, new MailboxNavigationGroup(looseFiles ? null : path), expanded: false);
    }

    private void PrepareMailboxCounts()
    {
        if (ReferenceEquals(_folderCountSession, _session)) return;
        _folderCountSession = _session;
        _sourceMailCounts.Clear();
        _directMailCounts.Clear();
        foreach (MailRecord record in _session?.Records ?? [])
        {
            _sourceMailCounts[record.SourceId] = _sourceMailCounts.GetValueOrDefault(record.SourceId) + 1;
            if (record.FolderNodeId is { } folderId)
            {
                var key = (record.SourceId, folderId);
                _directMailCounts[key] = _directMailCounts.GetValueOrDefault(key) + 1;
            }
        }
    }

    private static void SetCount(TreeViewItem item, int count)
    {
        SetMailboxCount(item, $"[{count:N0}]");
        AutomationProperties.SetHelpText(item, L($"읽은 메일 {count:N0}건", $"{count:N0} readable messages"));
    }

    private void SetNavigationGroupCount(TreeViewItem item) =>
        SetCount(item, ((MailboxNavigationGroup)item.DataContext).SourceIds.Sum(id => _sourceMailCounts.GetValueOrDefault(id)));

    private void AddStoreFolders(TreeViewItem target, SourceInfo source, PstFolder[] sourceFolders)
    {
        var ids = sourceFolders.Select(folder => folder.NodeId).ToHashSet();
        var children = sourceFolders.Where(folder => folder.ParentNodeId.HasValue)
            .ToLookup(folder => folder.ParentNodeId!.Value);
        var duplicateNames = sourceFolders.GroupBy(FolderNameKey).Where(group => group.Skip(1).Any())
            .Select(group => group.Key).ToHashSet();
        var roots = sourceFolders.Where(folder => folder.ParentNodeId is not { } parent || !ids.Contains(parent));
        var placed = new HashSet<uint>();
        foreach (PstFolder folder in roots.Concat(sourceFolders))
        {
            if (!placed.Add(folder.NodeId)) continue;
            var branch = MakeFolderNode(folder, expanded: false);
            target.Items.Add(branch);
            var remaining = new Stack<(PstFolder Folder, TreeViewItem Node)>();
            var placedBranch = new Stack<(PstFolder Folder, TreeViewItem Node)>();
            remaining.Push((folder, branch));
            while (remaining.TryPop(out var current))
            {
                placedBranch.Push(current);
                foreach (PstFolder child in children[current.Folder.NodeId])
                {
                    if (!placed.Add(child.NodeId)) continue;
                    var node = MakeFolderNode(child, expanded: false);
                    current.Node.Items.Add(node);
                    remaining.Push((child, node));
                }
            }
            while (placedBranch.TryPop(out var current))
            {
                int direct = _directMailCounts.GetValueOrDefault((source.Id, current.Folder.NodeId));
                int total = direct + current.Node.Items.OfType<TreeViewItem>()
                    .Sum(child => _folderMailCounts[(PstFolder)child.DataContext].Total);
                var count = new FolderMailCount(direct, total);
                _folderMailCounts.Add(current.Folder, count);
                SetCount(current.Node, count.Displayed);
                AutomationProperties.SetHelpText(current.Node, L($"이 폴더 {direct:N0}건, 하위 포함 {total:N0}건",
                    $"This folder: {direct:N0} messages; including descendants: {total:N0}"));
            }
        }

        TreeViewItem MakeFolderNode(PstFolder folder, bool expanded) =>
            FolderNode(folder, duplicateNames.Contains(FolderNameKey(folder)), source.Path, expanded);
    }


    private static (uint? ParentId, string Name) FolderNameKey(PstFolder folder) =>
        (folder.ParentNodeId, folder.Name.ToUpperInvariant());

    private static TreeViewItem FolderNode(PstFolder folder, bool duplicateName, string? sourcePath, bool expanded)
    {
        string name = string.IsNullOrWhiteSpace(folder.Name) ? folder.Path : folder.Name;
        string locator = folder.IsLogicalPath ? folder.Path : $"NID 0x{folder.NodeId:X8}";
        string identity = folder.IsLogicalPath ? folder.Path : $"{folder.Path} · {locator}";
        if (sourcePath is not null) identity += $" · {sourcePath}";
        return CreateFolderItem(duplicateName ? $"{name} · {locator}" : name, identity, folder, expanded);
    }

    private static TreeViewItem CreateFolderItem(string name, string identity, object scope, bool expanded, string icon = "Lucide.Folder")
    {
        var resources = Application.Current;
        var glyph = new ContentControl
        {
            Content = resources.FindResource(icon),
            Template = (ControlTemplate)resources.FindResource("CommandIcon"),
            Focusable = false,
            IsHitTestVisible = false,
            Margin = new Thickness(0, 0, 5, 0)
        };
        var label = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        label.Inlines.Add(new InlineUIContainer(glyph) { BaselineAlignment = BaselineAlignment.Center });
        label.Inlines.Add(new Run(name));
        var item = new TreeViewItem
        {
            Header = label,
            DataContext = scope,
            IsExpanded = expanded,
            ToolTip = identity
        };
        label.Style = SelectableForeground(typeof(TextBlock), TextBlock.ForegroundProperty, "Viewer.Text");
        glyph.Style = SelectableForeground(typeof(ContentControl), Control.ForegroundProperty, "Viewer.Muted");
        AutomationProperties.SetName(item, identity);
        ToolTipService.SetShowsToolTipOnKeyboardFocus(item, true);
        return item;

        Style SelectableForeground(Type type, DependencyProperty property, string normal)
        {
            var style = new Style(type);
            style.Setters.Add(new Setter(property, new DynamicResourceExtension(normal)));
            var selected = new DataTrigger
            {
                Binding = new System.Windows.Data.Binding(nameof(TreeViewItem.IsSelected)) { Source = item },
                Value = true
            };
            selected.Setters.Add(new Setter(property, new DynamicResourceExtension("Viewer.SelectionText")));
            style.Triggers.Add(selected);
            return style;
        }
    }

    private void RefreshMailboxTreeLanguage()
    {
        foreach (TreeViewItem item in PstFolderTree.Items.OfType<TreeViewItem>())
        {
            if (item.Header is not TextBlock label || label.Inlines.LastInline is not Run name)
                continue;
            if (item.DataContext is MailboxNavigationGroup { DirectoryPath: null })
            {
                item.ToolTip = L("개별 메일", "Message files");
                name.Text = (string)item.ToolTip;
            }
            else
                continue;
            AutomationProperties.SetName(item, (string)item.ToolTip);
        }
    }

    private async void OnPstFolderSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_syncingPstTree || !CanChooseSources) return;
        if (e.NewValue is not TreeViewItem { DataContext: { } scope }) return;
        _selectedPstFolder = scope as PstFolder;
        _selectedPstFile = (scope as PstFileScope)?.SourceId;
        _selectedSourceGroup = scope is MailboxNavigationGroup group
            ? new(group.DirectoryPath, Array.AsReadOnly(group.SourceIds.ToArray())) : null;
        _syncingPstTree = true;
        try
        {
            SelectSearchSource(_selectedPstFolder?.SourceId ?? _selectedPstFile);
            PstSubfoldersBox.IsChecked = _selectedPstFolder is { } folder &&
                _folderMailCounts.GetValueOrDefault(folder).IncludesChildrenByDefault;
        }
        finally
        {
            _syncingPstTree = false;
        }
        ShowSelectedPstFolder();
        await ApplyPstScopeAsync();
    }

    private void OnMailboxPaneToggled(object sender, RoutedEventArgs e)
    {
        if (PstFoldersToggle.IsChecked == true)
            ShowSelectedPstFolder();
    }

    private void SelectAllMailboxSources(object sender, RoutedEventArgs e)
    {
        if (AllMailScopeButton.IsChecked == true)
            ClearPstSourceScope(sender, e);
        else
            AllMailScopeButton.IsChecked = true;
    }

    private void ShowSelectedPstFolder()
    {
        if (PstFolderTree.IsVisible && PstFolderTree.SelectedItem is TreeViewItem selected)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!PstFolderTree.IsVisible || !selected.IsSelected) return;
                for (var parent = ItemsControl.ItemsControlFromItemContainer(selected) as TreeViewItem;
                     parent is not null; parent = ItemsControl.ItemsControlFromItemContainer(parent) as TreeViewItem)
                    parent.IsExpanded = true;
                selected.BringIntoView();
            }));
        }
    }

    private async void OnPstSubfoldersChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingPstTree || _selectedPstFolder is null || !CanChooseSources) return;
        if (_filterDialog is null) await ApplyPstScopeAsync(); else UpdateCommands();
    }

    private async void ClearPstFolderScope(object sender, RoutedEventArgs e)
    {
        if (!CanChooseSources || _selectedPstFolder is not { } folder) return;
        RestorePstTreeSelection(new SearchRequest(SourceId: folder.SourceId));
        if (_filterDialog is null) await ApplyPstScopeAsync(); else UpdateCommands();
    }

    private async void ClearPstSourceScope(object sender, RoutedEventArgs e)
    {
        if (!CanChooseSources) return;
        ClearPstTreeSelection();
        SearchSourceBox.SelectedIndex = 0;
        RestorePstTreeSelection(new SearchRequest());
        if (_filterDialog is null) await ApplyPstScopeAsync(); else UpdateCommands();
    }

    private void ClearPstTreeSelection()
    {
        _syncingPstTree = true;
        try
        {
            if (PstFolderTree.SelectedItem is TreeViewItem node) node.IsSelected = false;
            _selectedPstFolder = null;
            _selectedPstFile = null;
            _selectedSourceGroup = null;
            PstSubfoldersBox.IsChecked = false;
        }
        finally
        {
            _syncingPstTree = false;
        }
    }

    private async Task ApplyPstScopeAsync()
    {
        bool hadTreeFocus = PstFolderTree.IsKeyboardFocusWithin;
        var previous = _appliedRequest;
        var scope = CaptureSearch();
        var changed = previous with
        {
            SourceId = scope.SourceId,
            SourceGroup = scope.SourceGroup,
            PstFolderNodeId = scope.PstFolderNodeId,
            IncludePstSubfolders = scope.IncludePstSubfolders
        };
        if (!await SearchAsync(changed))
        {
            RestorePstTreeSelection(previous);
            UpdateCommands();
        }
        IInputElement? focused = Keyboard.FocusedElement;
        if (hadTreeFocus && (focused is null || focused == this || focused == CancelWorkButton) &&
            PstFolderPane.IsVisible && PstFolderTree.SelectedItem is TreeViewItem selected)
        {
            FocusManager.SetFocusedElement(this, selected);
            Keyboard.Focus(selected);
        }
    }

    private void RestorePstTreeSelection(SearchRequest previous)
    {
        ClearPstTreeSelection();
        _syncingPstTree = true;
        try { SelectSearchSource(previous.SourceId); }
        finally { _syncingPstTree = false; }
        if (!previous.PstFolderNodeId.HasValue && previous.SourceId is not null)
            _selectedPstFile = previous.SourceId;
        _selectedSourceGroup = previous.SourceGroup;
        var pending = new Stack<TreeViewItem>(PstFolderTree.Items.OfType<TreeViewItem>());
        while (pending.TryPop(out TreeViewItem? node))
        {
            var folder = node.DataContext as PstFolder;
            var file = node.DataContext as PstFileScope;
            if ((previous.PstFolderNodeId.HasValue && folder is not null &&
                folder.NodeId == previous.PstFolderNodeId && folder.SourceId == previous.SourceId) ||
                (!previous.PstFolderNodeId.HasValue && file is not null && file.SourceId == previous.SourceId) ||
                (previous.SourceGroup is { } group && node.DataContext is MailboxNavigationGroup navigation &&
                    string.Equals(group.DirectoryPath, navigation.DirectoryPath, StringComparison.OrdinalIgnoreCase)))
            {
                _syncingPstTree = true;
                try
                {
                    _selectedPstFolder = folder;
                    _selectedPstFile = file?.SourceId;
                    PstSubfoldersBox.IsChecked = previous.IncludePstSubfolders;
                    node.IsSelected = true;
                }
                finally
                {
                    _syncingPstTree = false;
                }
                break;
            }
            foreach (var child in node.Items.OfType<TreeViewItem>()) pending.Push(child);
        }
    }

    private void RefreshPstControls()
    {
        bool hasTree = PstFolderTree.Items.Count > 0;
        string treeLabel = L("메일함", "Mailboxes");
        bool expanded = PstFoldersToggle.IsChecked == true;
        PstFoldersToggle.Content = expanded ? L("메일함 접기", "Collapse mailboxes") : L("메일함 펼치기", "Expand mailboxes");
        string? selectedFormat = SourceForScope(_selectedPstFolder?.SourceId)?.Format is { } format
            ? StoreFormatName(format) : null;
        PstAllFoldersButton.Content = selectedFormat is null ? L("이 파일의 모든 폴더", "All folders in this file")
            : L($"이 {selectedFormat}의 모든 폴더", $"All folders in this {selectedFormat}");
        PstAllSourcesButton.Content = L("전체 자료", "All sources");
        PstSubfoldersBox.Content = L("하위 폴더 포함", "Include subfolders");
        AutomationProperties.SetName(PstFolderTree, treeLabel);
        PstFoldersToggle.Visibility = hasTree ? Visibility.Visible : Visibility.Collapsed;
        PstFolderPane.Visibility = hasTree ? Visibility.Visible : Visibility.Collapsed;
        PstFolderTree.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        AllMailScopeButton.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        AllMailScopeButton.IsChecked = SelectedSourceId is null && _selectedPstFolder is null &&
            _selectedPstFile is null && _selectedSourceGroup is null;
        SetMailboxCount(AllMailScopeButton, $"[{(_session?.Records.Count ?? 0):N0}]");
        PstFolderPane.IsEnabled = CanChooseSources;
        PstFoldersToggle.IsEnabled = CanChooseSources;
        var draft = CaptureSearch();
        string applied = PstScopeCaption(_appliedRequest);
        string selected = PstScopeCaption(draft);
        FilterFolderScope.Visibility = draft.SourceGroup is null ? Visibility.Collapsed : Visibility.Visible;
        FilterFolderScope.Text = draft.SourceGroup is null ? "" : L($"선택 폴더 · {selected}", $"Selected folder · {selected}");
        bool scopePending = draft.SourceId != _appliedRequest.SourceId || !Equals(draft.SourceGroup, _appliedRequest.SourceGroup) ||
            draft.PstFolderNodeId != _appliedRequest.PstFolderNodeId ||
            draft.IncludePstSubfolders != _appliedRequest.IncludePstSubfolders;
        string label = scopePending
            ? L($"범위 미적용 · 현재 {applied} · 대기 {selected}", $"Scope pending · Applied {applied} · Next {selected}")
            : L($"적용 범위 · {applied}", $"Applied · {applied}");
        PstScopeStatus.Text = label;
        SourceInfo? appliedSource = SourceForScope(_appliedRequest.SourceId);
        PstScopeBar.Visibility = hasTree && (appliedSource is not null || _appliedRequest.SourceGroup is not null || scopePending)
            ? Visibility.Visible : Visibility.Collapsed;
        string appliedFull = PstScopeCaption(_appliedRequest, fullPath: true);
        string selectedFull = PstScopeCaption(draft, fullPath: true);
        string description = L($"적용 범위 · {appliedFull}", $"Applied · {appliedFull}");
        if (scopePending)
            description += L($"\n미적용 · {selectedFull}", $"\nPending · {selectedFull}");
        PstScopeStatus.ToolTip = description;
        AutomationProperties.SetName(PstScopeStatus, description);
        PstAllFoldersButton.Visibility = _selectedPstFolder is not null ? Visibility.Visible : Visibility.Collapsed;
        PstAllFoldersButton.IsEnabled = _selectedPstFolder is not null && CanChooseSources;
        bool scoped = draft.SourceId is not null || draft.SourceGroup is not null;
        PstAllSourcesButton.IsEnabled = (scoped || _appliedRequest.SourceId is not null || _appliedRequest.SourceGroup is not null) && CanChooseSources;
        PstSubfoldersBox.Visibility = _selectedPstFolder is not null ? Visibility.Visible : Visibility.Collapsed;
        PstSubfoldersBox.IsEnabled = _selectedPstFolder is not null && CanChooseSources;
        PstAllSourcesButton.Visibility = scopePending || scoped ? Visibility.Visible : Visibility.Collapsed;
        RefreshMailboxFilterCaption();
        SynchronizeNarrowReader();
    }

    private SourceInfo? SourceForScope(string? sourceId) =>
        sourceId is null ? null : _session?.Sources.FirstOrDefault(source => source.Id == sourceId);

    private bool IsFolderStoreSource(string? sourceId) => SourceForScope(sourceId)?.Format.IsFolderStore() == true;

    private static string StoreFormatName(MailFormat format) => format.ToString().ToUpperInvariant();

    private string ScopeSourceName(SourceInfo source)
    {
        string file = System.IO.Path.GetFileName(source.Path);
        SourceInfo[] sameName = _session!.Sources.Where(item =>
            string.Equals(System.IO.Path.GetFileName(item.Path), file, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (sameName.Length <= 1) return file;

        string? parent = CompactParentName(source);
        if (!string.IsNullOrEmpty(parent) && sameName.Count(item =>
                string.Equals(CompactParentName(item), parent, StringComparison.OrdinalIgnoreCase)) == 1)
            return $"{parent} / {file}";

        int number = Array.FindIndex(sameName, item => item.Id == source.Id) + 1;
        return $"#{number} · {file}";
    }

    private static string? CompactParentName(SourceInfo source) =>
        System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(source.Path));

    private string PstScopeCaption(SearchRequest request, bool fullPath = false)
    {
        if (request.SourceGroup is { } group)
            return group.DirectoryPath is { } directory ? (fullPath ? directory : Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)))
                : L("개별 메일", "Message files");
        SourceInfo? source = SourceForScope(request.SourceId);
        if (source is null || !source.Format.IsFolderStore() && request.PstFolderNodeId is null)
            return source is null ? L("전체 자료", "All sources") : fullPath ? source.Path : ScopeSourceName(source);
        string scope = PstSearchPath(request, fullPath) ?? L("모든 폴더", "All folders");
        if (fullPath && source.Format.IsFolderStore() && request.PstFolderNodeId is { } nid)
            scope += $" · NID 0x{nid:X8}";
        string file = fullPath ? source.Path : ScopeSourceName(source);
        return $"{file} · {scope}";
    }

    private string? PstSearchPath(SearchRequest request, bool fullPath = true)
    {
        if (request.PstFolderNodeId is not { } nid || _session is not { } session) return null;
        PstFolder? selected = session.PstFolders.FirstOrDefault(folder => folder.SourceId == request.SourceId && folder.NodeId == nid);
        string path = selected is null ? $"NID 0x{nid:X8}"
            : fullPath || string.IsNullOrWhiteSpace(selected.Name) ? selected.Path : selected.Name;
        if (!fullPath && selected is not null && session.PstFolders.Count(folder => folder.SourceId == request.SourceId &&
                string.Equals(folder.Name, selected.Name, StringComparison.OrdinalIgnoreCase)) > 1)
            path += selected.IsLogicalPath ? $" · {selected.Path}" : $" · NID 0x{nid:X8}";
        return request.IncludePstSubfolders ? path + L(" · 하위 포함", " · includes subfolders") : path;
    }

    private sealed record PstFileScope(string SourceId);
    private void SelectSearchSource(string? sourceId) =>
        SearchSourceBox.SelectedItem = SearchSourceBox.Items.OfType<Choice<string?>>().FirstOrDefault(option => option.Value == sourceId);

    private sealed record MailboxNavigationGroup(string? DirectoryPath)
    {
        public List<string> SourceIds { get; } = [];
    }
}
