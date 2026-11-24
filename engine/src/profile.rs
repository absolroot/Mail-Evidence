use crate::{
    collect::{Candidate, Discovery, Failure, identify, is_redirect, open_read_only},
    model::{Format, Identification},
};
use serde::Serialize;
use std::{
    collections::HashSet,
    fs,
    io::{self, Read},
    path::{Component, Path, PathBuf},
};

#[derive(Clone, Copy)]
pub enum Kind {
    OutlookExpress,
    WindowsMail,
    Thunderbird,
}

impl Kind {
    pub fn parse(value: &str) -> Option<Self> {
        match value {
            "outlook-express" => Some(Self::OutlookExpress),
            "windows-mail" => Some(Self::WindowsMail),
            "thunderbird" => Some(Self::Thunderbird),
            _ => None,
        }
    }

    pub fn name(self) -> &'static str {
        match self {
            Self::OutlookExpress => "outlook_express_profile",
            Self::WindowsMail => "windows_mail_profile",
            Self::Thunderbird => "thunderbird_profile",
        }
    }

    pub fn accepts(self, format: Format) -> bool {
        use Format::*;
        match self {
            Self::OutlookExpress => matches!(
                format,
                Dbx | DbxFolders
                    | DbxMetadata
                    | OutlookExpressIdx
                    | OutlookExpressMbx
                    | OutlookExpressNch
                    | Mbox
            ),
            Self::WindowsMail => matches!(format, Eml),
            Self::Thunderbird => matches!(format, Eml | Mbox | ThunderbirdIndex),
        }
    }

    pub fn identify(self, candidate: &Candidate, identification: &mut Identification, size: u64) {
        if matches!(self, Self::Thunderbird)
            && size == 0
            && identification.format == Format::Unknown
            && candidate.path.extension().is_none()
            && regular(&candidate.path.with_extension("msf"))
        {
            *identification = Identification {
                format: Format::Mbox,
                basis: "empty_mailbox_with_msf",
                ndb: None,
            };
        }
    }

    pub fn logical_folder(self, candidate: &Candidate) -> Option<Vec<String>> {
        if matches!(self, Self::OutlookExpress) {
            return None;
        }
        let path = candidate.path.strip_prefix(&candidate.root).ok()?;
        let mut folders: Vec<_> = path
            .parent()?
            .iter()
            .map(|part| part.to_string_lossy().into_owned())
            .collect();
        if matches!(self, Self::Thunderbird) {
            for folder in &mut folders {
                if folder.to_ascii_lowercase().ends_with(".sbd") {
                    folder.truncate(folder.len() - 4);
                }
            }
            let parent = folders.last().map(String::as_str);
            if !matches!(parent, Some("cur" | "new")) {
                let name = path.file_name()?.to_string_lossy();
                let name = if name.to_ascii_lowercase().ends_with(".msf") {
                    &name[..name.len() - 4]
                } else {
                    &name
                };
                folders.push(name.to_owned());
            } else {
                folders.pop();
            }
        }
        Some(folders)
    }
}

#[derive(Serialize)]
pub struct Root {
    pub requested_path: PathBuf,
    pub store_path: PathBuf,
    pub basis: &'static str,
}

pub fn discover_profiles(kind: Kind, inputs: Vec<PathBuf>) -> (Discovery, Vec<Root>) {
    let mut result = Discovery::default();
    let mut stores = Vec::new();
    let mut used = HashSet::new();
    for requested in inputs {
        if !directory(&requested) {
            failure(
                &mut result,
                requested,
                "profile_root_unreadable",
                "Select a regular profile or store directory.",
            );
            continue;
        }
        let found = match kind {
            Kind::OutlookExpress => express_roots(&requested),
            Kind::WindowsMail => windows_roots(&requested),
            Kind::Thunderbird => thunderbird_roots(&requested, &mut result),
        };
        if found.is_empty() {
            failure(
                &mut result,
                requested.clone(),
                "profile_store_not_found",
                "No recognized local store was found under this root.",
            );
        }
        for (path, basis) in found {
            let key = fs::canonicalize(&path).unwrap_or_else(|_| path.clone());
            if !used.insert(key) {
                continue;
            }
            let mut plan = crate::collect::discover(vec![path.clone()]);
            for candidate in &mut plan.files {
                candidate.discovered_by = Some(basis);
                candidate.profile_kind = Some(kind);
            }
            result.files.extend(plan.files);
            result.failures.extend(plan.failures);
            stores.push(Root {
                requested_path: requested.clone(),
                store_path: path,
                basis,
            });
        }
    }
    (result, stores)
}

