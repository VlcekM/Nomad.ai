using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Nomad.Core.Codex;
using Nomad.Core.Usage;
using AgentProcess = System.Diagnostics.Process;

namespace Nomad.Core.Antigravity;

/// <summary>
/// Reads plan limits through Antigravity's headless quota command.
/// </summary>
public static class AntigravityUsage
{
    /// <summary>
    /// Fetches quotas through the CLI's existing sign-in without running a model turn.
    /// </summary>
    public static async Task<IReadOnlyList<PlanLimit>> FetchAsync()
    {
        var start = new ProcessStartInfo(AntigravityBackend.ExecutablePath)
        {
            WorkingDirectory = Path.GetTempPath(),
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "-p", "/usage", "--output-format", "json", "--print-timeout", "15s" })
            start.ArgumentList.Add(arg);

        using var process = Start(start);
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Antigravity took too long to report quotas.");
        }

        await errors.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException("Antigravity could not read quotas. Check your CLI sign-in.");

        return Parse(await output.ConfigureAwait(false));
    }

    /// <summary>
    /// Starts the CLI and reports installation errors in user-facing terms.
    /// </summary>
    private static AgentProcess Start(ProcessStartInfo start)
    {
        try
        {
            return AgentProcess.Start(start) ?? throw new InvalidOperationException("Could not start Antigravity.");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException("Install Antigravity CLI and sign in to see its quotas.", ex);
        }
    }

    /// <summary>
    /// Converts the main Gemini weekly and five-hour buckets to used percentages.
    /// </summary>
    internal static IReadOnlyList<PlanLimit> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var command = CodexJson.Obj(document.RootElement, "command");
        var groups = CodexJson.Arr(CodexJson.Obj(command, "data"), "groups");
        return [.. groups.SelectMany(group => CodexJson.Arr(group, "buckets"))
            .Select(ToLimit)
            .OfType<PlanLimit>()];
    }

    /// <summary>
    /// Maps the shared Gemini buckets to rows with the same semantics as Claude and Codex.
    /// </summary>
    private static PlanLimit? ToLimit(JsonElement bucket)
    {
        var id = CodexJson.Str(bucket, "id");
        if (id is not ("gemini-weekly" or "gemini-5h") || CodexJson.Bool(bucket, "disabled") == true
            || CodexJson.Double(bucket, "remaining_fraction") is not { } remaining) return null;

        var percent = (int)Math.Round(100 * (1 - Math.Clamp(remaining, 0, 1)));
        var weekly = id == "gemini-weekly";
        var resets = DateTimeOffset.TryParse(CodexJson.Str(bucket, "reset_time"), out var at) ? at.ToLocalTime() : (DateTimeOffset?)null;
        return new PlanLimit(weekly ? "weekly_all" : "session", weekly ? "Weekly limit" : "5-hour limit",
            percent, percent >= 80 ? "warning" : "normal", resets);
    }
}
