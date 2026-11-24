use crate::{
    collect::{digest, hash_reader},
    mime::{MESSAGE_LIMIT, body},
    model::{
        Attachment, ByteRange, Folder, Header, HeaderBlock, Inspection, Location, NdbLayout,
        Record, RtfBody,
    },
};
use cfb::CompoundFile;
use outlook_pst::{
    AnsiPstFile, UnicodePstFile,
    ltp::prop_context::PropertyValue,
    messaging::{
        attachment::{
            AnsiAttachment, Attachment as StoreAttachment, AttachmentData, UnicodeAttachment,
        },
        message::{AnsiMessage, Message, UnicodeMessage},
        store::{AnsiStore, EntryId, Store, UnicodeStore},
    },
    ndb::node_id::{NID_ROOT_FOLDER, NodeId},
};
use std::{
    collections::HashSet,
    fs::File,
    io::{self, Read, Seek, SeekFrom},
    rc::Rc,
};

const STORE_LIMIT: u64 = 2 * 1024 * 1024 * 1024;
const STORE_FIELDS: &[u16] = &[
    0x001a, 0x0037, 0x007d, 0x0c1f, 0x0e04, 0x1000, 0x1013, 0x1035, 0x3fde, 0x3ffd, 0x5d01,
];

fn invalid(message: &str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, message)
}

fn decode(data: &[u8], unicode: bool, codepage: Option<i32>) -> String {
    if unicode {
        let words = data
            .as_chunks::<2>()
            .0
            .iter()
            .copied()
            .map(u16::from_le_bytes)
            .collect::<Vec<_>>();
        String::from_utf16_lossy(&words)
            .trim_end_matches('\0')
            .to_owned()
    } else {
        let encoding = match codepage {
            Some(65001) => encoding_rs::UTF_8,
            Some(932) => encoding_rs::SHIFT_JIS,
            Some(936) => encoding_rs::GBK,
            Some(949) => encoding_rs::EUC_KR,
            Some(950) => encoding_rs::BIG5,
            Some(1250) => encoding_rs::WINDOWS_1250,
            Some(1251) => encoding_rs::WINDOWS_1251,
            Some(1253) => encoding_rs::WINDOWS_1253,
            Some(1254) => encoding_rs::WINDOWS_1254,
            Some(1255) => encoding_rs::WINDOWS_1255,
            Some(1256) => encoding_rs::WINDOWS_1256,
            Some(1257) => encoding_rs::WINDOWS_1257,
            Some(1258) => encoding_rs::WINDOWS_1258,
            _ => encoding_rs::WINDOWS_1252,
        };
        encoding.decode(data).0.trim_end_matches('\0').to_owned()
    }
}

fn text_headers(bytes: &[u8], unicode: bool, codepage: Option<i32>) -> Vec<Header> {
    let mut rows: Vec<Header> = Vec::new();
    let width = if unicode { 2 } else { 1 };
    let mut line_start = 0;
    let mut process = |start: usize, end: usize| {
        let line = decode(&bytes[start..end], unicode, codepage);
        let line = line.trim_end_matches(['\r', '\n']);
        if line.is_empty() {
            return false;
        }
        if line.starts_with([' ', '\t']) {
            if let Some(last) = rows.last_mut() {
                last.value.push('\n');
                last.value.push_str(line);
                if let Some(range) = &mut last.relative_range {
                    range.length = end as u64 - range.offset;
                }
            }
            return true;
        }
        if let Some((name, value)) = line.split_once(':') {
            rows.push(Header {
                name: name.to_owned(),
                value: value.trim().to_owned(),
                relative_range: Some(ByteRange::between(start as u64, end as u64)),
            });
        }
        true
    };
    for (index, unit) in bytes.chunks_exact(width).enumerate() {
        let offset = index * width;
        if unit.iter().all(|&byte| byte == 0) {
            process(line_start, offset);
            return rows;
        }
        if unit[0] == b'\n' && (!unicode || unit[1] == 0) {
            let end = offset + width;
            if !process(line_start, end) {
                return rows;
            }
            line_start = end;
        }
    }
    if line_start < bytes.len() {
        process(line_start, bytes.len());
    }
    rows
}

