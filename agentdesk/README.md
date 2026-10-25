# AgentDesk: ticket agent console

AgentDesk continues the ticket flow after the intake service in [`../helpdesk`](../helpdesk/README.md). The intake classifies chats and publishes `PRIVATE_CORP` requests—including unresolved general IT issues and requests for human follow-up—to a queue as `ProcessPrivateCorpTicketEvent`. AgentDesk consumes those events and turns them into tickets that service-desk agents work in a web console. The ticket description is the event's `summary` (what the requester needs), and the event's `conversationSummary` (what was asked, tried and said in the chat) is stored separately as the ticket's `conversationSummary` and shown as its own "Conversation so far" section. Events from before those fields existed fall back to `sanitizedPayload` as the description.

It has the following parts:
- **Storage.** Tickets, timelines and KB articles live in Cosmos DB. Attachments live in Azure Blob Storage.
- **Knowledge base.** Each article stores a vector embedding alongside it, generated on save.
- **AI assist.** The LLM suggests solutions and drafts replies. The encouraged reply is a new KB article plus a reply that cites it.

All of it runs through the shared pluggable LLM library, `Helpdesk.Llm`.

```
intake (helpdesk) ──ProcessPrivateCorpTicketEvent──▶ queue ──▶ intake consumer ─┐
                                                                                 ▼
agentdesk/frontend (agent console) ◀── REST + SignalR ──▶ AgentDesk.Api ──▶ Cosmos DB (tickets, kb, attachments)
                                                              │         └─▶ Blob Storage (attachment content)
                                                              └─▶ Helpdesk.Llm (chat + embedding providers)
```

```
agentdesk/
  backend/    .NET 10: AgentDesk.Core / Infrastructure / Api + xUnit tests (AgentDesk.slnx)
  frontend/   React 19 + TypeScript + Vite + Tailwind 4, TanStack Query, SignalR
```

The backend also references the following projects in `../helpdesk/backend/src`:
- `Helpdesk.Shared`: the event contract and `PromptInput`.
- `Helpdesk.Llm`: chat and embedding providers.
- `Helpdesk.Dlp`: redaction before egress.

**AgentDesk itself does not do RAG.** Embeddings are stored and the Cosmos container gets a vector index, but AgentDesk does not query them. AI assist sees only the current ticket and any KB articles the agent explicitly picks. The intake (`../helpdesk`) is the reader: it searches published, public articles with vector and keyword search (see its "Knowledge-base retrieval"), so keep the embedding model here and in the intake's `embedding` role the same.

## Run locally (no cloud accounts needed)

```powershell
# One command starts the API (http://localhost:5139) and the UI (http://localhost:5174). Ctrl+C stops both.
cd frontend
npm install
npm run debug
```

Or start them in separate terminals:

```powershell
cd backend\src\AgentDesk.Api; dotnet run --launch-profile http   # API on :5139
cd frontend; npm run dev                                          # UI on :5174 (proxies /api, /hubs and /dev-blobs)
```

The defaults need nothing installed beyond .NET and Node:

| Area | Default | Effect |
|---|---|---|
| Storage | `Storage:Provider=InMemory` | Data is lost when the API restarts. |
| Attachments | `Blobs:Provider=InMemory` | Uploads go to an in-process store served from `/dev-blobs/...`. |
| LLM | `assist` = `anthropic` (`claude-sonnet-5-5`) in Development, `offline` otherwise; `embedding` = `offline` | Assist calls Claude using `ANTHROPIC_API_KEY` from the environment. Anthropic has no embeddings API, so embeddings stay on deterministic hash-based vectors unless you point the `embedding` role at `azure_openai`, `openai` or `ollama`. Without a key the assist endpoints return an error; set `Llm__Roles__assist__Provider=offline` for canned drafts. |
| Intake | `Intake:Provider=None` | Nothing reads the queue. Create tickets in the UI. |
| Auth | Development dev auth | `X-Dev-*` headers; the UI sends them automatically and shows a "(dev)" badge. |

### Using real local services

These examples use environment variables. Use `dotnet user-secrets` in `backend\src\AgentDesk.Api` for anything secret.

