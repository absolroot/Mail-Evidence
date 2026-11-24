use crate::{
    collect::{digest, emlx_range, identify},
    mime::{self, MESSAGE_LIMIT, body, read_range},
    model::{Attachment, ByteRange, Format, Inspection, Location, Record},
};
use std::{
    collections::{HashMap, HashSet},
    io::{self, Cursor, Read, Seek},
    path::Path,
};

const ZIP_MEMBER_LIMIT: usize = 10_000;
const ZIP_TOTAL_LIMIT: u64 = 512 * 1024 * 1024;

fn invalid(detail: &str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, detail)
}

type MemberIndex = (usize, u64);

fn olm_member<R: Read + Seek>(
    zip: &mut zip::ZipArchive<R>,
    index: MemberIndex,
    limit: u64,
    budget: &mut u64,
) -> io::Result<Vec<u8>> {
    let (index, size) = index;
    if size > limit || size > *budget {
        return Err(invalid("OLM member exceeds the expansion limit."));
    }
    *budget -= size;
    let mut data = Vec::new();
    zip.by_index(index)?.take(size + 1).read_to_end(&mut data)?;
    if data.len() as u64 != size {
        return Err(invalid("OLM member length does not match."));
    }
    Ok(data)
}

pub fn olm<R: Read + Seek>(input: R, view: &mut Inspection) -> io::Result<()> {
    let mut zip = zip::ZipArchive::new(input)?;
    if zip.len() > ZIP_MEMBER_LIMIT {
        return Err(invalid("OLM has too many ZIP entries."));
    }
    let mut members = HashMap::new();
    let mut duplicates = HashSet::new();
    let mut documents = Vec::new();
    for index in 0..zip.len() {
        let entry = match zip.by_index(index) {
            Ok(entry) => entry,
            Err(error) => {
                view.issue(
                    "olm_member_unreadable",
                    format!("Entry {index}: {error}"),
                    None,
                );
                continue;
            }
        };
        if entry.is_dir() {
            continue;
        }
        let name = entry.name().to_owned();
        if entry.enclosed_name().is_none() || entry.is_symlink() {
            view.issue(
                "olm_member_path",
                "OLM member name or type is unsafe.",
                None,
            );
            continue;
        }
        let value = (index, entry.size());
        if members.insert(name.clone(), value).is_some() {
            duplicates.insert(name.clone());
        }
        let base = name.rsplit('/').next().unwrap_or_default();
        if (base.starts_with("message_") && base.ends_with(".xml")) || base == "Contacts.xml" {
            documents.push((name, value));
        }
    }
    if !duplicates.is_empty() {
        view.issue(
            "olm_duplicate_member",
            "Duplicate ZIP names are ambiguous.",
            None,
        );
    }
    let mut budget = ZIP_TOTAL_LIMIT;
    for (name, (index, size)) in documents {
        if duplicates.contains(&name) {
            continue;
        }
        let at = |range| Location::ArchiveMember {
            name: name.clone(),
            index,
            range,
        };
        let data = match olm_member(&mut zip, (index, size), 4 * 1024 * 1024, &mut budget) {
            Ok(data) => data,
            Err(error) => {
                view.issue(
                    "olm_xml_unreadable",
                    error,
                    Some(at(ByteRange::between(0, size))),
                );
                continue;
            }
        };
        let document = std::str::from_utf8(&data)
            .ok()
            .and_then(|text| roxmltree::Document::parse(text).ok());
        let Some(document) = document else {
            view.issue(
                "olm_xml",
                "Expected well-formed XML without a DTD.",
                Some(at(ByteRange::between(0, size))),
            );
            continue;
        };
        for item in document
            .root_element()
            .children()
            .filter(|node| node.is_element())
        {
            let range = item.range();
            let location = at(ByteRange::between(range.start as u64, range.end as u64));
            let find = |name| item.children().find(|node| node.has_tag_name(name));
            let text = |name| find(name).and_then(|node| node.text()).map(str::to_owned);
            let mut record = match item.tag_name().name() {
                "email" => Record::new("mail", location),
                "contact" => Record::new("contact_document", location),
                _ => continue,
            };
            if record.class == "contact_document" {
                record.subject =
                    text("OPFContactCopyDisplayName").or_else(|| text("OPFContactCopyFirstName"));
                view.add_record(record);
                continue;
            }
            record.subject = text("OPFMessageCopySubject");
            record.message_id = text("OPFMessageCopyMessageID");
            record.date_raw = text("OPFMessageCopySentTime");
            let addresses = |name| {
                find(name).and_then(|node| {
                    let values: Vec<_> = node
                        .children()
                        .filter(|child| child.has_tag_name("emailAddress"))
                        .filter_map(|address| {
                            address
                                .attribute("OPFContactEmailAddressAddress")
                                .or_else(|| address.attribute("address"))
                        })
                        .collect();
                    (!values.is_empty()).then(|| values.join(", "))
                })
            };
            record.from = addresses("OPFMessageCopyFromAddresses");
            record.to = addresses("OPFMessageCopyToAddresses");
            if let Some(html) = text("OPFMessageCopyHTMLBody").filter(|value| !value.is_empty()) {
                record.bodies.push(body("text/html", &html));
            } else if let Some(plain) = text("OPFMessageCopyBody") {
                record.bodies.push(body("text/olm-body", &plain));
            }
            if let Some(attachments) = find("OPFMessageCopyAttachmentList") {
                for item in attachments
                    .children()
                    .filter(|node| node.has_tag_name("messageAttachment"))
                {
                    let name = item.attribute("OPFAttachmentName").map(str::to_owned);
                    let url = item.attribute("OPFAttachmentURL");
                    let attached = url
                        .filter(|path| !duplicates.contains(*path))
                        .and_then(|path| members.get(path).map(|member| (path, *member)));
                    let mut result = Attachment {
                        media_type: None,
                        content_id: None,
                        name,
                        location: None,
                        representation: "attachment_payload_unavailable",
                        bytes: None,
                        sha256: None,
                    };
                    if let Some((path, (member, length))) = attached {
                        match olm_member(&mut zip, (member, length), MESSAGE_LIMIT, &mut budget) {
                            Ok(bytes) => {
                                result.location = Some(Location::ArchiveMember {
                                    name: path.to_owned(),
                                    index: member,
                                    range: ByteRange::between(0, length),
                                });
                                result.representation = "stored_archive_member";
                                result.bytes = Some(length);
                                result.sha256 = Some(digest(&bytes));
                            }
                            Err(error) => view.issue("olm_attachment_unreadable", error, None),
                        }
                    } else {
                        view.issue(
                            "olm_attachment_missing",
                            "No unambiguous local member for the attachment.",
                            None,
                        );
                    }
                    record.attachments.push(result);
                }
            }
            view.add_record(record);
        }
    }
    if view.records.is_empty() {
        view.issue(
            "olm_no_supported_documents",
            "No supported OLM message or contact document was found.",
            None,
        );
    }
    Ok(())
}

