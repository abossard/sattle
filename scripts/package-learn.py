"""Add the two learning guides to an existing Pages atlas without rebuilding the films."""

import argparse
import html
from html.parser import HTMLParser
from pathlib import Path
import re
import shutil
import subprocess
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

    # Add the guide to the hosted atlas without changing its standalone download.
    home = site / "index.html"
    contents = home.read_text()
    entry = '<a href="docs/learn/">Retail walkthrough</a>'
    if entry not in contents:
        navigation = '<nav aria-label="Main navigation">'
        if navigation not in contents:
            raise ValueError("Missing atlas home navigation")
        home.write_text(contents.replace(navigation, navigation + entry, 1))
    print(f"Packaged both guides and {len(assets.paths)} playback assets in {target}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("site", type=Path, help="the directory containing the built atlas")
    parser.add_argument("source_base", help="the GitHub blob URL for the deployed commit")
    args = parser.parse_args()
    package(args.site, args.source_base.rstrip("/") + "/")
