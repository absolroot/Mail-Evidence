use crate::model::{Format, Identification, NdbLayout};
use serde::Serialize;
use sha2::{Digest, Sha256};
use std::{
    collections::HashSet,
    fs::{self, File, Metadata, OpenOptions},
    io::{self, Read, Seek, SeekFrom, Write},
    path::{Path, PathBuf},
};

const MAX_FILES: usize = 100_000;
const MAX_DEPTH: usize = 100;
const PREFIX_BYTES: usize = 8192;

#[derive(Serialize)]
pub struct Source {
    pub id: String,
    pub selected_root: PathBuf,
    pub discovered_path: PathBuf,
    pub opened_path: PathBuf,
    pub collection_kind: &'static str,
    pub bytes: u64,
    pub sha256: String,
    pub identification: Identification,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub discovered_by: Option<&'static str>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub logical_folder: Option<Vec<String>>,
}

#[derive(Serialize)]
pub struct Failure {
    pub path: PathBuf,
    pub code: &'static str,
    pub detail: String,
}

pub struct Candidate {
    pub root: PathBuf,
    pub path: PathBuf,
    pub explicit: bool,
    pub discovered_by: Option<&'static str>,
    pub profile_kind: Option<crate::profile::Kind>,
}

#[derive(Default)]
pub struct Discovery {
    pub files: Vec<Candidate>,
    pub failures: Vec<Failure>,
    pub skipped_unrecognized: usize,
}

pub fn discover(roots: Vec<PathBuf>) -> Discovery {
    let mut result = Discovery::default();
    let mut pending: Vec<_> = roots
        .into_iter()
        .rev()
        .map(|root| (root.clone(), root, 0))
        .collect();
    let mut visited = HashSet::new();
    let mut examined = 0;
    while let Some((root, path, depth)) = pending.pop() {
        if examined == MAX_FILES {
            reject(
                &mut result,
                path,
                "entry_limit",
                "Collection entry limit reached.",
            );
            break;
        }
        examined += 1;
        match fs::symlink_metadata(&path) {
            Ok(meta) if is_redirect(&meta) => {
                reject(
                    &mut result,
                    path,
                    "path_redirect",
                    "Symbolic links and reparse points are excluded.",
                );
            }
            Ok(meta) if meta.is_dir() => {
                if depth >= MAX_DEPTH {
                    reject(
                        &mut result,
                        path,
                        "depth_limit",
                        "Directory depth limit reached.",
                    );
                    continue;
                }
                // Maildir tmp contains in-progress writes.
                if depth != 0
                    && path.file_name().is_some_and(|name| name == "tmp")
                    && path.parent().is_some_and(is_maildir)
                {
                    continue;
                }
                let entries = match fs::read_dir(&path) {
                    Ok(entries) => entries,
                    Err(error) => {
                        reject(&mut result, path, "directory_unreadable", error);
                        continue;
                    }
                };
                let mut children = Vec::new();
                for item in entries.take(MAX_FILES + 1) {
                    match item {
                        Ok(item) => children.push(item.path()),
                        Err(error) => reject(
                            &mut result,
                            path.clone(),
                            "directory_entry_unreadable",
                            error,
                        ),
                    }
                }
                children.sort_unstable();
                pending.extend(
                    children
                        .into_iter()
                        .rev()
                        .map(|child| (root.clone(), child, depth + 1)),
                );
            }
            Ok(meta) if meta.is_file() => {
                let key = fs::canonicalize(&path).unwrap_or_else(|_| path.clone());
                if visited.insert(key) {
                    result.files.push(Candidate {
                        root,
                        path,
                        explicit: depth == 0,
                        discovered_by: None,
                        profile_kind: None,
                    });
                }
            }
            Ok(_) => reject(
                &mut result,
                path,
                "not_a_regular_file",
                "The path is not a regular file or directory.",
            ),
            Err(error) => reject(&mut result, path, "path_unreadable", error),
        }
    }
    result
}

fn reject(result: &mut Discovery, path: PathBuf, code: &'static str, detail: impl ToString) {
    result.failures.push(Failure {
        path,
        code,
        detail: detail.to_string(),
    });
}

pub fn is_redirect(meta: &Metadata) -> bool {
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        meta.file_attributes() & 0x400 != 0
    }
    #[cfg(not(windows))]
    meta.file_type().is_symlink()
}

fn is_maildir(path: &Path) -> bool {
    path.join("cur").is_dir() && path.join("new").is_dir()
}

