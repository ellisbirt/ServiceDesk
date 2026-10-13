"""Load evals.toml into typed settings."""

from __future__ import annotations

import re
import tomllib
from dataclasses import dataclass, field
from pathlib import Path


@dataclass
class ModelSettings:
    model: str
    provider: str = "anthropic"
    max_tokens: int = 16000
    effort: str | None = None
    thinking_display: str | None = None
    fallbacks: str | None = None
    entrypoint: str | None = None
    endpoint: str | None = None
    api_version: str | None = None
    api_key_env: str | None = None


@dataclass
class RunSettings:
    reps: int = 1
    concurrency: int = 8
    timeout_s: float = 240.0
    max_attempts: int = 4


@dataclass
class GradingSettings:
    graders: list[str] = field(default_factory=lambda: ["code", "checklist", "rubric"])
    checklist_mode: str = "batched"
    rubric_max: float = 10.0
    rubric_pass_threshold: float = 8.0
    pairwise_reference: str = "baseline"


@dataclass
class Config:
    root: Path
    system_prompt_path: Path
    cases_path: Path
    rubric_path: Path | None
    runs_dir: Path
    flow: str
    target: ModelSettings
    judge: ModelSettings
    generator: ModelSettings
    run: RunSettings
    grading: GradingSettings

    @property
    def flow_dir(self) -> Path:
        return self.runs_dir / self.flow

    def read_system_prompt(self) -> str:
        return clean_prompt(self.system_prompt_path.read_text(encoding="utf-8"))

    def read_rubric(self) -> str:
        if self.rubric_path and self.rubric_path.exists():
            return self.rubric_path.read_text(encoding="utf-8")
        return ""


def clean_prompt(text: str) -> str:
    """Strip HTML comments (authoring notes) so they never reach the model."""
    return re.sub(r"<!--.*?-->", "", text, flags=re.DOTALL).strip()


def _model(section: dict, default_model: str) -> ModelSettings:
    return ModelSettings(
        model=section.get("model", default_model),
        provider=section.get("provider", "anthropic"),
        max_tokens=int(section.get("max_tokens", 16000)),
        effort=section.get("effort"),
        thinking_display=section.get("thinking_display"),
        fallbacks=section.get("fallbacks"),
        entrypoint=section.get("entrypoint"),
        endpoint=section.get("endpoint"),
        api_version=section.get("api_version"),
        api_key_env=section.get("api_key_env"),
    )


def load_config(path: str | Path = "evals.toml") -> Config:
    path = Path(path).resolve()
    data = tomllib.loads(path.read_text(encoding="utf-8"))
    root = path.parent
    paths = data.get("paths", {})

    def p(key: str, default: str | None) -> Path | None:
        value = paths.get(key, default)
        return (root / value).resolve() if value else None

    run = data.get("run", {})
    grading = data.get("grading", {})
    return Config(
        root=root,
        system_prompt_path=p("system_prompt", "system-prompt.md"),
        cases_path=p("cases", "cases.json"),
        rubric_path=p("rubric", None),
        runs_dir=p("runs_dir", "runs"),
        flow=paths.get("flow", "default"),
        target=_model(data.get("target", {}), "claude-sonnet-5-5"),
        judge=_model(data.get("judge", {}), "claude-sonnet-5-5"),
        generator=_model(data.get("generator", {}), "claude-opus-5-5"),
        run=RunSettings(
            reps=int(run.get("reps", 1)),
            concurrency=int(run.get("concurrency", 8)),
            timeout_s=float(run.get("timeout_s", 240)),
            max_attempts=int(run.get("max_attempts", 4)),
        ),
        grading=GradingSettings(
            graders=list(grading.get("graders", ["code", "checklist", "rubric"])),
            checklist_mode=grading.get("checklist_mode", "batched"),
            rubric_max=float(grading.get("rubric_max", 10)),
            rubric_pass_threshold=float(grading.get("rubric_pass_threshold", 8)),
            pairwise_reference=grading.get("pairwise_reference", "baseline"),
        ),
    )
