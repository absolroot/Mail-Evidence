using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MailViewer.Core;

namespace MailViewer.App;

public partial class MainWindow
{
    private enum AttachmentLayout { List, Docked, WidePreview }
    private sealed record PreparedAttachmentPreview(PreviewState State, BitmapSource? Image = null,
        AttachmentTextPreview? Text = null, bool IsDocument = false);

    private const double AttachmentDockWidth = 570;
    private readonly AttachmentPreviewRenderer _attachmentRenderer = new();
    private CancellationTokenSource? _previewWork;
    private (EvidenceSession Session, string Record, string Attachment)? _previewKey;
    private ((EvidenceSession Session, string Record, string Attachment) Key, PreparedAttachmentPreview Preview)? _lastPreparedPreview;
    private PreviewState _previewState;
    private bool _previewIsDocument;
    private AttachmentTextPreview? _previewText;
    private bool _reserveAttachmentPreview;
    private bool _expandAttachmentPreview;
    private bool _fitDocumentWidth = true;
    private bool _fitImageWidth;
    private bool? _attachmentInfoPreference;
    private bool _updatingAttachmentInfo;
    private AttachmentLayout _attachmentLayout;
    private GridLength _attachmentListWidth = new(1.3, GridUnitType.Star);
    private GridLength _attachmentCanvasWidth = new(2.2, GridUnitType.Star);
    private bool IsAttachmentPreviewExpanded =>
        _expandAttachmentPreview && _reserveAttachmentPreview && DetailTabs.SelectedItem == AttachmentsTab;

    private void OnAttachmentPreviewVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        RefreshAttachmentPreview();

    private void RefreshAttachmentPreview()
    {
        if (AttachmentInfoToggle is null || AttachmentsGrid is null) return;
        if (!AttachmentSurface.IsVisible || _activeRow is not { } row || _session is not { } session
            || AttachmentsGrid.SelectedItem is not MailAttachment attachment)
        {
            if (_previewWork is not null)
            {
                CancelAttachmentPreview();
                _previewKey = null;
            }
            if (AttachmentsGrid.SelectedItem is null)
            {
                _previewKey = null;
                SetAttachmentPreview(PreviewState.Empty);
            }
            return;
        }

        var key = (session, row.Record.Id, attachment.Id);
        if (_previewKey == key) return;

        CancelAttachmentPreview();
        _previewKey = key;
        AttachmentsGrid.ScrollIntoView(attachment);
        PreviewState availability = AttachmentPreviewAvailability(attachment);
        if (availability != PreviewState.Ready)
        {
            SetAttachmentPreview(availability);
            return;
        }

        _previewWork = new CancellationTokenSource();
        SetAttachmentPreview(PreviewState.Loading);
        _ = LoadAttachmentPreviewAsync(session, row.Record.Id, attachment, _previewWork);
    }

    private static PreviewState AttachmentPreviewAvailability(MailAttachment attachment)
    {
        if (attachment.Issue is not null || attachment.DecodedLength is null || attachment.Sha256 is null
            || (attachment.DecodedLength == 0 && !IsTextPreviewCandidate(attachment)))
            return PreviewState.Unreadable;
        if (!CanPreviewAttachment(attachment))
            return PreviewState.Unsupported;
        if (attachment.DecodedLength > AttachmentPreviewRenderer.MaxInputBytes ||
            attachment.EncodedRange is { Length: > AttachmentPreviewRenderer.MaxInputBytes * 3L })
            return PreviewState.Limit;
        return PreviewState.Ready;
    }

    private static bool CanPreviewAttachment(MailAttachment attachment) => IsTextPreviewCandidate(attachment) ||
        Path.GetExtension(attachment.Name).ToLowerInvariant() is ".pdf" or ".hwp" or ".hwpx" or ".docx"
            or ".jpg" or ".jpeg" or ".png"
        || IsStructuredPreviewCandidate(attachment)
        || attachment.MediaType.ToLowerInvariant() is "application/pdf" or "application/x-hwp" or "application/haansofthwp"
            or "application/hwp+zip" or "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
            or "image/jpeg" or "image/png";

