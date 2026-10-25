using System.Diagnostics;

namespace Helpdesk.UiTests.Infrastructure;

/// <summary>
/// Builds the React front end for the tests, into a folder of its own so the API's <c>wwwroot</c> is never touched. It is a
/// <b>development-mode</b> build, which is what makes the app use dev auth (the headers the test API accepts) instead of
/// signing in with Entra. The build is skipped when nothing under the front end changed since the last one.
/// Set <c>HELPDESK_UI_SPA_DIR</c> to use a build you made yourself.
/// </summary>
public static class SpaBuild
{
    public static string Ensure([System.Runtime.CompilerServices.CallerFilePath] string? thisFile = null)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("HELPDESK_UI_SPA_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            if (!File.Exists(Path.Combine(fromEnvironment, "index.html")))
                throw new InvalidOperationException($"HELPDESK_UI_SPA_DIR ({fromEnvironment}) has no index.html.");
            return fromEnvironment;
        }

        var frontend = FindFrontend(thisFile);
        var output = Path.Combine(frontend, "dist-uitests");
        var stamp = Path.Combine(output, ".built");
        if (File.Exists(stamp) && File.Exists(Path.Combine(output, "index.html")) && File.GetLastWriteTimeUtc(stamp) >= NewestSource(frontend))
            return output;

        Build(frontend, output);
        File.WriteAllText(stamp, DateTime.UtcNow.ToString("O"));
        return output;
    }

    /// <summary>
    /// Finds <c>helpdesk/frontend</c>: from <c>HELPDESK_UI_FRONTEND</c>, else by walking up from this source file (which works
    /// wherever the build output goes), else by walking up from the test binaries.
    /// </summary>
    private static string FindFrontend(string? thisFile)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("HELPDESK_UI_FRONTEND");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(Path.Combine(fromEnvironment, "package.json"))) return fromEnvironment;

        foreach (var start in new[] { thisFile is null ? null : Path.GetDirectoryName(thisFile), AppContext.BaseDirectory }.OfType<string>())
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "frontend");
                if (File.Exists(Path.Combine(candidate, "package.json")) && Directory.Exists(Path.Combine(dir.FullName, "backend"))) return candidate;
            }
        }
        throw new InvalidOperationException("Could not find helpdesk/frontend. Set HELPDESK_UI_FRONTEND to its path.");
    }

    private static DateTime NewestSource(string frontend)
    {
        var newest = DateTime.MinValue;
        foreach (var file in new[] { "package.json", "package-lock.json", "index.html", "vite.config.ts", "tsconfig.json" }
            .Select(f => Path.Combine(frontend, f)).Where(File.Exists))
        {
            newest = Max(newest, File.GetLastWriteTimeUtc(file));
        }
        foreach (var file in Directory.EnumerateFiles(Path.Combine(frontend, "src"), "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".test.ts", StringComparison.Ordinal) && !f.EndsWith(".test.tsx", StringComparison.Ordinal)))
        {
            newest = Max(newest, File.GetLastWriteTimeUtc(file));
        }
        return newest;

        static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    }

    private static void Build(string frontend, string output)
    {
        if (!Directory.Exists(Path.Combine(frontend, "node_modules")))
            Run(frontend, "npm", "ci");

        // `--mode development` is what turns on dev auth; a production build refuses to start without Entra settings.
        Run(frontend, "npx", $"vite build --mode development --outDir \"{output}\" --emptyOutDir");
    }

    private static void Run(string workingDirectory, string command, string arguments)
    {
        // npm and npx are scripts on Windows, so they go through the shell.
        var windows = OperatingSystem.IsWindows();
        var info = new ProcessStartInfo(windows ? "cmd.exe" : command, windows ? $"/c {command} {arguments}" : arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        Process process;
        try
        {
            process = Process.Start(info)!;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not run '{command}'. The UI tests need Node.js on the PATH to build the front end.", ex);
        }

        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromMinutes(5))) { process.Kill(true); throw new TimeoutException($"'{command} {arguments}' took too long."); }
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"'{command} {arguments}' failed ({process.ExitCode}):\n{output.Result}\n{error.Result}");
        }
    }
}
