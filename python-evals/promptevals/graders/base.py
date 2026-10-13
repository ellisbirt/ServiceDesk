from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any

from ..cases import Case
from ..config import Config
from ..llm import UsageMeter


@dataclass
class GradeContext:
    case: Case
    output: str
    system_prompt: str
    rubric_text: str
    config: Config
    client: Any
    rep: int = 0
    variant: str = "baseline"
    reference_output: str | None = None  # for pairwise; None when unavailable
    judge_meter: UsageMeter = field(default_factory=UsageMeter)


@dataclass
class GradeResult:
    scores: dict[str, float | None] = field(default_factory=dict)
    explanations: dict[str, str] = field(default_factory=dict)
    # Whether this grader considers the case passed; None = grader has no opinion.
    passed: bool | None = None


class Grader:
    """Base class. Subclasses declare `metrics` (report columns) and implement grade()."""

    name: str = "grader"
    metrics: list[dict[str, Any]] = []

    def applies_to(self, case: Case) -> bool:
        return True

    async def grade(self, ctx: GradeContext) -> GradeResult:  # pragma: no cover
        raise NotImplementedError
