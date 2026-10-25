# Enterprise / SaaS review: Helpdesk Intake (triage and ingestion)

Reviewed 2026-10-12. Scope: `backend/` (.NET 10) and `frontend/` (React 19), **as the intake component of a larger system**. This revision re-scopes the earlier review: findings are split into what this component must fix, what it must emit or enforce for the rest of the platform, and what belongs to other components.

> **Update 2026-10-25.** This is a snapshot from 2026-10-12 and the counts below are from then. Since then: the code is under git (the parent `service-desk/` repo) but there is still no CI (finding 1); the access-request card is gone (finding 2); the event carries `schemaVersion` (finding 3); history reaches inference as well as the classifier (finding 13); and a ticket is now queued only after the user confirms a draft (see `docs/design-decisions.md` D2.6). Current test counts are 641 API, 43 library and 35 browser tests, and 193 frontend tests. Findings not mentioned here have not been re-checked.

**Verified in this review:** backend 344 tests pass, frontend 55 tests pass, lint and typecheck clean, `dotnet list package --vulnerable` clean, `npm audit --omit=dev` clean (two moderate dev-only findings via `@tailwindcss/typography`). Not verified: a real browser against a running server (split upload, ticket connection), the prompts against the real model, and anything about the other components or the deployment.

## Role of this component (assumed from the code and README)

Intake is the front door. It authenticates the user, redacts, classifies with Claude, answers the public lanes itself, declines or coaches the policy lane, and hands `PRIVATE_CORP` requests to a queue (`ProcessPrivateCorpTicketEvent`). Per the README, the Service Bus worker, Cosmos DB RAG and PIM integration are outside it. I have not seen those components, so the boundary below is an assumption to confirm.

| Concern | Owner (assumed) | Intake's obligation |
|---|---|---|
| Classification, public answers, policy coaching | **Intake** | Correct, evaluated, cost-bounded |
| Authentication, tenant claim | Intake enforces; identity platform issues | Reject anything without a valid, trusted claim |
| Redaction before egress | **Intake** | Complete for the destinations it controls |
| Ticket creation, RAG, PIM workflow | Downstream | Hand off through a versioned, idempotent contract |
| Tenant registry, entitlements, billing | Platform | Consume tenant status; meter usage |
| Long-term audit store, retention, e-discovery | Platform | Emit structured audit events |
| Conversation record, user feedback | Undecided | Decide the owner (finding D3) |

## Verdict

The code is well built: classification fails closed, input is treated as untrusted, history is signed, hub credentials stay out of URLs, and the job lifecycle is handled carefully. For an **intake** service the gaps are delivery, an unproven integration contract with downstream, real-model evaluation of the prompts, and availability of the front door. Several earlier "blockers" (tenant registry, billing, audit storage, ticketing) are platform concerns and are downgraded to obligations that intake must meet at its edge.

Status key: **Open**, **Done** (fixed and tested earlier, see README), **Dependency** (needs another component or a decision).

## Summary

| # | Finding | Severity | Scope | Status |
|---|---|---|---|---|
| 1 | No source control, CI or delivery pipeline | Blocker | Intake | Partial (git added; no CI) |
| 2 | Access-request card reports a request that was never made | Blocker | Intake UI, handoff to PIM | Done (card removed) |
| 3 | Handoff contract to downstream is undefined and under-specified | Blocker | Shared interface | Partial (`schemaVersion` added) |
| 4 | No real-model evaluation of the production prompts in this project | High | Intake | Open |
| 5 | Front door is a single instance with in-memory state | High | Intake | Open |
| 6 | Pre-filter exemption can switch off a block | High | Intake | Done |
| 7 | Escaped angle brackets damage code questions | High | Intake | Done |
| 8 | Per-tenant spend and fairness for intake's own model calls | High | Intake, with platform input | Partial |
| 9 | Audit events are not emitted durably | High | Emit here, store elsewhere | Open |
| 10 | Vendor and data-governance controls for egress | High | Intake | Open |
| 11 | Tenant status is not consulted | Medium | Platform dependency | Dependency |
| 12 | Readiness does not reflect the dependencies intake needs | Medium | Intake | Open |
| 13 | Conversation context, persistence and record | Medium | Undecided | Dependency |
| 14 | Medium and low items | Medium and low | Mixed | Open |
| - | Earlier findings (message size, hub tickets, composite keys, config validation, HTTP hardening, observability, job lifecycle, signed history, DLP, pre-filter, frontend) | - | Intake | Done |

## Blockers

### 1. No delivery pipeline and no source control (Partial: git added, no CI)
No git repository, CI, container image, infrastructure as code, central package management (`Directory.Packages.props`), lock files, `TreatWarningsAsErrors`, analyzers beyond `CA2016`, or SBOM. `backend/.gitignore` lacks `.vs/`, `*.user` and `TestResults/`; `frontend/tsconfig.tsbuildinfo` should be ignored. Everything else in this review is unprotected until this exists. If the wider platform has a shared pipeline, adopt it rather than build one, but the repo still needs to be under source control first.
**Next:** git, then a build and test gate with dependency and secret scanning. Run the model evaluation (finding 4) in the same pipeline.

### 2. The access-request card reports a request that was never made (Done: the card was removed)
*Update:* `AccessRequestCard` no longer exists. After a policy refusal the UI offers a "Start an access request" button that sends an ordinary message and continues in the chat, ending in a confirmed ticket. The text below describes the state at review time.
`AccessRequestCard.tsx:7-8` hard-codes a SharePoint target and two approvers, and on submit only sets local state and shows "Request recorded for approval by …" (line 26). The copy claims PIM integration. A blocked user can believe an approval is in flight.
**Re-scoped:** PIM is downstream, so intake should not own the workflow, but it owns the promise it makes. Until the handoff exists, remove the card or show a plain instruction to use the real access process.
**Next:** when wired, intake emits an access-request event through the contract in finding 3, with the target and approver choices supplied by the server (never hard-coded in the UI), and only confirms after the handoff is accepted.

### 3. The handoff contract to downstream is undefined (Partial, was a "smaller point")
*Update:* `ProcessPrivateCorpTicketEvent` now carries `SchemaVersion` (currently 1, additive changes keep it). The other points below have not been re-checked.
This is now the main integration surface, so it moves up from Low. `ProcessPrivateCorpTicketEvent` is the only thing intake gives the rest of the system, and it has no stated guarantees.
- **No schema version.** Downstream cannot evolve safely, and intake cannot change fields without breaking consumers.
- **Delivery and idempotency undefined.** Duplicate detection is not confirmed on the queue (`MessageId` is the event id; Service Bus duplicate detection must be enabled and its window agreed). Storage Queue has none, and at-least-once delivery means duplicate tickets. Dead-letter handling and poison messages are the consumer's problem, but the contract must say so.
- **The payload is untrusted text.** `SanitizedPayload` is user text, redacted by pattern only, on a lane chosen by a model that can be wrong or manipulated. A downstream RAG or ticket worker must treat it as data and never as instructions. State this in the contract and add it to the downstream threat model. The field name `SanitizedPayload` overstates what has been done.
- **Not enough context travels with it.** The event has tenant, user, conversation id, correlation id and text, but no lane confidence or reasoning, no classifier or prompt version, no redaction counts, no pre-filter flags, and no history. Downstream cannot tell a high-confidence escalation from a guess, and cannot fetch the conversation (finding 13).
- **Redaction versus need.** The `Queue` policy keeps email and phone by default. Confirm what downstream needs and what its own retention allows.
- **No acknowledgement path.** The reply tells the user "You'll receive a ticket reference by email". Intake cannot verify that. If the worker fails, the user waits for a message that never comes.
- **Ordering and partitioning.** Service Bus `PartitionKey` only works on partitioning-enabled queues and cannot differ from `SessionId`; the Storage Queue path has no tenant partition at all. Agree what consumers may assume.
**Next:** write the contract as a versioned schema (JSON Schema or a shared package), add contract tests on both sides, include `schemaVersion`, `promptVersion`, `lane`, and a reply-confirmation or status callback, and document delivery semantics.

## High

