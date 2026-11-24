using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using JournalDesk;
using MailViewer.Core;

namespace MailViewer.App;

public partial class MainWindow
{
    private TabItem? _mailInfoTab;
    private DataGrid? _mailFieldsGrid;
    private DataGrid? _sourceFieldsGrid;
    private TextBlock? _messageFieldsHeading;
    private TextBlock? _sourceFieldsHeading;
    private MailHeader? _selectedSourceHeader;
    private TextBox? _headerSourceBox;
    private TextBlock? _headerSourceStatus;
    private TextBlock? _headerSourceEmpty;
    private Button? _headerSourceCopy;
    private Button? _headerSourceBytes;
    private Button? _headerSourceBack;
    private bool _selectingHeaderText;
    private string _headerSourceText = "";
    private byte[] _headerSourceData = [];
    private string? _headerSourceRecordId;

    private void EnsureMailInfo()
    {
        if (_mailInfoTab is not null) return;
        var grid = new DataGrid
        {
            Style = (Style)FindResource("EvidenceGrid"),
            IsReadOnly = true,
            AutoGenerateColumns = false,
            EnableRowVirtualization = true,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow
        };
        AutomationProperties.SetAutomationId(grid, "MailFieldsGrid");
        RegisterName("MailFieldsGrid", grid);
        grid.Columns.Add(MailFieldColumn(nameof(MailInvestigationField.Name), 126));
        grid.Columns.Add(MailValueColumn());
        grid.Columns.Add(MailFieldColumn(nameof(MailInvestigationField.Source), 136));
        grid.MouseDoubleClick += (_, e) =>
        {
            if (grid.CurrentColumn != grid.Columns[1] &&
                ItemsControl.ContainerFromElement(grid, e.OriginalSource as DependencyObject) is DataGridRow)
            {
                OpenMailFieldSource();
                e.Handled = true;
            }
        };
        grid.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            OpenMailFieldSource();
            e.Handled = true;
        };
        var info = new DockPanel { Margin = (Thickness)FindResource("EvidencePaneInset") };
        ConfigureMailFieldCommands(grid);
        var selectedValue = new TextBox
        {
            Style = (Style)FindResource("Copyable"), TextWrapping = TextWrapping.Wrap,
            MaxHeight = 100, Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(6, 3, 6, 3)
        };
        selectedValue.SetResourceReference(BackgroundProperty, "Viewer.Panel");
        RegisterName("SelectedMailFieldValue", selectedValue);
        AutomationProperties.SetAutomationId(selectedValue, "SelectedMailFieldValue");
        grid.SelectionChanged += (_, _) => UpdateSelectedValue();
        grid.SizeChanged += (_, _) => UpdateSelectedValue();
        selectedValue.Visibility = Visibility.Collapsed;
        void UpdateSelectedValue()
        {
            var selected = grid.SelectedItem as MailInvestigationField;
            selectedValue.Text = selected?.Value ?? "";
            AutomationProperties.SetName(selectedValue, selected?.Name ?? "");
            double availableWidth = grid.Columns[1].ActualWidth - 32;
            selectedValue.Visibility = selectedValue.Text.Length > 0 && availableWidth > 0 &&
                !FitsOnOneLine(selectedValue.Text, grid, availableWidth) ? Visibility.Visible : Visibility.Collapsed;
        }
        var messageSection = new DockPanel();
        DockPanel.SetDock(selectedValue, Dock.Bottom);
        messageSection.Children.Add(selectedValue);
        messageSection.Children.Add(grid);
        var sections = new Grid();
        sections.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sections.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sections.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sections.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        sections.SizeChanged += (_, e) => messageSection.MaxHeight = Math.Max(92, e.NewSize.Height * 0.6);
        _messageFieldsHeading = InformationHeading(sections, 0, new Thickness(0, 0, 0, 4));
        Grid.SetRow(messageSection, 1);
        sections.Children.Add(messageSection);
        _sourceFieldsHeading = InformationHeading(sections, 2, new Thickness(0, 11, 0, 4));
        _sourceFieldsGrid = new DataGrid
        {
            VerticalAlignment = VerticalAlignment.Top,
            Style = (Style)FindResource("EvidenceGrid"),
            IsReadOnly = true,
            AutoGenerateColumns = false,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow
        };
        _sourceFieldsGrid.Columns.Add(MailFieldColumn(nameof(EvidenceField.Label), 135));
        _sourceFieldsGrid.Columns.Add(MailValueColumn());
        SetValueCopyShortcut(_sourceFieldsGrid, () => (_sourceFieldsGrid.SelectedItem as EvidenceField)?.Value);
        AutomationProperties.SetAutomationId(_sourceFieldsGrid, "OverviewFields");
        RegisterName("OverviewFields", _sourceFieldsGrid);
        Grid.SetRow(_sourceFieldsGrid, 3);
        sections.Children.Add(_sourceFieldsGrid);
        info.Children.Add(sections);
        _mailFieldsGrid = grid;
        _mailInfoTab = new TabItem { Content = info };
        AutomationProperties.SetAutomationId(_mailInfoTab, "MailInfoTab");
        DetailTabs.Items.Insert(1, _mailInfoTab);

