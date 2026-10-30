using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nomad.Core.Files;

namespace Nomad.Core.Lsp;

/// <summary>A place in a file a language server pointed to: the file, and the range's start and end (0-based; columns in UTF-16 units).</summary>
public sealed record LspLocation(string Path, int Line, int Column, int EndLine, int EndColumn);

/// <summary>
/// Roslyn's C# language server (the one Visual Studio Code's C# extension runs) for a folder: downloaded once from
/// Microsoft's package feed into Nomad's local data, started on the folder's solution (or its projects), kept told of
/// the files open in the editor, and asked where things are declared, implemented and what they are. It needs the
/// .NET SDK, as building the projects does.
/// </summary>
public sealed class RoslynServer : IAsyncDisposable
{
    /// <summary>The server's version: pinned, so its custom messages are the ones this was written against.</summary>
    public const string Version = "5.4.0-2.26179.14";

    private const string Feed = "https://pkgs.dev.azure.com/azure-public/vside/_packaging/vs-impl/nuget/v3/index.json";

    private readonly LspConnection _connection;
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, int> _versions = new(StringComparer.OrdinalIgnoreCase);

    private RoslynServer(string root, LspConnection connection)
    {
        Root = root;
        _connection = connection;
    }

    public string Root { get; }

    /// <summary>The solution or projects opened, for the status ("Nomad.slnx", "3 projects").</summary>
    public string? Opened { get; private set; }

    /// <summary>The solution's projects are loaded: answers are complete from now on.</summary>
    public bool IsLoaded => _loaded.Task.IsCompleted;

    public Task Loaded => _loaded.Task;

    public bool IsRunning => _connection.IsRunning;

    /// <summary>The process ended.</summary>
    public event Action? Exited;

    /// <summary>Every message in and out (a log for debugging).</summary>
    public static Action<string, bool>? RawMessage { get; set; }

    // ------------------------------------------------------------------ install

    private static string Rid => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";

    /// <summary>Where this version of the server is kept.</summary>
    public static string InstallFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nomad", "roslyn", Version);

    private static string Exe => Path.Combine(InstallFolder, "Microsoft.CodeAnalysis.LanguageServer.exe");

    public static bool IsInstalled => File.Exists(Exe);

    /// <summary>Whether the .NET SDK (which loading projects needs) looks installed.</summary>
    public static bool HasDotnet => DotnetRoot() is not null;

    private static string? DotnetRoot()
    {
        foreach (var candidate in new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
        })
        {
            if (!string.IsNullOrEmpty(candidate) && Directory.Exists(Path.Combine(candidate, "sdk")))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Downloads and unpacks the server (about 60 MB), reporting the fraction done.</summary>
    public static async Task InstallAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var index = JsonDocument.Parse(await http.GetStringAsync(Feed, cancellationToken).ConfigureAwait(false));
        var baseAddress = index.RootElement.GetProperty("resources").EnumerateArray()
            .First(r => r.GetProperty("@type").GetString()?.StartsWith("PackageBaseAddress", StringComparison.Ordinal) == true)
            .GetProperty("@id").GetString()!;
        var id = $"microsoft.codeanalysis.languageserver.{Rid}";
        var url = $"{baseAddress}{id}/{Version}/{id}.{Version}.nupkg";

        var temp = Path.Combine(Path.GetTempPath(), $"nomad-roslyn-{Guid.NewGuid():N}.zip");
        try
        {
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var file = File.Create(temp);
                var buffer = new byte[256 * 1024];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    done += read;
                    if (total > 0)
                    {
                        progress?.Report(Math.Min(0.99, (double)done / total));
                    }
                }
            }

