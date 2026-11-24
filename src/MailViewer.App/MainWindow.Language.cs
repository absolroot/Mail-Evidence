using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using MailViewer.Core;

namespace MailViewer.App;

public partial class MainWindow
{
    private static bool _english;

    private static readonly Dictionary<string, string> EnglishLabels = new(StringComparer.Ordinal)
    {
        ["파일(_F)"] = "_File",
        ["편집(_E)"] = "_Edit",
        ["보기(_V)"] = "_View",
        ["내보내기(_X)"] = "E_xport",
        ["주 메뉴"] = "Main menu",
        ["표시 언어"] = "Display language",
        ["파일 열기"] = "Open files",
        ["폴더 열기"] = "Open folder",
        ["최근 내보내기 확인"] = "Verify recent exports",
        ["닫기"] = "Close",
        ["복사"] = "Copy",
        ["검색"] = "Search",
        ["밝은 테마"] = "Light theme",
        ["어두운 테마"] = "Dark theme",
        ["작업 취소"] = "Cancel task",
        ["조사"] = "Investigation",
        ["설정"] = "Settings",
        ["화면"] = "Display",
        ["테마"] = "Theme",
        ["분석할 자료"] = "Sources to analyze",
        ["메일 검색"] = "Search mail",
        ["자료 추가…"] = "Add sources…",
        ["메일 자료 열기"] = "Open mail sources",
        ["파일 선택…"] = "Choose files…",
        ["폴더 선택…"] = "Choose folder…",
        ["디스크 이미지…"] = "Disk image…",
        ["이 PC에서 메일 파일 찾기…"] = "Find mail files on this PC…",
        ["메일 파일, 폴더 또는 디스크 이미지를 끌어 놓을 수 있습니다."] =
            "You can drop mail files, a folder, or a disk image here.",
        ["선택한 자료"] = "Selected sources",
        ["목록 비우기"] = "Clear selection",
        ["선택 항목 제거"] = "Remove selected source",
        ["상태"] = "Status",
        ["분석 결과와 해시"] = "Read details and hashes",
        ["검색 항목"] = "Search field",
        ["검색어"] = "Search query",
        ["검색할 자료"] = "Search input",
        ["메일 날짜 (UTC)"] = "Message date (UTC)",
        ["기간 (UTC)"] = "Date range (UTC)",
        ["시작일 UTC"] = "Start date UTC",
        ["종료일 UTC"] = "End date UTC",
        ["첨부 유무"] = "Attachment filter",
        ["초기화"] = "Reset",
        ["이상 징후 유형 검색"] = "Search anomaly types",
        ["일치하는 항목이 없습니다."] = "No matching options.",
        ["적용"] = "Apply",
        ["검색 조건"] = "Search filters",
        ["보낸 사람, 받는 사람 및 제목"] = "Address and subject filters",
        ["주소와 제목"] = "Address / subject",
        ["선택한 메일 내보내기"] = "Export selected message",
        ["날짜"] = "Date",
        ["보낸 사람"] = "From",
        ["받는 사람"] = "To",
        ["제목"] = "Subject",
        ["보낸 사람 조건"] = "Sender filter",
        ["받는 사람 조건"] = "Recipient filter",
        ["제목 조건"] = "Subject filter",
        ["검색 범위"] = "Search coverage",
        ["검색 결과 내보내기"] = "Export results",
        ["검색 결과"] = "Search results",
        ["선택"] = "Check",
        ["이 메일 선택"] = "Select this message",
        ["첨부"] = "Files",
        ["일치 위치"] = "Matched field",
        ["원본 경로"] = "Source path",
        ["검색 결과와 증거 창 높이 조절"] = "Resize results and evidence panes",
        ["자료 출처"] = "Evidence source",
        ["본문"] = "Body",
        ["본문 형식"] = "Body representation",
        ["본문 출처"] = "Body source",
        ["전송 헤더"] = "Transport headers",
        ["원본 위치 열기"] = "Open source bytes",
        ["원본 전송 헤더"] = "Original transport headers",
        ["필드"] = "Field",
        ["값"] = "Value",
        ["원본 범위"] = "Source range",
        ["파일 범위"] = "File range",
        ["스트림 범위"] = "Stream range",
        ["선택한 헤더 값"] = "Selected header value",
        ["첨부파일"] = "Attachments",
        ["첨부파일 저장…"] = "Save attachment…",
        ["첨부 목록"] = "Attachment list",
        ["파일명"] = "File name",
        ["유형"] = "Type",
        ["크기 (바이트)"] = "Size (bytes)",
        ["MIME 파트"] = "MIME part",
        ["첨부 출처와 해시"] = "Attachment source and hash",
        ["원본 바이트"] = "Source bytes",
        ["메시지 범위"] = "Message range",
        ["MSG 파일 바이트"] = "MSG file bytes",
        ["첨부 스트림"] = "Attachment stream",
        ["근거로 돌아가기"] = "Back to evidence",
        ["CFB 스트림 및 MAPI 태그"] = "CFB stream and MAPI tag",
        ["이전 구간"] = "Previous range",
        ["다음 구간"] = "Next range",
        ["원본 Hex"] = "Source hex",
        ["분석 시작"] = "Analyze",
        ["파일 열기…"] = "Open files…",
        ["폴더 열기…"] = "Open folder…",
        ["후보 비우기"] = "Clear candidates",
        ["분석할 파일 목록"] = "Input candidates",
        ["포함"] = "Include",
        ["선택 방식"] = "Added by",
        ["경로"] = "Path",
        ["확정된 자료"] = "Loaded sources",
        ["자료 경로, 분석 결과 및 SHA-256"] = "Source paths, read details and SHA-256",
        ["내보내기"] = "Export",
        ["형식"] = "Format",
        ["내보내기 형식"] = "Output format",
        ["저장 폴더"] = "Destination",
        ["저장할 기존 폴더"] = "Existing destination folder",
        ["찾아보기…"] = "Browse…",
        ["내보내기 대상 및 검증 결과"] = "Export scope and verification results",
        ["저장"] = "Save",
        ["결과 다시 검증"] = "Verify saved files",
        ["검색 결과로 돌아가기"] = "Return to search",
        ["분석할 메일 파일을 선택하세요."] = "Choose mail files to analyze."
    };

