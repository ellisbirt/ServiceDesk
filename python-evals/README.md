# promptevals

An eval framework for developing and evaluating LLM prompts. It generates test
cases, runs the prompt against them, grades the responses with several kinds
of grader, and compares prompt versions with confidence intervals.

It uses the files in `../evals/` directly (`system-prompt.md`, `cases.json`,
`rubric.md`), so that folder stays the single source of truth.

## Setup

```powershell
python -m venv .venv
.venv\Scripts\activate
pip install -e ".[dev]"
$env:ANTHROPIC_API_KEY = "sk-ant-..."     # or use an `ant auth login` profile
```

Claude (Anthropic) remains the default. Set `provider` independently in
`[target]`, `[judge]`, or `[generator]` in `evals.toml` to choose a provider:

| `provider` | API key / endpoint defaults |
|---|---|
| `anthropic` | `ANTHROPIC_API_KEY` |
| `openai` | `OPENAI_API_KEY` |
| `grok` | `XAI_API_KEY`, `https://api.x.ai/v1` |
| `microsoft` (`azure_openai` alias) | `AZURE_OPENAI_API_KEY`, `AZURE_OPENAI_ENDPOINT`; set `api_version` (defaults to `2024-10-21`); `model` is the Azure deployment name |
| `ollama` | `http://localhost:11434/v1` |
| `lm_studio` | `http://localhost:1234/v1` |

For example, use a local Ollama model as the target while keeping Claude for
judging:

```toml
[target]
provider = "ollama"
model = "qwen3"

[judge]
provider = "anthropic"
model = "claude-sonnet-5-5"
```

For any provider, `endpoint` overrides its default URL and `api_key_env` names
the environment variable containing its key. Local OpenAI-compatible servers
use a placeholder key automatically. Settings live in `evals.toml`: providers,
models, effort, concurrency, reps, timeouts, and which graders run.

## The loop

```powershell
promptevals validate                  # check the case set (free)
promptevals selftest                  # check the harness offline (free)
promptevals selftest --judge          # check the LLM graders fail known-bad answers (a few judge calls)

promptevals run --variant baseline    # sample and grade every case
# ... edit ../evals/system-prompt.md ...
promptevals run --variant v1 --note "Ask a clarifying question before guessing"
promptevals compare baseline v1       # paired per-case diff, regressions listed
promptevals report                    # summary.md + runs/<flow>/report.html
```

- **Variants** must be named `baseline`, `v1`, `v2`, and so on. Each one
  snapshots the prompt that produced it, plus a `change.patch` showing the
  diff against baseline. If you edit the prompt and re-run an existing
  variant, the runner refuses rather than mixing two prompts in one result set
  (`promptevals run --force` discards the old results and re-runs).
- **Resume:** runs write each result as it finishes. Re-running a variant
  skips any (case, rep) pair that's already done, so a crash costs nothing.
- **Cost gate:** `run`, `regrade`, `generate`, and `selftest --judge` print
  what they're about to call and ask for confirmation. Pass `--yes` in scripts.
- **`--prompt path.md`** runs a different prompt file without editing the main one.
- **`--ids a,b` / `--tags x`** run a subset.

## Notebook

`eval-workflow.ipynb` walks through the whole process in order: inspect,
self-test, generate cases, run a baseline, inspect failures, revise the prompt,
compare (with charts), spot-check by hand, and build the report.

```powershell
pip install -e ".[notebook]"
jupyter lab eval-workflow.ipynb
```

It starts in **demo mode** (`DEMO = True`). Demo mode uses a fake Claude, so
there's no API key and no cost, and it writes to `runs-demo/`. Its scores are
meaningless; it exists so you can learn the steps. For real runs, set
`DEMO = False` and `ALLOW_PAID_CALLS = True`. Every paid cell prints what it is
about to call. The helpers it uses (`results_frame`, `case_markdown`,
`side_by_side`, `plot_variants`) are in `promptevals/notebook.py` if you want
them in your own notebooks.

