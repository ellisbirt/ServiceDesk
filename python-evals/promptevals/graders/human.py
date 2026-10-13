"""Human spot-check grading and judge calibration.

`export` writes a CSV (opens in Excel) with a stratified sample of graded
outputs and blank `human_pass` / `human_notes` columns. Fill them in (1/0, or
y/n), then `import` stores the labels and reports how often the automated
`pass` verdict agrees with the human one. Low agreement on clear-cut cases means
the judge prompt, rubric, or must/must_not items need work before their scores
can steer prompt changes.
"""

from __future__ import annotations

import csv
import json
import random
from collections import defaultdict
from pathlib import Path

from ..config import Config
from ..runner import read_jsonl

FIELDS = ["prompt_id", "rep", "tags", "prompt", "output", "auto_pass", "auto_explanation", "human_pass", "human_notes"]


def export_sample(config: Config, variant: str, out: Path, n: int | None = None, seed: int = 0) -> int:
    rows = [r for r in read_jsonl(config.flow_dir / variant / "results.jsonl") if r.get("status") == "ok"]
    if n is not None and n < len(rows):
        by_tag: dict[str, list[dict]] = defaultdict(list)
        for r in rows:
            by_tag[(r.get("tags") or ["untagged"])[0]].append(r)
        rng = random.Random(seed)
        for group in by_tag.values():
            rng.shuffle(group)
        sample: list[dict] = []
        while len(sample) < n:  # round-robin across tags so every tag is represented
            for group in by_tag.values():
                if group and len(sample) < n:
                    sample.append(group.pop())
        rows = sample
    with out.open("w", newline="", encoding="utf-8-sig") as f:
        w = csv.DictWriter(f, fieldnames=FIELDS)
        w.writeheader()
        for r in sorted(rows, key=lambda r: (r["prompt_id"], r.get("rep", 0))):
            expl = r.get("explanation") or {}
            w.writerow({
                "prompt_id": r["prompt_id"],
                "rep": r.get("rep", 0),
                "tags": ",".join(r.get("tags") or []),
                "prompt": r.get("prompt", ""),
                "output": r.get("output", ""),
                "auto_pass": (r.get("grade") or {}).get("pass"),
                "auto_explanation": "\n\n".join(f"[{k}]\n{v}" for k, v in expl.items() if v),
                "human_pass": "",
                "human_notes": "",
            })
    return len(rows)


def _parse_bool(s: str) -> float | None:
    s = (s or "").strip().lower()
    if s in {"1", "y", "yes", "pass", "true", "t"}:
        return 1.0
    if s in {"0", "n", "no", "fail", "false", "f"}:
        return 0.0
    return None


def import_labels(config: Config, variant: str, csv_path: Path) -> str:
    labels = []
    with csv_path.open(encoding="utf-8-sig", newline="") as f:
        for row in csv.DictReader(f):
            human = _parse_bool(row.get("human_pass", ""))
            if human is None:
                continue
            auto = row.get("auto_pass")
            labels.append({
                "prompt_id": row["prompt_id"],
                "rep": int(row.get("rep") or 0),
                "human_pass": human,
                "auto_pass": float(auto) if auto not in (None, "", "None") else None,
                "notes": row.get("human_notes", ""),
            })
    out = config.flow_dir / variant / "human.jsonl"
    existing = {(r["prompt_id"], r["rep"]): r for r in read_jsonl(out)}
    existing.update({(r["prompt_id"], r["rep"]): r for r in labels})
    out.write_text("".join(json.dumps(r, ensure_ascii=False) + "\n" for r in existing.values()), encoding="utf-8")

    comparable = [r for r in existing.values() if r["auto_pass"] is not None]
    if not comparable:
        return f"Stored {len(labels)} labels; none had an automated verdict to compare against."
    agree = sum(1 for r in comparable if r["auto_pass"] == r["human_pass"])
    fp = [r["prompt_id"] for r in comparable if r["auto_pass"] == 1.0 and r["human_pass"] == 0.0]
    fn = [r["prompt_id"] for r in comparable if r["auto_pass"] == 0.0 and r["human_pass"] == 1.0]
    human_rate = sum(r["human_pass"] for r in existing.values()) / len(existing)
    return (
        f"Stored {len(labels)} labels ({len(existing)} total) in {out}.\n"
        f"Human pass rate: {100 * human_rate:.0f}%\n"
        f"Auto vs human agreement: {agree}/{len(comparable)} ({100 * agree / len(comparable):.0f}%)\n"
        f"Auto passed, human failed (grader too lenient): {', '.join(fp) or '-'}\n"
        f"Auto failed, human passed (grader too strict):  {', '.join(fn) or '-'}"
    )
