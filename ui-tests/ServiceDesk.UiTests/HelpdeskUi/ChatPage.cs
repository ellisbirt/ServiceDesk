using System.Text.Json;
using Microsoft.Playwright;
using ServiceDesk.UiTests.Infrastructure;

namespace ServiceDesk.UiTests.HelpdeskUi;

/// <summary>One thing in the conversation, as a person sees it.</summary>
/// <param name="Kind">user, assistant, error, live (still arriving) or stopped.</param>
/// <param name="Text">What is written, without the files listed under a user message.</param>
/// <param name="Lane">For an assistant message, the lane it was answered in, for example PUBLIC_TECH.</param>
/// <param name="LaneLabel">The label on the bubble, for example "General tech".</param>
/// <param name="Files">Names of the files shown on a user message.</param>
/// <param name="Notice">A status line on the bubble, for example "Sent to the service desk".</param>
/// <param name="Buttons">The buttons on an assistant message.</param>
public sealed record ChatMessage(
    string Kind, string Text, string? Lane, string? LaneLabel, IReadOnlyList<string> Files, string? Notice, IReadOnlyList<string> Buttons)
{
    public bool IsUser => Kind == "user";
    public bool IsAssistant => Kind == "assistant";
    public bool IsError => Kind == "error";
}

/// <summary>The chat screen: the conversation, the message box, the attach button and the files waiting to be sent.</summary>
public sealed class ChatPage : PageObject
{
    private const string Form = "form[aria-label='Send a message']";
    private const string Input = "textarea[aria-label='Message']";
    private const string SendButton = Form + " button[type='submit']";
    private const string AttachButton = "button[aria-label='Attach files']";
    private const string FileInput = "input[type='file'][aria-label='Choose files to attach']";
    private const string ComposerNotice = Form + " [role='status']";
    private const string ComposerRejections = Form + " [role='alert']";

    private ChatPage(IPage page) : base(page)
    {
        Attachments = new AttachmentTray(page);
    }

    /// <summary>The files waiting to be sent with the next message.</summary>
    public AttachmentTray Attachments { get; }

    /// <summary>Opens the app and waits until it is ready for a message.</summary>
    public static async Task<ChatPage> OpenAsync(IPage page, Uri address)
    {
        await page.GotoAsync(address.ToString());
        var chat = new ChatPage(page);
        await chat.WaitForVisibleAsync(Input);
        return chat;
    }

    // ----- What is on the screen -----

    public Task<string> HeadingAsync() => TextOfAsync("h1");

    public Task<string> EmptyStateAsync() => TextOfAsync("main > p");

    public Task<string> ServiceStatusAsync() => TextOfAsync("header [role='status']");

    public Task<bool> CanSendAsync() => EvaluateAsync<bool>("sel => !document.querySelector(sel).disabled", SendButton);

    public Task<bool> HasAttachButtonAsync() => ExistsAsync(AttachButton);

    public Task<string> MessageBoxAsync() => EvaluateAsync<string>("sel => document.querySelector(sel).value", Input);

