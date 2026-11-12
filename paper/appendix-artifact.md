# Appendix: Artifact

This appendix is the only part of the paper that names programs, files and commands. It says where the software is, which part of it implements which chapter, and how to repeat each experiment of chapter 14.

## A.1 The release

- **Repository:** the Sattle repository in which this paper lives (the folder `paper/`).
- **Release:** *to be named at publication.* The experiments of chapter 14 are to be repeated on that release; its name will be entered here.
- **License:** see the file `LICENSE` at the root of the repository.

## A.2 What is in the repository

| Folder | What it is | Chapters it implements |
|---|---|---|
| `engine/src/Sattle.Core` | values and their canonical form, names of entities, the fixed core of types | 1, 2 |
| `engine/src/Sattle.Language` | the text form of definitions, its parser, printer and checker | 2, 5 |
| `engine/src/Sattle.Runtime` | graphs and versions, changes, step-by-step evaluation of projections, measurements, editable views, change of definitions, readers of sources, stores, restart | 3, 4, 6, 7, 8, 9, 10, 11 |
| `engine/src/Sattle.Cli` | the command line program `sattle` and the server that the viewer talks to | 10, 12 |
| `engine/definitions` | the definitions used in the experiments: types, naming rules, projections, the levels and the health model | 2, 5, 7 |
| `engine/tests/Sattle.Tests` | tests of the engine | 14 |
| `engine/tests/Sattle.Reference` | the independent, simple implementation written from the definitions of the paper | 6, 14 |
| `engine/tests/Sattle.Differential` | the comparison of the engine with that implementation on generated inputs | 6, 14 |
| `canvas/` | the viewer, a browser application | 12 |
| `lab/` | the description of the recording environment, the recording tools, and small scrubbed samples of recordings (`lab/fixtures`) | 10, 14 |
| `paper/evidence/` | the output of the runs that chapter 14 cites, one text file per run | 14 |
| `paper/inventory.md` | every numbered statement of the paper with its proof status | all |
| `paper/research/` | working documents: reviews, investigations, earlier states of the text | none |

## A.3 What can be repeated, and what cannot

- **Can be repeated from the repository:** every test; every experiment on the samples in `lab/fixtures` and on the synthetic estate in `engine/tests/Sattle.Tests/fixtures/estate`; the comparison with the independent implementation; the synthetic benchmarks; the checks of the viewer.
- **Cannot be repeated from the repository:** the experiments on the two real recordings. The recordings name real resources and are not published, and the cluster they were taken from no longer exists. For these the paper rests on the output files in `paper/evidence/`, in which real names are removed.

## A.4 Requirements

- .NET 10 for the engine.
- Node.js and a Chrome browser for the viewer and its checks.
- Optional: the Azure Cosmos DB emulator for the tests of that store.

## A.5 Commands

All engine commands run in the folder `engine/`; `sattle` stands for `dotnet src/Sattle.Cli/bin/Release/net10.0/sattle.dll`. All viewer commands run in the folder `canvas/`.

**Build.**

```bash
dotnet build -c Release
```

**Check the definitions** (chapters 2 and 5):

```bash
sattle check
```

**Tests of the engine** (chapter 14). The first command runs the default set; the others add the slow tests and the tests that need a real recording.

```bash
dotnet test -c Release
```

```bash
dotnet test -c Release --settings tests/slow.runsettings
```

```bash
dotnet test -c Release --settings tests/capture.runsettings
```

**Comparison with the independent implementation** (chapter 6, chapter 14):

```bash
dotnet test tests/Sattle.Differential -c Release
```

**Replay of a recording** (chapter 14). `DIR` is a recording; `lab/fixtures` and the synthetic estate work without further files. The second form replays in batches of `N` records, which must give the same fingerprint. The third writes the resulting graphs as files.

```bash
sattle replay DIR --cluster NAME
```

```bash
sattle replay DIR --cluster NAME --chunk N
```

```bash
sattle replay DIR --cluster NAME --out OUTDIR
```

For the synthetic estate: `DIR` is `tests/Sattle.Tests/fixtures/estate` and `NAME` is `synth`.

**Serve a recording to the viewer** (chapter 12):

```bash
sattle serve DIR --cluster NAME --port 5080
```

**Restart from a store** (chapter 11): serve with a store, stop, and start again with the store alone.

```bash
sattle serve DIR --cluster NAME --store sqlite=space.db
```

```bash
sattle serve --store sqlite=space.db
```

**Benchmarks** (chapter 13, chapter 14). The file `engine/PERF.md` lists each benchmark with its options.

```bash
sattle bench
```

```bash
sattle bench estate
```

**Viewer: tests and checks** (chapter 12).

```bash
npm ci
```

```bash
npm test
```

```bash
npm run typecheck
```

The next two need a served engine (see above) and a development server of the viewer.

```bash
npm run walkthroughs
```

```bash
npm run a11y
```

## A.6 Where each figure of chapter 14 comes from

Each run that chapter 14 cites has an output file in `paper/evidence/`. The first line of a file says what was run. The file names carry a number that orders the runs in time; a later number is a later state of the software. Figures that were measured by others in the project and not repeated by the authors of the paper are marked as such in chapter 14.
