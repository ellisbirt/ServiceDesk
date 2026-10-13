"""The system under test.

Default: call Claude with the draft system prompt and the case's conversation.

Custom: set `[target] entrypoint = "package.module:function"` to run your real
application code instead. The function receives the system prompt and the
conversation and may be sync or async:

    def respond(system_prompt: str, messages: list[dict], case: Case) -> str | dict

Return either the response text, or a dict with any of:
    output (str, required), model, usage, stop_reason, latency_s, trace (list of turns)
"""

from __future__ import annotations

import asyncio
import importlib
import inspect
import time
from dataclasses import dataclass, field
from typing import Any, Callable

from . import llm
from .cases import Case
from .config import Config


@dataclass
class TargetResult:
    output: str
    model: str | None
    stop_reason: str | None
    usage: dict[str, int]
    latency_s: float
    attempts: int = 1
    thinking: str = ""
    request_id: str | None = None
    served_by_fallback: bool = False
    stop_details: dict[str, Any] | None = None
    trace: list[dict[str, Any]] = field(default_factory=list)


def _load_callable(spec: str) -> Callable[..., Any]:
    module_name, _, attr = spec.partition(":")
    if not attr:
        raise ValueError(f"entrypoint must look like 'module:function', got {spec!r}")
    return getattr(importlib.import_module(module_name), attr)


class Target:
    def __init__(self, config: Config, client: Any):
        self.config = config
        self.client = client
        self.entry = _load_callable(config.target.entrypoint) if config.target.entrypoint else None

    async def respond(self, system_prompt: str, case: Case) -> TargetResult:
        messages = case.messages()
        if self.entry is not None:
            return await self._call_entry(system_prompt, messages, case)
        r = await llm.call(
            self.client,
            self.config.target,
            system=system_prompt,
            messages=messages,
            max_attempts=self.config.run.max_attempts,
            stream=self.config.target.max_tokens > 16000,
            allow_fallback=True,
        )
        return TargetResult(
            output=r.text,
            model=r.model,
            stop_reason=r.stop_reason,
            usage=r.usage,
            latency_s=r.latency_s,
            attempts=r.attempts,
            thinking=r.thinking,
            request_id=r.request_id,
            served_by_fallback=r.served_by_fallback,
            stop_details=r.stop_details,
        )

    async def _call_entry(self, system_prompt: str, messages: list[dict], case: Case) -> TargetResult:
        start = time.perf_counter()
        if inspect.iscoroutinefunction(self.entry):
            out = await self.entry(system_prompt, messages, case)
        else:
            out = await asyncio.to_thread(self.entry, system_prompt, messages, case)
        latency = time.perf_counter() - start
        if isinstance(out, str):
            out = {"output": out}
        if "output" not in out:
            raise ValueError("entrypoint must return a str or a dict with an 'output' key")
        return TargetResult(
            output=out["output"],
            model=out.get("model"),
            stop_reason=out.get("stop_reason", "end_turn"),
            usage=out.get("usage", {}),
            latency_s=float(out.get("latency_s", round(latency, 3))),
            trace=out.get("trace", []),
        )
