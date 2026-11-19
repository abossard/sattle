"""Generate local voice, captions, teaching diagrams' timings and the HTML film."""
import argparse
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


def offline_captions(vtt, version=""):
    # A file-opened page can load a sibling script even when the browser blocks fetch().
    # Keep the payload on its own script element so a later edition can load in the same tab.
    return "document.currentScript.sattleFilm = "+json.dumps({"version":version,"captions":vtt})+";\n"


def clock(seconds):
    ms = round(seconds * 1000)
    return f'{ms//3600000:02d}:{ms//60000%60:02d}:{ms//1000%60:02d}.{ms%1000:03d}'


# Keep captions and diagrams in the project's spelling. Only the narrator sees
# the spoken forms, including clock times that a general text reader may misread.
PRONUNCIATIONS = {
    "pg-shop": "P G shop", "offers-cache": "offers cache",
    "10:06:30": "thirty seconds past ten oh six", "10:11": "ten eleven",
    "10:14": "ten fourteen", "10:15": "ten fifteen",
}


def caption_phrases(passage):
    """Give long sentences a second caption at a clause or word boundary."""
    for sentence in re.split(r"(?<=[.!?])\s+", passage):
        while len(sentence) > 78:
            middle = len(sentence) // 2
            breaks = [m.end() for m in re.finditer(r"[,;] | (?=(?:and|but|because|so|then|when|to) )", sentence)]
            nearby = [i for i in breaks if 25 <= i <= 76 and len(sentence) - i >= 20]
            if not nearby:
                nearby = [m.start() for m in re.finditer(" ", sentence) if 25 <= m.start() <= 76]
            if not nearby:
                break
            cut = min(nearby, key=lambda i: abs(i-middle))
            yield sentence[:cut].strip()
            sentence = sentence[cut:].strip()
        yield sentence


def narrate(narrator, style, passage):
    """Read a whole diagram beat. Use the model's phoneme durations for captions."""
    import numpy as np
    from kokoro_onnx.config import MAX_PHONEME_LENGTH

    phrases = list(caption_phrases(passage))
    tokens, offsets = [0], []
    for phrase in phrases:
        spoken = phrase.replace("’", "'")
        for written, pronunciation in PRONUNCIATIONS.items():
            spoken = spoken.replace(written, pronunciation)
        if offsets:
            tokens.extend(narrator.tokenizer.tokenize(" "))
        offsets.append(len(tokens))
        phones = narrator.tokenizer.phonemize(spoken, "en-us")
        tokens.extend(narrator.tokenizer.tokenize(phones))
    count = len(tokens) - 1
    if count > MAX_PHONEME_LENGTH:
        raise ValueError(f"Split this passage into two beats: {passage}")
    tokens.append(0)
    # This v1.1 export returns both a waveform and the duration of each token.
    samples, durations = narrator.sess.run(["waveform", "duration"], {
        "input_ids": np.array([tokens], dtype=np.int64),
        "style": style[count], "speed": np.array([1.0], dtype=np.float32),
    })
    boundaries = np.concatenate(([0], np.cumsum(durations)))
    if len(durations) != len(tokens) or not np.all(np.isfinite(samples)):
        raise ValueError("Invalid narration or token timings")
    samples_per_tick, remainder = divmod(len(samples), int(boundaries[-1]))
    if remainder or samples_per_tick == 0:
        raise ValueError("Audio and caption clocks disagree")
    ends = offsets[1:] + [len(tokens) - 1]
    cues = [
        (int(boundaries[first]) * samples_per_tick, int(boundaries[last]) * samples_per_tick, phrase)
        for first, last, phrase in zip(offsets, ends, phrases)
    ]
    pcm = np.rint(np.clip(samples, -1, 1)*32767).astype("<i2").tobytes()
    return pcm, cues


def generate_voice(lesson, temp, voice, documents):
    from kokoro_onnx import Kokoro

    directory = HERE / ".narrator"
    narrator = Kokoro(str(directory/"kokoro-v1.0.onnx"), str(directory/"voices-v1.0.bin"))
    style = narrator.get_voice_style("af_heart")
    audio, captions = bytearray(), ["WEBVTT\n"]
    times = []
    for scene in lesson:
        print(f"Recording {scene['chapter']}", flush=True)
        beats = []
        for focus, text in scene["cues"]:
            start = len(audio) / (2*RATE)
            # Give the viewer a moment to find the new term before it is spoken.
            audio.extend(bytes(round(.35 * RATE) * 2))
            speech_start = len(audio) / (2*RATE)
            pcm, cues = narrate(narrator, style, text)
            audio.extend(pcm)
            for first, last, phrase in cues:
                captions.append(f"{clock(speech_start + first/RATE)} --> {clock(speech_start + last/RATE)}\n"
                                + "\n".join(textwrap.wrap(phrase, 42, break_on_hyphens=False))+"\n")
            # Hold the finished diagram after the explanation, especially at a
            # chapter boundary. Keep short definitions on screen for eight seconds.
            pause = 1.2 if len(beats) + 1 == len(scene["cues"]) else .8
            end = math.ceil(max(len(audio)/(2*RATE) + pause, start + 8) * FPS) / FPS
            audio.extend(bytes(round(end*RATE)*2-len(audio)))
            beats.append(dict(focus=focus, text=text, start=start, end=end))
        times.append(dict(id=scene["id"],chapter=scene["chapter"],title=scene["title"],beats=beats))
    with wave.open(str(temp/"voice.wav"),"wb") as out:
        out.setparams((1,2,RATE,0,"NONE","not compressed"))
        out.writeframes(audio)
    run("ffmpeg","-loglevel","error","-y","-i",str(temp/"voice.wav"),"-af","loudnorm=I=-16:TP=-1.5:LRA=9","-ar",str(RATE),"-c:a","aac","-b:a","112k",str(voice))
    vtt="\n".join(captions)
    (documents/"captions.vtt").write_text(vtt)
    # Keep the captions usable in browsers which restrict file:// text tracks.
    (documents/"captions-local.js").write_text(offline_captions(vtt))
    return times