        var layout = new Grid { Margin = (Thickness)FindResource("EvidencePaneInset") };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var commands = new DockPanel { Style = (Style)FindResource("EvidenceToolbar") };
        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal };
        var quietAction = (Style)FindResource("QuietAction");
        _headerSourceBack = new Button
        {
            Style = quietAction,
            Tag = FindResource("Lucide.Back"),
            Margin = new Thickness(0, 0, 6, 0),
            Visibility = Visibility.Collapsed
        };
        RegisterName("RawHeaderBackButton", _headerSourceBack);
        AutomationProperties.SetAutomationId(_headerSourceBack, "RawHeaderBackButton");
        _headerSourceCopy = new Button { Style = quietAction, Tag = "\uE8C8", Margin = new Thickness(4, 0, 0, 0) };
        RegisterName("RawHeaderCopyButton", _headerSourceCopy);
        AutomationProperties.SetAutomationId(_headerSourceCopy, "RawHeaderCopyButton");
        _headerSourceBytes = new Button { Style = quietAction, Tag = FindResource("Lucide.Code"), Margin = new Thickness(4, 0, 0, 0) };
        RegisterName("RawHeaderBytesButton", _headerSourceBytes);
        AutomationProperties.SetAutomationId(_headerSourceBytes, "RawHeaderBytesButton");
        _headerSourceBack.Click += (_, _) => FocusMailField();
        _headerSourceCopy.Click += (_, _) => CopyHeaderSource();
        _headerSourceBytes.Click += (_, _) => OpenHeaderBytes();
        DockPanel.SetDock(_headerSourceBack, Dock.Left);
        DockPanel.SetDock(buttonRow, Dock.Right);
        buttonRow.Children.Add(_headerSourceCopy);
        buttonRow.Children.Add(_headerSourceBytes);
        commands.Children.Add(_headerSourceBack);
        commands.Children.Add(buttonRow);
        _headerSourceStatus = new TextBlock
        {
            Style = (Style)FindResource("Desk.StatusText"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _headerSourceStatus.SetBinding(ToolTipProperty, new Binding(nameof(TextBlock.Text))
            { RelativeSource = new RelativeSource(RelativeSourceMode.Self) });
        RegisterName("RawHeaderStatus", _headerSourceStatus);
        commands.Children.Add(_headerSourceStatus);
        layout.Children.Add(commands);
        _headerSourceBox = new TextBox
        {
            Style = (Style)FindResource("Copyable"),
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            Padding = new Thickness(8, 6, 8, 6),
            BorderThickness = new Thickness(1),
            AcceptsReturn = true,
            IsInactiveSelectionHighlightEnabled = true,
            SelectionOpacity = 0.55,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas")
        };
        _headerSourceBox.SetResourceReference(TextBoxBase.SelectionBrushProperty, SystemColors.HighlightBrushKey);
        _headerSourceBox.SetResourceReference(Control.BorderBrushProperty, "Viewer.Rule");
        RegisterName("RawHeaderText", _headerSourceBox);
        AutomationProperties.SetAutomationId(_headerSourceBox, "RawHeaderText");
        Grid.SetRow(_headerSourceBox, 1);
        layout.Children.Add(_headerSourceBox);

        _headerSourceEmpty = new TextBlock
        {
            Style = (Style)FindResource("Desk.StatusText"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        RegisterName("RawHeaderEmpty", _headerSourceEmpty);
        AutomationProperties.SetLiveSetting(_headerSourceEmpty, AutomationLiveSetting.Polite);
        Grid.SetRow(_headerSourceEmpty, 1);
        layout.Children.Add(_headerSourceEmpty);
        HeadersTab.Content = layout;
        _headerSourceBox.SelectionChanged += (_, _) => SetHeaderFromCaret();
        RefreshMailInfo();
    }

    private static bool FitsOnOneLine(string value, Control control, double availableWidth)
    {
        if (value.Length > 200 || value.Contains('\n') || value.Contains('\r')) return false;
        var text = new System.Windows.Media.FormattedText(value,
            System.Globalization.CultureInfo.CurrentUICulture, control.FlowDirection,
            new System.Windows.Media.Typeface(control.FontFamily, control.FontStyle, control.FontWeight, control.FontStretch),
            control.FontSize, control.Foreground, System.Windows.Media.VisualTreeHelper.GetDpi(control).PixelsPerDip);
        return text.WidthIncludingTrailingWhitespace <= availableWidth;
    }

    private static TextBlock InformationHeading(Grid sections, int row, Thickness margin)
    {
        var heading = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = margin };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        Grid.SetRow(heading, row);
        sections.Children.Add(heading);
        return heading;
    }

    private static DataGridTextColumn MailFieldColumn(string path, DataGridLength width)
    {
        var element = new Style(typeof(TextBlock));
        element.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        element.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(path)));
        element.Setters.Add(new Setter(ToolTipService.ShowsToolTipOnKeyboardFocusProperty, true));
        element.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(6, 0, 6, 0)));
        element.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        return new DataGridTextColumn
        {
            Binding = new Binding(path),
            ElementStyle = element,
            Width = width,
            MinWidth = 72
        };
    }

    private DataGridTemplateColumn MailValueColumn() => new()
    {
        CellTemplate = (DataTemplate)FindResource("MailFieldValueCell"),
        Width = new DataGridLength(1, DataGridLengthUnitType.Star),
        MinWidth = 85,
        CanUserSort = false
    };

    private void ConfigureMailFieldCommands(DataGrid grid)
    {
        SetValueCopyShortcut(grid, () => (grid.SelectedItem as MailInvestigationField)?.Value);
        grid.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(grid, e.OriginalSource as DependencyObject) is DataGridRow row)
                grid.SelectedItem = row.Item;
        };
        var menu = new ContextMenu();
        grid.ContextMenu = menu;
        menu.Opened += (_, _) =>
        {
            menu.Items.Clear();
            var field = grid.SelectedItem as MailInvestigationField;
            var copy = new MenuItem
            {
                Header = L("선택한 값 복사", "Copy selected value"),
                InputGestureText = "Ctrl+C",
                IsEnabled = field is { Value.Length: > 0 }
            };
            AutomationProperties.SetAutomationId(copy, "CopyMailFieldValue");
            copy.Click += (_, _) => CopyEvidenceText(field?.Value ?? "");
            menu.Items.Add(copy);

            if (field is not null && CanChooseSources && MailFieldSearchKey(field) is { } key)
            {
                var find = new MenuItem
                {
                    Header = key.Kind == InvestigationKeyKind.Address
                        ? L("같은 이메일 주소 검색", "Find this email address")
                        : L("같은 Message-ID 검색", "Find this Message-ID")
                };
                AutomationProperties.SetAutomationId(find, "FindMailFieldValue");
                find.Click += async (_, _) =>
                {
                    grid.Focus();
                    await RunInvestigationSearch(key);
                };
                menu.Items.Add(find);
            }

            var view = new MenuItem
            {
                Header = L("선택한 항목의 출처 보기", "View source for selected field"),
                InputGestureText = "Enter",
                IsEnabled = field is { HasSource: true } && CanChooseSources
            };
            AutomationProperties.SetAutomationId(view, "OpenMailFieldSource");
            view.Click += (_, _) => OpenMailFieldSource();
            menu.Items.Add(view);
        };
    }

    private void SetValueCopyShortcut(DataGrid grid, Func<string?> selectedValue)
    {
        grid.ClipboardCopyMode = DataGridClipboardCopyMode.None;
        grid.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.C || Keyboard.Modifiers != ModifierKeys.Control ||
                Keyboard.FocusedElement is CopyText { SelectionLength: > 0 } || selectedValue() is not { Length: > 0 } value)
                return;
            CopyEvidenceText(value);
            e.Handled = true;
        };
    }

    private InvestigationKey? MailFieldSearchKey(MailInvestigationField field)
    {
        if (field.SearchKind is not { } kind || _session is not { } session || _activeRow?.Record is not { } record)
            return null;

        var keys = session.Inspect(record.Id).Observations
            .Where(row => row.Key?.Kind == kind && (row.EvidenceRecordId is null || row.EvidenceRecordId == record.Id)
                && MatchesLocation(row))
            .Select(row => row.Key).Distinct().Take(2).ToArray();
        return keys.Length == 1 ? keys[0] : null;

        bool MatchesLocation(InvestigationObservation row)
        {
            if (row.Location is not { } location) return false;
            if (field.Header is { } header)
                return location.Range == header.Range && row.Field.Equals(header.Name, StringComparison.OrdinalIgnoreCase)
                    && (kind != InvestigationKeyKind.Address || row.Value == field.Value);
            var stored = field.Stored;
            return stored is not null &&
                (stored.Stream is not null && location.Stream == stored.Stream ||
                 stored.Property is not null && location.Property == stored.Property);
        }
    }

    private void RefreshMailInfo()
    {
        if (_mailFieldsGrid is not { } grid) return;
        _mailInfoTab!.Header = L("메일 정보", "Message information");
        _messageFieldsHeading!.Text = L("메시지 속성", "Message fields");
        _sourceFieldsHeading!.Text = L("자료 출처", "Evidence source");
        _sourceFieldsGrid!.Columns[0].Header = L("항목", "Field");
        _sourceFieldsGrid.Columns[1].Header = L("값", "Value");
        AutomationProperties.SetName(_sourceFieldsGrid, _sourceFieldsHeading.Text);
        RefreshConversation();
        HeadersTab.Header = L("헤더 원문", "Raw headers");
        _headerSourceBack!.Content = L("메일 정보", "Message information");
        _headerSourceBack.ToolTip = L("메일 정보로 돌아가기", "Back to message information");
        AutomationProperties.SetName(_headerSourceBack, (string)_headerSourceBack.ToolTip);
        _headerSourceCopy!.Content = L("복사", "Copy");
        _headerSourceCopy.ToolTip = L("헤더 전체 복사", "Copy all headers");
        _headerSourceBytes!.Content = "HEX";
        _headerSourceBytes.ToolTip = L("헤더의 원본 바이트 열기", "Open the original header bytes");
        grid.Columns[0].Header = L("항목", "Field");
        grid.Columns[1].Header = L("값", "Value");
        grid.Columns[2].Header = L("출처", "Source");
        AutomationProperties.SetName(grid, L("메일 조사 필드", "Mail investigation fields"));
        AutomationProperties.SetName(_headerSourceBox!, L("전송 헤더 원문", "Raw transport headers"));
        var record = _activeRow?.Record;
        if (_headerSourceRecordId != record?.Id)
        {
            _headerSourceBack!.Visibility = Visibility.Collapsed;
            _headerSourceRecordId = null;
            _headerSourceText = "";
            _headerSourceData = [];
            _headerSourceBox!.Clear();
            ShowHeaderEmpty("");
        }
        grid.ItemsSource = record is null ? null : EnumerateMailFields(record).OrderBy(field => field.Group).ToArray();
    }

    private IEnumerable<MailInvestigationField> EnumerateMailFields(MailRecord record)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (MailHeader header in record.Headers)
        {
            string key = header.Name.ToUpperInvariant();
            int occurrence = counts[key] = counts.GetValueOrDefault(key) + 1;
            string source = $"{header.Name} #{occurrence}";
            string? addressLabel = MailAddressLabel(key);
            string name = addressLabel ?? key switch
            {
                "RETURN-PATH" => L("반송 경로", "Return path"),
                "DATE" => L("메시지 시각", "Message date"),
                "RECEIVED" => L("전송 경로", "Transport hop"),
                "X-ORIGINATING-IP" => L("기록 IP", "Recorded IP"),
                "AUTHENTICATION-RESULTS" => L("인증 기록", "Authentication"),
                "RECEIVED-SPF" => "Received-SPF",
                "DKIM-SIGNATURE" => L("서명 헤더", "Signature header"),
                "SUBJECT" => L("제목", "Subject"),
                "MESSAGE-ID" or "IN-REPLY-TO" or "REFERENCES" => header.Name,
                _ => ""
            };
            if (name.Length == 0) continue;
            string value = Regex.Replace(header.Value.Trim(), @"[\r\n]+[ \t]*", " ");
            if (addressLabel is not null)
            {
                foreach (var field in HeaderAddressFields(addressLabel, value, source, header))
                    yield return field;
                continue;
            }

            int group = key switch
            {
                "DATE" or "SUBJECT" or "MESSAGE-ID" or "IN-REPLY-TO" or "REFERENCES" => 1,
                "RECEIVED" or "X-ORIGINATING-IP" => 2,
                "AUTHENTICATION-RESULTS" or "RECEIVED-SPF" or "DKIM-SIGNATURE" => 3,
                _ => 0
            };
            if (group == 3 && _session is { } session)
            {
                foreach (var result in session.Inspect(record.Id).Observations
                             .Where(row => row.Location?.Range == header.Range && row.Field.Equals(header.Name, StringComparison.OrdinalIgnoreCase)))
                    yield return new(name, result.Value, $"{L("신뢰 미확인", "Unverified")} #{occurrence}", header, Group: group);
                continue;
            }
            if (key == "SUBJECT")
                value = MimeKit.Utils.Rfc2047.DecodeText(Encoding.UTF8.GetBytes(value));
            yield return new(name, value, source, header, Group: group,
                SearchKind: key == "MESSAGE-ID" ? InvestigationKeyKind.MessageId : null);

            if (key == "RECEIVED")
            {
                foreach (string candidate in Regex.Matches(value, @"\[([^\]]+)\]").Select(match => match.Groups[1].Value))
                {
                    string ip = candidate.StartsWith("IPv6:", StringComparison.OrdinalIgnoreCase) ? candidate[5..] : candidate;
                    if ((ip.Contains(':') || ip.Count(c => c == '.') == 3) && IPAddress.TryParse(ip, out _))
                        yield return new(L("기록 IP", "Recorded IP"), ip, source, header, Group: group);
                }
            }
        }

        foreach (StoredMailField field in record.StoredFields.OrderBy(StoredAddressOrder))
        {
            yield return StoredAddressField(field);
        }

        if (!counts.ContainsKey("BCC") && !record.StoredFields.Any(field => field.Name == "Display Bcc")
            && string.IsNullOrWhiteSpace(record.Bcc))
            yield return new(L("숨은 참조", "Bcc"), "", "", Group: record.Headers.Count == 0 ? 4 : 0);
    }

    private void OpenMailFieldSource()
    {
        if (!CanChooseSources || _mailFieldsGrid?.SelectedItem is not MailInvestigationField field) return;
        if (field.Header is { } header)
        {
            OpenHeaderSource(header);
            return;
        }
        if (_mailInfoTab is null || field.Stored is not { } stored) return;
        if (stored.Property is { } property)
            OpenEvidenceBytes(_mailInfoTab, new ByteRange(0, property.Length), stored.Name, null, property);
        else if (stored.Stream is { } stream)
            OpenEvidenceBytes(_mailInfoTab, new ByteRange(0, stream.Length), stored.Name, stream);
    }

    private void FocusMailField()
    {
        DetailTabs.SelectedItem = _mailInfoTab;
        if (_mailFieldsGrid is not { } grid) return;
        if (grid.SelectedItem is { } selected)
        {
            grid.ScrollIntoView(selected);
            grid.UpdateLayout();
            if (grid.ItemContainerGenerator.ContainerFromItem(selected) is DataGridRow row)
            {
                row.Focus();
                return;
            }
        }
        grid.Focus();
    }

    private void OpenHeaderSource(MailHeader? header)
    {
        DetailTabs.SelectedItem = HeadersTab;
        if (_activeRow?.Record is not { } record || _headerSourceBox is null) return;
        if (_headerSourceRecordId != record.Id) LoadHeaderSource(record);
        _headerSourceBack!.Visibility = header is null ? Visibility.Collapsed : Visibility.Visible;
        _headerSourceBox.Focus();
        SelectHeaderSourceOccurrence(header);
    }

    private void LoadHeaderSource(MailRecord record)
    {
        _headerSourceRecordId = record.Id;
        _selectedSourceHeader = null;
        _headerSourceData = [];
        _headerSourceText = "";
        _headerSourceBox!.Clear();
        _headerSourceCopy!.IsEnabled = false;
        _headerSourceCopy.Visibility = Visibility.Collapsed;
        ShowHeaderEmpty("");
        if (_session is not { } session) return;
        var range = record.HeaderRange;
        if (range.Length == 0 || (record.TransportHeaderState is { } state && state != HeaderEvidenceState.Present))
        {
            ShowHeaderEmpty(record.TransportHeaderState is null
                ? L("전송 헤더 원문을 확인할 수 없습니다.", "Raw transport headers are unavailable.")
                : HeaderStateText(record));
            return;
        }
        if (range.Length > 4 * 1024 * 1024)
        {
            ShowHeaderEmpty(L("헤더가 너무 깁니다. HEX에서 확인하세요.",
                "The headers exceed the text limit. View them in HEX."));
            return;
        }
        try
        {
            byte[] bytes = ReadHeaderSourceRange(session, record, range);
            var encoding = HeaderSourceEncoding(record);
            _headerSourceText = encoding.GetString(bytes).TrimEnd('\0');
            if (_headerSourceText.Length == 0)
            {
                ShowHeaderEmpty(L("표시할 헤더 텍스트가 없습니다.", "There is no header text to display."));
                return;
            }
            _headerSourceBox.Text = _headerSourceText;
            _headerSourceData = bytes;
            _headerSourceCopy.IsEnabled = true;
            _headerSourceCopy.Visibility = Visibility.Visible;
            ShowHeaderSourceLocation(record, encoding);
        }
        catch (Exception error) when (IsExpected(error) || error is DecoderFallbackException)
        {
            ShowHeaderEmpty(L("헤더를 텍스트로 읽을 수 없습니다. HEX에서 확인하세요.",
                "The headers could not be read as text. View them in HEX."));
        }
    }

    private void ShowHeaderEmpty(string reason)
    {
        _headerSourceStatus!.Text = "";
        _headerSourceStatus.Visibility = Visibility.Collapsed;
        _headerSourceEmpty!.Text = reason;
        _headerSourceEmpty.Visibility = reason.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private string HeaderSourceOffsetLabel(MailRecord record) => record.TransportHeaderProperty is not null
        ? PropertyOffsetLabel(record)
        : record.TransportHeaderStream is not null
            ? L("스트림 오프셋", "Stream offset") : L("파일 오프셋", "File offset");

    private static Encoding HeaderSourceEncoding(MailRecord record) => Encoding.GetEncoding(record.TransportHeaderCodePage ?? 65001,
        EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    private static byte[] ReadHeaderSourceRange(EvidenceSession session, MailRecord record, ByteRange range) =>
        record.TransportHeaderProperty is { } property ? session.ReadPstPropertyRange(record.SourceId, property, range)
        : record.TransportHeaderStream is { } stream ? session.ReadCompoundRange(record.SourceId, stream, range)
        : session.ReadRange(record.SourceId, range);

    private void SelectHeaderSourceOccurrence(MailHeader? header)
    {
        if (header is null || _headerSourceBox is not { } box || _headerSourceData.Length == 0
            || _activeRow?.Record is not { } record || _headerSourceRecordId != record.Id)
            return;
        try
        {
            int start = checked((int)(header.Range.Offset - record.HeaderRange.Offset));
            int length = checked((int)header.Range.Length);
            if (start < 0 || (long)start + length > record.HeaderRange.Length) return;
            var encoding = HeaderSourceEncoding(record);
            int prefixLength = encoding.GetCharCount(_headerSourceData.AsSpan(0, start));
            int selectionLength = encoding.GetCharCount(_headerSourceData.AsSpan(start, length));
            _selectingHeaderText = true;
            try { box.Select(prefixLength, selectionLength); }
            finally { _selectingHeaderText = false; }
            _selectedSourceHeader = header;
            ShowHeaderSourceLocation(record, encoding);
            box.ScrollToLine(box.GetLineIndexFromCharacterIndex(box.SelectionStart));
        }
        catch (Exception error) when (IsExpected(error) || error is DecoderFallbackException)
        {
            _selectedSourceHeader = null;
        }
    }

    private void SetHeaderFromCaret()
    {
        if (_selectingHeaderText || _headerSourceBox is not { } box
            || _activeRow?.Record is not { } record || _headerSourceRecordId != record.Id
            || _headerSourceData.Length == 0 || _headerSourceText.Length == 0)
            return;

        try
        {
            var encoding = HeaderSourceEncoding(record);
            int start = Math.Min(box.SelectionStart, _headerSourceText.Length);
            long offset = record.HeaderRange.Offset + encoding.GetByteCount(_headerSourceText.AsSpan(0, start));
            _selectedSourceHeader = record.Headers.FirstOrDefault(item => offset >= item.Range.Offset && offset < item.Range.End);
            ShowHeaderSourceLocation(record, encoding);
        }
        catch (EncoderFallbackException)
        {
            _selectedSourceHeader = null;
            ShowHeaderSourceLocation(record, HeaderSourceEncoding(record));
        }
    }

    private void ShowHeaderSourceLocation(MailRecord record, Encoding encoding)
    {
        string selected = _selectedSourceHeader is { } header ? $"{header.Name} · " : "";
        var range = _selectedSourceHeader?.Range ?? record.HeaderRange;
        _headerSourceStatus!.Visibility = Visibility.Visible;
        _headerSourceStatus.Text = $"{selected}{HeaderSourceOffsetLabel(record)} {FormatVisibleRange(range)} · {encoding.WebName}";
        if (!string.IsNullOrEmpty(record.TransportHeaderDetail))
            _headerSourceStatus.Text += $" · {record.TransportHeaderDetail}";
    }

    private void CopyHeaderSource() => CopyEvidenceText(_headerSourceText);

    private void CopyEvidenceText(string value)
    {
        if (value.Length == 0) return;
        try { Clipboard.SetText(value); }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            WorkStatus.Text = L("클립보드에 복사하지 못했습니다.", "Could not copy to the clipboard.");
        }
    }

    private sealed record MailInvestigationField(string Name, string Value, string Source, MailHeader? Header = null,
        StoredMailField? Stored = null, int Group = 0, InvestigationKeyKind? SearchKind = null)
    {
        public bool HasSource => Header is not null || Stored?.Property is not null || Stored?.Stream is not null;
    }
}

