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


def write_view_starts(project, story):
    """Switch outputs at their recorded paragraphs, even after a wording change."""
    beat = next(scene for scene in story if scene["id"] == "estate")["beats"][-1]
    if len(beat["answerStarts"]) != 5:
        raise ValueError("The Five views answer needs one paragraph for each of the five outputs.")
    duration = beat["end"] - beat["start"]
    starts = [0] + [round((time-beat["start"])/duration, 6) for time in beat["answerStarts"][1:]]
    (project/"view-starts.js").write_text("const shopViewStarts = "+json.dumps(starts)+";\n")


# Keep captions and diagrams in the project's spelling. Only the narrator sees
# the spoken forms, including clock times that a general text reader may misread.
PRONUNCIATIONS = {
    "GKQL": "G K Q L", "graph-match": "graph match",
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


def narrate(narrator, style, passage, speed):
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
        "style": style[count], "speed": np.array([speed], dtype=np.float32),
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


def generate_voice(lesson, temp, voice, documents, retail):
    from kokoro_onnx import Kokoro

    directory = HERE / ".narrator"
    narrator = Kokoro(str(directory/"kokoro-v1.0.onnx"), str(directory/"voices-v1.0.bin"))
    listener = narrator.get_voice_style("am_michael")
    guide = narrator.get_voice_style("af_heart")
    listener_speed = guide_speed = 1.0
    turn_gap, paragraph_gap = .35, .18
    if retail:
        # The voices have their own rhythm. Kokoro predicts the phoneme lengths
        # at these speeds, so the captions follow what each speaker actually says.
        listener = .45*listener + .55*narrator.get_voice_style("am_fenrir")
        guide = .45*guide + .55*narrator.get_voice_style("af_sarah")
        listener_speed, guide_speed = 1.24, 1.22
        turn_gap, paragraph_gap = .12, .08
    audio, captions = bytearray(), ["WEBVTT\n"]
    times = []
    for scene in lesson:
        print(f"Recording {scene['chapter']}", flush=True)
        beats = []
        for cue in scene["cues"]:
            start = len(audio) / (2*RATE)
            answers = cue["answer"] if isinstance(cue["answer"], list) else [cue["answer"]]
            answer_starts = []
            spoken_cues = []
            passages = [(listener, cue["question"], "Listener", listener_speed, turn_gap)]
            for index, paragraph in enumerate(answers):
                speaker = "Guide" if index == 0 else ""
                passages.append((guide, paragraph, speaker, guide_speed, turn_gap if speaker else paragraph_gap))
            for turn, (style, text, speaker, speed, pause) in enumerate(passages):
                audio.extend(bytes(round(pause * RATE) * 2))
                speech_start = len(audio) / (2*RATE)
                if turn > 0:
                    answer_starts.append(speech_start)
                pcm, phrases = narrate(narrator, style, text, speed)
                audio.extend(pcm)
                for i, (first, last, phrase) in enumerate(phrases):
                    if retail and turn:
                        spoken_cues.append([round(speech_start + first/RATE, 3), phrase])
                    if speaker and i == 0:
                        phrase = f"{speaker}: {phrase}"
                    captions.append(f"{clock(speech_start + first/RATE)} --> {clock(speech_start + last/RATE)}\n"
                                    + "\n".join(textwrap.wrap(phrase, 42, break_on_hyphens=False))+"\n")
            # Hold the finished diagram after the explanation, especially at a
            # chapter boundary. Keep short definitions on screen for eight seconds.
            pause = 1.2 if len(beats) + 1 == len(scene["cues"]) else .8
            end = math.ceil(max(len(audio)/(2*RATE) + pause, start + 8) * FPS) / FPS
            audio.extend(bytes(round(end*RATE)*2-len(audio)))
            beat = dict(**cue, start=start, end=end, answerStarts=answer_starts)
            if retail:
                beat["spokenCues"] = spoken_cues
            beats.append(beat)
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
    title = "From the master graph to system health" if retail else "How the shop gets its health model"
    shared = "../film/" if retail else ""
    voice = "voice.m4a" if retail else "assets/voice.m4a"
    if retail:
        drawings = ["case.js", "projection.js", "view-starts.js", "graph-journey.js", "journey-scene.js", "replay-cases.js", "replay-scenes.js", "gkql-example.js", "gkql-scene.js", shared+"figure.js", "story-data.js", "chapters.js"]
    else:
        drawings = ["master-data.js", "model-scenes.js", "change-scenes.js", "review-scenes.js", "figure.js", "shop-map.js"]
    scripts = "".join(f'<script src="{src}"></script>' for src in [shared+"vendor/gsap.min.js", "terms.js", *drawings])
    shots=[]
    for i, scene in enumerate(story):
        beats=scene["beats"]
        case = {"estate":"Order O-104 · illustrated shop", "rollup":"pg-shop · recorded estate",
                "discovery":"pg-shop · new API call to Redis", "gkql":"X12 · deployment health"}.get(scene["id"], "pg-shop on Azure")
        start, end=beats[0]["start"], beats[-1]["end"]
        offsets=",".join(str(b["end"]-start) for b in beats)
        questions = html.escape(json.dumps([b["question"] for b in beats], ensure_ascii=False), quote=True)
        answers = ",".join(str(b["answerStarts"][0]-start) for b in beats)
        shots.append(f'''<section class="clip shot" id="{scene['id']}" data-start="{start:.5f}" data-duration="{end-start:.5f}" data-track-index="0" data-beat-ends="{offsets}" data-questions="{questions}" data-answer-starts="{answers}">
<header><b>Sattle</b><span>{case}</span><span class="chapter">{html.escape(scene['chapter'])}</span><span>{i+1:02d} / {len(story):02d}</span></header>
<h1>{html.escape(scene['title'])}</h1>
<div class="story-board" data-topic="{scene['id']}"></div>
<div class="term-card"><strong class="term"></strong><span class="definition"></span></div></section>''')
    total=story[-1]["beats"][-1]["end"]
    return f'''<!doctype html><html lang="en"><head><meta charset="utf-8"><title>{title}</title>
<meta name="color-scheme" content="dark"><link rel="stylesheet" href="{shared}theme.css"><link rel="stylesheet" href="{shared}style.css">{scripts}</head>
<body class="{'retail' if retail else 'replay'}"><main id="film" data-composition-id="shop" data-width="1280" data-height="720" data-fps="24" data-duration="{total:.5f}">
{chr(10).join(shots)}
<audio id="voice" class="clip" data-start="0" data-duration="{total:.5f}" data-track-index="3" src="{voice}"></audio></main><script>{(HERE/"motion.js").read_text()}</script></body></html>'''


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
                     "cues":[{key:beat[key] for key in ("focus","question","answer")} for beat in chapter["beats"]]} for chapter in story]
        if recorded != lesson:
            parser.error("The lesson has new text. Record the voice before refreshing the markup.")
        if args.retail:
            write_view_starts(project, story)
        (project/"index.html").write_text(composition(story, args.retail))
        return
    with tempfile.TemporaryDirectory() as temp:
        story = generate_voice(lesson, Path(temp), voice, documents, args.retail)
    if args.retail:
        write_view_starts(project, story)
    (project/"index.html").write_text(composition(story, args.retail))
    (documents/"story-data.js").write_text("const story = "+json.dumps(story,indent=2,ensure_ascii=False)+";\n")
    title = "From the master graph to system health" if args.retail else "How the shop gets its health model"
    paragraphs = ["# "+title, "Transcript of the narrated Sattle field guide."]
    for scene in story:
        paragraphs.append(f"## {clock(scene['beats'][0]['start'])[3:8]} · {scene['chapter']}")
        for beat in scene["beats"]:
            paragraphs.append("**Listener**\n\n"+beat["question"])
            answer = beat["answer"]
            paragraphs.append("**Guide**\n\n"+("\n\n".join(answer) if isinstance(answer,list) else answer))
    (documents/"transcript.md").write_text("\n\n".join(paragraphs)+"\n")
    print(f"{len(story)} chapters, {story[-1]['beats'][-1]['end']:.2f} seconds")


if __name__=="__main__":
    main()
