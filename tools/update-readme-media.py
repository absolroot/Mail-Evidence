"""Regenerate the English README media from a Windows WPF run."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
from tempfile import TemporaryDirectory

from PIL import Image, ImageDraw, ImageFont


FRAMES = (
    ("01-mail.png", "Browse the imported messages", 2400),
    ("02-search.png", "Find a message as you type", 2800),
    ("03-attachment.png", "Preview the selected attachment", 3400),
    ("04-anomaly.png", "Compare the declared length with the available bytes", 5200),
    ("05-hex.png", "Open the marked byte range at 0x21", 4000),
    ("06-return.png", "Return to the same finding", 2300),
)


def render(captures, output):
    result = json.loads((captures / "flow-result.json").read_text(encoding="utf-8"))
    if result != {"Language": "en-US", "Findings": 4, "Offset": "0x21"}:
        raise RuntimeError(f"Unexpected README demo result: {result}")
    output.mkdir(parents=True, exist_ok=True)
    font = ImageFont.truetype(str(Path(os.environ["WINDIR"]) / "Fonts/seguisb.ttf"), 19)
    frames = []
    for name, caption, _ in FRAMES:
        with Image.open(captures / name) as raw:
            if raw.width < 1000 or raw.height < 600:
                raise RuntimeError(f"README capture is too small: {name}")
            screen = raw.convert("RGB")
            screen.thumbnail((1280, 860), Image.Resampling.LANCZOS)
        if name == "04-anomaly.png":
            screen.save(output / "anomaly-evidence-dark.png", optimize=True)
        card = Image.new("RGB", (screen.width, screen.height + 38), "#181b21")
        card.paste(screen)
        draw = ImageDraw.Draw(card)
        draw.line((0, screen.height, screen.width, screen.height), fill="#555d6a")
        draw.text((16, screen.height + 18), caption, font=font, anchor="lm", fill="#f2f4f7")
        frames.append(card.quantize(colors=192))
    frames[0].save(output / "full-app-walkthrough-dark.gif", save_all=True,
                   append_images=frames[1:], duration=[ms for _, _, ms in FRAMES],
                   optimize=True, loop=0)


def main():
    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=root / "docs/media")
    args = parser.parse_args()
    if sys.platform != "win32":
        parser.error("README media capture requires Windows.")
    with TemporaryDirectory(prefix="mail-evidence-readme-") as temporary:
        scratch = Path(temporary)
        artifacts = scratch / "build"
        subprocess.run(
            ["dotnet", "build", "tools/FlowSmoke/FlowSmoke.csproj", "-c", "Release",
             "--artifacts-path", str(artifacts)], cwd=root, check=True
        )
        captures = scratch / "captures"
        env = dict(os.environ, WEBVIEW2_USER_DATA_FOLDER=str(scratch / "webview"))
        run = subprocess.run(
            [sys.executable, str(root / "tools/run-flow-smoke.py"), str(captures),
             "--scenario", "readme-demo", "--executable",
             str(artifacts / "bin/FlowSmoke/release/FlowSmoke.exe")],
            cwd=root, env=env
        )
        if run.returncode:
            failure = captures / "failure.txt"
            if failure.exists():
                print(failure.read_text(encoding="utf-8"), file=sys.stderr)
            run.check_returncode()
        render(captures, args.output.resolve())


if __name__ == "__main__":
    main()
