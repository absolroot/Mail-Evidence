use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Format {
    Eml,
    Emlx,
    Mbox,
    Mmdf,
    Babyl,
    MhSequences,
    Msg,
    Pst,
    Ost,
    Zip,
    Dbx,
    DbxFolders,
    DbxMetadata,
    OutlookExpressMbx,
    OutlookExpressIdx,
    OutlookExpressNch,
    Olm,
    Tnef,
    P7m,
    Ics,
    Vcf,
    Edb,
    Nsf,
    ThunderbirdIndex,
    DiskImage,
    Unknown,
}

impl Format {
    pub fn can_inspect(self) -> bool {
        matches!(
            self,
            Self::Eml
                | Self::Emlx
                | Self::Mbox
                | Self::Mmdf
                | Self::Babyl
                | Self::MhSequences
                | Self::Msg
                | Self::Pst
                | Self::Ost
                | Self::Zip
                | Self::Dbx
                | Self::DbxFolders
                | Self::OutlookExpressMbx
                | Self::OutlookExpressIdx
                | Self::OutlookExpressNch
                | Self::Olm
                | Self::Tnef
                | Self::P7m
                | Self::Ics
                | Self::Vcf
        )
    }
}

#[derive(Clone, Debug, Serialize)]
pub struct Identification {
    pub format: Format,
    pub basis: &'static str,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ndb: Option<NdbLayout>,
}

#[derive(Clone, Copy, Debug, Serialize)]
pub struct NdbLayout {
    pub version: u16,
    pub page_size: Option<u16>,
    pub block_encoding: Option<u8>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct ByteRange {
    pub offset: u64,
    pub length: u64,
}

impl ByteRange {
    pub fn between(start: u64, end: u64) -> Self {
        Self {
            offset: start,
            length: end - start,
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum Location {
    File {
        range: ByteRange,
    },
    CompoundStream {
        path: String,
        property_tag: u32,
    },
    CompoundStorage {
        path: String,
        property_tag: u32,
    },
    ExternalFile {
        path: std::path::PathBuf,
        sha256: String,
        range: ByteRange,
        mime_part: String,
        apple_content_length: u64,
    },
    StoreItem {
        folder_nid: u32,
        message_nid: u32,
    },
    StoreProperty {
        folder_nid: u32,
        message_nid: u32,
        attachment_nid: Option<u32>,
        property_tag: u32,
    },
    ArchiveMember {
        index: usize,
        name: String,
        range: ByteRange,
    },
    Segments {
        index_offset: u64,
        logical_range: ByteRange,
        ranges: Vec<ByteRange>,
    },
}

#[derive(Debug, Serialize)]
pub struct Header {
    pub name: String,
    pub value: String,
    pub relative_range: Option<ByteRange>,
}

#[derive(Debug, Serialize)]
pub struct HeaderBlock {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub code_page: Option<i32>,
    pub representation: &'static str,
    pub location: Location,
    pub bytes: u64,
    pub sha256: String,
    pub fields: Vec<Header>,
}

#[derive(Debug, Serialize)]
pub struct Attachment {
    pub name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub media_type: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub content_id: Option<String>,
    pub location: Option<Location>,
    pub representation: &'static str,
    pub bytes: Option<u64>,
    pub sha256: Option<String>,
}

#[derive(Debug, Serialize)]
pub struct Body {
    pub media_type: String,
    pub text: String,
    pub truncated: bool,
}

#[derive(Debug, Serialize)]
pub struct RtfBody {
    pub location: Location,
    pub stored_bytes: u64,
    pub stored_sha256: String,
    pub declared_decoded_bytes: u64,
    pub decoded_bytes: u64,
    pub decoded_sha256: String,
}

#[derive(Debug, Serialize)]
pub struct MailboxLabels {
    pub values: Vec<String>,
    pub range: ByteRange,
}

#[derive(Debug, Serialize)]
pub struct MhSequence {
    pub name: String,
    pub members: Vec<[u32; 2]>,
    pub range: ByteRange,
}

#[derive(Debug, Serialize)]
pub struct Record {
    pub class: &'static str,
    pub location: Location,
    pub subject: Option<String>,
    pub from: Option<String>,
    pub to: Option<String>,
    pub message_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub mapi_message_class: Option<String>,
    pub date_raw: Option<String>,
    pub envelope: Option<Location>,
    pub separator: Option<Location>,
    pub headers: Option<HeaderBlock>,
    pub bodies: Vec<Body>,
    pub attachments: Vec<Attachment>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub mailbox_entry_offset: Option<u64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub mailbox_labels: Option<MailboxLabels>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub rtf: Option<RtfBody>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub embedded_messages: Vec<Record>,
}

impl Record {
    pub fn new(class: &'static str, location: Location) -> Self {
        Self {
            class,
            location,
            subject: None,
            from: None,
            to: None,
            message_id: None,
            mapi_message_class: None,
            date_raw: None,
            envelope: None,
            separator: None,
            headers: None,
            bodies: Vec::new(),
            attachments: Vec::new(),
            mailbox_entry_offset: None,
            mailbox_labels: None,
            rtf: None,
            embedded_messages: Vec::new(),
        }
    }

    pub fn retained_bytes(&self) -> usize {
        512 + self
            .bodies
            .iter()
            .map(|body| body.text.len() + 128)
            .sum::<usize>()
            + self.attachments.len() * 512
            + self.headers.as_ref().map_or(0, |header| {
                header
                    .fields
                    .iter()
                    .map(|field| field.name.len() + field.value.len() + 96)
                    .sum()
            })
            + self
                .embedded_messages
                .iter()
                .map(Self::retained_bytes)
                .sum::<usize>()
    }
}

#[derive(Debug, Serialize)]
pub struct Folder {
    pub nid: u32,
    pub parent_nid: Option<u32>,
    pub name: Option<String>,
}

#[derive(Debug, Serialize)]
pub struct ExpressFolder {
    pub id: Option<u32>,
    pub parent_id: Option<u32>,
    pub display_name: Option<String>,
    pub mailbox_name: Option<String>,
    pub data_source: Option<String>,
    pub child_id: Option<u32>,
    pub sibling_id: Option<u32>,
    pub entry: ByteRange,
}

#[derive(Clone, Copy, Debug, Serialize)]
pub struct IdxEntry {
    pub entry: ByteRange,
    pub message_number: u32,
    pub mailbox_entry: ByteRange,
    pub flags: u32,
    pub marked_deleted: bool,
}

#[derive(Clone, Copy, Debug, Serialize)]
pub struct MbxEntry {
    pub entry: ByteRange,
    pub message_number: u32,
    pub message: ByteRange,
}

#[derive(Debug, Serialize)]
pub struct Issue {
    pub code: &'static str,
    pub detail: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub location: Option<Location>,
}

#[derive(Debug, Default, Serialize)]
pub struct Inspection {
    pub folders: Vec<Folder>,
    pub records: Vec<Record>,
    pub issues: Vec<Issue>,
    pub omitted_issue_count: usize,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub express_folders: Vec<ExpressFolder>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub idx_entries: Vec<IdxEntry>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub mbx_entries: Vec<MbxEntry>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub mh_sequences: Vec<MhSequence>,
    #[serde(skip)]
    record_bytes: usize,
}

impl Inspection {
    pub fn issue(&mut self, code: &'static str, detail: impl ToString, location: Option<Location>) {
        if self.issues.len() >= 10_000 {
            self.omitted_issue_count += 1;
            return;
        }
        self.issues.push(Issue {
            code,
            detail: detail.to_string(),
            location,
        });
    }

    pub fn add_record(&mut self, record: Record) {
        let cost = record.retained_bytes();
        if self.records.len() >= 25_000 || self.record_bytes.saturating_add(cost) > 48 * 1024 * 1024
        {
            if !self
                .issues
                .iter()
                .any(|issue| issue.code == "inspection_output_limit")
            {
                self.issue(
                    "inspection_output_limit",
                    "The per-source output budget was reached; additional records were omitted.",
                    None,
                );
            }
            return;
        }
        self.record_bytes += cost;
        self.records.push(record);
    }
}