def composition(story, retail):
    title = "One order through the whole shop" if retail else "How the shop gets its health model"
    case = "Order O-104 · Retail architecture" if retail else "pg-shop on Azure"
    shared = "../film/" if retail else ""
    voice = "voice.m4a" if retail else "assets/voice.m4a"
    if retail:
        drawings = ["case.js", "projection.js", "live-scenes.js", "work-scenes.js", "commerce-scene.js", "projection-scene.js", shared+"figure.js", "chapters.js"]
    else:
        drawings = ["master-data.js", "model-scenes.js", "change-scenes.js", "review-scenes.js", "figure.js", "shop-map.js"]
    scripts = "".join(f'<script src="{src}"></script>' for src in [shared+"vendor/gsap.min.js", "terms.js", *drawings])
    theme = ""
    if retail:
        # Use each drawing's paper and ink in the surrounding chapter header too.
        theme = """for (const scene of document.querySelectorAll('.shot')) {
  const chapter = ShopMap.chapters[scene.id];
  scene.style.setProperty('--page', chapter.tint);
  if (chapter.captionInk) {
    scene.style.setProperty('--type', '#f8faf2');
    scene.style.setProperty('--secondary', chapter.captionInk);
    scene.style.setProperty('--hairline', '#74909d');
    scene.style.setProperty('--spot', '#a7e6d9');
  }
}"""
    shots=[]
    for i, scene in enumerate(story):
        beats=scene["beats"]
        start, end=beats[0]["start"], beats[-1]["end"]
        offsets=",".join(str(b["end"]-start) for b in beats)
        shots.append(f'''<section class="clip shot" id="{scene['id']}" data-start="{start:.5f}" data-duration="{end-start:.5f}" data-track-index="0" data-beat-ends="{offsets}">
<header><b>Sattle</b><span>{case}</span><span class="chapter">{html.escape(scene['chapter'])}</span><span>{i+1:02d} / {len(story):02d}</span></header>
<h1>{html.escape(scene['title'])}</h1>
<div class="story-board" data-topic="{scene['id']}"></div>
<div class="term-card"><strong class="term"></strong><span class="definition"></span></div></section>''')
    total=story[-1]["beats"][-1]["end"]
    return f'''<!doctype html><html lang="en"><head><meta charset="utf-8"><title>{title}</title>
{scripts}<link rel="stylesheet" href="{shared}style.css"></head>
<body class="{'retail' if retail else 'replay'}"><main id="film" data-composition-id="shop" data-width="1280" data-height="720" data-fps="24" data-duration="{total:.5f}">
{chr(10).join(shots)}
<audio id="voice" class="clip" data-start="0" data-duration="{total:.5f}" data-track-index="3" src="{voice}"></audio></main><script>{theme}\n{(HERE/"motion.js").read_text()}</script></body></html>'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--retail", action="store_true", help="build the illustrated shop case")
    parser.add_argument("--compose-only", action="store_true", help="refresh the film markup from the existing recording")
    args = parser.parse_args()
    project = HERE.parent/"foundry" if args.retail else HERE
    documents = project if args.retail else HERE.parent
    voice = project/"voice.m4a" if args.retail else HERE/"assets/voice.m4a"
    lesson = json.loads((project/"lesson.json").read_text())
    if args.compose_only:
        encoded = (documents/"story-data.js").read_text().removeprefix("const story = ").strip().removesuffix(";")
        story = json.loads(encoded)
        recorded = [{**{key:chapter[key] for key in ("id","chapter","title")},
                     "cues":[[beat["focus"],beat["text"]] for beat in chapter["beats"]]} for chapter in story]
        if recorded != lesson:
            parser.error("The lesson has new text. Record the voice before refreshing the markup.")
        (project/"index.html").write_text(composition(story, args.retail))
        return
    with tempfile.TemporaryDirectory() as temp:
        story = generate_voice(lesson, Path(temp), voice, documents)
    (project/"index.html").write_text(composition(story, args.retail))
    (documents/"story-data.js").write_text("const story = "+json.dumps(story,indent=2,ensure_ascii=False)+";\n")
    title = "One order through the whole shop" if args.retail else "How the shop gets its health model"
    paragraphs = ["# "+title, "Transcript of the narrated Sattle field guide."]
    for scene in story:
        paragraphs.append(f"## {clock(scene['beats'][0]['start'])[3:8]} · {scene['chapter']}")
        paragraphs.extend(b["text"] for b in scene["beats"])
    (documents/"transcript.md").write_text("\n\n".join(paragraphs)+"\n")
    print(f"{len(story)} chapters, {story[-1]['beats'][-1]['end']:.2f} seconds")


if __name__=="__main__":
    main()
