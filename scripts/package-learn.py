"""Add the learning guides to an existing Pages atlas without rebuilding the films."""

import argparse
import html
from html.parser import HTMLParser
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
from urllib.parse import urlsplit


ROOT = Path(__file__).resolve().parents[1]
GUIDE = Path("docs/learn")


class PlaybackAssets(HTMLParser):
    def __init__(self):
        super().__init__()
        self.paths = set()

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        for key in ("src", "poster", "href"):
            if key == "href" and not (tag == "link" and attrs.get("rel") == "stylesheet"):
                continue
            if attrs.get(key):
                self.paths.add(GUIDE / urlsplit(attrs[key]).path)


def transport(element):
    # JSON numbers cannot carry nanosecond instants exactly into JavaScript.
    return {key: str(value) if key.endswith("_ns") else value for key, value in element.items()}


def package_gql(site):
    lab_dir = ROOT / "lab/gql"
    sys.path.insert(0, str(lab_dir))
    try:
        from gql_lab import CRITICAL_PERCENT, STALE_AFTER_NS, WARNING_PERCENT, GqlHealthLab, instant_ns
    finally:
        sys.path.pop(0)

    source = ROOT / GUIDE / "gql"
    target = site / GUIDE / "gql"
    target.mkdir(parents=True, exist_ok=True)
    for name in ("index.html", "replay.css", "replay.mjs", "page.mjs", "grafeo-license.txt"):
        shutil.copyfile(source / name, target / name)

    vendor = target / "vendor"
    vendor.mkdir(exist_ok=True)
    wasm = source / "node_modules/@grafeo-db/wasm"
    shutil.copyfile(wasm / "grafeo_wasm_bg.js", vendor / "grafeo_wasm_bg.mjs")
    shutil.copyfile(wasm / "grafeo_wasm_bg.wasm", vendor / "grafeo_wasm_bg.wasm")

    scenario = json.loads((source / "scenario.json").read_text(encoding="utf-8"))
    with GqlHealthLab() as lab:
        lab.load_envelopes(scenario["capture"])
        nodes = lab.query("master", "MATCH (n) RETURN n AS node ORDER BY n.id")
        edges = lab.query(
            "master", "MATCH (a)-[e]->(b) RETURN a.id AS source, e AS edge, b.id AS target "
            "ORDER BY a.id, b.id, type(e)")
    seed = {
        "checkpoints": [{**stop, "ns": str(instant_ns(stop["at"]))} for stop in scenario["checkpoints"]],
        "ontology": (lab_dir / "azure_ontology.gql").read_text(encoding="utf-8"),
        "projection": (lab_dir / "projection.gql").read_text(encoding="utf-8").strip().rstrip(";"),
        "policy": {
            "warning_percent": WARNING_PERCENT,
            "critical_percent": CRITICAL_PERCENT,
            "stale_after_ns": str(STALE_AFTER_NS),
        },
        "nodes": [transport(row["node"]) for row in nodes],
        "edges": [{**row, "edge": transport(row["edge"])} for row in edges],
    }
    (target / "replay.json").write_text(json.dumps(seed, separators=(",", ":")) + "\n", encoding="utf-8")


def package(site, source_base):
    tracked = {
        Path(path) for path in subprocess.check_output(
            ["git", "ls-files", "-z", "--", str(GUIDE)], cwd=ROOT
        ).decode().split("\0") if path
    }
    assets = PlaybackAssets()
    target = site / GUIDE
    target.mkdir(parents=True, exist_ok=True)

    # Keep the published film URLs available to people who saved them.
    assets.paths.update(GUIDE / name for name in
                        ("foundry/shop.mp4", "foundry/poster.jpg", "sattle.mp4", "film/poster.jpg"))
    for folder in (GUIDE, GUIDE / "foundry"):
        assets.paths.update(folder / name for name in ("film-version.txt", "captions.vtt", "captions-local.js"))
    # The replay opens Canvas captures when the reader reaches their chapter.
    captures = GUIDE / "film/assets"
    assets.paths.update(path for path in tracked if path.parent == captures and path.suffix == ".webp")
    assets.paths.add(captures / "canvas-replay.mp4")

    for page in ("index.html", "pg-shop.html"):
        source = (ROOT / GUIDE / page).read_text()
        assets.feed(source)
        source = source.replace('<html lang="en">', f'<html lang="en" data-source-base="{html.escape(source_base, quote=True)}">', 1)

        # GitHub renders the cited Markdown and its original heading anchors.
        def markdown_href(match):
            relative = match[1]
            path = (ROOT / GUIDE / relative).resolve().relative_to(ROOT)
            if not (ROOT / path).is_file():
                raise ValueError(f"Missing citation: {relative}")
            return f'href="{source_base}{path.as_posix()}{match[2] or ""}"'

        source = re.sub(r'href="([^":#?]+\.md)(#[^"]*)?"', markdown_href, source)
        (target / page).write_text(source)

    for path in sorted(assets.paths):
        if path not in tracked or (ROOT / path).is_symlink():
            raise ValueError(f"Expected a tracked playback asset: {path}")
        output = site / path
        output.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(ROOT / path, output)

    package_gql(site)

    # Add the guides to the hosted atlas without changing its standalone download.
    home = site / "index.html"
    contents = home.read_text()
    entries = ('<a href="docs/learn/">Retail walkthrough</a>',
               '<a href="docs/learn/gql/">GQL health replay</a>')
    missing = "".join(entry for entry in entries if entry not in contents)
    if missing:
        navigation = '<nav aria-label="Main navigation">'
        if navigation not in contents:
            raise ValueError("Missing atlas home navigation")
        home.write_text(contents.replace(navigation, navigation + missing, 1))
    print(f"Packaged the guides, GQL replay, and {len(assets.paths)} playback assets in {target}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("site", type=Path, help="the directory containing the built atlas")
    parser.add_argument("source_base", help="the GitHub blob URL for the deployed commit")
    args = parser.parse_args()
    package(args.site, args.source_base.rstrip("/") + "/")
