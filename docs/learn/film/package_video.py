"""Add the chapter index and English captions to a local HyperFrames render."""
import json
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
LEARN = HERE.parent
story = json.loads((LEARN/"story-data.js").read_text().removeprefix("const story = ").strip().removesuffix(";"))
with tempfile.TemporaryDirectory() as temp:
    chapters = Path(temp)/"chapters.txt"
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
                    "-movie_timescale","1000","-metadata:s:s:0","language=eng","-movflags","+faststart",str(LEARN/"sattle.mp4")],check=True)
    subprocess.run(["ffmpeg","-loglevel","error","-y","-ss","7","-i",str(LEARN/"sattle.mp4"),
                    "-frames:v","1","-q:v","3",str(HERE/"poster.jpg")],check=True)
