import argparse
import ctypes
import json
import os
import runpy
import time
import uuid
from pathlib import Path


def main():
    workspace = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser()
    parser.add_argument("output", type=Path)
    parser.add_argument("--theme", choices=("light", "dark"))
    parser.add_argument("--scenario", choices=("mail", "msg", "pst", "ost", "ost-expanded", "investigation", "mailbox-folders", "mailbox-marks", "attachment-boundaries", "reader-attachments", "mail-fields", "startup-focus", "find-sources", "disk-image", "languages"), default="mail")
    args = parser.parse_args()
    smoke = workspace / "tools/FlowSmoke/bin/Release/net10.0-windows/FlowSmoke.exe"
    if not smoke.is_file():
        parser.error(f"Build FlowSmoke in Release first: {smoke}")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    launcher = runpy.run_path(str(workspace / "tools/probe-windows-desktop.py"))
    desktop_name = f"MailViewerSmoke_{os.getpid()}_{uuid.uuid4().hex}"
    desktop = launcher["create_desktop"](desktop_name, None, None, 0, 0x01FF, None)
    if not desktop:
        raise ctypes.WinError(ctypes.get_last_error())
    process = None
    try:
        command = [str(smoke), str(workspace), str(output)]
        if args.theme:
            command.append(f"--{args.theme}")
        if args.scenario != "mail":
            command.append(f"--{args.scenario}")
        process = launcher["DesktopProcess"](command, smoke.parent, desktop_name)
        deadline = time.monotonic() + 55
        acknowledged = False
        attachment_dialog_accepted = False
        while time.monotonic() < deadline:
            result = process.poll()
            if result is not None:
                print(f"FlowSmoke exit={result}, evidence={output}")
                if not result and not (output / "flow-result.json").is_file():
                    raise RuntimeError("FlowSmoke ended without a result")
                return result
            if args.scenario == "ost-expanded" and not acknowledged:
                for window in launcher["get_windows"](desktop, process.pid):
                    if window["class"] != "#32770" or window["title"] != "일부 자료 판독":
                        continue
                    yes = next((child for child in window["children"] if child["control_id"] == 6), None)
                    if yes and launcher["post"](window["handle"], 0x0111, 6, yes["handle"]):
                        record = {"title": window["title"], "action": "continue with readable source coverage"}
                        (output / "partial-read-dialog.json").write_text(
                            json.dumps(record, ensure_ascii=False) + "\n", encoding="utf-8"
                        )
                        acknowledged = True
            if args.scenario in ("mail", "msg") and not attachment_dialog_accepted:
                for window in launcher["get_windows"](desktop, process.pid):
                    if window["class"] != "#32770" or window["title"] not in ("첨부파일 저장", "Save attachment"):
                        continue
                    save = next((child for child in window["children"] if child["control_id"] == 1), None)
                    if save and launcher["post"](window["handle"], 0x0111, 1, save["handle"]):
                        attachment_dialog_accepted = True
            time.sleep(0.1)
        raise TimeoutError(f"FlowSmoke timed out: {output}")
    finally:
        if process is not None:
            process.close()
        launcher["close_desktop"](desktop)


if __name__ == "__main__":
    raise SystemExit(main())
