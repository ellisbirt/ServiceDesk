from __future__ import annotations

from ..config import Config
from .base import GradeContext, GradeResult, Grader
from .code import CodeGrader, run_check
from .judges import ChecklistGrader, PairwiseGrader, RubricGrader

# Metrics recorded on every row regardless of which graders are enabled.
# `pass` is listed first so it becomes the report's headline metric.
CORE_METRICS = [
    {"id": "pass", "label": "Pass", "kind": "binary"},
]
REFUSAL_METRIC = {"id": "refusal", "label": "Refused", "kind": "binary", "better": "lower"}


def build_graders(config: Config) -> list[Grader]:
    graders: list[Grader] = []
    for name in config.grading.graders:
        if name == "code":
            graders.append(CodeGrader())
        elif name == "checklist":
            graders.append(ChecklistGrader())
        elif name == "rubric":
            graders.append(RubricGrader(config.grading.rubric_max, config.grading.rubric_pass_threshold))
        elif name == "pairwise":
            graders.append(PairwiseGrader())
        else:
            raise ValueError(f"unknown grader {name!r} (expected code, checklist, rubric, pairwise)")
    return graders


def metric_declarations(graders: list[Grader]) -> list[dict]:
    metrics = list(CORE_METRICS)
    for g in graders:
        metrics.extend(g.metrics)
    metrics.append(REFUSAL_METRIC)
    return metrics


__all__ = [
    "Grader",
    "GradeContext",
    "GradeResult",
    "CodeGrader",
    "ChecklistGrader",
    "RubricGrader",
    "PairwiseGrader",
    "build_graders",
    "metric_declarations",
    "run_check",
]