    private static bool IsStructuredPreviewCandidate(MailAttachment attachment) =>
        Path.GetExtension(attachment.Name).ToLowerInvariant() is ".doc" or ".pptx" or ".ppt" or ".xlsx" or ".xls"
            or ".zip" or ".tar" or ".7z"
        || attachment.MediaType.ToLowerInvariant() is "application/msword" or "application/vnd.ms-powerpoint" or "application/vnd.ms-excel"
            or "application/vnd.openxmlformats-officedocument.presentationml.presentation"
            or "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
            or "application/zip" or "application/x-zip-compressed" or "application/x-tar" or "application/x-7z-compressed";

    private static bool IsTextPreviewCandidate(MailAttachment attachment) =>
        Path.GetExtension(attachment.Name).ToLowerInvariant() is ".txt" or ".bin" or ".dat" or ".log" or ".csv" or ".tsv"
            or ".json" or ".xml" or ".md" or ".ini" or ".cfg" or ".yaml" or ".yml"
        || attachment.MediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase);

    private async Task<PreparedAttachmentPreview> PrepareAttachmentPreviewAsync(
        EvidenceSession session, string recordId, MailAttachment attachment, CancellationToken ct)
    {
        var key = (session, recordId, attachment.Id);
        if (_lastPreparedPreview is { } cached && cached.Key == key) return cached.Preview;
        try
        {
            await Task.Delay(220, ct);
            bool renderText = IsTextPreviewCandidate(attachment) && !IsStructuredPreviewCandidate(attachment);
            var result = await Task.Run(async () =>
            {
                byte[] bytes = session.ReadAttachment(recordId, attachment.Id, ct);
                ct.ThrowIfCancellationRequested();
                if (renderText) return new PreviewResult(PreviewState.Ready, Text: AttachmentTextPreview.Create(bytes));
                return await _attachmentRenderer.RenderAsync(bytes, ct).ConfigureAwait(false);
            }, ct);

            ct.ThrowIfCancellationRequested();
            BitmapSource? image = null;
            if (result.Frame is { } frame)
            {
                image = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Pbgra32,
                    null, frame.Pixels, frame.Width * 4);
                image.Freeze();
            }
            var preview = new PreparedAttachmentPreview(result.State, image, result.Text, result.Frame?.IsDocument == true);
            if (preview.State == PreviewState.Ready) _lastPreparedPreview = (key, preview);
            return preview;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ObjectDisposedException
                                               or KeyNotFoundException or UnauthorizedAccessException)
        {
            return new(PreviewState.Unreadable);
        }
    }

    private async Task LoadAttachmentPreviewAsync(EvidenceSession session, string recordId, MailAttachment attachment,
        CancellationTokenSource work)
    {
        try
        {
            PreparedAttachmentPreview preview = await PrepareAttachmentPreviewAsync(session, recordId, attachment, work.Token);
            if (!ReferenceEquals(_previewWork, work) || !AttachmentSurface.IsVisible) return;
            _previewIsDocument = preview.IsDocument;
            if (preview.Image is not null) AutomationProperties.SetName(AttachmentPreviewImage, attachment.Name);
            SetAttachmentPreview(preview.State, preview.Image, preview.Text);
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_previewWork, work)) _previewWork = null;
            work.Dispose();
        }
    }

    private void CancelAttachmentPreview()
    {
        _previewWork?.Cancel();
        _previewWork = null;
    }

    private void SetAttachmentPreview(PreviewState state, BitmapSource? image = null, AttachmentTextPreview? text = null)
    {
        _previewState = state;
        _previewText = text;
        AttachmentPreviewImage.Source = image;
        AttachmentPreviewScroll.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        AttachmentPreviewTextBox.Text = text?.Text ?? "";
        AttachmentPreviewTextBox.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        AttachmentPreviewTextBox.ScrollToHome();
        AttachmentPreviewScroll.ScrollToHome();
        bool selected = state != PreviewState.Empty;
        bool wasPreviewExpanded = IsAttachmentPreviewExpanded;
        _reserveAttachmentPreview = selected && (state != PreviewState.Unsupported
            || AttachmentsGrid.Items.OfType<MailAttachment>().Any(CanPreviewAttachment));
        if (wasPreviewExpanded != IsAttachmentPreviewExpanded) SynchronizeNarrowReader();
        UpdateAttachmentLayout();
        FitAttachmentPreviewImage();
        RefreshAttachmentPreviewText();
    }

    private bool CanDockAttachmentPreview =>
        AttachmentSurface.ActualWidth >= AttachmentDockWidth && !_readerIsNarrow;

    private void OnAttachmentPreviewLayoutChanged(object sender, SizeChangedEventArgs e) => UpdateAttachmentLayout();

    private void UpdateAttachmentLayout()
    {
        if (AttachmentInfoToggle is null) return;
        var layout = !_reserveAttachmentPreview ? AttachmentLayout.List
            : CanDockAttachmentPreview && !_expandAttachmentPreview ? AttachmentLayout.Docked : AttachmentLayout.WidePreview;
        bool readable = AttachmentPreviewImage.Source is not null || _previewText is { Text.Length: > 0 };
        AttachmentPreviewExpandButton.Visibility = _expandAttachmentPreview || (!_readerIsNarrow && readable)
            ? Visibility.Visible : Visibility.Collapsed;
        bool pickerOnOwnRow = layout == AttachmentLayout.WidePreview && AttachmentSurface.ActualWidth < 535;
        Grid.SetRow(AttachmentPicker, pickerOnOwnRow ? 1 : 0);
        Grid.SetColumnSpan(AttachmentPicker, pickerOnOwnRow ? 2 : 1);
        AttachmentPicker.Margin = new Thickness(0, 0, pickerOnOwnRow ? 0 : 8, 5);
        UpdateAttachmentInfoLayout(layout);
        if (_attachmentLayout == layout) return;

        bool previousDock = _attachmentLayout == AttachmentLayout.Docked;
        if (previousDock)
        {
            _attachmentListWidth = AttachmentFilesColumn.Width;
            _attachmentCanvasWidth = AttachmentCanvasColumn.Width;
        }

        bool focusList = AttachmentPicker.IsKeyboardFocusWithin;
        bool focusPicker = AttachmentsGrid.IsKeyboardFocusWithin;
        bool listOnly = layout == AttachmentLayout.List;
        bool docked = layout == AttachmentLayout.Docked;
        bool wide = layout == AttachmentLayout.WidePreview;
        _attachmentLayout = layout;

        AttachmentFilesColumn.MinWidth = docked ? 206 : 0;
        AttachmentCanvasColumn.MinWidth = docked ? 345 : 0;
        AttachmentFilesColumn.Width = docked ? _attachmentListWidth : new GridLength(wide ? 0 : 1, GridUnitType.Star);
        AttachmentCanvasColumn.Width = docked ? _attachmentCanvasWidth : new GridLength(wide ? 1 : 0, GridUnitType.Star);
        AttachmentDividerColumn.Width = new GridLength(docked ? 7 : 0);
        AttachmentFilesPane.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        AttachmentPicker.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        AttachmentPreviewSplitter.Visibility = docked ? Visibility.Visible : Visibility.Collapsed;
        AttachmentPreviewPane.Visibility = listOnly ? Visibility.Collapsed : Visibility.Visible;
        AttachmentPreviewPane.Margin = new Thickness(docked ? 5 : 0, 0, 0, 0);
        Grid.SetRowSpan(AttachmentFilesPane, docked ? 2 : 1);
        Grid.SetColumn(AttachmentInfoToggle, listOnly ? 0 : 2);
        AttachmentInfoToggle.Margin = new Thickness(docked ? 5 : 0, 5, 0, 0);
        AttachmentPreviewFileName.Visibility = docked ? Visibility.Visible : Visibility.Collapsed;
        AttachmentListStatus.Visibility = listOnly && _previewState != PreviewState.Empty ? Visibility.Visible : Visibility.Collapsed;
        AttachmentMediaTypeColumn.Visibility = docked ? Visibility.Collapsed : Visibility.Visible;
        if (!wide)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RevealSelectedAttachmentRow));

        if ((wide && focusPicker) || (!wide && focusList)) FocusAttachmentSelection();
    }

    private void UpdateAttachmentInfoLayout(AttachmentLayout layout)
    {
        AttachmentInfoScroll.MaxHeight = Math.Clamp(AttachmentSurface.ActualHeight * 0.28, 65, 160);
        bool expanded = _attachmentInfoPreference ?? (layout == AttachmentLayout.List || AttachmentSurface.ActualHeight >= 380);
        if (AttachmentInfoToggle.IsExpanded == expanded) return;
        _updatingAttachmentInfo = true;
        try { AttachmentInfoToggle.IsExpanded = expanded; }
        finally { _updatingAttachmentInfo = false; }
    }

    private void OnAttachmentInfoChanged(object sender, RoutedEventArgs e)
    {
        if (!_updatingAttachmentInfo && IsLoaded && ReferenceEquals(e.OriginalSource, AttachmentInfoToggle))
            _attachmentInfoPreference = AttachmentInfoToggle.IsExpanded;
    }

    private void OnAttachmentPreviewViewportChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0) FitAttachmentPreviewImage();
    }

    private void FitAttachmentPreviewImage()
    {
        if (AttachmentPreviewImage.Source is not BitmapSource image) return;
        double width = AttachmentPreviewScroll.ViewportWidth;
        double height = AttachmentPreviewScroll.ViewportHeight;
        if (width <= 0 || height <= 0) return;
        bool fitWidth = _previewIsDocument ? _fitDocumentWidth : _fitImageWidth;
        double scale = Math.Min(width / image.Width, 2);
        if (!fitWidth) scale = Math.Min(scale, height / image.Height);
        AttachmentPreviewImage.Width = Math.Floor(image.Width * scale);
        AttachmentPreviewImage.Height = Math.Floor(image.Height * scale);
    }

    private void ChangeAttachmentPreviewFit(object sender, RoutedEventArgs e)
    {
        if (_previewIsDocument) _fitDocumentWidth = !_fitDocumentWidth;
        else _fitImageWidth = !_fitImageWidth;
        AttachmentPreviewScroll.ScrollToHome();
        FitAttachmentPreviewImage();
        RefreshAttachmentPreviewText();
    }

    private void ToggleAttachmentPreviewExpansion(object sender, RoutedEventArgs e)
    {
        SetAttachmentPreviewExpansion(!_expandAttachmentPreview);
        if (!_expandAttachmentPreview) FocusAttachmentSelection();
    }

    private void SetAttachmentPreviewExpansion(bool expanded)
    {
        _expandAttachmentPreview = expanded;
        SynchronizeNarrowReader();
        UpdateAttachmentLayout();
        RefreshAttachmentPreviewText();
    }

    private bool TryCloseExpandedAttachmentPreview()
    {
        if (!IsAttachmentPreviewExpanded) return false;
        SetAttachmentPreviewExpansion(false);
        FocusAttachmentSelection();
        return true;
    }

    private void RefreshAttachmentPreviewText()
    {
        if (AttachmentPreviewStatus is null) return;
        AttachmentInfoToggle.Header = L("파일 정보", "File information");
        AutomationProperties.SetName(AttachmentPicker, L("첨부파일 선택", "Choose attachment"));
        AutomationProperties.SetName(AttachmentPreviewSplitter, L("첨부 목록과 미리보기 너비 조절", "Resize the attachment preview"));
        AttachmentPreviewFitButton.Visibility = AttachmentPreviewImage.Source is null ? Visibility.Collapsed : Visibility.Visible;
        bool fitWidth = _previewIsDocument ? _fitDocumentWidth : _fitImageWidth;
        AttachmentPreviewFitButton.Content = fitWidth
            ? (_previewIsDocument ? L("페이지 맞춤", "Fit page") : L("이미지 맞춤", "Fit image"))
            : L("너비 맞춤", "Fit width");
        AttachmentPreviewFitButton.Tag = FindResource(fitWidth ? "Preview.FitWhole" : "Preview.FitWidth");
        AttachmentPreviewExpandButton.Content = _expandAttachmentPreview
            ? L("목록과 함께 보기 (Esc)", "Show list (Esc)") : L("크게 보기", "Expand preview");
        AttachmentPreviewExpandButton.Tag = FindResource(_expandAttachmentPreview ? "Lucide.Minimize" : "Lucide.Maximize");
        AttachmentListStatus.Text = AttachmentPreviewStatus.Text = AttachmentPreviewStatusText(_previewState, _previewText, _previewIsDocument);
        AttachmentListStatus.Visibility = _attachmentLayout == AttachmentLayout.List && _previewState != PreviewState.Empty
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string AttachmentPreviewStatusText(PreviewState state, AttachmentTextPreview? previewText,
        bool document) => state switch
        {
            PreviewState.Loading => L("미리보기를 불러오는 중…", "Loading preview…"),
            PreviewState.Ready when previewText is { } text => AttachmentTextStatus(text),
            PreviewState.Ready when document => L("미리보기 · 첫 페이지", "Preview · first page"),
            PreviewState.Ready => L("미리보기", "Preview"),
            PreviewState.Unsupported => L("이 첨부파일 형식은 미리보기를 지원하지 않습니다.", "Preview is unavailable for this file format."),
            PreviewState.Unreadable => L("첨부파일을 미리보기로 표시하지 못했습니다.", "This attachment could not be rendered."),
            PreviewState.Limit => L("첨부파일이 미리보기 판독 한도를 초과했습니다.", "This attachment exceeded a preview limit."),
            PreviewState.Unavailable => L("격리된 미리보기를 시작하지 못했습니다.", "The isolated preview could not start."),
            PreviewState.TimedOut => L("미리보기 처리 시간이 초과되었습니다.", "Preview rendering timed out."),
            PreviewState.PasswordRequired => L("이 첨부파일은 암호가 필요합니다.", "This attachment requires a password."),
            _ => ""
        };

    private static string AttachmentTextStatus(AttachmentTextPreview text)
    {
        if (text.Kind != AttachmentTextKind.Plain)
        {
            string label = text.Kind == AttachmentTextKind.Archive
                ? (text.Text.Length == 0 ? L("빈 압축 파일", "Empty archive") : L("압축 파일 목록", "Archive contents"))
                : (text.Text.Length == 0 ? L("표시할 텍스트가 없습니다.", "No text to display.") : L("문서 텍스트", "Document text"));
            if (text.Encrypted) label += L(" · 암호로 보호됨", " · Password protected");
            if (text.Truncated) label += L(" · 일부만 표시", " · Partial preview");
            return label;
        }
        if (text.BytesShown == 0) return L("비어 있는 파일입니다.", "This file is empty.");
        string mode = text.EncodingName is { } encoding
            ? L($"텍스트 · {encoding}", $"Text · {encoding}")
            : L("문자 보기 · ·는 표시할 수 없는 바이트", "Character view · · marks a non-printable byte");
        return text.Truncated ? mode + L($" · 처음 {text.BytesShown:N0}바이트", $" · first {text.BytesShown:N0} bytes") : mode;
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelBodyAttachmentPreview();
        CancelAttachmentPreview();
        _attachmentRenderer.Dispose();
        DisposeHtmlBody();
        DisposeRowThumbnails();
        base.OnClosed(e);
    }
}
