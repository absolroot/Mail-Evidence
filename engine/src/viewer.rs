use crate::{
    VERSION,
    collect::{self, Candidate},
    extraction::{MIME_PART, Reader},
    mime::{self, MESSAGE_LIMIT},
    model::{ByteRange, Location},
};
use serde::{Deserialize, Serialize};
use std::{
    io::{self, Read, Write},
    path::Path,
};

const SCHEMA: &str = "mailviewer-windows/1";

pub fn inspect(path: &Path, out: &mut impl Write) -> io::Result<()> {
    let candidate = Candidate {
        root: path.to_owned(),
        path: path.to_owned(),
        explicit: true,
        discovered_by: None,
        profile_kind: None,
    };
    let (entry, _) = crate::entry(&candidate, true, false)?
        .ok_or_else(|| invalid("The input was not inspected."))?;
    #[derive(Serialize)]
    struct Inspection {
        schema: &'static str,
        engine_version: &'static str,
        entry: crate::Entry,
    }
    serde_json::to_writer(
        out,
        &Inspection {
            schema: SCHEMA,
            engine_version: VERSION,
            entry,
        },
    )?;
    Ok(())
}

#[derive(Deserialize)]
struct Request {
    schema: String,
    source_length: u64,
    location: Location,
    range: Option<ByteRange>,
    expected_length: u64,
    expected_sha256: Option<String>,
    attachment: Option<Attachment>,
}

#[derive(Deserialize)]
struct Attachment {
    representation: String,
    message: Location,
    index: usize,
}

pub fn read(path: &Path, out: &mut impl Write) -> io::Result<()> {
    let mut command = Vec::new();
    io::stdin()
        .take(1024 * 1024 + 1)
        .read_to_end(&mut command)?;
    if command.len() > 1024 * 1024 {
        return Err(invalid("Evidence request exceeds the protocol limit."));
    }
    let request: Request = serde_json::from_slice(&command)?;
    if request.schema != SCHEMA || request.expected_length > MESSAGE_LIMIT {
        return Err(invalid("Unsupported evidence request."));
    }
    // The viewer keeps its verified read-only handle open while requests are served.
    let mut input = collect::open_read_only(path)?;
    let length = input.metadata()?.len();
    if length != request.source_length {
        return Err(invalid("The source length changed."));
    }
    let prefix = mime::read_range(&mut input, 0, length.min(8192))?;
    let id = collect::identify(path, &prefix, length);
    let mut reader = Reader::open(&input, id.format, length, id.ndb)?;

    let bytes = if let Some(attachment) = request.attachment {
        if request.expected_sha256.is_none() {
            return Err(invalid(
                "An attachment request requires the inspected SHA-256.",
            ));
        }
        let message = if attachment.representation == MIME_PART {
            Some(reader.message(&attachment.message)?)
        } else {
            None
        };
        let parsed = message.as_deref().map(mime::parse_message).transpose()?;
        reader.attachment_bytes(
            &attachment.representation,
            &request.location,
            attachment.index,
            parsed.as_ref(),
        )?
    } else {
        reader.message(&request.location)?
    };
    if bytes.len() as u64 != request.expected_length
        || request
            .expected_sha256
            .is_some_and(|sha| sha != collect::digest(&bytes))
    {
        return Err(invalid("The bytes differ from the inspected evidence."));
    }
    let range = request.range.unwrap_or(ByteRange {
        offset: 0,
        length: bytes.len() as u64,
    });
    let end = range
        .offset
        .checked_add(range.length)
        .filter(|end| *end <= bytes.len() as u64)
        .ok_or_else(|| invalid("Requested range is outside the evidence location."))?;
    out.write_all(&bytes[range.offset as usize..end as usize])
}

fn invalid(message: &str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, message)
}
