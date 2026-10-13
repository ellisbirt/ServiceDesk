"""Run a system-prompt variant over the case set and grade every output.

Output layout (compatible with the Claude eval report builder):

    runs/<flow>/_state.json                 metric declarations
    runs/<flow>/<variant>/results.jsonl     one row per (case, rep), appended as each completes
    runs/<flow>/<variant>/errors.jsonl      attempts that never produced a scorable output
    runs/<flow>/<variant>/traces/<id>_rep<k>.json
    runs/<flow>/<variant>/system-prompt.md  snapshot of the prompt that produced this variant
    runs/<flow>/<variant>/change.md|.patch  (non-baseline) what changed vs baseline
"""

from __future__ import annotations

import asyncio
import difflib
import json
import re
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from . import llm
from .cases import Case, load_cases
from .config import Config
from .graders import GradeContext, Grader, build_graders, metric_declarations
from .llm import UsageMeter
from .target import Target, TargetResult

VARIANT_RE = re.compile(r"^(baseline|v\d+)$")


class GraderFailure(Exception):
    def __init__(self, target: TargetResult, cause: Exception):
        super().__init__(repr(cause))
        self.target = target
        self.cause = cause


@dataclass
class RunSummary:
    variant: str
    completed: int = 0
    skipped: int = 0
    errors: int = 0
    wall_s: float = 0.0
    error_classes: dict[str, int] = field(default_factory=dict)


# ---------------------------------------------------------------- file helpers


def read_jsonl(path: Path) -> list[dict[str, Any]]:
    if not path.exists():
        return []
    rows = []
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line:
            rows.append(json.loads(line))
    return rows


def _write_json(path: Path, data: Any) -> None:
    path.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def update_state(config: Config, metrics: list[dict]) -> None:
    """Merge metric declarations into _state.json, keeping existing order first."""
    path = config.flow_dir / "_state.json"
    state = json.loads(path.read_text(encoding="utf-8")) if path.exists() else {}
    existing = state.get("metrics", [])
    ids = {m["id"] for m in existing}
    merged = existing + [m for m in metrics if m["id"] not in ids]
    # keep `pass` first so it stays the headline metric
    merged.sort(key=lambda m: 0 if m["id"] == "pass" else 1)
    state["metrics"] = merged
    state.setdefault(
        "perf_fields",
        [
            {"id": "cost_usd", "label": "Cost", "unit": "$"},
            {"id": "in_tokens", "label": "In tok"},
            {"id": "out_tokens", "label": "Out tok"},
            {"id": "latency_s", "label": "Latency", "unit": "s"},
        ],
    )
    config.flow_dir.mkdir(parents=True, exist_ok=True)
    _write_json(path, state)


def _snapshot_prompt(config: Config, vdir: Path, variant: str, prompt: str, force: bool, note: str | None) -> None:
    snap = vdir / "system-prompt.md"
    has_results = (vdir / "results.jsonl").exists()
    if snap.exists() and has_results and snap.read_text(encoding="utf-8").strip() != prompt.strip() and not force:
        raise SystemExit(
            f"{vdir.name}/ was produced by a different system prompt than the current one.\n"
            f"Run under a new variant name (e.g. the next v<N>), or pass --force to discard "
            f"its results and start over."
        )
    if snap.exists() and has_results and force and snap.read_text(encoding="utf-8").strip() != prompt.strip():
        for name in ("results.jsonl", "errors.jsonl"):
            (vdir / name).unlink(missing_ok=True)
        for t in (vdir / "traces").glob("*.json"):
            t.unlink()
    snap.write_text(prompt + "\n", encoding="utf-8")

    base_snap = config.flow_dir / "baseline" / "system-prompt.md"
    if variant != "baseline" and base_snap.exists():
        before = base_snap.read_text(encoding="utf-8")
        (vdir / "system-prompt.before.md").write_text(before, encoding="utf-8")
        diff = difflib.unified_diff(
            before.splitlines(keepends=True),
            (prompt + "\n").splitlines(keepends=True),
            fromfile=f"a/{config.system_prompt_path.name}",
            tofile=f"b/{config.system_prompt_path.name}",
        )
        (vdir / "change.patch").write_text("".join(diff), encoding="utf-8")
        change_md = vdir / "change.md"
        if note or not change_md.exists():
            change_md.write_text(
                # first line is shown as the variant's description in report.html
                f"{note or 'System prompt revision (add a one-line rationale with --note).'}\n",
                encoding="utf-8",
            )
        _write_json(vdir / "summary.json", {"target": "system_prompt", "description": note or ""})


