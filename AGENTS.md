# AGENTS.md

This file provides vendor-neutral guidance to coding agents working in this repository. It applies repository-wide; nested `AGENTS.md` files add directory-specific guidance and take precedence within their scope when instructions conflict.

## What this repo is

Development of a system prompt for an IT service-desk assistant (ticket classification, severity, etc.), plus an eval harness to measure prompt changes. The repo also holds the applications built around that prompt.

- `helpdesk/` — the intake service: a .NET API with a React UI that classifies tickets and publishes `ProcessPrivateCorpTicketEvent`. Its `backend/src` holds the shared libraries:
  - `Helpdesk.Llm`: the pluggable chat and embedding providers.
  - `Helpdesk.Dlp`: redaction.
  - `Helpdesk.Shared`: the event contract and prompt-input wrapping.
- `agentdesk/` — AgentDesk, the downstream agent console (.NET 10 API with a React UI). It stores tickets in Cosmos DB and attachments in Blob Storage, and adds the knowledge base and LLM reply assist. See `agentdesk/AGENTS.md` and `agentdesk/README.md`.

- `evals/` — the **single source of truth** for the prompt and its tests: `system-prompt.md` (the prompt under development), `cases.json` (eval cases with stable IDs), `rubric.md` (0–10 scoring, pass = all `must` satisfied, no `must_not` hit, score ≥ 8). Language/SDK-agnostic.
- `python-evals/` — `promptevals`, a Python package/CLI that reads `../evals/` via paths in `python-evals/evals.toml`. `python-evals/README.md` is the full user guide (case format, check types, output layout); read it before changing CLI behaviour.

Design decisions and their reasons are recorded in `docs/design-decisions.md`; read it before changing behaviour that it covers, and add to it when you make a new decision.

## Commands (run from `python-evals/`, PowerShell)

```powershell
python -m venv .venv; .venv\Scripts\activate; pip install -e ".[dev]"   # extras: schema, notebook
pytest                                              # offline, fake client, no API key needed
pytest tests/test_framework.py::test_code_checks    # single test
promptevals validate                                # check cases.json (free)
promptevals selftest                                # offline harness check (free)
promptevals run --variant baseline                  # PAID: samples + grades every case
promptevals compare baseline v1
promptevals report                                  # HTML report needs node or bun on PATH

# Evals for the REAL app (classifier, lanes, hand-off). Needs the intake API running with a model key: PAID.
python -m service_desk_evals.prompts_export         # re-export Prompts.cs to ../evals/app-prompts.md (--check = verify)
promptevals --config evals.app.toml run --variant baseline
```

`run`, `regrade`, `generate`, and `selftest --judge` make paid API calls and prompt for confirmation (`--yes` skips it). Don't run them without the user's go-ahead. `--config` selects a different toml.

## Architecture (promptevals)

- `cli.py` → `config.py` (loads `evals.toml` into typed settings; separate model settings for `target`, `judge`, `generator`) → `runner.py`.
- `runner.run_variant`: for each (case, rep), `target.Target` produces output (direct Claude call by default, or a user `entrypoint` function), then each enabled grader runs, and a row is appended to `runs/<flow>/<variant>/results.jsonl`. It resumes by skipping (case, rep) pairs that are already done. Failures that produce no scorable output go to `errors.jsonl` and are never scored as 0. Refusals get their own metric, and `max_tokens` cut-offs are marked truncated.
- Variant names must match `^(baseline|v\d+)$`. Each variant snapshots its prompt, and the runner refuses to re-run a variant if the prompt changed since (use a new variant name, or `run --force` to discard the old results). `clean_prompt` strips `<!-- -->` comments before the prompt reaches the model.
- `llm.py` is the only place that calls the Anthropic SDK: jittered retries, a served-model mismatch check (`ServedModelMismatch`), structured outputs, and a `UsageMeter`. Judge spend is metered separately; `stats.py` holds the price table and the Wilson/paired CI maths (no numpy).
- `graders/`: subclass `Grader` (`base.py`), declare `metrics`, implement async `grade(ctx) -> GradeResult`, then register it in `build_graders` in `graders/__init__.py`. A case passes only if every applicable grader passes. `code.py` covers deterministic `checks`. `judges.py` covers the checklist, rubric, and pairwise LLM judges, which treat the response as untrusted data. `human.py` covers CSV export/import and judge-agreement reporting.
- `service_desk_evals/` (a second package beside `promptevals`) runs the harness against the real intake API: `prompts_export` writes the C# prompts from `Prompts.cs` to `evals/app-prompts.md` (a pytest guards drift), and `target.respond` is the `entrypoint` used by `evals.app.toml`. Cases are `evals/app-cases.json`. See `python-evals/README.md`, "Evals for the running app".
- `fake.FakeClient` is an offline stand-in for `AsyncAnthropic` (a responder takes the request kwargs). It's used by the tests, by `selftest`, and by `demo.py` (the notebook's `DEMO = True` mode, which writes to `runs-demo/`).
- `generate.py` writes proposed cases to `../evals/cases.proposed.json` + `.md`. They're only merged into `cases.json` through `promptevals accept`.
- `report.py` writes `summary.md`/`compare` output and shells out to `report/build-report-lite.mjs` for `report.html`.

## Conventions

- Never delete an eval case just to make a prompt version pass. Keep cases that caught regressions.
- Prefer code `checks` over LLM judges whenever a property can be checked mechanically.
- The judge model should differ from the target model (the model under test shouldn't grade itself).
- The starter cases in `evals/cases.json` are placeholders (`[a common task ...]`) and are meant to be replaced with real service-desk cases.
