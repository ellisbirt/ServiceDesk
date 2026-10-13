"""Check the harness and graders before trusting their numbers.

Offline (free): pushes a null output, an oracle output (each case's `expected`, if
present), an API failure, and a truncated response through the real runner with
a fake model, and checks each lands where it should.

Judge (paid, small): sends known-bad responses - empty, "I don't know", and a
confident answer to a different question - to the real LLM graders for a few
cases and confirms they fail. Also confirms `expected` answers pass, if present.
"""

from __future__ import annotations

import asyncio
import copy
import json
import shutil
import tempfile
from pathlib import Path
from typing import Any

import anthropic

from .cases import load_cases
from .config import Config
from .fake import FakeClient
from .graders import build_graders
from .runner import grade_output, read_jsonl, run_variant

OFF_TOPIC = (
    "Great question! The French Revolution began in 1789 with the storming of the Bastille, "
    "driven by fiscal crisis, Enlightenment ideas, and resentment of aristocratic privilege. "
    "It ended the absolute monarchy and reshaped European politics for a century."
)


def _offline_config(config: Config, tmp: Path) -> Config:
    c = copy.deepcopy(config)
    c.runs_dir = tmp
    c.flow = "selftest"
    c.grading.graders = ["code"]
    c.target.entrypoint = None
    c.run.max_attempts = 1
    c.run.timeout_s = 30
    return c


def run_offline(config: Config) -> tuple[bool, str]:
    cases = load_cases(config.cases_path)
    lines, ok = [], True
    tmp = Path(tempfile.mkdtemp(prefix="promptevals-selftest-"))
    try:
        c = _offline_config(config, tmp)
        first = cases[0].id

        # 1. null output: code checks should fail wherever a case has content checks
        asyncio.run(run_variant(c, "baseline", FakeClient(lambda kw: ""), quiet=True))
        rows = read_jsonl(c.flow_dir / "baseline" / "results.jsonl")
        checked = [r for r in rows if r["grade"].get("checks") is not None]
        null_pass = [r["prompt_id"] for r in checked if r["grade"]["pass"] == 1.0]
        lines.append(f"null output: {len(rows)}/{len(cases)} rows written; "
                     f"{len(checked)} cases have code checks; passed on empty output: {null_pass or 'none'}")
        if len(rows) != len(cases):
            ok = False
        if null_pass:
            lines.append("  note: these cases' checks accept an empty response - consider adding a "
                         "min_words/contains check or rely on the LLM graders for them")

        # 2. oracle: `expected` answers should pass their code checks
        oracle = {cs.id: cs.expected for cs in cases if cs.expected}
        if oracle:
            def oracle_responder(kw: dict[str, Any]) -> str:
                last = kw["messages"][-1]["content"]
                for cs in cases:
                    if cs.messages()[-1]["content"] == last:
                        return oracle.get(cs.id, "")
                return ""

            asyncio.run(run_variant(c, "v1", FakeClient(oracle_responder), ids=list(oracle), quiet=True))
            rows = read_jsonl(c.flow_dir / "v1" / "results.jsonl")
            failed = [r["prompt_id"] for r in rows if r["grade"].get("checks") not in (None, 1.0)]
            lines.append(f"oracle (`expected` answers): {len(rows)} cases; failed own checks: {failed or 'none'}")
            ok &= not failed
        else:
            lines.append("oracle: skipped - no case has an `expected` answer")

        # 3. induced API error -> errors.jsonl, not a zero score
        def boom(kw: dict[str, Any]) -> Any:
            raise anthropic.APIConnectionError(request=None)  # type: ignore[arg-type]

        asyncio.run(run_variant(c, "v2", FakeClient(boom), ids=[first], quiet=True))
        res = read_jsonl(c.flow_dir / "v2" / "results.jsonl")
        errs = read_jsonl(c.flow_dir / "v2" / "errors.jsonl")
        good = not res and len(errs) == 1 and errs[0]["class"] == "api_error"
        lines.append(f"API failure -> errors.jsonl (not scored): {'OK' if good else 'FAILED'}")
        ok &= good

        # 4. truncated response -> status truncated, excluded from means
        asyncio.run(run_variant(c, "v3", FakeClient(lambda kw: {"text": "partial", "stop_reason": "max_tokens"}),
                                ids=[first], quiet=True))
        res = read_jsonl(c.flow_dir / "v3" / "results.jsonl")
        good = len(res) == 1 and res[0]["status"] == "truncated"
        lines.append(f"max_tokens response -> status=truncated: {'OK' if good else 'FAILED'}")
        ok &= good

        # 5. served-model mismatch -> errors.jsonl
        asyncio.run(run_variant(c, "v4", FakeClient(lambda kw: {"text": "hi", "model": "some-other-model"}),
                                ids=[first], quiet=True))
        errs = read_jsonl(c.flow_dir / "v4" / "errors.jsonl")
        good = len(errs) == 1 and errs[0]["class"] == "served_model_mismatch"
        lines.append(f"wrong served model -> errors.jsonl: {'OK' if good else 'FAILED'}")
        ok &= good
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    return ok, "\n".join(lines)


async def run_judge_check(config: Config, client: Any, n_cases: int = 3) -> tuple[bool, str]:
    graders = [g for g in build_graders(config) if g.name in ("checklist", "rubric")]
    if not graders:
        return True, "no LLM graders enabled (checklist/rubric) - nothing to check"
    cases = load_cases(config.cases_path)[:n_cases]
    prompt, rubric = config.read_system_prompt(), config.read_rubric()
    negatives = {"empty": "", "dont-know": "I don't know.", "off-topic": OFF_TOPIC}
    lines, ok = [], True

    async def one(case, label, output, expect_pass):
        grade, expl, meter = await grade_output(
            config=config, client=client, graders=graders, case=case, output=output, stop_reason="end_turn",
            system_prompt=prompt, rubric_text=rubric, variant="selftest", rep=0, reference_output=None,
        )
        got = grade.get("pass")
        good = got == (1.0 if expect_pass else 0.0)
        shown = {k: v for k, v in grade.items() if v is not None and k != "refusal"}
        return good, f"{'OK ' if good else 'BAD'} {case.id:<28} {label:<10} {json.dumps(shown)}"

    jobs = [one(cs, k, v, False) for cs in cases for k, v in negatives.items()]
    jobs += [one(cs, "oracle", cs.expected, True) for cs in cases if cs.expected]
    for good, line in await asyncio.gather(*jobs):
        ok &= good
        lines.append(line)
    if not ok:
        lines.append("A BAD row means the grader passed a known-bad response (or failed the oracle). "
                     "Tighten the case's must/must_not items or the rubric before trusting scores.")
    return ok, "\n".join(lines)
