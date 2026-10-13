"""An offline stand-in for anthropic.AsyncAnthropic, for tests and harness self-checks.

    client = FakeClient(lambda kwargs: "response text")

The responder receives the request kwargs and returns a str (text), a dict with
optional keys {text, model, stop_reason, usage}, or raises an exception.
"""

from __future__ import annotations

import json
from types import SimpleNamespace
from typing import Any, Callable

Responder = Callable[[dict[str, Any]], Any]


def _message(kwargs: dict[str, Any], out: Any) -> SimpleNamespace:
    if isinstance(out, (dict, list)) and not (isinstance(out, dict) and "text" in out):
        out = {"text": json.dumps(out)}
    if isinstance(out, str):
        out = {"text": out}
    usage = {"input_tokens": 100, "output_tokens": 50, "cache_read_input_tokens": 0,
             "cache_creation_input_tokens": 0, **out.get("usage", {})}
    return SimpleNamespace(
        model=out.get("model", kwargs["model"]),
        stop_reason=out.get("stop_reason", "end_turn"),
        stop_details=None,
        content=[SimpleNamespace(type="text", text=out["text"])],
        usage=SimpleNamespace(iterations=None, **usage),
        _request_id="req_fake",
    )


class _Stream:
    def __init__(self, msg_coro):
        self._coro = msg_coro

    async def __aenter__(self):
        self._msg = await self._coro
        return self

    async def __aexit__(self, *exc):
        return False

    async def get_final_message(self):
        return self._msg


class _Messages:
    def __init__(self, client: "FakeClient"):
        self._client = client

    async def create(self, **kwargs: Any) -> SimpleNamespace:
        self._client.calls.append(kwargs)
        return _message(kwargs, self._client.responder(kwargs))

    def stream(self, **kwargs: Any) -> _Stream:
        return _Stream(self.create(**kwargs))


class FakeClient:
    def __init__(self, responder: Responder):
        self.responder = responder
        self.calls: list[dict[str, Any]] = []
        self.messages = _Messages(self)
        self.beta = SimpleNamespace(messages=self.messages)
