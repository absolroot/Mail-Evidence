using JournalDesk;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using MailViewer.Core;

namespace MailViewer.App;

public partial class MainWindow
{
    private const int RawPageBytes = 4096;
    private ByteRange _rawRange;
    private long _rawPage;
    private string _rawName = "";
    private HexField? _rawField;
    private CompoundStreamLocator? _rawStream;
    private ContainerEvidence? _rawContainer;
    private PstPropertyLocator? _rawPstProperty;
    private TabItem? _rawReturnTab;
    private FrameworkElement? _rawReturnFocus;
    private bool _openingEvidenceBytes;
    private MailRecord? _rawSourceRecord;
    private EvidenceField[] _rawLocationFields = [];
    private string? _bodyRecordId;

    private void OnRecordSelected(object sender, SelectionChangedEventArgs e) =>
        ShowRecord(ResultsGrid.SelectedItem as ResultRow);

    private void ShowRecord(ResultRow? row)
    {
        _activeRow = row;
        ReaderHeader.DataContext = row;
        UpdateReaderHeader();
        RawBytesBox.ClearView();
        RawStatus.Text = "";
        ClearRawLocation();
        _rawRange = default;
        _rawField = null;
        _rawPage = 0;
        _rawStream = null;
        _rawContainer = null;
        _rawPstProperty = null;
        _rawReturnTab = null;
        _rawReturnFocus = null;
        _rawSourceRecord = null;
        _rawDecodedBytes = null;
        _rawDecodedAttachment = null;
        _rawAttachmentBoundary = null;
        _rawDecodedPeHeader = null;
        RawSignatureNote.Visibility = Visibility.Collapsed;
        MailRecord? record = row?.Record;
        _selectedSourceHeader = null;
        if (_headerSourceBack is { } back) back.Visibility = Visibility.Collapsed;
        AttachmentsGrid.ItemsSource = record?.Attachments;
        RefreshEvidenceText(row);
        if (record is { Attachments.Count: 0 } && DetailTabs.SelectedItem == AttachmentsTab)
            DetailTabs.SelectedItem = BodyTab;
        if (record is not null)
        {
            if (record.Attachments.Count > 0)
            {
                string? matchingPart = row?.SearchMatches.FirstOrDefault(match => match.Field == SearchField.AttachmentName)?.PartPath;
                AttachmentsGrid.SelectedItem = record.Attachments.FirstOrDefault(attachment => attachment.PartPath == matchingPart)
                    ?? record.Attachments[0];
            }
            if (record.CanReadMessage && DetailTabs.SelectedItem == RawTab &&
                (record.ContainerMessage is not null || record.Format is MailFormat.Eml or MailFormat.Mbox))
                OpenMessageBytes(this, new RoutedEventArgs());
            else
                ShowRawEmpty(record.Format);
        }
        UpdateCommands();
        if (_headerSourceBox is not null && record is not null && DetailTabs.SelectedItem == HeadersTab)
            LoadHeaderSource(record);
    }

