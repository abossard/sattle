"""Generate local voice, captions, teaching diagrams' timings and the HTML film."""
import html
import json
import math
import re
import subprocess
import tempfile
import textwrap
import wave
from pathlib import Path

HERE = Path(__file__).resolve().parent
FPS, RATE = 24, 24000


def run(*args):
    subprocess.run(args, check=True)


def clock(seconds):
    ms = round(seconds * 1000)
    return f'{ms//3600000:02d}:{ms//60000%60:02d}:{ms//1000%60:02d}.{ms%1000:03d}'


def display(text):
    return (text.replace("P G Shop","pg-shop").replace("a k s synth","aks-synth")
            .replace("offers cache","offers-cache"))


def generate_voice(lesson, temp):
    audio, captions = bytearray(), ["WEBVTT\n"]
    times = []
    for scene in lesson:
        beats = []
        for focus, text in scene["cues"]:
            start = len(audio)/(2*RATE)
            for sentence in re.split(r'(?<=[.!?])\s+', text):
                run("say","-v","Samantha","-r","166","-o",str(temp/"speech.aiff"),sentence)
                run("ffmpeg","-loglevel","error","-y","-i",str(temp/"speech.aiff"),"-ar",str(RATE),"-ac","1",str(temp/"speech.wav"))
                with wave.open(str(temp/"speech.wav")) as clip:
                    pcm = clip.readframes(clip.getnframes())
                if len(pcm)<4000:
                    raise RuntimeError("The installed voice returned empty audio")
                begin = len(audio)/(2*RATE)
                audio.extend(pcm)
                captions.append(f"{clock(begin)} --> {clock(len(audio)/(2*RATE))}\n"
                                + "\n".join(textwrap.wrap(display(sentence), 55))+"\n")
                audio.extend(bytes(round(RATE*.13)*2))
            end = math.ceil((len(audio)/(2*RATE)+.26)*FPS)/FPS
            audio.extend(bytes(round(end*RATE)*2-len(audio)))
            beats.append(dict(focus=focus, text=display(text), start=start, end=end))
        times.append(dict(id=scene["id"],chapter=scene["chapter"],title=scene["title"],beats=beats))
    with wave.open(str(temp/"voice.wav"),"wb") as out:
        out.setparams((1,2,RATE,0,"NONE","not compressed"))
        out.writeframes(audio)
    run("ffmpeg","-loglevel","error","-y","-i",str(temp/"voice.wav"),"-c:a","aac","-b:a","96k",str(HERE/"assets/voice.m4a"))
    vtt="\n".join(captions)
    (HERE.parent/"captions.vtt").write_text(vtt)
    # Keep the captions usable in browsers which restrict file:// text tracks.
    (HERE.parent/"captions-local.js").write_text("const localCaptions = "+json.dumps(vtt)+";\n")
    return times


def composition(story):
    shots=[]
    for i, scene in enumerate(story):
        for b, beat in enumerate(scene["beats"]):
            start, end = beat["start"],beat["end"]
            shots.append(f'''<section class="clip shot" id="{scene['id']}-{b}" data-start="{start:.5f}" data-duration="{end-start:.5f}" data-track-index="0">
<header><b>Sattle</b><span>Field guide / Shop on Azure</span><span>{i+1:02d} / {len(story):02d}</span></header>
<div class="chapter">{html.escape(scene['chapter'])}</div><h1>{html.escape(scene['title'])}</h1>
<div class="story-board" data-topic="{scene['id']}" data-beat="{b}"></div>
<footer><span>{html.escape(beat['focus'])}</span><small>Whole workload above · Evidence below</small></footer></section>''')
    demo=next(s for s in story if s["id"]=="inspect")["beats"][0]
    shots.append(f'<video class="clip recording" id="replay-recording" muted playsinline src="assets/canvas-replay.mp4" data-start="{demo["start"]:.5f}" data-duration="7" data-track-index="2"></video>')
    total=story[-1]["beats"][-1]["end"]
    return f'''<!doctype html><html lang="en"><head><meta charset="utf-8"><title>How the shop gets its health model</title>
<script src="vendor/gsap.min.js"></script><script src="shop-map.js"></script><link rel="stylesheet" href="style.css"></head>
<body><main id="film" data-composition-id="shop" data-width="1280" data-height="720" data-fps="24" data-duration="{total:.5f}">
{chr(10).join(shots)}
<audio id="voice" class="clip" data-start="0" data-duration="{total:.5f}" data-track-index="3" src="assets/voice.m4a"></audio></main><script>{(HERE/"motion.js").read_text()}</script></body></html>'''


def main():
    lesson=json.loads((HERE/"lesson.json").read_text())
    with tempfile.TemporaryDirectory() as temp:
        story=generate_voice(lesson,Path(temp))
    (HERE/"index.html").write_text(composition(story))
    (HERE.parent/"story-data.js").write_text("const story = "+json.dumps(story,indent=2,ensure_ascii=False)+";\n")
    paragraphs=["# How the shop gets its health model","Transcript of the narrated Sattle field guide."]
    for scene in story:
        paragraphs.append(f"## {clock(scene['beats'][0]['start'])[3:8]} · {scene['chapter']}")
        paragraphs.extend(b["text"] for b in scene["beats"])
    (HERE.parent/"transcript.md").write_text("\n\n".join(paragraphs)+"\n")
    print(f"{len(story)} chapters, {story[-1]['beats'][-1]['end']:.2f} seconds")


if __name__=="__main__":
    main()
