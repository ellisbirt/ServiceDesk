using System.Text;
using Helpdesk.Core.Interfaces;
using Helpdesk.Llm;

namespace Helpdesk.Infrastructure.Ai;

/// <summary>Puts attached files into a prompt in the way the model in use can take them.</summary>
public static class AttachmentPrompt
{
    public static string System(string system, AttachmentContext? attachments) =>
        attachments is { IsEmpty: false } ? system + "\n" + Prompts.AttachmentGuidance : system;

    /// <summary>
    /// The file text for the prompt, and the images and PDFs the model can take directly. A model that cannot see images
    /// is told which ones it was not given, so it says so instead of pretending.
    /// </summary>
    public static (string Text, IReadOnlyList<ChatContentPart>? Parts) For(IChatProvider client, AttachmentContext? attachments)
    {
        if (attachments is not { IsEmpty: false }) return ("", null);

        var supported = new List<ChatContentPart>();
        var notes = new StringBuilder();
        foreach (var part in attachments.Parts)
        {
            var canRead = part.Kind == ChatContentKinds.Document
                ? client.Capabilities.HasFlag(LlmCapabilities.Documents)
                : client.Capabilities.HasFlag(LlmCapabilities.Vision);
            if (canRead) supported.Add(part);
            else notes.Append($"You cannot view '{Safe(part.Name)}' ({part.Kind}). Tell the user you cannot see it and ask them to describe what it shows.\n");
        }

        // The note holds a file name, which the user chose, so it travels as untrusted data like the files do.
        var text = attachments.Text + (notes.Length > 0 ? PromptInput.Wrap("attachment_note", notes.ToString().TrimEnd()) + "\n\n" : "");
        return (text, supported.Count > 0 ? supported : null);
    }

    private static string Safe(string? name) =>
        new string((name ?? "file").Where(c => !char.IsControl(c)).Take(80).ToArray());
}
