"""Offline tests for the app evals: prompt export, the entrypoint, and the case set.

A tiny fake of the intake API (signed history, lanes, ticket drafts) stands in for the real app, so nothing here
makes a network call outside localhost or costs anything.
"""

from __future__ import annotations

import asyncio
import hashlib
import json
import re
import socket
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import pytest

from promptevals.cases import Case, load_cases
from promptevals.config import load_config
from promptevals.fake import FakeClient
from promptevals.graders import run_check
from promptevals.runner import read_jsonl, run_variant
from service_desk_evals import prompts_export
from service_desk_evals.target import AppUnavailable, respond, run_conversation

ROOT = Path(__file__).resolve().parents[2]
CASES_PATH = ROOT / "evals" / "app-cases.json"
APP_TOML = ROOT / "python-evals" / "evals.app.toml"

LANES = {"POLICY_VIOLATION", "PUBLIC_TECH", "PUBLIC_SEARCH", "PRIVATE_CORP", "NO_MATCH"}


# ----- prompt export -----------------------------------------------------------------------------------------------

def test_exported_prompts_are_up_to_date_with_prompts_cs():
    expected = prompts_export.export()
    committed = (ROOT / "evals" / "app-prompts.md").read_text(encoding="utf-8").replace("\r\n", "\n")
    assert committed == expected, "Prompts.cs changed: run `python -m service_desk_evals.prompts_export`"


def test_export_finds_the_prompts_the_evals_care_about():
    prompts = prompts_export.parse_prompts(prompts_export.PROMPTS_CS.read_text(encoding="utf-8"))
    for name in ("GuardrailRouter", "ComplianceCoach", "TicketInterviewer", "TicketInterviewerConfirmTurn", "TicketConfirmPrompt"):
        assert prompts[name].strip(), name
    assert "PRIVATE_CORP" in prompts["GuardrailRouter"]
    assert prompts["TicketConfirmPrompt"].startswith("**Shall I send this to the service desk?**")


def test_parse_handles_raw_and_plain_literals_and_rejects_unknown_escapes():
    source = (
        'public static class Prompts\n{\n'
        '    public const string A = """\n'
        '        line one\n'
        '          indented\n'
        '\n'
        '        line three\n'
        '        """;\n'
        '    public const string B =\n'
        '        "plain \\"quoted\\" text";\n'
        '}\n'
    )
    assert prompts_export.parse_prompts(source) == {"A": "line one\n  indented\n\nline three", "B": 'plain "quoted" text'}
    with pytest.raises(prompts_export.ExportError):
        prompts_export.parse_prompts('public const string C = "tab\\there";')
    with pytest.raises(prompts_export.ExportError):
        prompts_export.parse_prompts("class Nothing {}")


# ----- a fake of the intake API ------------------------------------------------------------------------------------

def _sig(conversation: str, index: int, role: str, content: str) -> str:
    return hashlib.sha256(f"{conversation}|{index}|{role}|{content}".encode()).hexdigest()[:16]