### 4. Nothing in this project measures the prompts against a real model (Open)
The five production prompts live as C# constants in `Infrastructure/Ai/Prompts.cs` (router, coach, PublicTech, PublicSearch, PublicSearchNative). The adversarial and false-positive fixtures in `tests/Helpdesk.Api.Tests/Fixtures` run only against fakes, so no test shows how the real model routes or answers. Because deterministic rules now look at the current message only, multi-turn escalation depends on the model alone. The README mentions "the evals" and says to run them before rollout, but no evaluation harness is part of this project, so what they cover, and whether they use these prompts, is unverified.
**Next:** keep the prompts in versioned files, add an evaluation run (lane routing, adversarial multi-turn, answer quality for code) that calls the real model, pin the model per environment, and gate releases on it. If an external harness is meant to cover this, state in the README which prompts it reads and where they come from.

### 5. The front door is a single instance with in-memory state (Open)
The job queue, per-conversation lock, rate limiters, hub ticket store and SignalR groups live in process memory. More than one instance sends events to the wrong node, limits are per node, and each deploy drops queued jobs. For the edge of a larger system this blocks autoscaling, rolling deploys and HA. The durable handoff to downstream (the queue write) is the one part that is sound.
**Next:** a SignalR backplane or Azure SignalR Service, shared tickets and limits, and either a durable job queue or a decision to make the pipeline run inline on the hub invocation, since the work is mostly waiting on the model. Until then run one replica and accept dropped jobs on deploy.

### 6. The pre-filter exemption can switch off a block (Done)
The unit-test exception is now anchored to an entire standalone "skip authentication in my unit tests" question, so adding that phrase after a security-control evasion no longer suppresses the block. Disabling or deactivating MFA is now detected as evasion, including on the user's own account. Regression cases cover both attack patterns; the standalone unit-test question remains in the false-positive corpus.

### 7. Escaped angle brackets damage code questions (Done)
`PromptInput.Wrap` now preserves input verbatim inside per-value wrappers with unpredictable boundary tokens. The prompts describe this boundary format, and regression tests assert that C# generics, comparisons, markup, and wrapper-like content remain unchanged inside the data boundary. A real-model answer-quality evaluation remains part of finding 4.

### 8. Per-tenant spend and fairness for intake's own model calls (Partial)
**Done:** `Chat:MaxJobsPerTenant` caps in-flight requests (hub and REST), per-user rate limits exist, and token use and estimated cost are on spans and logs per tenant.
**Open:** all tenants share one Anthropic key and one job-slot pool; there is no token or cost budget, no fair scheduling (FIFO across tenants), no per-tenant override of the cap, and every limit is per instance. Cost metrics deliberately omit tenant, so per-tenant dashboards need the log or trace backend.
**Re-scoped:** the budget policy (plans, quotas, billing) comes from the platform, but enforcement of it on intake's calls is intake's job.
**Next:** per-tenant token budgets enforced at admission using limits supplied by the platform (finding 11), fair scheduling, and a per-tenant concurrency override. Limits should be shared across instances once finding 5 is done.

### 9. Audit events are not emitted durably (Open)
**Re-scoped:** storage, retention and query belong to the platform; **emitting** the event belongs here. Today, policy blocks, escalations and history-signature failures are logs and counters only, which can be sampled or dropped. The pre-filter block log has no user id, and model-decided blocks are only an Information log of the lane.
**Next:** emit a structured audit event per decision (tenant, user, conversation, lane, rule or classifier version, redaction categories, message hash, correlation id, outcome) to the platform's audit channel with at-least-once delivery. Do not include message text.

### 10. Vendor and data-governance controls for egress (Open)
Redacted text goes to Anthropic and to one of five search vendors. There is no per-tenant control over lanes or providers, no region pinning, no retention or zero-data-retention setting, and no record of data-processing terms. DLP is pattern-based, outbound only, and misses free text ("my password is hunter2"), names and addresses; detectors are English and US/UK/IBAN oriented. The default Tavily provider is a free tier with no contractual terms. Search receives the user's whole redacted message as the query.
**Next:** decide the approved vendor list with the platform's security team, make lane and provider selection tenant-configurable, consider a managed PII service for free text, and send a derived search query instead of the raw message.

## Medium

