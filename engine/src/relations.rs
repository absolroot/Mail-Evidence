use crate::{
    collect::Source,
    model::{ByteRange, ExpressFolder, Format, IdxEntry, Inspection, MbxEntry},
};
use serde::Serialize;
use std::{
    collections::{BTreeMap, HashMap, HashSet},
    path::{Path, PathBuf},
};

#[derive(Default)]
pub struct Discovery {
    directories: BTreeMap<PathBuf, Vec<MailboxFile>>,
    retained_rows: usize,
}

struct MailboxFile {
    id: String,
    format: Format,
    name: String,
    folders: Vec<ExpressFolder>,
    idx: Vec<IdxEntry>,
    mbx: Vec<MbxEntry>,
    status: &'static str,
}

#[derive(Serialize)]
pub struct Store {
    directory: PathBuf,
    folders: Vec<FolderLink>,
    mailbox_indexes: Vec<IndexLink>,
}

#[derive(Serialize)]
struct FolderLink {
    index_source_id: String,
    folder_entry: ByteRange,
    folder_id: Option<u32>,
    parent_state: &'static str,
    mailbox_source_id: Option<String>,
    mailbox_state: &'static str,
}

#[derive(Serialize)]
struct IndexLink {
    index_source_id: Option<String>,
    mailbox_source_id: Option<String>,
    state: &'static str,
    matched: Vec<ByteRange>,
    unmatched: Vec<ByteRange>,
    unindexed_mbx_entries: Vec<ByteRange>,
}

impl Discovery {
    pub fn add(
        &mut self,
        source: &Source,
        status: &'static str,
        view: Option<&mut Inspection>,
    ) -> bool {
        use Format::*;
        let format = source.identification.format;
        if !matches!(
            format,
            Dbx | DbxFolders | OutlookExpressMbx | OutlookExpressIdx | OutlookExpressNch
        ) || source.identification.basis == "extension_hint"
        {
            return true;
        }
        let Some(directory) = source.opened_path.parent() else {
            return true;
        };
        let Some(name) = source.opened_path.file_name() else {
            return true;
        };
        let rows = view.as_ref().map_or(0, |view| {
            view.express_folders.len() + view.idx_entries.len() + view.mbx_entries.len()
        });
        if self.retained_rows.saturating_add(rows) > 1_000_000 {
            return false;
        }
        self.retained_rows += rows;
        let (folders, idx, mbx) = view.map_or_else(
            || (Vec::new(), Vec::new(), Vec::new()),
            |view| {
                (
                    std::mem::take(&mut view.express_folders),
                    std::mem::take(&mut view.idx_entries),
                    std::mem::take(&mut view.mbx_entries),
                )
            },
        );
        self.directories
            .entry(directory.to_owned())
            .or_default()
            .push(MailboxFile {
                id: source.id.clone(),
                name: name.to_string_lossy().to_lowercase(),
                format,
                folders,
                idx,
                mbx,
                status,
            });
        true
    }

    pub fn report(self) -> Vec<Store> {
        self.directories
            .into_iter()
            .map(|(directory, files)| Store::from_files(directory, &files))
            .collect()
    }
}