    public async Task<IReadOnlyList<ChatMessage>> MessagesAsync()
    {
        var json = await EvaluateAsync<string>(
            """
            () => JSON.stringify([...document.querySelectorAll(
              'main [data-testid="user-message"], main [data-testid="assistant-message"], main [data-testid="error-message"], main [data-testid="live-reply"], main [data-testid="stopped-reply"]'
            )].map(el => {
              const kind = el.dataset.testid;
              if (kind === 'user-message') {
                const bubble = el.firstElementChild.cloneNode(true);
                const files = [...bubble.querySelectorAll('ul[aria-label="Attachments"] li')]
                  .map(li => li.querySelector('span[title]')?.getAttribute('title') ?? li.textContent.trim());
                bubble.querySelectorAll('ul').forEach(u => u.remove());
                return { kind: 'user', text: bubble.textContent.trim(), files };
              }
              if (kind === 'assistant-message') {
                return {
                  kind: 'assistant',
                  text: el.querySelector('[data-testid="message-body"]').innerText.trim(),
                  lane: el.dataset.lane,
                  laneLabel: el.querySelector('[data-testid="lane-label"]').textContent.trim(),
                  notice: el.querySelector('[role="status"]')?.textContent.trim() ?? null,
                  buttons: [...el.querySelectorAll('button')].map(b => b.textContent.trim())
                };
              }
              return { kind: kind === 'error-message' ? 'error' : kind === 'live-reply' ? 'live' : 'stopped', text: el.innerText.trim() };
            }))
            """);

        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateArray().Select(e => new ChatMessage(
            e.GetProperty("kind").GetString()!,
            e.GetProperty("text").GetString()!,
            e.TryGetProperty("lane", out var lane) ? lane.GetString() : null,
            e.TryGetProperty("laneLabel", out var label) ? label.GetString() : null,
            e.TryGetProperty("files", out var files) ? [.. files.EnumerateArray().Select(f => f.GetString()!)] : [],
            e.TryGetProperty("notice", out var notice) && notice.ValueKind == JsonValueKind.String ? notice.GetString() : null,
            e.TryGetProperty("buttons", out var buttons) ? [.. buttons.EnumerateArray().Select(b => b.GetString()!)] : []))];
    }

    public async Task<ChatMessage> LastMessageAsync() => (await MessagesAsync()).Last();

    /// <summary>Everything the assistant (or an error) has said so far, not counting a reply that is still arriving.</summary>
    public async Task<IReadOnlyList<ChatMessage>> RepliesAsync() =>
        [.. (await MessagesAsync()).Where(m => m.IsAssistant || m.IsError)];

    // ----- Writing and sending -----

    /// <summary>Puts text in the message box in one step, as if pasted by keyboard shortcut from outside the page.</summary>
    public async Task FillAsync(string text)
    {
        await EvaluateAsync<bool>(
            """
            (sel, text) => {
              const box = document.querySelector(sel);
              Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(box, text);
              box.dispatchEvent(new Event('input', { bubbles: true }));
              box.focus();
              return true;
            }
            """, Input, text);
    }

    /// <summary>Types text a key at a time, the way a person does.</summary>
    public async Task TypeAsync(string text)
    {
        await Page.FocusAsync(Input);
        await Page.Keyboard.TypeAsync(text);
    }

    /// <summary>Writes the message, presses Send, and waits for the assistant's answer (or an error) to arrive.</summary>
    public async Task<ChatMessage> AskAsync(string text)
    {
        var before = (await RepliesAsync()).Count;
        await FillAsync(text);
        await ClickSendAsync();
        return await WaitForReplyAsync(before + 1);
    }

    /// <summary>Presses Send without waiting for the answer.</summary>
    public async Task ClickSendAsync()
    {
        await WaitUntilAsync(CanSendAsync, "the Send button to be enabled");
        await Page.ClickAsync(SendButton);
    }

    public Task PressEnterAsync() => Page.Keyboard.PressAsync("Enter");

    public async Task PressShiftEnterAsync()
    {
        await Page.Keyboard.DownAsync("Shift");
        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.UpAsync("Shift");
    }

    /// <summary>Waits until there are at least <paramref name="expectedReplies"/> finished replies and nothing is still arriving.</summary>
    public async Task<ChatMessage> WaitForReplyAsync(int expectedReplies)
    {
        await WaitUntilAsync(async () =>
        {
            var messages = await MessagesAsync();
            return messages.Count(m => m.IsAssistant || m.IsError) >= expectedReplies && messages.All(m => m.Kind != "live");
        }, $"{expectedReplies} finished repl{(expectedReplies == 1 ? "y" : "ies")}");
        return (await RepliesAsync())[expectedReplies - 1];
    }

    /// <summary>Waits for the reply to start arriving, returning what has appeared so far.</summary>
    public async Task<ChatMessage> WaitForLiveReplyAsync()
    {
        ChatMessage? live = null;
        await WaitUntilAsync(async () =>
        {
            live = (await MessagesAsync()).FirstOrDefault(m => m.Kind == "live" && m.Text.Length > 0);
            return live is not null;
        }, "the reply to start arriving");
        return live!;
    }

    public Task NewConversationAsync() => ClickButtonAsync("header", "New conversation");

    /// <summary>Clicks a button on the last assistant message, for example "Start an access request".</summary>
    public async Task ClickOnLastReplyAsync(string buttonText)
    {
        var clicked = await EvaluateAsync<bool>(
            """
            text => {
              const replies = [...document.querySelectorAll('main [data-testid="assistant-message"]')];
              const button = [...(replies.at(-1)?.querySelectorAll('button') ?? [])].find(b => b.textContent.trim() === text);
              if (!button || button.disabled) return false;
              button.click();
              return true;
            }
            """, buttonText);
        if (!clicked) throw new InvalidOperationException($"The last reply has no enabled '{buttonText}' button.");
    }

    /// <summary>Presses a button on the ticket draft (the last reply) and waits for the assistant's answer to it.</summary>
    public async Task<ChatMessage> AnswerDraftAsync(string buttonText)
    {
        var before = (await RepliesAsync()).Count;
        await ClickOnLastReplyAsync(buttonText);
        return await WaitForReplyAsync(before + 1);
    }

    /// <summary>Agrees to the ticket draft the assistant has just shown.</summary>
    public Task<ChatMessage> SendDraftAsync() => AnswerDraftAsync("Send to the service desk");

    /// <summary>Turns the ticket draft down.</summary>
    public Task<ChatMessage> DeclineDraftAsync() => AnswerDraftAsync("Don't send");

    // ----- Files -----

    /// <summary>Chooses files with the attach button's file picker.</summary>
    public async Task AttachAsync(params string[] paths)
    {
        if (await CountAsync(FileInput) == 0)
            throw new InvalidOperationException("There is no file picker: attachments are switched off.");
        await Page.SetInputFilesAsync(FileInput, paths);
    }

    /// <summary>
    /// Pastes text into the message box, the way Ctrl+V does, and says whether the app <b>took over</b> the paste (it
    /// turned it into an attachment). When it did not, the browser would insert the text itself; a scripted paste event
    /// cannot do that part, so the text only appears in the box if the app inserts it.
    /// </summary>
    public async Task<bool> PasteAsync(string text) =>
        await EvaluateAsync<bool>(
            """
            (sel, text) => {
              const box = document.querySelector(sel);
              box.focus();
              const data = new DataTransfer();
              data.setData('text/plain', text);
              const paste = new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true });
              box.dispatchEvent(paste);
              return paste.defaultPrevented;
            }
            """, Input, text);

    /// <summary>The status line above the message box, such as "Pasted text was added as an attachment." (or null).</summary>
    public async Task<string?> ComposerNoticeAsync() =>
        await ExistsAsync(ComposerNotice) ? await EvaluateAsync<string>(
            "sel => document.querySelector(sel).querySelector('span').textContent.trim()", ComposerNotice) : null;

    /// <summary>Why files were turned away, one line per file (empty when none were).</summary>
    public async Task<IReadOnlyList<string>> RejectionsAsync() =>
        await ExistsAsync(ComposerRejections)
            ? await EvaluateAsync<string[]>(
                "sel => [...document.querySelector(sel).querySelectorAll('li')].map(li => li.textContent.trim())", ComposerRejections)
            : [];

    public Task PasteAsTextInsteadAsync() => ClickButtonAsync(Form, "Paste as text instead");

    public async Task WaitForNoticeAsync(string text) =>
        await WaitUntilAsync(async () => (await ComposerNoticeAsync())?.Contains(text) == true, $"the notice '{text}'");
}

