"""``promptevals`` entrypoint that sends each case to a running intake API and reports what the app did.

Use it with ``evals.app.toml`` (``[target] entrypoint = "service_desk_evals.target:respond"``). It exercises the real
pipeline: the regex pre-filter, the classifier prompt, the lane routing, the coach and the ticket interviewer. Nothing
here calls a model directly, so the only paid calls are the ones the app itself makes.

What the app must be running with (see ``python-evals/README.md``):

* ``ASPNETCORE_ENVIRONMENT=Development`` and dev auth (the default there), so the ``X-Dev-*`` headers are accepted;
* a chat provider (for example ``Anthropic:ApiKey``), because offline mode would test a keyword stub, not the prompts;
* optionally Azurite, only for cases tagged ``needs-queue`` (they confirm a ticket, which publishes to the queue).

How a case is replayed. The app trusts only history it signed, so this cannot send a made-up assistant turn. It sends
the case's **user** turns one at a time and feeds the app's own replies (with their signatures) back as history. So a
multi-turn case is "what happens when this user says these things", not "what happens after this exact transcript".

The output is a JSON object (sorted keys) so cases can assert on it with ``json_schema`` checks:

``lane``           lane of the last turn (``POLICY_VIOLATION``, ``PUBLIC_TECH``, ``PUBLIC_SEARCH``, ``PRIVATE_CORP``, ``NO_MATCH``)
``ticketPending``  the last reply showed a ticket draft and is waiting for the user to confirm it
``ticketRaised``   the last reply queued a ticket
``draftReached``   some earlier turn showed a ticket draft (so a later "no" or "yes" really answered one)
``everRaised``     any turn queued a ticket
``turns``          ``[{lane, ticketPending, ticketRaised}]`` for every turn
``reply``          the last reply text, cut to 1,500 characters
"""

from __future__ import annotations

import json
import os
import re
import socket
import urllib.error
import urllib.parse
import urllib.request
import uuid
from functools import lru_cache
from typing import Any

from promptevals.cases import Case

DEFAULT_URL = "http://localhost:5129"
DEFAULT_TIMEOUT_S = 150
CONNECT_TIMEOUT_S = 5
MAX_HISTORY = 20  # the API accepts at most 20 history messages

TICKET_PENDING = "ticketPending"
TICKET_RAISED = "ticketRaised"


class AppUnavailable(RuntimeError):
    """The app could not be reached or answered with an error; the harness records this as an error, never as a 0."""


def request_timeout() -> float | None:
    """Seconds one request to the app may take: ``SERVICE_DESK_EVAL_TIMEOUT_S``, default 150; 0 means no limit.

    A slow local model behind the app (LM Studio on modest hardware) can need minutes per reply. Raise this, and the app's own
    limits (``Llm:Http``), together.
    """
    raw = os.environ.get("SERVICE_DESK_EVAL_TIMEOUT_S")
    seconds = float(raw) if raw else DEFAULT_TIMEOUT_S
    return seconds if seconds > 0 else None


def base_url() -> str:
    return os.environ.get("SERVICE_DESK_EVAL_URL", DEFAULT_URL).rstrip("/")


@lru_cache(maxsize=None)
def _check_reachable(url: str) -> None:
    """Fail within seconds if nothing is listening, instead of waiting out the request timeout once per case."""
    parts = urllib.parse.urlsplit(url)
    host, port = parts.hostname or "localhost", parts.port or (443 if parts.scheme == "https" else 80)
    try:
        socket.create_connection((host, port), timeout=CONNECT_TIMEOUT_S).close()
    except OSError as error:
        raise AppUnavailable(
            f"Could not reach the intake API at {url} ({error}). Start it (see python-evals/README.md) "
            f"or set SERVICE_DESK_EVAL_URL."
        ) from error


def _post(url: str, body: dict[str, Any], tenant: str, user: str) -> dict[str, Any]:
    request = urllib.request.Request(
        url,
        data=json.dumps(body).encode("utf-8"),
        method="POST",
        headers={"Content-Type": "application/json", "X-Dev-Tenant": tenant, "X-Dev-User": user},
    )
    try:
        with urllib.request.urlopen(request, timeout=request_timeout()) as response:  # noqa: S310 - a URL the operator configured
            return json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as error:
        detail = error.read().decode("utf-8", errors="replace")[:300]
        raise AppUnavailable(f"{url} answered HTTP {error.code}: {detail}") from error
    except (urllib.error.URLError, TimeoutError, ConnectionError) as error:
        raise AppUnavailable(
            f"Could not reach the intake API at {url} ({error}). Start it (see python-evals/README.md) "
            f"or set SERVICE_DESK_EVAL_URL."
        ) from error


def _safe_id(text: str) -> str:
    # Ids must not contain ':' (the API joins tenant and user into keys). One user per case avoids shared rate limits.
    return re.sub(r"[^A-Za-z0-9_.-]", "-", text)[:48]


def run_conversation(user_turns: list[str], case_id: str, url: str | None = None) -> dict[str, Any]:
    """Send ``user_turns`` in order and return the summary described in the module docstring."""
    root = url or base_url()
    _check_reachable(root)
    endpoint = f"{root}/api/v1/chat/message"
    tenant = "eval-tenant"
    user = f"eval-{_safe_id(case_id)}-{uuid.uuid4().hex[:6]}"
    conversation_id = str(uuid.uuid4())
    history: list[dict[str, Any]] = []
    turns: list[dict[str, Any]] = []
    last: dict[str, Any] = {}

    for text in user_turns:
        last = _post(
            endpoint,
            {"messageText": text, "conversationId": conversation_id, "conversationHistory": history[-MAX_HISTORY:]},
            tenant,
            user,
        )
        metadata = last.get("metadata") or {}
        turns.append(
            {
                "lane": last.get("actionLane"),
                TICKET_PENDING: metadata.get(TICKET_PENDING) == "true",
                TICKET_RAISED: metadata.get(TICKET_RAISED) == "true",
            }
        )
        # The server signs each message; send both back exactly as received so the next turn trusts them.
        history.append({"role": "user", "content": text, "sig": last.get("userSig")})
        history.append({"role": "assistant", "content": last.get("responseText", ""), "sig": last.get("assistantSig")})

    final = turns[-1]
    return {
        "lane": final["lane"],
        TICKET_PENDING: final[TICKET_PENDING],
        TICKET_RAISED: final[TICKET_RAISED],
        "draftReached": any(t[TICKET_PENDING] for t in turns[:-1]),
        "everRaised": any(t[TICKET_RAISED] for t in turns),
        "turns": turns,
        "reply": (last.get("responseText") or "")[:1500],
    }


def respond(system_prompt: str, messages: list[dict], case: Case) -> dict[str, Any]:
    """``promptevals`` entrypoint. ``system_prompt`` is the exported snapshot and is not sent anywhere."""
    user_turns = [m["content"] for m in messages if m["role"] == "user"]
    if not user_turns:
        raise ValueError(f"case {case.id}: no user turn to send")
    summary = run_conversation(user_turns, case.id)
    return {
        "output": json.dumps(summary, sort_keys=True),
        "model": "service-desk-app",
        "trace": [{"role": "user", "content": t} for t in user_turns] + [{"role": "app", "content": summary["reply"]}],
    }