fn field(headers: &[Header], name: &str) -> Option<String> {
    headers
        .iter()
        .find(|x| x.name.eq_ignore_ascii_case(name))
        .map(|x| x.value.clone())
}

pub(crate) fn stream<R: Read + Seek>(
    cf: &mut CompoundFile<R>,
    path: &str,
    max: u64,
) -> io::Result<Option<Vec<u8>>> {
    if !cf.is_stream(path) {
        return Ok(None);
    }
    let mut source = cf.open_stream(path)?;
    if source.len() > max {
        return Err(invalid("Compound stream exceeds read limit."));
    }
    let mut bytes = Vec::with_capacity(source.len() as usize);
    source.read_to_end(&mut bytes)?;
    Ok(Some(bytes))
}

type TextStream = (String, u32, Vec<u8>, String);

fn msg_text<R: Read + Seek>(
    cf: &mut CompoundFile<R>,
    parent: &str,
    tag: u16,
    cp: Option<i32>,
) -> io::Result<Option<TextStream>> {
    for ty in [0x001f, 0x001e] {
        let path = format!("{parent}/__substg1.0_{tag:04X}{ty:04X}");
        if let Some(bytes) = stream(cf, &path, 4 * 1024 * 1024)? {
            if ty == 0x001f && bytes.len() % 2 != 0 {
                return Err(invalid("Odd length UTF-16 compound stream."));
            }
            let value = decode(&bytes, ty == 0x001f, cp);
            return Ok(Some((
                value,
                (u32::from(tag) << 16) | ty as u32,
                bytes,
                path,
            )));
        }
    }
    Ok(None)
}

fn msg_value<R: Read + Seek>(
    cf: &mut CompoundFile<R>,
    parent: &str,
    tag: u16,
    cp: Option<i32>,
) -> io::Result<Option<String>> {
    Ok(msg_text(cf, parent, tag, cp)?.map(|data| data.0))
}

pub fn msg<R: Read + Seek>(input: R, size: u64, view: &mut Inspection) -> io::Result<()> {
    if size > MESSAGE_LIMIT {
        return Err(invalid("MSG exceeds the 128 MiB input limit."));
    }
    let mut cf = CompoundFile::open(input)?;
    let location = Location::File {
        range: ByteRange::between(0, size),
    };
    let mut context = MsgContext {
        view,
        attachments_left: 4096,
        embedded_left: 128,
        retained: 0,
    };
    let record = msg_record(&mut cf, "", location, 0, &mut context)?;
    context.view.add_record(record);
    Ok(())
}

struct MsgContext<'a> {
    view: &'a mut Inspection,
    attachments_left: usize,
    embedded_left: usize,
    retained: usize,
}

fn integer_property(properties: &[u8], header: usize, tag: u32) -> Option<i32> {
    properties
        .get(header..)?
        .as_chunks::<16>()
        .0
        .iter()
        .find(|entry| entry[..4] == tag.to_le_bytes())
        .map(|entry| i32::from_le_bytes(entry[8..12].try_into().expect("fixed property width")))
}