    private static Dictionary<string, string> KoreanLabels { get; } =
        EnglishLabels.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    private static string L(string korean, string english) => _english ? english : korean;

    private void SwitchLanguage(object sender, RoutedEventArgs e)
    {
        if (!CanChooseSources || sender is not MenuItem { Tag: string mode }) return;
        SetDisplayLanguage(mode == "en");
    }

    private void SetDisplayLanguage(bool english)
    {
        if (_english == english) return;

        var field = (SearchFieldBox.SelectedItem as Choice<SearchField>)?.Value ?? SearchField.Any;
        int sourceIndex = SearchSourceBox.SelectedIndex;
        int attachment = SearchAttachmentBox.SelectedIndex;
        _english = english;
        JournalDesk.L.ApplyMailLanguage(english);
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(english ? "en-US" : "ko-KR");
        SetStaticLabels(this);
        Language = XmlLanguage.GetLanguage(english ? "en-US" : "ko-KR");
        PopulateFilters();
        SearchFieldBox.SelectedIndex = (int)field;
        SearchSourceBox.SelectedIndex = sourceIndex;
        SearchAttachmentBox.SelectedIndex = attachment;
        foreach (ResultRow row in ResultRows()) row.RefreshLanguage();
        foreach (InputCandidate candidate in _candidates) candidate.RefreshLanguage();
        RefreshEvidenceText(_activeRow);
        RefreshAttachmentPreviewText();
        RefreshReaderLanguage();
        RefreshMailboxTreeLanguage();
        if ((_rawSourceRecord ?? _activeRow?.Record) is { } record && RawBytesBox.ByteCount > 0)
        {
            _rawName = TranslateStatic(_rawName);
            RawBytesBox.AddressLabel = RawAddressLabel(record);
            RawBytesBox.SelectRange(RawBytesBox.SelectionAnchor, RawBytesBox.SelectedOffset);
            RefreshRawLabels(record, new ByteRange(RawBytesBox.BaseOffset, RawBytesBox.ByteCount));
        }
        else if (_activeRow is { Record: var selected } && _rawRange.Length == 0)
            ShowRawEmpty(selected.Format);
        RefreshSourceReport();
        RefreshDisplaySettings();
        WorkStatus.Text = "";
        UpdateCommands();
    }

    private static string TranslateStatic(string label) =>
        (_english ? EnglishLabels : KoreanLabels).GetValueOrDefault(label) ?? label;

    private static void SetStaticLabels(DependencyObject target)
    {
        if (target is TreeViewItem) return;
        switch (target)
        {
            case Window window:
                window.Title = TranslateStatic(window.Title);
                break;
            case DataGrid table:
                foreach (DataGridColumn column in table.Columns)
                {
                    if (column.Header is string label) column.Header = TranslateStatic(label);
                }
                break;
            case MenuItem item when item.Header is string label:
                item.Header = TranslateStatic(label);
                break;
            case HeaderedContentControl control when control.Header is string label:
                control.Header = TranslateStatic(label);
                break;
            case ComboBoxItem item when item.Content is string label:
                item.Content = TranslateStatic(label);
                break;
            case Button button when button.Content is string label:
                button.Content = TranslateStatic(label);
                break;
            case TextBlock block:
                block.Text = TranslateStatic(block.Text);
                break;
        }
        string name = AutomationProperties.GetName(target);
        if (!string.IsNullOrEmpty(name)) AutomationProperties.SetName(target, TranslateStatic(name));
        foreach (object element in LogicalTreeHelper.GetChildren(target))
        {
            if (element is DependencyObject child) SetStaticLabels(child);
        }
    }
}
