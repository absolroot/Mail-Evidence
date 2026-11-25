using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MailViewer.App;

namespace MailViewer.FlowSmoke;

public static partial class Program
{
    private static async Task RunReaderAttachments(string workspace, string output, string? theme)
    {
        string folder = Path.Combine(workspace, "Mail-Viewer", ".local", "samples", "everyday-mail", "messages");
        string[] inputs = ["15-long-subject.eml", "14-same-name-attachments.eml", "05-table-reservation.eml", "01-order-receipt.eml"];
        string[] paths = inputs.Select(name => Path.Combine(folder, name)).ToArray();
        var window = new MainWindow(paths, Path.Combine(output, "marks"))
        {
            ShowInTaskbar = false,
            Width = 1020,
            Height = 650
        };
        window.Show();
        await Idle();
        ClickMenu(window, "KoreanLanguageMenuItem");
        if (theme is not null) ClickMenu(window, theme == "--dark" ? "DarkThemeMenuItem" : "LightThemeMenuItem");
        Click(window, "AnalyzeButton");

        var mail = Field<DataGrid>(window, "ResultsGrid");
        await WaitFor(() => mail.Items.Count == inputs.Length, "everyday mail fixtures");
        int IndexFor(string name) => Enumerable.Range(0, mail.Items.Count)
            .Single(index => Path.GetFileName(RecordAt(mail, index).SourcePath) == name);

        var details = Field<TabControl>(window, "DetailTabs");
        var bodyTab = Field<TabItem>(window, "BodyTab");
        var attachmentsTab = Field<TabItem>(window, "AttachmentsTab");
        var attachments = Field<DataGrid>(window, "AttachmentsGrid");
        var summary = Field<Button>(window, "BodyAttachmentsButton");
        var count = Field<TextBlock>(window, "BodyAttachmentCount");
        var name = Field<TextBlock>(window, "BodyAttachmentName");
        var body = Field<TextBox>(window, "BodyBox");
        var bodySurface = Field<Grid>(window, "BodyRenderSurface");
        var footer = Field<Border>(window, "BodyAttachmentStrip");
        var previewText = Field<TextBlock>(window, "BodyAttachmentPreviewText");
        var previewImage = Field<Image>(window, "BodyAttachmentPreviewImage");
        var picker = Field<ComboBox>(window, "BodyAttachmentSelector");
        int longMail = IndexFor(inputs[0]);
        mail.SelectedIndex = longMail;
        await Idle();
        string fullName = RecordAt(mail, longMail).Attachments.Single().Name;
        var glyph = (FrameworkElement)summary.Template.FindName("Glyph", summary);
        Rect bounds = name.TransformToAncestor(summary).TransformBounds(new Rect(name.RenderSize));
        Require(details.SelectedItem == bodyTab && summary.IsVisible && glyph.ActualWidth > 0 &&
            count.Text == "첨부파일 1개" && name.Text == fullName && Equals(summary.ToolTip, fullName) &&
            name.TextTrimming == TextTrimming.CharacterEllipsis && bounds.Left > 0 &&
            bounds.Right <= summary.ActualWidth && body.Text.StartsWith("행사 안내 내용", StringComparison.Ordinal),
            "긴 첨부 이름이 본문과 구분되지 않거나 버튼 밖으로 잘립니다.");
        Require(!footer.IsKeyboardFocusWithin && KeyboardNavigation.GetTabIndex(bodySurface) < KeyboardNavigation.GetTabIndex(footer),
            "본문을 열면 첨부 도구에 포커스가 자동으로 잡히지 않아야 합니다.");
        FocusManager.SetFocusedElement(window, body);
        body.Focus();
        await WaitFor(() => previewText.IsVisible && previewText.Text.Length > 0, "본문의 텍스트 첨부 미리보기");
        Require(ReferenceEquals(FocusManager.GetFocusedElement(window), body) && !footer.IsKeyboardFocusWithin &&
            bodySurface.TranslatePoint(new Point(0, bodySurface.ActualHeight), window).Y <= footer.TranslatePoint(new Point(), window).Y,
            "첨부 미리보기 완료 후에도 본문 포커스가 유지되고, 미리보기는 본문 아래에 있어야 합니다.");
        Capture(window, output, "01-long-name.png");

        Click(window, "BodyAttachmentsButton");
        Require(details.SelectedItem == attachmentsTab &&
            ReferenceEquals(attachments.SelectedItem, RecordAt(mail, longMail).Attachments.Single()),
            "본문의 첨부 버튼이 해당 첨부를 열지 못했습니다.");
        mail.SelectedIndex = IndexFor(inputs[1]);
        Require(details.SelectedItem == attachmentsTab && attachments.Items.Count == 2,
            "첨부파일이 있는 다음 메일에서 첨부 탭이 유지되지 않습니다.");
        details.SelectedItem = bodyTab;
        ClickMenu(window, "EnglishLanguageMenuItem");
        await Idle();
        Require(summary.IsVisible && count.Text == "2 attachments" && !name.IsVisible,
            "영어의 첨부 개수 또는 여러 첨부의 표시가 올바르지 않습니다.");
        await WaitFor(() => previewImage is { IsVisible: true, Source: BitmapSource }, "본문의 첫 이미지 첨부");
        Require(picker.IsVisible && picker.Items.Count == 2 && previewImage.ActualHeight <= 94 &&
            picker.Items.Cast<object>().Select(option => option.GetType().GetProperty("Label")?.GetValue(option)).Distinct().Count() == 2,
            "여러 파일이 있는 본문의 파일 선택과 미리보기 높이가 올바르지 않습니다.");
        var first = previewImage.Source;
        picker.SelectedIndex = 1;
        await WaitFor(() => previewImage is { IsVisible: true, Source: BitmapSource } && previewImage.Source != first,
            "이름이 같은 두 번째 이미지 첨부");
        Require(details.SelectedItem == bodyTab && !footer.IsKeyboardFocusWithin, "미리보기에서 파일을 바꾸어도 본문에 머물러야 합니다.");
        Capture(window, output, "02-multiple-en.png");

        Click(window, "BodyAttachmentsButton");
        Require(ReferenceEquals(attachments.SelectedItem, RecordAt(mail, IndexFor(inputs[1])).Attachments[1]),
            "첨부 탭에서 본문 미리보기로 확인한 파일을 선택해야 합니다.");
        mail.SelectedIndex = IndexFor(inputs[2]);
        await Idle();
        Require(details.SelectedItem == bodyTab && !summary.IsVisible && !footer.IsVisible && body.Text.Length > 0 &&
            Equals(attachmentsTab.Header, "Attachments (0)"),
            "첨부가 없는 메일에 본문이 표시되지 않습니다.");
        Capture(window, output, "03-body-without-attachments.png");

        mail.SelectedIndex = IndexFor(inputs[3]);
        var record = RecordAt(mail, mail.SelectedIndex);
        picker.SelectedIndex = Enumerable.Range(0, record.Attachments.Count)
            .Single(index => record.Attachments[index].Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        var status = Field<TextBlock>(window, "BodyAttachmentPreviewStatus");
        await WaitFor(() => status.IsVisible && status.Text != "Loading preview…", "본문의 PDF 미리보기 완료");
        Require(previewImage.Source is BitmapSource && status.Text == "Preview · first page", $"본문의 PDF 첫 페이지 표시. {status.Text}");
        Capture(window, output, "04-document-inline.png");
        File.WriteAllText(Path.Combine(output, "flow-result.json"), JsonSerializer.Serialize(new
        {
            LongName = true, OpensSelectedAttachment = true, EnglishCount = true, BodyWithoutAttachments = true,
            InlineText = true, InlineImages = true, InlinePdf = true, BodyFocusPreserved = true
        }));
        window.Close();
    }
}
