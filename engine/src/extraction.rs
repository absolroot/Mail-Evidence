use crate::{
    collect::{Source, digest, open_read_only},
    mime::{self, MESSAGE_LIMIT},
    model::{Attachment, ByteRange, Format, Location},
    outlook::{self, MailStore, StoreMessage},
};
use cfb::CompoundFile;
use std::{
    fs::File,
    io::{self, Read, Seek, SeekFrom},
};
use zip::ZipArchive;

pub const MIME_PART: &str = "decoded_mime_part";

pub fn available(attachment: &Attachment) -> bool {
    attachment.bytes.is_some_and(|bytes| bytes <= MESSAGE_LIMIT)
        && attachment.sha256.is_some()
        && attachment.location.is_some()
        && matches!(
            attachment.representation,
            MIME_PART
                | "stored_emlx_sidecar"
                | "stored_cfb_stream"
                | "decoded_mapi_property"
                | "stored_archive_member"
                | "stored_tnef_attribute"
        )
}

pub struct Reader {
    backend: Backend,
}

enum Backend {
    File(File),
    Compound(CompoundFile<File>),
    Archive(Box<Archive>),
    Store {
        store: MailStore,
        current: Option<(u32, StoreMessage)>,
    },
}

impl Reader {
    pub fn new(input: &File, source: &Source) -> io::Result<Self> {
        Self::open(
            input,
            source.identification.format,
            source.bytes,
            source.identification.ndb,
        )
    }

    pub(crate) fn open(
        input: &File,
        format: Format,
        size: u64,
        layout: Option<crate::model::NdbLayout>,
    ) -> io::Result<Self> {
        let mut clone = input.try_clone()?;
        clone.seek(SeekFrom::Start(0))?;
        let backend = match format {
            Format::Msg => Backend::Compound(CompoundFile::open(clone)?),
            Format::Pst | Format::Ost => Backend::Store {
                store: MailStore::read(input, size, layout)?,
                current: None,
            },
            Format::Zip | Format::Olm => Backend::Archive(Box::new(Archive {
                archive: ZipArchive::new(clone)?,
                current: None,
            })),
            _ => Backend::File(clone),
        };
        Ok(Self { backend })
    }

    pub fn message(&mut self, location: &Location) -> io::Result<Vec<u8>> {
        self.raw(location)
    }

    pub fn attachment(
        &mut self,
        attachment: &Attachment,
        index: usize,
        message: Option<&mail_parser::Message<'_>>,
    ) -> io::Result<Vec<u8>> {
        let location = attachment.location.as_ref().ok_or_else(unavailable)?;
        let data = self.attachment_bytes(attachment.representation, location, index, message)?;
        if Some(data.len() as u64) != attachment.bytes
            || attachment.sha256.as_deref() != Some(&digest(&data))
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "Extracted bytes differ from the inspected attachment.",
            ));
        }
        Ok(data)
    }

    pub(crate) fn attachment_bytes(
        &mut self,
        representation: &str,
        location: &Location,
        index: usize,
        message: Option<&mail_parser::Message<'_>>,
    ) -> io::Result<Vec<u8>> {
        if representation == MIME_PART {
            let part = message
                .and_then(|message| message.attachments().nth(index))
                .ok_or_else(unavailable)?;
            return mime::decoded_attachment(part);
        }
        self.raw(location)
    }

    fn raw(&mut self, location: &Location) -> io::Result<Vec<u8>> {
        match (&mut self.backend, location) {
            (Backend::File(file), Location::File { range }) => {
                mime::read_range(file, range.offset, range.length)
            }
            (
                Backend::File(file),
                Location::Segments {
                    logical_range,
                    ranges,
                    ..
                },
            ) => {
                if logical_range.length > MESSAGE_LIMIT {
                    return Err(unavailable());
                }
                let mut data = Vec::new();
                for range in ranges {
                    if (data.len() as u64).saturating_add(range.length) > logical_range.length {
                        return Err(unavailable());
                    }
                    data.extend(mime::read_range(file, range.offset, range.length)?);
                }
                if data.len() as u64 != logical_range.length {
                    return Err(unavailable());
                }
                Ok(data)
            }
            (Backend::Compound(compound), Location::CompoundStream { path, .. }) => {
                outlook::stream(compound, path, MESSAGE_LIMIT)?.ok_or_else(unavailable)
            }
            (Backend::Archive(archive), Location::ArchiveMember { index, name, range }) => {
                archive.range(*index, name, *range)
            }
            (
                Backend::Store { store, current },
                Location::StoreProperty {
                    message_nid,
                    attachment_nid,
                    property_tag,
                    ..
                },
            ) => {
                if current.as_ref().is_none_or(|(nid, _)| nid != message_nid) {
                    *current = Some((*message_nid, store.open_message(*message_nid)?));
                }
                let message = &current.as_ref().ok_or_else(unavailable)?.1;
                match (attachment_nid, property_tag) {
                    (Some(attachment), 0x37010102) => message.binary_attachment(*attachment),
                    (None, 0x007d001e | 0x007d001f) => {
                        message.transport_headers((*property_tag & 0xffff) as u16)
                    }
                    _ => Err(unavailable()),
                }
            }
            (
                _,
                Location::ExternalFile {
                    path,
                    range,
                    sha256,
                    ..
                },
            ) => {
                let mut file = open_read_only(path)?;
                let before = file.metadata()?;
                if range.offset != 0 || before.len() != range.length {
                    return Err(unavailable());
                }
                let data = mime::read_range(&mut file, 0, range.length)?;
                let after = file.metadata()?;
                if digest(&data) != *sha256
                    || before.len() != after.len()
                    || before.modified()? != after.modified()?
                {
                    return Err(io::Error::new(
                        io::ErrorKind::InvalidData,
                        "The external attachment has changed.",
                    ));
                }
                Ok(data)
            }
            _ => Err(unavailable()),
        }
    }
}

struct Archive {
    archive: ZipArchive<File>,
    current: Option<(usize, String, Vec<u8>)>,
}

impl Archive {
    fn range(&mut self, index: usize, name: &str, range: ByteRange) -> io::Result<Vec<u8>> {
        if self
            .current
            .as_ref()
            .is_none_or(|(i, n, _)| *i != index || n != name)
        {
            let member = self.archive.by_index(index)?;
            let length = member.size();
            if member.name() != name
                || member.is_dir()
                || member.is_symlink()
                || member.enclosed_name().is_none()
                || length > MESSAGE_LIMIT
            {
                return Err(unavailable());
            }
            let mut data = Vec::new();
            member.take(length + 1).read_to_end(&mut data)?;
            if data.len() as u64 != length {
                return Err(unavailable());
            }
            self.current = Some((index, name.to_owned(), data));
        }
        let (_, _, data) = self.current.as_ref().ok_or_else(unavailable)?;
        let end = range
            .offset
            .checked_add(range.length)
            .filter(|&end| end <= data.len() as u64)
            .ok_or_else(unavailable)?;
        Ok(data[range.offset as usize..end as usize].to_vec())
    }
}

fn unavailable() -> io::Error {
    io::Error::new(
        io::ErrorKind::InvalidData,
        "The recorded attachment bytes are unavailable.",
    )
}
