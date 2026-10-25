using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Helpdesk.Core.Interfaces;

namespace Helpdesk.Infrastructure.Attachments;

/// <summary>
/// Deletes files from conversations that never raised a ticket once they are older than
/// <see cref="AttachmentOptions.RetentionHours"/>. Files on a raised ticket are marked and kept.
/// </summary>
public sealed class AttachmentCleanupService(
    IAttachmentStore store, IOptions<AttachmentOptions> options, TimeProvider clock, ILogger<AttachmentCleanupService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.CleanupIntervalMinutes));
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Attachment cleanup failed; it will try again later.");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>One pass; returns how many files were deleted.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - TimeSpan.FromHours(Math.Max(1, options.Value.RetentionHours));
        var deleted = 0;
        while (!ct.IsCancellationRequested)
        {
            var batch = await store.ListExpiredAsync(cutoff, 200, ct);
            if (batch.Count == 0) break;
            foreach (var item in batch)
            {
                await store.DeleteAsync(item, ct);
                deleted++;
            }
        }
        if (deleted > 0) logger.LogInformation("Deleted {Count} expired attachment(s) from conversations without a ticket.", deleted);
        return deleted;
    }
}

public static class AttachmentServiceCollectionExtensions
{
    /// <summary>
    /// Registers chat attachments. With no <c>Attachments:Provider</c> they are on (in memory) in Development and off
    /// elsewhere, where the service then refuses uploads and ignores references.
    /// </summary>
    public static IServiceCollection AddAttachments(
        this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration config, IHostEnvironment env)
    {
        services.Configure<AttachmentOptions>(config.GetSection(AttachmentOptions.SectionName));
        var options = config.GetSection(AttachmentOptions.SectionName).Get<AttachmentOptions>() ?? new();

        switch (options.EffectiveProvider(env.IsDevelopment()))
        {
            case null:
                services.TryAddSingleton<IAttachmentService, NullAttachmentService>();
                return services;
            case var p when string.Equals(p, AttachmentProviders.InMemory, StringComparison.OrdinalIgnoreCase):
                services.AddSingleton<IAttachmentStore, InMemoryAttachmentStore>();
                break;
            case var p when string.Equals(p, AttachmentProviders.Azure, StringComparison.OrdinalIgnoreCase):
                services.AddSingleton<IAttachmentStore, AzureAttachmentStore>();
                break;
            case var other:
                throw new InvalidOperationException(
                    $"Unknown Attachments:Provider '{other}'. Use '{AttachmentProviders.InMemory}' or '{AttachmentProviders.Azure}', or leave it empty.");
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAttachmentScanner, NoOpAttachmentScanner>();
        services.AddSingleton<IAttachmentService, AttachmentService>();
        services.AddSingleton<AttachmentCleanupService>();
        services.AddHostedService(sp => sp.GetRequiredService<AttachmentCleanupService>());
        return services;
    }
}
