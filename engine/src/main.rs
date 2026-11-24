mod apple;
mod case_cli;
mod collect;
mod container;
mod express;
mod extraction;
mod mailbox;
mod mime;
mod model;
mod outlook;
mod profile;
mod relations;
mod rtf;
mod viewer;

use collect::{Candidate, Failure, Source, discover};
use model::{ByteRange, Format, Inspection, Location};
use serde::Serialize;
use std::{
    env,
    fs::File,
    io::{self, BufWriter, Write},
    panic::{AssertUnwindSafe, catch_unwind},
    path::{Path, PathBuf},
    process::ExitCode,
};

const MODULE: &str = "mailviewer-engine";
const VERSION: &str = env!("CARGO_PKG_VERSION");
const SCHEMA: &str = "mailviewer-engine/1";

#[derive(Serialize)]
struct Entry {
    source: Source,
    status: &'static str,
    #[serde(skip_serializing_if = "Option::is_none")]
    inspection: Option<Inspection>,
}

fn inspect(source: &Source, mut input: &mut File) -> Inspection {
    let mut view = Inspection::default();
    let size = source.bytes;
    let file_location = |range| Location::File { range };
    let result = match source.identification.format {
        Format::Eml => mime::read_range(&mut input, 0, size).and_then(|bytes| {
            mime::eml(&bytes, file_location, &mut view).map(|record| view.add_record(record))
        }),
        Format::Emlx => mime::read_emlx(&mut input, size).and_then(|(offset, bytes)| {
            mime::emlx(
                &bytes,
                |range| {
                    file_location(ByteRange {
                        offset: offset + range.offset,
                        length: range.length,
                    })
                },
                &mut view,
                &source.opened_path,
            )
            .map(|record| view.add_record(record))
        }),
        Format::Mbox => mime::mbox(&mut input, size, file_location, &mut view),
        Format::Mmdf | Format::Babyl => {
            mailbox::inspect(&mut input, size, source.identification.format, &mut view)
        }
        Format::MhSequences => mailbox::mh_sequences(&mut input, size, &mut view),
        Format::Msg => outlook::msg(&mut input, size, &mut view),
        Format::Pst | Format::Ost => {
            outlook::pst(input, size, source.identification.ndb, &mut view)
        }
        Format::Zip => container::zip(&mut input, &mut view),
        Format::Olm => container::olm(&mut input, &mut view),
        Format::Dbx => express::dbx(&mut input, size, &mut view),
        Format::DbxFolders => express::dbx_folders(&mut input, size, &mut view),
        Format::OutlookExpressMbx => express::mbx(&mut input, size, &mut view),
        Format::OutlookExpressIdx => express::idx(&mut input, size, &mut view),
        Format::OutlookExpressNch => express::nch(&mut input, size, &mut view),
        Format::Tnef => container::tnef(&mut input, size, &mut view),
        Format::P7m => container::cms(&mut input, size, &mut view),
        kind @ (Format::Ics | Format::Vcf) => {
            mime::read_range(&mut input, 0, size).and_then(|bytes| {
                container::text_object(
                    &bytes,
                    kind,
                    file_location(ByteRange::between(0, size)),
                    &mut view,
                )
            })
        }
        _ => {
            view.issue(
                "reader_unavailable",
                "The format is recorded in the collection plan.",
                None,
            );
            Ok(())
        }
    };
    if let Err(error) = result {
        view.issue("source_parse", error, None);
    }
    match collect::hash_file(input) {
        Ok(value) if value == source.sha256 => {}
        _ => {
            view = Inspection::default();
            view.issue(
                "source_changed",
                "The source bytes could not be reverified after parsing.",
                None,
            );
        }
    }
    view
}