fn directory(path: &Path) -> bool {
    fs::symlink_metadata(path).is_ok_and(|meta| meta.is_dir() && !is_redirect(&meta))
}

fn regular(path: &Path) -> bool {
    fs::symlink_metadata(path).is_ok_and(|meta| meta.is_file() && !is_redirect(&meta))
}

fn inside(root: &Path, relative: &Path) -> Option<PathBuf> {
    let mut result = root.to_owned();
    for component in relative.components() {
        let Component::Normal(part) = component else {
            return None;
        };
        result.push(part);
        if !directory(&result) {
            return None;
        }
    }
    Some(result)
}

fn children(root: &Path) -> impl Iterator<Item = PathBuf> {
    let mut files: Vec<_> = fs::read_dir(root)
        .into_iter()
        .flatten()
        .take(100_000)
        .filter_map(Result::ok)
        .map(|entry| entry.path())
        .collect();
    files.sort_unstable();
    files.into_iter()
}

fn file_name(path: &Path) -> String {
    path.file_name()
        .unwrap_or_default()
        .to_string_lossy()
        .to_lowercase()
}

fn has_express_files(root: &Path) -> bool {
    children(root).any(|path| {
        regular(&path)
            && (file_name(&path) == "folders.nch"
                || path.extension().is_some_and(|ext| {
                    ext.eq_ignore_ascii_case("mbx") || ext.eq_ignore_ascii_case("dbx")
                }))
    })
}

fn express_roots(root: &Path) -> Vec<(PathBuf, &'static str)> {
    let mut stores = Vec::new();
    if has_express_files(root) {
        stores.push((root.to_owned(), "selected_outlook_express_store"));
    }
    for suffix in [
        "Mail",
        "Outlook Express",
        "Microsoft/Outlook Express",
        "Application Data/Microsoft/Outlook Express",
        "Windows/Application Data/Microsoft/Outlook Express",
        "Local Settings/Application Data/Microsoft/Outlook Express",
    ] {
        if let Some(store) = inside(root, Path::new(suffix))
            && has_express_files(&store)
        {
            stores.push((store, "outlook_express_default_layout"));
        }
    }
    for suffix in [
        "Identities",
        "Application Data/Identities",
        "Windows/Application Data/Identities",
        "Local Settings/Application Data/Identities",
        "AppData/Local/Identities",
    ] {
        if let Some(identities) = inside(root, Path::new(suffix)) {
            for identity in children(&identities).filter(|path| directory(path)) {
                if let Some(store) = inside(&identity, Path::new("Microsoft/Outlook Express"))
                    && has_express_files(&store)
                {
                    stores.push((store, "outlook_express_identity_layout"));
                }
            }
        }
    }
    stores
}

fn windows_roots(root: &Path) -> Vec<(PathBuf, &'static str)> {
    let stores: Vec<_> = [
        "AppData/Local/Microsoft/Windows Mail",
        "AppData/Local/Microsoft/Windows Live Mail",
        "Microsoft/Windows Mail",
        "Microsoft/Windows Live Mail",
        "Windows Mail",
        "Windows Live Mail",
    ]
    .into_iter()
    .filter_map(|suffix| inside(root, Path::new(suffix)))
    .map(|path| (path, "windows_mail_default_layout"))
    .collect();
    if stores.is_empty() {
        return vec![(root.to_owned(), "selected_windows_mail_store")];
    }
    stores
}

fn thunderbird_roots(root: &Path, result: &mut Discovery) -> Vec<(PathBuf, &'static str)> {
    let mut stores = Vec::new();
    for start in [
        Some(root.to_owned()),
        inside(root, Path::new("AppData/Roaming/Thunderbird")),
        inside(root, Path::new("Application Data/Thunderbird")),
        inside(root, Path::new("Application Data/Mozilla/Thunderbird")),
        inside(root, Path::new(".thunderbird")),
        inside(root, Path::new("Library/Thunderbird")),
    ]
    .into_iter()
    .flatten()
    {
        let config = start.join("profiles.ini");
        if regular(&config) {
            match config_profiles(&start, &config, result) {
                Ok(paths) => {
                    for profile in paths {
                        mail_roots(&profile, "thunderbird_profiles_ini", &mut stores);
                    }
                }
                Err(error) => failure(result, config, "thunderbird_profiles_unreadable", error),
            }
        } else if let Some(profiles) = inside(&start, Path::new("Profiles")) {
            for profile in children(&profiles).filter(|path| directory(path)) {
                mail_roots(&profile, "thunderbird_profiles_directory", &mut stores);
            }
        }
        mail_roots(&start, "selected_thunderbird_profile", &mut stores);
        if matches!(file_name(&start).as_str(), "mail" | "imapmail" | "news") {
            stores.push((start, "selected_thunderbird_store"));
        }
    }
    stores
}

