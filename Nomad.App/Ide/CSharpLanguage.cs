using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Nomad.Core.Lsp;

namespace Nomad.App.Ide;

/// <summary>Where the C# language server of a folder is at, for the Files view's toolbar.</summary>
public enum CSharpState
{
    Starting,
    Downloading,
    Loading,
    Ready,
    Failed,
}

/// <summary>
/// The Files view's C# smarts for a folder: Roslyn's language server (<see cref="RoslynServer"/>), downloaded the first
/// time and started on the folder's solution, one for every session on the same folder (it goes a while after the last
/// lets it go). It is kept told of the C# files open in tabs, and asked where things are declared, implemented and what
/// they are; until it has loaded the solution it answers nothing, and the editor guesses on its own meanwhile.
/// </summary>
public sealed partial class CSharpLanguage : ObservableObject
{
    private static readonly Dictionary<string, CSharpLanguage> Shared = new(StringComparer.OrdinalIgnoreCase);
    private static Task? _installing;
    private static readonly TimeSpan Linger = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ChangeDelay = TimeSpan.FromMilliseconds(120);

    private readonly string _root;
    private int _users;
    private DispatcherTimer? _stopTimer;
    private RoslynServer? _server;
    private bool _stopped;

    // The open files: their text as last read (sent once the server runs), and edits not sent yet.
    private readonly Dictionary<string, (int Users, string Text, bool Sent, bool Pending)> _files = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _changeTimer;

