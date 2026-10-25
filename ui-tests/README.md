# UI tests

All of the repository's browser tests live here, in one .NET project, `ServiceDesk.UiTests`. They are written in C# with xUnit and [Microsoft Playwright for .NET](https://playwright.dev/dotnet/), using the Page Object Model, and they check accessibility with [axe-core](https://github.com/dequelabs/axe-core-nuget) (WCAG 2.0 and 2.1, levels A and AA). They are kept apart from the unit tests: this project is not in `helpdesk/backend/Helpdesk.slnx`, so `dotnet test` there never starts a browser.

| App | What the tests drive |
|---|---|
| **Helpdesk** (`HelpdeskUi/`) | The **real front end against the real API** in a real browser. Only the things that would call a model, a search provider or the ticket queue are replaced, by scripted stand-ins, so the tests are deterministic, fast and need no keys, network or Azurite. |
| **AgentDesk** (`AgentDeskUi/`) | The **real built front end** served from a small static host, with the API answered inside the browser by a stateful mock (Playwright route interception). AgentDesk's own API is not started. |

The front end's own unit and component tests (vitest, in `helpdesk/frontend` and `agentdesk/frontend`) are separate and stay there: they run in a simulated DOM, not a browser.

## Running them

```powershell
dotnet test ui-tests/ServiceDesk.UiTests                                  # all of them (about two and a half minutes, plus a front-end build the first time)
dotnet test ui-tests/ServiceDesk.UiTests --filter "FullyQualifiedName~AccessibilityTests"
dotnet test ui-tests/ServiceDesk.UiTests --filter "FullyQualifiedName~AgentDeskUi"
```

You need **Node.js on the PATH** (the first run builds each front end) and a **Chromium-based browser**. If Chrome or Edge is installed it is used; otherwise Playwright downloads its own Chromium once (about 150 MB). Settings, all optional:

| Variable | Meaning |
|---|---|
| `SERVICEDESK_UI_BROWSER` | Path to `chrome` or `msedge` to use. |
| `SERVICEDESK_UI_TIMEOUT_SECONDS` | How long a test waits for the screen to catch up (default 15). |
| `HELPDESK_UI_SPA_DIR`, `AGENTDESK_UI_SPA_DIR` | A front-end build you made yourself (a folder with `index.html`), to skip the build. |
| `HELPDESK_UI_FRONTEND`, `AGENTDESK_UI_FRONTEND` | Path to `<app>/frontend` if it cannot be found from the source tree. |

Each front end is built once into its own `<app>/frontend/dist-uitests` (git-ignored) with `vite build --mode development`, which is what makes the app use dev auth instead of signing in with Entra. It is rebuilt only when something under that `frontend/` changed. The helpdesk API's own `wwwroot` is never touched. The project has its own `nuget.config` (nuget.org only), like `helpdesk/backend` and `agentdesk/backend`, so a private feed on the machine does not get in the way.

## How it is put together

```
ServiceDesk.UiTests/
  Infrastructure/
    UiFixture.cs        Starts Playwright and the browser once (an xUnit collection fixture), and the helpdesk app and the
                        AgentDesk host the first time a test asks for them. UiTestBase gives each test its own browser context
                        and page, and collects console errors, page errors and failed requests with their URLs.
    PageObject.cs       Base of the page objects: waits and script helpers. The only place that polls.
    SpaBuild.cs         Builds and caches a front end (helpdesk or agentdesk).
    BrowserLocator.cs   Finds Chrome or Edge, else installs Playwright's Chromium.
    Accessibility.cs    Runs axe-core on the current page and fails with every violation.
  HelpdeskUi/
    HelpdeskApp.cs      The real API on a real Kestrel port (WebApplicationFactory plus a second, Kestrel host, because a
                        browser cannot reach the in-memory test server) serving the built front end.
    ScriptedServices.cs Stand-ins: ScriptedRouter (keywords -> lane), ScriptedInference (streams a canned answer and records what it
                        was asked, including attached files), ScriptedInterviewer (asks once, drafts the ticket, then raises it only
                        when the draft is confirmed; a reply starting "No" declines), RecordingPublisher (the ticket queue), plus an
                        empty knowledge base and search.
    ChatPage.cs         ChatPage (the conversation, the message box, files, paste, ticket drafts), ChatMessage (what a message
                        looks like to a person) and AttachmentTray (the chips above the message box).
    UiTest.cs           Base of the helpdesk tests: clears the recordings and opens the chat page.
    Tests/              ChatTests, HandOffTests (policy guidance, the access-request button, a ticket drafted by conversation and
                        queued only after the user confirms it, declining a draft), AttachmentTests, AccessibilityTests.
  AgentDeskUi/
    StaticSpaHost.cs    Serves the built AgentDesk front end on a free loopback port, falling back to index.html for client routes.
    AgentDeskApiMock.cs A stateful fake of the AgentDesk API, answered in the browser: ticket, KB-cited reply, knowledge base.
    Tests/              AgentConsoleTests: the ticket to KB article walk, with axe checks on each screen.
```

### The Page Object Model here

- **Tests say what a person does and sees**, never selectors: `await Chat.AskAsync("My VPN is down.")`, `reply.Lane`, `Chat.Attachments.RemoveAsync("a.log")`.
- **Selectors live in one place per page object.** Where the markup gave nothing stable to hold on to, the front end has `data-testid` attributes (`user-message`, `assistant-message` with `data-lane`, `lane-label`, `message-body`, `error-message`, `live-reply`, `stopped-reply`) and ARIA labels (`Message`, `Attach files`, `Send a message`, `Attachments to send`, and the draft group `Send this to the service desk?`). Styling classes are never used as selectors, so a restyle cannot break a test. If you change those hooks, change the page object.
- **Waiting is the page object's job.** Methods return when the screen is ready (`AskAsync` waits for the finished reply, `WaitForUploadedAsync` for the upload). Tests contain no sleeps. The AgentDesk test uses Playwright's own locators and auto-waiting assertions (`Expect(...)`) directly, because it is a single walk through one console.
- A **component object** (`AttachmentTray`, `ChatMessage`) is used for a part of the screen with its own behaviour. Add one the same way for a new part.

### Adding a test

1. Add a method to the page object if the screen can do something it cannot yet (and a `data-testid` to the front end if nothing stable exists).
2. For helpdesk, extend `ScriptedRouter` / `ScriptedInference` / `ScriptedInterviewer` if you need a new scripted behaviour; they record what they were asked so a test can check what reached the model or the queue. For AgentDesk, add the endpoint to `AgentDeskApiMock`.
3. Derive from `UiTest` (helpdesk) or `UiTestBase` (anything else), take `UiFixture` in the constructor, and write the test. Every test gets a fresh browser context.
4. Call `Accessibility.ExpectNoViolationsAsync(Page)` on any new screen.

## Known limits

- **A scripted paste is not a real one.** A test cannot put text on the system clipboard, so `PasteAsync` dispatches a paste event and reports whether the app took it over. When the app leaves it alone, the browser would insert the text; that part is not exercised.
- **Drag and drop is not covered** (it needs real pointer events with files).
- The tests run the model, search and queue as stand-ins; they prove the screen and the wiring, not the quality of any answer. Prompt quality is the job of the evals (`python-evals`).
- The AgentDesk API is a mock, so these tests prove the console's behaviour against the API's shape, not the API itself (that is covered by AgentDesk's own `WebApplicationFactory` tests).
- The apps have no favicon, so every page load gets a 404 for `/favicon.ico`; the tests ignore that one request.
- axe-core finds the accessibility problems a script can find. It does not replace a manual check with a screen reader.
