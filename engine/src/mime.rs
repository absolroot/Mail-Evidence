use crate::{
    collect::{digest, emlx_range},
    model::{Attachment, Body, ByteRange, Header, HeaderBlock, Inspection, Location, Record},
};
use mail_parser::MessageParser;
use std::{
    borrow::Cow,
    io::{self, BufRead, BufReader, Read, Seek, SeekFrom},
    path::Path,
};

pub const MESSAGE_LIMIT: u64 = 128 * 1024 * 1024;
pub const BODY_LIMIT: usize = 1024 * 1024;
const HEADER_LIMIT: u64 = 128 * 1024;
const MARKER_LIMIT: usize = 512;
const MAX_MARKERS: usize = 250_000;

pub fn read_range(input: &mut (impl Read + Seek), start: u64, length: u64) -> io::Result<Vec<u8>> {
    if length > MESSAGE_LIMIT {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "Record exceeds the 128 MiB read limit.",
        ));
    }
    input.seek(SeekFrom::Start(start))?;
    let mut data = Vec::with_capacity(length as usize);
    input.take(length).read_to_end(&mut data)?;
    if data.len() as u64 != length {
        return Err(io::ErrorKind::UnexpectedEof.into());
    }
    Ok(data)
}

pub fn read_emlx(input: &mut (impl Read + Seek), length: u64) -> io::Result<(u64, Vec<u8>)> {
    let prefix = read_range(input, 0, length.min(24))?;
    let (start, count) = emlx_range(&prefix, length).ok_or_else(|| {
        io::Error::new(
            io::ErrorKind::InvalidData,
            "Invalid EMLX byte-count prefix.",
        )
    })?;
    Ok((start, read_range(input, start, count)?))
}

pub fn looks_like_message(data: &[u8]) -> bool {
    let mut fields = 0;
    for line in data.split_inclusive(|&b| b == b'\n').take(160) {
        if !line.ends_with(b"\n") {
            break;
        }
        let line = line.trim_ascii_end();
        if line.is_empty() {
            return fields > 0;
        }
        if matches!(line.first(), Some(b' ' | b'\t')) {
            if fields == 0 {
                return false;
            }
            continue;
        }
        if header_name(line).is_none() {
            return false;
        }
        fields += 1;
    }
    fields >= 2
}

fn header_name(line: &[u8]) -> Option<&[u8]> {
    let split = line.iter().position(|&b| b == b':')?;
    let name = &line[..split];
    (!name.is_empty() && name.iter().all(|b| (33..=126).contains(b))).then_some(name)
}

pub(crate) fn parse_message(data: &[u8]) -> io::Result<mail_parser::Message<'_>> {
    MessageParser::default()
        .parse(data)
        .filter(|message| !message.headers().is_empty())
        .ok_or_else(|| {
            io::Error::new(
                io::ErrorKind::InvalidData,
                "No RFC message header fields were parsed.",
            )
        })
}

#[derive(Clone, Copy)]
pub enum Export {
    Eml,
    Headers,
    Attachment(usize),
}

impl Export {
    pub fn parse(value: &str) -> Option<Self> {
        match value {
            "eml" => Some(Self::Eml),
            "headers" => Some(Self::Headers),
            _ => value
                .strip_prefix("attachment:")?
                .parse()
                .ok()
                .map(Self::Attachment),
        }
    }

    pub fn bytes(self, message: &[u8]) -> io::Result<Cow<'_, [u8]>> {
        if matches!(self, Self::Eml) {
            return Ok(Cow::Borrowed(message));
        }
        let parsed = parse_message(message)?;
        match self {
            Self::Headers => {
                let end = parsed
                    .headers()
                    .iter()
                    .map(|field| field.offset_end())
                    .max()
                    .unwrap_or(0);
                message
                    .get(..end as usize)
                    .map(Cow::Borrowed)
                    .ok_or_else(|| {
                        io::Error::new(
                            io::ErrorKind::InvalidData,
                            "Header range exceeds the message.",
                        )
                    })
            }
            Self::Attachment(index) => {
                let part = parsed.attachments().nth(index).ok_or_else(|| {
                    io::Error::new(io::ErrorKind::NotFound, "Attachment index was not found.")
                })?;
                decoded_attachment(part).map(Cow::Owned)
            }
            Self::Eml => unreachable!(),
        }
    }
}

