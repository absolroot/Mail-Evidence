use crate::{
    Entry, MODULE, VERSION,
    collect::{self, Failure},
    extraction::{self, Reader},
    mime,
    model::{
        Attachment, Body, Folder, HeaderBlock, Location, MailboxLabels, MhSequence, Record, RtfBody,
    },
};
use serde::{Deserialize, Serialize};
use std::{
    collections::{HashMap, HashSet},
    fs::{self, File, OpenOptions},
    io::{self, BufWriter, Write},
    path::{Path, PathBuf},
};

const SCHEMA: &str = "mailviewer-case/1";

#[derive(Default, Serialize, Deserialize)]
struct Counts {
    sources: usize,
    messages: usize,
    attachments: usize,
    extracted: usize,
    unavailable: usize,
    failed: usize,
    source_issues: usize,
}

#[derive(Serialize)]
struct MessageRow<'a> {
    number: usize,
    parent_message: Option<usize>,
    class: &'a str,
    subject: Option<&'a str>,
    from: Option<&'a str>,
    to: Option<&'a str>,
    date_raw: Option<&'a str>,
    message_id: Option<&'a str>,
    folder: Vec<&'a str>,
    location: &'a Location,
    headers: Option<&'a HeaderBlock>,
    bodies: &'a [Body],
    #[serde(skip_serializing_if = "Option::is_none")]
    labels: Option<&'a MailboxLabels>,
    #[serde(skip_serializing_if = "Option::is_none")]
    rtf: Option<&'a RtfBody>,
    attachments: Vec<AttachmentRow<'a>>,
}

#[derive(Serialize)]
struct AttachmentRow<'a> {
    #[serde(flatten)]
    evidence: &'a Attachment,
    status: &'static str,
    #[serde(skip_serializing_if = "Option::is_none")]
    file: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    detail: Option<String>,
}

struct Writers<'a> {
    json: Option<&'a mut dyn Write>,
    csv: Option<&'a mut dyn Write>,
    attachments: Option<&'a Path>,
}

pub fn report(roots: Vec<PathBuf>, csv: bool, out: &mut dyn Write) -> io::Result<()> {
    let plan = plan(roots);
    let counts = if csv {
        scan(
            plan,
            Writers {
                json: None,
                csv: Some(out),
                attachments: None,
            },
        )?
    } else {
        scan(
            plan,
            Writers {
                json: Some(out),
                csv: None,
                attachments: None,
            },
        )?
    };
    if csv && counts.source_issues != 0 {
        eprintln!(
            "{} source issues. Use JSON report for details.",
            counts.source_issues
        );
    }
    Ok(())
}

pub fn extract(roots: Vec<PathBuf>, destination: &Path, out: &mut dyn Write) -> io::Result<()> {
    let plan = plan(roots);
    fs::create_dir_all(destination)?;
    let package = tempfile::Builder::new()
        .prefix("mailviewer-")
        .tempdir_in(destination.canonicalize()?)?;
    let payloads = package.path().join("attachments");
    fs::create_dir(&payloads)?;
    let mut json = BufWriter::new(File::create(package.path().join("manifest.json"))?);
    let mut csv = BufWriter::new(File::create(package.path().join("messages.csv"))?);
    let counts = scan(
        plan,
        Writers {
            json: Some(&mut json),
            csv: Some(&mut csv),
            attachments: Some(&payloads),
        },
    )?;
    for output in [&mut json, &mut csv] {
        output.flush()?;
        output.get_ref().sync_all()?;
    }
    drop((json, csv));
    let directory = package.keep();
    writeln!(
        out,
        "{}",
        serde_json::json!({
            "schema": SCHEMA, "module": MODULE, "engine_version": VERSION,
            "directory": directory, "counts": counts,
        })
    )
}

fn plan(roots: Vec<PathBuf>) -> collect::Discovery {
    let mut plan = collect::discover(roots);
    crate::profile::add_companions(&mut plan);
    plan
}

