using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nomad.Core.Process;

namespace Nomad.Core.OpenCode;

/// <summary>
/// The one <c>opencode serve</c> this app runs, shared by every OpenCode session and by the session list. Started on
/// first use on a free port of 127.0.0.1 with a password of its own (HTTP basic, user <c>opencode</c>), so nothing
/// else on the machine can drive it; ends with the app.
/// </summary>
public sealed partial class OpenCodeServer : IAsyncDisposable
{
    private const string User = "opencode";

    private static readonly SemaphoreSlim StartLock = new(1, 1);
    private static OpenCodeServer? _shared;

    private readonly System.Diagnostics.Process _process;
    private readonly Queue<string> _outputTail = new();

    private OpenCodeServer(System.Diagnostics.Process process, Uri baseAddress, string password)
    {
        _process = process;
        BaseAddress = baseAddress;

        // No overall timeout: event streams stay open for as long as a session does. Calls pass their own tokens.
        Http = new HttpClient { BaseAddress = baseAddress, Timeout = Timeout.InfiniteTimeSpan };
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{password}")));
    }

    public Uri BaseAddress { get; }

    /// <summary>Authenticated client for the server's API.</summary>
    public HttpClient Http { get; }

    public bool IsRunning
    {
        get
        {
            try
            {
                return !_process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>The OpenCode version the server reported, e.g. "1.19.5".</summary>
    public string? Version { get; private set; }

    /// <summary>The configured path or command name (Settings); "opencode" when empty.</summary>
    public static string? CliPath { get; set; }

    /// <summary>Whether OpenCode looks installed (its executable resolves); cheap, no process started.</summary>
    public static bool IsInstalled => ResolveExecutable(CliPath) is not null;

    /// <summary>OpenCode has been used on this machine (its database exists): only then is its server started to list sessions.</summary>
    public static bool HasData
    {
        get
        {
            var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return File.Exists(Path.Combine(data, "opencode", "opencode.db"));
        }
    }

    /// <summary>The running server, started if needed. Throws when OpenCode is not installed or does not come up.</summary>
    public static async Task<OpenCodeServer> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_shared is { IsRunning: true } running)
        {
            return running;
        }

        await StartLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_shared is { IsRunning: true } again)
            {
                return again;
            }

            _shared?.Kill();
            _shared = await StartAsync(cancellationToken).ConfigureAwait(false);
            return _shared;
        }
        finally
        {
            StartLock.Release();
        }
    }

    /// <summary>The server if one runs now, without starting it.</summary>
    public static OpenCodeServer? Current => _shared is { IsRunning: true } s ? s : null;

    /// <summary>Kills the shared server (app shutdown).</summary>
    public static void KillShared() => _shared?.Kill();

    private static async Task<OpenCodeServer> StartAsync(CancellationToken cancellationToken)
    {
        var exe = ResolveExecutable(CliPath)
            ?? throw new FileNotFoundException($"Could not find OpenCode ('{(string.IsNullOrWhiteSpace(CliPath) ? "opencode" : CliPath)}'). Install it (npm i -g opencode-ai), or set its path in Settings.");

        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        string[] arguments = ["serve", "--port", "0", "--hostname", "127.0.0.1"];
        if (CliLocator.IsBatchFile(exe))
        {
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.Arguments = $"/d /s /c \"\"{exe}\" {string.Join(' ', arguments)}\"";
        }
        else
        {
            psi.FileName = exe;
            foreach (var a in arguments)
            {
                psi.ArgumentList.Add(a);
            }
        }

        psi.Environment["OPENCODE_SERVER_PASSWORD"] = password;

        var process = new System.Diagnostics.Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException("OpenCode did not start.");
        }

        ChildProcessJob.TryAssign(process);

        // It prints "opencode server listening on http://127.0.0.1:<port>" once it serves.
        var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tail = new Queue<string>();
        void OnLine(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (tail)
            {
                tail.Enqueue(line);
                while (tail.Count > 30)
                {
                    tail.Dequeue();
                }
            }

            if (ListeningLine().Match(line) is { Success: true } m && Uri.TryCreate(m.Groups[1].Value, UriKind.Absolute, out var uri))
            {
                listening.TrySetResult(uri);
            }
        }

        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => listening.TrySetException(new InvalidOperationException("OpenCode exited before it was ready:\n" + string.Join("\n", tail)));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        Uri address;
        try
        {
            address = await listening.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            TryKill(process);
            throw;
        }

        var server = new OpenCodeServer(process, address, password);
        lock (tail)
        {
            foreach (var line in tail)
            {
                server._outputTail.Enqueue(line);
            }
        }

        try
        {
            using var health = await server.Http.GetAsync("global/health", cancellationToken).ConfigureAwait(false);
            if (health.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await health.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                server.Version = OpenCodeJson.Str(doc.RootElement, "version");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            // Only the version is missing.
        }

        return server;
    }

    /// <summary>
    /// The OpenCode executable for a configured path or command name, or null. An npm shim (<c>opencode.cmd</c>) is
    /// followed to the binary it starts, so no cmd.exe sits between Nomad and the server.
    /// </summary>
    public static string? ResolveExecutable(string? configured)
    {
        var name = string.IsNullOrWhiteSpace(configured) ? "opencode" : configured;
        var exe = CliLocator.Resolve(name);
        if (exe is not null && CliLocator.IsBatchFile(exe))
        {
            var binary = Path.Combine(Path.GetDirectoryName(exe)!, "node_modules", "opencode-ai", "bin", "opencode.exe");
            if (File.Exists(binary))
            {
                return binary;
            }
        }

        return exe;
    }

    public void Kill() => TryKill(_process);

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

    public ValueTask DisposeAsync()
    {
        Kill();
        Http.Dispose();
        _process.Dispose();
        return ValueTask.CompletedTask;
    }

    [GeneratedRegex(@"listening on (https?://\S+)")]
    private static partial Regex ListeningLine();
}
