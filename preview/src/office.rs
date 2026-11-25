use std::fmt::Write as _;
use std::io::Cursor;

use office_oxide::{
    DocumentFormat,
    ir::{Element, InlineContent, Section, inline_to_text},
};

use crate::{Frame, Result, Status, VisualKind, decode_image, render_docx, render_svg};

fn read_office(data: Vec<u8>, format: DocumentFormat) -> Result<office_oxide::Document> {
    let mut options = office_oxide::options::ParseOptions::default();
    options.limits.package_bytes = crate::MAX_ZIP_DATA;
    options.limits.part_bytes = 24 * 1024 * 1024;
    options.limits.memory_bytes = 64 * 1024 * 1024;
    options.limits.embedded_depth = 0;
    options.limits.embedded_packages = 0;
    office_oxide::Document::from_reader_with(Cursor::new(data), format, &options).map_err(|error| {
        match error {
            office_oxide::OfficeError::Ppt(office_oxide::ppt::PptError::Encrypted)
            | office_oxide::OfficeError::Xls(office_oxide::xls::XlsError::Encrypted) => {
                Status::PasswordRequired
            }
            _ => Status::Unreadable,
        }
    })
}

fn render_legacy_doc(data: &[u8]) -> Result<Frame> {
    let doc = rwml::Document::open(data).map_err(|error| match error {
        rwml::Error::Encrypted { .. } => Status::PasswordRequired,
        _ => Status::Unreadable,
    })?;
    let docx = rwml::try_write_docx(&doc.model()).map_err(|_| Status::Unreadable)?;
    render_docx(&docx)
}

fn render_legacy_slide(data: Vec<u8>) -> Result<Frame> {
    let document = read_office(data, DocumentFormat::Ppt)?;
    let mut ir = document.to_ir();
    ir.sections.truncate(1);
    let mut bytes = Cursor::new(Vec::new());
    office_oxide::create::create_from_ir_to_writer(&ir, DocumentFormat::Pptx, &mut bytes)
        .map_err(|_| Status::Unreadable)?;
    render_slide(bytes.get_ref())
}

pub fn render(data: Vec<u8>, format: DocumentFormat) -> Result<Frame> {
    match format {
        DocumentFormat::Pptx => render_slide(&data),
        DocumentFormat::Doc => render_legacy_doc(&data),
        DocumentFormat::Ppt => render_legacy_slide(data),
        DocumentFormat::Xlsx => {
            let document = read_office(data, format)?;
            let sheet = document
                .as_xlsx()
                .and_then(|book| book.sheets_ir().next())
                .ok_or(Status::Unreadable)?;
            render_sheet(&sheet.section)
        }
        DocumentFormat::Xls => {
            let document = read_office(data, format)?;
            let ir = document.to_ir();
            render_sheet(ir.sections.first().ok_or(Status::Unreadable)?)
        }
        _ => Err(Status::Unsupported),
    }
}

fn render_slide(data: &[u8]) -> Result<Frame> {
    let deck = rpptx::Presentation::from_bytes(data).map_err(|_| Status::Unreadable)?;
    let longest = deck
        .slide_size()
        .map_or(12_192_000, |(width, height)| width.0.max(height.0));
    if longest <= 0 {
        return Err(Status::Unreadable);
    }
    let dpi = (1536.0 * 914_400.0 / longest as f64).min(144.0);
    let font_dir =
        std::env::var_os("SystemRoot").map(|dir| std::path::PathBuf::from(dir).join("Fonts"));
    let fonts: Vec<_> = [
        "arial.ttf",
        "arialbd.ttf",
        "calibri.ttf",
        "calibrib.ttf",
        "malgun.ttf",
    ]
    .into_iter()
    .filter_map(|name| Some((name, std::fs::read(font_dir.as_ref()?.join(name)).ok()?)))
    .collect();
    let fonts: Vec<_> = fonts
        .iter()
        .map(|(name, bytes)| (*name, bytes.as_slice()))
        .collect();
    let (png, _) = deck
        .render_slide_to_png_with_diagnostics(0, dpi, &fonts)
        .map_err(|_| Status::Unreadable)?;
    decode_image(
        &png.ok_or(Status::Unreadable)?,
        image::ImageFormat::Png,
        VisualKind::Slide,
    )
}

const SHEET_COLUMNS: usize = 10;
const SHEET_ROWS: usize = 32;

