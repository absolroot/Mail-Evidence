"""Fetch fixed OST fixtures and record independent libpff property digests."""

import argparse
import hashlib
import json
from pathlib import Path
import re
import runpy
from urllib.request import urlopen


HERE = Path(__file__).resolve().parent
COMMIT = "7d08ef9bc7112c23275fa948306c76bba6e24fba"
ORIGIN = f"https://raw.githubusercontent.com/epfromer/pst-extractor/{COMMIT}/example/testdata"
SAMPLES = {
    "primary": ("pstextractortestpdf", "c914f1f9eea55da0eb3884d063acf18a109fcb34cdd7dda623770f117ff63918"),
    "secondary": ("pstextractortest", "daacd8d441428634e103b7c1e37fe1e5909256cc917551667049b5e84dfcf5f0"),
}
PYPFF_VERSION = "20261030"
ASPOSE_ORIGIN = (
    "https://raw.githubusercontent.com/aspose-email/Aspose.Email-for-.NET/"
    "88cb2da311382bbbba6d14813127b0e6b3854c12/Examples/Data/MAPI"
)
EXPANDED_SAMPLES = {
    "legacy-permute": ("SampleOstFile.ost", 4_080_640,
                       "5ff8bc133935a03fad8241f271a604e1cc41e36640d6538d6bcc9bd39e6d9866",
                       23, 512, 1),
}
SDK_ORIGIN = "https://raw.githubusercontent.com/emk/pstsdk/21c7d2d57bd09555afd0a08adc966f9b3779cb19/test/sample2.pst"


def digest(data):
    return hashlib.sha256(data).hexdigest()


def source(name, expected, fetch):
    return fixed_source(f"{name}.ost", 16_818_176, expected, f"{ORIGIN}/{name}%40outlook.com.ost", fetch)


def fixed_source(name, size, expected, url, fetch):
    path = HERE / "ost" / name
    if not path.exists() and fetch:
        with urlopen(url, timeout=45) as response:
            data = response.read(size + 1)
        if len(data) != size or digest(data) != expected:
            raise ValueError(f"Downloaded OST digest did not match {name}.")
        path.parent.mkdir(exist_ok=True)
        path.write_bytes(data)
    data = path.read_bytes()
    if len(data) != size or digest(data) != expected:
        raise ValueError(f"Local OST digest did not match {path}.")
    return path, data, url


def properties(item):
    return {entry.entry_type: entry for entry in item.get_record_set(0).entries}


def property_digest(entry):
    raw = entry.data
    return dict(tag=(entry.entry_type << 16) | entry.value_type, length=len(raw), sha256=digest(raw))