pub fn zip<R: Read + Seek>(input: R, view: &mut Inspection) -> io::Result<()> {
    let mut archive = zip::ZipArchive::new(input)?;
    if archive.len() > ZIP_MEMBER_LIMIT {
        return Err(invalid("ZIP has too many entries."));
    }
    let mut total = 0u64;
    for index in 0..archive.len() {
        let member = match archive.by_index(index) {
            Ok(member) => member,
            Err(error) => {
                view.issue(
                    "zip_member_unreadable",
                    format!("Entry {index}: {error}"),
                    None,
                );
                continue;
            }
        };
        if member.is_dir() {
            continue;
        }
        let name = member.name().to_owned();
        let length = member.size();
        let at = |range| Location::ArchiveMember {
            index,
            name: name.clone(),
            range,
        };
        let full = || Some(at(ByteRange::between(0, length)));
        if member.enclosed_name().is_none() || member.is_symlink() {
            view.issue("zip_member_path", "Member name or type is unsafe.", full());
            continue;
        }
        if length > MESSAGE_LIMIT || length > ZIP_TOTAL_LIMIT.saturating_sub(total) {
            view.issue(
                "zip_member_limit",
                "Member exceeds the expansion limit.",
                full(),
            );
            continue;
        }
        total += length;
        let mut data = Vec::new();
        let result = member.take(length + 1).read_to_end(&mut data);
        if let Err(error) = result {
            view.issue("zip_member_unreadable", error, full());
            continue;
        }
        if data.len() as u64 != length {
            view.issue(
                "zip_member_size",
                "Uncompressed member length does not match.",
                full(),
            );
            continue;
        }
        let kind = identify(Path::new(&name), &data[..data.len().min(8192)], length).format;
        let parsed = match kind {
            Format::Eml => mime::eml(&data, at, view).map(|record| view.add_record(record)),
            Format::Emlx => match emlx_range(&data, length) {
                Some((start, count)) => mime::eml(
                    &data[start as usize..(start + count) as usize],
                    |range| {
                        at(ByteRange {
                            offset: start + range.offset,
                            length: range.length,
                        })
                    },
                    view,
                )
                .map(|record| view.add_record(record)),
                None => Err(invalid("Invalid EMLX byte-count prefix.")),
            },
            Format::Mbox => mime::mbox(&mut Cursor::new(&data), length, at, view),
            Format::Ics | Format::Vcf => {
                text_object(&data, kind, at(ByteRange::between(0, length)), view)
            }
            Format::Unknown => continue,
            _ => {
                view.issue(
                    "zip_member_format",
                    format!("ZIP member format {kind:?} has no inline reader."),
                    full(),
                );
                continue;
            }
        };
        if let Err(error) = parsed {
            view.issue("zip_member_message", error, full());
        }
    }
    if view.records.is_empty() && view.issues.is_empty() {
        view.issue("zip_no_mail", "No supported mail members were found.", None);
    }
    Ok(())
}