fn entry(candidate: &Candidate, parse: bool, mail_only: bool) -> io::Result<Option<(Entry, File)>> {
    let Some((source, mut input)) = collect::source(candidate)? else {
        return Ok(None);
    };
    if mail_only
        && matches!(
            source.identification.format,
            Format::Ics | Format::Vcf | Format::DiskImage
        )
    {
        return Ok(None);
    }
    let supported = source.identification.format.can_inspect();
    let inspection = (parse && supported).then(|| {
        catch_unwind(AssertUnwindSafe(|| inspect(&source, &mut input))).unwrap_or_else(|_| {
            let mut report = Inspection::default();
            report.issue(
                "reader_panicked",
                "The parser stopped unexpectedly for this source.",
                None,
            );
            report
        })
    });
    let status = match &inspection {
        Some(result) if result.issues.is_empty() => "parsed",
        Some(result)
            if !result.records.is_empty()
                || !result.folders.is_empty()
                || !result.express_folders.is_empty()
                || !result.idx_entries.is_empty()
                || !result.mbx_entries.is_empty()
                || !result.mh_sequences.is_empty() =>
        {
            "partial"
        }
        Some(_) => "unread",
        None if !supported => "identified_only",
        None => "identified",
    };
    Ok(Some((
        Entry {
            source,
            status,
            inspection,
        },
        input,
    )))
}

fn run_collection(
    roots: Vec<PathBuf>,
    profile: Option<profile::Kind>,
    parse: bool,
    out: &mut impl Write,
) -> io::Result<()> {
    if roots.is_empty() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "Specify at least one input file or directory.",
        ));
    }
    let (mut plan, profile_roots) = match profile {
        Some(kind) => profile::discover_profiles(kind, roots),
        None => (discover(roots), Vec::new()),
    };
    profile::add_companions(&mut plan);
    let mut failures = plan.failures;
    let mut skipped = plan.skipped_unrecognized;
    let mut sep = "";
    let mut related = relations::Discovery::default();
    write!(
        out,
        "{{\"schema\":\"{SCHEMA}\",\"module\":\"{MODULE}\",\"engine_version\":\"{VERSION}\",\"sources\":["
    )?;
    for candidate in plan.files {
        match entry(&candidate, parse, false) {
            Ok(Some((mut entry, _))) => {
                out.write_all(sep.as_bytes())?;
                serde_json::to_writer(&mut *out, &entry)?;
                if !related.add(&entry.source, entry.status, entry.inspection.as_mut()) {
                    failures.push(Failure {
                        path: entry.source.discovered_path.clone(),
                        code: "relationship_limit",
                        detail:
                            "Collection relationship budget exceeded; source inspection is present."
                                .to_owned(),
                    });
                }
                sep = ",";
            }
            Ok(None) => skipped += 1,
            Err(error) => failures.push(Failure {
                path: candidate.path,
                code: "source_unreadable",
                detail: error.to_string(),
            }),
        }
    }
    write!(out, "],\"skipped_unrecognized\":{skipped},\"failures\":")?;
    serde_json::to_writer(&mut *out, &failures)?;
    write!(out, ",\"profile_roots\":")?;
    serde_json::to_writer(&mut *out, &profile_roots)?;
    write!(out, ",\"outlook_express_stores\":")?;
    serde_json::to_writer(&mut *out, &related.report())?;
    writeln!(out, "}}")
}

fn count(value: &std::ffi::OsStr) -> io::Result<u64> {
    value.to_str().and_then(|n| n.parse().ok()).ok_or_else(|| {
        io::Error::new(
            io::ErrorKind::InvalidInput,
            "Offset and length must be unsigned decimal integers.",
        )
    })
}

fn sha256(value: &std::ffi::OsStr) -> io::Result<&str> {
    value
        .to_str()
        .filter(|hash| hash.len() == 64 && hash.bytes().all(|b| b.is_ascii_hexdigit()))
        .ok_or_else(|| {
            io::Error::new(
                io::ErrorKind::InvalidInput,
                "Expected a SHA-256 hex digest.",
            )
        })
}