def header_fields(entry):
    data = entry.data
    width = 2 if entry.value_type == 0x001F else 1
    encoding = "utf-16-le" if width == 2 else "latin1"
    text = data.decode(encoding).rstrip("\0")
    header_end = re.search(r"\r?\n\r?\n", text)
    if header_end:
        text = text[:header_end.start()] + header_end.group()[:len(header_end.group()) // 2]
    names = list(re.finditer(r"(?:\A|(?<=[\r\n]))([!-9;-~]+):", text))
    starts = [len(text[:match.start(1)].encode(encoding)) for match in names]
    starts.append(len(text.encode(encoding)))
    return [dict(name=match.group(1), offset=start, length=end-start, sha256=digest(data[start:end]))
            for match, start, end in zip(names, starts, starts[1:])]


def message_record(message, folder_nid, message_class, entries):
    header = None
    if 0x007D in entries:
        entry = entries[0x007D]
        header = property_digest(entry)
        header["fields"] = header_fields(entry)

    attachments = []
    for index, attachment in enumerate(message.attachments):
        entries = properties(attachment)
        method = entries[0x3705].data_as_integer
        raw = entries.get(0x3701) if method == 1 else None
        value = property_digest(raw) if raw else None
        if raw:
            data = raw.data
            value["ranges"] = [dict(offset=start, length=end-start, sha256=digest(data[start:end]))
                               for start, end in ((0, 8), (4064, 4128), (65_504, 65_568)) if end <= len(data)]
        attachments.append(dict(index=index, attachmentNid=attachment.identifier, name=attachment.long_filename,
                                method=method, property=value))

    return dict(messageNid=message.identifier, folderNid=folder_nid, subject=message.subject,
                messageClass=message_class, header=header, attachments=attachments)


def read_ost(pypff, path, raw, url):
    folders = []
    messages = []
    archive = pypff.open(str(path))

    def walk(folder, parent_nid):
        if folder.identifier & 0x1F == 3:
            return
        folders.append(dict(nid=folder.identifier, parentNid=parent_nid, name=folder.name,
                            itemCount=folder.number_of_sub_messages))
        for message in folder.sub_messages:
            entries = properties(message)
            message_class = entries[0x001A].data_as_string if 0x001A in entries else None
            if message_class is None or message_class.lower().startswith(("ipm.note", "report.ipm.note")):
                messages.append(message_record(message, folder.identifier, message_class, entries))
        for child in folder.sub_folders:
            walk(child, folder.identifier)

    try:
        walk(archive.get_root_folder(), None)
    finally:
        archive.close()
    version = int.from_bytes(raw[10:12], "little")
    return dict(file=path.name, url=url, clientMagic=raw[8:10].decode("ascii"),
                version=version, encoding=raw[461 if version in (14, 15) else 513], length=len(raw),
                sha256=digest(raw), folders=folders, messages=messages)


def prepare_controls(files, fetch):
    generator = runpy.run_path(str(HERE / "encode-ost-control.py"))
    permute_original = files["legacy-permute"][0]
    cyclic = HERE / "ost" / generator["OUTPUT_NAME"]
    count, _ = generator["create"](permute_original, cyclic)
    ansi_pst = fixed_source("sdk-sample2.pst", 271_360, generator["ANSI_SOURCE_SHA256"], SDK_ORIGIN, fetch)
    controls = [dict(path=cyclic, kind="reencoded", source=files["legacy-permute"], changedBlocks=count)]
    unicode21 = HERE / "ost" / "generated-aspose-v21.ost"
    generator["wrap_unicode21"](permute_original, unicode21)
    controls.append(dict(path=unicode21, kind="version-adjusted", source=files["legacy-permute"]))
    for version in (14, 15):
        control = HERE / "ost" / f"generated-sdk-v{version}.ost"
        generator["wrap_ansi"](ansi_pst[0], control, version)
        controls.append(dict(path=control, kind="relabelled-pst", source=ansi_pst))
    return controls


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("--fetch", action="store_true", help="Download missing originals from the pinned commit.")
    cli.add_argument("--fetch-only", action="store_true", help="Fetch and authenticate without installing pypff.")
    cli.add_argument("--expanded", action="store_true", help="Prepare additional OST variants instead of the v36 pair.")
    args = cli.parse_args()
    fetch = args.fetch or args.fetch_only
    files = ({role: fixed_source(name, size, expected, f"{ASPOSE_ORIGIN}/{name}", fetch)
              for role, (name, size, expected, _, _, _) in EXPANDED_SAMPLES.items()}
             if args.expanded else {role: source(name, expected, fetch)
                                    for role, (name, expected) in SAMPLES.items()})
    controls = prepare_controls(files, fetch) if args.expanded else []
    if args.fetch_only:
        for path, data, _ in files.values():
            print(f"{path.name}  {len(data)}  {digest(data)}")
        for control in controls:
            path = control["path"]
            data = path.read_bytes()
            print(f"{path.name}  {len(data)}  {digest(data)}  generated")
        return

    import pypff

    if pypff.get_version() != PYPFF_VERSION:
        raise RuntimeError(f"Install libpff-python=={PYPFF_VERSION} to regenerate these digests.")
    result = {"parser": "libpff-python", "parserVersion": pypff.get_version()}
    if args.expanded:
        cases = []
        for role, file in files.items():
            expected = EXPANDED_SAMPLES[role]
            case = read_ost(pypff, *file)
            if (case["clientMagic"], case["version"], case["encoding"]) != ("SO", expected[3], expected[5]):
                raise ValueError(f"Unexpected source variant in {case['file']}.")
            case.update(role=role, pageSize=expected[4])
            cases.append(case)
        result["samples"] = cases
        result["controls"] = []
        for control in controls:
            path = control["path"]
            source_sample = read_ost(pypff, *control["source"])
            derived = read_ost(pypff, path, path.read_bytes(), None)
            for field in ("folders", "messages"):
                if source_sample[field] != derived[field]:
                    raise ValueError(f"Independent verification found different {field} in {path.name}.")
            derived.update(kind=control["kind"], pageSize=512, originSha256=source_sample["sha256"],
                           originUrl=source_sample["url"], changedBlocks=control.get("changedBlocks"))
            if control["source"] == files["legacy-permute"]:
                del derived["folders"], derived["messages"]
                derived["expectationsFrom"] = "legacy-permute"
            result["controls"].append(derived)
    else:
        result.update({role: read_ost(pypff, *file) for role, file in files.items()})
    name = "ost-expanded-oracle.json" if args.expanded else "ost-oracle.json"
    (HERE / name).write_text(json.dumps(result, ensure_ascii=True, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