            // Only the server itself: the package's content/LanguageServer/<rid>/ folder.
            var staging = InstallFolder + ".partial";
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            var prefix = $"content/LanguageServer/{Rid}/";
            using (var zip = ZipFile.OpenRead(temp))
            {
                foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && e.Name.Length > 0))
                {
                    var target = Path.GetFullPath(Path.Combine(staging, entry.FullName[prefix.Length..]));
                    if (!target.StartsWith(Path.GetFullPath(staging), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
            }

            if (Directory.Exists(InstallFolder))
            {
                Directory.Delete(InstallFolder, recursive: true);
            }

            Directory.Move(staging, InstallFolder);
            progress?.Report(1);
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }
        }
    }

    // ------------------------------------------------------------------ start

    /// <summary>
    /// What the server should open in a folder: its solution (the one named like the folder, else the first; .slnx
    /// before .sln), one a little below it, or else its projects. Null when there is no C# here.
    /// </summary>
    public static (string? Solution, IReadOnlyList<string> Projects)? FindWork(string root)
    {
        static IEnumerable<string> Under(string folder, string pattern, int depth)
        {
            IEnumerable<string> found = [];
            try
            {
                found = Directory.EnumerateFiles(folder, pattern).ToList();
                if (depth > 0)
                {
                    found = found.Concat(Directory.EnumerateDirectories(folder)
                        .Where(d => !DirectoryListing.IsHidden(Path.GetFileName(d)) && !DirectoryListing.IsExcluded(Path.GetFileName(d), isDirectory: true))
                        .SelectMany(d => Under(d, pattern, depth - 1)).ToList());
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }

            return found;
        }

        string? Pick(IEnumerable<string> solutions)
        {
            var list = solutions.OrderBy(s => Path.GetExtension(s) == ".slnx" ? 0 : 1).ThenBy(s => s.Count(c => c == Path.DirectorySeparatorChar)).ToList();
            var name = Path.GetFileName(root);
            return list.FirstOrDefault(s => string.Equals(Path.GetFileNameWithoutExtension(s), name, StringComparison.OrdinalIgnoreCase)) ?? list.FirstOrDefault();
        }

        if (Pick(Under(root, "*.slnx", 0).Concat(Under(root, "*.sln", 0))) is { } here)
        {
            return (here, []);
        }

        if (Pick(Under(root, "*.slnx", 2).Concat(Under(root, "*.sln", 2))) is { } below)
        {
            return (below, []);
        }

        var projects = Under(root, "*.csproj", 4).ToList();
        return projects.Count > 0 ? (null, projects) : null;
    }

    /// <summary>Starts the server on a folder and has it load the folder's solution or projects (<see cref="Loaded"/> says when).</summary>
    public static async Task<RoslynServer> StartAsync(string root, CancellationToken cancellationToken = default)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var logs = Path.Combine(Path.GetTempPath(), "nomad-roslyn-logs");
        Directory.CreateDirectory(logs);
        var environment = new Dictionary<string, string>();
        if (DotnetRoot() is { } dotnet)
        {
            environment["DOTNET_ROOT"] = dotnet;
        }

        var connection = LspConnection.Start(Exe, ["--logLevel", "Warning", "--extensionLogDirectory", logs, "--stdio"], root, environment);
        connection.RawMessage = RawMessage;
        var server = new RoslynServer(root, connection);
        connection.RequestHandler = server.OnRequest;
        connection.Notification += server.OnNotification;
        connection.Exited += () =>
        {
            server._loaded.TrySetException(new InvalidOperationException("The C# language server stopped" + (connection.StderrTail is { Length: > 0 } t ? ":\n" + t : ".")));
            server.Exited?.Invoke();
        };

        try
        {
            var rootUri = Uri(root);
            await connection.CallAsync("initialize", new JsonObject
            {
                ["processId"] = Environment.ProcessId,
                ["clientInfo"] = new JsonObject { ["name"] = "Nomad" },
                ["rootUri"] = rootUri,
                ["workspaceFolders"] = new JsonArray(new JsonObject { ["uri"] = rootUri, ["name"] = Path.GetFileName(root) }),
                ["capabilities"] = new JsonObject
                {
                    ["workspace"] = new JsonObject { ["workspaceFolders"] = true, ["configuration"] = true },
                    ["textDocument"] = new JsonObject
                    {
                        ["synchronization"] = new JsonObject { ["didSave"] = false, ["dynamicRegistration"] = false },
                        ["hover"] = new JsonObject { ["contentFormat"] = new JsonArray("markdown", "plaintext") },
                        ["definition"] = new JsonObject { ["linkSupport"] = false },
                        ["implementation"] = new JsonObject { ["linkSupport"] = false },
                    },
                },
            }, cancellationToken, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            await connection.NotifyAsync("initialized", new JsonObject()).ConfigureAwait(false);

            switch (FindWork(root))
            {
                case ({ } solution, _):
                    server.Opened = Path.GetFileName(solution);
                    await connection.NotifyAsync("solution/open", new JsonObject { ["solution"] = Uri(solution) }).ConfigureAwait(false);
                    break;
                case (null, { Count: > 0 } projects):
                    server.Opened = projects.Count == 1 ? Path.GetFileName(projects[0]) : $"{projects.Count} projects";
                    await connection.NotifyAsync("project/open", new JsonObject { ["projects"] = new JsonArray([.. projects.Select(p => (JsonNode?)Uri(p))]) }).ConfigureAwait(false);
                    break;
                default:
                    server._loaded.TrySetResult();
                    break;
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return server;
    }

    // The server's requests: nothing to configure, nothing to register, no progress shown; answered null.
    private JsonNode? OnRequest(string method, JsonElement parameters)
    {
        if (method == "workspace/configuration" && parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            return new JsonArray([.. Enumerable.Range(0, items.GetArrayLength()).Select(_ => (JsonNode?)null)]);
        }

        return null;
    }

    private void OnNotification(string method, JsonElement parameters)
    {
        if (method == "workspace/projectInitializationComplete")
        {
            _loaded.TrySetResult();
        }
    }

    // ------------------------------------------------------------------ documents

    /// <summary>The URI the protocol names a file by.</summary>
    public static string Uri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    /// <summary>A file's path from its URI (null for other schemes). Unescaped by hand: a "%3A" drive or a "%23" in a name stays a path.</summary>
    public static string? PathOf(string uri)
    {
        if (uri.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(System.Uri.UnescapeDataString(uri[8..]).Replace('/', Path.DirectorySeparatorChar));
        }

        return uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(@"\\" + System.Uri.UnescapeDataString(uri[7..]).Replace('/', Path.DirectorySeparatorChar))
            : null;
    }

    /// <summary>A file was opened in the editor: from now on the server reads it from here, not from disk.</summary>
    public Task OpenAsync(string path, string text)
    {
        _versions[path] = 1;
        return _connection.NotifyAsync("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri(path), ["languageId"] = "csharp", ["version"] = 1, ["text"] = text },
        });
    }

    /// <summary>An open file's whole new text.</summary>
    public Task ChangeAsync(string path, string text)
    {
        var version = _versions[path] = _versions.GetValueOrDefault(path) + 1;
        return _connection.NotifyAsync("textDocument/didChange", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri(path), ["version"] = version },
            ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = text }),
        });
    }

    public Task CloseAsync(string path)
    {
        _versions.Remove(path);
        return _connection.NotifyAsync("textDocument/didClose", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = Uri(path) } });
    }

    // ------------------------------------------------------------------ questions

    private static JsonObject At(string path, int line, int column) => new()
    {
        ["textDocument"] = new JsonObject { ["uri"] = Uri(path) },
        ["position"] = new JsonObject { ["line"] = line, ["character"] = column },
    };

    /// <summary>Where the symbol at a place is declared (several for a partial type; a decompiled file for a library's).</summary>
    public async Task<IReadOnlyList<LspLocation>> DefinitionAsync(string path, int line, int column, CancellationToken cancellationToken = default) =>
        Locations(await _connection.CallAsync("textDocument/definition", At(path, line, column), cancellationToken, TimeSpan.FromSeconds(15)).ConfigureAwait(false));

    /// <summary>What implements the interface or member at a place (or overrides it).</summary>
    public async Task<IReadOnlyList<LspLocation>> ImplementationAsync(string path, int line, int column, CancellationToken cancellationToken = default) =>
        Locations(await _connection.CallAsync("textDocument/implementation", At(path, line, column), cancellationToken, TimeSpan.FromSeconds(30)).ConfigureAwait(false));

    /// <summary>The hover text at a place (markdown: the declaration in a code block, then its documentation); null for nothing.</summary>
    public async Task<string?> HoverAsync(string path, int line, int column, CancellationToken cancellationToken = default)
    {
        var result = await _connection.CallAsync("textDocument/hover", At(path, line, column), cancellationToken, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("contents", out var contents))
        {
            return null;
        }

        return contents.ValueKind switch
        {
            JsonValueKind.String => contents.GetString(),
            JsonValueKind.Object => contents.TryGetProperty("value", out var value) ? value.GetString() : null,
            JsonValueKind.Array => string.Join("\n\n", contents.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() : c.TryGetProperty("value", out var v) ? $"```csharp\n{v.GetString()}\n```" : null)),
            _ => null,
        };
    }

    private static List<LspLocation> Locations(JsonElement result)
    {
        var list = new List<LspLocation>();
        void Add(JsonElement location)
        {
            if (location.ValueKind == JsonValueKind.Object &&
                (location.TryGetProperty("uri", out var uri) || location.TryGetProperty("targetUri", out uri)) &&
                PathOf(uri.GetString() ?? "") is { } path &&
                (location.TryGetProperty("range", out var range) || location.TryGetProperty("targetSelectionRange", out range)))
            {
                var start = range.GetProperty("start");
                var end = range.GetProperty("end");
                list.Add(new LspLocation(path, start.GetProperty("line").GetInt32(), start.GetProperty("character").GetInt32(),
                    end.GetProperty("line").GetInt32(), end.GetProperty("character").GetInt32()));
            }
        }

        if (result.ValueKind == JsonValueKind.Array)
        {
            foreach (var location in result.EnumerateArray())
            {
                Add(location);
            }
        }
        else
        {
            Add(result);
        }

        return list;
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
