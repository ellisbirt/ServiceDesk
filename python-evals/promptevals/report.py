"""Summaries, variant comparison, and the HTML report."""

from __future__ import annotations

import json
import shutil
import subprocess
from collections import defaultdict
from pathlib import Path
from statistics import median
from typing import Any

from .config import Config
from .runner import VARIANT_RE, read_jsonl
from .stats import cost_usd, mean_ci, paired_diff, wilson

LITE_BUILDER = Path(__file__).parent / "report" / "build-report-lite.mjs"


def list_variants(config: Config) -> list[str]:
    if not config.flow_dir.exists():
        return []
    names = [p.name for p in config.flow_dir.iterdir() if p.is_dir() and VARIANT_RE.match(p.name)]
    return sorted(names, key=lambda n: -1 if n == "baseline" else int(n[1:]))


def _metrics(config: Config) -> list[dict[str, Any]]:
    path = config.flow_dir / "_state.json"
    if path.exists():
        return json.loads(path.read_text(encoding="utf-8")).get("metrics", [])
    return [{"id": "pass", "kind": "binary"}]


def per_case_scores(rows: list[dict], metric: str) -> dict[str, float]:
    """Mean over status-ok reps, per case. Cases with no score for the metric are omitted."""
    buckets: dict[str, list[float]] = defaultdict(list)
    for r in rows:
        v = (r.get("grade") or {}).get(metric)
        if r.get("status") == "ok" and v is not None:
            buckets[r["prompt_id"]].append(float(v))
    return {k: sum(v) / len(v) for k, v in buckets.items()}


def _fmt_metric(m: dict, rows: list[dict]) -> str:
    scores = per_case_scores(rows, m["id"])
    if not scores:
        return "-"
    vals = list(scores.values())
    single_rep = all(len([r for r in rows if r["prompt_id"] == k and r.get("status") == "ok"]) == 1 for k in scores)
    if m.get("kind") == "binary" and single_rep:
        lo, hi = wilson(sum(vals), len(vals))
        return f"{100 * sum(vals) / len(vals):.0f}% [{100 * lo:.0f}-{100 * hi:.0f}] (n={len(vals)})"
    mu, lo, hi = mean_ci(vals)
    top = m.get("scale", 1 if m.get("kind") == "binary" else 10)
    lo, hi = max(0.0, lo), min(float(top), hi)
    if m.get("kind") == "binary":
        return f"{100 * mu:.0f}% [{100 * lo:.0f}-{100 * hi:.0f}] (n={len(vals)})"
    return f"{mu:.2f} [{lo:.2f}-{hi:.2f}] (n={len(vals)})"


def _variant_stats(config: Config, v: str) -> dict[str, Any]:
    vdir = config.flow_dir / v
    rows = read_jsonl(vdir / "results.jsonl")
    errors = read_jsonl(vdir / "errors.jsonl")
    ok_keys = {(r["prompt_id"], r.get("rep", 0)) for r in rows}
    open_errors = [e for e in errors if (e["prompt_id"], e.get("rep", 0)) not in ok_keys]
    target_cost = [cost_usd(r.get("model"), r.get("usage")) for r in rows]
    judge_cost = [cost_usd(r.get("judge_model"), r.get("judge_usage")) for r in rows]
    err_cost = [cost_usd(e.get("model"), e.get("usage")) for e in errors]
    lat = [r["latency_s"] for r in rows if r.get("latency_s")]
    return {
        "rows": rows,
        "errors": open_errors,
        "truncated": sum(1 for r in rows if r.get("status") == "truncated"),
        "models": sorted({r.get("model") for r in rows if r.get("model")}),
        "target_cost": sum(c for c in target_cost if c),
        "judge_cost": sum(c for c in judge_cost if c),
        "error_cost": sum(c for c in err_cost if c),
        "unpriced": any(c is None for c in target_cost if rows),
        "latency_p50": median(lat) if lat else None,
        "out_tokens": median([r["usage"].get("output_tokens", 0) for r in rows if r.get("usage")] or [0]),
    }


