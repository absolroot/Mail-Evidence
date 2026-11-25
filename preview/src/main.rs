use std::io::{self, Cursor, Read, Write};

use hayro::{PixmapSettings, RenderCache, RenderSettings, hayro_interpret::InterpreterSettings};
use image::{ImageFormat, ImageReader, Limits};
use resvg::{tiny_skia, usvg};

mod listing;

const MAX_INPUT: u64 = 24 * 1024 * 1024;
const MAX_EDGE: u32 = 1600;
const MAX_ZIP_DATA: u64 = 96 * 1024 * 1024;
const CFB_MAGIC: &[u8] = b"\xd0\xcf\x11\xe0\xa1\xb1\x1a\xe1";

enum Format {
    Jpeg,
    Png,
    Pdf,
    Docx,
    Hwp,
    Office(office_oxide::DocumentFormat),
    Zip,
    Tar,
    SevenZip,
}
#[derive(Clone, Copy)]
enum Status {
    Unsupported = 1,
    Unreadable = 2,
    Limit = 3,
    PasswordRequired = 5,
}
type Result<T> = std::result::Result<T, Status>;

struct Frame {
    width: u32,
    height: u32,
    document: bool,
    pixels: Vec<u8>,
}

fn identify(data: &[u8]) -> Result<Format> {
    if data.starts_with(b"\xff\xd8\xff") {
        return Ok(Format::Jpeg);
    }
    if data.starts_with(b"\x89PNG\r\n\x1a\n") {
        return Ok(Format::Png);
    }
    if data.starts_with(b"%PDF-") {
        return Ok(Format::Pdf);
    }
    if data.starts_with(CFB_MAGIC) {
        let storage = cfb::CompoundFile::open(Cursor::new(data)).map_err(|_| Status::Unreadable)?;
        return if storage.is_stream("/FileHeader") && storage.is_storage("/BodyText") {
            Ok(Format::Hwp)
        } else if storage.is_stream("/EncryptionInfo") && storage.is_stream("/EncryptedPackage") {
            Err(Status::PasswordRequired)
        } else if storage.is_stream("/WordDocument") {
            Ok(Format::Office(office_oxide::DocumentFormat::Doc))
        } else if storage.is_stream("/PowerPoint Document") {
            Ok(Format::Office(office_oxide::DocumentFormat::Ppt))
        } else if storage.is_stream("/Workbook") || storage.is_stream("/Book") {
            Ok(Format::Office(office_oxide::DocumentFormat::Xls))
        } else {
            Err(Status::Unsupported)
        };
    }
    if data.starts_with(b"7z\xbc\xaf\x27\x1c") {
        return Ok(Format::SevenZip);
    }
    if listing::is_tar(data) {
        return Ok(Format::Tar);
    }
    if !data.starts_with(b"PK\x03\x04") && !data.starts_with(b"PK\x05\x06") {
        return Err(Status::Unsupported);
    }

    let mut archive = zip::ZipArchive::new(Cursor::new(data)).map_err(|_| Status::Unreadable)?;
    if archive.len() > 4096 {
        return Err(Status::Limit);
    }
    let format = if archive.index_for_name("[Content_Types].xml").is_some() {
        if archive.index_for_name("word/document.xml").is_some() {
            Format::Docx
        } else if archive.index_for_name("ppt/presentation.xml").is_some() {
            Format::Office(office_oxide::DocumentFormat::Pptx)
        } else if archive.index_for_name("xl/workbook.xml").is_some() {
            Format::Office(office_oxide::DocumentFormat::Xlsx)
        } else {
            Format::Zip
        }
    } else if archive.index_for_name("Contents/content.hpf").is_some()
        && archive.index_for_name("Contents/section0.xml").is_some()
    {
        Format::Hwp
    } else {
        Format::Zip
    };
    if matches!(format, Format::Zip) {
        return Ok(format);
    }
    let mut total = 0u64;
    for i in 0..archive.len() {
        let member = archive.by_index_raw(i).map_err(|_| Status::Unreadable)?;
        if member.encrypted() {
            return Err(Status::PasswordRequired);
        }
        total = total.checked_add(member.size()).ok_or(Status::Limit)?;
        if total > MAX_ZIP_DATA {
            return Err(Status::Limit);
        }
    }
    Ok(format)
}

fn scale(width: f32, height: f32) -> Result<f32> {
    if !width.is_finite() || !height.is_finite() || width <= 0.0 || height <= 0.0 {
        return Err(Status::Unreadable);
    }
    Ok((MAX_EDGE as f32 / width.max(height)).min(2.0))
}

fn decode_image(data: &[u8], format: ImageFormat, document: bool) -> Result<Frame> {
    let mut reader = ImageReader::with_format(Cursor::new(data), format);
    let mut limits = Limits::default();
    limits.max_image_width = Some(8192);
    limits.max_image_height = Some(8192);
    limits.max_alloc = Some(96 * 1024 * 1024);
    reader.limits(limits);
    let mut decoder = reader.into_decoder().map_err(image_status)?;
    let orientation = image::ImageDecoder::orientation(&mut decoder).map_err(image_status)?;
    let mut image = image::DynamicImage::from_decoder(decoder).map_err(image_status)?;
    image.apply_orientation(orientation);
    if image.width().max(image.height()) > MAX_EDGE {
        image = image.resize(MAX_EDGE, MAX_EDGE, image::imageops::FilterType::Triangle);
    }
    let mut image = image.into_rgba8();
    let (width, height) = image.dimensions();
    for pixel in image.as_mut().as_chunks_mut::<4>().0 {
        let alpha = u16::from(pixel[3]);
        for channel in pixel.iter_mut().take(3) {
            *channel = ((u16::from(*channel) * alpha + 127) / 255) as u8;
        }
    }
    Ok(Frame {
        width,
        height,
        document,
        pixels: image.into_raw(),
    })
}

