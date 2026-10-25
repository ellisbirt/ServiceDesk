using Helpdesk.Api.Chat;
using Helpdesk.Core.Interfaces;

namespace Helpdesk.Api.Endpoints;

public static class AttachmentEndpointRouteBuilderExtensions
{
    public const string Path = "/api/v1/chat/attachments";
    public const string RateLimitPolicy = "attachments";

    /// <summary>
    /// Upload, delete and limits for chat attachments. Files are stored for the caller's conversation and referenced from
    /// messages by <see cref="Helpdesk.Shared.Contracts.AttachmentRef"/>. When no storage is configured these answer
    /// 404 <c>attachments_disabled</c>, except the limits, which report <c>enabled: false</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapAttachmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(Path).RequireAuthorization().RequireRateLimiting(RateLimitPolicy);

        group.MapGet("/config", (IAttachmentService attachments) =>
        {
            var l = attachments.Limits;
            return Results.Ok(new
            {
                enabled = l.Enabled,
                maxFileBytes = l.MaxFileBytes,
                maxFilesPerMessage = l.MaxFilesPerMessage,
                maxFilesPerConversation = l.MaxFilesPerConversation,
                maxTotalBytesPerConversation = l.MaxTotalBytesPerConversation,
                allowedExtensions = l.AllowedExtensions,
                pasteMinChars = l.PasteMinChars,
                pasteMinLines = l.PasteMinLines
            });
        });

        group.MapPost("/", async (
            HttpRequest request,
            ITenantProvider tenantProvider,
            IAttachmentService attachments,
            IConversationSigner signer,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (!attachments.Enabled) throw new AttachmentException("attachments_disabled", "File attachments are not available.", 404);
            if (!request.HasFormContentType)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Send the file as multipart/form-data."] });

            var form = await request.ReadFormAsync(ct);
            if (!Guid.TryParse(form["conversationId"].ToString(), out var conversationId) || conversationId == Guid.Empty)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["conversationId"] = ["A non-empty conversationId is required."] });
            if (form.Files.GetFile("file") is not { Length: > 0 } file)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["A non-empty file is required."] });

            var tenantId = tenantProvider.GetCurrentTenantId();
            var userId = tenantProvider.GetCurrentUserId();
            await using var stream = file.OpenReadStream();
            var info = await attachments.UploadAsync(tenantId, userId, conversationId, file.FileName, file.ContentType, stream, ct);

            // Names and content are never logged.
            loggerFactory.CreateLogger("Attachments").LogInformation(
                "Accepted a {Kind} attachment for Tenant: {TenantId}, User: {UserId}", info.Kind, tenantId, userId);
            var reference = AttachmentTokens.ToRef(signer, tenantId, userId, conversationId, info);
            return Results.Created($"{Path}/{info.Id}", reference);
        }).DisableAntiforgery();

        group.MapDelete("/{id:guid}", async (
            Guid id, Guid conversationId, ITenantProvider tenantProvider, IAttachmentService attachments, CancellationToken ct) =>
        {
            if (conversationId == Guid.Empty)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["conversationId"] = ["A non-empty conversationId is required."] });
            await attachments.DeleteAsync(tenantProvider.GetCurrentTenantId(), tenantProvider.GetCurrentUserId(), conversationId, id, ct);
            return Results.NoContent();
        });

        return endpoints;
    }
}