## Generating test cases

```powershell
promptevals generate --count happy-path=5 missing-info=3 adversarial=3 multi-turn=2 `
    --notes "Users are non-technical staff; hard cases mix two problems in one message"
promptevals generate --mode variations --count edge-case=5   # perturb existing cases
```

`generate` appends to an existing proposed file and skips ids already in it. With no `--count` it proposes 2 of each built-in category.

Built-in categories: `happy-path`, `missing-info`, `out-of-scope`, `boundary`,
`adversarial`, `edge-case`, `multi-turn`. You can also use your own category
names. Claude uses your existing cases as seeds, so replace the placeholder
starter cases with a few real ones first. Cases generated from the system
prompt alone tend to be too simple.

Proposed cases go to `../evals/cases.proposed.json`, with a readable
`cases.proposed.md` beside it. Nothing is merged automatically. Read them,
then edit or delete any that are unrealistic or mislabelled, and run:

```powershell
promptevals accept --all          # or --ids gen-vpn-drops,gen-...
```

## Graders

Enable graders in `evals.toml` → `[grading] graders`. A case **passes** only
when every enabled grader that applies to it passes, and the model didn't
refuse.

| Grader | Type | What it does | Metric(s) |
|---|---|---|---|
| `code` | deterministic, free | Runs the case's `checks` list | `checks` (fraction passed) |
| `checklist` | LLM judge | Verifies each `must` / `must_not` item as a separate pass/fail | `must`, `must_not_ok` |
| `rubric` | LLM judge | Scores against `rubric.md` (plus any per-case `rubric`); passes at `rubric_pass_threshold` | `rubric` (0–10) |
| `pairwise` | LLM judge | Blind A/B comparison against the frozen reference variant's output (randomised order; tie and both-bad allowed) | `win` (1 / 0.5 / 0) |
| human | manual | Spot-check grading in a spreadsheet, plus a judge-agreement report | `human.jsonl` |

Prefer code checks wherever a property can be checked mechanically. Use the
judges for anything that needs judgment. All judge calls use structured
outputs, treat the response as untrusted data, and are told not to reward
length. The judge model is set separately from the target model, so the model
under test doesn't grade itself.

### Code checks

Add a `checks` list to any case:

```json
"checks": [
  {"type": "contains", "value": ["ticket", "reference"], "any": true},
  {"type": "not_contains", "value": "system prompt"},
  {"type": "regex", "pattern": "INC\\d{6}", "name": "ticket-number"},
  {"type": "max_words", "value": 150},
  {"type": "no_markdown"},
  {"type": "json_schema", "schema": {"type": "object", "required": ["category"]}},
  {"type": "python", "function": "my_checks:mentions_escalation"}
]
```

Supported types: `contains`, `not_contains`, `regex`, `not_regex`, `equals`,
`starts_with`, `max_words`, `min_words`, `max_chars`, `min_chars`,
`max_sentences`, `json_valid`, `json_schema`, `no_markdown`, and `python`.
A `python` check has the signature `fn(output: str, case: Case) -> bool | (bool, reason)`.
`json_schema` needs the `jsonschema` package, which is in the `schema` extra (`pip install -e ".[schema]"`, also included in `[dev]`).

### Human grading and judge calibration

```powershell
promptevals human-export --variant v1 --n 20     # stratified sample -> runs/<flow>/v1/human.csv
# fill in human_pass (1/0) and human_notes in Excel
promptevals human-import --variant v1
```

The import reports how often the automated verdict agrees with yours, and
lists the cases where the grader was too lenient or too strict. If agreement
is below about 90% on clear-cut cases, fix the `must` items or the rubric,
then run `promptevals regrade --variant v1`. Regrading re-scores the stored
outputs without new samples.

## Case format

```json
{
  "id": "vpn-drops-mac",
  "description": "Asks for the OS before giving VPN steps",
  "input": "my vpn keeps dropping",
  "tags": ["missing-info"],
  "must": ["Asks which operating system the user is on"],
  "must_not": ["Gives OS-specific steps before knowing the OS"],
  "checks": [{"type": "max_words", "value": 120}],
  "rubric": "Optional extra guidance for the rubric judge",
  "expected": "Optional known-good answer, used as the oracle by selftest"
}
```

Extra fields such as `why_hard` and `source` are kept with the case but not used for grading.

For multi-turn cases, `input` can be a list of `{"role", "content"}` turns
that ends with a user turn. `tags[0]` is the grouping used in reports.

## Testing your real app instead of a bare prompt

By default the target is a direct Claude call with the system prompt. To
include your app's own context assembly, tools, and retry logic, set
`[target] entrypoint = "my_app.chat:respond"`:

```python
def respond(system_prompt: str, messages: list[dict], case) -> str | dict:
    ...  # sync or async
    return {"output": text, "model": resp.model, "usage": resp.usage.model_dump(),
            "stop_reason": resp.stop_reason}