fn scan(plan: collect::Discovery, mut writers: Writers<'_>) -> io::Result<Counts> {
    let mut failures = plan.failures;
    let mut skipped = plan.skipped_unrecognized;
    let mut counts = Counts::default();
    let mut separator = "";
    let mut related = crate::relations::Discovery::default();
    if let Some(out) = writers.json.as_deref_mut() {
        write!(
            out,
            "{{\"schema\":\"{SCHEMA}\",\"module\":\"{MODULE}\",\"engine_version\":\"{VERSION}\",\"sources\":["
        )?;
    }
    if let Some(out) = writers.csv.as_deref_mut() {
        writeln!(
            out,
            "source_id,source_path,source_sha256,format,message,parent_message,class,folder,date_raw,from,to,subject,message_id,attachment_count,extractable_count\r"
        )?;
    }
    for candidate in plan.files {
        let (mut entry, mut input) = match crate::entry(&candidate, true, true) {
            Ok(Some(result)) => result,
            Ok(None) => {
                skipped += 1;
                continue;
            }
            Err(error) => {
                failures.push(Failure {
                    path: candidate.path,
                    code: "source_unreadable",
                    detail: error.to_string(),
                });
                continue;
            }
        };
        counts.sources += 1;
        let mut messages = Vec::new();
        if let Some(view) = &entry.inspection {
            counts.source_issues += view.issues.len() + view.omitted_issue_count;
            let folders: HashMap<_, _> = view
                .folders
                .iter()
                .map(|folder| (folder.nid, folder))
                .collect();
            collect_rows(&entry, &view.records, None, &folders, &mut messages);
            let mut reader = writers
                .attachments
                .filter(|_| {
                    messages.iter().any(|row| {
                        row.attachments
                            .iter()
                            .any(|attachment| extraction::available(attachment.evidence))
                    })
                })
                .map(|_| Reader::new(&input, &entry.source));
            for row in &mut messages {
                if row.class == "mail" {
                    counts.messages += 1;
                }
                if let Some(directory) = writers.attachments {
                    extract_row(directory, row, reader.as_mut(), &mut counts)?;
                } else {
                    counts.attachments += row.attachments.len();
                    counts.unavailable += row
                        .attachments
                        .iter()
                        .filter(|attachment| attachment.status == "unavailable")
                        .count();
                }
                if let Some(out) = writers.csv.as_deref_mut() {
                    csv_row(out, &entry, row)?;
                }
            }
            if reader.is_some() {
                drop(reader);
                if collect::hash_file(&mut input)? != entry.source.sha256 {
                    return Err(invalid(
                        "The source changed while attachments were extracted.",
                    ));
                }
            }
        }
        if entry.status != "parsed"
            && entry
                .inspection
                .as_ref()
                .is_none_or(|view| view.issues.is_empty())
        {
            counts.source_issues += 1;
        }
        if let Some(out) = writers.json.as_deref_mut() {
            write!(out, "{separator}")?;
            let view = entry.inspection.as_ref();
            #[derive(Serialize)]
            struct SourceReport<'a, 'b> {
                source: &'a collect::Source,
                status: &'a str,
                messages: &'b [MessageRow<'a>],
                issues: &'a [crate::model::Issue],
                omitted_issue_count: usize,
                #[serde(skip_serializing_if = "<[MhSequence]>::is_empty")]
                mh_sequences: &'a [MhSequence],
            }
            serde_json::to_writer(
                out,
                &SourceReport {
                    source: &entry.source,
                    status: entry.status,
                    messages: &messages,
                    issues: view.map_or(&[], |view| &view.issues),
                    omitted_issue_count: view.map_or(0, |view| view.omitted_issue_count),
                    mh_sequences: view.map_or(&[], |view| &view.mh_sequences),
                },
            )?;
            separator = ",";
        }
        drop(messages);
        if !related.add(&entry.source, entry.status, entry.inspection.as_mut()) {
            failures.push(Failure {
                path: entry.source.discovered_path,
                code: "relationship_limit",
                detail: "Collection relationship budget exceeded.".to_owned(),
            });
        }
    }
    counts.source_issues += failures.len();
    if let Some(out) = writers.json {
        write!(
            out,
            "],\"skipped_unrecognized_or_non_mail\":{skipped},\"failures\":"
        )?;
        serde_json::to_writer(&mut *out, &failures)?;
        write!(out, ",\"outlook_express_stores\":")?;
        serde_json::to_writer(&mut *out, &related.report())?;
        write!(out, ",\"counts\":")?;
        serde_json::to_writer(&mut *out, &counts)?;
        writeln!(out, "}}")?;
    }
    Ok(counts)
}

fn collect_rows<'a>(
    entry: &'a Entry,
    records: &'a [Record],
    parent: Option<usize>,
    folders: &HashMap<u32, &'a Folder>,
    rows: &mut Vec<MessageRow<'a>>,
) {
    for record in records.iter().filter(|row| {
        matches!(row.class, "mail" | "tnef")
            && row
                .mapi_message_class
                .as_deref()
                .is_none_or(crate::outlook::is_mail_class)
    }) {
        let number = rows.len() + 1;
        rows.push(MessageRow {
            number,
            parent_message: parent,
            class: record.class,
            subject: record.subject.as_deref(),
            from: record.from.as_deref(),
            to: record.to.as_deref(),
            date_raw: record.date_raw.as_deref(),
            message_id: record.message_id.as_deref(),
            folder: folder_names(entry, record, folders),
            location: &record.location,
            headers: record.headers.as_ref(),
            bodies: &record.bodies,
            labels: record.mailbox_labels.as_ref(),
            rtf: record.rtf.as_ref(),
            attachments: record
                .attachments
                .iter()
                .map(|evidence| AttachmentRow {
                    evidence,
                    status: if extraction::available(evidence) {
                        "available"
                    } else {
                        "unavailable"
                    },
                    file: None,
                    detail: None,
                })
                .collect(),
        });
        collect_rows(
            entry,
            &record.embedded_messages,
            Some(number),
            folders,
            rows,
        );
    }
}

