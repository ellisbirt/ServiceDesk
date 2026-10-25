"""Export the shipped prompts from ``Prompts.cs`` so the evals track what the app really runs.

The C# constants stay the single source of truth. This writes them to ``evals/app-prompts.md``, which the app eval
config (``evals.app.toml``) uses as its "system prompt". The app does not read that file; it exists so that:

* each variant snapshots the real prompt text, and ``promptevals`` refuses to re-run a variant after the prompt changed;
* ``change.patch`` shows exactly which prompt text a variant changed.

Run from ``python-evals/``::

    python -m service_desk_evals.prompts_export          # rewrite evals/app-prompts.md
    python -m service_desk_evals.prompts_export --check  # exit 1 if it is out of date

A pytest guards the same thing, so editing ``Prompts.cs`` without re-exporting fails the offline tests.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PROMPTS_CS = ROOT / "helpdesk" / "backend" / "src" / "Helpdesk.Infrastructure" / "Ai" / "Prompts.cs"
OUTPUT = ROOT / "evals" / "app-prompts.md"

HEADER = (
    "<!-- Generated from helpdesk/backend/src/Helpdesk.Infrastructure/Ai/Prompts.cs by "
    "python-evals/service_desk_evals/prompts_export.py. Do not edit: change Prompts.cs, then re-run the export. -->\n"
)

_NAME = re.compile(r"^\s*public const string (\w+) =\s*(.*)$")


class ExportError(ValueError):
    pass


def _unescape(literal: str, name: str) -> str:
    """Decode a plain C# string literal. Only ``\\"`` and ``\\\\`` are supported; anything else fails loudly."""
    body = literal.replace('\\"', "\x00").replace("\\\\", "\x01")
    if "\\" in body:
        raise ExportError(f"{name}: unsupported escape in a plain string literal; use a raw string or extend the exporter")
    return body.replace("\x00", '"').replace("\x01", "\\")


def parse_prompts(source: str) -> dict[str, str]:
    """Return ``{constant name: text}`` for every ``public const string`` in ``Prompts.cs``, in file order."""
    lines = source.splitlines()
    found: dict[str, str] = {}
    i = 0
    while i < len(lines):
        m = _NAME.match(lines[i])
        if not m:
            i += 1
            continue
        name, rest = m.group(1), m.group(2).strip()
        if not rest:  # the literal starts on the next line
            i += 1
            rest = lines[i].strip()
        if rest == '"""':
            body: list[str] = []
            i += 1
            while i < len(lines) and not re.fullmatch(r'\s*""";', lines[i]):
                body.append(lines[i])
                i += 1
            if i >= len(lines):
                raise ExportError(f"{name}: unterminated raw string literal")
            margin = len(lines[i]) - len(lines[i].lstrip())
            text = "\n".join(line[margin:] if line.strip() else "" for line in body)
        elif rest.startswith('"') and rest.endswith('";') and len(rest) >= 3:
            text = _unescape(rest[1:-2], name)
        else:
            raise ExportError(f"{name}: unsupported literal form: {rest[:40]!r}")
        if name in found:
            raise ExportError(f"duplicate constant {name}")
        found[name] = text
        i += 1
    if not found:
        raise ExportError("no prompt constants found; has Prompts.cs moved?")
    return found


def render(prompts: dict[str, str]) -> str:
    parts = [HEADER]
    for name, text in prompts.items():
        parts.append(f"\n## {name}\n\n````text\n{text}\n````\n")
    return "".join(parts)


def export(source_path: Path = PROMPTS_CS) -> str:
    return render(parse_prompts(source_path.read_text(encoding="utf-8")))


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--check", action="store_true", help="exit 1 if evals/app-prompts.md is not up to date")
    args = ap.parse_args(argv)
    text = export()
    current = OUTPUT.read_text(encoding="utf-8").replace("\r\n", "\n") if OUTPUT.exists() else None
    if args.check:
        if current != text:
            print(f"{OUTPUT.relative_to(ROOT)} is out of date: run `python -m service_desk_evals.prompts_export`", file=sys.stderr)
            return 1
        print("app-prompts.md is up to date")
        return 0
    OUTPUT.write_text(text, encoding="utf-8", newline="\n")
    print(f"wrote {OUTPUT.relative_to(ROOT)} ({len(text.splitlines())} lines)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