fn msg_record<R: Read + Seek>(
    cf: &mut CompoundFile<R>,
    parent: &str,
    location: Location,
    depth: usize,
    context: &mut MsgContext<'_>,
) -> io::Result<Record> {
    let header_size = if depth == 0 { 32 } else { 24 };
    let properties = stream(
        cf,
        &format!("{parent}/__properties_version1.0"),
        4 * 1024 * 1024,
    )?
    .filter(|value| value.len() >= header_size)
    .ok_or_else(|| invalid("The compound file has no MSG message properties."))?;
    let cp = [0x3ffd0003, 0x3fde0003]
        .into_iter()
        .find_map(|tag| integer_property(&properties, header_size, tag));
    let mut record = Record::new("mail", location);
    match msg_value(cf, parent, 0x001a, cp) {
        Ok(value) => record.mapi_message_class = value,
        Err(error) => context.view.issue(
            "msg_message_class_unreadable",
            error,
            Some(record.location.clone()),
        ),
    }
    record.subject = msg_value(cf, parent, 0x0037, cp)?;
    record.from = match msg_value(cf, parent, 0x5d01, cp)? {
        Some(value) => Some(value),
        None => msg_value(cf, parent, 0x0c1f, cp)?,
    };
    record.to = msg_value(cf, parent, 0x0e04, cp)?;
    record.message_id = msg_value(cf, parent, 0x1035, cp)?;
    if let Some((_, tag, bytes, path)) = msg_text(cf, parent, 0x007d, cp)? {
        let fields = text_headers(&bytes, tag == 0x007d001f, cp);
        record.date_raw = field(&fields, "Date");
        record.headers = Some(HeaderBlock {
            representation: "stored_cfb_stream",
            location: Location::CompoundStream {
                path,
                property_tag: tag,
            },
            bytes: bytes.len() as u64,
            sha256: digest(&bytes),
            fields,
        });
    }
    if let Some(plain) = msg_value(cf, parent, 0x1000, cp)? {
        record.bodies.push(body("text/plain", &plain));
    }
    if let Some(html) = msg_value(cf, parent, 0x1013, cp)? {
        record.bodies.push(body("text/html", &html));
    } else if let Some(html) = stream(
        cf,
        &format!("{parent}/__substg1.0_10130102"),
        4 * 1024 * 1024,
    )? {
        record
            .bodies
            .push(body("text/html", &decode(&html, false, cp)));
    }
    let rtf_path = format!("{parent}/__substg1.0_10090102");
    let rtf_location = Location::CompoundStream {
        path: rtf_path.clone(),
        property_tag: 0x10090102,
    };
    match stream(cf, &rtf_path, MESSAGE_LIMIT).and_then(|data| {
        data.map(|raw| crate::rtf::decompress(&raw).map(|decoded| (raw, decoded)))
            .transpose()
    }) {
        Ok(Some((raw, decoded))) => {
            let declared_bytes = decoded.declared_bytes;
            let decoded = decoded.bytes;
            if declared_bytes != decoded.len() {
                context.view.issue(
                    "rtf_mela_size_disagreement",
                    format!(
                        "MELA declares {declared_bytes} bytes but contains {} bytes.",
                        decoded.len()
                    ),
                    Some(rtf_location.clone()),
                );
            }
            record.rtf = Some(RtfBody {
                location: rtf_location,
                stored_bytes: raw.len() as u64,
                stored_sha256: digest(&raw),
                declared_decoded_bytes: declared_bytes as u64,
                decoded_bytes: decoded.len() as u64,
                decoded_sha256: digest(&decoded),
            });
            if record.bodies.is_empty() {
                let preview = &decoded[..decoded.len().min(crate::mime::BODY_LIMIT + 1)];
                let mut rtf_body = body("application/rtf", &decode(preview, false, cp));
                rtf_body.truncated |= preview.len() < decoded.len();
                record.bodies.push(rtf_body);
            }
        }
        Ok(None) => {}
        Err(error) => context
            .view
            .issue("rtf_body_unavailable", error, Some(rtf_location)),
    }
    context.retained = context.retained.saturating_add(record.retained_bytes());
    let storage = if parent.is_empty() { "/" } else { parent };
    let paths = cf
        .read_storage(storage)?
        .filter(|entry| entry.is_storage() && entry.name().starts_with("__attach_version1.0_#"))
        .take(context.attachments_left + 1)
        .map(|entry| entry.path().to_string_lossy().replace('\\', "/"))
        .collect::<Vec<_>>();
    for path in paths {
        if context.attachments_left == 0 {
            context.view.issue(
                "msg_attachment_limit",
                "MSG attachment count exceeds the inspection limit.",
                Some(record.location.clone()),
            );
            break;
        }
        context.attachments_left -= 1;
        let fallback = Location::CompoundStorage {
            path: path.clone(),
            property_tag: 0,
        };
        match msg_attachment(cf, &path, cp, depth, &mut record, context) {
            Ok(attachment) => record.attachments.push(attachment),
            Err(error) => {
                context
                    .view
                    .issue("msg_attachment_unavailable", error, Some(fallback.clone()));
                record.attachments.push(Attachment {
                    name: None,
                    location: Some(fallback),
                    representation: "attachment_payload_unavailable",
                    bytes: None,
                    sha256: None,
                });
            }
        }
    }
    Ok(record)
}