fn run() -> io::Result<()> {
    let mut args = env::args_os().skip(1);
    let op = args.next();
    let mut out = BufWriter::new(io::stdout().lock());
    let rest = args.collect::<Vec<_>>();
    match op.as_ref().and_then(|op| op.to_str()) {
        Some("viewer-inspect") if rest.len() == 1 => {
            viewer::inspect(Path::new(&rest[0]), &mut out)?;
        }
        Some("viewer-bytes") if rest.len() == 1 => {
            viewer::read(Path::new(&rest[0]), &mut out)?;
        }
        Some("report") if !rest.is_empty() => {
            let csv = rest[0] == "--csv";
            if rest.len() == usize::from(csv) {
                return Err(io::Error::new(io::ErrorKind::InvalidInput, USAGE));
            }
            case_cli::report(
                rest.into_iter()
                    .skip(usize::from(csv))
                    .map(PathBuf::from)
                    .collect(),
                csv,
                &mut out,
            )?;
        }
        Some("extract") if rest.len() >= 2 => {
            let destination = PathBuf::from(&rest[0]);
            case_cli::extract(
                rest.into_iter().skip(1).map(PathBuf::from).collect(),
                &destination,
                &mut out,
            )?;
        }
        Some("verify") if rest.len() == 1 => {
            if !case_cli::verify(Path::new(&rest[0]), &mut out)? {
                out.flush()?;
                return Err(io::Error::other(
                    "One or more exported attachments failed verification.",
                ));
            }
        }
        Some("collect" | "inspect") => {
            let parse = op.as_ref().is_some_and(|op| op == "inspect");
            run_collection(
                rest.into_iter().map(PathBuf::from).collect(),
                None,
                parse,
                &mut out,
            )?;
        }
        Some("collect-profile" | "inspect-profile") if rest.len() >= 2 => {
            let kind = rest[0]
                .to_str()
                .and_then(profile::Kind::parse)
                .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidInput, USAGE))?;
            let parse = op.as_ref().is_some_and(|op| op == "inspect-profile");
            run_collection(
                rest.into_iter().skip(1).map(PathBuf::from).collect(),
                Some(kind),
                parse,
                &mut out,
            )?;
        }
        Some("export-express") if rest.len() == 5 => {
            let expected = sha256(&rest[1])?;
            let offset = count(&rest[2])?;
            let selector = rest[3]
                .to_str()
                .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidInput, USAGE))?;
            let item = mime::Export::parse(selector)
                .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidInput, USAGE))?;
            let (bytes, hash) = collect::export_express(
                Path::new(&rest[0]),
                expected,
                offset,
                item,
                Path::new(&rest[4]),
            )?;
            serde_json::to_writer(
                &mut out,
                &serde_json::json!({
                    "schema": SCHEMA,
                    "module": MODULE,
                    "engine_version": VERSION,
                    "source_sha256": expected.to_ascii_lowercase(),
                    "entry_offset": offset,
                    "selector": selector,
                    "bytes": bytes,
                    "sha256": hash,
                }),
            )?;
            writeln!(out)?;
        }
        Some("export-mailbox") if rest.len() == 5 => {
            let path = Path::new(&rest[0]);
            let expected = sha256(&rest[1])?;
            let offset = count(&rest[2])?;
            let selector = rest[3].to_str().unwrap_or_default();
            let item = mime::Export::parse(selector).ok_or_else(|| {
                io::Error::new(
                    io::ErrorKind::InvalidInput,
                    "Invalid mailbox export selector.",
                )
            })?;
            let (bytes, hash) =
                collect::write_verified(path, expected, Path::new(&rest[4]), |input, size| {
                    let prefix = mime::read_range(input, 0, size.min(8192))?;
                    let format = collect::identify(path, &prefix, size).format;
                    mailbox::export(input, size, format, offset, item)
                })?;
            writeln!(
                out,
                "{}",
                serde_json::json!({
                    "schema": SCHEMA, "module": MODULE, "engine_version": VERSION,
                    "source_sha256": expected.to_ascii_lowercase(), "entry_offset": offset,
                    "selector": selector, "bytes": bytes, "sha256": hash,
                })
            )?;
        }
        Some("read-msg-stream") if rest.len() == 5 => {
            let expected = sha256(&rest[1])?;
            let compound_path = rest[2].to_str().ok_or_else(|| {
                io::Error::new(io::ErrorKind::InvalidInput, "Invalid compound stream path.")
            })?;
            let decompress = match rest[3].to_str() {
                Some("raw") => false,
                Some("rtf") => true,
                _ => {
                    return Err(io::Error::new(
                        io::ErrorKind::InvalidInput,
                        "Choose raw or rtf.",
                    ));
                }
            };
            let (bytes, hash) = collect::write_verified(
                Path::new(&rest[0]),
                expected,
                Path::new(&rest[4]),
                |input, size| {
                    if size > mime::MESSAGE_LIMIT {
                        return Err(io::Error::new(
                            io::ErrorKind::InvalidData,
                            "MSG exceeds the 128 MiB input limit.",
                        ));
                    }
                    outlook::msg_stream(input, compound_path, decompress)
                },
            )?;
            writeln!(
                out,
                "{}",
                serde_json::json!({
                    "schema": SCHEMA, "module": MODULE, "engine_version": VERSION,
                    "source_sha256": expected.to_ascii_lowercase(), "compound_path": compound_path,
                    "representation": if decompress { "decoded_rtf" } else { "stored_cfb_stream" },
                    "bytes": bytes, "sha256": hash,
                })
            )?;
        }
        Some("read-range") if rest.len() == 5 => {
            let expected = sha256(&rest[1])?;
            let (offset, length) = (count(&rest[2])?, count(&rest[3])?);
            let hash = collect::copy_range(
                Path::new(&rest[0]),
                expected,
                offset,
                length,
                Path::new(&rest[4]),
            )?;
            writeln!(
                out,
                "{{\"schema\":\"{SCHEMA}\",\"offset\":{offset},\"length\":{length},\"sha256\":\"{hash}\"}}"
            )?;
        }
        Some("--version") if rest.is_empty() => writeln!(out, "{MODULE} {VERSION}")?,
        Some("--help") if rest.is_empty() => writeln!(out, "{USAGE}")?,
        _ => return Err(io::Error::new(io::ErrorKind::InvalidInput, USAGE)),
    }
    out.flush()
}

