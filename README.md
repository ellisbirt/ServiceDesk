# service-desk

A personal project: an AI IT service-desk assistant, and the tooling I use to measure and improve its prompts. It isn't a product or an employer's code, and it isn't production-ready. It's where I explore how to build an LLM-backed support workflow with sensible guardrails, data protection and testing.

An employee describes an IT problem in a chat. The assistant classifies the request, answers what it safely can, coaches the user when a request can't be done as asked (for example, someone else's password), and otherwise gathers details in conversation and, once the user confirms a draft, hands a ticket to a human.

I built it with AI assistance (Claude Code), and the commit trailers say so. The design decisions and their reasons are mine to explain, and they're recorded in [`docs/design-decisions.md`](docs/design-decisions.md).

## What's here

| Folder | What it is |
|---|---|
| [`helpdesk/`](helpdesk/) | The intake service: a .NET 10 API plus a React UI. It classifies each message into a lane (policy violation, public tech, public search, private corp, no match), answers or routes it, and publishes a ticket event after the user confirms a draft. Shared libraries cover pluggable LLM vendors (`Helpdesk.Llm`), data redaction (`Helpdesk.Dlp`) and the event contract (`Helpdesk.Shared`). |
| [`agentdesk/`](agentdesk/) | A downstream agent console: a .NET 10 API plus a React UI that stores tickets in Cosmos DB and attachments in Blob Storage, with a knowledge base and LLM reply assist. Secondary to `helpdesk/`. |
| [`evals/`](evals/) | The source of truth for the assistant prompt and its tests: `system-prompt.md`, `cases.json` and a scoring `rubric.md`. |
| [`python-evals/`](python-evals/) | `promptevals`, a Python CLI that runs the cases against a prompt variant, grades the results and compares variants. |
| [`docs/`](docs/) | [`design-decisions.md`](docs/design-decisions.md): what I decided, why, what it costs, and the known gaps. Start here to see how I think about the design. |

## Where to start

- To see the design and trade-offs, read [`docs/design-decisions.md`](docs/design-decisions.md).
- To run the intake service offline (no API keys, canned replies), see [`helpdesk/README.md`](helpdesk/README.md).
- To run the prompt evals, see [`python-evals/README.md`](python-evals/README.md). Offline checks are free, and real runs call a paid API.
- Each part has a `CLAUDE.md` with its commands and architecture notes.

## Status

Work in progress, built mostly on Windows (the documented commands are PowerShell). Known gaps are listed in [`docs/design-decisions.md`](docs/design-decisions.md#11-known-gaps-and-rejected-alternatives), and they include single-instance state, no malware scanning on attachments and no per-tenant cost controls. There's no CI pipeline yet.

## Licence

MIT. See [`LICENSE`](LICENSE).