def _trace(system_prompt: str, case: Case, result: TargetResult) -> list[dict[str, Any]]:
    if result.trace:
        return result.trace
    turns: list[dict[str, Any]] = [{"role": "system", "content": system_prompt}]
    turns += [{"role": m["role"], "content": m["content"]} for m in case.messages()]
    final: dict[str, Any] = {"role": "assistant", "content": result.output}
    if result.thinking:
        final["thinking"] = result.thinking
    turns.append(final)
    return turns


# ---------------------------------------------------------------- grading


async def grade_output(
    *,
    config: Config,
    client: Any,
    graders: list[Grader],
    case: Case,
    output: str,
    stop_reason: str | None,
    system_prompt: str,
    rubric_text: str,
    variant: str,
    rep: int,
    reference_output: str | None,
) -> tuple[dict[str, float | None], dict[str, str], UsageMeter]:
    meter = UsageMeter()
    metric_ids = [m["id"] for m in metric_declarations(graders)]
    grade: dict[str, float | None] = {m: None for m in metric_ids}
    explanation: dict[str, str] = {}

    if stop_reason == "refusal":
        grade.update({"pass": 0.0, "refusal": 1.0})
        explanation["pass"] = "model refused (stop_reason=refusal)"
        return grade, explanation, meter

    ctx = GradeContext(
        case=case,
        output=output,
        system_prompt=system_prompt,
        rubric_text=rubric_text,
        config=config,
        client=client,
        rep=rep,
        variant=variant,
        reference_output=reference_output,
        judge_meter=meter,
    )
    applicable = [g for g in graders if g.applies_to(case)]
    results = await asyncio.gather(*(g.grade(ctx) for g in applicable))
    opinions = []
    for g, r in zip(applicable, results):
        grade.update(r.scores)
        explanation.update(r.explanations)
        if r.passed is not None:
            opinions.append((g.name, r.passed))
    grade["refusal"] = 0.0
    if opinions:
        grade["pass"] = float(all(p for _, p in opinions))
        failed = [n for n, p in opinions if not p]
        explanation["pass"] = "failed: " + ", ".join(failed) if failed else "all graders passed"
    return grade, explanation, meter


# ---------------------------------------------------------------- runner


def load_reference_outputs(config: Config, variant: str) -> dict[tuple[str, int], str]:
    rows = read_jsonl(config.flow_dir / variant / "results.jsonl")
    return {(r["prompt_id"], r.get("rep", 0)): r.get("output", "") for r in rows if r.get("status") == "ok"}


def _reference_for(refs: dict[tuple[str, int], str], case_id: str, rep: int) -> str | None:
    return refs.get((case_id, rep), refs.get((case_id, 0)))


def select_cases(cases: list[Case], ids: list[str] | None, tags: list[str] | None) -> list[Case]:
    if ids:
        unknown = set(ids) - {c.id for c in cases}
        if unknown:
            raise SystemExit(f"unknown case ids: {sorted(unknown)}")
        cases = [c for c in cases if c.id in ids]
    if tags:
        cases = [c for c in cases if set(tags) & set(c.tags)]
    return cases


