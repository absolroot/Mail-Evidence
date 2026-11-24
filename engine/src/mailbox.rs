use crate::{
    mime::{self, Export, mbox_envelope, read_range},
    model::{ByteRange, Format, Inspection, Location, MailboxLabels, MhSequence},
};
use std::io::{self, BufRead, BufReader, Read, Seek, SeekFrom};

const MMDF: &[u8] = b"\x01\x01\x01\x01";
const BABYL_OPEN: &[u8] = b"\x1f\x0c";
const BABYL_CLOSE: &[u8] = b"\x1f";
const RECORD_LIMIT: usize = 250_000;
const METADATA_LIMIT: u64 = 1024 * 1024;

struct Entry {
    marker: ByteRange,
    message: ByteRange,
    closing_eol: usize,
    closed: bool,
}

fn invalid(detail: &'static str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, detail)
}

fn at(range: ByteRange) -> Location {
    Location::File { range }
}

fn split_line(bytes: &[u8]) -> (&[u8], usize) {
    let length = memchr::memchr(b'\n', bytes).map_or(bytes.len(), |index| index + 1);
    let line = &bytes[..length];
    (without_eol(line), length)
}

fn without_eol(line: &[u8]) -> &[u8] {
    line.strip_suffix(b"\r\n")
        .or_else(|| line.strip_suffix(b"\n"))
        .unwrap_or(line)
}

fn offsets(input: &mut (impl Read + Seek), size: u64, format: Format) -> io::Result<Vec<Entry>> {
    if size == 0 {
        return Ok(Vec::new());
    }
    input.seek(SeekFrom::Start(0))?;
    let mut reader = BufReader::with_capacity(128 * 1024, input.take(size));
    let mut current: Option<ByteRange> = None;
    let mut entries = Vec::new();
    let mut position = 0;
    let mut prefix = Vec::with_capacity(32);
    let mut start = 0;
    let mut previous_eol = 0;
    let mut preceding_byte = None;
    loop {
        let buffer = reader.fill_buf()?;
        let newline = memchr::memchr(b'\n', buffer);
        let count = newline.map_or(buffer.len(), |index| index + 1);
        prefix.extend_from_slice(&buffer[..count.min(32 - prefix.len())]);
        let eol = if newline.is_some() {
            let before_newline = if count >= 2 {
                Some(buffer[count - 2])
            } else {
                preceding_byte
            };
            usize::from(before_newline == Some(b'\r')) + 1
        } else {
            0
        };
        if count != 0 {
            preceding_byte = Some(buffer[count - 1]);
        }
        reader.consume(count);
        position += count as u64;
        let eof = count == 0;
        if newline.is_none() && !eof {
            continue;
        }
        if eof && start == position {
            break;
        }
        let line = if position - start <= 32 {
            without_eol(&prefix)
        } else {
            &[]
        };
        if start == 0 && format == Format::Babyl && line != b"BABYL OPTIONS:" {
            return Err(invalid("The mailbox has no BABYL OPTIONS header."));
        }
        let (closes, opens) = match format {
            Format::Mmdf => (
                line == MMDF && current.is_some(),
                line == MMDF && current.is_none() && !eof,
            ),
            Format::Babyl => (
                line == BABYL_OPEN || line == BABYL_CLOSE,
                line == BABYL_OPEN && !eof,
            ),
            _ => return Err(invalid("Unsupported mailbox format.")),
        };
        if closes && let Some(marker) = current.take() {
            let payload = marker.offset + marker.length;
            entries.push(Entry {
                marker,
                message: ByteRange::between(payload, start),
                closing_eol: (previous_eol as u64).min(start - payload) as usize,
                closed: true,
            });
        }
        if opens {
            if entries.len() == RECORD_LIMIT {
                return Err(invalid(
                    "Mailbox boundary count exceeds the inspection limit.",
                ));
            }
            current = Some(ByteRange::between(start, position));
        }
        previous_eol = eol;
        start = position;
        prefix.clear();
        if eof {
            break;
        }
    }
    if position != size {
        return Err(io::ErrorKind::UnexpectedEof.into());
    }
    if let Some(marker) = current {
        entries.push(Entry {
            marker,
            message: ByteRange::between(marker.offset + marker.length, size),
            closing_eol: 0,
            closed: false,
        });
    }
    Ok(entries)
}