### 11. Tenant status is not consulted (Dependency)
Intake trusts any validly signed token carrying a `tenant_id` claim. There is no tenant registry, suspension, plan or kill switch. **Re-scoped:** the registry is a platform concern. Intake should consult a tenant-status source (cached, fail-closed) and refuse suspended or unknown tenants. Also document that the `tenant_id` claim comes from the token-issuance extension and cannot be set by the user, and add a test that proves the validator rejects tokens from other issuers.

### 12. Readiness does not reflect what intake needs (Open)
`/readyz` returns 200 while the in-process worker runs, and does not touch the queue, Anthropic or search. The only success path for `PRIVATE_CORP` is the queue, so intake can report ready while the escalation lane fails for everyone. `/status` validates configuration only.
**Next:** an opt-in live probe (queue send-permission check, Anthropic reachability with a cached result) feeding readiness and alerts, with SLOs and alert rules set in the telemetry backend.

### 13. Conversation context, persistence and record (Dependency, needs a decision)
- **Follow-ups lose context (resolved):** history now reaches inference and the interviewer as well as the classifier.
- **A page refresh loses everything.** Plan: keep the transcript with its signatures in `sessionStorage`.
- **No server-side record** for compliance, support or feedback. **Decision needed:** does intake own a write-only recorder (redacted, opt-in, with retention and deletion), or does the platform? The answer also decides whether downstream can fetch context for a ticket (finding 3).
- **Signed history never expires.** There is no timestamp or TTL in the HMAC, so a genuine turn replays forever and revocation is key rotation only. Assistant text influenced by prompt injection is signed as genuine.

### 14. Medium and low items (Open)
- Secrets come from configuration or environment variables with no Key Vault integration. `DefaultAzureCredential` is used in production paths; `ManagedIdentityCredential` is more predictable.
- Rate limiting: fixed windows allow a double burst, hub and REST count separately (a user can get twice the rate), and there is no IP-level limit ahead of JWT validation.
- User ids are logged at Information level on each request (`ChatEndpoints.cs:48`, `ChatHub.cs:131`). Define a retention policy or hash them.
- The dev-auth guard relies on `ASPNETCORE_ENVIRONMENT` alone. Add a second guard.
- The search lane appends model-supplied citation links. `react-markdown` blocks raw HTML but not phishing links; consider a link policy.
- `UnsupportedLaneException` maps to 400 but is a server fault.
- `DependencyInjection.cs` reads configuration in two places with duplicated key-fallback logic.
- No OpenAPI document or API versioning policy beyond the `/v1` path.
- Test packages are a major version behind and `xunit` v2 is legacy.
- The in-memory ticket store scans all tickets under a lock on each issue; fine at the current cap, not beyond it.

## Resolved earlier (see README for the design)
Message-size limit via `SendStreamed` parts; config validation at startup and `/status`; colon-free tenant and user ids; single-use hub tickets and WebSocket-only transport; pre-filter false positives, bounded regexes and current-message-only matching (finding 6 is a new hole in that work); observability (Serilog, OpenTelemetry, correlation ids, usage and cost); signed conversation history; job lifecycle, cancellation and per-tenant cap; destination-aware DLP; HTTP hardening; frontend sign-in loop guard, production-build guard, axe and Playwright tests.

## Out of scope (belongs to other components)
Ticket creation and the Service Bus worker, Cosmos DB RAG, the PIM workflow itself, the tenant registry and billing, the long-term audit store and its retention, and anything about downstream prompt design. Intake's responsibilities toward each are in findings 2, 3, 8, 9, 11 and 13. **Please confirm this boundary.** If any of these actually live in this component, the corresponding finding returns to full scope.

## Suggested order
1. Put the repo under git with CI, central package management, analyzers and scanning (1).
2. Remove or gate the PIM card (2); it is a small change with a trust payoff.
3. Define and test the downstream contract (3), since both sides depend on it.
4. Add a real-model evaluation of the production prompts (4), then fix 6 and 7 against them.
5. Emit audit events (9) and settle the vendor and data-governance decisions (10).
6. Admission control with platform-supplied budgets and tenant status (8, 11).
7. Make the front door scalable and observable end to end (5, 12), then settle the conversation decision (13).
8. Work through the remaining items (14).