    private void RefreshEvidenceText(ResultRow? row)
    {
        RefreshMailInfo();
        RefreshInvestigation();
        var attachments = row?.Record.Attachments ?? [];
        int attachmentCount = attachments.Count;
        RefreshReaderAttachments(attachments);
        AttachmentsTab.Header = L($"첨부파일 ({attachmentCount:N0})", $"Attachments ({attachmentCount:N0})");
        AttachmentsGrid.Visibility = AttachmentToolbar.Visibility = attachmentCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        AttachmentEmpty.Visibility = attachmentCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        AttachmentEmpty.Text = row is null ? L("메일을 선택하세요.", "Select a message.")
            : L("첨부파일이 없습니다.", "No attachments.");
        if (row is null)
        {
            if (_sourceFieldsGrid is not null) _sourceFieldsGrid.ItemsSource = null;
            RefreshBodyRepresentations(null);
            RefreshAttachmentText();
            return;
        }
        MailRecord record = row.Record;
        bool msg = record.Format == MailFormat.Msg;
        bool folderStore = record.Format.IsFolderStore();
        SourceInfo? source = _session?.Sources.FirstOrDefault(value => value.Id == record.SourceId);
        List<EvidenceField> fields =
        [
            new(source?.ImageOrigin is null ? L("원본 경로", "Source path") : L("추출 파일 경로", "Extracted file path"), record.SourcePath),
            new(source?.ImageOrigin is null ? L("원본 SHA-256", "Source SHA-256") : L("추출 파일 SHA-256", "Extracted file SHA-256"),
                source?.Sha256 ?? "", IsDigest: true)
        ];
        if (source?.ImageOrigin is { } image)
        {
            fields.Add(new(L("원본 디스크 이미지", "Source disk image"), image.ImagePath));
            fields.Add(new(L("볼륨 시작 위치", "Volume offset"), $"0x{image.VolumeOffset:X} {L("바이트", "bytes")}"));
            fields.Add(new(L("이미지 내부 경로", "Path in image"), image.FilePath));
            fields.Add(new(L("NTFS 파일 참조", "NTFS file reference"), $"0x{image.FileReference:X16}"));
            fields.Add(new(L("추출 매니페스트", "Extraction manifest"), image.ManifestPath));
        }
        if (record.PstMessage is { } pstMessage)
        {
            string format = StoreFormatName(record.Format);
            if (source?.StoreLayout is { } layout) format += $" · {DescribeStoreLayout(layout)}";
            fields.Add(new(L("자료 형식", "Source format"), format));
            fields.Add(new(L("폴더 경로", "Folder path"), pstMessage.FolderPath));
            fields.Add(new(L("폴더 NID", "Folder NID"), pstMessage.FolderNid));
            fields.Add(new(L("메시지 NID", "Message NID"), pstMessage.MessageNid));
        }
        else if (record.ContainerMessage is { } messageLocation)
            fields.Add(new(L("메시지 원본 위치", "Message source location"), messageLocation.Description));
        else if (!folderStore)
            fields.Add(new(msg ? L("MSG 파일 범위", "MSG file range") : L("메시지 범위", "Message range"), record.MessageRange.ToString()));
        if (record.EnvelopeRange is { } envelope) fields.Add(new(L("MBOX 봉투 줄", "MBOX envelope"), envelope.ToString()));
        if (record.SeparatorRange is { } separator) fields.Add(new(L("MBOX 구분 바이트", "MBOX separator bytes"), separator.ToString()));
        if (!string.IsNullOrEmpty(row.Matches)) fields.Add(new(L("검색 일치", "Search matches"), row.Matches));
        if (_sourceFieldsGrid is not null) _sourceFieldsGrid.ItemsSource = fields;
        RefreshBodyRepresentations(row);
        AttachmentSizeColumn.Header = L("크기 (B)", "Size (B)");
        RawMessageButton.Content = folderStore ? L("전체 속성", "Whole property")
            : msg ? L("MSG 파일 전체", "Whole MSG file") : L("메시지 전체", "Whole message");
        RefreshAttachmentText();
    }

    private bool _updatingBodyOptions;

    private void RefreshBodyRepresentations(ResultRow? row)
    {
        string? selectedPart = row is not null && row.Record.Id == _bodyRecordId
            ? (BodyRepresentationBox.SelectedItem as Choice<MailBodyPart>)?.Value.PartPath : null;
        _bodyRecordId = row?.Record.Id;
        IReadOnlyList<MailBodyPart> parts = row?.Record.BodyParts ?? [];
        Choice<MailBodyPart>[] options = parts.Select(part => new Choice<MailBodyPart>(part,
            parts.Count(other => other.MediaType == part.MediaType) > 1 ? $"{BodyFormatName(part)} · {part.PartPath}"
                : BodyFormatName(part))).ToArray();
        _updatingBodyOptions = true;
        BodyRepresentationBox.ItemsSource = options;
        bool useSegments = options.Length == 2 && parts.Any(part => part.MediaType == "text/plain") &&
            parts.Any(part => part.MediaType == "text/html");
        BodyFormatSwitch.Visibility = useSegments ? Visibility.Visible : Visibility.Collapsed;
        BodyRepresentationBox.Visibility = options.Length > 1 && !useSegments ? Visibility.Visible : Visibility.Collapsed;
        BodyPlainButton.Content = L("평문", "Plain text");
        BodyHtmlButton.ToolTip = L("서식을 적용한 HTML 본문", "Formatted HTML body");
        string? matchedPart = row?.SearchMatches.FirstOrDefault(match => match.Field == SearchField.Body)?.PartPath;
        BodyRepresentationBox.SelectedItem = options.FirstOrDefault(choice => choice.Value.PartPath == selectedPart)
            ?? options.FirstOrDefault(choice => choice.Value.MediaType == "text/html")
            ?? options.FirstOrDefault(choice => choice.Value.PartPath == matchedPart)
            ?? options.FirstOrDefault(choice => choice.Value.MediaType == "text/plain")
            ?? options.FirstOrDefault();
        _updatingBodyOptions = false;
        RefreshBodyText();
    }

