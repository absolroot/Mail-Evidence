using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MailViewer.Core;

namespace MailViewer.App;

public partial class MainWindow
{
    private readonly DispatcherTimer _searchTyping = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private CancellationTokenSource? _liveSearch;
    private bool _composingSearch;
    private bool _restoringSearchQuery;

    private void InitializeLiveSearch()
    {
        SearchQueryBox.TextChanged += OnSearchQueryChanged;
        TextCompositionManager.AddPreviewTextInputStartHandler(SearchQueryBox, (_, _) =>
        {
            _composingSearch = true;
            StopLiveSearch();
        });
        SearchQueryBox.AddHandler(TextCompositionManager.TextInputEvent,
            new TextCompositionEventHandler((_, _) => FinishSearchComposition()), handledEventsToo: true);
        SearchQueryBox.LostKeyboardFocus += (_, _) => FinishSearchComposition();
        _searchTyping.Tick += SearchTypingElapsed;
    }

    private void RestoreSearchQuery(string value)
    {
        StopLiveSearch();
        _restoringSearchQuery = true;
        try { SearchQueryBox.Text = value; }
        finally { _restoringSearchQuery = false; }
    }

    private void OnSearchQueryChanged(object sender, TextChangedEventArgs e)
    {
        if (!_restoringSearchQuery)
        {
            StopLiveSearch();
            QueueLiveSearch();
        }
        UpdateCommands();
    }

    private void FinishSearchComposition()
    {
        if (!_composingSearch) return;
        _composingSearch = false;
        QueueLiveSearch();
        UpdateCommands();
    }

    private bool CanLiveSearch => _session is not null && CanChooseSources && _filterDialog is null &&
        !_composingSearch && !_restoringSearchQuery && WorkspaceTabs.SelectedItem == ActiveMailboxTab;

    private void QueueLiveSearch()
    {
        if (CanLiveSearch && SearchQueryBox.Text.Trim() != _appliedRequest.Query)
            _searchTyping.Start();
    }

    private void StopLiveSearch()
    {
        _searchTyping.Stop();
        _liveSearch?.Cancel();
        _liveSearch = null;
    }

    private async void SearchTypingElapsed(object? sender, EventArgs e)
    {
        _searchTyping.Stop();
        if (_session is not { } session || !CanLiveSearch) return;
        SearchRequest request = WithCurrentBookmarks(_appliedRequest with { Query = SearchQueryBox.Text.Trim() });
        if (request == _appliedRequest) return;

        var work = new CancellationTokenSource();
        var tab = ActiveMailboxTab;
        _liveSearch = work;
        try
        {
            var result = await Task.Run(() => session.Search(request, work.Token), work.Token);
            if (!ReferenceEquals(_liveSearch, work) || !ReferenceEquals(_session, session) ||
                WorkspaceTabs.SelectedItem != tab || SearchQueryBox.Text.Trim() != request.Query) return;

            _liveSearch = null;
            SetResults(request, result, selectFirst: false);
            WorkStatus.Text = "";
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { }
        catch (Exception error) when (IsExpected(error))
        {
            if (ReferenceEquals(_liveSearch, work))
                WorkStatus.Text = L($"검색하지 못했습니다. {error.Message}", $"Search failed. {error.Message}");
        }
        finally
        {
            if (ReferenceEquals(_liveSearch, work))
            {
                _liveSearch = null;
                UpdateCommands();
            }
            work.Dispose();
        }
    }
}
