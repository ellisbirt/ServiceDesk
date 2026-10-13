# CLAUDE.md (agentdesk)

AgentDesk is the agent console and ticket backend that continues the flow after `../helpdesk` (the intake). `README.md` is the full guide; read it before changing configuration, auth or the KB flow.

## Commands (PowerShell, no `&&`)

```powershell
cd agentdesk\backend; dotnet test AgentDesk.slnx              # offline: in-memory stores, offline LLM provider
cd agentdesk\frontend; npm run lint; npm run typecheck; npm test
cd agentdesk\frontend; npm run test:e2e                       # Playwright, mocked API on :5175
cd agentdesk\frontend; npm run debug                          # API :5139 (dotnet watch) + Vite :5174
```

## Architecture

- `AgentDesk.Core`: domain documents (`Domain/`), vocabularies, rules (state machine, priority matrix, SLA, numbering), services (`TicketService`, `KbService`, `AttachmentService`) and the abstractions they depend on (`Abstractions/Contracts.cs`). It has no Azure SDK references.
- `AgentDesk.Infrastructure`:
  - Cosmos and in-memory repositories (`Storage:Provider`)
  - Blob stores (`Blobs:Provider`)
  - Intake consumers for Storage Queue and Service Bus (`Intake:Provider`)
  - `Ai/`: `TicketAssistService` and `KbEmbeddingService`, plus the embedding sweep

  All of it is wired in `DependencyInjection.cs`.
- `AgentDesk.Api`:
  - Minimal-API endpoint groups in `Endpoints/`
  - Auth modes in `Security/`: dev, anonymous or jwt
  - The SignalR `TicketsHub`, with single-use hub tickets
  - `/status` configuration checks, Problem Details, and Serilog/OTel
- Shared from `../helpdesk/backend/src`:
  - `Helpdesk.Llm`: pluggable chat and embedding providers selected per role (`Llm:Roles:assist`, `Llm:Roles:embedding`).
  - `Helpdesk.Dlp`: redaction before any egress, with `Model` and `Embedding` destinations.
  - `Helpdesk.Shared`: `ProcessPrivateCorpTicketEvent` and `PromptInput`.

  Changes there affect the intake too, so run its tests as well.
- Frontend: React 19, Vite, Tailwind 4 and TanStack Query.
  - `src/lib/api.ts` is the only fetch layer. It adds dev headers or the MSAL token.
  - `src/lib/hub.ts` handles SignalR.
  - The screens live under `src/pages/` and `src/components/`.

## Conventions

- Every document is partitioned by `tenantId`. Every repository call takes the tenant from the authenticated `CurrentUser`, never from the request body.
- AI assist only ever returns drafts. Nothing is persisted until the agent sends or saves.
- Ticket and KB text going to a model is untrusted:
  - Redact it first.
  - Wrap it with `PromptInput.Wrap`.
  - Use structured output and validate the result.
  - Bump the prompt version constant in `TicketAssistService` (for example `draft-reply/1`) when you change a prompt.
- Do not add RAG or vector queries without an explicit request. Embeddings are stored for later use only.
- Backend tests must stay offline. Use the in-memory stores, the `offline` provider or a fake `ILlmProviderRouter`.
- Don't run anything that calls a paid LLM API without the user's go-ahead.