/// <summary>The row of files waiting to be sent, above the message box.</summary>
public sealed class AttachmentTray(IPage page) : PageObject(page)
{
    private const string Tray = "ul[aria-label='Attachments to send']";

    /// <summary>The names of the files in the tray.</summary>
    public async Task<IReadOnlyList<string>> NamesAsync() =>
        await ExistsAsync(Tray)
            ? await EvaluateAsync<string[]>(
                "sel => [...document.querySelector(sel).querySelectorAll('li span[title]')].map(s => s.getAttribute('title'))", Tray)
            : [];

    public async Task<bool> IsEmptyAsync() => (await NamesAsync()).Count == 0;

    /// <summary>Waits for a file to be uploaded (no longer in progress) and returns its type label, for example "Text" or "Image".</summary>
    public async Task<string> WaitForUploadedAsync(string name)
    {
        await WaitUntilAsync(async () => await StateAsync(name) == "ready", $"'{name}' to finish uploading");
        return await EvaluateAsync<string>(
            """
            (sel, name) => {
              const li = [...document.querySelectorAll(sel + ' li')].find(l => l.querySelector('span[title]')?.getAttribute('title') === name);
              return li.querySelector('span').textContent.trim();
            }
            """, Tray, name);
    }

    /// <summary>Waits for a file whose name matches <paramref name="pattern"/> to be uploaded, and returns its name.</summary>
    public async Task<string> WaitForUploadedMatchingAsync(System.Text.RegularExpressions.Regex pattern)
    {
        string? found = null;
        await WaitUntilAsync(async () =>
        {
            found = (await NamesAsync()).FirstOrDefault(n => pattern.IsMatch(n));
            return found is not null && await StateAsync(found) == "ready";
        }, $"a file matching {pattern} to finish uploading");
        return found!;
    }

    public async Task RemoveAsync(string name)
    {
        await Page.ClickAsync($"{Tray} button[aria-label='Remove {name}']");
        await WaitUntilAsync(async () => !(await NamesAsync()).Contains(name), $"'{name}' to leave the tray");
    }

    public async Task WaitUntilEmptyAsync() => await WaitUntilAsync(IsEmptyAsync, "the attachment tray to empty");

    /// <summary>uploading, ready or error.</summary>
    private Task<string> StateAsync(string name) =>
        EvaluateAsync<string>(
            """
            (sel, name) => {
              const li = [...document.querySelectorAll(sel + ' li')].find(l => l.querySelector('span[title]')?.getAttribute('title') === name);
              if (!li) return 'missing';
              if (li.querySelector('progress')) return 'uploading';
              if (li.className.includes('red')) return 'error';
              return 'ready';
            }
            """, Tray, name);
}
