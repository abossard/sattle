# How the shop gets its health model

[Open the interactive guide](index.html) · [Watch the narrated film](sattle.mp4) · [Read the transcript](transcript.md)

Open the HTML file in a browser from a local checkout. Select **Play with narration** to hear the chapter, click a moment to jump ahead, or drag the time control. The upper diagram keeps the whole shop in view. The lower diagram shows the observation, rule or change currently being explained. Arrows point from a component to what it needs. During a failure, the animated dots travel back toward the affected callers.

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

For film editing, the narration script is in [lesson.json](film/lesson.json). The film and walkthrough share the same [animated diagrams](film/shop-map.js) and recorded audio. The narration build uses the installed macOS Samantha voice, Python 3 and FFmpeg. Install HyperFrames under the film folder for local rendering; the ready-to-watch files need no installation.

```sh
npm --prefix docs/learn/film ci
python3 -B docs/learn/film/build.py
npm --prefix docs/learn/film run check
npm --prefix docs/learn/film run render
```

The film uses the captured local Canvas and repository screenshots. The included GSAP animation runtime is version 3.14.2. Its original license notice is preserved in [the library](film/vendor/gsap.min.js).