fn msg_attachment<R: Read + Seek>(
    cf: &mut CompoundFile<R>,
    parent: &str,
    cp: Option<i32>,
    depth: usize,
    record: &mut Record,
    context: &mut MsgContext<'_>,
) -> io::Result<Attachment> {
    let name = match msg_value(cf, parent, 0x3707, cp)? {
        Some(name) => Some(name),
        None => msg_value(cf, parent, 0x3704, cp)?,
    };
    let raw = format!("{parent}/__substg1.0_37010102");
    if cf.is_stream(&raw) {
        let mut stream = cf.open_stream(&raw)?;
        let length = stream.len();
        if length > MESSAGE_LIMIT {
            return Err(invalid("Compound stream exceeds read limit."));
        }
        let (bytes, sha256) = hash_reader(&mut stream)?;
        if bytes != length {
            return Err(invalid(
                "Compound stream length differs from the directory entry.",
            ));
        }
        return Ok(Attachment {
            name,
            location: Some(Location::CompoundStream {
                path: raw,
                property_tag: 0x37010102,
            }),
            representation: "stored_cfb_stream",
            bytes: Some(bytes),
            sha256: Some(sha256),
        });
    }
    let object = format!("{parent}/__substg1.0_3701000D");
    let location = cf.is_storage(&object).then(|| Location::CompoundStorage {
        path: object.clone(),
        property_tag: 0x3701000d,
    });
    let mut representation = "attachment_payload_unavailable";
    if let Some(address) = &location {
        let properties = stream(
            cf,
            &format!("{parent}/__properties_version1.0"),
            4 * 1024 * 1024,
        )?;
        if properties
            .as_deref()
            .and_then(|data| integer_property(data, 8, 0x37050003))
            == Some(5)
        {
            if depth >= 8 || context.embedded_left == 0 || context.retained >= 32 * 1024 * 1024 {
                context.view.issue(
                    "msg_embedded_limit",
                    "Embedded MSG exceeds an inspection limit.",
                    Some(address.clone()),
                );
            } else {
                context.embedded_left -= 1;
                match msg_record(cf, &object, address.clone(), depth + 1, context) {
                    Ok(message) => {
                        record.embedded_messages.push(message);
                        representation = "embedded_message_storage";
                    }
                    Err(error) => {
                        context
                            .view
                            .issue("msg_embedded_unavailable", error, Some(address.clone()))
                    }
                }
            }
        }
    }
    Ok(Attachment {
        name,
        location,
        representation,
        bytes: None,
        sha256: None,
    })
}