struct Message {
    data: Vec<u8>,
    segments: Vec<ByteRange>,
    envelope: Option<ByteRange>,
    labels: Option<MailboxLabels>,
}

impl Message {
    fn location(&self, marker: u64, logical_range: ByteRange) -> Location {
        if let [segment] = self.segments.as_slice() {
            return at(ByteRange {
                offset: segment.offset + logical_range.offset,
                length: logical_range.length,
            });
        }
        let end = logical_range.offset + logical_range.length;
        let mut cursor = 0;
        let mut ranges = Vec::new();
        for segment in &self.segments {
            let start = cursor.max(logical_range.offset);
            let overlap_end = (cursor + segment.length).min(end);
            if start < overlap_end {
                ranges.push(ByteRange {
                    offset: segment.offset + start - cursor,
                    length: overlap_end - start,
                });
            }
            cursor += segment.length;
        }
        Location::Segments {
            index_offset: marker,
            logical_range,
            ranges,
        }
    }
}

fn payload(input: &mut (impl Read + Seek), format: Format, entry: &Entry) -> io::Result<Message> {
    let base = entry.message.offset;
    let mut data = read_range(input, base, entry.message.length)?;
    if format == Format::Mmdf {
        data.truncate(data.len() - entry.closing_eol);
        let (first, first_end) = split_line(&data);
        let envelope =
            mbox_envelope(first).then_some(ByteRange::between(base, base + first_end as u64));
        let start = envelope.map_or(base, |range| range.offset + range.length);
        if envelope.is_some() {
            data.drain(..first_end);
        }
        return Ok(Message {
            segments: vec![ByteRange::between(start, start + data.len() as u64)],
            data,
            envelope,
            labels: None,
        });
    }
    let (first, first_end) = split_line(&data);
    if !(first.starts_with(b"0,") || first.starts_with(b"1,"))
        || first_end > METADATA_LIMIT as usize
    {
        return Err(invalid("Invalid Babyl message label line."));
    }
    let formatted = first[0] == b'1';
    let label_text =
        std::str::from_utf8(&first[2..]).map_err(|_| invalid("Babyl labels are not UTF-8."))?;
    let labels = MailboxLabels {
        range: ByteRange::between(base, base + first_end as u64),
        values: label_text
            .split(',')
            .map(str::trim)
            .filter(|value| !value.is_empty())
            .map(str::to_owned)
            .collect(),
    };
    let original_end = line_after(&data, first_end, b"*** EOOH ***")?;
    if !formatted {
        if original_end.0 != first_end {
            return Err(invalid(
                "Unformatted Babyl has data before the original header boundary.",
            ));
        }
        let start = original_end.1;
        let range = ByteRange::between(base + start as u64, base + data.len() as u64);
        data.drain(..start);
        return Ok(Message {
            data,
            segments: vec![range],
            envelope: None,
            labels: Some(labels),
        });
    }
    data.truncate(data.len() - entry.closing_eol);
    let body = line_after(&data, original_end.1, b"")?.1;
    let original = first_end..original_end.0;
    if original.is_empty()
        || !data[original.clone()].ends_with(b"\n\n")
            && !data[original.clone()].ends_with(b"\r\n\r\n")
    {
        return Err(invalid(
            "Babyl original headers have no stored header/body boundary.",
        ));
    }
    let segments = vec![
        ByteRange::between(base + original.start as u64, base + original.end as u64),
        ByteRange::between(base + body as u64, base + data.len() as u64),
    ];
    data.drain(original.end..body);
    data.drain(..original.start);
    Ok(Message {
        data,
        segments,
        envelope: None,
        labels: Some(labels),
    })
}

