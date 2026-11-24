use crate::{
    mime::{MESSAGE_LIMIT, eml, read_range},
    model::{ByteRange, ExpressFolder, IdxEntry, Inspection, Location, MbxEntry},
};
use std::{
    collections::HashSet,
    io::{self, Read, Seek},
};

fn invalid(detail: &str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, detail)
}

fn u32_at(data: &[u8], at: usize) -> io::Result<u32> {
    let bytes = data
        .get(at..at + 4)
        .ok_or_else(|| invalid("Missing 32-bit index value."))?;
    Ok(u32::from_le_bytes(
        bytes.try_into().expect("fixed index width"),
    ))
}

fn bounded(
    input: &mut (impl Read + Seek),
    size: u64,
    start: u64,
    bytes: u64,
) -> io::Result<Vec<u8>> {
    if start.checked_add(bytes).is_none_or(|end| end > size) {
        return Err(invalid("Record points outside the source."));
    }
    read_range(input, start, bytes)
}

enum TreeWalk {
    Node { start: u64, parent: u64 },
    Record(u64),
}

fn dbx_index(
    input: &mut (impl Read + Seek),
    size: u64,
    subtype: u8,
) -> io::Result<(Vec<u64>, Option<io::Error>)> {
    let head = bounded(input, size, 0, 0xe8)?;
    if head[..8] != [0xcf, 0xad, 0x12, 0xfe, subtype, 0xfd, 0x74, 0x6f] {
        return Err(invalid(
            "DBX signature does not identify the required index type.",
        ));
    }
    let count = u32_at(&head, 0xc4)? as usize;
    if count > 250_000 {
        return Err(invalid("DBX record count exceeds the parser limit."));
    }
    let root = u64::from(u32_at(&head, 0xe4)?);
    let mut ordered = Vec::with_capacity(count);
    let issue = dbx_walk(input, size, root, count, &mut ordered).err();
    Ok((ordered, issue))
}

fn dbx_walk(
    input: &mut (impl Read + Seek),
    size: u64,
    root: u64,
    count: usize,
    ordered: &mut Vec<u64>,
) -> io::Result<()> {
    let mut nodes = HashSet::new();
    let mut rows = HashSet::new();
    let mut tasks = vec![TreeWalk::Node {
        start: root,
        parent: 0,
    }];
    while let Some(task) = tasks.pop() {
        match task {
            TreeWalk::Node { start: 0, .. } => {}
            TreeWalk::Node { start, parent } => {
                if !nodes.insert(start) || nodes.len() > 25_000 {
                    return Err(invalid(
                        "DBX index node is repeated or the tree exceeds its limit.",
                    ));
                }
                let node = bounded(input, size, start, 0x27c)?;
                let stored = u64::from(u32_at(&node, 0)?);
                let stored_parent = u64::from(u32_at(&node, 12)?);
                let entries = usize::from(node[17]);
                if start != stored || parent != stored_parent || entries > 0x33 {
                    return Err(invalid(
                        "DBX B-tree pointer, parent or entry count is invalid.",
                    ));
                }
                for entry in node[24..24 + entries * 12].as_chunks::<12>().0.iter().rev() {
                    let next = u64::from(u32_at(entry, 4)?);
                    let index = u64::from(u32_at(entry, 0)?);
                    tasks.push(TreeWalk::Node {
                        start: next,
                        parent: start,
                    });
                    tasks.push(TreeWalk::Record(index));
                }
                tasks.push(TreeWalk::Node {
                    start: u64::from(u32_at(&node, 8)?),
                    parent: start,
                });
            }
            TreeWalk::Record(index) => {
                if index == 0 || !rows.insert(index) || rows.len() > count {
                    return Err(invalid(
                        "DBX has an invalid, repeated or unexpected message pointer.",
                    ));
                }
                ordered.push(index);
            }
        }
    }
    if count != ordered.len() {
        return Err(invalid("DBX tree count differs from the index header."));
    }
    Ok(())
}

pub fn dbx(input: &mut (impl Read + Seek), size: u64, view: &mut Inspection) -> io::Result<()> {
    let (entries, issue) = dbx_index(input, size, 0xc5)?;
    for offset in entries {
        match dbx_payload(input, size, offset).and_then(|(data, segments)| {
            let mut record = eml(&data, |range| dbx_location(offset, &segments, range), view)?;
            record.mailbox_entry_offset = Some(offset);
            Ok(record)
        }) {
            Ok(record) => view.add_record(record),
            Err(error) => view.issue("dbx_message_read", error, Some(file_range(offset, 0))),
        }
    }
    issue.map_or(Ok(()), Err)
}

