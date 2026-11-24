using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MailViewer.Core;

namespace MailViewer.App;

public partial class MainWindow
{
    private const int RowImageLimit = 2 * 1024 * 1024;
    private const int RowThumbnailCacheLimit = 96;
    private readonly AttachmentPreviewRenderer _thumbnailRenderer = new();
    private readonly SemaphoreSlim _thumbnailGate = new(1);
    private readonly Dictionary<FrameworkElement, CancellationTokenSource> _thumbnailRequests = [];
    private readonly Dictionary<string, CachedThumbnail> _thumbnailCache = [];

    private sealed record RowThumbnail(BitmapSource Bitmap, string? AttachmentId);
    private sealed record CachedThumbnail(RowThumbnail? Value, WeakReference<ResultRow> Owner);

    private void OnRowThumbLoaded(object sender, RoutedEventArgs e) => QueueRowThumbnail((FrameworkElement)sender);
    private void OnRowThumbUnloaded(object sender, RoutedEventArgs e) => CancelRowThumbnail((FrameworkElement)sender);

    private void OnRowThumbContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var surface = (FrameworkElement)sender;
        if (surface.IsLoaded) QueueRowThumbnail(surface);
    }

    private void QueueRowThumbnail(FrameworkElement surface)
    {
        CancelRowThumbnail(surface);
        if (_session is not { } session || surface.DataContext is not ResultRow row) return;
        if (RestoreRowThumbnail(row)) return;

        MailBodyPart? html = row.Record.BodyParts.FirstOrDefault(part => part.MediaType == "text/html");
        MailAttachment? image = row.Record.Attachments.FirstOrDefault(item => item.DecodedLength is > 0 and <= RowImageLimit &&
            item.Issue is null && item.Sha256 is not null &&
            (item.MediaType is "image/png" or "image/jpeg" ||
             Path.GetExtension(item.Name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg"));
        if (html is null && image is null) return;
        var work = new CancellationTokenSource();
        _thumbnailRequests[surface] = work;
        _ = LoadRowThumbnailAsync(surface, row, session, html, image, work);
    }

    private async Task LoadRowThumbnailAsync(FrameworkElement surface, ResultRow row, EvidenceSession session,
        MailBodyPart? html, MailAttachment? image, CancellationTokenSource work)
    {
        CancellationToken ct = work.Token;
        bool entered = false;
        try
        {
            await Task.Delay(210, ct);
            await _thumbnailGate.WaitAsync(ct);
            entered = true;
            if (!ReferenceEquals(session, _session) || !ReferenceEquals(surface.DataContext, row)
                || RestoreRowThumbnail(row)) return;
            RowThumbnail? preview = await Task.Run(async () =>
            {
                byte[]? bytes = html is null ? null : HtmlBodyPreview.ReadFirstLocalImage(session, row.Record.Id, html.PartPath, ct);
                string? attachmentId = null;
                if (bytes is null && image is not null)
                {
                    bytes = session.ReadAttachment(row.Record.Id, image.Id, ct);
                    attachmentId = image.Id;
                }
                if (bytes is null) return null;
                PreviewResult result = await _thumbnailRenderer.RenderAsync(bytes, ct).ConfigureAwait(false);
                if (result.Frame is not { IsDocument: false } frame) return null;
                return new RowThumbnail(ScaleRowThumbnail(frame), attachmentId);
            }, ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(session, _session) || !ReferenceEquals(surface.DataContext, row))
                return;
            if (preview is not null || row.ListThumbnail is null) CacheRowThumbnail(row, preview);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or ObjectDisposedException or FormatException
            or ArgumentException or NotSupportedException or UnauthorizedAccessException or KeyNotFoundException)
        {
        }
        finally
        {
            if (entered) _thumbnailGate.Release();
            if (_thumbnailRequests.TryGetValue(surface, out var active) && ReferenceEquals(active, work))
                _thumbnailRequests.Remove(surface);
            work.Dispose();
        }
    }

    private bool RestoreRowThumbnail(ResultRow row)
    {
        if (!_thumbnailCache.TryGetValue(row.Record.Id, out var cached)) return false;
        if (!cached.Owner.TryGetTarget(out var owner) || !ReferenceEquals(owner, row))
            cached.Owner.SetTarget(row);
        row.SetListThumbnail(cached.Value);
        return true;
    }

    private void CacheRowThumbnail(ResultRow row, RowThumbnail? preview)
    {
        _thumbnailCache[row.Record.Id] = new(preview, new(row));
        if (_thumbnailCache.Count > RowThumbnailCacheLimit &&
            _thumbnailCache.Remove(_thumbnailCache.Keys.First(), out var removed) &&
            removed.Owner.TryGetTarget(out var owner) && ReferenceEquals(owner.ListThumbnail, removed.Value))
            owner.SetListThumbnail(null);
        row.SetListThumbnail(preview);
    }

    private async Task RememberApprovedThumbnailAsync(ResultRow row, byte[] bytes, CancellationToken ct)
    {
        bool entered = false;
        try
        {
            await _thumbnailGate.WaitAsync(ct);
            entered = true;
            if (row.ListThumbnail is not null || bytes.Length > RowImageLimit) return;
            BitmapSource? thumbnail = await Task.Run(async () =>
            {
                PreviewResult result = await _thumbnailRenderer.RenderAsync(bytes, ct).ConfigureAwait(false);
                return result.Frame is { IsDocument: false } frame ? ScaleRowThumbnail(frame) : null;
            }, ct);
            if (thumbnail is not null && !ct.IsCancellationRequested)
                CacheRowThumbnail(row, new RowThumbnail(thumbnail, null));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException) { }
        finally
        {
            if (entered) _thumbnailGate.Release();
        }
    }

    private static BitmapSource ScaleRowThumbnail(PreviewFrame frame)
    {
        var original = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Pbgra32,
            null, frame.Pixels, frame.Width * 4);
        double factor = Math.Min(1, Math.Min(168d / frame.Width, 108d / frame.Height));
        BitmapSource scaled = new TransformedBitmap(original, new ScaleTransform(factor, factor));
        int stride = scaled.PixelWidth * 4;
        var pixels = new byte[stride * scaled.PixelHeight];
        scaled.CopyPixels(pixels, stride, 0);
        var result = BitmapSource.Create(scaled.PixelWidth, scaled.PixelHeight, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private void CancelRowThumbnail(FrameworkElement surface)
    {
        if (_thumbnailRequests.Remove(surface, out var request)) request.Cancel();
    }

    private void DisposeRowThumbnails()
    {
        foreach (CancellationTokenSource request in _thumbnailRequests.Values) request.Cancel();
        _thumbnailRequests.Clear();
        _thumbnailCache.Clear();
        _thumbnailRenderer.Dispose();
    }

    private void OpenRowAttachment(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ResultRow row } button || !CanChooseSources) return;
        string? attachmentId = button.CommandParameter as string;
        ResultsGrid.SelectedItem = row;
        ShowMailboxReader(markViewed: false);
        if (!ReferenceEquals(_activeRow, row)) return;
        DetailTabs.SelectedItem = AttachmentsTab;
        if (attachmentId is not null)
            AttachmentsGrid.SelectedItem = row.Record.Attachments.FirstOrDefault(item => item.Id == attachmentId);
        FocusAttachmentSelection();
    }

    private void OpenRowThumbnail(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ResultRow row } button || !CanChooseSources) return;
        if (button.CommandParameter is string)
        {
            OpenRowAttachment(sender, e);
            return;
        }
        ResultsGrid.SelectedItem = row;
        if (ReferenceEquals(_activeRow, row)) DetailTabs.SelectedItem = BodyTab;
        ShowMailboxReader();
    }
}