fn folder_names<'a>(
    entry: &'a Entry,
    record: &'a Record,
    folders: &HashMap<u32, &'a Folder>,
) -> Vec<&'a str> {
    if let Location::StoreItem { folder_nid, .. } = record.location {
        let mut names = Vec::new();
        let mut visited = HashSet::new();
        let mut next = Some(folder_nid);
        while let Some(folder) = next.and_then(|nid| folders.get(&nid)) {
            if !visited.insert(folder.nid) {
                break;
            }
            if let Some(name) = &folder.name {
                names.push(name.as_str());
            }
            next = folder.parent_nid;
        }
        names.reverse();
        return names;
    }
    if let Location::ArchiveMember { name, .. } = &record.location {
        return name
            .rsplit_once('/')
            .map_or_else(Vec::new, |(parent, _)| parent.split('/').collect());
    }
    let source = &entry.source;
    if let Some(folder) = &source.logical_folder {
        return folder.iter().map(String::as_str).collect();
    }
    let Some(parent) = source.discovered_path.parent() else {
        return Vec::new();
    };
    let parent = match source.collection_kind {
        "maildir" | "apple_mail_layout"
            if parent
                .file_name()
                .is_some_and(|name| matches!(name.to_str(), Some("cur" | "new" | "Messages"))) =>
        {
            parent.parent().unwrap_or(parent)
        }
        _ => parent,
    };
    let relative = parent
        .strip_prefix(&source.selected_root)
        .unwrap_or(Path::new(""));
    let names: Vec<_> = relative.iter().filter_map(|name| name.to_str()).collect();
    if names.is_empty()
        && matches!(
            source.collection_kind,
            "maildir" | "apple_mail_layout" | "mh_mailbox"
        )
    {
        return parent
            .file_name()
            .and_then(|name| name.to_str())
            .into_iter()
            .collect();
    }
    names
}

fn extract_row(
    directory: &Path,
    row: &mut MessageRow<'_>,
    reader: Option<&mut io::Result<Reader>>,
    counts: &mut Counts,
) -> io::Result<()> {
    let needs_mime = row.attachments.iter().any(|item| {
        item.status == "available" && item.evidence.representation == extraction::MIME_PART
    });
    let mut reader = reader;
    let message = if needs_mime {
        reader
            .as_deref_mut()
            .and_then(|reader| reader.as_mut().ok())
            .map(|reader| {
                reader
                    .message(row.location)
                    .map_err(|error| error.to_string())
            })
    } else {
        None
    };
    let parsed = message.as_ref().map(|data| {
        data.as_ref().map_err(String::as_str).and_then(|bytes| {
            mime::parse_message(bytes).map_err(|_| "MIME message could not be reopened.")
        })
    });
    for (index, item) in row.attachments.iter_mut().enumerate() {
        counts.attachments += 1;
        if item.status == "unavailable" {
            counts.unavailable += 1;
            continue;
        }
        let bytes = match reader.as_deref_mut() {
            Some(Ok(reader)) => {
                if item.evidence.representation == extraction::MIME_PART
                    && let Some(Err(error)) = parsed
                {
                    Err(error.to_owned())
                } else {
                    let parsed = parsed.as_ref().and_then(|result| result.as_ref().ok());
                    reader
                        .attachment(item.evidence, index, parsed)
                        .map_err(|error| error.to_string())
                }
            }
            Some(Err(error)) => Err(error.to_string()),
            None => Err("Source reader unavailable.".to_owned()),
        };
        match bytes {
            Ok(bytes) => {
                let basename = safe_name(counts.attachments, item.evidence.name.as_deref());
                let mut output = OpenOptions::new()
                    .write(true)
                    .create_new(true)
                    .open(directory.join(&basename))?;
                output.write_all(&bytes)?;
                output.sync_all()?;
                counts.extracted += 1;
                item.status = "extracted";
                item.file = Some(basename);
            }
            Err(detail) => {
                counts.failed += 1;
                item.status = "failed";
                item.detail = Some(detail);
            }
        }
    }
    Ok(())
}

fn safe_name(number: usize, original: Option<&str>) -> String {
    let name = original
        .unwrap_or_default()
        .chars()
        .take(64)
        .map(|ch| {
            if ch.is_control() || matches!(ch, '/' | '\\' | ':' | '*' | '?' | '"' | '<' | '>' | '|')
            {
                '_'
            } else {
                ch
            }
        })
        .collect::<String>();
    let name = name.trim_matches([' ', '.']);
    let name = if name.is_empty() {
        "attachment.bin"
    } else {
        name
    };
    format!("{number:08}-{name}")
}