fn collection_kind(path: &Path, format: Format) -> &'static str {
    let parent = path.parent();
    if format == Format::MhSequences
        || (format == Format::Eml
            && path
                .file_name()
                .and_then(|name| name.to_str())
                .is_some_and(|name| {
                    !name.is_empty() && name.bytes().all(|byte| byte.is_ascii_digit())
                })
            && parent.is_some_and(|dir| {
                fs::symlink_metadata(dir.join(".mh_sequences"))
                    .is_ok_and(|meta| meta.is_file() && !is_redirect(&meta))
            }))
    {
        return "mh_mailbox";
    }
    if parent.is_some_and(|dir| {
        dir.file_name()
            .is_some_and(|name| name == "cur" || name == "new")
            && dir.parent().is_some_and(is_maildir)
    }) {
        return "maildir";
    }
    if matches!(format, Format::Mbox | Format::ThunderbirdIndex)
        && path.ancestors().any(|dir| {
            dir.file_name().is_some_and(|name| {
                let name = name.to_string_lossy();
                name.eq_ignore_ascii_case("ImapMail") || name.to_ascii_lowercase().ends_with(".sbd")
            })
        })
    {
        return "thunderbird_layout";
    }
    if format == Format::Emlx
        && path
            .ancestors()
            .any(|dir| dir.extension().is_some_and(|ext| ext == "mbox"))
    {
        return "apple_mail_layout";
    }
    "local_file"
}

pub fn open_read_only(path: &Path) -> io::Result<File> {
    if is_redirect(&fs::symlink_metadata(path)?) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "Refusing a path redirect.",
        ));
    }
    let mut options = OpenOptions::new();
    options.read(true);
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        use windows_sys::Win32::Storage::FileSystem::{
            FILE_FLAG_OPEN_REPARSE_POINT, FILE_SHARE_READ,
        };
        options
            .share_mode(FILE_SHARE_READ)
            .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT);
    }
    let file = options.open(path)?;
    let meta = file.metadata()?;
    if !meta.is_file() || is_redirect(&meta) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "Refusing a non-regular file.",
        ));
    }
    Ok(file)
}

#[cfg(windows)]
pub fn opened_path(file: &File, _: &Path) -> io::Result<PathBuf> {
    use std::{
        ffi::OsString,
        os::windows::{ffi::OsStringExt, io::AsRawHandle},
    };
    use windows_sys::Win32::Storage::FileSystem::GetFinalPathNameByHandleW;
    let mut buffer = vec![0; 32768];
    let length = unsafe {
        GetFinalPathNameByHandleW(
            file.as_raw_handle(),
            buffer.as_mut_ptr(),
            buffer.len() as u32,
            0,
        )
    } as usize;
    if length == 0 {
        return Err(io::Error::last_os_error());
    }
    if length >= buffer.len() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "Opened path exceeds the Windows path limit.",
        ));
    }
    Ok(OsString::from_wide(&buffer[..length]).into())
}

#[cfg(not(windows))]
pub fn opened_path(_: &File, path: &Path) -> io::Result<PathBuf> {
    fs::canonicalize(path)
}

pub fn source(candidate: &Candidate) -> io::Result<Option<(Source, File)>> {
    let mut file = open_read_only(&candidate.path)?;
    let meta = file.metadata()?;
    let mut prefix = Vec::with_capacity(PREFIX_BYTES);
    Read::by_ref(&mut file)
        .take(PREFIX_BYTES as u64)
        .read_to_end(&mut prefix)?;
    let mut identification = identify(&candidate.path, &prefix, meta.len());
    if let Some(kind) = candidate.profile_kind {
        kind.identify(candidate, &mut identification, meta.len());
        if !kind.accepts(identification.format) {
            return Ok(None);
        }
    }
    if !candidate.explicit && identification.format == Format::Unknown {
        return Ok(None);
    }
    let path = opened_path(&file, &candidate.path)?;
    let hash = hash_file(&mut file)?;
    let after = file.metadata()?;
    if meta.len() != after.len() || meta.modified().ok() != after.modified().ok() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "Source changed while its identity was read.",
        ));
    }
    let id = format!(
        "{:x}",
        Sha256::digest(format!("{}\n{hash}", path.display()))
    );
    let collection_kind = candidate.profile_kind.map_or_else(
        || collection_kind(&candidate.path, identification.format),
        crate::profile::Kind::name,
    );
    Ok(Some((
        Source {
            id,
            selected_root: candidate.root.clone(),
            discovered_path: candidate.path.clone(),
            opened_path: path,
            collection_kind,
            bytes: meta.len(),
            sha256: hash,
            identification,
            discovered_by: candidate.discovered_by,
            logical_folder: candidate
                .profile_kind
                .and_then(|kind| kind.logical_folder(candidate)),
        },
        file,
    )))
}

