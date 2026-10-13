namespace Helpdesk.Api.Status;

public static class StartupValidation
{
    /// <summary>
    /// Runs <see cref="ConfigurationValidator"/> once at startup and logs every warning and failure.
    /// Outside Development a failed check stops the app, so a bad deployment never starts serving traffic.
    /// In Development failures are only logged, so local setups with partial configuration still run.
    /// </summary>
    public static void ValidateConfigurationAtStartup(this WebApplication app)
    {
        var report = app.Services.GetRequiredService<ConfigurationValidator>().Validate();

        foreach (var check in report.Checks.Where(c => c.Status != CheckStatus.Pass))
        {
            app.Logger.Log(
                check.Status == CheckStatus.Fail ? LogLevel.Error : LogLevel.Warning,
                "Configuration {Area}/{Name}: {Message}", check.Area, check.Name, check.Message);
        }
        app.Logger.LogInformation("Configuration validation finished: {Status}", report.Status);

        if (report.Status != "invalid" || app.Environment.IsDevelopment()) return;

        var failed = report.Checks.Where(c => c.Status == CheckStatus.Fail).Select(c => $"{c.Area}/{c.Name}: {c.Message}");
        throw new InvalidOperationException(
            $"Configuration is invalid; refusing to start. {string.Join(" | ", failed)}");
    }
}