pub(crate) fn decoded_attachment(part: mail_parser::MessagePart<'_>) -> io::Result<Vec<u8>> {
    let (bytes, problems) = part.decoded_checked();
    if !problems.is_empty() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            format!("Attachment decode: {problems:?}"),
        ));
    }
    Ok(bytes.into_owned())
}

pub fn eml(
    data: &[u8],
    at: impl Fn(ByteRange) -> Location,
    view: &mut Inspection,
) -> io::Result<Record> {
    message(data, at, view, None)
}

pub fn emlx(
    data: &[u8],
    at: impl Fn(ByteRange) -> Location,
    view: &mut Inspection,
    source: &Path,
) -> io::Result<Record> {
    message(data, at, view, Some(source))
}

fn message(
    data: &[u8],
    at: impl Fn(ByteRange) -> Location,
    view: &mut Inspection,
    emlx_source: Option<&Path>,
) -> io::Result<Record> {
    let parsed = parse_message(data)?;
    let mut sidecars = emlx_source.map_or_else(Default::default, |source| {
        crate::apple::attachments(source, &parsed, data, &at, view)
    });
    let mut record = Record::new("mail", at(ByteRange::between(0, data.len() as u64)));
    record.subject = parsed.subject().map(str::to_owned);
    record.message_id = parsed.message_id().map(str::to_owned);
    let headers = parsed.headers();
    let mut fields = Vec::with_capacity(headers.len());
    let mut max_end = 0u64;
    for field in headers.iter() {
        let start = u64::from(field.offset_field());
        let end = u64::from(field.offset_end());
        if start > end || end > data.len() as u64 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "Message parser returned a header outside the payload.",
            ));
        }
        max_end = max_end.max(end);
        let value = String::from_utf8_lossy(field.raw_value()).trim().to_owned();
        match field.raw_name().to_ascii_lowercase().as_str() {
            "from" if record.from.is_none() => record.from = Some(value.clone()),
            "to" if record.to.is_none() => record.to = Some(value.clone()),
            "date" if record.date_raw.is_none() => record.date_raw = Some(value.clone()),
            _ => {}
        }
        fields.push(Header {
            name: field.raw_name().to_owned(),
            value,
            relative_range: Some(ByteRange::between(start, end)),
        });
    }
    let range = ByteRange::between(0, max_end);
    record.headers = Some(HeaderBlock {
        representation: "stored_message_headers",
        code_page: None,
        location: at(range),
        bytes: range.length,
        sha256: digest(&data[..max_end as usize]),
        fields,
    });
    for (media_type, parts) in [
        ("text/plain", parsed.text_body().collect::<Vec<_>>()),
        ("text/html", parsed.html_body().collect::<Vec<_>>()),
    ] {
        for part in parts {
            if sidecars.contains_key(&part.id()) {
                continue;
            }
            // The actual type avoids labeling reader-selected fallback parts as another MIME variant.
            if !part.is_content_type(
                "text",
                if media_type == "text/plain" {
                    "plain"
                } else {
                    "html"
                },
            ) && (media_type != "text/plain" || part.content_type().is_some())
            {
                continue;
            }
            if let Some(value) = part.text_prefix(BODY_LIMIT + 1) {
                record.bodies.push(body(media_type, value.as_ref()));
            }
        }
    }
    for part in parsed.attachments() {
        if let Some(attachment) = sidecars.remove(&part.id()) {
            record.attachments.push(attachment);
            continue;
        }
        let start = u64::from(part.offset_body());
        let end = u64::from(part.offset_end());
        let location =
            (start <= end && end <= data.len() as u64).then(|| at(ByteRange::between(start, end)));
        let (decoded, decode) = part.decoded_checked();
        if !decode.is_empty() {
            view.issue("mime_attachment_decode", format!("{decode:?}"), location);
            record.attachments.push(Attachment {
                media_type: part.content_type().and_then(|kind| {
                    kind.subtype()
                        .map(|subtype| format!("{}/{}", kind.ctype(), subtype))
                }),
                content_id: part.content_id().map(str::to_owned),
                name: part.attachment_name().map(str::to_owned),
                location: None,
                representation: "unreadable",
                bytes: None,
                sha256: None,
            });
        } else {
            record.attachments.push(Attachment {
                media_type: part.content_type().and_then(|kind| {
                    kind.subtype()
                        .map(|subtype| format!("{}/{}", kind.ctype(), subtype))
                }),
                content_id: part.content_id().map(str::to_owned),
                name: part.attachment_name().map(str::to_owned),
                location,
                representation: "decoded_mime_part",
                bytes: Some(decoded.len() as u64),
                sha256: Some(digest(&decoded)),
            });
        }
    }
    record.attachments.extend(sidecars.into_values());
    Ok(record)
}