pub fn hash_file(file: &mut File) -> io::Result<String> {
    file.seek(SeekFrom::Start(0))?;
    let (_, hash) = hash_reader(file)?;
    file.seek(SeekFrom::Start(0))?;
    Ok(hash)
}

pub fn hash_reader(reader: &mut impl Read) -> io::Result<(u64, String)> {
    let mut hash = Sha256::new();
    let length = io::copy(reader, &mut hash)?;
    Ok((length, format!("{:x}", hash.finalize())))
}

pub fn digest(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}

pub fn identify(path: &Path, data: &[u8], file_size: u64) -> Identification {
    use Format::*;
    let ext = path
        .extension()
        .and_then(|x| x.to_str())
        .unwrap_or("")
        .to_ascii_lowercase();
    let mut ndb = None;
    let (format, basis) = if data.starts_with(b"!BDN")
        && matches!(data.get(8..10), Some(b"SM" | b"SO"))
    {
        let version = data
            .get(10..12)
            .map(|raw| u16::from_le_bytes([raw[0], raw[1]]));
        ndb = version.map(|version| NdbLayout {
            version,
            page_size: match version {
                14 | 15 | 21 | 23 => Some(512),
                36 => Some(4096),
                _ => None,
            },
            block_encoding: match version {
                14 | 15 => data.get(461).copied(),
                21 | 23 | 36 => data.get(513).copied(),
                _ => None,
            },
        });
        (
            if data.get(8..10) == Some(b"SO") {
                Ost
            } else {
                Pst
            },
            "ndb_client_signature",
        )
    } else if data.starts_with(&[0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1]) && ext == "msg" {
        (Msg, "compound_document_candidate")
    } else if data.starts_with(&[0xcf, 0xad, 0x12, 0xfe, 0x02, 0, 0x02, 0]) {
        (OutlookExpressNch, "oe4_nch_signature")
    } else if data.starts_with(&[0xcf, 0xad, 0x12, 0xfe, 0xc6, 0xfd, 0x74, 0x6f]) {
        (DbxFolders, "dbx_folder_index_signature")
    } else if data.starts_with(&[0xcf, 0xad, 0x12, 0xfe, 0xc7, 0xfd, 0x74, 0x6f])
        || data.starts_with(&[0xcf, 0xad, 0x12, 0xfe, 0x30, 0x9d, 0xfe, 0x26])
    {
        (DbxMetadata, "dbx_non_mail_metadata_signature")
    } else if data.starts_with(&[0xcf, 0xad, 0x12, 0xfe]) {
        (Dbx, "dbx_signature")
    } else if data.starts_with(b"JMF6") {
        (OutlookExpressMbx, "oe4_mbx_signature")
    } else if data.starts_with(b"JMF9") {
        (OutlookExpressIdx, "oe4_idx_signature")
    } else if data.starts_with(&[0x78, 0x9f, 0x3e, 0x22]) {
        (Tnef, "tnef_signature")
    } else if matches!(data.get(..4), Some(b"PK\x03\x04" | b"PK\x05\x06")) {
        (if ext == "olm" { Olm } else { Zip }, "zip_signature")
    } else if path.file_name().is_some_and(|name| name == ".mh_sequences") {
        (MhSequences, "mh_sequence_filename")
    } else if data.starts_with(b"BABYL OPTIONS:\n") || data.starts_with(b"BABYL OPTIONS:\r\n") {
        (Babyl, "babyl_options_header")
    } else if data.starts_with(b"\x01\x01\x01\x01\n") || data.starts_with(b"\x01\x01\x01\x01\r\n") {
        (Mmdf, "mmdf_separator")
    } else if data
        .get(..15)
        .is_some_and(|s| s.eq_ignore_ascii_case(b"BEGIN:VCALENDAR"))
    {
        (Ics, "vcalendar_delimiter")
    } else if data
        .get(..11)
        .is_some_and(|s| s.eq_ignore_ascii_case(b"BEGIN:VCARD"))
    {
        (Vcf, "vcard_delimiter")
    } else if emlx_range(data, file_size).is_some_and(|(_, length)| length != 0) {
        (Emlx, "emlx_length_prefix")
    } else if crate::mime::mbox_envelope(data.split(|&b| b == b'\n').next().unwrap_or_default()) {
        (Mbox, "mbox_envelope")
    } else if crate::mime::looks_like_message(data) {
        (Eml, "message_header_fields")
    } else {
        (
            match ext.as_str() {
                "eml" | "nws" | "mime" | "rfc822" => Eml,
                "emlx" => Emlx,
                "mbox" => Mbox,
                "mmdf" => Mmdf,
                "babyl" | "rmail" => Babyl,
                "msg" => Msg,
                "pst" => Pst,
                "ost" => Ost,
                "zip" => Zip,
                "dbx" => Dbx,
                "mbx" => OutlookExpressMbx,
                "idx" => OutlookExpressIdx,
                "nch"
                    if path
                        .file_name()
                        .is_some_and(|name| name.eq_ignore_ascii_case("folders.nch")) =>
                {
                    OutlookExpressNch
                }
                "olm" => Olm,
                "tnef" => Tnef,
                "p7m" | "p7s" => P7m,
                "ics" => Ics,
                "vcf" => Vcf,
                "edb" => Edb,
                "nsf" => Nsf,
                "msf" => ThunderbirdIndex,
                "e01" | "ex01" | "aff" | "aff4" | "dd" => DiskImage,
                _ => Unknown,
            },
            "extension_hint",
        )
    };
    Identification { format, basis, ndb }
}

