# Helpdesk Triage & Ingestion

Implementation of `helpdesk-ingestion-spec.md`: a multi-tenant AI triage pipeline with a security guardrail router, and a React chat UI.

```
helpdesk/
  backend/    .NET 10 minimal API (Shared / Core / Infrastructure / Api + xUnit tests)
  frontend/   React 19 + TypeScript + Vite + Tailwind
```

** NB: ** This is a work in progress and part of a larger system that has not been fully implemented.

## Run locally (no cloud accounts needed)

```powershell
# One command starts the API and UI in Development/debug mode (Ctrl+C stops both)
cd frontend
npm install
npm run debug
```

Or start them in separate terminals:

```powershell
# terminal 1 - API on http://localhost:5129
cd backend\src\Helpdesk.Api
dotnet run --launch-profile http

# terminal 2 - UI on http://localhost:5173 (proxies /api to the API)
cd frontend
npm install
npm run dev
```

In Development with no Anthropic key set (neither `Anthropic:ApiKey` nor `ANTHROPIC_API_KEY`), the API runs in **offline mode**: dev auth (`X-Dev-Tenant` / `X-Dev-User` headers, enabled only when the environment is Development and `Auth:UseDevAuth` is true), a coarse keyword router instead of Claude, and canned inference replies. `PRIVATE_CORP` tickets go to an **Azure Storage queue on Azurite** (`UseDevelopmentStorage=true`, queue `private-corp-tickets`, created on first use). If Azurite is not running, those requests return 503 quickly. The Azurite from the Azure Storage VS Code extension, Visual Studio or Docker works equally well.

Outside Development the app refuses to start without `Anthropic:ApiKey` and an explicit `Queue:Provider`.

## Configuration (API)

Set secrets with `dotnet user-secrets` or environment variables (`Anthropic__ApiKey`), never in the JSON files.