fn file_range(offset: u64, length: u64) -> Location {
    Location::File {
        range: ByteRange { offset, length },
    }
}

struct DbxFields {
    bytes: Vec<u8>,
    entries_len: usize,
}

impl DbxFields {
    fn read(input: &mut (impl Read + Seek), size: u64, offset: u64) -> io::Result<Self> {
        let info = bounded(input, size, offset, 12)?;
        if u64::from(u32_at(&info, 0)?) != offset {
            return Err(invalid(
                "DBX indexed metadata does not match its own pointer.",
            ));
        }
        let entries_len = usize::from(info[10]) * 4;
        let size_bytes = u64::from(u32_at(&info, 4)?);
        if !(entries_len as u64..=1024 * 1024).contains(&size_bytes) {
            return Err(invalid("DBX indexed metadata has an invalid length."));
        }
        Ok(Self {
            bytes: bounded(input, size, offset + 12, size_bytes)?,
            entries_len,
        })
    }

    fn field(&self, tag: u8) -> Option<(bool, usize)> {
        let cell = self.bytes[..self.entries_len]
            .as_chunks::<4>()
            .0
            .iter()
            .find(|entry| entry[0] & 0x7f == tag)?;
        Some((
            cell[0] & 0x80 != 0,
            u32::from_le_bytes([cell[1], cell[2], cell[3], 0]) as usize,
        ))
    }

    fn number(&self, tag: u8) -> io::Result<Option<u32>> {
        match self.field(tag) {
            Some((true, value)) => Ok(Some(value as u32)),
            Some((false, value)) => u32_at(&self.bytes[self.entries_len..], value).map(Some),
            None => Ok(None),
        }
    }

    fn string(&self, tag: u8) -> io::Result<Option<String>> {
        match self.field(tag) {
            Some((false, start)) => self.bytes[self.entries_len..]
                .get(start..)
                .ok_or_else(|| invalid("DBX string points outside its metadata."))
                .and_then(legacy_string),
            Some((true, _)) => Err(invalid("Unsupported inline DBX string.")),
            None => Ok(None),
        }
    }
}

fn legacy_string(bytes: &[u8]) -> io::Result<Option<String>> {
    let end = bytes
        .iter()
        .position(|&byte| byte == 0)
        .ok_or_else(|| invalid("Legacy store string has no terminator."))?;
    let bytes = &bytes[..end];
    if bytes.is_empty() {
        return Ok(None);
    }
    let name = std::str::from_utf8(bytes)
        .map(str::to_owned)
        .unwrap_or_else(|_| {
            encoding_rs::WINDOWS_1252
                .decode_without_bom_handling(bytes)
                .0
                .into_owned()
        });
    Ok(Some(name))
}

pub fn dbx_folders(
    input: &mut (impl Read + Seek),
    size: u64,
    view: &mut Inspection,
) -> io::Result<()> {
    let (entries, issue) = dbx_index(input, size, 0xc6)?;
    for offset in entries {
        match DbxFields::read(input, size, offset).and_then(|fields| {
            Ok(ExpressFolder {
                id: fields.number(0)?,
                parent_id: fields.number(1)?.filter(|&id| id != u32::MAX),
                display_name: fields.string(2)?,
                mailbox_name: fields.string(3)?,
                data_source: fields.string(5)?,
                child_id: None,
                sibling_id: None,
                entry: ByteRange {
                    offset,
                    length: 12 + fields.bytes.len() as u64,
                },
            })
        }) {
            Ok(folder) => view.express_folders.push(folder),
            Err(error) => view.issue("dbx_folder_read", error, Some(file_range(offset, 0))),
        }
    }
    issue.map_or(Ok(()), Err)
}

struct Segment {
    offset: u64,
    logical_end: u64,
}

fn dbx_payload(
    input: &mut (impl Read + Seek),
    size: u64,
    index: u64,
) -> io::Result<(Vec<u8>, Vec<Segment>)> {
    let mut start = u64::from(
        DbxFields::read(input, size, index)?
            .number(4)?
            .filter(|&pointer| pointer != 0)
            .ok_or_else(|| invalid("DBX message has no stored content pointer."))?,
    );
    let mut data = Vec::new();
    let mut seen = HashSet::new();
    let mut ranges = Vec::new();
    while start != 0 {
        if !seen.insert(start) || seen.len() > 262_144 {
            return Err(invalid(
                "DBX message segment is repeated or the chain exceeds its limit.",
            ));
        }
        let head = bounded(input, size, start, 16)?;
        let allocated = u64::from(u32_at(&head, 4)?);
        let used = u64::from(u32_at(&head, 8)?);
        if u64::from(u32_at(&head, 0)?) != start
            || used > allocated
            || (data.len() as u64).saturating_add(used) > MESSAGE_LIMIT
        {
            return Err(invalid(
                "DBX message segment has an invalid marker or length.",
            ));
        }
        let bytes = bounded(input, size, start + 16, used)?;
        data.extend(bytes);
        if used != 0 {
            ranges.push(Segment {
                offset: start + 16,
                logical_end: data.len() as u64,
            });
        }
        start = u64::from(u32_at(&head, 12)?);
    }
    Ok((data, ranges))
}