pub fn emlx_range(data: &[u8], size: u64) -> Option<(u64, u64)> {
    let end = data
        .get(..data.len().min(24))?
        .iter()
        .position(|&b| b == b'\n')?;
    let line = std::str::from_utf8(&data[..end]).ok()?.trim_ascii();
    if line.is_empty() || !line.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    let length = line.parse::<u64>().ok()?;
    let offset = (end + 1) as u64;
    (offset.checked_add(length)? <= size).then_some((offset, length))
}

pub fn copy_range(
    path: &Path,
    expected: &str,
    offset: u64,
    length: u64,
    output: &Path,
) -> io::Result<String> {
    let mut input = open_read_only(path)?;
    let bound = input.metadata()?.len();
    if offset.checked_add(length).is_none_or(|end| end > bound) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "Range exceeds the source length.",
        ));
    }
    verify_sha(&mut input, expected)?;
    let mut draft = new_output(output)?;
    input.seek(SeekFrom::Start(offset))?;
    if io::copy(&mut Read::by_ref(&mut input).take(length), &mut draft)? != length {
        return Err(io::Error::new(
            io::ErrorKind::UnexpectedEof,
            "Source ended during copy.",
        ));
    }
    verify_sha(&mut input, expected)?;
    finish_output(draft, output)
}

pub fn export_express(
    path: &Path,
    expected: &str,
    offset: u64,
    item: crate::mime::Export,
    output: &Path,
) -> io::Result<(usize, String)> {
    write_verified(path, expected, output, |input, size| {
        let prefix = crate::mime::read_range(input, 0, size.min(8192))?;
        let format = identify(path, &prefix, size).format;
        let message = crate::express::message_payload(input, size, format, offset)?;
        match item.bytes(&message)? {
            std::borrow::Cow::Borrowed(_) if matches!(item, crate::mime::Export::Eml) => {
                Ok(message)
            }
            bytes => Ok(bytes.into_owned()),
        }
    })
}

pub fn write_verified(
    path: &Path,
    expected: &str,
    output: &Path,
    extract: impl FnOnce(&mut File, u64) -> io::Result<Vec<u8>>,
) -> io::Result<(usize, String)> {
    let mut input = open_read_only(path)?;
    let size = input.metadata()?.len();
    verify_sha(&mut input, expected)?;
    let bytes = extract(&mut input, size)?;
    let mut draft = new_output(output)?;
    draft.write_all(&bytes)?;
    verify_sha(&mut input, expected)?;
    Ok((bytes.len(), finish_output(draft, output)?))
}

fn verify_sha(file: &mut File, expected: &str) -> io::Result<()> {
    if hash_file(file)?.eq_ignore_ascii_case(expected) {
        Ok(())
    } else {
        Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "The source SHA-256 does not match.",
        ))
    }
}

fn new_output(output: &Path) -> io::Result<tempfile::NamedTempFile> {
    let folder = output
        .parent()
        .filter(|path| !path.as_os_str().is_empty())
        .unwrap_or(Path::new("."));
    tempfile::NamedTempFile::new_in(folder)
}

fn finish_output(mut draft: tempfile::NamedTempFile, output: &Path) -> io::Result<String> {
    draft.flush()?;
    draft.as_file().sync_all()?;
    let hash = hash_file(draft.as_file_mut())?;
    draft.persist_noclobber(output)?;
    Ok(hash)
}