fn image_status(error: image::ImageError) -> Status {
    if matches!(error, image::ImageError::Limits(_)) {
        Status::Limit
    } else {
        Status::Unreadable
    }
}

fn render_pdf(data: Vec<u8>) -> Result<Frame> {
    let pdf = hayro::hayro_syntax::Pdf::new(data).map_err(|_| Status::Unreadable)?;
    let page = pdf.pages().first().ok_or(Status::Unreadable)?;
    let (width, height) = page.render_dimensions();
    let factor = scale(width, height)?;
    let settings = PixmapSettings {
        x_scale: factor,
        y_scale: factor,
        bg_color: hayro::vello_cpu::color::palette::css::WHITE,
    };
    let image = hayro::render(
        page,
        &RenderCache::new(),
        &InterpreterSettings::default(),
        &RenderSettings::default(),
        &settings,
    );
    Ok(Frame {
        width: u32::from(image.width()),
        height: u32::from(image.height()),
        document: true,
        pixels: image.data_as_u8_slice().to_vec(),
    })
}

fn render_hwp(data: &[u8]) -> Result<Frame> {
    let doc = rhwp::DocumentCore::from_bytes(data).map_err(|_| Status::Unreadable)?;
    let svg = doc
        .render_page_svg_native(0)
        .map_err(|_| Status::Unreadable)?;
    let mut options = usvg::Options::default();
    options.image_href_resolver.resolve_string = Box::new(|_, _| None);
    let fonts = options.fontdb_mut();
    if let Some(windows) = std::env::var_os("SystemRoot") {
        let path = std::path::PathBuf::from(windows).join("Fonts");
        for font in [
            "malgun.ttf",
            "malgunbd.ttf",
            "batang.ttc",
            "gulim.ttc",
            "arial.ttf",
            "cambria.ttc",
            "symbol.ttf",
        ] {
            let _ = fonts.load_font_file(path.join(font));
        }
    }
    fonts.set_sans_serif_family("Malgun Gothic");
    fonts.set_serif_family("Batang");
    let tree = usvg::Tree::from_str(&svg, &options).map_err(|_| Status::Unreadable)?;
    let factor = scale(tree.size().width(), tree.size().height())?;
    let width = (tree.size().width() * factor).ceil() as u32;
    let height = (tree.size().height() * factor).ceil() as u32;
    let mut image = tiny_skia::Pixmap::new(width, height).ok_or(Status::Limit)?;
    image.fill(tiny_skia::Color::WHITE);
    resvg::render(
        &tree,
        tiny_skia::Transform::from_scale(factor, factor),
        &mut image.as_mut(),
    );
    Ok(Frame {
        width,
        height,
        document: true,
        pixels: image.take(),
    })
}

fn render(format: Format, data: Vec<u8>) -> Result<Frame> {
    match format {
        Format::Png => decode_image(&data, ImageFormat::Png, false),
        Format::Jpeg => decode_image(&data, ImageFormat::Jpeg, false),
        Format::Pdf => render_pdf(data),
        Format::Hwp => render_hwp(&data),
        Format::Docx => {
            let doc = rdocx::Document::from_bytes(&data).map_err(|_| Status::Unreadable)?;
            let malgun = std::env::var_os("SystemRoot").and_then(|windows| {
                let font = std::path::PathBuf::from(windows).join("Fonts/malgun.ttf");
                std::fs::read(font).ok()
            });
            let fonts: Vec<_> = malgun
                .as_deref()
                .into_iter()
                .map(|bytes| ("malgun.ttf", bytes))
                .collect();
            let png = doc
                .render_page_to_png_with_fonts_and_options(
                    0,
                    110.0,
                    &fonts,
                    rdocx::RenderOptions::default(),
                )
                .map_err(|_| Status::Unreadable)?
                .ok_or(Status::Unreadable)?;
            decode_image(&png, ImageFormat::Png, true)
        }
        _ => unreachable!("text previews are sent separately"),
    }
}

fn main() -> io::Result<()> {
    let mut data = Vec::new();
    io::stdin()
        .lock()
        .take(MAX_INPUT + 1)
        .read_to_end(&mut data)?;
    let format = if data.len() as u64 > MAX_INPUT {
        Err(Status::Limit)
    } else {
        identify(&data)
    };
    let mut output = io::BufWriter::new(io::stdout().lock());
    let result = match format {
        Ok(Format::Office(format)) => {
            return listing::write(&mut output, listing::office(data, format));
        }
        Ok(Format::Zip) => return listing::write(&mut output, listing::zip(&data)),
        Ok(Format::Tar) => return listing::write(&mut output, listing::tar(&data)),
        Ok(Format::SevenZip) => return listing::write(&mut output, listing::seven_zip(&data)),
        Ok(format) => render(format, data),
        Err(status) => Err(status),
    };
    match result {
        Ok(mut frame)
            if (1..=MAX_EDGE).contains(&frame.width) && (1..=MAX_EDGE).contains(&frame.height) =>
        {
            output.write_all(&[0, u8::from(frame.document)])?;
            output.write_all(&frame.width.to_le_bytes())?;
            output.write_all(&frame.height.to_le_bytes())?;
            for pixel in frame.pixels.as_chunks_mut::<4>().0 {
                pixel.swap(0, 2);
            }
            output.write_all(&frame.pixels)?;
        }
        Ok(_) => output.write_all(&[Status::Limit as u8])?,
        Err(status) => output.write_all(&[status as u8])?,
    }
    output.flush()
}