fn dbx_location(index_offset: u64, segments: &[Segment], logical_range: ByteRange) -> Location {
    let end = logical_range.offset + logical_range.length;
    let first = segments.partition_point(|segment| segment.logical_end <= logical_range.offset);
    let mut cursor = if first == 0 {
        0
    } else {
        segments[first - 1].logical_end
    };
    let mut ranges = Vec::new();
    for segment in &segments[first..] {
        let start = cursor.max(logical_range.offset);
        let overlap_end = segment.logical_end.min(end);
        if overlap_end > start {
            ranges.push(ByteRange {
                offset: segment.offset + start - cursor,
                length: overlap_end - start,
            });
        }
        cursor = segment.logical_end;
        if cursor >= end {
            break;
        }
    }
    Location::Segments {
        index_offset,
        logical_range,
        ranges,
    }
}

fn mbx_header(input: &mut (impl Read + Seek), size: u64) -> io::Result<u32> {
    let head = bounded(input, size, 0, 84)?;
    if !head.starts_with(b"JMF6") {
        return Err(invalid("Outlook Express 4 MBX signature does not match."));
    }
    let count = u32_at(&head, 8)?;
    if count > 250_000 {
        return Err(invalid("MBX message count exceeds the parser limit."));
    }
    Ok(count)
}

fn mbx_entry(input: &mut (impl Read + Seek), size: u64, offset: u64) -> io::Result<MbxEntry> {
    let head = bounded(input, size, offset, 16)?;
    let consumed = u64::from(u32_at(&head, 8)?);
    let length = u64::from(u32_at(&head, 12)?);
    if u32_at(&head, 0)? != 0x7f007f00
        || consumed < 16
        || length > consumed - 16
        || consumed > size - offset
    {
        return Err(invalid("MBX entry marker or length is invalid."));
    }
    Ok(MbxEntry {
        entry: ByteRange {
            offset,
            length: consumed,
        },
        message_number: u32_at(&head, 4)?,
        message: ByteRange {
            offset: offset + 16,
            length,
        },
    })
}

pub fn mbx(input: &mut (impl Read + Seek), size: u64, view: &mut Inspection) -> io::Result<()> {
    let messages = mbx_header(input, size)?;
    let mut offset = 84;
    while offset < size {
        if view.mbx_entries.len() == 250_000 {
            return Err(invalid("MBX message count exceeds the parser limit."));
        }
        let entry = mbx_entry(input, size, offset)?;
        view.mbx_entries.push(entry);
        let location =
            |range: ByteRange| file_range(entry.message.offset + range.offset, range.length);
        match bounded(input, size, entry.message.offset, entry.message.length)
            .and_then(|bytes| eml(&bytes, location, view))
        {
            Ok(mut record) => {
                record.mailbox_entry_offset = Some(offset);
                view.add_record(record);
            }
            Err(error) => view.issue(
                "mbx_message_read",
                error,
                Some(file_range(entry.message.offset, entry.message.length)),
            ),
        }
        offset += entry.entry.length;
    }
    if view.mbx_entries.len() != messages as usize {
        view.issue(
            "mbx_count_mismatch",
            format!(
                "MBX header declares {messages} entries, walked {}.",
                view.mbx_entries.len()
            ),
            Some(file_range(8, 4)),
        );
    }
    Ok(())
}

