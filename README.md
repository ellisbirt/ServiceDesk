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
| [`ui-tests/`](ui-tests/) | All the browser tests, in one .NET project (Playwright for .NET, with axe accessibility checks), covering both apps and kept apart from the unit tests. |
| [`docs/`](docs/) | [`design-decisions.md`](docs/design-decisions.md): what I decided, why, what it costs, and the known gaps. Start here to see how I think about the design. |

## Scope

The charter is small: classify an IT request, resolve what can safely be resolved (knowledge base, web search), coach when a request can't be done as asked, and otherwise gather details in conversation and hand a confirmed ticket to a human ([D1.1 to D1.3](docs/design-decisions.md#1-product-shape)). Not everything here serves that equally, so each area is labelled:

| Tier | What it covers |
|---|---|
| **Core** | The classify, route and hand-off pipeline (lanes, coach, ticket interviewer with draft confirmation, ticket event), data redaction (`Helpdesk.Dlp`), signed stateless conversation history, and the prompt eval harness (`evals/`, `python-evals/`). |
| **Supporting** | Things that make the core usable and safe, kept proportionate: model tiers and refusal handling, knowledge-base retrieval, web search (Tavily or Claude's native search), streaming and the chat job lifecycle, startup config validation, and the unit, API and browser tests. |
| **Extension** | Deliberate additions beyond the charter: file attachments ([D8](docs/design-decisions.md#8-attachments)) and AgentDesk, the downstream agent console (secondary; it shares the `Llm`, `Dlp` and `Shared` libraries with `helpdesk/`). |
| **Optional / experimental** | Implemented and covered by offline tests, but never run against a live service, and labelled **experimental** in [`helpdesk/README.md`](helpdesk/README.md): the non-Anthropic LLM vendors (OpenAI, Azure OpenAI, Grok and Ollama; LM Studio has been checked live for chat and embeddings) and the search providers other than Tavily and Claude (Brave, Serper, Google, SearXNG). They are kept on purpose, as a working example of an LLM-agnostic design. Per-tenant DLP policy and the second queue backend are also in this tier. |

New work needs to serve a line of the charter, or be recorded as an extension with its cost ([D1.5 and D1.6](docs/design-decisions.md#1-product-shape)).

## Where to start

- To see the design and trade-offs, read [`docs/design-decisions.md`](docs/design-decisions.md).
- To run the intake service offline (no API keys, canned replies), see [`helpdesk/README.md`](helpdesk/README.md).
- To run the prompt evals, see [`python-evals/README.md`](python-evals/README.md). Offline checks are free, and real runs call a paid API.
- Coding-agent guidance lives in [`AGENTS.md`](AGENTS.md), with directory-specific commands and architecture notes in [`helpdesk/AGENTS.md`](helpdesk/AGENTS.md) and [`agentdesk/AGENTS.md`](agentdesk/AGENTS.md). These files use the vendor-neutral AGENTS.md pattern; agents that do not discover it automatically should be pointed to the applicable files.

## Status

Work in progress, built mostly on Windows (the documented commands are PowerShell). Known gaps are listed in [`docs/design-decisions.md`](docs/design-decisions.md#11-known-gaps-and-rejected-alternatives), and they include single-instance state, no malware scanning on attachments and no per-tenant cost controls. There's no CI pipeline yet.

## Licence

MIT. See [`LICENSE`](LICENSE).