impl Store {
    fn from_files(directory: PathBuf, files: &[MailboxFile]) -> Self {
        use Format::*;
        let mut folders = Vec::new();
        let mut mailbox_indexes = Vec::new();
        let mut indexed_mailboxes = HashSet::new();
        let mut by_name: HashMap<&str, Vec<&MailboxFile>> = HashMap::new();
        for file in files {
            by_name.entry(file.name.as_str()).or_default().push(file);
        }
        let lookup = |name: &str, format| {
            let mut candidates = by_name
                .get(name)
                .into_iter()
                .flatten()
                .filter(|file| file.format == format);
            match (candidates.next(), candidates.next()) {
                (Some(file), None) => (Some(*file), "linked"),
                (None, _) => (None, "not_collected"),
                _ => (None, "ambiguous"),
            }
        };
        for index in files {
            if matches!(index.format, DbxFolders | OutlookExpressNch) {
                let parent_states = parent_states(&index.folders);
                for (folder, parent_state) in index.folders.iter().zip(parent_states) {
                    let mut mailbox = folder.mailbox_name.as_ref().map(|name| name.to_lowercase());
                    let format = if index.format == DbxFolders {
                        Dbx
                    } else {
                        OutlookExpressMbx
                    };
                    if format == OutlookExpressMbx
                        && let Some(name) = mailbox.as_mut()
                        && !name.ends_with(".mbx")
                    {
                        name.push_str(".mbx");
                    }
                    let (source, state) = match mailbox {
                        Some(name) if safe_name(&name) => lookup(&name, format),
                        Some(_) => (None, "invalid_name"),
                        None => (None, "no_stored_filename"),
                    };
                    folders.push(FolderLink {
                        index_source_id: index.id.clone(),
                        folder_entry: folder.entry,
                        folder_id: folder.id,
                        parent_state,
                        mailbox_source_id: source.map(|source| source.id.clone()),
                        mailbox_state: state,
                    });
                }
            }
            if index.format == OutlookExpressIdx {
                let name = Path::new(&index.name).with_extension("mbx");
                let (mailbox, state) = lookup(&name.to_string_lossy(), OutlookExpressMbx);
                let mut link = IndexLink {
                    index_source_id: Some(index.id.clone()),
                    mailbox_source_id: mailbox.map(|source| source.id.clone()),
                    state,
                    matched: Vec::new(),
                    unmatched: Vec::new(),
                    unindexed_mbx_entries: Vec::new(),
                };
                if let Some(mailbox) = mailbox {
                    indexed_mailboxes.insert(&mailbox.id);
                    if index.status == "identified" || mailbox.status == "identified" {
                        link.state = "name_match_only";
                    } else {
                        link.validate(&index.idx, &mailbox.mbx);
                        if link.state == "linked"
                            && (index.status != "parsed" || mailbox.status != "parsed")
                        {
                            link.state = "incomplete";
                        }
                    }
                }
                mailbox_indexes.push(link);
            }
        }
        for mailbox in files.iter().filter(|file| {
            file.format == OutlookExpressMbx && !indexed_mailboxes.contains(&file.id)
        }) {
            mailbox_indexes.push(IndexLink {
                index_source_id: None,
                mailbox_source_id: Some(mailbox.id.clone()),
                state: "no_companion_index",
                matched: Vec::new(),
                unmatched: Vec::new(),
                unindexed_mbx_entries: mailbox.mbx.iter().map(|entry| entry.entry).collect(),
            });
        }
        Self {
            directory,
            folders,
            mailbox_indexes,
        }
    }
}

impl IndexLink {
    fn validate(&mut self, index: &[IdxEntry], mailbox: &[MbxEntry]) {
        let mbx: HashMap<_, _> = mailbox
            .iter()
            .map(|entry| (entry.entry.offset, entry))
            .collect();
        let mut counts = HashMap::new();
        for row in index {
            *counts.entry(row.mailbox_entry.offset).or_insert(0u32) += 1;
        }
        let mut matched_offsets = HashSet::new();
        for row in index {
            if counts[&row.mailbox_entry.offset] == 1
                && mbx.get(&row.mailbox_entry.offset).is_some_and(|entry| {
                    entry.entry == row.mailbox_entry && entry.message_number == row.message_number
                })
            {
                self.matched.push(row.entry);
                matched_offsets.insert(row.mailbox_entry.offset);
            } else {
                self.unmatched.push(row.entry);
            }
        }
        self.unindexed_mbx_entries.extend(
            mailbox
                .iter()
                .filter(|entry| !matched_offsets.contains(&entry.entry.offset))
                .map(|entry| entry.entry),
        );
        if !self.unmatched.is_empty() || !self.unindexed_mbx_entries.is_empty() {
            self.state = "index_mismatch";
        }
    }
}

fn parent_states(folders: &[ExpressFolder]) -> Vec<&'static str> {
    let mut ids = HashMap::new();
    let mut duplicates = HashSet::new();
    for (index, folder) in folders.iter().enumerate() {
        if let Some(id) = folder.id
            && ids.insert(id, index).is_some()
        {
            duplicates.insert(id);
        }
    }
    let mut states = vec![None; folders.len()];
    for start in 0..folders.len() {
        if states[start].is_some() {
            continue;
        }
        let mut chain = Vec::new();
        let mut visited = HashSet::new();
        let mut current = start;
        let reason = loop {
            if let Some(state) = states[current] {
                break if matches!(state, "root" | "linked") {
                    "linked"
                } else {
                    state
                };
            }
            if !visited.insert(current) {
                break "cycle";
            }
            chain.push(current);
            let folder = &folders[current];
            if folder.id.is_some_and(|id| duplicates.contains(&id)) {
                break "duplicate_id";
            }
            match folder.parent_id {
                None => {
                    states[current] = Some("root");
                    break "linked";
                }
                Some(parent) if duplicates.contains(&parent) => break "ambiguous_parent",
                Some(parent) => match ids.get(&parent) {
                    Some(&index) => current = index,
                    None => break "missing_parent",
                },
            }
        };
        for item in chain {
            states[item].get_or_insert(reason);
        }
    }
    states
        .into_iter()
        .map(|state| state.unwrap_or("unknown"))
        .collect()
}

fn safe_name(name: &str) -> bool {
    !name.is_empty()
        && !name.ends_with(['.', ' '])
        && !name.chars().any(|c| {
            c.is_control() || matches!(c, '\\' | '/' | ':' | '*' | '?' | '"' | '<' | '>' | '|')
        })
}