fn csv_row(out: &mut dyn Write, entry: &Entry, row: &MessageRow<'_>) -> io::Result<()> {
    let source = &entry.source;
    let format = serde_json::to_value(source.identification.format)?;
    let fields = [
        source.id.as_str(),
        &source.opened_path.to_string_lossy(),
        &source.sha256,
        format.as_str().unwrap_or_default(),
        &row.number.to_string(),
        &row.parent_message
            .map_or_else(String::new, |value| value.to_string()),
        row.class,
        &row.folder.join(" / "),
        row.date_raw.unwrap_or_default(),
        row.from.unwrap_or_default(),
        row.to.unwrap_or_default(),
        row.subject.unwrap_or_default(),
        row.message_id.unwrap_or_default(),
        &row.attachments.len().to_string(),
        &row.attachments
            .iter()
            .filter(|item| item.status != "unavailable")
            .count()
            .to_string(),
    ];
    for (index, field) in fields.into_iter().enumerate() {
        if index != 0 {
            out.write_all(b",")?;
        }
        let dangerous = field.starts_with(['\t', '\r', '\n'])
            || field
                .trim_start_matches(char::is_whitespace)
                .starts_with(['=', '+', '-', '@', '＝', '＋', '－', '＠']);
        let prefix = if dangerous { "[text] " } else { "" };
        write!(out, "\"{prefix}{}\"", field.replace('"', "\"\""))?;
    }
    out.write_all(b"\r\n")
}

#[derive(Deserialize)]
struct Manifest {
    schema: String,
    counts: Counts,
    sources: Vec<ManifestSource>,
}

#[derive(Deserialize)]
struct ManifestSource {
    messages: Vec<ManifestMessage>,
}

#[derive(Deserialize)]
struct ManifestMessage {
    attachments: Vec<ManifestAttachment>,
}

#[derive(Deserialize)]
struct ManifestAttachment {
    status: String,
    file: Option<String>,
    bytes: Option<u64>,
    sha256: Option<String>,
}

pub fn verify(directory: &Path, out: &mut dyn Write) -> io::Result<bool> {
    let manifest = collect::open_read_only(&directory.join("manifest.json"))?;
    let manifest: Manifest = serde_json::from_reader(io::BufReader::new(manifest))?;
    if manifest.schema != SCHEMA {
        return Err(invalid("Unsupported extraction manifest schema."));
    }
    let attachments = directory.join("attachments");
    let metadata = fs::symlink_metadata(&attachments)?;
    if !metadata.is_dir() || collect::is_redirect(&metadata) {
        return Err(invalid(
            "The attachments directory is missing or redirected.",
        ));
    }
    let mut names = HashSet::new();
    let mut failures = Vec::new();
    let mut checked = 0;
    for item in manifest
        .sources
        .into_iter()
        .flat_map(|source| source.messages)
        .flat_map(|message| message.attachments)
    {
        if item.status != "extracted" {
            continue;
        }
        checked += 1;
        let name = item.file.as_deref().unwrap_or_default();
        if !valid_basename(name) || !names.insert(name.to_lowercase()) {
            return Err(invalid(
                "Invalid or repeated attachment filename in the manifest.",
            ));
        }
        let path = attachments.join(name);
        let matches = collect::open_read_only(&path)
            .and_then(|mut file| collect::hash_reader(&mut file))
            .is_ok_and(|(bytes, sha)| {
                item.bytes == Some(bytes) && item.sha256.as_deref() == Some(&sha)
            });
        if !matches {
            failures.push(name.to_owned());
        }
    }
    if checked != manifest.counts.extracted {
        return Err(invalid("Extraction count disagrees with the manifest."));
    }
    writeln!(
        out,
        "{}",
        serde_json::json!({
            "schema": SCHEMA, "checked": checked, "verified": checked - failures.len(),
            "failed": failures, "unavailable_at_extraction": manifest.counts.unavailable,
            "failed_at_extraction": manifest.counts.failed,
        })
    )?;
    Ok(failures.is_empty())
}

fn valid_basename(name: &str) -> bool {
    name.split_once('-').is_some_and(|(index, rest)| {
        index.len() >= 8
            && index.bytes().all(|byte| byte.is_ascii_digit())
            && !rest.is_empty()
            && rest.trim_matches([' ', '.']) == rest
            && !rest.chars().any(|ch| {
                ch.is_control()
                    || matches!(ch, '/' | '\\' | ':' | '*' | '?' | '"' | '<' | '>' | '|')
            })
    })
}

fn invalid(message: &'static str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, message)
}