fn line_after(data: &[u8], mut start: usize, expected: &[u8]) -> io::Result<(usize, usize)> {
    let limit = data.len().min(start + METADATA_LIMIT as usize);
    while start < limit {
        let (line, length) = split_line(&data[start..limit]);
        if line == expected && data[start + length - 1] == b'\n' {
            return Ok((start, start + length));
        }
        start += length;
    }
    Err(invalid("A Babyl header boundary is missing."))
}

pub fn inspect(
    input: &mut (impl Read + Seek),
    size: u64,
    format: Format,
    view: &mut Inspection,
) -> io::Result<()> {
    let entries = offsets(input, size, format)?;
    if entries.is_empty() && size != 0 && format == Format::Mmdf {
        return Err(invalid("The MMDF mailbox contains no opening separator."));
    }
    for entry in entries {
        if !entry.closed {
            view.issue(
                "mailbox_unclosed_message",
                "The message has no closing delimiter.",
                Some(at(entry.marker)),
            );
        }
        let record = payload(input, format, &entry).and_then(|mut message| {
            let mut record = mime::eml(
                &message.data,
                |range| message.location(entry.marker.offset, range),
                view,
            )?;
            record.mailbox_entry_offset = Some(entry.marker.offset);
            record.separator = Some(at(entry.marker));
            record.envelope = message.envelope.map(at);
            record.mailbox_labels = message.labels.take();
            Ok(record)
        });
        match record {
            Ok(record) => view.add_record(record),
            Err(error) => view.issue("mailbox_message_unreadable", error, Some(at(entry.message))),
        }
    }
    Ok(())
}

pub fn export(
    input: &mut (impl Read + Seek),
    size: u64,
    format: Format,
    offset: u64,
    selector: Export,
) -> io::Result<Vec<u8>> {
    if !matches!(format, Format::Babyl | Format::Mmdf) {
        return Err(invalid("Mailbox export requires an MMDF or Babyl source."));
    }
    let entries = offsets(input, size, format)?;
    let entry = entries
        .iter()
        .find(|entry| entry.marker.offset == offset && entry.closed)
        .ok_or_else(|| invalid("No complete mailbox message begins at this offset."))?;
    let message = payload(input, format, entry)?;
    if matches!(selector, Export::Eml) {
        Ok(message.data)
    } else {
        Ok(selector.bytes(&message.data)?.into_owned())
    }
}

pub fn mh_sequences(
    input: &mut (impl Read + Seek),
    size: u64,
    view: &mut Inspection,
) -> io::Result<()> {
    if size > METADATA_LIMIT {
        return Err(invalid("MH sequence metadata exceeds the read limit."));
    }
    let data = read_range(input, 0, size)?;
    let mut offset = 0;
    for physical in data.split_inclusive(|&byte| byte == b'\n') {
        let line = without_eol(physical);
        let range = ByteRange::between(offset, offset + physical.len() as u64);
        offset += physical.len() as u64;
        if line.iter().all(u8::is_ascii_whitespace) {
            continue;
        }
        match sequence(line, range) {
            Some(sequence) => view.mh_sequences.push(sequence),
            None => view.issue(
                "mh_sequence_syntax",
                "Invalid sequence name or message-number range.",
                Some(at(range)),
            ),
        }
    }
    Ok(())
}

fn sequence(line: &[u8], range: ByteRange) -> Option<MhSequence> {
    let (name, numbers) = std::str::from_utf8(line).ok()?.split_once(':')?;
    if name.is_empty()
        || !name
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_'))
    {
        return None;
    }
    let mut members = Vec::new();
    for member in numbers.split_ascii_whitespace() {
        let (lo, hi) = member.split_once('-').unwrap_or((member, member));
        let (lo, hi) = (lo.parse().ok()?, hi.parse().ok()?);
        if lo == 0 || lo > hi {
            return None;
        }
        members.push([lo, hi]);
    }
    Some(MhSequence {
        name: name.to_owned(),
        members,
        range,
    })
}