async def run_variant(
    config: Config,
    variant: str,
    client: Any,
    *,
    ids: list[str] | None = None,
    tags: list[str] | None = None,
    reps: int | None = None,
    force: bool = False,
    note: str | None = None,
    system_prompt: str | None = None,
    quiet: bool = False,
) -> RunSummary:
    if not VARIANT_RE.match(variant):
        raise SystemExit(f"variant must be 'baseline' or 'v<N>' (got {variant!r}); describe it with --note")
    reps = reps or config.run.reps
    vdir = config.flow_dir / variant
    (vdir / "traces").mkdir(parents=True, exist_ok=True)

    prompt = system_prompt if system_prompt is not None else config.read_system_prompt()
    rubric_text = config.read_rubric()
    _snapshot_prompt(config, vdir, variant, prompt, force, note)

    graders = build_graders(config)
    update_state(config, metric_declarations(graders))
    target = Target(config, client)
    cases = select_cases(load_cases(config.cases_path), ids, tags)
    refs = (
        load_reference_outputs(config, config.grading.pairwise_reference)
        if any(g.name == "pairwise" for g in graders)
        else {}
    )

    results_path, errors_path = vdir / "results.jsonl", vdir / "errors.jsonl"
    done = {(r["prompt_id"], r.get("rep", 0)) for r in read_jsonl(results_path)}
    todo = [(c, k) for c in cases for k in range(reps) if (c.id, k) not in done]
    summary = RunSummary(variant=variant, skipped=len(cases) * reps - len(todo))
    if not quiet:
        print(f"[{variant}] {len(todo)} to run ({summary.skipped} already done), "
              f"{len(cases)} cases x {reps} reps, target={config.target.model}")

    sem = asyncio.Semaphore(config.run.concurrency)
    lock = asyncio.Lock()
    start = time.perf_counter()
    counter = {"n": 0}

    async def append(path: Path, row: dict[str, Any]) -> None:
        async with lock:
            with path.open("a", encoding="utf-8") as f:
                f.write(json.dumps(row, ensure_ascii=False) + "\n")

    async def execute(case: Case, rep: int) -> dict[str, Any]:
        result = await target.respond(prompt, case)
        status = "truncated" if result.stop_reason == "max_tokens" else "ok"
        try:
            if status == "ok":
                grade, explanation, meter = await grade_output(
                    config=config, client=client, graders=graders, case=case, output=result.output,
                    stop_reason=result.stop_reason, system_prompt=prompt, rubric_text=rubric_text,
                    variant=variant, rep=rep, reference_output=_reference_for(refs, case.id, rep),
                )
            else:
                grade, explanation, meter = {}, {"pass": "response hit max_tokens"}, UsageMeter()
        except Exception as e:  # noqa: BLE001
            raise GraderFailure(result, e) from e

        trace_rel = f"{variant}/traces/{case.id}_rep{rep}.json"
        _write_json(config.flow_dir / trace_rel, _trace(prompt, case, result))
        return {
            "prompt_id": case.id,
            "rep": rep,
            "prompt": case.prompt_text(),
            "tags": case.tags or ["untagged"],
            "status": status,
            "stop_reason": result.stop_reason,
            "output": result.output,
            "grade": grade,
            "explanation": explanation,
            "model": result.model,
            "usage": result.usage,
            "latency_s": result.latency_s,
            "judge_model": meter.model,
            "judge_usage": meter.usage or None,
            "trace": trace_rel,
            "meta": {
                "description": case.description,
                "attempts": result.attempts,
                "request_id": result.request_id,
                "served_by_fallback": result.served_by_fallback,
                "stop_details": result.stop_details,
            },
        }

    async def one(case: Case, rep: int) -> None:
        async with sem:
            err: dict[str, Any] | None = None
            try:
                row = await asyncio.wait_for(execute(case, rep), timeout=config.run.timeout_s)
            except asyncio.TimeoutError:
                err = {"class": "timeout", "detail": f"exceeded {config.run.timeout_s}s"}
            except llm.RetriesExhausted as e:
                err = {"class": "api_error", "detail": repr(e.last), "attempts": e.attempts}
            except llm.ServedModelMismatch as e:
                err = {"class": "served_model_mismatch", "detail": str(e)}
            except GraderFailure as e:
                err = {"class": "grader_error", "detail": repr(e.cause),
                       "model": e.target.model, "usage": e.target.usage}
            except Exception as e:  # noqa: BLE001 - recorded, never scored as a model failure
                err = {"class": "harness_error", "detail": repr(e)}

            counter["n"] += 1
            prefix = f"  [{counter['n']}/{len(todo)}] {case.id} rep{rep}"
            if err:
                summary.errors += 1
                summary.error_classes[err["class"]] = summary.error_classes.get(err["class"], 0) + 1
                await append(errors_path, {"prompt_id": case.id, "rep": rep, "ts": time.time(), **err})
                if not quiet:
                    print(f"{prefix} ERROR {err['class']}: {err['detail'][:160]}")
                return
            await append(results_path, row)
            summary.completed += 1
            if not quiet:
                shown = {k: v for k, v in row["grade"].items() if v is not None and k != "refusal"}
                verdict = {1.0: "PASS", 0.0: "FAIL"}.get(row["grade"].get("pass"), row["status"].upper())
                print(f"{prefix} {verdict} " + " ".join(f"{k}={v:g}" for k, v in shown.items() if k != "pass"))

    await asyncio.gather(*(one(c, k) for c, k in todo))
    summary.wall_s = round(time.perf_counter() - start, 1)
    return summary