    private void SelectBodyFormat(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string mediaType } &&
            BodyRepresentationBox.Items.OfType<Choice<MailBodyPart>>().FirstOrDefault(choice => choice.Value.MediaType == mediaType) is { } option)
            BodyRepresentationBox.SelectedItem = option;
    }

    private void OnBodyRepresentationChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_updatingBodyOptions) RefreshBodyText();
    }

    private void RefreshBodyText()
    {
        var choice = BodyRepresentationBox.SelectedItem as Choice<MailBodyPart>;
        BodyPlainButton.IsChecked = choice?.Value.MediaType == "text/plain";
        BodyHtmlButton.IsChecked = choice?.Value.MediaType == "text/html";
        if (choice is null)
        {
            BodyOriginBox.Text = _activeRow is null ? "" : L("판독한 본문이 없습니다.", "No readable body.");
            BodyBox.Clear();
            UpdateHtmlBody();
            return;
        }
        MailBodyPart part = choice.Value;
        string source = _activeRow?.Record.Format switch
        {
            MailFormat.Msg => $"{L("CFB 스트림", "CFB stream")}  {part.PartPath}",
            MailFormat.Pst or MailFormat.Ost => $"{StoreFormatName(_activeRow.Record.Format)} {L("본문", "body")}  {part.PartPath}",
            _ => $"MIME {part.PartPath}"
        };
        BodyOriginBox.Text = BodyRepresentationBox.Items.Count == 1 ? $"{BodyFormatName(part)} · {source}" : source;
        BodyBox.Text = part.Text;
        UpdateHtmlBody();
    }

    private static string BodyFormatName(MailBodyPart part) => part.MediaType switch
    {
        "text/plain" => L("평문", "Plain text"),
        "text/html" => "HTML",
        "text/rtf" or "application/rtf" => L("RTF 텍스트", "RTF text"),
        _ => part.MediaType
    };

    private static string HeaderStateText(MailRecord record) => record.TransportHeaderState switch
    {
        HeaderEvidenceState.Present => L($"원본 필드 {record.Headers.Count:N0}개", $"{record.Headers.Count:N0} original fields"),
        HeaderEvidenceState.Missing => L("전송 헤더 속성이 없습니다.", "Transport header property is absent."),
        HeaderEvidenceState.Empty => L("전송 헤더 속성이 비어 있습니다.", "Transport header property is empty."),
        _ => L("전송 헤더를 판독하지 못했습니다.", "The transport header could not be read.")
    };

    private void OnAttachmentSelected(object sender, SelectionChangedEventArgs e)
    {
        AttachmentLocationExpander.IsExpanded = false;
        RefreshAttachmentText();
        RefreshAttachmentPreview();
        UpdateCommands();
    }

    private void RefreshAttachmentText()
    {
        var fields = new List<EvidenceField>();
        var location = new List<EvidenceField>();
        if (AttachmentsGrid.SelectedItem is MailAttachment attachment && _activeRow?.Record is { } record)
        {
            fields.Add(new(L("파일명", "File name"), attachment.Name));
            fields.Add(new(L("MIME 유형", "MIME type"), attachment.MediaType));
            if (attachment.DecodedLength is { } length)
                fields.Add(new(L("크기", "Size"), $"{length:N0} {L("바이트", "bytes")}"));
            if (attachment.Sha256 is { } hash)
            {
                string label = attachment.PstProperty is not null ? L("속성 SHA-256", "Property SHA-256")
                    : attachment.CompoundStream is not null ? L("스트림 SHA-256", "Stream SHA-256")
                    : L("디코딩 SHA-256", "Decoded SHA-256");
                fields.Add(new(label, hash, IsDigest: true));
            }
            if (attachment.PstProperty is { } property)
                location.AddRange(PropertyLocationFields(property));
            else if (attachment.CompoundStream is { } stream)
                location.AddRange(StreamLocationFields(stream));
            else if (attachment.CompoundStoragePath is { } storage)
                location.Add(new(L("저장소 경로", "Storage path"), "/" + string.Join("/", storage)));
            else if (attachment.ContainerLocation is { } container)
                location.Add(new(L("첨부 원본 위치", "Attachment source location"), container.Description));
            else if (record.Format is MailFormat.Eml or MailFormat.Mbox)
            {
                location.Add(new(L("MIME 파트", "MIME part"), attachment.PartPath));
                if (!string.IsNullOrWhiteSpace(attachment.TransferEncoding))
                    location.Add(new(L("전송 인코딩", "Transfer encoding"), attachment.TransferEncoding));
                location.Add(new(L("인코딩 원본 범위", "Encoded source range"), attachment.EncodedRange is { } encoded
                    ? FormatVisibleRange(encoded) : L("확인되지 않음", "Unavailable")));
            }
            if (location.Count > 0)
                location.Add(new(L("원본 경로", "Source path"), record.SourcePath));
            if (attachment.Analysis?.Boundary is { } boundary)
                fields.Add(new(L("파일 구조", "File structure"), DescribeBoundary(boundary)));
            else if (attachment.Analysis?.BoundaryLimited == true)
                fields.Add(new(L("파일 구조", "File structure"), L("일부 구조를 확인할 수 없습니다.", "Some structure could not be assessed.")));
            if (attachment.PstObject is { } pstObject)
                fields.Add(new(L("첨부 유형", "Attachment type"), pstObject == PstObjectAttachment.EmbeddedMessage
                    ? L("내장 메시지 · 내용 열기 및 내보내기 미지원", "Embedded message · opening and export unavailable")
                    : L("응용 프로그램 개체 · 내용 열기 및 내보내기 미지원", "Application object · opening and export unavailable")));
            else if (attachment.Issue is { } issue)
                fields.Add(new(L("판독 상태", "Read status"), issue));
            else if (attachment.Sha256 is null && attachment.CompoundStoragePath is null)
                fields.Add(new("SHA-256", L("확인되지 않음", "Unavailable")));
        }

        string hexDescription = AttachmentsGrid.SelectedItem switch
        {
            MailAttachment { PstProperty: not null } =>
                L("첨부파일 MAPI 속성을 HEX로 보기", "View attachment MAPI property as hex"),
            MailAttachment { CompoundStream: not null } =>
                L("첨부파일 MSG 스트림을 HEX로 보기", "View attachment MSG stream as hex"),
            _ => L("저장된 MIME 바이트를 HEX로 보기", "View stored MIME bytes as hex")
        };
        OpenAttachmentBytesButton.ToolTip = hexDescription;
        AutomationProperties.SetName(OpenAttachmentBytesButton, hexDescription);
        AttachmentDetailFields.ItemsSource = fields;
        AttachmentLocationFields.ItemsSource = location;
        AttachmentLocationExpander.Header = L("원본 위치", "Source location");
        AttachmentLocationExpander.Visibility = location.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AttachmentDetails.Visibility = fields.Count + location.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FormatStreamLocation(CompoundStreamLocator stream) =>
        $"{stream.DisplayPath} · MAPI {stream.TagHex} · {stream.Length:N0} {L("바이트", "bytes")}";

    private static string FormatPstLocation(PstPropertyLocator property)
    {
        string attachment = property.AttachmentNid is { } nid ? $" · {L("첨부 NID", "Attachment NID")} {nid}"
            : property.AttachmentIndex is { } index ? $" · {L("첨부 인덱스", "Attachment index")} {index}" : "";
        return $"{L("폴더 NID", "Folder NID")} {property.Message.FolderNid}"
            + $" · {L("메시지 NID", "Message NID")} {property.Message.MessageNid}{attachment}"
            + $" · MAPI {property.TagHex} · {property.Length:N0} {L("바이트", "bytes")} · {property.Message.FolderPath}";
    }

    private void OnResultsKeyDown(object sender, KeyEventArgs e)
    {
        if (_activeRow is not { } row) return;
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None)
        {
            SelectionModeButton.IsChecked = true;
            row.Checked = !row.Checked;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && CanChooseSources)
        {
            DetailTabs.SelectedItem = HeadersTab;
            _headerSourceBox?.Focus();
            e.Handled = true;
        }
    }

    private void OnAttachmentKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && OpenAttachmentBytesButton.IsEnabled)
        {
            OpenAttachmentBytes(sender, e);
            e.Handled = true;
        }
    }

    private void OpenHeaderBytes()
    {
        if (_activeRow is not { } row || _headerSourceBytes is not { IsEnabled: true }) return;
        MailHeader? header = _selectedSourceHeader;
        CompoundStreamLocator? stream = row.Record.TransportHeaderStream;
        PstPropertyLocator? property = row.Record.TransportHeaderProperty;
        ByteRange range = header?.Range ?? (property is not null ? new ByteRange(0, property.Length)
            : stream is not null ? new ByteRange(0, stream.Length) : row.Record.HeaderRange);
        HexField? field = header is not null && range.Length is > 0 and <= RawPageBytes
            ? new HexField(0, (int)range.Length, header.Name, header.Value)
            : null;
        OpenEvidenceBytes(HeadersTab, range, header?.Name ?? L("전송 헤더", "Transport headers"), stream, property, field,
            container: row.Record.ContainerHeaders is { FileRange: null } container ? container : null);
    }

    private void OpenAttachmentBytes(object sender, RoutedEventArgs e)
    {
        if (AttachmentsGrid.SelectedItem is not MailAttachment attachment || !OpenAttachmentBytesButton.IsEnabled) return;
        if (attachment.PstProperty is { } property)
        {
            OpenEvidenceBytes(AttachmentsTab, new ByteRange(0, property.Length),
                L("첨부 속성", "Attachment property"), null, property);
        }
        else if (attachment.CompoundStream is { } stream)
        {
            OpenEvidenceBytes(AttachmentsTab, new ByteRange(0, stream.Length), L("첨부 스트림", "Attachment stream"), stream);
        }
        else if (attachment.ContainerLocation is { FileRange: null, Length: > 0, CanRead: true } container)
            OpenEvidenceBytes(AttachmentsTab, new ByteRange(0, container.Length), L("첨부 원본 바이트", "Stored attachment bytes"),
                null, container: container);
        else if (attachment.EncodedRange is { } range)
            OpenEvidenceBytes(AttachmentsTab, range, $"{L("인코딩 원본", "Encoded source")} · MIME {attachment.PartPath}", null);
    }

    private void OpenEvidenceBytes(TabItem origin, ByteRange range, string label, CompoundStreamLocator? stream,
        PstPropertyLocator? property = null, HexField? field = null, MailRecord? sourceRecord = null,
        FrameworkElement? returnFocus = null, ContainerEvidence? container = null)
    {
        _rawReturnTab = origin;
        _rawReturnFocus = returnFocus;
        _rawSourceRecord = sourceRecord;
        SetRawRange(range, label, stream, property, field, container: container);
        _openingEvidenceBytes = true;
        try { DetailTabs.SelectedItem = RawTab; }
        finally { _openingEvidenceBytes = false; }
        RawBytesBox.FocusHexColumn();
    }

    private void ReturnToEvidence(object sender, RoutedEventArgs e)
    {
        if (_rawReturnTab is not { } origin) return;
        DetailTabs.SelectedItem = origin;
        if (_rawReturnFocus is { IsVisible: true, IsEnabled: true } focus && focus.Focus()) return;
        if (origin == _mailInfoTab)
        {
            FocusMailField();
            return;
        }
        if (origin == InvestigationCluesTab)
        {
            var row = InvestigationGrid.IsVisible
                ? InvestigationGrid.ItemContainerGenerator.ContainerFromItem(InvestigationGrid.SelectedItem) as DataGridRow
                : null;
            if (row?.Focus() != true)
            {
                if (InvestigationFindings.IsVisible) InvestigationFindings.Focus();
                else if (OpenFindingSourceButton.IsVisible) OpenFindingSourceButton.Focus();
                else InvestigationCluesTab.Focus();
            }
            return;
        }
        if (origin == HeadersTab)
        {
            _headerSourceBox?.Focus();
            return;
        }
        FocusAttachmentSelection();
    }

    private void FocusAttachmentSelection()
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (DetailTabs.SelectedItem != AttachmentsTab || !AttachmentSurface.IsVisible) return;
            if (AttachmentPicker.IsVisible)
            {
                FocusManager.SetFocusedElement(this, AttachmentPicker);
                Keyboard.Focus(AttachmentPicker);
                return;
            }
            RevealSelectedAttachmentRow();
            IInputElement target = AttachmentsGrid.ItemContainerGenerator.ContainerFromItem(AttachmentsGrid.SelectedItem) as DataGridRow
                ?? (IInputElement)AttachmentsGrid;
            FocusManager.SetFocusedElement(this, target);
            Keyboard.Focus(target);
        }));
    }

    private void RevealSelectedAttachmentRow()
    {
        if (AttachmentsGrid.IsVisible && AttachmentsGrid.SelectedItem is { } selected)
            AttachmentsGrid.ScrollIntoView(selected);
    }

    private void OpenMessageBytes(object sender, RoutedEventArgs e)
    {
        if ((_rawSourceRecord ?? _activeRow?.Record) is not { } record) return;
        if (record.ContainerMessage is { Length: > 0, CanRead: true } stored)
            SetRawRange(stored.FileRange ?? new ByteRange(0, stored.Length),
                L("저장된 메시지 바이트", "Stored message bytes"), container: stored.FileRange is null ? stored : null);
        else if (record.Format.IsFolderStore())
        {
            if (_rawPstProperty is { } property)
                SetRawRange(new ByteRange(0, property.Length), L("전체 속성", "Whole property"), property: property);
        }
        else if (record.CanReadMessage)
            SetRawRange(record.MessageRange, record.Format == MailFormat.Msg
                ? L("MSG 파일 바이트", "MSG file bytes") : L("메시지 범위", "Message range"));
        if (ReferenceEquals(sender, RawMessageButton))
            RawBytesBox.FocusHexColumn();
    }

    private void SetRawRange(ByteRange range, string label, CompoundStreamLocator? stream = null,
        PstPropertyLocator? property = null, HexField? field = null, byte[]? decoded = null,
        MailAttachment? decodedAttachment = null, AttachmentBoundary? boundary = null, ByteRange? decodedPeHeader = null,
        ContainerEvidence? container = null)
    {
        _rawContainer = container;
        _rawRange = range;
        _rawField = field;
        _rawDecodedBytes = decoded;
        _rawDecodedAttachment = decodedAttachment;
        _rawAttachmentBoundary = boundary;
        _rawDecodedPeHeader = decodedPeHeader;
        ByteRange? decodedFinding = boundary?.Range ?? decodedPeHeader;
        RawSignatureNote.Visibility = decodedFinding is null ? Visibility.Collapsed : Visibility.Visible;
        RawLocationExpander.IsExpanded = false;
        _rawPage = decodedFinding is { } focus
            ? Math.Min(Math.Max(0, range.Length - 1), focus.Offset) / RawPageBytes * RawPageBytes : 0;
        _rawName = label;
        _rawStream = stream;
        _rawPstProperty = property;
        ShowRawPage();
    }

    private void PreviousRawPage(object sender, RoutedEventArgs e)
    {
        _rawPage = Math.Max(0, _rawPage - RawPageBytes);
        ShowRawPage();
    }

    private void NextRawPage(object sender, RoutedEventArgs e)
    {
        if (_rawRange.Length - _rawPage > RawPageBytes)
        {
            _rawPage += RawPageBytes;
            ShowRawPage();
        }
    }

    private void ShowRawEmpty(MailFormat format)
    {
        string hint = format.IsFolderStore()
            ? L("헤더 원문 또는 첨부파일에서 확인할 원본을 여세요.", "Open bytes from Raw headers or Attachments.")
            : format == MailFormat.Msg
                ? L("MSG 파일 전체를 열거나 헤더·첨부의 바이트를 확인하세요.", "Open the whole MSG file or inspect header or attachment bytes.")
                : "";
        RawBytesBox.ClearView(hint);
    }

    private void ShowRawPage()
    {
        if (_session is not { } session || (_rawSourceRecord ?? _activeRow?.Record) is not { } record) return;
        var page = new ByteRange(checked(_rawRange.Offset + _rawPage), Math.Min(RawPageBytes, _rawRange.Length - _rawPage));
        try
        {
            byte[] bytes = _rawDecodedBytes is { } decoded ? decoded.AsSpan((int)page.Offset, (int)page.Length).ToArray()
                : _rawPstProperty is { } property ? session.ReadPstPropertyRange(record.SourceId, property, page)
                : _rawStream is { } cfbStream ? session.ReadCompoundRange(record.SourceId, cfbStream, page)
                : _rawContainer is { } container ? session.ReadContainerRange(record.SourceId, container, page)
                : session.ReadRange(record.SourceId, page);
            RawBytesBox.AddressLabel = RawAddressLabel(record);
            var fields = _rawField is { } field ? new List<HexField> { field } : [];
            int cursor = 0;
            if ((_rawAttachmentBoundary?.Range ?? _rawDecodedPeHeader) is { } finding && bytes.Length > 0)
            {
                long start = Math.Max(page.Offset, finding.Offset), end = Math.Min(page.End, finding.End);
                if (start < end)
                {
                    cursor = (int)(start - page.Offset);
                    int visibleLength = (int)(end - start);
                    string label = _rawAttachmentBoundary is { } boundary ? BoundaryTitle(boundary.Kind)
                        : L("Windows PE 헤더", "Windows PE header");
                    if (visibleLength != finding.Length) label += L(" · 현재 페이지", " · Current page");
                    string visibleBytes = BitConverter.ToString(bytes, cursor, Math.Min(visibleLength, 16)).Replace('-', ' ');
                    fields.Add(new HexField(cursor, visibleLength, label, visibleBytes)
                        { IsWarning = true });
                }
                else if (_rawAttachmentBoundary?.Kind == AttachmentBoundaryKind.MissingEnd && finding.Offset == page.End)
                    cursor = bytes.Length - 1;
            }
            RawBytesBox.ShowBytes(bytes, page.Offset, fields, cursor);
            RefreshRawLabels(record, page);
        }
        catch (Exception error) when (IsExpected(error))
        {
            RawStatus.Text = "";
            ClearRawLocation();
            RawBytesBox.ClearView($"{L("원본을 읽지 못했습니다.", "Could not read the source.")} {error.Message}");
        }
        UpdateCommands();
    }

    private string RawAddressLabel(MailRecord record) => _rawDecodedBytes is not null
        ? L("디코딩한 첨부 오프셋", "Decoded attachment offset")
        : _rawContainer is not null ? L("저장 항목 기준 오프셋", "Stored evidence offset")
        : _rawPstProperty is not null ? PropertyOffsetLabel(record)
        : _rawStream is not null ? L("스트림 오프셋", "Stream offset") : L("파일 오프셋", "File offset");

    private void RefreshRawLabels(MailRecord record, ByteRange page)
    {
        RawStatus.Text = $"{_rawName} · {RawAddressLabel(record)} {FormatVisibleRange(_rawRange)}";
        if (page != _rawRange) RawStatus.Text += $" · {L("표시", "Shown")} {FormatVisibleRange(page)}";
        if (_rawAttachmentBoundary is { } boundary)
            RawSignatureText.Text = $"{BoundaryTitle(boundary.Kind)} · {DescribeBoundary(boundary)}";
        else if (_rawDecodedPeHeader is { } pe)
            RawSignatureText.Text = L($"디코딩한 첨부 바이트의 0x{pe.Offset:X}에서 PE 시그니처 50 45 00 00을 확인했습니다.",
                $"The PE signature 50 45 00 00 was found at 0x{pe.Offset:X} in the decoded attachment.");
        var fields = new List<EvidenceField>();
        string name = System.IO.Path.GetFileName(record.SourcePath);
        string summary = name;
        if (_rawDecodedAttachment is { } decodedFile)
        {
            summary += $" · {decodedFile.Name}";
            if (decodedFile.EncodedRange is { } encoded)
                fields.Add(new(L("인코딩 원본 범위", "Encoded source range"), FormatVisibleRange(encoded)));
            if (decodedFile.PstProperty is { } storedProperty) fields.AddRange(PropertyLocationFields(storedProperty));
            if (decodedFile.CompoundStream is { } storedStream) fields.AddRange(StreamLocationFields(storedStream));
            if (decodedFile.Sha256 is { } attachmentHash)
                fields.Add(new(L("첨부 SHA-256", "Attachment SHA-256"), attachmentHash, IsDigest: true));
        }
        else if (_rawContainer is { } container)
        {
            fields.Add(new(L("원본 위치", "Source location"), container.Description));
        }
        else if (_rawPstProperty is { } property)
        {
            summary += $" · MAPI {property.TagHex}";
            if (PropertyAttachmentField(property) is { } attachment)
                summary += $" · {attachment.Label} {attachment.Value}";
            fields.AddRange(PropertyLocationFields(property));
        }
        else if (_rawStream is { } stream)
        {
            summary += $" · MAPI {stream.TagHex}";
            fields.AddRange(StreamLocationFields(stream));
        }
        fields.Add(new(L("원본 경로", "Source path"), record.SourcePath));
        if (_session?.Sources.FirstOrDefault(source => source.Id == record.SourceId)?.Sha256 is { } sha256)
            fields.Add(new(L("원본 파일 SHA-256", "Source file SHA-256"), sha256, IsDigest: true));
        _rawLocationFields = fields.ToArray();
        RawLocationFields.ItemsSource = _rawLocationFields;
        RawLocationSummary.Text = L($"원본 위치 · {summary}", $"Source location · {summary}");
        RawLocationRow.Visibility = Visibility.Visible;
    }

    private static IEnumerable<EvidenceField> PropertyLocationFields(PstPropertyLocator property)
    {
        if (PropertyAttachmentField(property) is { } attachment) yield return attachment;
        yield return new(L("MAPI 태그", "MAPI tag"), property.TagHex);
        yield return new(L("메시지 NID", "Message NID"), property.Message.MessageNid);
        yield return new(L("폴더 NID", "Folder NID"), property.Message.FolderNid);
        yield return new(L("폴더 경로", "Folder path"), property.Message.FolderPath);
    }

    private static EvidenceField? PropertyAttachmentField(PstPropertyLocator property) =>
        property.AttachmentNid is { } nid ? new(L("첨부 NID", "Attachment NID"), nid)
        : property.AttachmentIndex is { } index ? new(L("첨부 인덱스", "Attachment index"), index.ToString(CultureInfo.CurrentCulture))
        : null;

    private static EvidenceField[] StreamLocationFields(CompoundStreamLocator stream) =>
    [
        new(L("MAPI 태그", "MAPI tag"), stream.TagHex),
        new(L("스트림 경로", "Stream path"), stream.DisplayPath)
    ];

    private void ClearRawLocation()
    {
        _rawLocationFields = [];
        RawLocationFields.ItemsSource = null;
        RawLocationSummary.Text = "";
        RawLocationExpander.IsExpanded = false;
        RawLocationRow.Visibility = Visibility.Collapsed;
    }

    private static string FormatVisibleRange(ByteRange range) =>
        $"0x{range.Offset:X} · {range.Length:N0}\u00A0{L("바이트", "bytes")}";

    private string PropertyOffsetLabel(MailRecord record) =>
        record.Format == MailFormat.Ost && SourceForScope(record.SourceId)?.StoreLayout?.BlockEncoding is 1 or 2
            ? L("속성 상대 오프셋 (블록 디코딩 후)", "Property-relative offset (after block decoding)")
            : L("속성 상대 오프셋", "Property-relative offset");

    private void CopyRawLocation(object sender, RoutedEventArgs e)
    {
        if (_rawLocationFields.Length == 0) return;
        CopyEvidenceText(string.Join(Environment.NewLine,
            _rawLocationFields.Select(field => $"{field.Label}  {field.Value}").Append(RawStatus.Text)));
    }

    private sealed record EvidenceField(string Label, string Value, bool IsDigest = false);
}

public sealed class AttachmentIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string { Length: > 0 } name ? ShellIcons.For(name, folder: false, small: true) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