```powershell
# Cosmos DB emulator (Windows emulator or the vNext Linux container on https://localhost:8081)
$env:Storage__Provider = "Cosmos"
$env:Cosmos__ConnectionString = "<emulator connection string>"
$env:Cosmos__AllowInsecureEmulatorCertificate = "true"     # emulator only
$env:Cosmos__EmbeddingDimensions = "768"                   # must match the embedding model, see below

# Azurite for attachments and the intake queue
$env:Blobs__Provider = "Azure"
$env:Blobs__ConnectionString = "UseDevelopmentStorage=true"
$env:Intake__Provider = "StorageQueue"
$env:Intake__ConnectionString = "UseDevelopmentStorage=true"

# Ollama for assist and embeddings (ollama pull llama3.1; ollama pull nomic-embed-text)
$env:Llm__Roles__assist__Provider = "ollama";    $env:Llm__Roles__assist__Model = "llama3.1"
$env:Llm__Roles__embedding__Provider = "ollama"; $env:Llm__Roles__embedding__Model = "nomic-embed-text"
```

With the intake running against the same Azurite (`npm run debug` in `../helpdesk/frontend`), a `PRIVATE_CORP` request that the assistant raised as a ticket appears in the AgentDesk ticket list within a couple of seconds.

## Configuration (API)

