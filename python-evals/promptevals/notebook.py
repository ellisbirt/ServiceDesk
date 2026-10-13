"""Helpers for exploring eval runs in Jupyter (needs pandas; plotting needs matplotlib)."""

from __future__ import annotations

import json
from typing import Any

from .cases import load_cases
from .config import Config
from .report import _metrics, list_variants, per_case_scores
from .runner import read_jsonl
from .stats import mean_ci, wilson

# Single-series chart colours (reference data-viz palette, light surface).
SERIES = "#2a78d6"
SURFACE = "#fcfcfb"
TEXT = "#0b0b0b"
MUTED = "#52514e"
GRID = "#e4e3df"


def cases_frame(config: Config):
    import pandas as pd

    rows = []
    for c in load_cases(config.cases_path):
        rows.append({
            "id": c.id,
            "tag": (c.tags or ["untagged"])[0],
            "input": c.prompt_text(),
            "turns": 1 if isinstance(c.input, str) else len(c.input),
            "must": len(c.must),
            "must_not": len(c.must_not),
            "checks": len(c.checks),
        })
    return pd.DataFrame(rows).set_index("id")


def results_frame(config: Config, variant: str):
    """One row per (case, rep) with every metric as a column."""
    import pandas as pd

    metric_ids = [m["id"] for m in _metrics(config)]
    rows = []
    for r in read_jsonl(config.flow_dir / variant / "results.jsonl"):
        grade = r.get("grade") or {}
        rows.append({
            "case": r["prompt_id"],
            "rep": r.get("rep", 0),
            "tag": (r.get("tags") or ["untagged"])[0],
            "status": r.get("status"),
            **{m: grade.get(m) for m in metric_ids},
            "latency_s": r.get("latency_s"),
            "out_tokens": (r.get("usage") or {}).get("output_tokens"),
            "output": r.get("output", ""),
        })
    df = pd.DataFrame(rows)
    return df.sort_values(["case", "rep"]).set_index(["case", "rep"]) if not df.empty else df


def errors_frame(config: Config, variant: str):
    import pandas as pd

    return pd.DataFrame(read_jsonl(config.flow_dir / variant / "errors.jsonl"))


def side_by_side(config: Config, a: str, b: str, metric: str = "pass"):
    """Per-case scores for two variants, regressions first."""
    import pandas as pd

    sa = per_case_scores(read_jsonl(config.flow_dir / a / "results.jsonl"), metric)
    sb = per_case_scores(read_jsonl(config.flow_dir / b / "results.jsonl"), metric)
    df = pd.DataFrame({a: pd.Series(sa), b: pd.Series(sb)})
    df["change"] = df[b] - df[a]
    return df.sort_values("change")


def case_markdown(config: Config, variant: str, case_id: str, rep: int = 0) -> str:
    """Input, response, and every grader's explanation for one case, as markdown."""
    row = next((r for r in read_jsonl(config.flow_dir / variant / "results.jsonl")
                if r["prompt_id"] == case_id and r.get("rep", 0) == rep), None)
    if row is None:
        return f"No result for `{case_id}` rep {rep} in `{variant}`."
    grade = {k: v for k, v in (row.get("grade") or {}).items() if v is not None}
    parts = [f"### {case_id} - {variant} rep {rep}", "",
             f"**Status:** {row.get('status')} &nbsp; **Grades:** `{json.dumps(grade)}`", "",
             "**Input**", "", *[f"> {line}" for line in (row.get("prompt") or "").splitlines()], "",
             "**Response**", "", *[f"> {line}" for line in (row.get("output") or "(empty)").splitlines()], ""]
    for name, text in (row.get("explanation") or {}).items():
        if text and not (name == "must_not_ok" and text == row["explanation"].get("must")):
            parts += [f"**{name}**", "", "```", text, "```", ""]
    parts.append(f"Full transcript: `{config.flow_dir / row['trace']}`")
    return "\n".join(parts)


def variant_scores(config: Config, metric: str = "pass") -> list[dict[str, Any]]:
    decl = next((m for m in _metrics(config) if m["id"] == metric), {"id": metric, "kind": "float"})
    out = []
    for v in list_variants(config):
        vals = list(per_case_scores(read_jsonl(config.flow_dir / v / "results.jsonl"), metric).values())
        if not vals:
            continue
        if decl.get("kind") == "binary":
            mu = sum(vals) / len(vals)
            lo, hi = wilson(sum(vals), len(vals))
        else:
            mu, lo, hi = mean_ci(vals)
        top = decl.get("scale", 1 if decl.get("kind") == "binary" else 10)
        out.append({"variant": v, "mean": mu, "lo": max(0.0, lo), "hi": min(float(top), hi),
                    "n": len(vals), "scale": top, "binary": decl.get("kind") == "binary",
                    "label": decl.get("label", metric)})
    return out


def plot_variants(config: Config, metric: str = "pass"):
    """Dot + 95% CI per variant for one metric (single series, so no legend)."""
    import matplotlib.pyplot as plt

    data = variant_scores(config, metric)
    if not data:
        print(f"No scores for {metric!r} yet.")
        return None
    pct = 100 if data[0]["binary"] else 1
    fig, ax = plt.subplots(figsize=(7, 0.55 * len(data) + 1.2), facecolor=SURFACE)
    ax.set_facecolor(SURFACE)
    ys = list(range(len(data)))[::-1]
    for y, d in zip(ys, data):
        ax.plot([d["lo"] * pct, d["hi"] * pct], [y, y], color=SERIES, linewidth=2, solid_capstyle="round")
        ax.plot(d["mean"] * pct, y, "o", color=SERIES, markersize=9, markeredgecolor=SURFACE, markeredgewidth=2)
        value = f"{d['mean'] * pct:.0f}%" if pct == 100 else f"{d['mean']:.2f}"
        ax.annotate(f"{value}  (n={d['n']})", (d["hi"] * pct, y), xytext=(8, 0), textcoords="offset points",
                    va="center", color=MUTED, fontsize=9)
    ax.set_yticks(ys, [d["variant"] for d in data], color=TEXT)
    ax.set_xlim(0, d["scale"] * pct * 1.18)
    ax.set_ylim(-0.6, len(data) - 0.4)
    unit = " (%)" if pct == 100 else f" (0-{d['scale']:g})"
    ax.set_xlabel(f"{data[0]['label']}{unit} - dot = mean, line = 95% CI", color=MUTED, fontsize=9)
    ax.tick_params(colors=MUTED, length=0)
    ax.grid(axis="x", color=GRID, linewidth=0.8)
    ax.set_axisbelow(True)
    for side in ("top", "right", "left"):
        ax.spines[side].set_visible(False)
    ax.spines["bottom"].set_color(GRID)
    ax.set_title(f"{data[0]['label']} by variant", loc="left", color=TEXT, fontsize=11)
    fig.tight_layout()
    return fig