fn render_sheet(sheet: &Section) -> Result<Frame> {
    let table = sheet.elements.iter().find_map(|element| match element {
        Element::Table(table) => Some(table),
        _ => None,
    });
    let column_count = table
        .map_or(0, |table| {
            table
                .rows
                .iter()
                .take(SHEET_ROWS)
                .map(|row| {
                    row.cells
                        .iter()
                        .take(SHEET_COLUMNS)
                        .map(|cell| cell.col_span.max(1) as usize)
                        .sum()
                })
                .max()
                .unwrap_or(0)
        })
        .clamp(4, SHEET_COLUMNS);

    let mut columns = vec![0u32];
    for col in 0..column_count {
        let width = table
            .and_then(|table| table.column_widths_twips.get(col))
            .map_or(128, |twips| (twips / 15).clamp(64, 206));
        columns.push(columns[col] + width);
    }
    let width = columns[column_count];
    let row_count = table
        .map_or(0, |table| table.rows.len())
        .clamp(6, SHEET_ROWS);
    let heights: Vec<_> = (0..row_count)
        .map(|index| {
            table
                .and_then(|table| table.rows.get(index)?.height_twips)
                .map_or(25, |twips| (twips / 15).clamp(22, 64))
        })
        .collect();
    let height = 38 + heights.iter().sum::<u32>();
    let mut svg = format!(
        r#"<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" font-family="Arial, Malgun Gothic, sans-serif">"#
    );
    let tab = escape(sheet.title.as_deref().unwrap_or("").chars().take(150));
    let _ = write!(
        svg,
        r##"<rect width="{width}" height="38" fill="#f0f4f1"/><text x="10" y="25" font-size="15" font-weight="bold" fill="#185c37">{tab}</text>"##
    );
    let mut y = 38;
    let mut covered_until = [0; SHEET_COLUMNS];
    for (row_index, &height) in heights.iter().enumerate() {
        let mut col = 0;
        let cells = table
            .and_then(|table| table.rows.get(row_index))
            .map(|row| row.cells.as_slice())
            .unwrap_or_default();
        for cell in cells.iter().map(Some).chain(std::iter::repeat(None)) {
            while col < column_count && covered_until[col] > row_index {
                col += 1;
            }
            if col >= column_count {
                break;
            }
            let span = cell.map_or(1, |cell| cell.col_span.max(1) as usize);
            let end = col.saturating_add(span).min(column_count);
            let row_end = row_index
                .saturating_add(cell.map_or(1, |cell| cell.row_span.max(1) as usize))
                .min(row_count);
            covered_until[col..end].fill(row_end);
            let cell_height: u32 = heights[row_index..row_end].iter().sum();
            let (x, cell_width) = (columns[col], columns[end] - columns[col]);
            let background = cell
                .and_then(|cell| cell.background_color)
                .unwrap_or([255; 3]);
            let [r, g, b] = background;
            let _ = write!(
                svg,
                r##"<rect x="{x}" y="{y}" width="{cell_width}" height="{cell_height}" fill="#{r:02x}{g:02x}{b:02x}" stroke="#d5dad6"/>"##
            );
            if let Some(cell) = cell {
                let paragraph = cell.content.iter().find_map(|element| match element {
                    Element::Paragraph(paragraph) => Some(paragraph),
                    _ => None,
                });
                if let Some(paragraph) = paragraph {
                    let text = inline_to_text(&paragraph.content);
                    let text = escape(text.lines().next().unwrap_or_default().chars().take(180));
                    let style = paragraph.content.iter().find_map(|item| match item {
                        InlineContent::Text(span) => Some(span),
                        _ => None,
                    });
                    let [r, g, b] = style.and_then(|span| span.color).unwrap_or([30, 35, 32]);
                    let bold = if style.is_some_and(|span| span.bold) {
                        "bold"
                    } else {
                        "normal"
                    };
                    let font_style = if style.is_some_and(|span| span.italic) {
                        "italic"
                    } else {
                        "normal"
                    };
                    let clip = format!("cell-{row_index}-{col}");
                    let left = x + 5;
                    let top = y + height.min(25) - 7;
                    let _ = write!(
                        svg,
                        r##"<clipPath id="{clip}"><rect x="{x}" y="{y}" width="{cell_width}" height="{cell_height}"/></clipPath><text x="{left}" y="{top}" font-size="14" font-weight="{bold}" font-style="{font_style}" fill="#{r:02x}{g:02x}{b:02x}" clip-path="url(#{clip})">{text}</text>"##
                    );
                }
            }
            col = end;
        }
        y += height;
    }
    svg.push_str("</svg>");
    render_svg(&svg, VisualKind::Sheet)
}

fn escape(chars: impl Iterator<Item = char>) -> String {
    let mut text = String::new();
    for ch in chars {
        match ch {
            '&' => text.push_str("&amp;"),
            '<' => text.push_str("&lt;"),
            '>' => text.push_str("&gt;"),
            ch if ch.is_control() => text.push(' '),
            ch => text.push(ch),
        }
    }
    text
}
