use std::io::{self, Cursor, Write};

use crate::{Result, Status};

const MAX_TEXT: usize = 64 * 1024;

#[derive(Default)]
pub struct Listing {
    text: String,
    archive: bool,
    encrypted: bool,
    truncated: bool,
}

impl Listing {
    fn archive() -> Self {
        Self {
            archive: true,
            ..Self::default()
        }
    }

    fn entry(&mut self, name: &str, directory: bool) {
        if self.truncated {
            return;
        }
        let name = name
            .replace('\\', "\\\\")
            .replace('\n', "\\n")
            .replace('\r', "\\r")
            .replace('\t', "\\t");
        let suffix = if directory && !name.ends_with('/') {
            "/"
        } else {
            ""
        };
        if self.text.len() + name.len() + suffix.len() + 1 > MAX_TEXT {
            self.truncated = true;
            return;
        }
        self.text.push_str(&name);
        self.text.push_str(suffix);
        self.text.push('\n');
    }
}

pub fn zip(data: &[u8]) -> Result<Listing> {
    let mut archive = zip::ZipArchive::new(Cursor::new(data)).map_err(|_| Status::Unreadable)?;
    let mut listing = Listing::archive();
    for index in 0..archive.len() {
        let entry = archive
            .by_index_raw(index)
            .map_err(|_| Status::Unreadable)?;
        listing.encrypted |= entry.encrypted();
        listing.entry(entry.name(), entry.is_dir());
    }
    Ok(listing)
}

pub fn is_tar(data: &[u8]) -> bool {
    let Some(header) = data.get(..512) else {
        return false;
    };
    if header[257..].starts_with(b"ustar") {
        return true;
    }
    if data.len() >= 1024 && data.iter().take(1024).all(|&byte| byte == 0) {
        return true;
    }
    let field = &header[148..156];
    let checksum = std::str::from_utf8(field)
        .ok()
        .and_then(|value| u32::from_str_radix(value.trim_matches(['\0', ' ']), 8).ok());
    let sum = header[..148]
        .iter()
        .chain(&header[156..])
        .map(|&byte| u32::from(byte))
        .sum::<u32>();
    checksum == Some(sum + 8 * u32::from(b' '))
}

pub fn tar(data: &[u8]) -> Result<Listing> {
    let mut archive = tar::Archive::new(Cursor::new(data));
    let mut listing = Listing::archive();
    for entry in archive.entries().map_err(|_| Status::Unreadable)? {
        let entry = entry.map_err(|_| Status::Unreadable)?;
        listing.entry(
            &String::from_utf8_lossy(&entry.path_bytes()),
            entry.header().entry_type().is_dir(),
        );
        if listing.truncated {
            break;
        }
    }
    Ok(listing)
}

pub fn seven_zip(data: &[u8]) -> Result<Listing> {
    let archive = sevenz_rust2::Archive::read_with_memory_limit(
        &mut Cursor::new(data),
        &sevenz_rust2::Password::empty(),
        32 * 1024,
    )
    .map_err(|error| match error {
        sevenz_rust2::Error::PasswordRequired => Status::PasswordRequired,
        sevenz_rust2::Error::MaxMemLimited { .. } => Status::Limit,
        _ => Status::Unreadable,
    })?;
    let mut listing = Listing::archive();
    listing.encrypted = (0..archive.files.len()).any(|index| archive.is_file_encrypted(index));
    for entry in archive.files {
        listing.entry(entry.name(), entry.is_directory());
        if listing.truncated {
            break;
        }
    }
    Ok(listing)
}

pub fn office(data: Vec<u8>, format: office_oxide::DocumentFormat) -> Result<Listing> {
    let mut options = office_oxide::options::ParseOptions::default();
    options.limits.package_bytes = crate::MAX_ZIP_DATA;
    options.limits.part_bytes = 24 * 1024 * 1024;
    options.limits.memory_bytes = 64 * 1024 * 1024;
    options.limits.embedded_depth = 0;
    options.limits.embedded_packages = 0;
    let document = office_oxide::Document::from_reader_with(Cursor::new(data), format, &options)
        .map_err(|error| match error {
            office_oxide::OfficeError::Doc(office_oxide::doc::DocError::Encrypted)
            | office_oxide::OfficeError::Ppt(office_oxide::ppt::PptError::Encrypted)
            | office_oxide::OfficeError::Xls(office_oxide::xls::XlsError::Encrypted) => {
                Status::PasswordRequired
            }
            _ => Status::Unreadable,
        })?;
    let mut output = LimitedText::default();
    if document.write_plain_text(&mut output).is_err() && !output.truncated {
        return Err(Status::Unreadable);
    }
    if let Err(error) = std::str::from_utf8(&output.bytes) {
        if !output.truncated || error.error_len().is_some() {
            return Err(Status::Unreadable);
        }
        output.bytes.truncate(error.valid_up_to());
    }
    Ok(Listing {
        text: String::from_utf8(output.bytes).map_err(|_| Status::Unreadable)?,
        truncated: output.truncated,
        ..Listing::default()
    })
}

#[derive(Default)]
struct LimitedText {
    bytes: Vec<u8>,
    truncated: bool,
}

impl Write for LimitedText {
    fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
        let count = bytes.len().min(MAX_TEXT - self.bytes.len());
        self.truncated |= count < bytes.len();
        self.bytes.extend_from_slice(&bytes[..count]);
        Ok(count)
    }

    fn flush(&mut self) -> io::Result<()> {
        Ok(())
    }
}

pub fn write(output: &mut impl Write, result: Result<Listing>) -> io::Result<()> {
    match result {
        Ok(listing) => {
            let flags = u8::from(listing.archive)
                | (u8::from(listing.encrypted) << 1)
                | (u8::from(listing.truncated) << 2);
            output.write_all(&[4, flags])?;
            output.write_all(&(listing.text.len() as u32).to_le_bytes())?;
            output.write_all(listing.text.as_bytes())?;
        }
        Err(status) => output.write_all(&[status as u8])?,
    }
    output.flush()
}