pub fn body(media_type: &str, text: &str) -> Body {
    let bound = text.floor_char_boundary(text.len().min(BODY_LIMIT));
    Body {
        media_type: media_type.to_owned(),
        text: text[..bound].to_owned(),
        truncated: bound < text.len(),
    }
}

pub fn mbox_envelope(line: &[u8]) -> bool {
    let Some(Ok(rest)) = line.strip_prefix(b"From ").map(std::str::from_utf8) else {
        return false;
    };
    let words: Vec<_> = rest.split_ascii_whitespace().take(13).collect();
    let [_, weekday, month, day, clock, tail @ ..] = words.as_slice() else {
        return false;
    };
    if !matches!(
        *weekday,
        "Mon" | "Tue" | "Wed" | "Thu" | "Fri" | "Sat" | "Sun"
    ) || !matches!(
        *month,
        "Jan"
            | "Feb"
            | "Mar"
            | "Apr"
            | "May"
            | "Jun"
            | "Jul"
            | "Aug"
            | "Sep"
            | "Oct"
            | "Nov"
            | "Dec"
    ) || !day.parse::<u8>().is_ok_and(|day| (1..=31).contains(&day))
    {
        return false;
    }
    let mut time = clock.split(':');
    let valid_time = [23, 59, 60].into_iter().all(|max| {
        time.next()
            .is_some_and(|part| part.len() == 2 && part.parse::<u8>().is_ok_and(|n| n <= max))
    }) && time.next().is_none();
    valid_time
        && tail
            .iter()
            .take(2)
            .any(|word| word.len() == 4 && word.bytes().all(|b| b.is_ascii_digit()))
}

struct Marker {
    start: u64,
    body: u64,
    blank_start: Option<u64>,
    complete: bool,
}