| Key | Purpose |
|---|---|
| `Storage:Provider` | `InMemory` (default) or `Cosmos`. |
| `Cosmos:Endpoint` (managed identity) or `Cosmos:ConnectionString` | Cosmos account. `Cosmos:Database` defaults to `agentdesk`. Containers default to `tickets`, `kb` and `attachments`, all partitioned by `/tenantId`. |
| `Cosmos:CreateIfNotExists` | Create the database and containers at startup (default true). |
| `Cosmos:EnableVectorPolicy`, `Cosmos:EmbeddingDimensions` | Create the `kb` container with a vector embedding policy on `/embedding` (cosine, float32) and a vector index. The dimension is fixed when the container is created, so it must match the embedding model: 1536 for `text-embedding-3-small` and 768 for `nomic-embed-text`. The account needs the NoSQL vector search capability. |
| `Cosmos:AllowInsecureEmulatorCertificate` | Accept the emulator's self-signed certificate. Never set it against a real account. |
| `Blobs:Provider` | `InMemory` (default) or `Azure`. |
| `Blobs:ConnectionString` or `Blobs:ServiceUri` (managed identity), `Blobs:Container` | Attachment storage. The default container is `ticket-attachments`. Blob paths are `{tenantId}/{ticketId}/{attachmentId}`. With managed identity, uploads and downloads use user-delegation SAS URLs. |
| `Attachments:MaxBytes`, `Attachments:MaxPerTicket`, `Attachments:SasLifetimeMinutes` | 25 MB, 50 per ticket, and 10-minute upload/download URLs by default. `MaxPerTicket` also caps the chat attachments imported from the intake. `Blobs:Container` must match the helpdesk (see "Attachments from the helpdesk chat"). |
| `Intake:Provider` | `None` (default), `StorageQueue` or `ServiceBus`. Match the intake's `Queue:Provider`. |
| `Intake:QueueName`, `Intake:ConnectionString`, `Intake:QueueServiceUri`, `Intake:FullyQualifiedNamespace` | Queue settings. The default queue is `private-corp-tickets`. The URI and namespace forms use managed identity. |
| `Intake:MaxDeliveries`, `Intake:PollIntervalSeconds`, `Intake:BatchSize`, `Intake:VisibilityTimeoutSeconds` | Poison handling and polling. A Storage Queue message that fails `MaxDeliveries` times (default 5) moves to `<queue>-poison`. Service Bus dead-letters it. |
| `Tickets:DefaultAssignmentGroup`, `Tickets:PrefixByType`, `Tickets:IntakeReceiptTtlSeconds` | Defaults for new tickets. With `PrefixByType`, numbers look like `INC-000123` or `REQ-000124`. `IntakeReceiptTtlSeconds` is how long event-id dedupe records are kept (default 14 days). |
| `Llm:Roles:<role>:Provider` | `offline`, `anthropic`, `openai`, `azure_openai`, `grok`, `ollama` or `lm_studio`. AgentDesk uses the `assist` role (chat) and the `embedding` role. |
| `Llm:Roles:<role>:Model`, `Endpoint`, `ApiKey`, `ApiVersion`, `MaxTokens`, `Dimensions` | Per-role model settings. For Azure OpenAI, `Model` is the deployment name; leave `ApiKey` empty to use managed identity. Keys fall back to `ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `XAI_API_KEY` or `AZURE_OPENAI_API_KEY`. |
| `Assist:Role`, `Assist:MaxTimelineEntries`, `Assist:MaxContextChars`, `Assist:MaxTokens`, `Assist:Temperature` | How much of the ticket is sent to the model, and the generation settings. |
| `Kb:Embedding:Role`, `Kb:Embedding:MaxInputChars`, `Kb:Embedding:MaxAttempts` | Embedding role (default `embedding`) and input limit. A failed embedding is retried up to `MaxAttempts` times. |
| `Kb:Embedding:SweepEnabled`, `SweepIntervalSeconds`, `SweepBatchSize` | The background sweep that retries failed or pending embeddings (on, every 60 s, 20 at a time). |
| `Dlp:*` | The same redaction rules as the intake (see `../helpdesk/README.md`). The `Model` destination applies to assist prompts. The `Embedding` destination applies to KB text before it is embedded. |
| `Auth:UseDevAuth`, `Auth:Enforce`, `Auth:Authority`, `Auth:Audience`, `Auth:SpaClientId`, `Auth:ApiScope`, `Auth:Anonymous:*` | See "Authentication". |
| `Cors:AllowedOrigins`, `RateLimit:AssistPerMinute`, `RateLimit:HubTicketPerMinute` | Browser origins, and per-user limits for AI assist (default 20/min) and hub connection tickets (default 30/min). |
| `OTEL_EXPORTER_OTLP_ENDPOINT`, `Serilog:*` | Observability. The same setup as the intake, plus the activity sources `AgentDesk.Intake`, `AgentDesk.Assist` and `Helpdesk.Llm`. |

### Production embedding setup (Azure OpenAI)

```json
"Llm": { "Roles": {
  "assist":    { "Provider": "azure_openai", "Endpoint": "https://<resource>.openai.azure.com/", "Model": "<chat-deployment>", "MaxTokens": 2048 },
  "embedding": { "Provider": "azure_openai", "Endpoint": "https://<resource>.openai.azure.com/", "Model": "<text-embedding-3-small-deployment>", "Dimensions": 1536 }
} },
"Cosmos": { "EmbeddingDimensions": 1536 }
```

## Data model

The model follows the common shape of ServiceNow, Zendesk, Jira Service Management and Freshdesk.

Every document carries:
- `id`, `tenantId` (the partition key), a `docType` discriminator and `schemaVersion`.
- Audit fields.
- An ETag. Updates accept an optional `If-Match`, and a stale ETag returns 412.

**Ticket** (`tickets` container, `docType: "ticket"`)
- Identity and type:
  - `number` (`INC-000123`, from a per-tenant counter)
  - `subject`, `description`
  - `type`: incident, service_request, question or problem
- Status and priority:
  - `status`: new, open, pending, on_hold, resolved, closed or cancelled. Transitions are enforced by a state machine.
  - `pendingReason`
  - `impact` × `urgency` gives `priority` (P1 to P4)
- Classification:
  - `category`/`subcategory`, aligned with the categories in the evals prompt
  - `tags`
- People:
  - `requester`
  - `assignmentGroup`, `assignee`, `watchers`
- Origin:
  - `channel`: chat_intake, portal, email, phone or agent
  - `source`: the intake event id, conversation id and correlation id
- Links: `linkedTickets`, `kbArticleIds`.
- SLA (`sla`):
  - `firstResponseDueAt` and `resolutionDueAt`, derived from priority
  - `firstRespondedAt`
  - breach flags
- Resolution (`resolution`): code, notes, by and at.
- Activity: `attachmentCount`, `lastActivityAt`.

**Timeline**

Timeline entries live in the same partition as their ticket:
- `comment`:
  - `visibility`: public or internal
  - markdown `body`, `attachmentIds`
  - `citedKbArticles`, each with id, number, version and title
  - `aiAssisted`: feature, provider, model and prompt version
- `event`: an audit record with a field diff, covering created, status changes, assignment, field changes, attachments, KB published and KB cited.

Intake dedupe records (with TTL) and the per-tenant number counters live in the same container.

**KB article** (`kb` container, `docType: "kbArticle"`)
- Content and lifecycle:
  - `number` (`KB0001234`)
  - `title`, `summary`, markdown `body`
  - `symptoms`, `cause`, `resolution`
  - `category`, `tags`
  - `status`: draft, published or archived
  - `visibility`: internal or public
  - `version`, `sourceTicketIds`
  - `publishedAt`, `reviewDueAt`
- Embedding fields:
  - `embedding` (float32[])
  - `embeddingProvider`, `embeddingModel`, `embeddingDimensions`
  - `embeddingContentHash`, `embeddedAt`
  - `embeddingStatus`: pending, ready, failed or disabled, plus `embeddingError`
- Every published version is kept as a `kbVersion` snapshot.

**Attachment** (`attachments` container)
- File details: `ticketId`, `fileName`, `contentType`, `size`, `sha256`, `blobPath`.
- `status`: pending_upload, available, quarantined or rejected, plus `scanResult`.

## API overview (`/api/v1`)

Errors are RFC 9457 Problem Details. Every response carries `X-Correlation-Id`.

| Area | Endpoints |
|---|---|
| Tickets | `GET /tickets` (filters: status, priority, assignee, unassigned, group, category, text, breached; sort; continuation token), `POST /tickets`, `GET/PATCH /tickets/{id}`, `POST /tickets/{id}/assign`, `POST /tickets/{id}/transition` |
| Timeline | `GET /tickets/{id}/timeline`, `POST /tickets/{id}/comments` (`visibility`, `body`, `attachmentIds`, `citedKbArticleIds`) |
| Attachments | `POST /tickets/{id}/attachments` returns a short-lived upload URL. The browser `PUT`s the file straight to Blob Storage, then calls `POST .../{attachmentId}/complete`, which checks the size and, if one was declared, the SHA-256. `GET .../{attachmentId}/download` returns a read URL. |
| Knowledge base | `GET/POST /kb/articles`, `GET/PUT /kb/articles/{id}`, `GET /kb/articles/{id}/versions`, `POST /kb/articles/{id}/publish`, `/archive`, `/restore` and `/reembed` |
| AI assist | `POST /tickets/{id}/assist/suggest-solutions`, `/draft-reply`, `/draft-kb`, `/reply-with-kb` and `/improve` |
| Meta | `GET /meta/vocabulary` (statuses, priorities, categories), `GET /meta/config` (public auth settings for the SPA), `GET /me` |
| Realtime | `POST /hub/ticket` (a single-use connection ticket), then the SignalR hub `/hubs/tickets`. Events: `ticketCreated`, `ticketUpdated`, `timelineEntryAdded`, `kbChanged`. |
| Ops | `/healthz`, `/readyz`, `/status` (admin only: configuration checks for storage, blobs, intake and LLM roles) |

## Attachments from the helpdesk chat

Files a requester attaches in the helpdesk chat (pasted logs, documents, images) travel with the ticket event as `attachments`. Each entry has `id`, `fileName`, `contentType`, `size`, `sha256`, `blobPath` and `scanStatus`.

**The two services share one blob container.** The helpdesk writes the files to `intake/{tenantId}/{conversationId}/{attachmentId}/{fileName}` in the container it is configured with, and AgentDesk records each one against the new ticket without copying it. This needs:

- `Blobs:Container` here to be the same container as the helpdesk's `Attachments:Container` (both default to `ticket-attachments`), on the **same storage account** (the same `ConnectionString` or `ServiceUri`).
- Read access for AgentDesk to the `intake/` folder. Downloads use the usual short-lived SAS URL for the stored `blobPath`.
- Locally, the in-memory blob store cannot see helpdesk files, because it lives inside one process. Run Azurite and set `Blobs__Provider=Azure` with `Blobs__ConnectionString=UseDevelopmentStorage=true` here, and the matching `Attachments__Provider=Azure` on the helpdesk. Without that, imported attachments are listed as missing and not shown.

**How an import behaves**
- One attachment document per file, with an id derived from the event, so a redelivered event adds nothing. The ticket's `attachmentCount` and timeline are updated like any other attachment.
- A `blobPath` is accepted only if it starts with `intake/{tenantId}/` for the ticket's own tenant and has no `..`, empty segments, backslashes, a leading `/` or control characters (and is under 1,024 characters). Anything else is skipped with a warning, and the ticket is still created.
- At most `Attachments:MaxPerTicket` are imported. A file that is not in blob storage is stored as `rejected` (`missing_blob`) and is not listed. A storage error while checking does not drop the file.
- Imported files bypass the agent-upload type allowlist, because the helpdesk validated them. Agent uploads still use it (`.md` and `text/markdown` are now allowed).

**Chat attachments are not scanned.** The helpdesk has no malware scanner yet, so these files arrive with `scanStatus: not_scanned`, and AgentDesk stores that value as the attachment's `scanResult`. It is never shown as clean. Treat them as untrusted when opening them, until a scanner is added on the helpdesk side (the same `IAttachmentScanner` seam exists here for agent uploads).

## Knowledge base and the "reply with KB" flow

1. In the ticket's AI panel, the agent clicks **Draft KB + reply**. `reply-with-kb` returns:
   - a KB draft built from the ticket and its timeline (title, summary, symptoms, cause, resolution, tags)
   - a reply that cites it through a `{{KB}}` placeholder

   The prompt instructs the model not to invent facts that are not in the ticket.
2. The agent reviews and edits both drafts, which are labelled "AI-generated". **Save article & send reply**:
   - creates the article as a draft, linked to the ticket through `sourceTicketIds`
   - replaces the placeholder with the article's number and title
   - posts the public reply with `citedKbArticleIds`
3. A public reply that cites a draft **publishes it in the same operation**. The timeline records `kb_published` and `kb_cited`, and the reply counts as the first response for the SLA. An internal note can cite a draft without publishing it.
4. **Embeddings on save.** Every create, update or publish builds the embedding input:
   - The input is the title, summary, symptoms, cause, resolution, body and tags.
   - It is redacted for the `Embedding` DLP destination.
   - It is hashed. Unchanged content with the same provider and model is not re-embedded.

   The new vector is stored on the article document. If the provider is unavailable, the article still saves with `embeddingStatus: failed`, and the background sweep retries it. `POST /kb/articles/{id}/reembed` forces a refresh, for example after changing the embedding model. The editor shows the embedding status badge.

Other assist features:
- **Suggest solutions**: likely causes, steps, and classification and priority suggestions with a rationale.
- **Draft reply**: tone, instructions, and optional KB articles to cite.
- **Improve**: rewrite, shorten, make friendlier or fix grammar on the composer text.

Every feature returns a draft. Nothing is saved until the agent sends or saves it.

All model inputs are handled the same way:
- They are treated as untrusted. Ticket text is DLP-redacted for `Model` and wrapped with `PromptInput.Wrap`.
- Outputs use JSON-schema structured output and are validated. Prompts are versioned, and the version is recorded in `aiAssisted`.

## Authentication (planned, not enforced)

The API picks one of three modes at startup:

| Mode | When | Who the caller is |
|---|---|---|
| **dev** | Development with `Auth:UseDevAuth=true` (the default in `appsettings.Development.json`) | Taken from `X-Dev-Tenant`, `X-Dev-User`, `X-Dev-Name` and `X-Dev-Roles`. |
| **anonymous** | `Auth:Enforce=false` (today's default elsewhere) | The fixed `Auth:Anonymous` identity (tenant `default`, user `agent`). A startup warning is logged; do not expose this publicly. |
| **jwt** | `Auth:Enforce=true` | Entra ID bearer tokens for the API. Tenant from `tenant_id`/`tid`, user from `oid`/`sub`, roles from `roles`. The hub accepts only single-use tickets bought with the JWT, never a raw token in the URL. |

Roles and policies are already applied to the endpoints, so turning auth on needs configuration only:
- `AgentDesk.Agent`: tickets and assist.
- `AgentDesk.KbAuthor`: KB authoring and publishing.
- `AgentDesk.Admin`: `/status`.
- `AgentDesk.Requester`: reserved for a future requester portal.

Every query is scoped to the caller's tenant partition, and tenant and user ids may not contain `:`.

The SPA includes an MSAL scaffold (`src/lib/auth.ts`). It turns on when the `VITE_ENTRA_*` variables are set; see `frontend/.env.example`.

## Tests

```powershell
cd backend; dotnet test AgentDesk.slnx    # domain rules, services, API (WebApplicationFactory), intake consumer, embeddings, DLP
cd frontend
npm run lint; npm run typecheck
npm test                                  # vitest + Testing Library + axe (API client, formatting, App flows)
npx playwright install chromium           # once
npm run test:e2e                          # Playwright against a mocked API on :5175, with axe checks
```

The backend tests use the in-memory stores and offline providers, so they need no emulator or API key. `AgentDeskFactory` pins the `assist` role back to `offline`, overriding the Anthropic setting in `appsettings.Development.json`.

## Known gaps

- **No RAG or semantic search.** KB search is a text filter. Vectors are stored and indexed for later.
- **Attachment scanning.** `IAttachmentScanner` accepts everything. Wire in Defender for Storage malware scanning before production.
- **SLA.** SLA targets come from a fixed priority table on a 24×7 clock. There are no business calendars or per-tenant policies yet.
- **Scale-out.** Hub tickets are kept in memory, and SignalR has no backplane, so multiple instances need Azure SignalR Service and a shared ticket store.
- **Intake contract.** The intake gets no status or acknowledgement callback.
- **Not built yet:**
  - requester portal
  - email channel
  - CSAT
  - assignment-group administration UI
  - IaC/CI
