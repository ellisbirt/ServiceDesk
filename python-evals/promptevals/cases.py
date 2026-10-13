"""Eval case model and loading.

The on-disk format is a JSON list of objects, compatible with ../evals/cases.json:

    {
      "id": "missing-information",            # stable, unique
      "description": "Asks for context instead of guessing.",
      "input": "Please reset it.",            # or a list of {"role", "content"} turns
      "tags": ["clarification"],              # tags[0] is the primary grouping key
      "must": ["Asks which account or system needs resetting."],
      "must_not": ["Claims to have reset anything."],
      "checks": [{"type": "max_words", "value": 120}],   # deterministic graders
      "rubric": "Optional per-case rubric text for the rubric judge.",
      "expected": "Optional reference answer (oracle for self-tests)."
    }
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any


@dataclass
class Case:
    id: str
    input: str | list[dict[str, str]]
    description: str = ""
    tags: list[str] = field(default_factory=list)
    must: list[str] = field(default_factory=list)
    must_not: list[str] = field(default_factory=list)
    checks: list[dict[str, Any]] = field(default_factory=list)
    rubric: str | None = None
    expected: str | None = None
    meta: dict[str, Any] = field(default_factory=dict)

    @classmethod
    def from_dict(cls, d: dict[str, Any]) -> "Case":
        known = {"id", "input", "description", "tags", "must", "must_not", "checks", "rubric", "expected"}
        if "id" not in d or "input" not in d:
            raise ValueError(f"case is missing 'id' or 'input': {d!r}")
        return cls(
            id=str(d["id"]),
            input=d["input"],
            description=d.get("description", ""),
            tags=list(d.get("tags") or []),
            must=list(d.get("must") or []),
            must_not=list(d.get("must_not") or []),
            checks=list(d.get("checks") or []),
            rubric=d.get("rubric"),
            expected=d.get("expected"),
            meta={k: v for k, v in d.items() if k not in known},
        )

    def to_dict(self) -> dict[str, Any]:
        d: dict[str, Any] = {"id": self.id, "description": self.description, "input": self.input}
        for key in ("tags", "must", "must_not", "checks"):
            if getattr(self, key):
                d[key] = getattr(self, key)
        for key in ("rubric", "expected"):
            if getattr(self, key) is not None:
                d[key] = getattr(self, key)
        d.update(self.meta)
        return d

    def messages(self) -> list[dict[str, str]]:
        """The conversation sent to the target. The last turn must be the user's."""
        if isinstance(self.input, str):
            return [{"role": "user", "content": self.input}]
        turns = [{"role": t["role"], "content": t["content"]} for t in self.input]
        if not turns or turns[-1]["role"] != "user":
            raise ValueError(f"case {self.id}: multi-turn input must end with a user turn")
        return turns

    def prompt_text(self) -> str:
        """Human-readable rendering of the input, for reports and judges."""
        if isinstance(self.input, str):
            return self.input
        return "\n\n".join(f"[{t['role']}]\n{t['content']}" for t in self.input)


def load_cases(path: Path) -> list[Case]:
    raw = json.loads(Path(path).read_text(encoding="utf-8"))
    cases = [Case.from_dict(d) for d in raw]
    seen: set[str] = set()
    for c in cases:
        if c.id in seen:
            raise ValueError(f"duplicate case id: {c.id}")
        seen.add(c.id)
        c.messages()  # validate shape early
    return cases


def save_cases(path: Path, cases: list[Case]) -> None:
    Path(path).write_text(
        json.dumps([c.to_dict() for c in cases], indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )
