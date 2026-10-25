# AGENTS.md (helpdesk)

This file provides vendor-neutral guidance to coding agents working in `helpdesk/` and its subdirectories, in addition to the repository-wide guidance in `../AGENTS.md`.

## What this is

`helpdesk/` is a multi-tenant AI triage service: a .NET 10 minimal API (`backend/`) plus a React 19 + Vite + Tailwind chat UI (`frontend/`). A message is classified by Claude into a lane (`POLICY_VIOLATION`, `PUBLIC_TECH`, `PUBLIC_SEARCH`, `PRIVATE_CORP`, `NO_MATCH`) and routed. `README.md` is the full reference for configuration keys and design notes; `REVIEW.md` is an enterprise/SaaS review (its test counts are stale). The lane prompts are C# constants in `Infrastructure/Ai/Prompts.cs`.

Why things are the way they are is in `../docs/design-decisions.md` (hand-off, DLP, vendors, tiers, attachments). The git repository is the parent folder (`service-desk/`); there is no CI pipeline yet. Backend solution is `backend/Helpdesk.slnx`.

## Commands

```powershell
# Run everything locally (API on :5129 + Vite on :5173; offline mode, no keys needed in Development)
cd frontend; npm install; npm run debug

cd backend; dotnet test                                   # all backend tests
dotnet test --filter "FullyQualifiedName~ConversationSigningTests"   # one class / name fragment
cd frontend; npm test                                      # vitest
npx vitest run src/lib/auth.test.ts                        # one frontend file
cd frontend; npm run lint; npm run typecheck
dotnet test ../ui-tests/ServiceDesk.UiTests                # ALL browser tests (C#, xUnit, Playwright, axe), kept apart from the unit tests; needs Node and Chrome or Edge
cd frontend; npm run build:api                             # builds SPA into backend/src/Helpdesk.Api/wwwroot
```

- `dotnet test` fails with MSB3027 file-lock errors while `dotnet run`/`dotnet watch` has the API running. Stop it, or build elsewhere: `dotnet test --artifacts-path $env:TEMP\hd-artifacts`.
- Production frontend builds fail unless `VITE_ENTRA_CLIENT_ID`, `VITE_ENTRA_AUTHORITY` and `VITE_API_SCOPE` are set (use `--mode development` for local experiments).
- Development with no Anthropic key runs in offline mode (keyword router, canned replies, dev-auth headers `X-Dev-Tenant`/`X-Dev-User`). `PRIVATE_CORP` writes to Azurite; without it those requests return 503.

## Architecture

Projects: `Shared` (contracts/events) → `Core` (interfaces, `Telemetry`) → `Infrastructure` (Anthropic client, search providers, queues, DLP, JWT tenant provider) → `Api` (endpoints, SignalR hub, hosting). Tests: `tests/Helpdesk.Api.Tests` (`WebApplicationFactory`, fake Anthropic/queue clients), `tests/Helpdesk.Llm.Tests`, (unit and API tests only). Browser tests are a separate project, `../ui-tests/ServiceDesk.UiTests` (Playwright for .NET: a real browser against the real API and the built SPA, with scripted model/search/queue; see `../ui-tests/README.md`), and are not in `Helpdesk.slnx`. The SPA keeps `data-testid` hooks and ARIA labels the page objects rely on: change them together.

**One pipeline, two entry points.** `Chat/ChatPipeline.RunAsync` is shared by the SignalR path (`ChatHub` → `ChatJobQueue` → `ChatJobProcessor` background service → pipeline, events pushed to a per-user group) and the REST fallback (`POST /api/v1/chat/message`). Order is fixed and fail-closed: verify signed history → redact for `Model` → classify (regex prefilter in `GuardrailPolicyPrefilter`, then Claude with JSON-schema output) → route by lane. Nothing (inference, search, queue) runs before classification succeeds. The `PRIVATE_CORP` branch (`ChatPipeline.HandOffAsync`) lets the model lead a conversational hand-off: `ITicketInterviewer` returns either the next question or a ticket (title, summary, recommended action), and the pipeline shows a returned ticket to the user as a draft. It calls `ITicketQueuePublisher` only after the user confirms it: a draft is "pending" when the last signed assistant message ends with `Prompts.TicketConfirmPrompt`, and the model's `userConfirmed` counts only then (D2.6). There is no form or escalation endpoint; the policy-redirect behaviour lives in the `ComplianceCoach` and `TicketInterviewer` prompts.

**Cross-cutting rules that span files:**
- *Tenancy:* every endpoint requires the `tenant_id` claim (fallback authorization policy). Tenant/user ids must not contain `:` because they are joined with `JwtTenantProvider.KeySeparator` into group names and rate-limit keys; use that constant when building composite keys.
- *Hub auth:* the hub never accepts a JWT. The client buys a single-use ticket from `POST /api/v1/chat/ticket` (`Security/HubTickets.cs`, in-memory) and passes it as `access_token`. Dev auth (`DevAuthHandler`) is registered only when environment is Development and `Auth:UseDevAuth` is true.
- *Stateless conversations:* the client stores the transcript; `ConversationSigner` HMAC-signs each message in a chain, and only the longest valid chain is trusted. `sig`/`userSig`/`assistantSig` must round-trip unchanged through UI and API. Signing keys come from `Conversation:Keys`.
- *DLP:* `SensitiveDataRedactor` + `DlpPolicy` redact per destination (`Model`, `Search`, `Queue`) and optionally per tenant. Anything new that sends text off-service must go through it.
- *Untrusted text:* user/search content is embedded via `PromptInput.Wrap` with per-value unpredictable boundary tokens; content is preserved verbatim and prompts treat it as data.
- *Model tiers:* every `ChatRequest` carries a `ModelTier` (Fast, Standard, Deep) and `TierRoutingChatProvider` sends it to that tier's model within the one vendor (`LlmProviderOptions.ForTier`, vendor defaults in `LlmTierDefaults`). The classifier is Fast; the coach and interviewer are Standard; `InferenceRoutePolicy` (fixed, no config) picks the tier for answers from the classifier's complexity and the estimated input size. See README "Model tiers and routing".
- *Rate limits (429):* `RateLimitedSender` (in `Helpdesk.Llm`) owns 429/`Retry-After` handling; the resilience handler is configured not to retry 429. Cancellation tokens are threaded through all calls (`CA2016` is a warning).
- *Message size:* hub messages are limited to 32 KB; the UI splits larger requests with `SendStreamed` (`frontend/src/lib/chatRequest.ts`), and `ChatRequestValidator.MaxPartBytes` bounds `Chat:MaxReceiveMessageBytes`.
- *Config validation:* `Status/ConfigurationValidator` runs at startup (fails the app outside Development) and backs `GET /status`. New config keys with constraints belong there.
- *Logs:* request logging records the path only (the hub URL carries a ticket); don't log query strings, tokens or message text.

**Frontend:** `lib/auth.ts` (MSAL, redirects at most once per session, then a "session expired" banner), `lib/chatHub.ts` + `lib/hubTicket.ts` (WebSocket-only SignalR, falls back to REST), `components/` for the chat bubbles. The bubble shows a "Sent to the service desk" note when the reply's `metadata.ticketRaised` is `true`, and "Send to the service desk" / "Don't send" buttons (latest message only) when `metadata.ticketPending` is `true`.

## Conventions

- Adversarial guardrail cases live in `backend/tests/Helpdesk.Api.Tests/Fixtures/*.json`; add cases there rather than deleting any that caught a regression.
- Defaults the code assumes a single API instance (in-memory queue, limiters, tickets, SignalR groups); don't introduce more process-local state without noting it in README "Known gaps".
