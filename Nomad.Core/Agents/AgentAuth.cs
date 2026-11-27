using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nomad.Core.Antigravity;
using Nomad.Core.Codex;
using Nomad.Core.OpenCode;
using Nomad.Core.Process;
using Nomad.Core.Usage;

namespace Nomad.Core.Agents;

/// <summary>The sign-in status of one coding agent CLI.</summary>
public sealed record AgentAuthStatus(
    bool IsSignedIn,
    bool IsSubscription,
    string Summary,
    string? Detail = null,
    IReadOnlyList<OpenCodeConnectedProvider>? ConnectedProviders = null);

/// <summary>Live status reported while a browser subscription sign-in is waiting on the user.</summary>
public sealed record AgentLoginProgress(
    string Message,
    string? Url = null,
    string? DeviceCode = null,
    bool NeedsCodeInput = false);

/// <summary>
/// Checks authentication status and runs browser-based subscription sign-in / sign-out for all four coding agents:
/// Claude Code, Codex, Antigravity, and OpenCode.
/// </summary>
public static partial class AgentAuth
{
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(15);

    // ------------------------------------------------------------------ Claude Code

    /// <summary>Checks Claude Code's sign-in status via <c>claude auth status --json</c>.</summary>
    public static async Task<AgentAuthStatus> GetClaudeStatusAsync(string? cliPath, CancellationToken ct = default)
    {
        var exe = CliLocator.Resolve(string.IsNullOrWhiteSpace(cliPath) ? "claude" : cliPath.Trim());
        if (exe is null)
        {
            return new AgentAuthStatus(false, false, "Not installed");
        }

        try
        {
            var (code, stdout, _) = await RunCaptureAsync(exe, ["auth", "status", "--json"], StatusTimeout, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(stdout))
            {
                using var doc = JsonDocument.Parse(stdout);
                var root = doc.RootElement;
                var loggedIn = root.TryGetProperty("loggedIn", out var li) && li.ValueKind == JsonValueKind.True;
                if (!loggedIn)
                {
                    return new AgentAuthStatus(false, false, "Not signed in");
                }

                var method = Str(root, "authMethod");
                var email = Str(root, "email");
                var sub = Str(root, "subscriptionType");
                var tier = Str(root, "planTier");
                var isSubscription = string.Equals(method, "claude.ai", StringComparison.OrdinalIgnoreCase);
                var plan = PlanUsageClient.PlanName(sub, tier);
                var summary = isSubscription
                    ? (plan is { Length: > 0 } ? $"Claude {plan}" : "Claude subscription")
                    : (method is { Length: > 0 } ? method : "API key");
                return new AgentAuthStatus(true, isSubscription, summary, email);
            }

            if (code == 0 && File.Exists(PlanUsageClient.CredentialsPath))
            {
                return new AgentAuthStatus(true, true, "Claude subscription");
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or TimeoutException)
        {
            if (File.Exists(PlanUsageClient.CredentialsPath))
            {
                return new AgentAuthStatus(true, true, "Claude subscription");
            }
        }

        return new AgentAuthStatus(false, false, "Not signed in");
    }

    /// <summary>
    /// Signs into Claude Code with a Claude subscription (<c>claude auth login --claudeai</c>).
    /// The CLI opens the browser directly and listens for the OAuth callback; any printed URL is surfaced so the user can reopen it.
    /// </summary>
    public static async Task LoginClaudeAsync(string? cliPath, Action<AgentLoginProgress> onProgress, CancellationToken ct = default)
    {
        var exe = CliLocator.Resolve(string.IsNullOrWhiteSpace(cliPath) ? "claude" : cliPath.Trim())
            ?? throw new InvalidOperationException("Claude Code CLI was not found. Install it or set its path below.");

        onProgress(new AgentLoginProgress("Opening Claude sign-in in your browser…"));

        var psi = CreateStartInfo(exe, ["auth", "login", "--claudeai"]);
        psi.Environment["NO_COLOR"] = "1";

        using var process = new System.Diagnostics.Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start Claude Code to sign in.");
        }

        ChildProcessJob.TryAssign(process);
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        var tail = new List<string>();
        string? capturedUrl = null;
        void HandleLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            var clean = StripAnsi(line).Trim();
            if (clean.Length == 0)
            {
                return;
            }

            lock (tail)
            {
                tail.Add(clean);
                if (tail.Count > 12)
                {
                    tail.RemoveAt(0);
                }
            }

            if (capturedUrl is null && UrlRegex().Match(clean) is { Success: true } m)
            {
                capturedUrl = TrimUrl(m.Value);
                onProgress(new AgentLoginProgress("Finish signing in to Claude in your browser…", Url: capturedUrl));
            }
        }