async def regrade_variant(config: Config, variant: str, client: Any, quiet: bool = False) -> int:
    """Re-run graders over stored outputs (no new samples). Use after editing the rubric,
    checks, or must/must_not lists. Rewrites results.jsonl in place."""
    vdir = config.flow_dir / variant
    rows = read_jsonl(vdir / "results.jsonl")
    if not rows:
        raise SystemExit(f"no results for {variant}")
    prompt_path = vdir / "system-prompt.md"
    prompt = prompt_path.read_text(encoding="utf-8").strip() if prompt_path.exists() else config.read_system_prompt()
    cases = {c.id: c for c in load_cases(config.cases_path)}
    graders = build_graders(config)
    update_state(config, metric_declarations(graders))
    refs = (
        load_reference_outputs(config, config.grading.pairwise_reference)
        if any(g.name == "pairwise" for g in graders)
        else {}
    )
    rubric_text = config.read_rubric()
    sem = asyncio.Semaphore(config.run.concurrency)
    failures = 0

    async def one(row: dict[str, Any]) -> None:
        nonlocal failures
        case = cases.get(row["prompt_id"])
        if case is None or row.get("status") != "ok":
            return
        async with sem:
            try:
                grade, explanation, meter = await grade_output(
                    config=config, client=client, graders=graders, case=case, output=row.get("output", ""),
                    stop_reason=row.get("stop_reason"), system_prompt=prompt, rubric_text=rubric_text,
                    variant=variant, rep=row.get("rep", 0),
                    reference_output=_reference_for(refs, case.id, row.get("rep", 0)),
                )
            except Exception as e:  # noqa: BLE001
                failures += 1
                if not quiet:
                    print(f"  {case.id} rep{row.get('rep', 0)}: grader error {e!r}")
                return
            row.update(grade=grade, explanation=explanation, judge_model=meter.model, judge_usage=meter.usage or None)
            row["tags"] = case.tags or ["untagged"]

    await asyncio.gather(*(one(r) for r in rows))
    tmp = vdir / "results.jsonl.tmp"
    tmp.write_text("".join(json.dumps(r, ensure_ascii=False) + "\n" for r in rows), encoding="utf-8")
    tmp.replace(vdir / "results.jsonl")
    return failures