pub fn text_object(
    data: &[u8],
    kind: Format,
    location: Location,
    view: &mut Inspection,
) -> io::Result<()> {
    let text = std::str::from_utf8(data)
        .map_err(|_| invalid("Expected UTF-8 for the calendar or contact file."))?;
    let label = if kind == Format::Ics {
        "VCALENDAR"
    } else {
        "VCARD"
    };
    let begin = format!("BEGIN:{label}");
    let end = format!("END:{label}");
    let mut lines = Vec::<String>::new();
    for line in text.trim_start_matches('\u{feff}').lines() {
        if line.starts_with([' ', '\t']) {
            let previous = lines
                .last_mut()
                .ok_or_else(|| invalid("Orphan folded text line."))?;
            previous.push_str(&line[1..]);
        } else {
            lines.push(line.to_owned());
        }
    }
    if !lines
        .first()
        .is_some_and(|line| line.eq_ignore_ascii_case(&begin))
        || !lines
            .iter()
            .rev()
            .find(|line| !line.is_empty())
            .is_some_and(|line| line.eq_ignore_ascii_case(&end))
    {
        return Err(invalid("Expected matching calendar or contact delimiters."));
    }
    let key = if kind == Format::Ics { "SUMMARY" } else { "FN" };
    let mut record = Record::new(
        if kind == Format::Ics {
            "calendar_document"
        } else {
            "contact_document"
        },
        location,
    );
    record.subject = lines
        .iter()
        .filter_map(|line| line.split_once(':'))
        .find_map(|(name, value)| {
            name.split(';')
                .next()
                .filter(|name| name.eq_ignore_ascii_case(key))
                .map(|_| value.to_owned())
        });
    record.bodies.push(body(
        if kind == Format::Ics {
            "text/calendar"
        } else {
            "text/vcard"
        },
        text,
    ));
    view.add_record(record);
    Ok(())
}

pub fn tnef(input: &mut (impl Read + Seek), size: u64, view: &mut Inspection) -> io::Result<()> {
    let data = read_range(input, 0, size)?;
    if data.len() < 6 || !data.starts_with(&[0x78, 0x9f, 0x3e, 0x22]) {
        return Err(invalid("Invalid TNEF signature."));
    }
    let mut record = Record::new(
        "tnef",
        Location::File {
            range: ByteRange::between(0, size),
        },
    );
    let mut attribute = 6;
    let mut pending: Option<(Option<String>, Option<ByteRange>)> = None;
    while attribute < data.len() {
        let heading = data
            .get(attribute..attribute + 9)
            .ok_or_else(|| invalid("TNEF attribute header was cut short."))?;
        let level = heading[0];
        let name = u16::from_le_bytes([heading[1], heading[2]]);
        let count = u32::from_le_bytes(
            heading[5..9]
                .try_into()
                .expect("fixed attribute header size"),
        ) as usize;
        let start = attribute + 9;
        let end = start
            .checked_add(count)
            .filter(|&end| end <= data.len().saturating_sub(2))
            .ok_or_else(|| invalid("TNEF attribute exceeds the source length."))?;
        let value = &data[start..end];
        let checksum = u16::from_le_bytes([data[end], data[end + 1]]);
        let range = ByteRange::between(start as u64, end as u64);
        attribute = end + 2;
        if value
            .iter()
            .fold(0u16, |sum, &byte| sum.wrapping_add(u16::from(byte)))
            != checksum
        {
            view.issue(
                "tnef_checksum",
                "Stored attribute checksum does not match.",
                Some(Location::File { range }),
            );
            if name == 0x9002 {
                flush_tnef(&mut record, pending.take(), &data);
            }
            continue;
        }
        match (level, name) {
            (1, 0x8004) => {
                record.subject = Some(
                    encoding_rs::WINDOWS_1252
                        .decode(value)
                        .0
                        .trim_end_matches('\0')
                        .to_owned(),
                )
            }
            (1, 0x800c) => record.bodies.push(body(
                "text/plain",
                &encoding_rs::WINDOWS_1252.decode(value).0,
            )),
            (2, 0x9002) => {
                flush_tnef(&mut record, pending.take(), &data);
                pending = Some((None, None));
            }
            (2, 0x8010) => {
                if let Some((title, _)) = &mut pending {
                    *title = Some(
                        encoding_rs::WINDOWS_1252
                            .decode(value)
                            .0
                            .trim_end_matches('\0')
                            .to_owned(),
                    );
                }
            }
            (2, 0x800f) => {
                if let Some((_, contents)) = &mut pending {
                    *contents = Some(range);
                }
            }
            (1 | 2, 0x9003 | 0x9005) => {
                view.issue(
                    "tnef_mapi_property_list",
                    "The stored TNEF MAPI list needs a property decoder.",
                    Some(Location::File { range }),
                );
            }
            _ => {}
        }
    }
    flush_tnef(&mut record, pending, &data);
    view.add_record(record);
    Ok(())
}

