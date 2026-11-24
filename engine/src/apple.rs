use crate::{
    collect::{hash_file, is_redirect, open_read_only, opened_path},
    mime::MESSAGE_LIMIT,
    model::{Attachment, ByteRange, Inspection, Location},
};
use mail_parser::Message;
use std::{
    collections::BTreeMap,
    fs, io,
    path::{Path, PathBuf},
};

pub fn attachments(
    source: &Path,
    message: &Message<'_>,
    data: &[u8],
    at: &impl Fn(ByteRange) -> Location,
    view: &mut Inspection,
) -> BTreeMap<u32, Attachment> {
    let Some(id) = source
        .file_name()
        .and_then(|name| name.to_str())
        .and_then(|name| name.strip_suffix(".partial.emlx"))
        .filter(|id| !id.is_empty() && id.bytes().all(|byte| byte.is_ascii_digit()))
    else {
        return BTreeMap::new();
    };
    let layout = source
        .parent()
        .filter(|dir| dir.file_name().is_some_and(|name| name == "Messages"))
        .and_then(Path::parent);
    let protected = message.parts().any(|part| {
        part.is_content_type("multipart", "signed")
            || part.is_content_type("multipart", "encrypted")
            || part.is_content_type("application", "pkcs7-mime")
            || part.is_content_type("application", "x-pkcs7-mime")
            || part.is_content_type("application", "pgp-encrypted")
    });
    let mut replacements = BTreeMap::new();
    let mut pending = vec![(message.root_part(), String::new(), 0)];
    let mut budget = MESSAGE_LIMIT;
    while let Some((part, section, depth)) = pending.pop() {
        if part.is_multipart() {
            if depth == 32 {
                view.issue(
                    "emlx_part_depth",
                    "MIME part nesting exceeds the sidecar lookup limit.",
                    None,
                );
                continue;
            }
            for (index, child) in part.children().enumerate().rev() {
                let index = index + 1;
                let child_section = if section.is_empty() {
                    index.to_string()
                } else {
                    format!("{section}.{index}")
                };
                pending.push((child, child_section, depth + 1));
            }
            continue;
        }
        let headers = part.headers();
        let mut claims = headers.iter().filter(|header| {
            header
                .raw_name()
                .eq_ignore_ascii_case("X-Apple-Content-Length")
        });
        let Some(claim) = claims.next() else { continue };
        let start = u64::from(part.offset_body());
        let end = u64::from(part.offset_end());
        let Some(raw) = data.get(start as usize..end as usize) else {
            continue;
        };
        if !raw.iter().all(u8::is_ascii_whitespace) {
            continue;
        }
        let stored = at(ByteRange::between(start, end));
        let section = if section.is_empty() {
            "1".to_owned()
        } else {
            section
        };
        let claimed_length = std::str::from_utf8(claim.raw_value())
            .ok()
            .and_then(|value| value.trim_ascii().parse::<u64>().ok())
            .filter(|&length| length > 0 && claims.next().is_none());
        let name = part.attachment_name().map(str::to_owned);
        let recovered = if protected {
            Err(invalid(
                "The message contains signed or encrypted MIME parts.",
            ))
        } else if let Some((root, length)) = layout.zip(claimed_length) {
            read_sidecar(root, id, &section, length, name.as_deref(), &mut budget)
        } else {
            Err(invalid(
                "A valid Apple attachment length and Messages directory are required.",
            ))
        };
        let attachment = match recovered {
            Ok((location, bytes, sha256)) => Attachment {
                media_type: part.content_type().and_then(|kind| {
                    kind.subtype()
                        .map(|subtype| format!("{}/{}", kind.ctype(), subtype))
                }),
                content_id: part.content_id().map(str::to_owned),
                name,
                sha256: Some(sha256),
                bytes: Some(bytes),
                location: Some(location),
                representation: "stored_emlx_sidecar",
            },
            Err(error) => {
                view.issue(
                    "emlx_sidecar_unavailable",
                    format!("MIME part {section}: {error}"),
                    Some(stored.clone()),
                );
                Attachment {
                    media_type: part.content_type().and_then(|kind| {
                        kind.subtype()
                            .map(|subtype| format!("{}/{}", kind.ctype(), subtype))
                    }),
                    content_id: part.content_id().map(str::to_owned),
                    name,
                    location: Some(stored),
                    representation: "attachment_payload_unavailable",
                    bytes: None,
                    sha256: None,
                }
            }
        };
        replacements.insert(part.id(), attachment);
    }
    replacements
}

fn invalid(detail: &'static str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, detail)
}

fn read_sidecar(
    root: &Path,
    id: &str,
    section: &str,
    claim: u64,
    name: Option<&str>,
    budget: &mut u64,
) -> io::Result<(Location, u64, String)> {
    let mut directory = PathBuf::from(root);
    for component in ["Attachments", id, section] {
        directory.push(component);
        let metadata = fs::symlink_metadata(&directory)?;
        if !metadata.is_dir() || is_redirect(&metadata) {
            return Err(invalid(
                "Apple attachment directory is not a regular directory.",
            ));
        }
    }
    let mut children = fs::read_dir(&directory)?;
    let entry = children
        .next()
        .transpose()?
        .ok_or_else(|| invalid("Apple attachment directory is empty."))?;
    if children.next().is_some() || name.is_some_and(|name| entry.file_name() != name) {
        return Err(invalid(
            "Apple attachment directory has no unambiguous filename match.",
        ));
    }
    let path = entry.path();
    let mut file = open_read_only(&path)?;
    let opened = opened_path(&file, &path)?;
    if opened != path {
        return Err(invalid(
            "The opened Apple attachment path differs from the evidence path.",
        ));
    }
    let metadata = file.metadata()?;
    let length = metadata.len();
    if length == 0 || length > *budget {
        return Err(invalid(
            "Apple attachment is empty or exceeds the sidecar read budget.",
        ));
    }
    *budget -= length;
    let sha256 = hash_file(&mut file)?;
    let after = file.metadata()?;
    if length != after.len() || metadata.modified()? != after.modified()? {
        return Err(invalid("Apple attachment changed during hashing."));
    }
    Ok((
        Location::ExternalFile {
            path: opened,
            sha256: sha256.clone(),
            range: ByteRange::between(0, length),
            mime_part: section.to_owned(),
            apple_content_length: claim,
        },
        length,
        sha256,
    ))
}
