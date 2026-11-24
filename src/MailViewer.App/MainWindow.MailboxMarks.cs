using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MailViewer.Core;

namespace MailViewer.App;

public partial class MainWindow
{
    private readonly MailboxMarks _mailMarks;
    private IReadOnlyList<string> _bookmarkScope = [];
    private EvidenceSession? _bookmarkCountSession;
    private int _loadedBookmarkCount;

    private void RefreshBookmarkScope()
    {
        _bookmarkCountSession = _session;
        _bookmarkScope = _session is null || _mailMarks.Bookmarks.Count == 0 ? []
            : Array.AsReadOnly(_session.Records.Where(record => _mailMarks.IsBookmarked(record.Id))
                .Select(record => record.Id).ToArray());
        _loadedBookmarkCount = _bookmarkScope.Count;
    }

    private SearchRequest WithCurrentBookmarks(SearchRequest request) =>
        request.BookmarkRecordIds is null ? request : request with { BookmarkRecordIds = _bookmarkScope };

    private SearchRequest ExpandBookmarksOnIntake(SearchRequest request, EvidenceSession additional)
    {
        if (request.BookmarkRecordIds is not { } bookmarked) return request;
        var added = additional.Records.Where(record => _mailMarks.IsBookmarked(record.Id)).Select(record => record.Id);
        return request with { BookmarkRecordIds = Array.AsReadOnly(bookmarked.Concat(added).Distinct(StringComparer.Ordinal).ToArray()) };
    }

    private async void ToggleRowBookmark(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ResultRow row } || !CanChooseSources) return;
        if (!TrySetRowMark(row, pinned: false, enabled: !row.Bookmarked)) return;
        RefreshBookmarkScope();
        if (ActiveMailboxTab == BookmarksMailboxTab)
        {
            if (await SearchAsync(_appliedRequest) && ResultsGrid.Items.Count > 0) ResultsGrid.Focus();
        }
        else
            RefreshMailboxTabs();
    }

    private async void ToggleCheckedBookmarks(object sender, RoutedEventArgs e)
    {
        if (!CanChooseSources || !BookmarkCheckedButton.IsEnabled || SelectionModeButton.IsChecked != true) return;
        ResultRow[] rows = ResultRows().Where(row => row.Checked).ToArray();
        if (rows.Length == 0) return;
        bool add = rows.Any(row => !row.Bookmarked);
        string[] targets = rows.Where(row => row.Bookmarked != add).Select(row => row.Record.Id).ToArray();

        var work = StartWork(add ? L("북마크를 추가하는 중입니다.", "Adding bookmarks.")
            : L("북마크를 해제하는 중입니다.", "Removing bookmarks."));
        HashSet<string> saved = [];
        int failed = 0;
        bool canceled;
        try
        {
            await Task.Run(() =>
            {
                foreach (string id in targets)
                {
                    if (work.IsCancellationRequested) break;
                    try
                    {
                        _mailMarks.WriteMark(id, pinned: false, enabled: add);
                        saved.Add(id);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        failed++;
                    }
                }
            });
            foreach (ResultRow row in rows)
            {
                if (!saved.Contains(row.Record.Id)) continue;
                _mailMarks.SetInMemory(row.Record.Id, pinned: false, enabled: add);
                row.SetMarks(add, _mailMarks.IsPinned(row.Record.Id));
            }
            RefreshBookmarkScope();
            canceled = work.IsCancellationRequested;
        }
        finally
        {
            EndWork(work);
        }

        if (ActiveMailboxTab == BookmarksMailboxTab && saved.Count > 0)
        {
            await SearchAsync(_appliedRequest);
            if (ResultsGrid.Items.Count > 0) ResultsGrid.Focus();
            else SelectionModeButton.Focus();
        }
        string status = add ? L($"메일 {saved.Count:N0}통을 북마크했습니다.", $"Bookmarked {saved.Count:N0} messages.")
            : L($"메일 {saved.Count:N0}통의 북마크를 해제했습니다.", $"Removed bookmarks from {saved.Count:N0} messages.");
        if (failed > 0) status += L($" {failed:N0}통은 저장하지 못했습니다.", $" {failed:N0} could not be saved.");
        if (canceled) status += L(" 작업을 취소했습니다.", " The operation was canceled.");
        WorkStatus.Text = status;
        InteractionFeedback.Acknowledge(WorkStatus);
    }

    private void ToggleRowPin(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ResultRow row } || !CanChooseSources) return;
        if (!TrySetRowMark(row, pinned: true, enabled: !row.Pinned)) return;
        var selected = ResultsGrid.SelectedItem;
        ApplyMailboxSort();
        if (selected is not null && ResultsGrid.Items.Contains(selected))
        {
            ResultsGrid.SelectedItem = selected;
            ResultsGrid.ScrollIntoView(selected);
        }
        if (ReferenceEquals(selected, row) && !ReferenceEquals(sender, ReaderPinButton))
        {
            ResultsGrid.Focus();
            if (ResultsGrid.ItemContainerGenerator.ContainerFromItem(row) is DataGridRow container) container.Focus();
        }
    }

    private bool TrySetRowMark(ResultRow row, bool pinned, bool enabled)
    {
        try
        {
            _mailMarks.Set(row.Record.Id, pinned, enabled);
            row.SetMarks(_mailMarks.IsBookmarked(row.Record.Id), _mailMarks.IsPinned(row.Record.Id));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            WorkStatus.Text = L("북마크 또는 핀 상태를 저장하지 못했습니다.", "Could not save the bookmark or pin.");
            return false;
        }
    }

    private static string MailboxDateSection(DateTimeOffset? date)
    {
        if (date is not { } value) return L("날짜 미확인", "Unknown date");
        DateTime day = value.LocalDateTime.Date;
        DateTime today = DateTime.Today;
        if (day > today) return L("미래 날짜", "Future date");
        if (day == today) return L("오늘", "Today");
        if (day == today.AddDays(-1)) return L("어제", "Yesterday");
        if (day.Year == today.Year && day.Month == today.Month) return L("이번 달", "This month");
        if (day.Year == today.Year) return L($"{day.Month}월", day.ToString("MMMM", CultureInfo.InvariantCulture));
        return L($"{day.Year}년 {day.Month}월", day.ToString("MMMM yyyy", CultureInfo.InvariantCulture));
    }
}

