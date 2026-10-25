"""Timeouts can be raised or switched off, for slow local models (Ollama, LM Studio on modest hardware)."""

from __future__ import annotations

import asyncio
import json
from pathlib import Path

from promptevals.config import ModelSettings, load_config
from promptevals.fake import FakeClient
from promptevals.llm import LMStudioProvider, ProviderRouter
from promptevals.runner import read_jsonl, run_variant
from service_desk_evals.target import DEFAULT_TIMEOUT_S, request_timeout


def _client_timeout(settings: ModelSettings):
    provider = ProviderRouter()._provider(settings)
    assert isinstance(provider, LMStudioProvider)
    return provider.client.timeout


def test_a_model_keeps_the_sdk_default_unless_it_sets_timeout_s():
    default = _client_timeout(ModelSettings(model="m", provider="lm_studio"))

    assert default.read == 600  # the SDK's own default (10 minutes)


def test_timeout_s_raises_a_models_request_limit():
    timeout = _client_timeout(ModelSettings(model="m", provider="lm_studio", timeout_s=3600))

    assert timeout == 3600


def test_timeout_s_of_zero_switches_the_limit_off():
    timeout = _client_timeout(ModelSettings(model="m", provider="lm_studio", timeout_s=0))

    assert timeout is None


def test_timeout_s_is_read_from_the_toml(tmp_path: Path):
    (tmp_path / "p.md").write_text("prompt", encoding="utf-8")
    (tmp_path / "r.md").write_text("rubric", encoding="utf-8")
    (tmp_path / "c.json").write_text(json.dumps([{"id": "a", "input": "hi", "checks": [{"type": "contains", "value": "x"}]}]), encoding="utf-8")
    (tmp_path / "evals.toml").write_text(
        """
[paths]
system_prompt = "p.md"
cases = "c.json"
rubric = "r.md"
[target]
provider = "lm_studio"
model = "local"
timeout_s = 0
[judge]
provider = "lm_studio"
model = "other"
timeout_s = 1800
[run]
timeout_s = 0
[grading]
graders = ["code"]
""",
        encoding="utf-8",
    )

    config = load_config(tmp_path / "evals.toml")

    assert config.target.timeout_s == 0 and config.judge.timeout_s == 1800 and config.run.timeout_s == 0


def test_a_per_case_ceiling_of_zero_means_no_ceiling(tmp_path: Path):
    (tmp_path / "p.md").write_text("prompt", encoding="utf-8")
    (tmp_path / "r.md").write_text("rubric", encoding="utf-8")
    (tmp_path / "c.json").write_text(json.dumps([{"id": "a", "input": "hi", "checks": [{"type": "contains", "value": "ok"}]}]), encoding="utf-8")
    (tmp_path / "evals.toml").write_text(
        '[paths]\nsystem_prompt = "p.md"\ncases = "c.json"\nrubric = "r.md"\nflow = "t"\n'
        '[target]\nmodel = "claude-opus-5-5"\n[run]\ntimeout_s = 0\nmax_attempts = 1\n[grading]\ngraders = ["code"]\n',
        encoding="utf-8",
    )
    config = load_config(tmp_path / "evals.toml")

    summary = asyncio.run(run_variant(config, "baseline", FakeClient(lambda kw: "ok"), quiet=True))

    assert (summary.completed, summary.errors) == (1, 0)
    assert read_jsonl(config.flow_dir / "baseline" / "results.jsonl")[0]["grade"]["pass"] == 1.0


def test_the_app_entrypoint_timeout_defaults_and_can_be_raised_or_removed(monkeypatch):
    monkeypatch.delenv("SERVICE_DESK_EVAL_TIMEOUT_S", raising=False)
    assert request_timeout() == DEFAULT_TIMEOUT_S

    monkeypatch.setenv("SERVICE_DESK_EVAL_TIMEOUT_S", "3600")
    assert request_timeout() == 3600

    monkeypatch.setenv("SERVICE_DESK_EVAL_TIMEOUT_S", "0")
    assert request_timeout() is None
