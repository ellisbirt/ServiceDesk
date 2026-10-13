"""Deterministic (code-based) graders driven by each case's `checks` list.

Supported check types:
    contains / not_contains     {"value": str | [str], "case_sensitive": false, "any": false}
    regex / not_regex           {"pattern": str, "flags": "i"}
    equals                      {"value": str}   (whitespace/case-normalised)
    starts_with                 {"value": str}
    max_words / min_words       {"value": int}
    max_chars / min_chars       {"value": int}
    max_sentences               {"value": int}
    json_valid                  {}               (output, or its first ```json block, parses)
    json_schema                 {"schema": {...}} (needs `pip install jsonschema`)
    no_markdown                 {}               (no headings, bullets, bold, or code fences)
    python                      {"function": "module:func"}  func(output, case) -> bool | (bool, str)

Every check may carry an optional "name" used in reports.
"""

from __future__ import annotations

import importlib
import json
import re
from typing import Any, Callable

from ..cases import Case
from .base import GradeContext, GradeResult, Grader


def _norm(s: str) -> str:
    return re.sub(r"\s+", " ", s).strip().lower()


def _extract_json(text: str) -> Any:
    m = re.search(r"```(?:json)?\s*(.*?)```", text, re.DOTALL)
    return json.loads(m.group(1) if m else text)


def _flags(spec: str) -> int:
    return (re.IGNORECASE if "i" in spec else 0) | (re.MULTILINE if "m" in spec else 0) | (
        re.DOTALL if "s" in spec else 0
    )


def _contains(output: str, check: dict) -> tuple[bool, str]:
    values = check["value"] if isinstance(check["value"], list) else [check["value"]]
    hay = output if check.get("case_sensitive") else output.lower()
    hits = [v for v in values if (v if check.get("case_sensitive") else v.lower()) in hay]
    ok = bool(hits) if check.get("any") else len(hits) == len(values)
    missing = [v for v in values if v not in hits]
    return ok, "" if ok else f"missing {missing}"


def run_check(output: str, check: dict, case: Case) -> tuple[bool, str]:
    t = check["type"]
    words = len(output.split())
    if t == "contains":
        return _contains(output, check)
    if t == "not_contains":
        values = check["value"] if isinstance(check["value"], list) else [check["value"]]
        hay = output if check.get("case_sensitive") else output.lower()
        found = [v for v in values if (v if check.get("case_sensitive") else v.lower()) in hay]
        return not found, "" if not found else f"found {found}"
    if t == "regex":
        ok = re.search(check["pattern"], output, _flags(check.get("flags", ""))) is not None
        return ok, "" if ok else f"no match for /{check['pattern']}/"
    if t == "not_regex":
        m = re.search(check["pattern"], output, _flags(check.get("flags", "")))
        return m is None, "" if m is None else f"matched {m.group(0)!r}"
    if t == "equals":
        ok = _norm(output) == _norm(check["value"])
        return ok, "" if ok else "not equal"
    if t == "starts_with":
        ok = _norm(output).startswith(_norm(check["value"]))
        return ok, "" if ok else "wrong prefix"
    if t == "max_words":
        return words <= check["value"], f"{words} words (max {check['value']})"
    if t == "min_words":
        return words >= check["value"], f"{words} words (min {check['value']})"
    if t == "max_chars":
        return len(output) <= check["value"], f"{len(output)} chars (max {check['value']})"
    if t == "min_chars":
        return len(output) >= check["value"], f"{len(output)} chars (min {check['value']})"
    if t == "max_sentences":
        n = len([s for s in re.split(r"(?<=[.!?])\s+", output.strip()) if s])
        return n <= check["value"], f"{n} sentences (max {check['value']})"
    if t == "json_valid":
        try:
            _extract_json(output)
            return True, ""
        except (json.JSONDecodeError, ValueError) as e:
            return False, f"invalid JSON: {e}"
    if t == "json_schema":
        try:
            import jsonschema
        except ImportError as e:  # pragma: no cover
            raise RuntimeError("json_schema checks need `pip install jsonschema`") from e
        try:
            jsonschema.validate(_extract_json(output), check["schema"])
            return True, ""
        except (json.JSONDecodeError, ValueError) as e:
            return False, f"invalid JSON: {e}"
        except jsonschema.ValidationError as e:
            return False, f"schema: {e.message}"
    if t == "no_markdown":
        m = re.search(r"(^#{1,6}\s|^\s*[-*+]\s|\*\*|```)", output, re.MULTILINE)
        return m is None, "" if m is None else f"markdown found: {m.group(0)!r}"
    if t == "python":
        fn = _load(check["function"])
        result = fn(output, case)
        if isinstance(result, tuple):
            return bool(result[0]), str(result[1])
        return bool(result), ""
    raise ValueError(f"unknown check type {t!r} in case {case.id}")


_FN_CACHE: dict[str, Callable] = {}


def _load(spec: str) -> Callable:
    if spec not in _FN_CACHE:
        mod, _, attr = spec.partition(":")
        _FN_CACHE[spec] = getattr(importlib.import_module(mod), attr)
    return _FN_CACHE[spec]


class CodeGrader(Grader):
    name = "code"
    metrics = [{"id": "checks", "label": "Code checks", "kind": "float", "scale": 1}]

    def applies_to(self, case: Case) -> bool:
        return bool(case.checks)

    async def grade(self, ctx: GradeContext) -> GradeResult:
        results, notes = [], []
        for i, check in enumerate(ctx.case.checks):
            name = check.get("name") or f"{check['type']}#{i + 1}"
            ok, detail = run_check(ctx.output, check, ctx.case)
            results.append(ok)
            notes.append(f"{'PASS' if ok else 'FAIL'} {name}" + (f": {detail}" if detail and not ok else ""))
        frac = sum(results) / len(results)
        return GradeResult(
            scores={"checks": round(frac, 4)},
            explanations={"checks": "\n".join(notes)},
            passed=all(results),
        )