class FakeApp:
    """Mimics ``POST /api/v1/chat/message``: trusts only history it signed, and answers from a script."""

    def __init__(self, script):
        self.script = script  # (text, turn_index) -> (lane, metadata)
        self.requests: list[dict] = []
        self.status = 200
        owner = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):  # keep test output quiet
                pass

            def do_POST(self):
                body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                owner.requests.append({"path": self.path, "headers": dict(self.headers), "body": body})
                if owner.status != 200:
                    self._send(owner.status, {"title": "unavailable"})
                    return
                conversation = body["conversationId"]
                history = body["conversationHistory"]
                for i, m in enumerate(history):
                    if m.get("sig") != _sig(conversation, i, m["role"], m["content"]):
                        self._send(400, {"error": "unsigned history"})
                        return
                lane, metadata = owner.script(body["messageText"], len(history) // 2)
                reply = f"reply to: {body['messageText'][:40]}"
                self._send(200, {
                    "conversationId": conversation,
                    "actionLane": lane,
                    "responseText": reply,
                    "metadata": metadata,
                    "userSig": _sig(conversation, len(history), "user", body["messageText"]),
                    "assistantSig": _sig(conversation, len(history) + 1, "assistant", reply),
                })

            def _send(self, status, payload):
                data = json.dumps(payload).encode()
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.url = f"http://127.0.0.1:{self.server.server_address[1]}"
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def close(self):
        self.server.shutdown()
        self.server.server_close()


@pytest.fixture
def make_app(monkeypatch):
    apps: list[FakeApp] = []

    def make(script):
        app = FakeApp(script)
        apps.append(app)
        monkeypatch.setenv("SERVICE_DESK_EVAL_URL", app.url)
        return app

    yield make
    for app in apps:
        app.close()


# ----- the entrypoint ----------------------------------------------------------------------------------------------

def test_conversation_replays_user_turns_and_round_trips_signatures(make_app):
    def script(text, turn):
        if turn == 0:
            return "PRIVATE_CORP", {"ticketPending": "true"}
        return "PRIVATE_CORP", {}

    app = make_app(script)

    summary = run_conversation(["first message", "No, don't send it."], "confirm-decline")

    assert summary["lane"] == "PRIVATE_CORP"
    assert summary["draftReached"] is True and summary["ticketPending"] is False
    assert summary["ticketRaised"] is False and summary["everRaised"] is False
    assert [t["ticketPending"] for t in summary["turns"]] == [True, False]
    second = app.requests[1]["body"]
    assert [m["role"] for m in second["conversationHistory"]] == ["user", "assistant"]  # accepted, so the signatures round-tripped
    assert app.requests[0]["path"] == "/api/v1/chat/message"


def test_each_conversation_gets_its_own_user_and_a_colon_free_id(make_app):
    app = make_app(lambda text, turn: ("PUBLIC_TECH", {}))

    run_conversation(["hi"], "weird: case/id")
    run_conversation(["hi"], "weird: case/id")

    users = [r["headers"]["X-Dev-User"] for r in app.requests]
    assert len(set(users)) == 2
    assert all(":" not in u and "/" not in u for u in users)
    assert app.requests[0]["headers"]["X-Dev-Tenant"] == "eval-tenant"


def test_respond_ignores_assistant_turns_in_the_case_and_returns_sorted_json(make_app):
    app = make_app(lambda text, turn: ("PUBLIC_TECH", {"ticketRaised": "true"}) if turn else ("PUBLIC_TECH", {}))
    case = Case(id="c", input=[
        {"role": "user", "content": "one"},
        {"role": "assistant", "content": "(never sent)"},
        {"role": "user", "content": "two"},
    ])

    result = respond("ignored prompt", case.messages(), case)

    sent = [r["body"]["messageText"] for r in app.requests]
    assert sent == ["one", "two"]
    output = json.loads(result["output"])
    assert output["everRaised"] is True and output["ticketRaised"] is True and len(output["turns"]) == 2
    assert result["output"] == json.dumps(output, sort_keys=True)
    assert result["model"] == "service-desk-app"


def test_errors_are_reported_as_unavailable_not_scored(make_app, monkeypatch):
    app = make_app(lambda text, turn: ("PUBLIC_TECH", {}))
    app.status = 503
    with pytest.raises(AppUnavailable, match="HTTP 503"):
        run_conversation(["hi"], "x")

    with socket.socket() as probe:  # a port that was free a moment ago, so nothing is listening on it
        probe.bind(("127.0.0.1", 0))
        closed = probe.getsockname()[1]
    monkeypatch.setenv("SERVICE_DESK_EVAL_URL", f"http://127.0.0.1:{closed}")
    with pytest.raises(AppUnavailable, match="Could not reach the intake API"):
        run_conversation(["hi"], "x")


# ----- the case set ------------------------------------------------------------------------------------------------

def _cases() -> list[Case]:
    return load_cases(CASES_PATH)


def _lane_of(case: Case) -> str | None:
    for check in case.checks:
        lane = check["schema"]["properties"].get("lane", {}).get("const")
        if lane:
            return lane
    return None


def test_every_case_is_well_formed():
    cases = _cases()
    assert len(cases) >= 30
    for case in cases:
        assert case.checks, f"{case.id} has no checks"
        assert all(c["type"] == "json_schema" for c in case.checks), case.id
        assert case.tags and case.description, case.id
        case.messages()  # ends with a user turn
        lane = _lane_of(case)
        assert lane is None or lane in LANES, case.id
    assert len({c.id for c in cases}) == len(cases)


def test_every_case_but_the_queue_one_asserts_that_nothing_is_queued_without_confirmation():
    for case in _cases():
        names = {c["name"] for c in case.checks}
        if "needs-queue" in case.tags:
            assert "ticket-queued" in names, case.id
        else:
            assert "nothing-queued" in names, case.id


def test_the_cases_cover_the_classifier_example_for_replies_to_a_draft():
    confirmation = {c.id for c in _cases() if c.tags[0] == "confirmation"}
    assert {"confirm-decline", "confirm-amend", "confirm-yes-queues-the-ticket", "confirm-reply-breaks-the-rules"} <= confirmation


def test_checks_pass_the_right_outcome_and_fail_the_wrong_one():
    case = next(c for c in _cases() if c.id == "fixture-direct-coworker-password")
    good = json.dumps({"lane": "POLICY_VIOLATION", "everRaised": False})
    wrong_lane = json.dumps({"lane": "PUBLIC_TECH", "everRaised": False})
    queued = json.dumps({"lane": "POLICY_VIOLATION", "everRaised": True})
    assert all(run_check(good, c, case)[0] for c in case.checks)
    assert not all(run_check(wrong_lane, c, case)[0] for c in case.checks)
    assert not all(run_check(queued, c, case)[0] for c in case.checks)


# ----- the whole thing through the harness -------------------------------------------------------------------------

def _oracle(cases: list[Case]):
    """What a correct app does for each message in the case set, derived from the cases' own expectations."""
    last: dict[str, tuple[str, dict]] = {}
    for case in cases:
        text = case.messages()[-1]["content"]
        lane = _lane_of(case) or "PUBLIC_TECH"
        names = {c["name"] for c in case.checks}
        metadata: dict[str, str] = {}
        if "draft-shown" in names:
            metadata["ticketPending"] = "true"
        if "ticket-queued" in names:
            metadata["ticketRaised"] = "true"
        last[text] = (lane, metadata)
    draft_first = {c.messages()[0]["content"] for c in cases if any(x["name"] == "draft-was-shown" for x in c.checks)}

    def script(text, turn):
        if text in draft_first and turn == 0:
            return "PRIVATE_CORP", {"ticketPending": "true"}
        return last.get(text, ("PUBLIC_TECH", {}))

    return script


def _config(tmp_path: Path):
    text = APP_TOML.read_text(encoding="utf-8")
    base = APP_TOML.parent
    for key in ("system_prompt", "cases", "rubric"):
        text = re.sub(rf'^{key} = "([^"]+)"', lambda m, k=key: f'{k} = "{(base / m.group(1)).resolve().as_posix()}"', text, flags=re.M)
    text = text.replace('runs_dir = "runs"', 'runs_dir = "runs"')
    (tmp_path / "evals.app.toml").write_text(text, encoding="utf-8")
    return load_config(tmp_path / "evals.app.toml")


def test_a_correct_app_passes_every_case_through_the_harness(make_app, tmp_path):
    cases = _cases()
    make_app(_oracle(cases))
    config = _config(tmp_path)

    summary = asyncio.run(run_variant(config, "baseline", FakeClient(lambda kw: ""), quiet=True))

    rows = read_jsonl(config.flow_dir / "baseline" / "results.jsonl")
    failed = [r["prompt_id"] for r in rows if r["grade"].get("pass") != 1.0]
    assert (summary.completed, summary.errors) == (len(cases), 0)
    assert not failed, failed


def test_an_app_that_queues_before_confirmation_fails_the_safety_checks(make_app, tmp_path):
    cases = _cases()
    oracle = _oracle(cases)

    def eager(text, turn):
        lane, metadata = oracle(text, turn)
        return lane, {**metadata, "ticketRaised": "true"}  # queues on every turn, with no confirmation

    make_app(eager)
    config = _config(tmp_path)

    asyncio.run(run_variant(config, "baseline", FakeClient(lambda kw: ""), quiet=True))

    rows = read_jsonl(config.flow_dir / "baseline" / "results.jsonl")
    failed = {r["prompt_id"] for r in rows if r["grade"].get("pass") != 1.0}
    expected = {c.id for c in cases if "needs-queue" not in c.tags}
    assert failed == expected  # every case that must not queue now fails; the one that must queue still passes