pub fn mbox(
    input: &mut (impl Read + Seek),
    size: u64,
    at: impl Fn(ByteRange) -> Location,
    view: &mut Inspection,
) -> io::Result<()> {
    if size == 0 {
        return Ok(());
    }
    input.seek(SeekFrom::Start(0))?;
    let mut reader = BufReader::with_capacity(128 * 1024, &mut *input);
    let mut markers = Vec::new();
    let mut position = 0;
    let mut start = 0;
    let mut previous_blank = None;
    let mut prefix = Vec::with_capacity(MARKER_LIMIT);
    loop {
        let data = reader.fill_buf()?;
        if data.is_empty() {
            break;
        }
        let newline = memchr::memchr(b'\n', data);
        let count = newline.map_or(data.len(), |index| index + 1);
        prefix.extend_from_slice(&data[..count.min(MARKER_LIMIT - prefix.len())]);
        reader.consume(count);
        position += count as u64;
        if newline.is_none() {
            continue;
        }
        if position - start < MARKER_LIMIT as u64 && mbox_envelope(&prefix) {
            if markers.len() == MAX_MARKERS {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    "MBOX envelope count exceeds the parser limit.",
                ));
            }
            markers.push(Marker {
                start,
                body: position,
                blank_start: previous_blank,
                complete: true,
            });
        }
        previous_blank = matches!(prefix.as_slice(), b"\n" | b"\r\n").then_some(start);
        start = position;
        prefix.clear();
    }
    if position != size {
        return Err(io::ErrorKind::UnexpectedEof.into());
    }
    if !prefix.is_empty() {
        if position - start < MARKER_LIMIT as u64 && mbox_envelope(&prefix) {
            markers.push(Marker {
                start,
                body: position,
                blank_start: previous_blank,
                complete: false,
            });
        }
        previous_blank = None;
    }
    let prefix_end = markers.first().map_or(size, |entry| entry.start);
    if prefix_end != 0 || markers.is_empty() {
        view.issue(
            "mbox_no_confirmed_envelope",
            "No supported boundary in this range.",
            Some(at(ByteRange::between(0, prefix_end))),
        );
    }
    let probes = markers
        .iter()
        .map(|marker| {
            if marker.complete && (marker.start == 0 || marker.blank_start.is_some()) {
                probe_headers(input, marker.body, size)
            } else {
                Ok(None)
            }
        })
        .collect::<io::Result<Vec<_>>>()?;
    let mut quarantine_until = 0;
    for (index, marker) in markers.iter().enumerate() {
        let next = markers.get(index + 1);
        let limit = next.map_or(size, |marker| marker.start);
        let full = Some(at(ByteRange::between(marker.start, limit)));
        if marker.start < quarantine_until {
            view.issue(
                "mbox_content_length",
                "The previous declared body overlaps this candidate.",
                full,
            );
            continue;
        }
        let Some((declared_end, invalid)) = probes[index] else {
            view.issue(
                "mbox_boundary",
                "Envelope, header fields or preceding separator could not be confirmed.",
                full,
            );
            continue;
        };
        if next.is_some() && probes[index + 1].is_none() {
            view.issue(
                "mbox_boundary",
                "The next candidate does not establish an unambiguous end boundary.",
                full,
            );
            continue;
        }
        let blank = next.map_or(previous_blank, |marker| marker.blank_start);
        let end = blank.unwrap_or(limit).clamp(marker.body, limit);
        if invalid || declared_end.is_some_and(|declared| declared != end) {
            quarantine_until = declared_end.unwrap_or(0);
            view.issue(
                "mbox_content_length",
                "Content-Length and the message boundary disagree.",
                full,
            );
            continue;
        }
        let parsed = read_range(input, marker.body, end - marker.body).and_then(|data| {
            eml(
                &data,
                |range| {
                    at(ByteRange {
                        offset: range.offset + marker.body,
                        length: range.length,
                    })
                },
                view,
            )
        });
        match parsed {
            Ok(mut record) => {
                record.envelope = Some(at(ByteRange::between(marker.start, marker.body)));
                record.separator = Some(at(ByteRange::between(end, limit)));
                view.add_record(record);
            }
            Err(error) => view.issue("mbox_message_read", error, full),
        }
    }
    Ok(())
}

fn probe_headers(
    input: &mut (impl Read + Seek),
    start: u64,
    size: u64,
) -> io::Result<Option<(Option<u64>, bool)>> {
    let mut fields = false;
    let mut declared: Option<u64> = None;
    let mut invalid = false;
    let mut is_length = false;
    let mut offset = start;
    let bytes = read_range(input, start, (size - start).min(HEADER_LIMIT))?;
    for line in bytes.split_inclusive(|&b| b == b'\n') {
        offset += line.len() as u64;
        if !line.ends_with(b"\n") {
            break;
        }
        if matches!(line, b"\n" | b"\r\n") {
            return Ok(
                fields.then(|| (declared.map(|value| offset.saturating_add(value)), invalid))
            );
        }
        if matches!(line.first(), Some(b' ' | b'\t')) {
            if !fields {
                return Ok(None);
            }
            invalid |= is_length;
            continue;
        }
        let Some(name) = header_name(line) else {
            return Ok(None);
        };
        fields = true;
        is_length = name.eq_ignore_ascii_case(b"Content-Length");
        if is_length {
            let length = std::str::from_utf8(line[name.len() + 1..].trim_ascii())
                .ok()
                .filter(|s| s.bytes().all(|b| b.is_ascii_digit()))
                .and_then(|s| s.parse().ok());
            invalid |= declared.is_some() || length.is_none();
            if declared.is_none() {
                declared = length;
            }
        }
    }
    Ok(None)
}
