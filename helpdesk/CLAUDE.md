# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`helpdesk/` is a multi-tenant AI triage service: a .NET 10 minimal API (`backend/`) plus a React 19 + Vite + Tailwind chat UI (`frontend/`). A message is classified by Claude into a lane (`POLICY_VIOLATION`, `PUBLIC_TECH`, `PUBLIC_SEARCH`, `PRIVATE_CORP`, `NO_MATCH`) and routed. `README.md` is the full reference for configuration keys and design notes; `REVIEW.md` is an enterprise/SaaS review (its test counts are stale). The lane prompts are C# constants in `Infrastructure/Ai/Prompts.cs`.

Not a git repo. Backend solution is `backend/Helpdesk.slnx`.

## Commands

```powershell
# Run everything locally (API on :5129 + Vite on :5173; offline mode, no keys needed in Development)
cd frontend; npm install; npm run debug

cd backend; dotnet test                                   # all backend tests
dotnet test --filter "FullyQualifiedName~ConversationSigningTests"   # one class / name fragment
cd frontend; npm test                                      # vitest
npx vitest run src/lib/auth.test.ts                        # one frontend file
cd frontend; npm run lint; npm run typecheck
cd frontend; npm run test:e2e                              # Playwright + axe, mocked API
cd frontend; npm run build:api                             # builds SPA into backend/src/Helpdesk.Api/wwwroot
```

- `dotnet test` fails with MSB3027 file-lock errors while `dotnet run`/`dotnet watch` has the API running. Stop it, or build elsewhere: `dotnet test --artifacts-path $env:TEMP\hd-artifacts`.
- Production frontend builds fail unless `VITE_ENTRA_CLIENT_ID`, `VITE_ENTRA_AUTHORITY` and `VITE_API_SCOPE` are set (use `--mode development` for local experiments).
- Development with no Anthropic key runs in offline mode (keyword router, canned replies, dev-auth headers `X-Dev-Tenant`/`X-Dev-User`). `PRIVATE_CORP` writes to Azurite; without it those requests return 503.

## Architecture

Projects: `Shared` (contracts/events) → `Core` (interfaces, `Telemetry`) → `Infrastructure` (Anthropic client, search providers, queues, DLP, JWT tenant provider) → `Api` (endpoints, SignalR hub, hosting). Tests are one project, `tests/Helpdesk.Api.Tests`, using `WebApplicationFactory` and fake Anthropic/queue clients.

**One pipeline, two entry points.** `Chat/ChatPipeline.RunAsync` is shared by the SignalR path (`ChatHub` → `ChatJobQueue` → `ChatJobProcessor` background service → pipeline, events pushed to a per-user group) and the REST fallback (`POST /api/v1/chat/message`). Order is fixed and fail-closed: verify signed history → redact for `Model` → classify (regex prefilter in `GuardrailPolicyPrefilter`, then Claude with JSON-schema output) → route by lane. Nothing (inference, search, queue) runs before classification succeeds. The `PRIVATE_CORP` branch (`ChatPipeline.HandOffAsync`) lets the model lead a conversational hand-off: `ITicketInterviewer` returns either the next question or a ticket (title, summary, recommended action), and only when it raises one does the pipeline call `ITicketQueuePublisher`. There is no form or escalation endpoint; the policy-redirect behaviour lives in the `ComplianceCoach` and `TicketInterviewer` prompts.

**Cross-cutting rules that span files:**
- *Tenancy:* every endpoint requires the `tenant_id` claim (fallback authorization policy). Tenant/user ids must not contain `:` because they are joined with `JwtTenantProvider.KeySeparator` into group names and rate-limit keys; use that constant when building composite keys.
- *Hub auth:* the hub never accepts a JWT. The client buys a single-use ticket from `POST /api/v1/chat/ticket` (`Security/HubTickets.cs`, in-memory) and passes it as `access_token`. Dev auth (`DevAuthHandler`) is registered only when environment is Development and `Auth:UseDevAuth` is true.
- *Stateless conversations:* the client stores the transcript; `ConversationSigner` HMAC-signs each message in a chain, and only the longest valid chain is trusted. `sig`/`userSig`/`assistantSig` must round-trip unchanged through UI and API. Signing keys come from `Conversation:Keys`.
- *DLP:* `SensitiveDataRedactor` + `DlpPolicy` redact per destination (`Model`, `Search`, `Queue`) and optionally per tenant. Anything new that sends text off-service must go through it.
- *Untrusted text:* user/search content is embedded via `PromptInput.Wrap` with per-value unpredictable boundary tokens; content is preserved verbatim and prompts treat it as data.
- *Rate limits (429):* `AnthropicClient` owns 429/`Retry-After` handling; the resilience handler is configured not to retry 429. Cancellation tokens are threaded through all calls (`CA2016` is a warning).
- *Message size:* hub messages are limited to 32 KB; the UI splits larger requests with `SendStreamed` (`frontend/src/lib/chatRequest.ts`), and `ChatRequestValidator.MaxPartBytes` bounds `Chat:MaxReceiveMessageBytes`.
- *Config validation:* `Status/ConfigurationValidator` runs at startup (fails the app outside Development) and backs `GET /status`. New config keys with constraints belong there.
- *Logs:* request logging records the path only (the hub URL carries a ticket); don't log query strings, tokens or message text.

**Frontend:** `lib/auth.ts` (MSAL, redirects at most once per session, then a "session expired" banner), `lib/chatHub.ts` + `lib/hubTicket.ts` (WebSocket-only SignalR, falls back to REST), `components/` for the chat bubbles. The bubble shows a "Sent to the service desk" note when the reply's `metadata.ticketRaised` is `true`.

## Conventions

- Adversarial guardrail cases live in `backend/tests/Helpdesk.Api.Tests/Fixtures/*.json`; add cases there rather than deleting any that caught a regression.
- Defaults the code assumes a single API instance (in-memory queue, limiters, tickets, SignalR groups); don't introduce more process-local state without noting it in README "Known gaps".