pub fn msg_stream<R: Read + Seek>(input: R, path: &str, decompress: bool) -> io::Result<Vec<u8>> {
    let (root, components) = path
        .split_once('/')
        .ok_or_else(|| invalid("An absolute compound stream path is required."))?;
    if !root.is_empty()
        || components
            .split('/')
            .any(|part| part.is_empty() || matches!(part, "." | "..") || part.contains('\\'))
    {
        return Err(invalid("Invalid compound stream path."));
    }
    if decompress
        && !components
            .rsplit('/')
            .next()
            .is_some_and(|name| name.eq_ignore_ascii_case("__substg1.0_10090102"))
    {
        return Err(invalid(
            "RTF decoding requires a PidTagRtfCompressed stream.",
        ));
    }
    let mut cf = CompoundFile::open(input)?;
    let data = stream(&mut cf, path, MESSAGE_LIMIT)?
        .ok_or_else(|| invalid("The compound stream does not exist."))?;
    if decompress {
        Ok(crate::rtf::decompress(&data)?.bytes)
    } else {
        Ok(data)
    }
}

pub(crate) enum MailStore {
    Unicode(Rc<UnicodeStore>),
    Ansi(Rc<AnsiStore>),
}

impl MailStore {
    pub(crate) fn read(input: &File, size: u64, layout: Option<NdbLayout>) -> io::Result<Self> {
        if size > STORE_LIMIT {
            return Err(invalid("PST/OST exceeds the 2 GiB input limit."));
        }
        let layout = layout
            .ok_or_else(|| invalid("The expected NDB header signature was not confirmed."))?;
        let mut clone = input.try_clone()?;
        clone.seek(SeekFrom::Start(0))?;
        let reader = Box::new(clone);
        match (layout.version, layout.block_encoding) {
            (14 | 15, Some(0..=2)) => Ok(Self::Ansi(AnsiStore::read(Rc::new(
                AnsiPstFile::read_from(reader)?,
            ))?)),
            (21 | 23, Some(0..=2)) | (36, Some(0)) => Ok(Self::Unicode(UnicodeStore::read(
                Rc::new(UnicodePstFile::read_from(reader)?),
            )?)),
            _ => Err(invalid("Unsupported NDB version or block encoding.")),
        }
    }

    pub(crate) fn open_message(&self, nid: u32) -> io::Result<StoreMessage> {
        let entry = self.store().properties().make_entry_id(nid.into())?;
        self.open_entry(&entry)
    }

    fn open_entry(&self, entry: &EntryId) -> io::Result<StoreMessage> {
        match self {
            Self::Unicode(store) => Ok(StoreMessage::Unicode(UnicodeMessage::read(
                store.clone(),
                entry,
                Some(STORE_FIELDS),
            )?)),
            Self::Ansi(store) => Ok(StoreMessage::Ansi(AnsiMessage::read(
                store.clone(),
                entry,
                Some(STORE_FIELDS),
            )?)),
        }
    }

    fn store(&self) -> &dyn Store {
        match self {
            Self::Unicode(store) => store.as_ref(),
            Self::Ansi(store) => store.as_ref(),
        }
    }

    fn message(
        &self,
        entry: &EntryId,
        folder: u32,
        view: &mut Inspection,
    ) -> io::Result<Option<Record>> {
        let nid = u32::from(entry.node_id());
        let message = self.open_entry(entry)?;
        describe_message(message.message(), folder, nid, view, |id| {
            message.attachment(id)
        })
    }
}

pub(crate) enum StoreMessage {
    Unicode(Rc<UnicodeMessage>),
    Ansi(Rc<AnsiMessage>),
}

impl StoreMessage {
    fn message(&self) -> &dyn Message {
        match self {
            Self::Unicode(message) => message.as_ref(),
            Self::Ansi(message) => message.as_ref(),
        }
    }

    fn attachment(&self, nid: u32) -> io::Result<Rc<dyn StoreAttachment>> {
        match self {
            Self::Unicode(message) => {
                Ok(UnicodeAttachment::read(message.clone(), nid.into(), None)?)
            }
            Self::Ansi(message) => Ok(AnsiAttachment::read(message.clone(), nid.into(), None)?),
        }
    }