    private CSharpLanguage(string root)
    {
        _root = root;
        _ = StartAsync();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorking), nameof(IsLoaded), nameof(IsFailed))]
    public partial CSharpState State { get; private set; } = CSharpState.Starting;

    /// <summary>Downloading or loading: the toolbar's dot is amber.</summary>
    public bool IsWorking => State is CSharpState.Starting or CSharpState.Downloading or CSharpState.Loading;

    public bool IsLoaded => State == CSharpState.Ready;

    public bool IsFailed => State == CSharpState.Failed;

    /// <summary>A line about it for the toolbar's tooltip: what it is doing, or why it failed.</summary>
    [ObservableProperty]
    public partial string Status { get; private set; } = "C#: starting the language server…";

    public bool IsReady => State == CSharpState.Ready && _server is { IsRunning: true };

    /// <summary>The folder's language server (started if this is the first to ask), for as long as it is not released.</summary>
    public static CSharpLanguage Acquire(string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Shared.TryGetValue(root, out var language) || language._stopped)
        {
            language = Shared[root] = new CSharpLanguage(root);
        }

        language._users++;
        language._stopTimer?.Stop();
        return language;
    }

    /// <summary>Let go of: the server stops a few minutes after its last user goes (in case one comes back).</summary>
    public void Release()
    {
        if (--_users > 0)
        {
            return;
        }

        if (_stopTimer is null)
        {
            _stopTimer = new DispatcherTimer { Interval = Linger };
            _stopTimer.Tick += (_, _) =>
            {
                _stopTimer.Stop();
                if (_users <= 0)
                {
                    Stop();
                }
            };
        }

        _stopTimer.Start();
    }

    private void Stop()
    {
        _stopped = true;
        Shared.Remove(_root);
        if (_server is { } server)
        {
            _server = null;
            _ = server.DisposeAsync().AsTask();
        }
    }

    private async Task StartAsync()
    {
        try
        {
            if (!RoslynServer.HasDotnet)
            {
                Fail("C# needs the .NET SDK, which isn't installed (dotnet.microsoft.com).");
                return;
            }

            if (!RoslynServer.IsInstalled)
            {
                State = CSharpState.Downloading;
                Status = "C#: downloading the language server…";
                var progress = new Progress<double>(p => Status = $"C#: downloading the language server ({p:P0})…");
                await (_installing ??= Task.Run(() => RoslynServer.InstallAsync(progress)));
            }

            State = CSharpState.Loading;
            Status = "C#: loading the solution…";
            var server = await Task.Run(() => RoslynServer.StartAsync(_root));
            if (_stopped)
            {
                await server.DisposeAsync();
                return;
            }

            _server = server;
            server.DiagnosticsStale += () => Dispatcher.UIThread.Post(ScheduleDiagnostics);
            server.DiagnosticsPublished += (path, found) => Dispatcher.UIThread.Post(() => SetDiagnostics(path, found));
            Status = $"C#: loading {server.Opened ?? "the solution"}…";
            server.Exited += () => Dispatcher.UIThread.Post(() =>
            {
                if (_server == server)
                {
                    _server = null;
                    Fail("C#: the language server stopped.");
                }
            });

            // The files already open: the server reads them from here from now on.
            foreach (var path in _files.Keys.ToList())
            {
                var file = _files[path];
                _files[path] = file with { Sent = true, Pending = false };
                await server.OpenAsync(path, file.Text);
            }

            await server.Loaded;
            if (_server == server)
            {
                State = CSharpState.Ready;
                Status = $"C#: {server.Opened ?? "ready"} loaded";
                ScheduleDiagnostics();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _installing = _installing is { IsFaulted: true } ? null : _installing;
            Fail("C#: " + ex.Message);
        }
    }

    private void Fail(string message)
    {
        State = CSharpState.Failed;
        Status = message;
    }

    // ------------------------------------------------------------------ open files

    public static bool Handles(string path) => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>A tab shows the file (with this text).</summary>
    public void Open(string path, string text)
    {
        if (_files.TryGetValue(path, out var file))
        {
            _files[path] = file with { Users = file.Users + 1 };
            return;
        }

        var sent = _server is { IsRunning: true } server;
        _files[path] = (1, text, sent, false);
        if (sent)
        {
            _ = Send(() => _server!.OpenAsync(path, text));
            ScheduleDiagnostics();
        }
    }

    /// <summary>The file's text changed in the editor (sent a moment later, or before the next question about it).</summary>
    public void Change(string path, string text)
    {
        if (!_files.TryGetValue(path, out var file))
        {
            return;
        }

        _files[path] = file with { Text = text, Pending = file.Sent };
        if (file.Sent)
        {
            _changeTimer ??= new DispatcherTimer { Interval = ChangeDelay };
            _changeTimer.Tick -= OnChangeTimer;
            _changeTimer.Tick += OnChangeTimer;
            _changeTimer.Stop();
            _changeTimer.Start();
            ScheduleDiagnostics();
        }
    }

    private void OnChangeTimer(object? sender, EventArgs e)
    {
        _changeTimer?.Stop();
        Flush();
    }

    /// <summary>The tab is gone (the last one showing it closes it on the server).</summary>
    public void Close(string path)
    {
        if (!_files.TryGetValue(path, out var file))
        {
            return;
        }

        if (file.Users > 1)
        {
            _files[path] = file with { Users = file.Users - 1 };
            return;
        }

        _files.Remove(path);
        _diagnostics.Remove(path);
        if (file.Sent)
        {
            _ = Send(() => _server!.CloseAsync(path));
        }
    }

    // Sends the edits waiting (all of them: a question about one file may depend on another's).
    private void Flush()
    {
        foreach (var path in _files.Keys.ToList())
        {
            var file = _files[path];
            if (file.Pending)
            {
                _files[path] = file with { Pending = false };
                var text = file.Text;
                _ = Send(() => _server!.ChangeAsync(path, text));
            }
        }
    }

    private async Task Send(Func<Task> send)
    {
        if (_server is not { IsRunning: true })
        {
            return;
        }

        try
        {
            await send();
        }
        catch (InvalidOperationException)
        {
            // It stopped meanwhile: Exited says so.
        }
    }

    // ------------------------------------------------------------------ questions

    private async Task<T?> AskAsync<T>(Func<RoslynServer, Task<T>> ask) where T : class
    {
        if (!IsReady || _server is not { } server)
        {
            return null;
        }

        _changeTimer?.Stop();
        Flush();
        try
        {
            return await ask(server);
        }
        catch (Exception ex) when (ex is LspException or TimeoutException or InvalidOperationException or OperationCanceledException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Where the symbol at a place is declared; null when the server can't say yet (not loaded).</summary>
    public Task<IReadOnlyList<LspLocation>?> DefinitionAsync(string path, int line, int column, CancellationToken cancellationToken = default) =>
        AskAsync<IReadOnlyList<LspLocation>>(s => s.DefinitionAsync(path, line, column, cancellationToken));

    public Task<IReadOnlyList<LspLocation>?> ImplementationAsync(string path, int line, int column, CancellationToken cancellationToken = default) =>
        AskAsync<IReadOnlyList<LspLocation>>(s => s.ImplementationAsync(path, line, column, cancellationToken));

    /// <summary>The hover markdown at a place; null for nothing, or no answer yet.</summary>
    public Task<string?> HoverAsync(string path, int line, int column, CancellationToken cancellationToken = default) =>
        AskAsync<string>(async s => (await s.HoverAsync(path, line, column, cancellationToken))!);

    public Task<IReadOnlyList<LspLocation>?> ReferencesAsync(string path, int line, int column, CancellationToken cancellationToken = default) =>
        AskAsync<IReadOnlyList<LspLocation>>(s => s.ReferencesAsync(path, line, column, cancellationToken));

    /// <summary>The name a rename at a place would change (null when nothing there can be renamed).</summary>
    public async Task<LspLocation?> PrepareRenameAsync(string path, int line, int column, CancellationToken cancellationToken = default) =>
        await AskAsync<LspLocation>(async s => (await s.PrepareRenameAsync(path, line, column, cancellationToken))?.Range!);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>>?> RenameAsync(string path, int line, int column, string newName, CancellationToken cancellationToken = default) =>
        AskAsync<IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>>>(s => s.RenameAsync(path, line, column, newName, cancellationToken));

    public Task<LspCompletionList?> CompletionAsync(string path, int line, int column, string? trigger, CancellationToken cancellationToken = default) =>
        AskAsync<LspCompletionList>(s => s.CompletionAsync(path, line, column, trigger, cancellationToken));

    public Task<LspSignatureHelp?> SignatureHelpAsync(string path, int line, int column, string? trigger, bool retrigger, CancellationToken cancellationToken = default) =>
        AskAsync<LspSignatureHelp>(async s => (await s.SignatureHelpAsync(path, line, column, trigger, retrigger, cancellationToken))!);

    public Task<IReadOnlyList<LspCodeAction>?> CodeActionsAsync(string path, int line, int column, int endLine, int endColumn, CancellationToken cancellationToken = default) =>
        AskAsync<IReadOnlyList<LspCodeAction>>(s => s.CodeActionsAsync(path, line, column, endLine, endColumn, cancellationToken));

    public Task<IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>>?> ResolveAsync(LspCodeAction action, CancellationToken cancellationToken = default) =>
        AskAsync<IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>>>(s => s.ResolveAsync(action, cancellationToken));

    public Task<LspResolvedCompletion?> ResolveAsync(LspCompletion completion, CancellationToken cancellationToken = default) =>
        AskAsync<LspResolvedCompletion>(s => s.ResolveAsync(completion, cancellationToken));

    // ------------------------------------------------------------------ diagnostics

    private static readonly TimeSpan DiagnosticsDelay = TimeSpan.FromMilliseconds(500);
    private readonly Dictionary<string, IReadOnlyList<LspDiagnostic>> _diagnostics = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _diagnosticsTimer;
    private bool _pulling;
    private bool _pullAgain;

    /// <summary>A file's errors and warnings changed (<see cref="DiagnosticsOf"/> has them).</summary>
    public event Action<string>? DiagnosticsChanged;

    public IReadOnlyList<LspDiagnostic> DiagnosticsOf(string path) => _diagnostics.GetValueOrDefault(path) ?? [];

    // The open files' diagnostics are asked for a moment after the typing stops (or the server says they're stale).
    private void ScheduleDiagnostics()
    {
        if (_stopped)
        {
            return;
        }

        if (_diagnosticsTimer is null)
        {
            _diagnosticsTimer = new DispatcherTimer { Interval = DiagnosticsDelay };
            _diagnosticsTimer.Tick += (_, _) =>
            {
                _diagnosticsTimer.Stop();
                _ = PullDiagnosticsAsync();
            };
        }

        _diagnosticsTimer.Stop();
        _diagnosticsTimer.Start();
    }

    private async Task PullDiagnosticsAsync()
    {
        if (!IsReady || _server is not { } server)
        {
            return;
        }

        if (_pulling)
        {
            _pullAgain = true;
            return;
        }

        _pulling = true;
        try
        {
            _changeTimer?.Stop();
            Flush();
            foreach (var path in _files.Where(f => f.Value.Sent).Select(f => f.Key).ToList())
            {
                IReadOnlyList<LspDiagnostic> found;
                try
                {
                    found = await server.DiagnosticsAsync(path);
                }
                catch (Exception ex) when (ex is LspException or TimeoutException or InvalidOperationException or OperationCanceledException or System.Text.Json.JsonException or KeyNotFoundException)
                {
                    continue;
                }

                // Edited meanwhile: the next pull has the newer ones.
                if (_files.TryGetValue(path, out var file) && !file.Pending)
                {
                    SetDiagnostics(path, found);
                }
            }
        }
        finally
        {
            _pulling = false;
        }

        if (_pullAgain)
        {
            _pullAgain = false;
            ScheduleDiagnostics();
        }
    }

    private void SetDiagnostics(string path, IReadOnlyList<LspDiagnostic> found)
    {
        _diagnostics[path] = found;
        DiagnosticsChanged?.Invoke(path);
    }
}