fn flush_tnef(
    record: &mut Record,
    pending: Option<(Option<String>, Option<ByteRange>)>,
    input: &[u8],
) {
    if let Some((name, range)) = pending {
        record.attachments.push(Attachment {
            media_type: None,
            content_id: None,
            name,
            location: range.map(|range| Location::File { range }),
            representation: if range.is_some() {
                "stored_tnef_attribute"
            } else {
                "attachment_payload_unavailable"
            },
            bytes: range.map(|range| range.length),
            sha256: range.map(|range| {
                digest(&input[range.offset as usize..(range.offset + range.length) as usize])
            }),
        });
    }
}

const CMS_SIGNED: &[u8] = b"\x2a\x86\x48\x86\xf7\x0d\x01\x07\x02";
const CMS_DATA: &[u8] = b"\x2a\x86\x48\x86\xf7\x0d\x01\x07\x01";

pub fn cms(input: &mut (impl Read + Seek), size: u64, view: &mut Inspection) -> io::Result<()> {
    let data = read_range(input, 0, size)?;
    let mut contents = data.as_slice();
    let mut outer = der(&mut contents, 0x30)?;
    if !contents.is_empty() {
        return Err(invalid("Unexpected bytes after CMS ContentInfo."));
    }
    let oid = der(&mut outer, 0x06)?;
    if oid != CMS_SIGNED {
        view.issue("cms_content_unavailable", "Only embedded content in DER CMS SignedData can be read. Encrypted content requires its key.", None);
        return Ok(());
    }
    let mut explicit = der(&mut outer, 0xa0)?;
    let mut signed = der(&mut explicit, 0x30)?;
    der(&mut signed, 0x02)?;
    der(&mut signed, 0x31)?;
    let mut embedded = der(&mut signed, 0x30)?;
    if der(&mut embedded, 0x06)? != CMS_DATA || embedded.is_empty() {
        view.issue(
            "cms_detached_or_unhandled",
            "No embedded CMS Data payload.",
            None,
        );
        return Ok(());
    }
    let mut data_value = der(&mut embedded, 0xa0)?;
    let mail = der(&mut data_value, 0x04)?;
    let start = (mail.as_ptr().addr() - data.as_ptr().addr()) as u64;
    view.issue(
        "cms_signature_unverified",
        "Embedded bytes were read. Signer identity and signature validity were not verified.",
        None,
    );
    let record = if mime::looks_like_message(mail) {
        mime::eml(
            mail,
            |range| Location::File {
                range: ByteRange {
                    offset: range.offset + start,
                    length: range.length,
                },
            },
            view,
        )?
    } else {
        let range = ByteRange::between(start, start + mail.len() as u64);
        let mut record = Record::new("cms_signed_payload", Location::File { range });
        record.attachments.push(Attachment {
            media_type: None,
            content_id: None,
            name: None,
            location: Some(Location::File { range }),
            representation: "unverified_cms_payload",
            bytes: Some(range.length),
            sha256: Some(digest(mail)),
        });
        record
    };
    view.add_record(record);
    Ok(())
}

fn der<'a>(input: &mut &'a [u8], expected: u8) -> io::Result<&'a [u8]> {
    let tag = *input.first().ok_or_else(|| invalid("Missing DER tag."))?;
    let marker = *input.get(1).ok_or_else(|| invalid("Missing DER length."))?;
    if tag != expected {
        return Err(invalid("Unexpected DER tag."));
    }
    let (header, length) = if marker < 128 {
        (2, usize::from(marker))
    } else {
        let n = usize::from(marker & 127);
        if n == 0 || n > 4 {
            return Err(invalid("DER length is indefinite or too wide."));
        }
        let bytes = input
            .get(2..2 + n)
            .ok_or_else(|| invalid("Truncated DER length."))?;
        if bytes[0] == 0 {
            return Err(invalid("Non-minimal DER length."));
        }
        let length = bytes
            .iter()
            .fold(0usize, |acc, &byte| (acc << 8) | usize::from(byte));
        if length < 128 {
            return Err(invalid("Non-minimal DER length."));
        }
        (2 + n, length)
    };
    let end = header
        .checked_add(length)
        .filter(|end| *end <= input.len())
        .ok_or_else(|| invalid("Truncated DER value."))?;
    let (whole, rest) = input.split_at(end);
    *input = rest;
    Ok(&whole[header..])
}