    pub(crate) fn binary_attachment(&self, nid: u32) -> io::Result<Vec<u8>> {
        let attachment = self.attachment(nid)?;
        match (
            attachment.data(),
            attachment.properties().attachment_method(),
        ) {
            (Some(AttachmentData::Binary(data)), Ok(1))
                if data.buffer().len() as u64 <= MESSAGE_LIMIT =>
            {
                Ok(data.buffer().to_vec())
            }
            _ => Err(invalid(
                "The recorded MAPI property is not an extractable by-value attachment.",
            )),
        }
    }
}

pub fn pst(
    input: &File,
    size: u64,
    layout: Option<NdbLayout>,
    view: &mut Inspection,
) -> io::Result<()> {
    let mailbox = MailStore::read(input, size, layout)?;
    let store = mailbox.store();
    let mut folders = vec![(NID_ROOT_FOLDER, None, 0)];
    let mut seen_folders = HashSet::new();
    let mut seen_messages = HashSet::new();
    while let Some((id, parent, depth)) = folders.pop() {
        let nid = u32::from(id);
        if depth > 100 || seen_folders.len() >= 10_000 {
            return Err(invalid("PST/OST folder traversal limit reached."));
        }
        if !seen_folders.insert(nid) {
            view.issue(
                "mapi_folder_repeated",
                format!("Folder NID 0x{nid:08X}"),
                None,
            );
            continue;
        }
        let entry = store
            .properties()
            .make_entry_id(id)
            .and_then(|key| store.open_folder(&key));
        let folder = match entry {
            Ok(folder) => folder,
            Err(error) if parent.is_none() => return Err(error),
            Err(error) => {
                view.issue(
                    "mapi_folder_read",
                    format!("Folder NID 0x{nid:08X}: {error}"),
                    None,
                );
                continue;
            }
        };
        view.folders.push(Folder {
            nid,
            parent_nid: parent,
            name: folder.properties().display_name().ok(),
        });
        if let Some(table) = folder.hierarchy_table() {
            let children = table
                .rows_matrix()
                .map(|row| u32::from(row.id()))
                .filter(|nid| nid & 31 == 2)
                .collect::<Vec<_>>();
            folders.extend(
                children
                    .into_iter()
                    .rev()
                    .map(|child| (NodeId::from(child), Some(nid), depth + 1)),
            );
        }
        if let Some(table) = folder.contents_table() {
            for row in table.rows_matrix() {
                let message = u32::from(row.id());
                if message & 31 != 4 || !seen_messages.insert(message) {
                    continue;
                }
                if seen_messages.len() > 250_000 {
                    return Err(invalid("PST/OST message traversal limit reached."));
                }
                let result = store
                    .properties()
                    .make_entry_id(message.into())
                    .and_then(|entry| mailbox.message(&entry, nid, view));
                match result {
                    Ok(Some(record)) => view.add_record(record),
                    Ok(None) => {}
                    Err(error) => view.issue(
                        "mapi_message_read",
                        error,
                        Some(Location::StoreItem {
                            folder_nid: nid,
                            message_nid: message,
                        }),
                    ),
                }
            }
        }
    }
    Ok(())
}

pub(crate) fn is_mail_class(class: &str) -> bool {
    let class = class.to_ascii_lowercase();
    class.starts_with("ipm.note") || class.starts_with("report.ipm.note")
}