pub fn idx(input: &mut (impl Read + Seek), size: u64, view: &mut Inspection) -> io::Result<()> {
    let head = bounded(input, size, 0, 80)?;
    if !head.starts_with(b"JMF9") || u32_at(&head, 4)? != 0x00010004 {
        return Err(invalid("Unsupported Outlook Express IDX header."));
    }
    let count = u32_at(&head, 8)?;
    if count > 250_000 {
        return Err(invalid("IDX record count exceeds the parser limit."));
    }
    let declared_size = u64::from(u32_at(&head, 12)?);
    if declared_size != size {
        view.issue(
            "idx_size_mismatch",
            format!("IDX header declares {declared_size} bytes, source has {size}."),
            Some(file_range(12, 4)),
        );
    }
    let mut offset = 80;
    let mut seen = HashSet::new();
    for _ in 0..count {
        let head = bounded(input, size, offset, 28)?;
        let length = u64::from(u32_at(&head, 16)?);
        let flags = u32_at(&head, 0)?;
        let mailbox_offset = u64::from(u32_at(&head, 20)?);
        let mailbox_length = u64::from(u32_at(&head, 24)?);
        if u64::from(u32_at(&head, 12)?) != offset || length < 28 || length > size - offset {
            return Err(invalid("IDX self pointer or entry length is invalid."));
        }
        if !seen.insert(mailbox_offset) {
            view.issue(
                "idx_duplicate_mailbox_offset",
                "More than one IDX record references this MBX offset.",
                Some(file_range(offset, length)),
            );
        }
        view.idx_entries.push(IdxEntry {
            entry: ByteRange { offset, length },
            message_number: u32_at(&head, 8)?,
            mailbox_entry: ByteRange {
                offset: mailbox_offset,
                length: mailbox_length,
            },
            flags,
            marked_deleted: flags & 1 != 0,
        });
        offset += length;
    }
    if offset != size {
        view.issue(
            "idx_trailing_bytes",
            "Bytes after the declared IDX entries were not interpreted.",
            Some(file_range(offset, size - offset)),
        );
    }
    Ok(())
}

pub fn nch(input: &mut (impl Read + Seek), size: u64, view: &mut Inspection) -> io::Result<()> {
    let head = bounded(input, size, 0, 24)?;
    if !head.starts_with(&[0xcf, 0xad, 0x12, 0xfe, 0x02, 0, 0x02, 0]) {
        return Err(invalid("Unsupported Outlook Express NCH header."));
    }
    let mut offset = u64::from(u32_at(&head, 20)?);
    let mut seen = HashSet::new();
    let mut previous = 0;
    while offset != 0 {
        if offset < 24 || !seen.insert(offset) || seen.len() > 25_000 {
            return Err(invalid(
                "NCH folder pointer is repeated, out of bounds or exceeds the limit.",
            ));
        }
        let row = bounded(input, size, offset, 555)?;
        let length = u64::from(u32_at(&row, 4)?);
        if u64::from(u32_at(&row, 0)?) != offset || length < 555 || length > size - offset {
            return Err(invalid("NCH folder pointer or record length is invalid."));
        }
        if u64::from(u32_at(&row, 8)?) != previous {
            view.issue(
                "nch_previous_pointer",
                "The record's previous pointer differs from the traversed record.",
                Some(file_range(offset + 8, 4)),
            );
        }
        let id = u32_at(&row, 16)?;
        view.express_folders.push(ExpressFolder {
            id: Some(id),
            parent_id: (id != 0)
                .then(|| u32_at(&row, 539))
                .transpose()?
                .filter(|&parent| parent != u32::MAX),
            display_name: legacy_string(&row[20..279])?,
            mailbox_name: legacy_string(&row[279..539])?,
            data_source: None,
            child_id: Some(u32_at(&row, 543)?).filter(|&id| id != 0 && id != u32::MAX),
            sibling_id: Some(u32_at(&row, 547)?).filter(|&id| id != 0 && id != u32::MAX),
            entry: ByteRange { offset, length },
        });
        previous = offset;
        offset = u64::from(u32_at(&row, 12)?);
    }
    if seen.is_empty() {
        return Err(invalid("NCH does not contain a supported folder chain."));
    }
    Ok(())
}

pub fn message_payload(
    input: &mut (impl Read + Seek),
    size: u64,
    format: crate::model::Format,
    offset: u64,
) -> io::Result<Vec<u8>> {
    match format {
        crate::model::Format::Dbx => {
            if !dbx_index(input, size, 0xc5)?.0.contains(&offset) {
                return Err(invalid("Offset is not an indexed DBX message."));
            }
            dbx_payload(input, size, offset).map(|(data, _)| data)
        }
        crate::model::Format::OutlookExpressMbx => {
            mbx_header(input, size)?;
            let mut position = 84;
            for _ in 0..250_000 {
                let entry = mbx_entry(input, size, position)?;
                if position == offset {
                    return bounded(input, size, entry.message.offset, entry.message.length);
                }
                position += entry.entry.length;
                if position > offset {
                    break;
                }
            }
            Err(invalid("Offset is not an indexed MBX message."))
        }
        _ => Err(invalid("Source is not an Outlook Express message store.")),
    }
}
