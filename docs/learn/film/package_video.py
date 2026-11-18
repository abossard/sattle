"""Add the chapter index and English captions to a local HyperFrames render."""
import hashlib
import json
import re
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
LEARN = HERE.parent
story = json.loads((LEARN/"story-data.js").read_text().removeprefix("const story = ").strip().removesuffix(";"))


def versioned_asset(match):
    path = match.group(1)
    digest = hashlib.sha256((LEARN/path).read_bytes()).hexdigest()
    return f'{path}?v={digest[:16]}'


with tempfile.TemporaryDirectory(dir=LEARN) as temp:
    chapters = Path(temp)/"chapters.txt"
    film = Path(temp)/"sattle.mp4"
    poster = Path(temp)/"poster.jpg"
    lines = [";FFMETADATA1","title=How the shop gets its health model"]
    for scene in story:
        start,end = round(scene["beats"][0]["start"]*1000), round(scene["beats"][-1]["end"]*1000)
        lines.extend(["[CHAPTER]","TIMEBASE=1/1000",f"START={start}",f"END={end}","title="+scene["chapter"]])
    chapters.write_text("\n".join(lines)+"\n")
    # Single-narrator film. Mux the same AAC that the guide plays so the render's
    # stereo mix and second lossy encode do not change the voice or its level.
    subprocess.run(["ffmpeg","-loglevel","error","-y","-i",sys.argv[1],"-i",str(HERE/"assets/voice.m4a"),
                    "-i",str(LEARN/"captions.vtt"),"-f","ffmetadata","-i",str(chapters),
                    "-map","0:v:0","-map","1:a:0","-map","2:0","-map_metadata","3","-map_chapters","3",
                    "-c:v","copy","-c:a","copy","-c:s","mov_text",
                    "-movie_timescale","1000","-metadata:s:s:0","language=eng","-movflags","+faststart",str(film)],check=True)
    # Show the database outage and the healthy cache in the player preview.
    impact = next(scene for scene in story if scene["id"] == "incident")["beats"][1]
    preview_time = impact["start"] + 0.85 * (impact["end"] - impact["start"])
    subprocess.run(["ffmpeg","-loglevel","error","-y","-ss",str(preview_time),"-i",str(film),
                    "-frames:v","1","-q:v","3",str(poster)],check=True)
    # Publish the version last, once the film and its captions are ready to play.
    version = Path(temp)/"film-version.txt"
    version.write_text(hashlib.sha256(film.read_bytes()).hexdigest() + "\n")
    guide = Path(temp)/"index.html"
    guide.write_text(re.sub(r'([\w./-]+\.(?:js|css|m4a))\?v=[\w-]+', versioned_asset,
                            (LEARN/"index.html").read_text()))
    film.replace(LEARN/"sattle.mp4")
    poster.replace(HERE/"poster.jpg")
    guide.replace(LEARN/guide.name)
    version.replace(LEARN/version.name)
