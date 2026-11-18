"""Package a local render with its original voice, captions, chapters and guide."""
import argparse
import hashlib
import json
import math
import re
import shutil
import subprocess
import tempfile
from pathlib import Path

from build import offline_captions

HERE = Path(__file__).resolve().parent
LEARN = HERE.parent
RETAIL = LEARN/"foundry"


def run(*command):
    subprocess.run(command, check=True)


def ffmpeg(*args):
    run("ffmpeg", "-loglevel", "error", "-y", *args)


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def prepare_retail_stage(destination):
    """Serve the shared sources below one HTTP root for a standalone render."""
    destination.mkdir(parents=True, exist_ok=True)
    if any(destination.iterdir()):
        raise ValueError(f"Render staging directory must be empty: {destination}")
    composition = (RETAIL/"index.html").read_text()

    def copy_resource(match):
        attribute, url = match.groups()
        original = (RETAIL/url).resolve()
        original.relative_to(LEARN)
        relative = "shared/"+url.removeprefix("../film/") if url.startswith("../film/") else url
        target = destination/relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(original, target)
        return f'{attribute}="{relative}"'

    markup = re.sub(r'\b(src|href)="([\w./-]+)"', copy_resource, composition)
    (destination/"index.html").write_text(markup)


def publish(raw, retail):
    documents = RETAIL if retail else LEARN
    voice = RETAIL/"voice.m4a" if retail else HERE/"assets/voice.m4a"
    movie = RETAIL/"shop.mp4" if retail else LEARN/"sattle.mp4"
    poster = RETAIL/"poster.jpg" if retail else HERE/"poster.jpg"
    guide = LEARN/("index.html" if retail else "pg-shop.html")
    title = "One order through the whole shop" if retail else "How the shop gets its health model"
    story = json.loads((documents/"story-data.js").read_text().removeprefix("const story = ").strip().removesuffix(";"))
    length = float(subprocess.check_output(["ffprobe", "-v", "error", "-select_streams", "v:0",
                                           "-show_entries", "stream=duration", "-of", "csv=p=0", raw]))
    expected = story[-1]["beats"][-1]["end"]
    if abs(length-expected) > 1/24 + .001:
        raise ValueError(f"Video runs {length:.3f}s, but this narration requires {expected:.3f}s")

    with tempfile.TemporaryDirectory(dir=documents) as scratch:
        temp = Path(scratch)
        metadata = temp/"metadata.txt"
        lines = [";FFMETADATA1", "title="+title]
        for scene in story:
            start = round(scene["beats"][0]["start"]*1000)
            end = round(scene["beats"][-1]["end"]*1000)
            lines.extend(["[CHAPTER]", "TIMEBASE=1/1000", f"START={start}", f"END={end}", "title="+scene["chapter"]])
        metadata.write_text("\n".join(lines)+"\n")
        # Keep the exact narration from the interactive guide. The renderer can
        # produce a second, differently mixed audio track.
        ffmpeg("-i", raw, "-i", voice, "-i", documents/"captions.vtt", "-f", "ffmetadata", "-i", metadata,
               "-map", "0:v:0", "-map", "1:a:0", "-map", "2:0", "-map_metadata", "3", "-map_chapters", "3",
               "-c:v", "copy", "-c:a", "copy", "-c:s", "mov_text", "-movie_timescale", "1000",
               "-metadata:s:s:0", "language=eng", "-movflags", "+faststart", temp/movie.name)
        overview = story[0]["beats"][1]
        at = overview["start"]+.90*(overview["end"]-overview["start"])
        ffmpeg("-ss", str(at), "-i", temp/movie.name, "-frames:v", "1", "-q:v", "3", temp/poster.name)

        last_beat = {"logistics","sandbox","jobs","engagement"} if retail else {"incident","dependency","azure"}
        for i, scene in enumerate(story):
            beat = scene["beats"][2 if scene["id"] in last_beat else 1]
            at = beat["start"]+.86*(beat["end"]-beat["start"])
            ffmpeg("-ss", str(at), "-i", temp/movie.name, "-vf", "crop=1240:552:20:45,scale=600:-2:flags=lanczos",
                   "-frames:v", "1", temp/f"plate-{i:02d}.png")
        ffmpeg("-framerate", "1", "-i", temp/"plate-%02d.png", "-vf",
               f"tile=2x{math.ceil(len(story)/2)}:padding=8:margin=8:color=0xd1d3c9",
               "-frames:v", "1", "-q:v", "2", temp/"chapters.jpg")

        # Hash the incoming files before moving them. Each edition updates its
        # own guide and publishes its new playback digest last.
        uploads = {movie:temp/movie.name, poster:temp/poster.name, documents/"chapters.jpg":temp/"chapters.jpg"}
        if retail:
            # Show the business-level graph and the two original records behind
            # Payout → Returns intake at a readable, full-frame size.
            evidence = next(scene for scene in story if scene["id"] == "dependencies")["beats"][2]
            at = evidence["start"]+.90*(evidence["end"]-evidence["start"])
            frame = temp/"projection.jpg"
            ffmpeg("-ss", str(at), "-i", temp/movie.name, "-frames:v", "1", "-q:v", "2", frame)
            uploads[documents/frame.name] = frame
        hashes = {destination:digest(source) for destination, source in uploads.items()}
        local_track = temp/"captions-local.js"
        local_track.write_text(offline_captions((documents/"captions.vtt").read_text(), hashes[movie]))
        uploads[documents/local_track.name] = local_track
        hashes[documents/local_track.name] = digest(local_track)

        def versioned(match):
            attribute, relative = match.groups()
            path = (guide.parent/relative).resolve()
            if path not in hashes:
                hashes[path] = digest(path)
            return f'{attribute}="{relative}?v={hashes[path][:16]}"'

        text = re.sub(r'\b(src|href|poster)="([\w./-]+\.(?:js|css|m4a|mp4|jpg|vtt))(?:\?v=[\w-]+)?"',
                      versioned, guide.read_text())
        updated = temp/"guide.html"
        updated.write_text(text)
        version = temp/"film-version.txt"
        version.write_text(hashes[movie]+"\n")
        for destination, source in uploads.items():
            source.replace(destination)
        updated.replace(guide)
        version.replace(documents/version.name)
    print(f"{movie.name}: {len(story)} chapters, {length:.2f}s, SHA-256 {hashes[movie]}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("rendered_file", nargs="?", type=Path, help="package the video track from this MP4")
    parser.add_argument("--retail", action="store_true", help="select the illustrated shop instead of the pg-shop replay")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--stage", type=Path, help="prepare a standalone retail composition for HyperFrames")
    mode.add_argument("--render", action="store_true", help="render and publish the retail composition")
    mode.add_argument("--check", action="store_true", help="run HyperFrames checks on the retail composition")
    args = parser.parse_args()
    if args.rendered_file:
        if args.stage or args.render or args.check:
            parser.error("provide a rendered file or choose --stage, --render or --check")
        publish(args.rendered_file.resolve(), args.retail)
    elif args.retail and args.stage:
        prepare_retail_stage(args.stage)
    elif args.retail and (args.render or args.check):
        # npm scripts supply HyperFrames on PATH. Stage only runtime inputs so
        # the rendered movie and the guide never become composition assets.
        with tempfile.TemporaryDirectory() as scratch:
            stage = Path(scratch)/"retail"
            prepare_retail_stage(stage)
            if args.check:
                run("hyperframes", "check", stage)
            else:
                video = Path(scratch)/"render.mp4"
                run("hyperframes", "render", stage, "--quality", "delivery", "--fps", "24", "--output", video)
                publish(video, True)
    else:
        parser.error("provide a rendered MP4, or use --retail with --stage, --render or --check")


if __name__ == "__main__":
    main()
