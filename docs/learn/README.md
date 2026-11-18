# How the shop gets its health model

[Open the interactive guide](index.html) · [Watch the narrated film](sattle.mp4) · [Read the transcript](transcript.md)

Open the HTML file in a browser and select **Play with narration**. Choose a term to jump to its scene, or drag the time control to see a change again. Each term stays on screen with its definition while the diagram plays.

Read arrows from a caller to the service it needs. During an outage the dots travel back toward the callers. Green with a check means Healthy, amber with a triangle means Degraded, red with a cross means Unhealthy, and violet with a question mark means Unknown.

Both lessons work offline. To view them through a local server, run this from the repository root, then open the [field guide](http://127.0.0.1:8397/docs/learn/).

```sh
python3 -m http.server 8397 --bind 127.0.0.1
```

## Explore the two changes

The database timeline comes from the [synthetic shop’s Azure meter readings](../../engine/tests/Sattle.Tests/fixtures/estate/azure-metrics/shop.ndjson). The first zero belongs to the **10:11 UTC** sample. Its capture arrives at **10:12:01**. The static workload export records the database becoming unhealthy in **revision 16** and recovering in **revision 18**. Sample time, capture time and graph revision answer different questions.

The dependency chapters use local replay experiments with the same KQL and rules. Each experiment keeps the original fixture intact and replays a temporary copy through **10:06:30 UTC**.

| Experiment | Input difference | Engine result |
| --- | --- | --- |
| Existing cache | The API deployment gains a CACHE_HOST setting naming shopcache. | During the cache incident, API and gateway change from Healthy to Degraded. The workload structure graph records the configuration setting as evidence. |
| New Azure cache | The API names offers-cache. In the comparison run, Azure Resource Graph also reports the new Redis resource. | With the matching inventory row, offers-cache and its dependency appear. Its health is Unknown because this experiment provides no readings for the new cache. |

See the [structure query](../../engine/definitions/acme.workload.workloadStructure.kql), [health query](../../engine/definitions/acme.workload.workloadHealth.kql), [authored thresholds](../../engine/tests/Sattle.Tests/fixtures/estate/authoring/rules.ndjson), and [white paper](../../paper/README.md). To open the real Canvas alongside the guide, follow the [repository’s getting-started steps](../../README.md) and run `scripts/demo-replay.sh`. Choose the static synthetic estate for the exact revision numbers in the guide. Live replay can group events into different revisions.

## Reproduce the lesson

From the repository root, these commands extract the original evidence and rerun the two controlled comparisons with Sattle’s .NET 11 engine.

```sh
dotnet build engine/src/Sattle.Cli -c Release
python3 -B docs/learn/build_evidence.py
python3 -B docs/learn/replay_change.py
```

Edit the narration in [lesson.json](film/lesson.json) and the on-screen definitions in [terms.js](film/terms.js). The film and walkthrough share the voice recording, captions and [diagrams](film/shop-map.js). Each passage gets one take. The builder uses the timing of the spoken sounds to place the captions.

To record a new take, install Python 3.12 and FFmpeg. The setup script downloads the Kokoro model and its voices once into `film/.narrator/`. After that, you can record offline. This lesson uses the American English voice Heart. Playback needs only a browser. [Kokoro ONNX](https://github.com/thewh1teagle/kokoro-onnx)

```sh
sh docs/learn/film/setup-voice.sh
docs/learn/film/.narrator/venv/bin/python -B docs/learn/film/build.py
npm --prefix docs/learn/film ci
npm --prefix docs/learn/film run check
npm --prefix docs/learn/film run render
```

The film uses the captured local Canvas and repository screenshots. The included GSAP animation runtime is version 3.14.2. Its original license notice is preserved in [the library](film/vendor/gsap.min.js).