const USAGE: &str = "mailviewer-engine report [--csv] FILE_OR_DIRECTORY ...\nmailviewer-engine extract OUTPUT_ROOT FILE_OR_DIRECTORY ...\nmailviewer-engine verify EXTRACTION_DIRECTORY\nmailviewer-engine collect|inspect FILE_OR_DIRECTORY ...\nmailviewer-engine collect-profile|inspect-profile outlook-express|windows-mail|thunderbird PROFILE_OR_STORE ...\nmailviewer-engine read-range FILE SHA256 OFFSET LENGTH NEW_OUTPUT\nmailviewer-engine read-msg-stream MSG SHA256 COMPOUND_PATH raw|rtf NEW_OUTPUT\nmailviewer-engine export-express FILE SHA256 ENTRY_OFFSET eml|headers|attachment:ZERO_BASED_INDEX NEW_OUTPUT\nmailviewer-engine export-mailbox MMDF_OR_BABYL SHA256 ENTRY_OFFSET eml|headers|attachment:ZERO_BASED_INDEX NEW_OUTPUT\nmailviewer-engine --version";

fn main() -> ExitCode {
    match run() {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) if error.kind() == io::ErrorKind::BrokenPipe => ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("{error}");
            ExitCode::FAILURE
        }
    }
}