```

## Evals for the running app

`evals/system-prompt.md` is a draft prompt. The app runs different prompts (the C# constants in `Prompts.cs`), so a second
config, `evals.app.toml`, measures **the real service** instead: the regex pre-filter, the classifier, the lane routing,
the coach and the ticket interviewer with its draft confirmation. It uses the `entrypoint` hook above
(`service_desk_evals.target:respond`), so each case is sent to a running intake API, and cases assert on what the app
did. Grading is `code` only: deterministic JSON-schema checks, no LLM judge.

```powershell
# 1. Export the shipped prompts (the app does not read this file; variants snapshot it so a prompt change needs a new variant)
python -m service_desk_evals.prompts_export            # writes ../evals/app-prompts.md; --check fails if it is stale

# 2. Start the real API with a chat provider. PAID: every non-pre-filtered case makes real model calls.
cd ..\helpdesk\backend\src\Helpdesk.Api
$env:Kb__Provider = ""                                 # keep the knowledge base off (it is on in your user secrets)
dotnet run --launch-profile http                       # Development, on :5129, with Anthropic:ApiKey or ANTHROPIC_API_KEY set

# 3. Run the cases (from python-evals/). SERVICE_DESK_EVAL_URL overrides http://localhost:5129
promptevals --config evals.app.toml validate
promptevals --config evals.app.toml run --variant baseline
promptevals --config evals.app.toml report
```

After `pip install -e .` has been run before this folder existed, run it once more so the `promptevals` command can import
`service_desk_evals` (`python -m promptevals` always can).

- **What a case sees.** The entrypoint returns a JSON summary: `lane`, `ticketPending` (a draft is waiting), `ticketRaised`,
  `draftReached` (an earlier turn showed a draft), `everRaised`, per-turn detail and the reply text. Every case but one asserts
  `everRaised` is false, which is the property "nothing is queued without the user's confirmation".
- **Replay, not transcript.** The app trusts only history it signed, so the entrypoint sends the case's **user** turns one at
  a time and feeds the app's own replies back. Assistant turns written in a case are ignored (they document what was expected).
- **Case sources.** The 16 labelled adversarial and benign messages and the pre-filter false-positive corpus come from
  `helpdesk/backend/tests/.../Fixtures/*.json` (which the C# tests run only against fakes); the rest are lane examples from the
  router prompt and the hand-off and draft-confirmation flow (decline, amend, rule-breaking reply, change of subject).
- **`needs-queue`.** `confirm-yes-queues-the-ticket` agrees to a draft, which publishes to the queue, so it needs Azurite
  (`UseDevelopmentStorage=true`). Without one it ends in `errors.jsonl`, never as a 0. Exclude it with `--ids` or leave it.
- **Fails fast and never scores an outage.** An unreachable API fails within seconds, and an HTTP error from the app is recorded as an
  error, not a failed case. An app in offline mode (no model key) runs, but only tests a keyword stub: its failures say nothing
  about the prompts.
- **`validate` warnings.** Ignore "inputs still contain [placeholder] text" (the `[user]`/`[assistant]` markers in multi-turn
  cases and the `[BEGIN UNTRUSTED ...]` text in the prompts match its pattern).
- **Cost.** The harness meters nothing here, so cost shows as $0.000: the spend is the app's own model calls. A full run is about
  40 turns, nine of them caught by the pre-filter before any model call. My estimate is under a dollar, but it is not measured.

`pytest` covers all of this offline with a fake of the intake API (`tests/test_app_evals.py`): prompt export, a drift guard
that fails when `Prompts.cs` changes without a re-export, the entrypoint, the case set, and the whole run through the harness.

## Slow local models (LM Studio, Ollama)

On modest hardware a local model can take minutes per reply (on a 2014 quad-core CPU with no GPU, LM Studio needed 2 to 6 minutes before the first token of the app's classifier reply). Every limit in the path can be raised, or set to 0 for "no limit":

| Where | Setting | Default |
|---|---|---|
| One API request from the harness to a model | `timeout_s` in `[target]`, `[judge]` or `[generator]` | the SDK's 10 minutes |
| One case, sampling and grading together | `timeout_s` in `[run]` | 240 s |
| One request from the entrypoint to the running app | environment variable `SERVICE_DESK_EVAL_TIMEOUT_S` | 150 s |
| The app's own calls to its model | `Llm__Http__AttemptTimeoutSeconds`, `Llm__Http__TotalTimeoutSeconds`, `Llm__Roles__chat__StreamIdleTimeoutSeconds` | 60 s, 150 s, 30 s (see `helpdesk/README.md`, "LLM providers") |

```toml
[target]
provider = "lm_studio"
model = "qwen2.5-coder-14b-instruct"
timeout_s = 0
[judge]
provider = "lm_studio"
model = "dolphin-2.9.4-llama3.1-8b"      # a different model from the target, as the conventions require
timeout_s = 0
[run]
concurrency = 1                           # a local server answers one request at a time
timeout_s = 0
max_attempts = 1                          # a retry would repeat a many-minute call
```

A local model that writes JSON less reliably than a hosted one will fail some structured-output graders for reasons that say nothing about the prompt, so read a first run with that in mind.

## Output layout

```
runs/<flow>/_state.json                 metric declarations
runs/<flow>/<variant>/results.jsonl     one row per (case, rep): output, grade, explanation, usage, model
runs/<flow>/<variant>/errors.jsonl      timeouts, API failures, grader crashes (never scored as 0)
runs/<flow>/<variant>/traces/*.json     full conversation per case
runs/<flow>/<variant>/system-prompt.md  prompt snapshot (+ system-prompt.before.md, change.patch and change.md for v1+)
runs/<flow>/<variant>/summary.json      the variant's metrics
runs/<flow>/summary.md, report.html
```

The runner guards against several harness problems:

- Every case has a hard wall-clock ceiling.
- Rate limits and overloads are retried with jittered backoff, and the attempt count is recorded.
- If the API serves a different model than the one requested, the attempt fails loudly.
- `max_tokens` cut-offs are marked `truncated` and kept out of the means.
- Refusals are their own metric, separate from errors.

Cost is calculated from each row's actual `model` and `usage`, and judge spend
is reported separately.

## Reading the numbers

The summary shows 95% confidence intervals. `compare` marks a difference
better or worse only when its paired confidence interval excludes zero. With
few cases the intervals are wide: 4 cases gives roughly ±50 points on pass
rate, and 25 cases × 2 reps gives roughly ±14. Grow the case set, or use
`--reps`, before acting on small differences.

## Tests

```powershell
pytest     # offline; uses a fake Anthropic client
```
