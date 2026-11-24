"""Regenerate the English README media from a Windows WPF run."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
from tempfile import TemporaryDirectory

from PIL import Image, ImageDraw


# A target is recorded in the screenshot that precedes the action.
SCENES = (
    ("01-mail.png", None, 1600),
    ("02-typing.png", "search", 230),
    ("03-search.png", None, 940),
    ("04-attachments.png", "attachments", 1450),
    ("05-preview.png", "preview", 1600),
    ("06-expanded.png", "expand", 500),
    ("06b-image.png", "file-info", 2050),
    ("07-restored.png", "restore", 380),
    ("08-all.png", "reset", 280),
    ("09-anomaly-mailbox.png", "anomaly-mailbox", 950),
    ("11-anomaly.png", "png-length", 2300),
    ("12-hex.png", "hex", 2100),
    ("13-return.png", "back", 850),
)

POINTER = ((0, 0), (0, 20), (5, 15), (9, 23), (13, 21), (9, 14), (16, 14))
RING_COLOR = (60, 164, 243)


def shared_palette(screens):
    width, height = screens[0].size
    thumb_size = (width // 4, height // 4)
    samples = Image.new("RGB", (thumb_size[0] * 4, thumb_size[1] * ((len(screens) + 3) // 4)))
    for index, screen in enumerate(screens):
        thumb = screen.resize(thumb_size, Image.Resampling.NEAREST)
        samples.paste(thumb, ((index % 4) * thumb_size[0], (index // 4) * thumb_size[1]))
    colors = samples.quantize(colors=253).getpalette()[:253 * 3]
    palette = Image.new("P", (1, 1))
    palette.putpalette(colors + [255, 255, 255, 24, 28, 34, *RING_COLOR])
    return palette


def render(captures, output):
    result = json.loads((captures / "flow-result.json").read_text(encoding="utf-8"))
    if result != {
        "Language": "en-US", "Messages": 24, "Preview": "market-stall-map.png",
        "Findings": 4, "Offset": "0x21",
    }:
        raise RuntimeError(f"Unexpected README demo result: {result}")

    points = json.loads((captures / "clicks.json").read_text(encoding="utf-8"))
    if set(points) != {target for _, target, _ in SCENES if target}:
        raise RuntimeError("Click targets do not match the GIF sequence.")
    screens = []
    for name, _, _ in SCENES:
        with Image.open(captures / name) as raw:
            if raw.width < 1000 or raw.height < 600:
                raise RuntimeError(f"README capture is too small: {name}")
            screen = raw.convert("RGB")
            screen.thumbnail((1280, 860), Image.Resampling.LANCZOS)
        screens.append(screen)

    width, height = screens[0].size
    if any(screen.size != (width, height) for screen in screens):
        raise RuntimeError("The capture changed size during the demo.")
    palette = shared_palette(screens)
    frames, durations = [], []
    position = (width - 46, 69)

    def frame(screen, duration, ring=None):
        image = screen.copy()
        draw = ImageDraw.Draw(image)
        x, y = position
        if ring is not None:
            draw.ellipse((x - ring, y - ring, x + ring, y + ring), outline=RING_COLOR, width=3)
        draw.polygon([(x + dx, y + dy) for dx, dy in POINTER], fill="white", outline=(24, 28, 34), width=2)
        frames.append(image.quantize(palette=palette, dither=Image.Dither.NONE))
        durations.append(duration)

    click_frames = []
    for index, ((_, target, hold), screen) in enumerate(zip(SCENES, screens)):
        if target:
            point = points[target]
            if not (0 <= point["X"] < 1 and 0 <= point["Y"] < 1):
                raise RuntimeError(f"Click target outside the capture: {target}")
            before = screens[index - 1]
            start = position
            end = (round(point["X"] * width), round(point["Y"] * height))
            for step in range(1, 6):
                ease = step / 5
                ease = ease ** 2 * (3 - 2 * ease)
                position = tuple(round(a + (b - a) * ease) for a, b in zip(start, end))
                frame(before, 50)
            click_frames.append((len(frames), position))
            frame(before, 90, ring=9)
            frame(screen, 90, ring=14)
        frame(screen, hold)

    output.mkdir(parents=True, exist_ok=True)
    gif = output / "full-app-walkthrough-dark.gif"
    frames[0].save(gif, save_all=True, append_images=frames[1:],
                   duration=durations, optimize=True, loop=0)
    # Verify the click indicator survives GIF encoding and RGB palette conversion.
    with Image.open(gif) as demo:
        if demo.n_frames < 40 or demo.size != (width, height):
            raise RuntimeError("The published GIF lost the pointer movement frames.")
        # Duplicate state frames can merge during encoding, so check the clicks by time.
        elapsed = 0
        click_times = {sum(durations[:index]) + 20: point for index, point in click_frames}
        for index in range(demo.n_frames):
            demo.seek(index)
            end = elapsed + demo.info["duration"]
            for when, (x, y) in list(click_times.items()):
                if elapsed <= when < end:
                    crop = demo.convert("RGB").crop((x - 10, y - 10, x + 11, y + 11))
                    if RING_COLOR not in crop.getdata():
                        raise RuntimeError(f"Missing click indicator at {when} ms.")
                    del click_times[when]
            elapsed = end
        if click_times:
            raise RuntimeError("The published GIF ended before the final click.")
        print(f"README GIF: {demo.n_frames} frames, {elapsed / 1000:.2f}s, {width}×{height}")
    screenshots = {name: screen for (name, _, _), screen in zip(SCENES, screens)}
    screenshots["05-preview.png"].save(output / "attachment-preview-dark.png", optimize=True)
    screenshots["11-anomaly.png"].save(output / "anomaly-evidence-dark.png", optimize=True)


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
