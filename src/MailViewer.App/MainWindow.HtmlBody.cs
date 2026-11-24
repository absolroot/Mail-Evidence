using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using MailViewer.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MailViewer.App;

public partial class MainWindow
{
    private enum HtmlViewState { None, Loading, Ready, ImageLoading, ImagesLoaded, Unavailable }

    private (EvidenceSession Session, string RecordId, string PartPath)? _htmlBodyKey;
    private WebView2? _htmlView;
    private Task<CoreWebView2>? _initializeHtmlView;
    private CancellationTokenSource? _htmlWork;
    private HtmlBodyPreview? _htmlPreview;
    private IReadOnlyDictionary<string, HtmlBodyPreview.ImageData>? _htmlImages;
    private ulong? _htmlNavigation;
    private HtmlViewState _htmlState;
    private bool _linkWarningPending;

    private void OnHtmlBodyVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (BodyRenderSurface.IsVisible) UpdateHtmlBody();
    }

    private void UpdateHtmlBody()
    {
        var part = (BodyRepresentationBox.SelectedItem as Choice<MailBodyPart>)?.Value;
        (EvidenceSession, string, string)? key =
            part?.MediaType == "text/html" && _session is { } session && _activeRow?.Record is { } record
                ? (session, record.Id, part.PartPath) : null;
        if (_htmlBodyKey != key)
        {
            _htmlWork?.Cancel();
            _htmlWork?.Dispose();
            _htmlWork = null;
            _htmlBodyKey = key;
            _htmlPreview = null;
            _htmlImages = null;
            _htmlNavigation = null;
            _htmlState = HtmlViewState.None;
            ShowPlainBodySurface();
        }
        if (key is { } current && _htmlState == HtmlViewState.None && BodyRenderSurface.IsVisible)
        {
            _htmlWork = new CancellationTokenSource();
            _ = RenderHtmlBodyAsync(current.Item1, current.Item2, current.Item3, _htmlWork.Token);
        }
        RefreshHtmlBodyStatus();
    }

    private async Task RenderHtmlBodyAsync(EvidenceSession session, string recordId, string partPath, CancellationToken ct)
    {
        _htmlState = HtmlViewState.Loading;
        RefreshHtmlBodyStatus();
        try
        {
            HtmlBodyPreview? preview = await Task.Run(() =>
            {
                string? html = session.ReadHtmlBody(recordId, partPath, ct);
                return html is null ? null : HtmlBodyPreview.Create(html, session, recordId, ct);
            }, ct);
            ct.ThrowIfCancellationRequested();
            if (preview is null)
            {
                ShowHtmlFallback();
                return;
            }
            BodyHtmlHost.Visibility = Visibility.Visible;
            var view = await (_initializeHtmlView ??= InitializeHtmlViewAsync());
            ct.ThrowIfCancellationRequested();
            _htmlPreview = preview;
            _htmlImages = preview.LocalImages;
            view.Navigate(preview.BodyUri);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) when (error is WebView2RuntimeNotFoundException or COMException or Win32Exception or
            InvalidOperationException or IOException or FormatException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            if (!ct.IsCancellationRequested) ShowHtmlFallback();
        }
    }

    private async Task<CoreWebView2> InitializeHtmlViewAsync()
    {
        var browser = new WebView2
        {
            AllowDrop = false,
            DefaultBackgroundColor = System.Drawing.Color.White
        };
        AutomationProperties.SetName(browser, L("HTML 본문", "HTML message body"));
        BodyHtmlHost.Child = _htmlView = browser;
        var options = new CoreWebView2EnvironmentOptions("--disable-background-networking --dns-prefetch-disable");
        string profilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Absolroot", "MailEvidence", "BodyPreview");
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profilePath, options: options);
        var controller = environment.CreateCoreWebView2ControllerOptions();
        controller.IsInPrivateModeEnabled = true;
        await browser.EnsureCoreWebView2Async(environment, controller);

        var view = browser.CoreWebView2;
        view.Settings.IsScriptEnabled = false;
        view.Settings.IsWebMessageEnabled = false;
        view.Settings.AreHostObjectsAllowed = false;
        view.Settings.AreDefaultScriptDialogsEnabled = false;
        view.Settings.AreDevToolsEnabled = false;
        view.Settings.AreDefaultContextMenusEnabled = false;
        view.Settings.AreBrowserAcceleratorKeysEnabled = false;
        view.Settings.IsGeneralAutofillEnabled = false;
        view.Settings.IsPasswordAutosaveEnabled = false;
        view.NavigationStarting += (_, args) =>
        {
            args.Cancel = !string.Equals(args.Uri, _htmlPreview?.BodyUri, StringComparison.Ordinal);
            if (!args.Cancel) _htmlNavigation = args.NavigationId;
            else if (args.IsUserInitiated) QueueLinkWarning(args.Uri);
        };
        view.NavigationCompleted += (_, args) =>
        {
            if (_htmlNavigation != args.NavigationId || _htmlPreview is null) return;
            if (!args.IsSuccess)
            {
                ShowHtmlFallback();
                return;
            }
            if (_htmlState == HtmlViewState.Loading) _htmlState = HtmlViewState.Ready;
            if (BodyBox.IsKeyboardFocusWithin && _htmlView?.Focus() != true && BodyBox.IsKeyboardFocusWithin)
                ReaderSubject.Focus();
            BodyBox.Visibility = Visibility.Collapsed;
            RefreshHtmlBodyStatus();
        };
        view.FrameNavigationStarting += (_, args) => args.Cancel = true;
        view.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (args.IsUserInitiated) QueueLinkWarning(args.Uri);
        };
        view.DownloadStarting += (_, args) => { args.Cancel = true; args.Handled = true; };
        view.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        view.ProcessFailed += (_, _) => { if (_htmlBodyKey is not null) ShowHtmlFallback(); };
        view.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        view.WebResourceRequested += (_, args) =>
        {
            const string responseHeaders = "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer";
            if (args.Request.Method == "GET" && _htmlPreview is { } preview)
            {
                if (args.ResourceContext == CoreWebView2WebResourceContext.Document && args.Request.Uri == preview.BodyUri)
                {
                    args.Response = environment.CreateWebResourceResponse(new MemoryStream(preview.Html, writable: false), 200, "OK",
                        $"{responseHeaders}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Security-Policy: {HtmlBodyPreview.ContentPolicy}");
                    return;
                }
                if (args.ResourceContext == CoreWebView2WebResourceContext.Image &&
                    _htmlImages?.TryGetValue(args.Request.Uri, out var image) == true)
                {
                    args.Response = environment.CreateWebResourceResponse(new MemoryStream(image.Bytes, writable: false), 200, "OK",
                        $"{responseHeaders}\r\nContent-Type: {image.MediaType}");
                    return;
                }
            }
            args.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", responseHeaders);
        };
        return view;
    }

    private void QueueLinkWarning(string previewUri)
    {
        if (_linkWarningPending || _htmlPreview is not { } preview ||
            !preview.TryGetLink(previewUri, out var target) || target is null) return;
        _linkWarningPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            try
            {
                if (ReferenceEquals(_htmlPreview, preview) && BodyHtmlHost.IsVisible) ShowLinkWarning(target);
            }
            finally { _linkWarningPending = false; }
        }));
    }

    private void ShowLinkWarning(HtmlBodyPreview.LinkTarget target)
    {
        var dialog = new Window
        {
            Owner = this, Title = L("메일 링크 주의", "Email link warning"), Width = 480,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FontFamily = FontFamily, Language = Language
        };
        Appearance.ApplyToWindow(dialog);
        dialog.SetResourceReference(BackgroundProperty, "Viewer.Canvas");
        dialog.SetResourceReference(ForegroundProperty, "Viewer.Text");
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = L("외부 링크입니다. 접속하기 전에 목적지 주소를 확인하세요.",
                "This is an external link. Check its destination before visiting it."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        });
        panel.Children.Add(new TextBlock { Text = target.Host, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold });
        var address = new TextBox
        {
            Text = target.Address, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 120,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 12),
            FlowDirection = FlowDirection.LeftToRight
        };
        AutomationProperties.SetName(address, L("목적지 주소", "Destination address"));
        AutomationProperties.SetAutomationId(address, "MailLinkAddress");
        address.SetResourceReference(BackgroundProperty, "Viewer.Panel");
        address.SetResourceReference(ForegroundProperty, "Viewer.Text");
        address.SetResourceReference(BorderBrushProperty, "Viewer.Rule");
        panel.Children.Add(address);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var copy = new Button { Content = L("주소 복사", "Copy address"), Style = (Style)FindResource("Action") };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(target.Address); }
            catch (ExternalException)
            {
                address.Focus();
                address.SelectAll();
                return;
            }
            dialog.Close();
        };
        var close = new Button { Content = L("닫기", "Close"), IsDefault = true, IsCancel = true, Style = (Style)FindResource("PrimaryAction") };
        actions.Children.Add(copy);
        actions.Children.Add(close);
        panel.Children.Add(actions);
        var surface = new Border { Padding = new Thickness(20), Child = panel };
        surface.SetResourceReference(Border.BackgroundProperty, "Viewer.Canvas");
        dialog.Content = surface;
        dialog.Loaded += (_, _) => close.Focus();
        dialog.ShowDialog();
        if (BodyHtmlHost.IsVisible) _htmlView?.Focus();
    }

    private async void ShowExternalBodyImages(object sender, RoutedEventArgs e)
    {
        if (_htmlPreview is not { RemoteImageCount: > 0 } preview || _htmlState != HtmlViewState.Ready ||
            _htmlWork is not { } work || _htmlView?.CoreWebView2 is not { } view)
            return;
        if (System.Windows.MessageBox.Show(this,
                L("현재 메일의 이미지를 외부 서버에서 가져옵니다. 서버가 이미지 요청을 기록할 수 있습니다. 표시하시겠습니까?",
                    "Load images for this message from external servers? The servers may record those requests."),
                L("외부 이미지 표시", "Show external images"), MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (!ReferenceEquals(_htmlPreview, preview) || !ReferenceEquals(_htmlWork, work)) return;

        CancellationToken ct = work.Token;
        _htmlState = HtmlViewState.ImageLoading;
        RefreshHtmlBodyStatus();
        try
        {
            var images = await preview.LoadRemoteImagesAsync(ct);
            ct.ThrowIfCancellationRequested();
            _htmlImages = images;
            _htmlState = HtmlViewState.ImagesLoaded;
            RefreshHtmlBodyStatus();
            if (images.Count > preview.LocalImages.Count)
            {
                view.Navigate(preview.BodyUri);
                if (_activeRow is { ListThumbnail: null } row)
                {
                    var image = images.FirstOrDefault(item => !preview.LocalImages.ContainsKey(item.Key) &&
                        item.Value.MediaType is "image/jpeg" or "image/png").Value;
                    if (image is not null) _ = RememberApprovedThumbnailAsync(row, image.Bytes, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            if (!ct.IsCancellationRequested) ShowHtmlFallback();
        }
    }

    private void ShowHtmlFallback()
    {
        _htmlState = HtmlViewState.Unavailable;
        ShowPlainBodySurface();
        RefreshHtmlBodyStatus();
    }

    private void ShowPlainBodySurface()
    {
        BodyBox.Visibility = Visibility.Visible;
        if (_htmlView?.IsKeyboardFocusWithin == true && BodyBox.IsVisible)
            BodyBox.Focus();
        BodyHtmlHost.Visibility = Visibility.Collapsed;
    }

    private void RefreshHtmlBodyStatus()
    {
        if (BodySecurityBar is null) return;
        BodyExternalImagesButton.Content = L("외부 이미지 표시…", "Show external images…");
        if (_htmlView is { } browser) AutomationProperties.SetName(browser, L("HTML 본문", "HTML message body"));
        var preview = _htmlPreview;
        string? status = _htmlState switch
        {
            HtmlViewState.Loading => L("HTML 미리보기를 불러오는 중…", "Loading HTML preview…"),
            HtmlViewState.Unavailable => L("HTML 미리보기를 열 수 없어 본문을 텍스트로 표시합니다.", "HTML preview is unavailable. The body is displayed as text."),
            HtmlViewState.ImageLoading => L("외부 이미지를 불러오는 중…", "Loading external images…"),
            HtmlViewState.ImagesLoaded when preview is not null => LoadedImageStatus(preview),
            HtmlViewState.Ready when preview is { RemoteImageCount: > 0 } =>
                L("외부 이미지와 스크립트가 차단되었습니다.", "External images and scripts are blocked."),
            HtmlViewState.Ready when preview is { ActiveContentRemoved: true } or { OtherResourcesBlocked: true } =>
                L("스크립트와 일부 콘텐츠가 차단되었습니다.", "Scripts and some content are blocked."),
            _ => null
        };
        BodySecurityText.Text = status ?? "";
        BodySecurityBar.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
        BodyExternalImagesButton.Visibility = _htmlState == HtmlViewState.Ready && preview is { RemoteImageCount: > 0 }
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private string LoadedImageStatus(HtmlBodyPreview preview)
    {
        int count = (_htmlImages?.Count ?? 0) - preview.LocalImages.Count;
        return count == preview.RemoteImageCount ? L("외부 이미지를 표시했습니다. 스크립트는 차단됩니다.", "External images shown. Scripts remain blocked.")
            : L($"외부 이미지 {preview.RemoteImageCount}개 중 {count}개를 표시했습니다. 스크립트는 차단됩니다.",
                $"Loaded {count} of {preview.RemoteImageCount} external images. Scripts remain blocked.");
    }

    private void DisposeHtmlBody()
    {
        _htmlWork?.Cancel();
        _htmlWork?.Dispose();
        _htmlView?.Dispose();
    }
}