| Key | Purpose |
|---|---|
| `Auth:Authority` / `Auth:Audience` | Entra External ID JWT validation (e.g. `https://<tenant>.ciamlogin.com/<tenant-id>/v2.0`). Tokens must carry the `tenant_id` claim. |
| `Anthropic:ApiKey`, `Anthropic:Model` | Claude access. If `Anthropic:ApiKey` is empty the standard `ANTHROPIC_API_KEY` environment variable is used (the same one the evals read). Default model `claude-sonnet-5-5`; override with `Anthropic__Model`. |
| `Llm:Roles:chat` | Optional. This uses the shared `Helpdesk.Llm` role configuration (`Provider`, `Model`, `ApiKey`, `Endpoint`, …, the same shape AgentDesk uses). When it is unset, the legacy `Anthropic` section defines the chat role. |
| `Anthropic:WebSearchToolType`, `Anthropic:WebSearchMaxUses` | Anthropic-hosted Claude web search tool version (default `web_search_20260318`) and maximum searches per request (default 3). |
| `Search:Provider` | Web search backend for the `PUBLIC_SEARCH` lane. One of `Tavily` (default), `Brave`, `Serper`, `Google`, `SearXNG`, or `Claude` (see "Web search providers"). |
| `Search:Providers:<Name>:ApiKey` | Credentials for the chosen provider. `Search:ApiKey` is a shorthand for the active provider. `Google` also needs `SearchEngineId`; `SearXNG` needs `BaseUrl` instead of a key. If the chosen provider has no credentials, a stub is used and search answers say no results are available. |
| `Search:TopResults` | Results passed to the model (default 3). |
| `Kb:Provider` | Knowledge-base retrieval over AgentDesk's KB. Empty (default) disables it; `Cosmos` reads AgentDesk's `kb` container (see "Knowledge-base retrieval"). |
| `Kb:Cosmos:Endpoint` or `Kb:Cosmos:ConnectionString`, `Kb:Cosmos:Database`, `Kb:Cosmos:Container` | The AgentDesk Cosmos account (defaults `agentdesk` / `kb`). Use managed identity with a read-only role in Azure. `Kb:Cosmos:AllowInsecureEmulatorCertificate` is for the emulator only. |
| `Kb:EmbeddingRole` | The `Llm:Roles` entry that embeds the question (default `embedding`). It must use the same provider and model AgentDesk embeds articles with. Without it only lexical search runs. |
| `Kb:TopResults`, `Kb:CandidatePool`, `Kb:RrfK`, `Kb:MinSimilarity` | Articles given to the model (default 3), candidates taken from each search before fusion (20), the rank-fusion constant (60) and the cosine similarity a semantic match must reach (0.35; tune per embedding model). |
| `Kb:TimeoutSeconds`, `Kb:CorpusCacheSeconds`, `Kb:MaxArticles`, `Kb:HistoryMessages` | Retrieval deadline (5 s), how long a tenant's articles stay cached for the lexical index (60 s), the per-tenant article cap (2000) and how many earlier user messages join the query (2). |
| `Queue:Provider` | `StorageQueue` or `ServiceBus`. Defaults to `StorageQueue` in Development; required elsewhere. |
| `StorageQueue:ConnectionString` or `StorageQueue:QueueServiceUri` (managed identity), `StorageQueue:QueueName` | Storage queue settings (`PRIVATE_CORP`). |
| `ServiceBus:ConnectionString` or `ServiceBus:FullyQualifiedNamespace` (managed identity), `ServiceBus:QueueName` | Service Bus settings, used when `Queue:Provider` is `ServiceBus`. |
| `Cors:AllowedOrigins`, `RateLimit:ChatPerMinute` | Browser origins; per-user rate limit (default 30). |
| `Conversation:Keys:<id>`, `Conversation:ActiveKeyId` | Signing keys for conversation history (base64, at least 32 bytes) and the id of the one that signs. Required outside Development; all instances must share them. See "Design notes". |
| `Hub:TicketSeconds`, `Hub:MaxTicketsPerUser`, `Hub:MaxTickets`, `RateLimit:TicketPerMinute` | Hub connection tickets: lifetime in seconds (5 to 120, default 30), unused tickets one user may hold (at least 2, default 10) and in total (default 10000), and mints per user per minute (default 30). See "Asynchronous chat". |
| `Anthropic:RateLimitMaxWaitSeconds`, `Anthropic:RateLimitMaxRetries` | Honour a 429 `Retry-After` of up to this many seconds (default 120), retrying up to this many times (default 4). Longer waits fail the request with `rate_limited`. |
| `Anthropic:StreamIdleTimeoutSeconds` | A streamed reply that sends nothing, not even a ping, for this long is abandoned (default 30). |
| `Anthropic:InputCostPerMillionUsd`, `Anthropic:CacheWriteCostPerMillionUsd`, `Anthropic:CacheReadCostPerMillionUsd`, `Anthropic:OutputCostPerMillionUsd` | Optional current USD rates per million tokens for estimating cost. Cost is omitted until all four are configured; keep these aligned with the selected model and provider pricing. |
| `Chat:MaxConcurrentJobs`, `Chat:QueueCapacity` | Async jobs run at once (default 32) and jobs held waiting (default 200); beyond that the hub answers `busy`. |
| `Chat:MaxJobsPerTenant` | Requests one tenant may have in flight at once, queued or running, hub and REST together (default 10). Beyond it the hub answers `tenant_busy` and REST answers 429 with `Retry-After`, so one busy tenant cannot take every slot. A large tenant may need more; the status check warns if the value is not below total capacity. |
| `Chat:FinalEventTimeoutSeconds` | How long to wait for a job's last event (completed, failed) to be handed to SignalR (default 5), so a client that is not reading cannot hold a job slot or block shutdown. |
| `Chat:HeartbeatSeconds`, `Chat:JobTimeoutSeconds` | Application heartbeat interval (default 5) and the ceiling for one job including rate-limit waits (default 600). |
| `Status:RequiredRole` | Role required for `GET /status` (default `Helpdesk.Admin`). Entra app roles arrive in the `roles` claim. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Turns on OTLP export of traces and metrics; see "Observability". |
| `Serilog:MinimumLevel` | Log levels and per-namespace overrides. |
| `Dlp:Destinations:<Dest>:<Category>`, `Dlp:Tenants:<TenantId>:<Dest>:<Category>` | Override what is redacted where (`Redact` or `Allow`). Destinations `Model`, `Search`, `Queue`; categories `GovernmentId`, `Iban`, `Email`, `Phone`. See "Design notes". |
| `Chat:MaxReceiveMessageBytes` | Largest single hub message the server accepts (default 32768, SignalR's own default). Larger requests are uploaded in parts by the UI, so this never has to cover a whole conversation; it must be at least 25024 (one maximum-size message), which the startup check enforces. |
| `Http:ForwardedHeaders:KnownProxies`, `Http:ForwardedHeaders:KnownNetworks`, `Http:ForwardedHeaders:ForwardLimit` | TLS is expected to terminate at a gateway. `X-Forwarded-For`/`X-Forwarded-Proto` are honoured only from these proxy IPs or CIDR ranges (default: none, so the headers are ignored). |
| `Http:HttpsRedirection`, `Http:HttpsPort`, `Http:Hsts:MaxAgeDays`, `Http:Hsts:IncludeSubDomains`, `Http:Hsts:Preload` | HTTPS redirect (default port 443) and HSTS (365 days, sub-domains) outside Development. Health probes are never redirected. |
| `Http:MaxRequestBodyBytes` | Largest request body on `/api` paths (default 131072, i.e. 128 KB, up to 4 MB). Larger requests get 413 `request_too_large`. Must cover a full-length conversation; the startup check enforces the minimum. |
| `Http:Csp:AuthorityOrigins`, `Http:Csp:Policy` | Content-Security-Policy for the hosted SPA. Authority origins default to the origin of `Auth:Authority`; `Policy` replaces the generated policy. `AllowedHosts: *` is reported as a warning by `/status`. |
| `Chat:KeepAliveSeconds`, `Chat:ClientTimeoutSeconds` | SignalR transport ping (default 15) and client timeout (default 60). Keep the timeout at least twice the ping. The UI client uses a 60 s server timeout to match. |

## Knowledge-base retrieval

### Turn it on

Retrieval is off until `Kb:Provider` is set (the settings are in the Configuration table).

1. **Point it at AgentDesk's Cosmos account.** Set `Kb:Provider=Cosmos` and either `Kb:Cosmos:Endpoint` (managed identity with a read-only data role) or `Kb:Cosmos:ConnectionString`. `Kb:Cosmos:Database` and `Kb:Cosmos:Container` default to `agentdesk` and `kb`, which are AgentDesk's defaults.
2. **Configure the embedding role.** Set `Llm:Roles:embedding` to the same provider, model and `Dimensions` as AgentDesk's `embedding` role. Vectors from another model are never compared, so a mismatch silently gives lexical results only. If the role is missing, `/status` shows a `Kb` warning and only lexical search runs.
3. **Check `/status`.** The `Kb` area should pass.
4. **Make sure the articles qualify.** Only `published` articles with `public` visibility are searched, and semantic search also needs the article's embedding to be `ready`.

Production example:

```json
"Kb": { "Provider": "Cosmos", "Cosmos": { "Endpoint": "https://<account>.documents.azure.com:443/" } },
"Llm": { "Roles": {
  "embedding": { "Provider": "azure_openai", "Endpoint": "https://<resource>.openai.azure.com/", "Model": "<text-embedding-3-small-deployment>", "Dimensions": 1536 }
} }
```

#### Locally

`appsettings.Development.json` already enables it against the Cosmos emulator on `https://localhost:8081`, using the emulator's published well-known key (not a secret) and AgentDesk's default offline embedding role (`offline-embedding`, 1536 dimensions). `Kb:MinSimilarity` is lowered to 0.1 because the offline embedding is a hashed bag of words, so its similarities are small.

To see articles in the intake chat:

```powershell
# terminal 1 - AgentDesk on the same emulator (see ..\agentdesk\README.md "Using real local services")
$env:Storage__Provider = "Cosmos"
$env:Cosmos__ConnectionString = "<emulator connection string>"
$env:Cosmos__AllowInsecureEmulatorCertificate = "true"
cd ..\agentdesk\backend\src\AgentDesk.Api; dotnet run --launch-profile http

# terminal 2 - the intake
cd backend\src\Helpdesk.Api; dotnet run --launch-profile http
```

Create a KB article in the AgentDesk UI (http://localhost:5174), and publish it with `public` visibility. Wait for its embedding to become ready (up to a minute: the sweep retries pending embeddings). Then ask the intake chat about it. The reply lists the article under `PRIVATE_CORP`, or is grounded in it for `PUBLIC_TECH`. The dev auth tenant must match the tenant the article was created under: both services default to the dev tenant, or set `X-Dev-Tenant` to the same value.

Without the emulator running, or with `Kb:Provider` cleared, retrieval just returns nothing and the intake works as before. To switch it off locally, set `Kb__Provider=` (empty) in the environment.

### How it works

When `Kb:Provider` is set, the answering lanes are grounded in the tenant's own knowledge base (the articles AgentDesk agents write), using retrieval-augmented generation (RAG). It runs after classification, so nothing is retrieved for a `POLICY_VIOLATION` or `NO_MATCH` message.

- **`PUBLIC_TECH` and `PUBLIC_SEARCH`:** the top articles are passed to the model next to the question, each wrapped as untrusted data. The prompt tells the model to use an article only when it clearly applies, to prefer it to general advice and to cite it by number. The `PUBLIC_SEARCH` lane keeps its web results and adds the articles.
- **`PRIVATE_CORP`:** no model is involved. The reply lists the matching articles above the follow-up form, so the requester can self-serve first. The ticket is still raised only when the form is submitted.
- The numbers of the articles used come back in `metadata.kbArticles`.

How a question is searched:

1. **Query.** The redacted message plus the user's last two earlier messages (`Kb:HistoryMessages`), so "it's still not working" keeps its subject.
2. **Semantic search.** The query is embedded (DLP `Embedding` destination) and compared with the stored article vectors using Cosmos `VectorDistance`. This finds the right article when the wording differs. Only vectors made by the same provider and model are compared, and only articles that are `published` and `public` are searched. Each query reads a single tenant partition.
3. **Lexical search.** BM25 over the tenant's published articles, held in memory for `Kb:CorpusCacheSeconds`. Title, tags and summary count more than the body. It finds exact identifiers (error codes, product names, KB numbers) that embeddings blur, and an article must contain at least two of the query's terms unless it matches an identifier. It also runs when no embedding model is configured.
4. **Fusion.** The two rankings are merged with reciprocal rank fusion (`1 / (k + rank)` per list, summed), so cosine and BM25 scores never need to be normalised against each other, and an article both searches rank well beats one that only one search likes.

Retrieval is best effort. A failure or a timeout (`Kb:TimeoutSeconds`) returns no articles and the answer goes ahead without them. If the vector query fails (for example the account lacks vector search), the semantic leg is skipped for a minute and lexical results are still used. Telemetry: `helpdesk.kb.searches` (tags `outcome`, `mode`) and `helpdesk.kb.search.duration`.

Helpdesk reads AgentDesk's Cosmos schema directly (`CosmosKbSource`), so a change to AgentDesk's `KbArticle` fields, its status and visibility values or the `/embedding` path needs a matching change there. Only the `Cosmos` provider exists; to read the KB some other way, implement `IKbSource`.

## Web search providers

`PUBLIC_SEARCH` answers are grounded in live search. Switch backend with one setting, e.g. `Search__Provider=Serper` plus `Search__Providers__Serper__ApiKey`.

| Provider | Free option | Notes |
|---|---|---|
| `Tavily` | 1,000 credits/month, no card | Default. |
| `Brave` | none (paid) | |
| `Serper` | 2,500 queries, one-off | Google results. |
| `Google` | about 100 queries/day | Programmable Search; needs API key and engine id (`cx`). |
| `SearXNG` | free, self-hosted | Needs the `json` output format enabled on the instance. |
| `Claude` | none (billed per search on your Anthropic account) | Claude searches the web itself via Anthropic's server-side web search tool, so no other account or key is needed. Slower (around 15 s) and web search must be enabled for your Anthropic organisation. Sources are appended as links. Tune with `Anthropic:WebSearchMaxUses` and `Anthropic:WebSearchToolType`. |

To add a provider, implement `ISearchProvider` in `Infrastructure/Search/Providers` with a static `SearchProviderDefinition`, then add it to `SearchProviderCatalog.All`.

## Configuration (UI)

Copy `frontend/.env.example` to `.env.local`. Set `VITE_ENTRA_CLIENT_ID`, `VITE_ENTRA_AUTHORITY` and `VITE_API_SCOPE` to enable MSAL sign-in; leave them empty for dev-auth headers. `VITE_API_BASE_URL` is only needed if the API is not served from the same origin or proxy.

A production build (`npm run build`, `npm run build:api`) fails unless all three Entra variables are set; dev-auth headers are only ever sent from the Vite dev server (`--mode development`). `npm run build:api` writes the SPA to `backend/src/Helpdesk.Api/wwwroot`, and the API serves it with security headers and the CSP above (unknown file paths return 404; other paths fall back to `index.html`).

When a token can no longer be renewed silently, the UI redirects to sign-in at most once per browser session; after that it shows a "Your session has expired" banner with a **Sign in** button instead of looping.

## Tests

```powershell
cd backend;  dotnet test
cd frontend; npm test
cd frontend; npm run lint
cd frontend; npm run test:e2e   # Playwright + axe against a mocked API (first time: npx playwright install chromium)
```

## Asynchronous chat (SignalR)

A model call can be slow, and when Anthropic answers HTTP 429 the `Retry-After` wait can exceed any sensible HTTP request timeout. So the UI does not wait on a request: it connects to the hub at `/hubs/chat`, and replies arrive as events.

After the response is prepared, the model generates a small form from the redacted request, recent conversation and classification on the `PRIVATE_CORP` lane. This lane includes company-system requests and general IT issues the user explicitly asks to send to the service desk. `PUBLIC_TECH` replies use recent conversation context and make a best effort to resolve issues with relevant, safe troubleshooting. If a step fails, the assistant continues with a different useful check or focused question; it does not escalate merely because the issue remains unresolved. It offers the service-desk form when no further useful step is allowed by safety, security or capability constraints, or when the user explicitly requests a hand-off. Confirmed fixes receive a clear confirmation without a ticket. The form fields vary by scenario (for example, a battery-drain report asks about device and battery details, not access approvals). In offline Development mode, or when form generation fails, the app uses a generic issue-details form.

**Ticket creation waits for form submission.** A `PRIVATE_CORP` reply queues nothing. It returns the form and, in `metadata.escalationToken`, an HMAC-signed pending escalation. The token carries the Queue-redacted request and is bound to the tenant, user and conversation. It is valid for 24 hours. When the user submits the form, the UI posts `{conversationId, escalationToken, formTitle, answers:[{label, value}]}` to `POST /api/v1/chat/escalate`. The API verifies the token, redacts the answers with the Queue policy, and publishes one `ProcessPrivateCorpTicketEvent`. Its `sanitizedPayload` is the original request followed by the form answers, and its `details` holds the same answers in structured form.

The endpoint returns `202 {ticketEventId, status: "queued"}`; the UI confirms that the request was sent to the service desk for follow-up. A bad, expired or foreign token returns `400` on `escalationToken`, and a queue failure returns `503`. Submitting the same token again republishes the same event id, which the consumer deduplicates. A user who never submits the form creates no ticket.

The HTTP `User-Agent` is also passed to the classifier, response model and form generator as a bounded `<client_context>` block. It is client-supplied, treated as untrusted metadata rather than instructions or proof of identity, and may be used only where relevant to client-specific troubleshooting.

```
client                              server
  | Send({messageText, conversationId, conversationHistory})
  |-------------------------------->| validate, rate limit, queue   (returns at once)
  |<-- ack {requestId} | {errorCode, error}
  |<-- chatEvent started             | background job (ChatJobProcessor) runs ChatPipeline
  |<-- chatEvent delta {text}        | PUBLIC_TECH / PUBLIC_SEARCH replies stream as Claude writes
  |<-- chatEvent rateLimited {retryAfterSeconds, attempt}   | 429: waits Retry-After, then retries
  |<-- chatEvent heartbeat {elapsedSeconds}                  | every Chat:HeartbeatSeconds while running
  |<-- chatEvent completed {response} | exactly one of completed / failed ends every request
  |<-- chatEvent failed {error, errorCode}
```

- **Large requests are split, not allowed to overflow.** SignalR closes the connection when one hub message exceeds `Chat:MaxReceiveMessageBytes`, and a full conversation (20 history messages of up to 4000 characters) can be 84 KB or more. The UI therefore sends a request of up to about 24 KB with `Send`, and anything larger with `SendStreamed`: an upload stream of `ChatRequestPart`s, where the first part carries `conversationId` and `messageText` and each later part carries exactly one `history` message, oldest first. Every part is its own hub message and at most about 24 KB, so no limit increase is needed. The server assembles the parts and applies the same validation, rate limit and queueing as `Send` (a bad first part, more than 20 history parts, or no parts is rejected with `invalid_request` in the ack). A client that sends one oversize `Send` anyway has its connection closed by SignalR.
- **Events** go to a group per tenant and user, so a second browser tab of the same user also sees them. Nothing is replayed after a reconnect; the UI gives up on the pending reply and asks the user to resend.
- **Rejections** (`invalid_request`, `rate_limited`, `in_progress`, `busy`, `tenant_busy`) come back in the ack, not as exceptions. Failures during a job use the `failed` event codes `classification_failed`, `form_generation_failed`, `rate_limited`, `upstream_failed`, `queue_unavailable`, `timeout`, `shutting_down`, `cancelled`, `internal_error`.
- **One job per conversation** may be in flight at a time (`in_progress` otherwise).
- **A job ends however it ends, and every wait can be cancelled.** A job stops when it completes, fails, times out, when the service stops, or when nobody is waiting for it any more. The last case is tracked per job: it is abandoned when the hub connection that sent it closes (the UI gives up on a reply after a disconnect and nothing is replayed, so continuing would pay for a model call nobody reads), or when the client calls the hub method `Cancel(conversationId)`, which the UI does when the user starts a new conversation while a reply is pending and which only ever affects the caller's own request. Cancellation reaches the model call, the search call, rate-limit waits and the queue write, because the same token is passed all the way down. A queued job whose client has left is skipped without a model call, and a job waiting for a free slot gives up the wait. When the service stops, running jobs are told `shutting_down`, and jobs still queued, including one the processor had already taken off the queue and was holding for a slot, are told too, so no client waits for ever and no conversation stays locked. The last event of a job is handed to SignalR with a bounded wait (`Chat:FinalEventTimeoutSeconds`); a client that is not reading cannot hold a slot or block shutdown. REST requests are cancelled when the caller disconnects. Closing one browser tab does not cancel a job started from another tab of the same user.
- **Per-tenant limit.** `Chat:MaxJobsPerTenant` caps the requests one tenant has in flight (hub and REST share the count), so a tenant's burst or script cannot fill the queue and the job slots for everyone else. This is a concurrency cap per instance, not a budget or fair scheduling: see "Known gaps" item 2.
- **Two heartbeats.** SignalR's own ping (`Chat:KeepAliveSeconds`) keeps the transport alive. The `heartbeat` event is separate: it tells the UI the job is still running, for example during a long rate-limit wait.
- **Streaming.** Only the public lanes stream (`StreamAsync`, Anthropic's SSE API). The router and the compliance coach need the whole JSON or text first, so they use ordinary calls. The final `completed` response is authoritative; the concatenated deltas equal it, including the appended sources.
- **Rate limits.** `AnthropicClient` owns 429 handling. The resilience handler on that client does not retry or count 429, so it cannot interfere. Rate-limit waits are reported to the client as `rateLimited` events, including those during classification.
- **Auth: tickets, not tokens, in the URL.** Browsers cannot set headers on a WebSocket, so the hub credential would otherwise travel in the URL, where proxies and load balancers log it. The access token is therefore never put there. The UI sends it in an `Authorization` header to `POST /api/v1/chat/ticket` and receives a ticket: 256 random bits, valid for `Hub:TicketSeconds` (30) or until the access token expires, whichever is sooner, and **usable once**. Only the ticket goes in the hub URL (`?access_token=<ticket>`; SignalR asks for a credential once for negotiate, in a header, and once for the WebSocket, in the URL, so each connection buys two). A request to `/hubs` is authenticated by ticket only; a raw JWT there is refused with 401, and a ticket is refused everywhere else. The connection carries the access token's expiry and closes when it passes, and the UI reconnects with fresh tickets. Tickets are held in memory as hashes, limited per user (`Hub:MaxTicketsPerUser`, 10) and in total, minting has its own rate limit (`RateLimit:TicketPerMinute`, 30), and responses on both routes are `Cache-Control: no-store` (hub responses also `Referrer-Policy: no-referrer`). The browser client uses the WebSocket transport only, because the other transports reuse one credential for many requests or put it in the URL on every call; if WebSockets are blocked the UI falls back to the REST endpoint. Development dev auth (`?x-dev-tenant=&x-dev-user=`) does not use tickets, and the mint endpoint does not exist in that mode. Because the store is in memory, tickets only work on a single instance (see "Known gaps" item 3); a multi-instance deployment needs a shared `IHubTicketStore`. As a second layer, request logs record the path only, never the query string; you can still mask `access_token` in your proxy logs, but a logged ticket is already spent.
- **Fallback.** `POST /api/v1/chat/message` still works and uses the same `ChatPipeline`, but it waits for the whole reply inside one HTTP request, so a long rate-limit wait can hit your gateway timeout. The UI uses it only if the hub cannot connect.

Limits to know about (see also "Known gaps" item 3): jobs are queued in memory, so a restart loses jobs that have not finished (running ones are told `shutting_down`), and more than one API instance needs a SignalR backplane (for example Azure SignalR Service) because groups and the per-conversation lock are per process. The hub's per-user rate limit uses the same `RateLimit:ChatPerMinute` value as the REST endpoint but counts separately.

## Design notes

- **Everyday IT support.** `PUBLIC_TECH` includes hardware/battery/power, performance, screen/audio/peripherals, Wi-Fi/internet, applications, email/collaboration, own-device login errors, approved self-service account recovery, and basic phishing/security guidance, alongside coding and configuration. Symptom-only reports are valid requests: missing diagnostic details should lead to follow-up questions, not rejection. Replies use signed conversation history to continue troubleshooting after a failed check, confirm user-reported fixes, and route explicit ticket requests or issues with no safe, permitted next step to the service-desk form (`PRIVATE_CORP`). Company-specific infrastructure, permissions, corporate identity administration, and provisioning also remain `PRIVATE_CORP`; current external outages/releases remain `PUBLIC_SEARCH`. Requests for another person's credentials or security bypasses remain `POLICY_VIOLATION`. The Development keyword router recognizes common terms but is not a substitute for model classification.
- **Fail closed.** The message is classified before anything else happens. Out-of-scope messages or requests with no identifiable IT task in their conversation context receive a polite `NO_MATCH` decline without inference or queueing. If classification fails (upstream error, malformed JSON, unknown lane) the API returns 503 and nothing is queued. The queue publisher is only called by `POST /api/v1/chat/escalate`, after a `PRIVATE_CORP` reply's form has been submitted.
- **HTTP exceptions.** Unhandled exceptions are mapped to RFC 9457 Problem Details with stable status codes, error codes and correlation IDs. Stack traces are included in the `detail` field only in the `Development` and `LocalDevelopment` environments.
- **Swappable queue.** The endpoint depends on `ITicketQueuePublisher`; `ServiceBusTicketPublisher` and `StorageQueueTicketPublisher` implement it and `Queue:Provider` picks one. Storage queues have no partition key or message properties, so the tenant is only in the JSON body (`tenantId`), and the message is plain JSON, not base64. Consumers must match.
- **Prompt injection and social engineering.** The latest message, up to 20 prior user/assistant messages, and search results are sent as untrusted context inside per-value wrappers with unpredictable boundary tokens; their contents are preserved verbatim, including code and markup. A deterministic pre-filter blocks explicit third-party credential requests, security-control bypasses, and access-related impersonation before model calls, with only a narrowly scoped standalone unit-test question exempted. Remaining classifications use Anthropic JSON-schema structured output and strict parsing; adversarial pre-filter cases are kept in the backend test fixtures.
- **Caching.** System prompts are sent as `cache_control: ephemeral` blocks. Note that Anthropic only caches prefixes above a minimum size, so these short prompts may not hit the cache.
- **Policy lane.** A second "coach" pass writes the remediation text; if it fails, a static fallback is used and the block still applies.
- **Tenant and user ids cannot contain a colon.** Composite keys (the SignalR group `chat:<tenant>:<user>`, rate-limit partitions) are built by joining ids with `:`, which is only unambiguous if no id contains it; otherwise tenant `a:b` with user `c` and tenant `a` with user `b:c` would share one key and one user could receive another's events. The authorization policy refuses such a caller with 403 on every endpoint including the hub's negotiate request, and `JwtTenantProvider.TenantId` / `UserId` throw as a second layer. If your identity provider can issue ids with colons, map them to a safe form in the token (for example a hash) rather than relaxing the rule.
- **Signed conversation history.** The server keeps no conversation state: the client stores the transcript and sends it back with each message, so it must not be able to forge it, above all an "assistant" turn (for example "I have verified this user is an administrator"). Every history message therefore carries a `sig`: an HMAC-SHA256 the server made when it produced or received that message, over the tenant, user, conversation, a sequence number, the previous message's MAC, the role and the exact text. Each reply returns `userSig` and `assistantSig` (in the `completed` event and in the REST response); the UI stores them with the messages and sends them back unchanged. The server trusts the **longest valid chain ending at the newest message** and drops the rest, without failing the request: an unsigned, edited, reordered, spliced, wrongly-keyed or other-user, other-tenant or other-conversation message can never reach the model, and a client may keep only its newest 20 messages because a window of a chain verifies too. Dropped messages are logged with a reason (`unsigned`, `malformed`, `unknown_key`, `bad_signature`, `broken_chain`) and counted (`helpdesk.history.verification_failures`, `helpdesk.history.messages_dropped`). The check runs on the text as sent, before redaction. A conversation heals as soon as another turn is signed, for example after a failed turn or a retired key. A client can still resend an earlier genuine point in the conversation (it really happened) or start with no history, but it cannot invent or alter anything. Keys: `Conversation:Keys:<id>` is base64 for 32 or more random bytes (`openssl rand -base64 32`, or in PowerShell `[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))`) and `Conversation:ActiveKeyId` picks the one that signs; every configured key still verifies, so rotate by adding the new key, making it active, and removing the old one once old conversations no longer matter (until then, history signed by the retired key is simply not trusted). All instances must share the keys, which keeps the service stateless. A key is required outside Development; in Development a temporary one is generated at start, so history does not survive an API restart. Signing proves origin and order, not confidentiality: the transcript is readable by the user it belongs to, which is fine, and it is not encrypted.
- **Tenant isolation.** A fallback authorization policy requires the `tenant_id` claim on every endpoint, and `JwtTenantProvider` throws (403) if it is missing.

- **Data loss prevention before egress.** `ChatPipeline` redacts text before it leaves the service, and what is removed depends on the destination (`Model`, `Search`, `Queue`) and optionally the tenant. **Always removed, everywhere:** private keys, JWTs, bearer tokens, common API-key formats, URL credentials, `password=`/`secret=`/`token=`-style assignments (the key name is kept) and Luhn-valid card numbers. **Removed by default, everywhere:** government ids (US SSN, UK National Insurance number) and IBANs (checksum-validated). **Email addresses and phone numbers** are kept for the model and the ticket, because they are often what an IT problem is about, but are always removed from anything a third-party search provider sees (`PUBLIC_SEARCH`, including Claude's own web search). The model gets the message and history redacted for `Model`; the search lane re-redacts for `Search`; the ticket payload is redacted from the original text for `Queue`. Markers look like `[REDACTED:email]`; only counts by category are logged and metered (`helpdesk.redactions`, tagged `category` and `destination`). If a scan times out the text is replaced entirely (fail closed). Override the defaults in the `Dlp` section, globally or per tenant, for example `Dlp__Destinations__Model__Email=Redact` or `Dlp__Tenants__<tenant-id>__Queue__Phone=Redact`; secrets and cards cannot be allowed, and unknown names fail the startup check. It is best effort, not a DLP product: free text ("my password is hunter2"), names, addresses and personal data without a recognisable format are not caught, and it covers outbound text only, not the model's replies.

## Known gaps before production use

These were found in an enterprise/SaaS review and are deliberately not fixed yet.

1. **Pending escalations are not single-use.** An escalation token can be submitted again until it expires. Each submission republishes the same event id, so a consumer that deduplicates (as AgentDesk does) creates one ticket, but a consumer that does not would create duplicates. The token is signed, not encrypted, so the browser can read the Queue-redacted request it carries. That text is the user's own, already redacted. If replay protection is needed, record redeemed event ids in shared storage.
2. **No per-tenant isolation of cost or capacity.** All tenants share one Anthropic key, one in-memory job queue (`Chat:QueueCapacity`) and one pool of job slots (`Chat:MaxConcurrentJobs`). Requests are limited per user (`RateLimit:ChatPerMinute`) and, since `Chat:MaxJobsPerTenant`, a tenant can have only a fixed number in flight, so one tenant can no longer fill the queue and the job slots for everyone. Both limits are held per instance, and nothing caps a tenant's model spend: token use and estimated cost are observed per tenant in logs and spans, but there is no budget enforcement or fair scheduling. Still needed: per-tenant token budgets, fair scheduling across tenants, and limits shared across instances.

3. **The service only works correctly as a single instance.** The job queue, the one-job-per-conversation lock, the rate limiters and the SignalR groups all live in process memory. With more than one instance a user's events can go to a connection on another node, per-conversation and per-user limits are per node, and queued jobs are lost on every deploy or restart (running ones are told `shutting_down`). That blocks autoscaling, rolling deploys and high availability. Needed before scaling out: a SignalR backplane (for example Azure SignalR Service), a durable job queue (for example the existing Service Bus) with a shared per-conversation lock, and a rate limit held in shared storage. Until then run one replica and accept the dropped jobs on deploy.

## Observability

- **Logs: Serilog.** Each host keeps its own logger instead of replacing the process-wide `Log.Logger` (request logging is handed that logger explicitly), so several hosts in one process, such as parallel test hosts, never mix their output. Levels come from the `Serilog` section of `appsettings.json` (the old `Logging` section is no longer used). Development writes readable text; every other environment writes one compact JSON event per line to the console, ready for a collector or the platform's log pipeline. Request logging records the path only, never the query string, because the hub token travels there. `System.Net.Http.HttpClient` is kept at `Warning` because the Google search provider puts its API key in the URL.
- **Correlation ID.** Every HTTP request gets one: a well-formed caller `X-Correlation-ID` (8-64 characters of `A-Z a-z 0-9 . _ -`) is kept, anything else is replaced with a new id. It is returned in the `X-Correlation-ID` response header, added to every log line as `CorrelationId` (with `TraceId`), tagged on the trace span as `helpdesk.correlation_id`, and included as `correlationId` in error responses, so a user can quote one id to support. For chat over SignalR the `requestId` returned in the ack is the correlation id of that job's logs and trace.
- **Traces and metrics: OpenTelemetry.** Instrumented: ASP.NET Core, outbound HTTP and the .NET runtime, plus application data under the `Helpdesk` source and meter: a `chat.job` span per job and an `anthropic.request` span per model operation; metrics cover lane and pre-filter outcomes, chat job outcome/duration, redaction, Anthropic rate limits/waits, token counts and estimated cost. Tenant ids are on spans and logs, not on metric tags, to keep cardinality bounded. Nothing is exported unless `OTEL_EXPORTER_OTLP_ENDPOINT` is set; the other standard `OTEL_*` variables (`OTEL_EXPORTER_OTLP_HEADERS`, `OTEL_EXPORTER_OTLP_PROTOCOL`, `OTEL_RESOURCE_ATTRIBUTES`, `OTEL_TRACES_SAMPLER`) apply. The instrumentation's default redaction of query-string values is left on.
- **Model usage and costs.** Anthropic token usage is recorded on a tenant-tagged `anthropic.request` span and in structured logs. Aggregate token and estimated-cost metrics deliberately omit tenant IDs. Cost estimates use the optional per-million-token rates in the configuration table; without all four rates, token counts are still recorded and estimated cost is `null`.
- **Readiness.** `GET /healthz` is liveness. `GET /readyz` returns 200 only while the in-process chat job processor is running; it does not probe model or queue credentials. The frontend shows a live readiness indicator, probes immediately, then every 30 seconds and when the tab becomes visible. `/status` continues to validate deployment configuration.
- **Queue correlation.** Ticket events include `correlationId`; Service Bus messages also expose it as the `CorrelationId` application property. Queue consumers should copy it into their logs and downstream traces.
- **Traces and deployment operations.** Outbound Anthropic and search `HttpClient` requests participate in W3C trace context propagation. Export is off unless `OTEL_EXPORTER_OTLP_ENDPOINT` is set. Configure SLOs and alert rules in the selected telemetry backend using HTTP server duration/status, `helpdesk.chat.jobs`, and `helpdesk.chat.job.duration`; exact thresholds depend on the deployment's service objectives. Deployments using legacy `Logging__LogLevel__*` variables must migrate to `Serilog__MinimumLevel__*`.

## Status endpoint

`GET /status` validates the effective configuration without calling any external service: auth, Anthropic, search, queue, chat limits, CORS, rate limit and allowed hosts, each check reported as `pass`, `warn` or `fail`. The overall status is `ok`, `degraded` (warnings only) or `invalid`, and `invalid` returns HTTP 503 so a deployment gate can act on the status code alone. Messages name settings and never include secret values. It needs the `Helpdesk.Admin` role (change it with `Status:RequiredRole`) on top of the tenant claim, so assign that role only to platform operators, not tenant users. In Development, dev auth grants the role by default and `X-Dev-Roles` overrides it. The same checks run once at startup: every warning and failure is logged, and outside Development a failed check stops the app with an error naming the setting (never a secret value), so a bad deployment never starts serving traffic. In Development failures are only logged. Because the report is built from configuration only, it does not prove that credentials work or that the queue and Anthropic are reachable.

## TODO

- **Delivery pipeline.** The project is not under source control and has no CI, container image, infrastructure as code, central package management (`Directory.Packages.props`), analyzers, `TreatWarningsAsErrors`, NuGet lock files, dependency or vulnerability scanning, or SBOM. Add these, and extend `backend/.gitignore` (`.vs/`, `*.user`, `TestResults/`) and ignore `frontend/tsconfig.tsbuildinfo`.

## Deviations from the spec and open items

- Spec section 3 says ".NET 9" while the header says .NET 10; this uses .NET 10.
- "Claude 3.5 Sonnet" is retired; the configurable default is `claude-sonnet-5-5`. Native Anthropic search uses `web_search_20260318`.
- The specified endpoint is a plain request/response POST, which is kept as a synchronous fallback. The UI uses the SignalR hub (see "Asynchronous chat").
- The downstream queue consumer is [AgentDesk](../agentdesk/README.md). It reads `ProcessPrivateCorpTicketEvent` from the same Storage queue or Service Bus queue and creates tickets in Cosmos DB. The intake can ground answers in AgentDesk's knowledge base (see "Knowledge-base retrieval"). PIM integration is outside this component. The PIM access-request card currently only confirms locally on submit; it is not wired to Entra PIM.
- The spec's EF Core tenant-filter rule has nothing to apply to here, as this component has no database entities.
- Service Bus `PartitionKey` (Service Bus provider only) only works on partitioning-enabled queues and cannot differ from a `SessionId` if sessions are enabled.