fn describe_message(
    source: &dyn Message,
    folder: u32,
    nid: u32,
    view: &mut Inspection,
    get_attachment: impl Fn(u32) -> io::Result<Rc<dyn StoreAttachment>>,
) -> io::Result<Option<Record>> {
    if let Ok(class) = source.properties().message_class()
        && !is_mail_class(&class)
    {
        return Ok(None);
    }
    let props = source.properties();
    let cp = [0x3ffd, 0x3fde]
        .iter()
        .find_map(|&tag| match props.get(tag) {
            Some(PropertyValue::Integer32(value)) => Some(*value),
            _ => None,
        });
    let text = |tag| string_prop(props.get(tag), cp);
    let property = |tag, attachment| Location::StoreProperty {
        folder_nid: folder,
        message_nid: nid,
        attachment_nid: attachment,
        property_tag: tag,
    };
    let mut record = Record::new(
        "mail",
        Location::StoreItem {
            folder_nid: folder,
            message_nid: nid,
        },
    );
    record.subject = text(0x0037).map(|title| {
        if title.starts_with('\u{1}') {
            title.chars().skip(2).collect()
        } else {
            title
        }
    });
    record.from = text(0x5d01).or_else(|| text(0x0c1f));
    record.to = text(0x0e04);
    record.message_id = text(0x1035);
    match source.raw_transport_header() {
        Ok(Some((ty, bytes))) => {
            let tag = 0x007d0000 | u32::from(ty);
            if ty == 0x001f && bytes.len() % 2 != 0 {
                view.issue(
                    "mapi_header_text_decode",
                    "Odd UTF-16 length; raw property fingerprint retained.",
                    None,
                );
            }
            let fields = text_headers(bytes, ty == 0x001f, cp);
            record.date_raw = field(&fields, "Date");
            record.headers = Some(HeaderBlock {
                representation: "decoded_mapi_property",
                location: property(tag, None),
                bytes: bytes.len() as u64,
                sha256: digest(bytes),
                fields,
            });
        }
        Ok(None) => {}
        Err(error) => view.issue(
            "mapi_transport_header_read",
            error,
            Some(Location::StoreItem {
                folder_nid: folder,
                message_nid: nid,
            }),
        ),
    }
    if let Some(value) = text(0x1000) {
        record.bodies.push(body("text/plain", &value));
    }
    if let Some(value) = text(0x1013) {
        record.bodies.push(body("text/html", &value));
    } else if let Some(PropertyValue::Binary(data)) = props.get(0x1013) {
        record
            .bodies
            .push(body("text/html", &decode(data.buffer(), false, cp)));
    }
    if let Some(table) = source.attachment_table() {
        for row in table.rows_matrix() {
            let id = u32::from(row.id());
            let record_attachment = match get_attachment(id) {
                Ok(attachment) => {
                    let props = attachment.properties();
                    let name = string_prop(props.get(0x3707), cp)
                        .or_else(|| string_prop(props.get(0x3704), cp));
                    match (attachment.data(), props.attachment_method().ok()) {
                        (Some(AttachmentData::Binary(value)), Some(1)) => Some(Attachment {
                            name,
                            location: Some(property(0x37010102, Some(id))),
                            representation: "decoded_mapi_property",
                            bytes: Some(value.buffer().len() as u64),
                            sha256: Some(digest(value.buffer())),
                        }),
                        _ => Some(Attachment {
                            name,
                            location: None,
                            representation: "attachment_payload_unavailable",
                            bytes: None,
                            sha256: None,
                        }),
                    }
                }
                Err(error) => {
                    view.issue(
                        "mapi_attachment_read",
                        format!("Attachment NID 0x{id:08X}: {error}"),
                        Some(Location::StoreItem {
                            folder_nid: folder,
                            message_nid: nid,
                        }),
                    );
                    Some(Attachment {
                        name: None,
                        location: None,
                        representation: "attachment_payload_unavailable",
                        bytes: None,
                        sha256: None,
                    })
                }
            };
            if let Some(attachment) = record_attachment {
                record.attachments.push(attachment);
            }
        }
    }
    Ok(Some(record))
}

fn string_prop(value: Option<&PropertyValue>, cp: Option<i32>) -> Option<String> {
    match value? {
        PropertyValue::Unicode(value) => Some(value.to_string()),
        PropertyValue::String8(value) => Some(decode(value.buffer(), false, cp)),
        _ => None,
    }
}
