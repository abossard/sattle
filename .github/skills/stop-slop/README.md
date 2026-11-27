# Stop Slop

Imported from https://github.com/abossard/stop-slop at commit
`066a6299f17a70146a198c9ac77b77179e2db36f`, under the included MIT license.
Only relative paths were changed to keep the skill self-contained.

Copilot discovers this skill in `.github/skills/stop-slop/SKILL.md`.
Use `/stop-slop` to ask for a rewrite, or let the agent load it when writing
text for a reader.

From the repository root, check a saved draft with:

```bash
.github/skills/stop-slop/tools/readcheck --file README.md
```

The wrapper uses `uv` when available. Otherwise, it uses Python with spaCy
and the `en_core_web_sm` model. Missing Python dependencies produce exit
code 3; the agent should report that and continue without the checker.
