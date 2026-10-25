# Design decisions

This records how the service-desk assistant is designed and why, so a later change can tell a deliberate choice from an accident. Each entry says what was decided, the reason, what it costs, and where it lives. It covers the intake service (`helpdesk/`), its effect on AgentDesk (`agentdesk/`), and the eval harness (`evals/`, `python-evals/`). Configuration keys and endpoints are in `helpdesk/README.md`; this file is about intent.

Entries are grouped by theme and numbered so they can be referred to. "Decided by the owner" marks choices that were the owner's call rather than a default I picked; the rest were my recommendations, accepted.

## Contents

1. [Product shape](#1-product-shape)
2. [Conversational hand-off](#2-conversational-hand-off)
3. [Policy requests and refusals](#3-policy-requests-and-refusals)
4. [What a ticket contains](#4-what-a-ticket-contains)
5. [Data protection (DLP)](#5-data-protection-dlp)
6. [Model vendors and defaults](#6-model-vendors-and-defaults)
7. [Model tiers and routing](#7-model-tiers-and-routing)
8. [Attachments](#8-attachments)
9. [Evals](#9-evals)
10. [Cross-cutting rules](#10-cross-cutting-rules)
11. [Known gaps and rejected alternatives](#11-known-gaps-and-rejected-alternatives)
12. [Browser (UI) tests](#12-browser-ui-tests)

---

## 1. Product shape

**D1.1. A small chat shell that behaves like a helpdesk operator.** It resolves common issues using what it has learned (the knowledge base) and internet-available resources (web search). If information is missing it asks for it in conversation. If it cannot resolve the issue it opens a ticket for a human. *Decided by the owner.* The repository had drifted into building a second product (AgentDesk) and plumbing; this was the anchor for pulling it back.

**D1.2. The language model drives information gathering, not predefined forms.** The earlier design generated a form and held the ticket back until it was submitted. That was removed (see D2.1). *Decided by the owner.*

**D1.3. Requests that cannot be done as asked get educational guidance on the correct approach.** The standing example: grant access to the files instead of resetting a colleague's password. That becomes a ticket for the correct action so a human can do it. Later this may be automated through Microsoft Graph and PIM; nothing here prevents that. *Decided by the owner.*

**D1.4. AgentDesk is a downstream consumer, not the focus.** It was frozen except where the ticket contract changes (summary fields, attachments). Knowledge-base retrieval in the intake was built because AgentDesk already writes the articles; it reads AgentDesk's Cosmos schema directly.

---

## 2. Conversational hand-off

**D2.1. A model-run "ticket interviewer" replaces the follow-up form.** On the `PRIVATE_CORP` lane each turn calls `ITicketInterviewer` with the redacted conversation. It returns structured output: either `raiseTicket: false` with the next question, or `raiseTicket: true` (a draft ticket) with a title, summary, conversation summary, recommended action and steps tried. *Why:* the form was a predefined path that fought the stated design (D1.2), made the user fill in what they had already said, and could not adapt. The user confirms the draft before anything is queued (D2.6). *Where:* `ChatPipeline.HandOffAsync`, `AnthropicTicketInterviewer`, `Prompts.TicketInterviewer`.

**D2.2. The escalation token, the escalate endpoint and the form generator were deleted,** not left dormant. Dead paths with their own tests invite drift. The frontend `IntakeFormCard` went with them.

**D2.3. The model decides when it has enough; a code backstop stops interviews that never end.** `Chat:MaxInterviewTurns` (default 8). At the limit the interviewer is told, in its system prompt, to draft now and note what is unknown; if it asks another question anyway, the pipeline drafts a minimal ticket itself (title from the first message, a summary saying details may be missing, the start of each message). *Why a backstop and not a fixed question limit:* a number written into the prompt was an arbitrary cap I had added; the owner chose model-led gathering with a hard stop in code. *Known limitation:* the history carries no marker for which turns were hand-off turns, so the count is all user messages in the conversation. A user who troubleshoots for eight turns and then asks for a ticket is shown a draft immediately, with no questions (they can still change or decline it, D2.6). Fixing it properly needs a signed lane marker in the history (not done). The prompt's softer instruction is "do not keep asking: stop once what is missing is something the support team can find out themselves".

**D2.4. A retried identical request raises the same event.** The event id is a hash of tenant, user, conversation and history length, so a retry republishes the same id and AgentDesk deduplicates. A *reworded* retry is now less likely to create a second ticket, because the user has to confirm a draft first (D2.6), but it can still happen. The interviewer prompt also tells the model not to raise a second ticket for something it already confirmed.

**D2.5. The ticket is raised before the reply is sent, and files are marked only after the publish succeeds.** A failed publish leaves the files to expire normally and the user sees an error rather than a false "sent" message.

**D2.6. The user confirms a draft before any ticket is queued.** The interviewer's `raiseTicket: true` now means "a draft is ready", not "send it". The pipeline replies with the draft (the model's lead-in, then everything the operator will get: title, summary, conversation summary, suggested action, steps tried and file names, quoted) ending with a fixed prompt, `Prompts.TicketConfirmPrompt`, and sets `metadata.ticketPending = "true"`. The next user message answers it. The interviewer is told a draft is pending (`proposalPending`) and returns `userConfirmed`: true to agree, false with an updated draft to amend, or `raiseTicket: false` to decline. *Code gate:* the pipeline queues only if a draft was pending **and** `userConfirmed` is true. A model that says "confirmed" with no draft pending is ignored. *Statelessness:* "a draft is pending" is read from the signed history: the last message must be an assistant message ending with the confirmation prompt. The client cannot forge it (unsigned history is dropped), and a draft the user has moved past no longer counts. The UI shows "Send to the service desk" and "Don't send" buttons on the latest draft; they send ordinary messages ("Yes, send it to the service desk.", "No, don't send it."), so typing an answer works equally well and the wire format is unchanged. *Why this and not a signed draft token:* it needs no new request fields and keeps the model-led design (D1.2). *Cost:* (1) the confirmed ticket is written again by the model, told to keep the fields the user saw, so wording can differ from the draft; a token carrying the exact draft would remove that. (2) The classifier still runs on the answer, so its prompt carries an example that keeps replies to a draft in `PRIVATE_CORP`; that example has not been measured against the real model. (3) No KB articles are appended to a draft, because the prompt must be the last text. (4) Each part of the draft is clipped so the whole fits the 4,000-character history limit. *Where:* `ChatPipeline.HandOffAsync`, `ProposalText`, `IsProposalPending`, `AnthropicTicketInterviewer`, `Prompts.TicketInterviewerConfirmTurn`, `ChatMessageResponseBubble`.

---

## 3. Policy requests and refusals

**D3.1. Policy requests are classified, then answered by a "coach".** A regex pre-filter and the classifier route requests such as another person's password, bypassing MFA, or evading controls to `POLICY_VIOLATION`; a separate coach prompt writes the educational reply, and the interviewer shapes any ticket around the correct route (D1.3). The coach is a different task from the one the user asked for ("write guidance"), which is why it is refused far less often.

**D3.2. Role-play and "turn off the antivirus" requests go to the policy lane.** The classifier prompt lists a persona that ignores the rules, and switching off or working around security software on a managed device, as policy violations. The pre-filter also covers "reset" and "change" for another person's password and "his/her/their" phrasings.

**D3.3. The model's own refusals (`stop_reason: refusal`) must never reach the user as an empty or cut-off answer.** The API can decline a request outright or part-way through a reply; before this change the helpdesk ignored the signal (an empty refusal became a 500, a partial one was shown as complete). Now `AnthropicChatProvider` throws `LlmRefusalException`, and:
- the **classifier** treats a refusal as a policy block;
- in an **answer lane or the hand-off** the pipeline switches to the coach prompt, tries it **twice** (refusals are intermittent: the same request is often answered on the next attempt; measured at about two refusals in six on the two problem inputs), and falls back to **static educational text** if both are refused;
- nothing is queued on that path, and the reply is marked as a policy reply.
This is the owner's required workaround for "educational advice even when the model declines". *Limitation:* a streamed answer may already have shown part of its text; the final reply replaces it.

**D3.4. Refusals cannot be fixed with the system prompt, so the evals do not try.** Two eval cases fail intermittently because the harness sees a refusal before any prompt matters (D9.5). The production safety net exists; the harness cannot see it.

---

## 4. What a ticket contains

**D4.1. The operator gets summaries, not a transcript.** The owner asked for conversation history to be available for context, but summarised. The interviewer writes two **separate** fields: `summary` (what the user needs now) and `conversationSummary` (a short chronological account of what was asked, tried and said, keeping contact details, device names and error messages exactly as given). *Decided by the owner:* summary and conversation in different fields. They are separate top-level fields on `ProcessPrivateCorpTicketEvent`, repeated as labelled answers in `details` and as text in `sanitizedPayload` for consumers that ignore the structured fields. AgentDesk stores them as `Ticket.Description` and `Ticket.ConversationSummary` and shows "Conversation so far" separately.

**D4.2. The contract change is additive.** New optional fields; `schemaVersion` stays 1. Consumers must ignore unknown fields and treat the new ones as possibly absent, and AgentDesk falls back to `sanitizedPayload` for older events.

**D4.3. Summaries are written from the Model-redacted conversation and redacted again for the Queue destination,** so the ticket cannot carry a secret the model never saw, or one the model reproduced.

**D4.4. Recommended action and steps already tried are first-class.** The recommended action is where the "correct route" for a refused request goes (D1.3).

---

## 5. Data protection (DLP)

**D5.1. Only true secrets and card numbers are redacted by default for the model and the ticket.** *Decided by the owner:* redaction loses information that the model and the support team need (an email address, a phone number or an account number is often what the problem is about). Secrets (keys, tokens, `password=`-style assignments, URL credentials, private keys) and Luhn-valid card numbers are always redacted and cannot be allowed. Email, phone, government ids and IBANs now pass through to the model and the queue.

**D5.2. Third parties and stored vectors still get everything redacted.** `Search` (a web search provider, including Claude's own tool) and `Embedding` (KB vectors) redact the personal categories too: a third party or an index never needs them. This stricter default is mine, not the owner's; it is configurable per destination and per tenant (`Dlp:Destinations`, `Dlp:Tenants`).

**D5.3. The defaults live in the shared `Helpdesk.Dlp` library, so AgentDesk inherits them.** A deliberate side effect.

**D5.4. DLP is best effort, not a product.** Free text without a recognisable format (names, addresses) is not caught, and it covers outbound text only.

---

## 6. Model vendors and defaults

**D6.1. A pluggable vendor library, fixed per run.** `Helpdesk.Llm` (shared with AgentDesk) offers Anthropic, OpenAI, Azure OpenAI, Grok, Ollama, LM Studio and an offline provider behind `IChatProvider` and `IEmbeddingProvider`, selected by role in `Llm:Roles:<role>`. The vendor is chosen at startup. *Decided by the owner:* if dynamic switching is ever added it will be at the vendor level.

**D6.2. Anthropic is the default.** With no `Llm:Roles:chat`, an `Anthropic:ApiKey` (or `ANTHROPIC_API_KEY`) selects it with `claude-sonnet-5-5`. An explicit chat role wins and needs no Anthropic key. Outside Development the app refuses to start with no chat provider, so the offline stand-ins can never run in production.

**D6.3. Search and embeddings follow the chat vendor where it offers them** (`ProviderAffinity`). *Decided by the owner.*
- **Search:** an explicit `Search:Provider` wins; then a third-party provider that already has credentials (including the legacy `Search:ApiKey`, which means Tavily), so existing setups do not change; then Claude's own web search when the chat vendor is Anthropic (no extra key); otherwise Tavily, a stub until it has a key.
- **Embeddings:** with no embedding role, one is derived from an **OpenAI** chat vendor (`text-embedding-3-small`, 1536 dimensions, same endpoint and key). Anthropic and Grok have no embeddings API; Azure OpenAI, Ollama and LM Studio need a deployment or model name only the operator knows, so nothing is guessed for them.
- **Consequence:** the knowledge base compares vectors only from the same provider and model, so AgentDesk's `embedding` role must match. AgentDesk has no equivalent derivation. In Development an explicit offline embedding role is configured, so the derivation never applies there.

**D6.4. The legacy `Anthropic` section remains the chat role when no explicit role is set,** so existing deployments keep working. Class names such as `AnthropicGuardrailRouterService` are historical; they run on any vendor. (Renaming them, and moving the tuning options out of `Anthropic:`, is a safe refactor not yet done.)

**D6.5. Native web search needs a vendor with server-side tools,** which today means Anthropic; `/status` fails a configuration that asks for it with another vendor.

---

## 7. Model tiers and routing

**D7.1. Goal: cost.** *Decided by the owner.* Routing sends cheap questions to cheap models and keeps the expensive model for work that needs it.

**D7.2. Three tiers per vendor: Fast, Standard, Deep.** Each vendor provides them; a vendor with fewer models serves several tiers with the same one (a single local model for LM Studio, for instance). *Decided by the owner.* Anthropic defaults are Haiku, Sonnet and Opus. OpenAI, Azure OpenAI, Grok, Ollama and LM Studio have **no built-in tier models** (I do not know their current model ids and will not invent them): every tier uses the role's model until `FastTier:Model` and `DeepTier:Model` are set. `/status` states which model serves each tier and says when one model serves all.

**D7.3. The routing policy is fixed in code, not configurable.** *Decided by the owner.* `InferenceRoutePolicy`: Deep if the estimated input is at least 12,000 tokens or the classifier rated the request `complex`; Fast if rated `simple`, at most 1,500 tokens and at most four user turns; Standard otherwise. It applies to the `PUBLIC_TECH` and `PUBLIC_SEARCH` answers. Size is estimated at about four characters per token over the message, history, KB articles, search results and attached files. The thresholds are starting points and are meant to be tuned from the `helpdesk.inference.routes` metric.

**D7.4. The classifier runs on the Fast tier and also rates complexity** in the same call (`simple | standard | complex`), so routing costs no extra call. *Decided by the owner.* The classifier prompt says to rate from the content and to ignore any request about which model to use. The coach and the ticket interviewer stay on Standard (quality and safety of what the user reads).

**D7.5. Safety nets for the cheaper model.** An unusable fast classification is retried once on Standard before failing closed. A non-streamed Fast request that is refused is repeated once on Standard. A stream is not retried (text may already have reached the user). Neither retry happens when two tiers are the same model.

**D7.6. A tier model is never priced at the standard model's rates.** Tier costs are configured per tier or not estimated at all.

**D7.7. The classifier's output ceiling is 768 tokens (`Anthropic:RouterMaxTokens`).** Measured: at 256, about one Haiku classification in six was cut off by its own long reasoning note and had to be retried on Standard. At 512 it was about one in fifty. The prompt also asks for a one-sentence reasoning.

**D7.8. Live evidence for Haiku as classifier, with its limits.** On the labelled fixtures plus complexity probes (about 100 calls) Haiku classified every adversarial policy case correctly and rated complexity correctly 10 of 10. The fixtures hold only 23 labelled messages, so this is a smoke test, not proof of equal safety.

**D7.9. No tenant restrictions.** *Decided by the owner for now.* A user can make a question look complex to force the Deep tier; spend is bounded only by the per-user rate limit and the per-tenant job limit. Noted in the README.

**D7.10. The vendor is never switched mid-run, and tier selection is stateless** (re-derived each turn from the request), consistent with the service keeping no conversation state.

---

## 8. Attachments

Requested capability: office documents, PDF, text, CSV, log files and images (not video); pasting a log turns it into an attachment.

**D8.1. Supported types (modern formats only).** *Decided by the owner.* Text (`.txt .log .csv .json .md`), images (`.png .jpg .jpeg .gif .webp`), `.pdf`, `.docx .xlsx .pptx`. Refused: video, audio, archives (`.zip`), executables, `.eml/.msg/.har`, macro-enabled Office (`.docm`, anything with a `vbaProject`), password-protected and legacy binary Office (`.doc .xls .ppt`). The accepted list is configurable but can only be narrowed (an empty list means "everything supported").

**D8.2. The type is decided by content, not by name.** Magic bytes, the Office package structure (the right main part must exist), strict text decoding with binary detection, and rejection of text that is really a program, archive, PDF or image. A declared content type is ignored.

**D8.3. Upload goes through the API.** `POST /api/v1/chat/attachments` (multipart), not direct-to-blob, because the service must validate, extract and (one day) scan before use. AgentDesk's direct-to-blob design needs a ticket to exist; a chat does not have one. The body-size limit on that path is the file limit plus 1 MB instead of the general 128 KB.

**D8.4. A file is referenced by a signed token, not by re-signing messages.** The `AttachmentRef` carries an HMAC over the attachment id bound to tenant, user and conversation (the conversation signing keys). The server re-reads the real name, type and size from storage and ignores what the client says. Changing the message signature scheme was rejected as invasive; a user may attach any of their own uploads to any of their own messages, which is harmless. `ChatHistoryMessage` equality was overridden to compare attachments by content.

**D8.5. One container shared with AgentDesk, no copying.** *Decided by the owner.* Files live at `intake/{tenantId}/{conversationId}/{attachmentId}/content` in the container AgentDesk reads (`Blobs:Container`, same account). The ticket event carries the blob path and AgentDesk records the attachment against the ticket without copying. *Security:* AgentDesk accepts only paths under `intake/{its own tenant}/`, with no `..`, empty segments, backslashes or control characters, and a bad attachment never fails the ticket import. *Consequence:* the two services must agree on the container, and the in-memory dev store cannot be shared (use Azurite for both).

**D8.6. Files are kept until a ticket is raised, then kept for good; otherwise deleted after 24 hours.** A background job deletes old files from conversations that never raised a ticket; a file on a raised ticket is marked with the event id and never touched by it. Retention and the sweep interval are configurable.

**D8.7. Storage defaults to off outside Development.** With no `Attachments:Provider`, Development uses in-memory storage and everywhere else the feature is **off** (uploads refused, references ignored, `/status` says so), so production never holds files in memory by accident. The `NullAttachmentService` implements "off".

**D8.8. Limits are configurable.** *Decided by the owner.* Per file 10 MB, per image 5 MB (Claude rejects larger), 5 files per message, 20 and 25 MB per conversation, token budgets for what the model reads, parsing caps, paste thresholds, retention. `/status` validates them.

**D8.9. No malware scanning, with a documented placeholder.** *Decided by the owner.* `IAttachmentScanner` is the seam; `NoOpAttachmentScanner` returns `not_scanned`. That status is stored with the file, sent on the ticket event and carried into AgentDesk, which never presents it as clean. The README states it plainly. Mitigation in the meantime: files are never executed, and Office files are read as XML, never opened by Office.

**D8.10. Images go to the model as they are.** *Decided by the owner.* They cannot be redacted like text; a screenshot containing a secret or personal data is sent to the model provider. Documented as a privacy note. OCR-based redaction is the future option.

**D8.11. The stored original is not redacted.** Redaction applies to what the model sees and to the ticket's summaries. An agent downloading the file gets it as uploaded, secrets included. (Redacting the original would defeat its purpose as evidence.)

**D8.12. Text is reduced, not truncated, and redacted after reduction.** A log is mostly noise around a few failures, so `ContentReducer` keeps the head (a quarter of the budget), the tail (35%) and error-like lines from the middle with a line of context each, and says how many lines were left out. Redacting after reducing keeps the scan small and means secrets in dropped lines never leave the service.

**D8.13. A cost and context budget per request.** Current-message files get up to 20,000 tokens each, earlier-message files a 1,500-token excerpt, all files together 40,000; past that a file is listed but not shown. A long log is therefore not re-sent in full on every turn. The interviewer reads every file as an excerpt, for the same reason.

**D8.14. File text is untrusted data.** Each file is wrapped with a per-file nonce, with guidance added to the system prompt only when files are present. File names are user-controlled, so a name appears only inside wrappers.

**D8.15. Images and scanned PDFs are sent natively when the vendor can read them.** `LlmCapabilities.Vision` and `Documents`, and `ChatRequest.Parts`. Anthropic reads both; OpenAI-style providers read images (documents are given as extracted text). A vendor that cannot (default for Ollama and LM Studio, overridable with `SupportsVision`) is told which files it was not given and says so, rather than pretending. Earlier messages' images are not re-sent; they are named.

**D8.16. Attachment size feeds the routing decision,** so a big log lands on the Deep tier (D7.3). Images and scanned PDFs use a fixed estimate.

**D8.17. Paste-as-attachment is a client behaviour.** Text pasted at 1,500 characters or 25 lines (configurable, served by the config endpoint), or that looks like a log or stack trace, becomes `pasted-log-<time>.txt` with a "Paste as text instead" undo. Clipboard images and drag-and-drop work too. The attach button, drop and paste conversion are hidden when the server reports `enabled: false`. Only the browser path converts; an API client posting a long message is still held to the 4,000-character limit.

**D8.18. The attachments dependency.** PDF text uses the `PdfPig` package (version 0.1.16). The NuGet id `UglyToad.PdfPig` is **not** the maintained library (it carries an odd pre-release); `PdfPig` is. Office text uses hand-written, bounded XML readers rather than a heavier SDK. Azure access uses `Azure.Storage.Blobs`.

**D8.19. A gotcha recorded for maintainers.** `XmlReader.ReadElementContentAsString` moves *past* the end tag, so a loop that then calls `Read()` skips the next node (it silently dropped every shared string in spreadsheets). The extractor uses a helper that leaves the reader on the end tag.

---

## 9. Evals

**D9.1. The prompt and cases are the source of truth** (`evals/system-prompt.md`, `evals/cases.json`); the harness (`python-evals`) measures a variant (`v1`, `v2`, …) against them. The eval prompt describes the *behaviour we want*; the C# prompts (`Prompts.cs`) implement the app. They overlap in spirit but are separate artefacts, and the evals do not exercise the interviewer, the classifier, tier routing or attachments (no harness exists for them yet).

**D9.2. Behaviour the eval prompt encodes now.** Help before handing off (small safe steps, one at a time, acknowledge what was already tried); no steps that delete data, weaken security or need admin access; **urgent incidents skip self-help** (several people down, a possible compromise such as credentials typed into a phishing page) and are handed off at once with what is unknown noted; **company-managed systems and shared equipment** (VPN, locked-down software, file servers, a shared printer) go to the support team once the user's own checks are done; re-checking a connection counts as repeating "unplug and re-plug"; suspicious credential requests relying on a claimed role are recorded as security concerns; a decline gives plain-language guidance first and then the JSON record on its own; `user_conversation` is copied verbatim.

**D9.3. Cases are never deleted to make a version pass; stale expectations are rewritten.** When the prompt's policy changed (conversation first, ticket when appropriate) several cases still demanded an immediate JSON ticket; their wording was relaxed to "if a ticket record is produced" and similar. The owner approved this. Cases that exposed real weaknesses were left unchanged until the prompt fixed them.

**D9.4. Results.** `v2` 20/32, `v3` 25/32, `v4` 28/32, `v5` 31/32. One sample per case has about ±18 points of noise, so these show direction, not exact rates. The remaining failure is an API-level refusal.

**D9.5. Refusals are scored separately and fail the case.** The harness treats `stop_reason: refusal` as a refusal metric and no output. Two role-play and fake-authority cases refuse intermittently. The production path handles this (D3.3); the harness measures the bare model, so it cannot show that. A `fallbacks` option exists in `evals.toml` to mirror server-side refusal fallbacks; it was not enabled because production does not use that mechanism.

**D9.6. Live checks of safety-relevant changes.** Any change that moves a safety decision to a different model (D7.7, D7.8) was checked against the real API, not only with unit tests.

---

## 10. Cross-cutting rules

**D10.1. One pipeline, fail-closed order.** Verify signed history, redact for the model, classify (pre-filter then model), route by lane. Nothing (inference, search, queue) runs before classification succeeds. Attachments are resolved before routing but only shown to the model after classification.

**D10.2. The service is stateless and single-instance.** The client keeps the transcript and the server signs it. In-memory queue, limiters, hub tickets and (in Development) attachments mean one instance only; this is in the README's known gaps.

**D10.3. Tenancy.** Every endpoint requires a tenant claim; tenant and user ids must not contain `:` (composite keys), and attachment paths additionally refuse `/`, `\`, `..` and control characters.

**D10.4. Observability without content.** Logs and metrics carry counts, kinds, tiers and categories; never message text, file names, file content or tokens.

**D10.5. Testing approach.** Offline fakes for every model and storage seam; live API calls only for the specific safety or cost questions above; each feature lands with unit and endpoint tests. The suite is 624 Api and 43 library tests at the time of writing, plus 33 browser tests (section 12), and the AgentDesk and frontend suites.

**D10.6. Line endings.** The repository has `core.autocrlf` on; working copies are CRLF and the index LF. The "LF will be replaced by CRLF" warnings from git are noise.

---

## 11. Known gaps and rejected alternatives

**D11.1. Resolved by D2.6: a ticket is only queued after the user confirms a draft.** What remains: the confirmed ticket is regenerated by the model from the conversation, so it can differ in wording from the draft (D2.6).

**D11.2. The interview backstop counts all user messages** (D2.3).

**D11.3. Cost exposure on the Deep tier** (D7.9) and no per-tenant budgets.

**D11.4. No malware scanning** (D8.9); **images are not redacted** (D8.10); **originals are not redacted** (D8.11).

**D11.5. Rejected: sending the whole transcript to operators.** Replaced by two summaries (D4.1).

**D11.6. Rejected: re-signing every message to cover attachments.** Replaced by per-file tokens (D8.4).

**D11.7. Rejected: dynamic routing across vendors.** Tiers stay within one vendor (D6.1, D7.2).

**D11.8. Rejected: a configurable routing policy.** Fixed in code on purpose (D7.3).

**D11.9. Rejected: copying files into AgentDesk on ticket creation.** A shared container avoids the copy and its failure modes (D8.5).

**D11.10. Not built.** Server-side conversion of oversized text from API clients into attachments; OCR and redaction of images; legacy Office formats; a real scanner; a signed lane marker in history; per-tenant cost controls; renaming the vendor-neutral classes that still say "Anthropic".

---

## 12. Browser (UI) tests

**D12.1. UI tests are C#, xUnit and PuppeteerSharp, in a separate project (`helpdesk/backend/tests/Helpdesk.UiTests`), using the Page Object Model.** *Decided by the owner.* They sit beside the other .NET tests, run with `dotnet test`, and share the team's language and tooling. They complement, and do not replace, the front end's own vitest and Playwright suites (which test components and a mocked API). PuppeteerSharp is the maintained `PuppeteerSharp` package (owner `kblok`, `hardkoded/puppeteer-sharp`).

**D12.2. They run the real front end against the real API, with only the model, search and ticket queue scripted.** The pipeline, hub, uploads, attachment checks and dev auth are the production code, so the tests prove the screen and the wiring end to end. The stand-ins (`ScriptedRouter`, `ScriptedInference`, `ScriptedInterviewer`, `RecordingPublisher`) are deterministic, offline and need no keys or Azurite, and they record what they were asked, so a test can assert what reached the model or the ticket (for example the attached files). Prompt and answer quality is out of scope here; that is the evals' job.

**D12.3. The browser needs a real port, so the API is hosted twice.** `WebApplicationFactory` serves from an in-memory server a browser cannot reach, so `HelpdeskApp` also starts the same app on Kestrel at a free loopback port (the well-known two-host workaround) and the browser uses that. The in-memory host exists only because the base class requires it.

**D12.4. The front end is built once, in development mode, into its own folder.** `vite build --mode development` into `helpdesk/frontend/dist-uitests` (git-ignored), rebuilt only when sources change. Development mode is what makes the app use dev auth instead of Entra sign-in. The API's `wwwroot` is never touched, and a prebuilt folder can be supplied (`HELPDESK_UI_SPA_DIR`).

**D12.5. An installed Chrome or Edge is used; Chromium is downloaded only as a last resort.** Avoids a 150 MB download in the common case and uses what the machine already has. `HELPDESK_UI_BROWSER` overrides.

**D12.6. Page objects hold all the selectors, waits and scripts; tests read like a person's actions.** `ChatPage`, `ChatMessage` and the `AttachmentTray` component object. Tests contain no sleeps or selectors; the page object waits for the screen to settle (finished reply, finished upload).

**D12.7. The front end carries stable test hooks.** Styling classes are never used as selectors. `data-testid` attributes (`user-message`, `assistant-message` with `data-lane`, `lane-label`, `message-body`, `error-message`, `live-reply`, `stopped-reply`) and the existing ARIA labels are the contract between the app and the page objects; they are noted in the helpdesk `CLAUDE.md` so a change to one is a change to the other.

**D12.8. Each test gets a fresh browser context and cleared recordings; the tests share one app and one browser.** Isolation without paying for a host and a browser per test. They run sequentially in one xUnit collection (about 40 seconds for 33 tests), and the suite passed three full runs in a row.

**D12.9. The solution includes the project, so a plain `dotnet test` now runs it too.** It needs Node.js and a Chromium-based browser. `dotnet test --filter "Category!=UI"` excludes it (every UI test carries `Category=UI`). A missing prerequisite fails with a message that says what to install. Not done: skipping gracefully when Node or a browser is absent (xUnit 2 has no built-in dynamic skip).

**D12.10. Limits, stated.** A scripted paste event cannot put text on the system clipboard, so for ordinary text the test asserts only that the app did **not** take the paste over (the browser would insert it); drag and drop is not covered; and the app has no favicon, so every page load 404s for `/favicon.ico`, which the tests ignore (a small finding about the app, not a test problem).
