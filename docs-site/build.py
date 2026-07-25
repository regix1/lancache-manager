#!/usr/bin/env python3
"""
Assemble the MkDocs documentation site.

The per-topic pages in docs-site/content/ are the source of truth for the long
form documentation. The READMEs are deliberately short landing pages and are
not part of this build.

This script stages content into a generated tree and writes the MkDocs config
so the nav stays in step with docs-site/nav.yml:

    python docs-site/build.py
    mkdocs build --strict -f docs-site/generated/mkdocs.yml

Everything it writes lives under docs-site/generated/, which is gitignored.
"""

from __future__ import annotations

import shutil
import sys
from pathlib import Path

import yaml

REPO = Path(__file__).resolve().parent.parent
SITE = REPO / "docs-site"
CONTENT = SITE / "content"
NAV = SITE / "nav.yml"
BASE_CONFIG = SITE / "mkdocs.base.yml"
IMAGES = REPO / "docs" / "images"
OUT = SITE / "generated"
DOCS = OUT / "docs"

LANGUAGES = ["en", "zh"]
DEFAULT_LANG = "en"


def fail(message: str) -> int:
    print(f"error: {message}", file=sys.stderr)
    return 1


def build() -> int:
    for required in (NAV, BASE_CONFIG, CONTENT):
        if not required.exists():
            return fail(f"missing {required.relative_to(REPO)}")

    entries = yaml.safe_load(NAV.read_text(encoding="utf-8"))
    if not entries:
        return fail("nav.yml is empty")

    # Every page must exist in every language, or the switcher lands on a 404.
    missing = []
    for entry in entries:
        for lang in LANGUAGES:
            page = CONTENT / f"{entry['file']}.{lang}.md"
            if not page.exists():
                missing.append(str(page.relative_to(REPO)))
    if missing:
        return fail("missing content files:\n  " + "\n  ".join(missing))

    # Content files present but absent from nav would be built yet unreachable.
    listed = {f"{e['file']}.{lang}.md" for e in entries for lang in LANGUAGES}
    orphans = sorted(p.name for p in CONTENT.glob("*.md") if p.name not in listed)
    if orphans:
        return fail(
            "content files not listed in nav.yml:\n  " + "\n  ".join(orphans)
        )

    if DOCS.exists():
        shutil.rmtree(DOCS)
    DOCS.mkdir(parents=True, exist_ok=True)

    for page in CONTENT.glob("*.md"):
        shutil.copy2(page, DOCS / page.name)

    if IMAGES.is_dir():
        shutil.copytree(IMAGES, DOCS / "images", dirs_exist_ok=True)

    config = yaml.safe_load(BASE_CONFIG.read_text(encoding="utf-8"))
    config["nav"] = [{e[DEFAULT_LANG]: f"{e['file']}.md"} for e in entries]

    # The i18n plugin translates nav labels per locale rather than per file.
    for language in config["plugins"]:
        if not isinstance(language, dict) or "i18n" not in language:
            continue
        for locale in language["i18n"]["languages"]:
            code = locale["locale"]
            if code == DEFAULT_LANG:
                continue
            locale["nav_translations"] = {
                e[DEFAULT_LANG]: e[code] for e in entries if code in e
            }

    (OUT / "mkdocs.yml").write_text(
        yaml.safe_dump(config, sort_keys=False, allow_unicode=True),
        encoding="utf-8",
    )

    print(f"staged {len(entries)} pages x {len(LANGUAGES)} languages -> {DOCS}")
    return 0


if __name__ == "__main__":
    raise SystemExit(build())