fn mail_roots(profile: &Path, basis: &'static str, stores: &mut Vec<(PathBuf, &'static str)>) {
    stores.extend(
        ["Mail", "ImapMail", "News"]
            .into_iter()
            .filter_map(|name| inside(profile, Path::new(name)))
            .map(|path| (path, basis)),
    );
}

fn config_profiles(root: &Path, config: &Path, result: &mut Discovery) -> io::Result<Vec<PathBuf>> {
    let mut text = String::new();
    open_read_only(config)?
        .take(1_048_577)
        .read_to_string(&mut text)?;
    if text.len() > 1_048_576 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "profiles.ini is larger than 1 MiB.",
        ));
    }
    let mut profiles = Vec::new();
    for section in text.split('[').skip(1) {
        let Some((header, fields)) = section.split_once(']') else {
            continue;
        };
        if !header.starts_with("Profile") {
            continue;
        }
        let mut fields = fields.lines().filter_map(|line| line.split_once('='));
        let Some(path) = fields
            .clone()
            .find_map(|(key, value)| (key.trim() == "Path").then_some(value.trim()))
        else {
            continue;
        };
        let relative =
            fields.find_map(|(key, value)| (key.trim() == "IsRelative").then_some(value.trim()));
        let path = path.replace('\\', "/");
        if matches!(relative, None | Some("1"))
            && let Some(profile) = inside(root, Path::new(&path))
        {
            profiles.push(profile);
        } else {
            failure(
                result,
                config.to_owned(),
                "thunderbird_profile_path_not_followed",
                path,
            );
        }
    }
    Ok(profiles)
}

fn failure(result: &mut Discovery, path: PathBuf, code: &'static str, detail: impl ToString) {
    result.failures.push(Failure {
        path,
        code,
        detail: detail.to_string(),
    });
}

pub fn add_companions(plan: &mut Discovery) {
    let mut seen: HashSet<_> = plan.files.iter().map(|file| key(&file.path)).collect();
    let mut additional = Vec::new();
    for file in plan.files.iter().filter(|file| file.explicit) {
        let Ok(mut input) = open_read_only(&file.path) else {
            continue;
        };
        let mut prefix = Vec::new();
        if (&mut input).take(8192).read_to_end(&mut prefix).is_err() {
            continue;
        }
        let format = identify(
            &file.path,
            &prefix,
            input.metadata().map_or(0, |meta| meta.len()),
        )
        .format;
        use Format::*;
        if !matches!(
            format,
            Dbx | DbxFolders | OutlookExpressMbx | OutlookExpressIdx | OutlookExpressNch
        ) {
            continue;
        }
        let folder = file
            .path
            .parent()
            .filter(|path| !path.as_os_str().is_empty())
            .unwrap_or(Path::new("."));
        let stem = file
            .path
            .file_stem()
            .unwrap_or_default()
            .to_string_lossy()
            .to_lowercase();
        for sibling in children(folder) {
            let name = file_name(&sibling);
            let matches = match format {
                Dbx => name == "folders.dbx",
                DbxFolders => name.ends_with(".dbx"),
                OutlookExpressMbx => name == "folders.nch" || name == format!("{stem}.idx"),
                OutlookExpressIdx => name == "folders.nch" || name == format!("{stem}.mbx"),
                OutlookExpressNch => name.ends_with(".mbx") || name.ends_with(".idx"),
                _ => false,
            };
            if matches && regular(&sibling) && seen.insert(key(&sibling)) {
                additional.push(Candidate {
                    root: file.root.clone(),
                    path: sibling,
                    explicit: false,
                    discovered_by: Some("outlook_express_companion"),
                    profile_kind: Some(Kind::OutlookExpress),
                });
            }
        }
    }
    plan.files.extend(additional);
}

fn key(path: &Path) -> PathBuf {
    fs::canonicalize(path).unwrap_or_else(|_| path.to_owned())
}