        var outTask = DrainLinesAsync(process.StandardOutput, HandleLine);
        var errTask = DrainLinesAsync(process.StandardError, HandleLine);

        using var reg = ct.Register(() => TryKill(process));
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        await Task.WhenAll(outTask, errTask).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            string message;
            lock (tail)
            {
                message = tail.LastOrDefault(l => !l.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    ?? "Claude sign-in did not complete.";
            }

            throw new InvalidOperationException(message);
        }
    }

    /// <summary>Signs out of Claude Code (<c>claude auth logout</c>).</summary>
    public static async Task LogoutClaudeAsync(string? cliPath, CancellationToken ct = default)
    {
        var exe = CliLocator.Resolve(string.IsNullOrWhiteSpace(cliPath) ? "claude" : cliPath.Trim())
            ?? throw new InvalidOperationException("Claude Code CLI was not found.");

        var (code, stdout, stderr) = await RunCaptureAsync(exe, ["auth", "logout"], StatusTimeout, ct).ConfigureAwait(false);
        if (code != 0)
        {
            var err = FirstMeaningfulLine(stderr) ?? FirstMeaningfulLine(stdout) ?? "Could not sign out of Claude Code.";
            throw new InvalidOperationException(err);
        }
    }

    // ------------------------------------------------------------------ Codex

    /// <summary>Checks Codex's sign-in status via its app server (<c>account/read</c>).</summary>
    public static async Task<AgentAuthStatus> GetCodexStatusAsync(CancellationToken ct = default)
    {
        if (!CodexServer.IsInstalled)
        {
            return new AgentAuthStatus(false, false, "Not installed");
        }

        var server = await CodexServer.GetAsync(ct).ConfigureAwait(false);
        var (type, email, plan) = await CodexClient.AccountAsync(server, ct).ConfigureAwait(false);
        return type switch
        {
            "chatgpt" => new AgentAuthStatus(
                true,
                true,
                plan is { Length: > 0 } ? $"ChatGPT {FormatCodexPlan(plan)}" : "ChatGPT subscription",
                email),
            "apiKey" => new AgentAuthStatus(true, false, "API key", "Signed in with an OpenAI API key"),
            null or "" => new AgentAuthStatus(false, false, "Not signed in"),
            _ => new AgentAuthStatus(true, false, type, email),
        };
    }

    /// <summary>
    /// Signs into Codex with a ChatGPT subscription via <c>account/login/start</c>, opens the returned OAuth URL in the
    /// browser, and waits for <c>account/login/completed</c>.
    /// </summary>
    public static async Task LoginCodexAsync(Action<string> openUrl, Action<AgentLoginProgress> onProgress, CancellationToken ct = default)
    {
        onProgress(new AgentLoginProgress("Starting ChatGPT sign-in…"));

        var server = await CodexServer.GetAsync(ct).ConfigureAwait(false);
        var done = new TaskCompletionSource<(bool Success, string? Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? loginId = null;

        void OnNotification(string method, JsonElement parameters)
        {
            if (method != "account/login/completed")
            {
                return;
            }

            var id = CodexJson.Str(parameters, "loginId");
            if (loginId is not null && id is not null && id != loginId)
            {
                return;
            }

            var ok = CodexJson.Bool(parameters, "success") == true;
            var err = CodexJson.Str(parameters, "error");
            done.TrySetResult((ok, err));
        }

        server.Notification += OnNotification;
        try
        {
            var (id, authUrl) = await CodexClient.StartChatGptLoginAsync(server, ct).ConfigureAwait(false);
            loginId = id;

            openUrl(authUrl);
            onProgress(new AgentLoginProgress("Finish signing in to ChatGPT in your browser…", Url: authUrl));

            using var reg = ct.Register(() => done.TrySetCanceled(ct));
            var (success, error) = await done.Task.ConfigureAwait(false);
            if (!success)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Codex sign-in did not complete." : error);
            }
        }
        catch (OperationCanceledException)
        {
            if (loginId is not null)
            {
                try
                {
                    await CodexClient.CancelLoginAsync(server, loginId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            throw;
        }
        finally
        {
            server.Notification -= OnNotification;
        }
    }

    /// <summary>Signs out of Codex via <c>account/logout</c>.</summary>
    public static async Task LogoutCodexAsync(CancellationToken ct = default)
    {
        var server = await CodexServer.GetAsync(ct).ConfigureAwait(false);
        await CodexClient.LogoutAsync(server, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ Antigravity

    /// <summary>Checks Antigravity's sign-in status via <c>agy models</c>.</summary>
    public static async Task<AgentAuthStatus> GetAntigravityStatusAsync(CancellationToken ct = default)
    {
        var exe = AntigravityBackend.ResolveExecutable(AntigravityBackend.CliPath);
        if (exe is null)
        {
            return new AgentAuthStatus(false, false, "Not installed");
        }

        try
        {
            var (code, stdout, _) = await RunCaptureAsync(exe, ["models"], StatusTimeout, ct).ConfigureAwait(false);
            if (code == 0 && !string.IsNullOrWhiteSpace(stdout))
            {
                return new AgentAuthStatus(true, true, "Google account");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
        }

        return new AgentAuthStatus(false, false, "Not signed in");
    }

    /// <summary>
    /// Signs into Antigravity with a Google account using its ACP <c>authenticate</c> (<c>oauth-personal</c>) flow,
    /// opening the browser for OAuth and waiting until credentials are stored.
    /// </summary>
    public static async Task LoginAntigravityAsync(Action<AgentLoginProgress> onProgress, CancellationToken ct = default)
    {
        var exe = AntigravityBackend.ResolveExecutable(AntigravityBackend.CliPath)
            ?? throw new InvalidOperationException("Antigravity CLI was not found. Install it or set its path below.");

        onProgress(new AgentLoginProgress("Opening Google sign-in in your browser…"));

        string? capturedUrl = null;
        void OnStderr(string line)
        {
            var clean = StripAnsi(line);
            if (capturedUrl is null && UrlRegex().Match(clean) is { Success: true } m)
            {
                capturedUrl = TrimUrl(m.Value);
                onProgress(new AgentLoginProgress("Finish signing in with Google in your browser…", Url: capturedUrl));
            }
        }

        await using var connection = new AcpConnection(
            exe,
            Path.GetTempPath(),
            _ => Task.CompletedTask,
            (_, _) => { },
            onStderr: OnStderr);

        await connection.CallAsync("initialize", AntigravityBackend.InitializeParameters(), ct).ConfigureAwait(false);
        onProgress(new AgentLoginProgress("Finish signing in with Google in your browser…", Url: capturedUrl));
        await connection.CallAsync("authenticate", new JsonObject { ["methodId"] = "oauth-personal" }, ct).ConfigureAwait(false);
    }

    /// <summary>Signs out of Antigravity via ACP <c>logout</c>.</summary>
    public static async Task LogoutAntigravityAsync(CancellationToken ct = default)
    {
        var exe = AntigravityBackend.ResolveExecutable(AntigravityBackend.CliPath)
            ?? throw new InvalidOperationException("Antigravity CLI was not found.");

        await using var connection = new AcpConnection(exe, Path.GetTempPath(), _ => Task.CompletedTask, (_, _) => { });
        await connection.CallAsync("initialize", AntigravityBackend.InitializeParameters(), ct).ConfigureAwait(false);
        try
        {
            await connection.CallAsync("logout", new JsonObject(), ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            await connection.CallAsync("auth/logout", new JsonObject(), ct).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ OpenCode

    /// <summary>
    /// Fast status check for OpenCode: reads OpenCode's local <c>auth.json</c> immediately unless a v2 server is running
    /// or <paramref name="queryServer"/> is true (after connecting or disconnecting a provider).
    /// </summary>
    public static async Task<AgentAuthStatus> GetOpenCodeStatusAsync(bool queryServer = false, CancellationToken ct = default)
    {
        if (!OpenCodeServer.IsInstalled)
        {
            return new AgentAuthStatus(false, false, "Not installed");
        }

        var running = OpenCodeServer.Current;
        if (queryServer || running?.IsV2 == true)
        {
            var server = running ?? await OpenCodeServer.GetAsync(ct).ConfigureAwait(false);
            var (_, connected) = await OpenCodeClient.GetProviderAuthAsync(server, ct).ConfigureAwait(false);
            return BuildOpenCodeStatus(connected);
        }

        var stored = OpenCodeClient.ReadStoredAuthTypes();
        var list = stored
            .Select(kv => new OpenCodeConnectedProvider(kv.Key, FormatProviderName(kv.Key), string.Equals(kv.Value, "oauth", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return BuildOpenCodeStatus(list);
    }

    /// <summary>Loads OpenCode's available OAuth (subscription/browser) providers and connected providers.</summary>
    public static async Task<(IReadOnlyList<OpenCodeOAuthProvider> OAuthProviders, IReadOnlyList<OpenCodeConnectedProvider> Connected)> LoadOpenCodeProvidersAsync(
        CancellationToken ct = default)
    {
        var server = await OpenCodeServer.GetAsync(ct).ConfigureAwait(false);
        return await OpenCodeClient.GetProviderAuthAsync(server, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts an OpenCode provider's OAuth flow and opens the authorization webpage in the browser.
    /// Returns the authorization info so the caller can either wait for automatic callback or ask for a pasted code.
    /// </summary>
    public static async Task<OpenCodeOAuthAuthorization> StartOpenCodeOAuthAsync(
        OpenCodeOAuthProvider provider,
        IReadOnlyDictionary<string, string>? inputs,
        Action<string> openUrl,
        Action<AgentLoginProgress> onProgress,
        CancellationToken ct = default)
    {
        onProgress(new AgentLoginProgress($"Starting {provider.Name} sign-in…"));

        var server = await OpenCodeServer.GetAsync(ct).ConfigureAwait(false);
        var auth = await OpenCodeClient.StartOAuthAsync(server, provider.Id, provider.MethodIndex, inputs, ct).ConfigureAwait(false);

        openUrl(auth.Url);
        var code = ExtractDeviceCode(auth.Instructions);
        var message = string.IsNullOrWhiteSpace(auth.Instructions)
            ? $"Finish signing in to {provider.Name} in your browser…"
            : auth.Instructions.Trim();

        onProgress(new AgentLoginProgress(
            message,
            Url: auth.Url,
            DeviceCode: code,
            NeedsCodeInput: string.Equals(auth.Method, "code", StringComparison.OrdinalIgnoreCase)));

        return auth;
    }

    /// <summary>Completes an OpenCode OAuth flow (blocks until browser callback for "auto", or submits the pasted code).</summary>
    public static async Task CompleteOpenCodeOAuthAsync(
        OpenCodeOAuthProvider provider,
        string? code = null,
        CancellationToken ct = default)
    {
        var server = await OpenCodeServer.GetAsync(ct).ConfigureAwait(false);
        await OpenCodeClient.CompleteOAuthAsync(server, provider.Id, provider.MethodIndex, code, ct).ConfigureAwait(false);
    }

    /// <summary>Disconnects a provider from OpenCode.</summary>
    public static async Task RemoveOpenCodeProviderAsync(string providerId, CancellationToken ct = default)
    {
        var server = await OpenCodeServer.GetAsync(ct).ConfigureAwait(false);
        await OpenCodeClient.RemoveAuthAsync(server, providerId, ct).ConfigureAwait(false);
    }

    private static AgentAuthStatus BuildOpenCodeStatus(IReadOnlyList<OpenCodeConnectedProvider> connected)
    {
        if (connected.Count == 0)
        {
            return new AgentAuthStatus(false, false, "No account connected", null, connected);
        }

        var hasOAuth = connected.Any(p => p.IsOAuth);
        var summary = connected.Count == 1 ? "1 provider connected" : $"{connected.Count} providers connected";
        return new AgentAuthStatus(true, hasOAuth, summary, null, connected);
    }

    // ------------------------------------------------------------------ helpers

    private static string FormatCodexPlan(string plan) => plan.ToLowerInvariant() switch
    {
        "plus" => "Plus",
        "pro" => "Pro",
        "prolite" => "Pro Lite",
        "promax" => "Pro Max",
        "team" => "Team",
        "business" => "Business",
        "enterprise" => "Enterprise",
        "edu" => "Edu",
        "free" => "Free",
        "go" => "Go",
        _ => char.ToUpperInvariant(plan[0]) + plan[1..].Replace('_', ' '),
    };

    private static string FormatProviderName(string id) => id.ToLowerInvariant() switch
    {
        "openai" => "OpenAI",
        "anthropic" => "Anthropic",
        "github-copilot" => "GitHub Copilot",
        "xai" => "xAI",
        "gitlab" => "GitLab Duo",
        "poe" => "Poe",
        "digitalocean" => "DigitalOcean",
        "snowflake-cortex" => "Snowflake Cortex",
        "openrouter" => "OpenRouter",
        "google" => "Google",
        _ => id.Length > 0 ? char.ToUpperInvariant(id[0]) + id[1..] : id,
    };

    private static string? ExtractDeviceCode(string? instructions)
    {
        if (string.IsNullOrWhiteSpace(instructions))
        {
            return null;
        }

        var match = DeviceCodeRegex().Match(instructions);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static ProcessStartInfo CreateStartInfo(string exe, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetTempPath(),
        };

        if (CliLocator.IsBatchFile(exe))
        {
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            var joined = string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            psi.Arguments = $"/d /s /c \"\"{exe}\" {joined}\"";
        }
        else
        {
            psi.FileName = exe;
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }
        }

        return psi;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCaptureAsync(
        string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = CreateStartInfo(exe, args);
        psi.Environment["NO_COLOR"] = "1";

        using var process = new System.Diagnostics.Process { StartInfo = psi };
        await Task.Run(() =>
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Could not start {Path.GetFileName(exe)}.");
            }

            ChildProcessJob.TryAssign(process);
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
            }
        }, ct).ConfigureAwait(false);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"{Path.GetFileName(exe)} took too long to answer.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }

    private static async Task DrainLinesAsync(StreamReader reader, Action<string?> onLine)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                onLine(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private static void TryKill(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string? FirstMeaningfulLine(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? null
            : text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(StripAnsi)
                .FirstOrDefault(l => l.Length > 0);

    private static string StripAnsi(string value) => AnsiRegex().Replace(value, "");

    private static string TrimUrl(string url) => url.TrimEnd('.', ',', ';', ')', ']', '>', '"', '\'');

    [GeneratedRegex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiRegex();

    [GeneratedRegex(@"\b([A-Z0-9]{4,5}-[A-Z0-9]{4,5})\b")]
    private static partial Regex DeviceCodeRegex();
}
