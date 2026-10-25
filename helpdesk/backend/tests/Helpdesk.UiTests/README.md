# Helpdesk UI tests

Browser tests for the helpdesk chat, written in C# with xUnit and [PuppeteerSharp](https://github.com/hardkoded/puppeteer-sharp), using the Page Object Model. They drive the **real front end against the real API** in a real browser. Only the things that would call a model, a search provider or the ticket queue are replaced, by scripted stand-ins, so the tests are deterministic, fast and need no keys, network or Azurite.

## Running them

```powershell
cd helpdesk/backend
dotnet test tests/Helpdesk.UiTests                      # all of them (about 40 seconds)
dotnet test tests/Helpdesk.UiTests --filter "FullyQualifiedName~AttachmentTests"
dotnet test --filter "Category!=UI"                      # everything except the UI tests (the solution includes this project)
```

You need **Node.js on the PATH** (the first run builds the front end) and a **Chromium-based browser**. If Chrome or Edge is installed it is used; otherwise Chromium is downloaded once (about 150 MB). Settings, all optional:

| Variable | Meaning |
|---|---|
| `HELPDESK_UI_BROWSER` | Path to `chrome` or `msedge` to use. |
| `HELPDESK_UI_SPA_DIR` | A front-end build you made yourself (a folder with `index.html`), to skip the build. |
| `HELPDESK_UI_FRONTEND` | Path to `helpdesk/frontend` if it cannot be found from the source tree. |
| `HELPDESK_UI_TIMEOUT_SECONDS` | How long a test waits for the screen to catch up (default 15). |

The front end is built once into `helpdesk/frontend/dist-uitests` (git-ignored) with `vite build --mode development`, which is what makes the app use dev auth instead of signing in with Entra. It is rebuilt only when something under `frontend/` changed. The API's own `wwwroot` is never touched.

## How it is put together

```
Infrastructure/
  HelpdeskApp.cs        The real API on a real Kestrel port (WebApplicationFactory plus a second, Kestrel host,
                        because a browser cannot reach the in-memory test server) serving the built front end.
  ScriptedServices.cs   Stand-ins: ScriptedRouter (keywords -> lane), ScriptedInference (streams a canned answer, records
                        what it was asked, including attached files), ScriptedInterviewer (asks once, then raises),
                        RecordingPublisher (the ticket queue), plus an empty knowledge base and search.
  SpaBuild.cs           Builds and caches the front end.
  BrowserLocator.cs     Finds Chrome or Edge, else downloads Chromium.
  UiFixture.cs          Starts all of that once (xUnit collection fixture) and gives each test its own browser context
                        and an open ChatPage (UiTest base class). Collects browser errors, and failed requests with URLs.
Pages/
  PageObject.cs         Base: waits and script helpers. The only place that polls.
  ChatPage.cs           ChatPage (the conversation, the message box, files, paste), ChatMessage (what a message looks
                        like to a person) and AttachmentTray (the chips above the message box).
Tests/
  ChatTests.cs          Opening, sending, streaming, Enter and Shift+Enter, follow-ups, new conversation, markdown and
                        script safety, out-of-scope, errors, live search.
  HandOffTests.cs       Policy guidance, the access-request button, a ticket raised by conversation with no form, the
                        separate summary and conversation summary.
  AttachmentTests.cs    Choosing files, removing them, refused types, paste-a-log becomes an attachment, paste as text
                        instead, files reaching the model and the ticket.
```

### The Page Object Model here

- **Tests say what a person does and sees**, never selectors: `await Chat.AskAsync("My VPN is down.")`, `reply.Lane`, `Chat.Attachments.RemoveAsync("a.log")`.
- **Selectors live in one place per page object.** Where the markup gave nothing stable to hold on to, the front end has `data-testid` attributes (`user-message`, `assistant-message` with `data-lane`, `lane-label`, `message-body`, `error-message`, `live-reply`, `stopped-reply`) and ARIA labels (`Message`, `Attach files`, `Send a message`, `Attachments to send`). Styling classes are never used as selectors, so a restyle cannot break a test. If you change those hooks, change the page object.
- **Waiting is the page object's job.** Methods return when the screen is ready (`AskAsync` waits for the finished reply, `WaitForUploadedAsync` for the upload). Tests contain no sleeps.
- A **component object** (`AttachmentTray`, `ChatMessage`) is used for a part of the screen with its own behaviour. Add one the same way for a new part.

### Adding a test

1. Add a method to the page object if the screen can do something it cannot yet (and a `data-testid` to the front end if nothing stable exists).
2. Extend `ScriptedRouter` / `ScriptedInference` / `ScriptedInterviewer` if you need a new scripted behaviour; they record what they were asked so a test can check what reached the model or the queue.
3. Derive from `UiTest`, take `UiFixture` in the constructor, and write the test. Every test gets a fresh browser context and cleared recordings.

## Known limits

- **A scripted paste is not a real one.** A test cannot put text on the system clipboard, so `PasteAsync` dispatches a paste event and reports whether the app took it over. When the app leaves it alone, the browser would insert the text; that part is not exercised.
- **Drag and drop is not covered** (it needs real pointer events with files).
- The tests run the model, search and queue as stand-ins; they prove the screen and the wiring, not the quality of any answer. Prompt quality is the job of the evals (`python-evals`).
- The app has no favicon, so every page load gets a 404 for `/favicon.ico`; the tests ignore that one request.
