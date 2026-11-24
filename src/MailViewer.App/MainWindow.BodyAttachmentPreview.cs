using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MailViewer.Core;

namespace MailViewer.App;

public partial class MainWindow
{
    private sealed record BodyAttachmentOption(MailAttachment Attachment, string Label);

    private CancellationTokenSource? _bodyAttachmentWork;
    private (EvidenceSession Session, string Record, string Attachment)? _bodyAttachmentKey;
    private PreparedAttachmentPreview _bodyAttachmentContent = new(PreviewState.Empty);
    private IReadOnlyList<MailAttachment>? _bodyAttachments;
    private MailAttachment? SelectedBodyAttachment => (BodyAttachmentSelector.SelectedItem as BodyAttachmentOption)?.Attachment;

    private void RefreshBodyAttachmentStrip(IReadOnlyList<MailAttachment> attachments)
    {
        if (!ReferenceEquals(_bodyAttachments, attachments))
        {
            _bodyAttachments = attachments;
            var duplicateNames = attachments.GroupBy(attachment => attachment.Name, StringComparer.Ordinal)
                .Where(group => group.Count() > 1).ToDictionary(group => group.Key, _ => 0, StringComparer.Ordinal);
            BodyAttachmentOption[] options = attachments.Select(attachment =>
            {
                string name = attachment.Name;
                if (duplicateNames.TryGetValue(name, out int index))
                {
                    duplicateNames[name] = index + 1;
                    name = $"{name} ({index + 1})";
                }
                return new BodyAttachmentOption(attachment, name);
            }).ToArray();
            BodyAttachmentSelector.ItemsSource = options;
            BodyAttachmentSelector.SelectedItem = options.FirstOrDefault(option =>
                AttachmentPreviewAvailability(option.Attachment) == PreviewState.Ready) ?? options.FirstOrDefault();
        }
        BodyAttachmentStrip.Visibility = attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BodyAttachmentSelector.Visibility = attachments.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(BodyAttachmentSelector, L("미리 볼 첨부파일", "Attachment to preview"));
        RefreshBodyAttachmentPreview();
        UpdateBodyAttachmentPreviewStatus();
    }

    private void OnBodyAttachmentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, BodyAttachmentSelector)) RefreshBodyAttachmentPreview();
    }

    private void OnBodyAttachmentPreviewVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        RefreshBodyAttachmentPreview();

    private void RefreshBodyAttachmentPreview()
    {
        if (BodyAttachmentPreview is null) return;
        if (!BodyAttachmentStrip.IsVisible || _session is not { } session || _activeRow is not { } row ||
            SelectedBodyAttachment is not { } attachment)
        {
            CancelBodyAttachmentPreview();
            _bodyAttachmentKey = null;
            SetBodyAttachmentPreview(new(PreviewState.Empty));
            return;
        }

        var key = (session, row.Record.Id, attachment.Id);
        if (_bodyAttachmentKey == key) return;
        CancelBodyAttachmentPreview();
        _bodyAttachmentKey = key;
        PreviewState availability = AttachmentPreviewAvailability(attachment);
        if (availability != PreviewState.Ready)
        {
            SetBodyAttachmentPreview(new(availability));
            return;
        }
        var work = new CancellationTokenSource();
        _bodyAttachmentWork = work;
        SetBodyAttachmentPreview(new(PreviewState.Loading));
        _ = LoadBodyAttachmentPreviewAsync(session, row.Record.Id, attachment, work);
    }

    private async Task LoadBodyAttachmentPreviewAsync(EvidenceSession session, string recordId,
        MailAttachment attachment, CancellationTokenSource work)
    {
        try
        {
            PreparedAttachmentPreview preview = await PrepareAttachmentPreviewAsync(session, recordId, attachment, work.Token);
            if (!ReferenceEquals(_bodyAttachmentWork, work) || !BodyAttachmentStrip.IsVisible) return;
            if (preview.Image is not null) AutomationProperties.SetName(BodyAttachmentPreviewImage, attachment.Name);
            SetBodyAttachmentPreview(preview);
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_bodyAttachmentWork, work)) _bodyAttachmentWork = null;
            work.Dispose();
        }
    }

    private void SetBodyAttachmentPreview(PreparedAttachmentPreview preview)
    {
        _bodyAttachmentContent = preview;
        BodyAttachmentPreviewImage.Source = preview.Image;
        BodyAttachmentPreviewImage.Visibility = preview.Image is null ? Visibility.Collapsed : Visibility.Visible;
        string text = preview.Text?.Text ?? "";
        BodyAttachmentPreviewText.Text = text.Length > 400 ? text[..400] + "…" : text;
        BodyAttachmentPreviewText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        BodyAttachmentPreview.Visibility = preview.State == PreviewState.Empty ? Visibility.Collapsed : Visibility.Visible;
        UpdateBodyAttachmentPreviewStatus();
    }

    private void UpdateBodyAttachmentPreviewStatus()
    {
        var preview = _bodyAttachmentContent;
        string status = preview.State == PreviewState.Ready && (BodyAttachmentPreviewText.Visibility == Visibility.Visible ||
            preview.Image is not null && !preview.IsDocument) ? "" :
            AttachmentPreviewStatusText(preview.State, preview.Text, preview.IsDocument);
        BodyAttachmentPreviewStatus.Text = status;
        BodyAttachmentPreviewStatus.Visibility = status.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CancelBodyAttachmentPreview()
    {
        _bodyAttachmentWork?.Cancel();
        _bodyAttachmentWork = null;
    }
}