def summarize(config: Config, variants: list[str] | None = None, show_failures: bool = True) -> str:
    variants = variants or list_variants(config)
    if not variants:
        return "No runs yet. Try: promptevals run --variant baseline"
    metrics = _metrics(config)
    out = [f"# Eval summary - {config.flow}", ""]
    header = ["variant", "cases"] + [m.get("label", m["id"]) for m in metrics] + [
        "errors", "trunc", "p50 latency", "median out tok", "cost (target + judge)"]
    out.append("| " + " | ".join(header) + " |")
    out.append("|" + "---|" * len(header))
    stats_by_v = {}
    for v in variants:
        s = _variant_stats(config, v)
        stats_by_v[v] = s
        n_cases = len({r["prompt_id"] for r in s["rows"]})
        cells = [v, str(n_cases)] + [_fmt_metric(m, s["rows"]) for m in metrics]
        cost = f"${s['target_cost']:.3f} + ${s['judge_cost']:.3f}"
        if s["error_cost"]:
            cost += f" (+${s['error_cost']:.3f} failed)"
        if s["unpriced"]:
            cost += " (some models unpriced)"
        cells += [
            str(len(s["errors"])),
            str(s["truncated"]),
            f"{s['latency_p50']:.1f}s" if s["latency_p50"] else "-",
            f"{s['out_tokens']:.0f}",
            cost,
        ]
        out.append("| " + " | ".join(cells) + " |")
    out.append("")
    out.append("Intervals are 95% CIs over cases. Errors (timeouts, API failures, grader crashes) "
               "and truncated responses are excluded from the means - check them separately.")

    # Per-tag breakdown of the headline metric for the latest variant.
    latest = variants[-1]
    rows = stats_by_v[latest]["rows"]
    headline = (next((m for m in metrics if m.get("kind") == "binary"), metrics[0]))["id"]
    by_tag: dict[str, list[str]] = defaultdict(list)
    for r in rows:
        by_tag[(r.get("tags") or ["untagged"])[0]].append(r["prompt_id"])
    scores = per_case_scores(rows, headline)
    if len(by_tag) > 1:
        out += ["", f"## `{headline}` by tag ({latest})", "", "| tag | cases | score |", "|---|---|---|"]
        for tag, ids in sorted(by_tag.items()):
            vals = [scores[i] for i in set(ids) if i in scores]
            out.append(f"| {tag} | {len(vals)} | {100 * sum(vals) / len(vals):.0f}% |" if vals else f"| {tag} | 0 | - |")

    if show_failures:
        fails = [r for r in rows if r.get("status") == "ok" and (r.get("grade") or {}).get("pass") == 0.0]
        if fails:
            out += ["", f"## Failures ({latest})", ""]
            for r in sorted(fails, key=lambda r: (r["prompt_id"], r.get("rep", 0))):
                expl = r.get("explanation") or {}
                reasons = [expl.get("pass", "")]
                for k in ("checks", "must", "rubric", "win"):
                    if k in expl and expl[k]:
                        reasons.append("\n".join(
                            line for line in expl[k].splitlines() if line.startswith("FAIL") or k in ("rubric", "win")
                        ))
                body = "\n".join(x for x in reasons if x).strip()
                out.append(f"- **{r['prompt_id']}** rep{r.get('rep', 0)} (trace: `{r.get('trace')}`)")
                out += [f"    {line}" for line in body.splitlines()]
        errs = stats_by_v[latest]["errors"]
        if errs:
            out += ["", f"## Unscored attempts ({latest})", ""]
            for e in errs:
                out.append(f"- {e['prompt_id']} rep{e.get('rep', 0)}: {e['class']} - {str(e.get('detail'))[:200]}")
    return "\n".join(out) + "\n"


def compare(config: Config, a: str, b: str) -> str:
    metrics = _metrics(config)
    ra = read_jsonl(config.flow_dir / a / "results.jsonl")
    rb = read_jsonl(config.flow_dir / b / "results.jsonl")
    out = [f"# {b} vs {a}", "", "| metric | " + a + " | " + b + " | diff (paired, 95% CI) | n |", "|---|---|---|---|---|"]
    for m in metrics:
        sa, sb = per_case_scores(ra, m["id"]), per_case_scores(rb, m["id"])
        common = sorted(set(sa) & set(sb))
        if not common:
            continue
        xa, xb = [sa[k] for k in common], [sb[k] for k in common]
        d, lo, hi = paired_diff(xa, xb)
        scale = 100 if m.get("kind") == "binary" else 1
        unit = " pts" if scale == 100 else ""
        better = m.get("better", "higher")
        sig = ""
        if lo > 0 or hi < 0:
            improved = (d > 0) == (better == "higher")
            sig = " **better**" if improved else " **worse**"
        out.append(
            f"| {m.get('label', m['id'])} | {scale * sum(xa) / len(xa):.2f} | {scale * sum(xb) / len(xb):.2f} | "
            f"{scale * d:+.2f}{unit} [{scale * lo:+.2f}, {scale * hi:+.2f}]{sig} | {len(common)} |"
        )
    pa, pb = per_case_scores(ra, "pass"), per_case_scores(rb, "pass")
    fixed = sorted(k for k in set(pa) & set(pb) if pb[k] > pa[k])
    broke = sorted(k for k in set(pa) & set(pb) if pb[k] < pa[k])
    out += ["", f"Improved ({len(fixed)}): {', '.join(fixed) or '-'}",
            f"Regressed ({len(broke)}): {', '.join(broke) or '-'}", "",
            "A diff is marked better/worse only when its 95% CI excludes zero."]
    return "\n".join(out) + "\n"


def build_html(config: Config) -> Path | None:
    """Render runs/<flow>/report.html with the bundled lite report builder (needs node)."""
    node = shutil.which("node") or shutil.which("bun")
    if not node or not LITE_BUILDER.exists():
        return None
    _write_cost_fields(config)
    subprocess.run([node, str(LITE_BUILDER), str(config.flow_dir)], check=True, capture_output=True, text=True)
    html = config.flow_dir / "report.html"
    return html if html.exists() else None


def _write_cost_fields(config: Config) -> None:
    """Add derived perf fields (cost_usd, in/out tokens) to each row for the report columns."""
    for v in list_variants(config):
        path = config.flow_dir / v / "results.jsonl"
        rows = read_jsonl(path)
        changed = False
        for r in rows:
            usage = r.get("usage") or {}
            t = cost_usd(r.get("model"), usage)
            j = cost_usd(r.get("judge_model"), r.get("judge_usage"))
            fields = {
                "cost_usd": round((t or 0) + (j or 0), 6) if t is not None else None,
                "in_tokens": usage.get("input_tokens", 0) + usage.get("cache_read_input_tokens", 0)
                + usage.get("cache_creation_input_tokens", 0),
                "out_tokens": usage.get("output_tokens", 0),
            }
            if any(r.get(k) != val for k, val in fields.items()):
                r.update(fields)
                changed = True
        if changed:
            tmp = path.with_suffix(".jsonl.tmp")
            tmp.write_text("".join(json.dumps(r, ensure_ascii=False) + "\n" for r in rows), encoding="utf-8")
            tmp.replace(path)
