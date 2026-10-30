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
    private readonly Dictionary<string, string> _texts = new(StringComparer.OrdinalIgnoreCase);

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
                    ["workspace"] = new JsonObject
                    {
                        ["workspaceFolders"] = true,
                        ["configuration"] = true,
                        ["workspaceEdit"] = new JsonObject { ["documentChanges"] = true },
                        ["diagnostics"] = new JsonObject { ["refreshSupport"] = true },
                    },
                    ["textDocument"] = new JsonObject
                    {
                        ["synchronization"] = new JsonObject { ["didSave"] = false, ["dynamicRegistration"] = false },
                        ["hover"] = new JsonObject { ["contentFormat"] = new JsonArray("markdown", "plaintext") },
                        ["definition"] = new JsonObject { ["linkSupport"] = false },
                        ["implementation"] = new JsonObject { ["linkSupport"] = false },
                        ["references"] = new JsonObject(),
                        ["signatureHelp"] = new JsonObject
                        {
                            ["signatureInformation"] = new JsonObject
                            {
                                ["documentationFormat"] = new JsonArray("plaintext", "markdown"),
                                ["parameterInformation"] = new JsonObject { ["labelOffsetSupport"] = true },
                                ["activeParameterSupport"] = true,
                            },
                            ["contextSupport"] = true,
                        },
                        ["rename"] = new JsonObject { ["prepareSupport"] = true },
                        ["diagnostic"] = new JsonObject { ["dynamicRegistration"] = false, ["relatedDocumentSupport"] = false },
                        ["publishDiagnostics"] = new JsonObject { ["relatedInformation"] = false },
                        ["completion"] = new JsonObject
                        {
                            ["completionItem"] = new JsonObject
                            {
                                ["snippetSupport"] = false,
                                ["documentationFormat"] = new JsonArray("plaintext", "markdown"),
                                ["resolveSupport"] = new JsonObject { ["properties"] = new JsonArray("documentation", "detail", "additionalTextEdits") },
                            },
                            ["completionList"] = new JsonObject { ["itemDefaults"] = new JsonArray("editRange", "data", "commitCharacters") },
                        },
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
            // Its defaults, but types from namespaces not imported yet are offered too (adding their using), as in Visual Studio.
            return new JsonArray([.. items.EnumerateArray().Select(item => (JsonNode?)(LspJson.String(item, "section") switch
            {
                "csharp|completion.dotnet_show_completion_items_from_unimported_namespaces" => JsonValue.Create(true),
                _ => null,
            }))]);
        }

        if (method == "workspace/diagnostic/refresh")
        {
            DiagnosticsStale?.Invoke();
        }

        return null;
    }

    private void OnNotification(string method, JsonElement parameters)
    {
        if (method == "workspace/projectInitializationComplete")
        {
            _loaded.TrySetResult();
        }
        else if (method == "textDocument/publishDiagnostics" && LspJson.String(parameters, "uri") is { } uri && PathOf(uri) is { } path &&
                 parameters.TryGetProperty("diagnostics", out var items))
        {
            DiagnosticsPublished?.Invoke(path, Diagnostics(items));
        }
    }

    /// <summary>The server says what it found before may be out of date (another file changed, the projects loaded): ask again.</summary>
    public event Action? DiagnosticsStale;

    /// <summary>The server sent a file's diagnostics on its own (servers that push them rather than being asked).</summary>
    public event Action<string, IReadOnlyList<LspDiagnostic>>? DiagnosticsPublished;

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
        _texts[path] = text;
        return _connection.NotifyAsync("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri(path), ["languageId"] = "csharp", ["version"] = 1, ["text"] = text },
        });
    }

    /// <summary>An open file's new text: sent as the one span that changed (Roslyn takes only ranged changes).</summary>
    public Task ChangeAsync(string path, string text)
    {
        var old = _texts.GetValueOrDefault(path) ?? "";
        _texts[path] = text;
        var (start, end, insert) = Difference(old, text);
        var starts = LspTextEdit.LineStarts(old);
        var version = _versions[path] = _versions.GetValueOrDefault(path) + 1;
        return _connection.NotifyAsync("textDocument/didChange", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri(path), ["version"] = version },
            ["contentChanges"] = new JsonArray(new JsonObject
            {
                ["range"] = new JsonObject { ["start"] = Position(starts, start), ["end"] = Position(starts, end) },
                ["text"] = insert,
            }),
        });
    }

    /// <summary>The one span of an old text a new one differs in (between their common start and end), and what replaces it.</summary>
    public static (int Start, int End, string Insert) Difference(string old, string text)
    {
        var start = 0;
        var most = Math.Min(old.Length, text.Length);
        while (start < most && old[start] == text[start])
        {
            start++;
        }

        // Don't split a "\r\n": the server would see a lone '\r' as a line of its own.
        if (start > 0 && old[start - 1] == '\r')
        {
            start--;
        }

        var tail = 0;
        while (tail < most - start && old[^(tail + 1)] == text[^(tail + 1)])
        {
            tail++;
        }

        if (tail > 0 && old.Length - tail < old.Length && old[old.Length - tail] == '\n' && old.Length - tail - 1 >= start && old[old.Length - tail - 1] == '\r')
        {
            tail--;
        }

        return (start, old.Length - tail, text[start..(text.Length - tail)]);
    }

    private static JsonObject Position(List<int> starts, int offset)
    {
        var line = starts.BinarySearch(offset);
        line = line >= 0 ? line : ~line - 1;
        return new JsonObject { ["line"] = line, ["character"] = offset - starts[line] };
    }

    public Task CloseAsync(string path)
    {
        _versions.Remove(path);
        _texts.Remove(path);
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

    /// <summary>Every place the symbol at a place is used, its declaration included.</summary>
    public async Task<IReadOnlyList<LspLocation>> ReferencesAsync(string path, int line, int column, CancellationToken cancellationToken = default)
    {
        var parameters = At(path, line, column);
        parameters["context"] = new JsonObject { ["includeDeclaration"] = true };
        return Locations(await _connection.CallAsync("textDocument/references", parameters, cancellationToken, TimeSpan.FromSeconds(60)).ConfigureAwait(false));
    }

    /// <summary>The name at a place that a rename would change (its range and text), or null when it can't be renamed.</summary>
    public async Task<(LspLocation Range, string Name)?> PrepareRenameAsync(string path, int line, int column, CancellationToken cancellationToken = default)
    {
        var result = await _connection.CallAsync("textDocument/prepareRename", At(path, line, column), cancellationToken, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var range = result.TryGetProperty("range", out var inner) ? inner : result;
        if (range.ValueKind != JsonValueKind.Object || !range.TryGetProperty("start", out _))
        {
            return null;
        }

        var (l, c, el, ec) = LspJson.Range(range);
        return (new LspLocation(path, l, c, el, ec), LspJson.String(result, "placeholder") ?? "");
    }

    /// <summary>What renaming the symbol at a place changes: each file's edits (files not open in the editor too).</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>>> RenameAsync(string path, int line, int column, string newName, CancellationToken cancellationToken = default)
    {
        var parameters = At(path, line, column);
        parameters["newName"] = newName;
        var result = await _connection.CallAsync("textDocument/rename", parameters, cancellationToken, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        var edits = new Dictionary<string, List<LspTextEdit>>(StringComparer.OrdinalIgnoreCase);
        void Add(string uri, JsonElement list)
        {
            if (PathOf(uri) is not { } file || list.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            if (!edits.TryGetValue(file, out var into))
            {
                edits[file] = into = [];
            }

            into.AddRange(list.EnumerateArray().Select(LspJson.Edit).OfType<LspTextEdit>());
        }

        if (result.ValueKind == JsonValueKind.Object)
        {
            if (result.TryGetProperty("documentChanges", out var changes) && changes.ValueKind == JsonValueKind.Array)
            {
                foreach (var change in changes.EnumerateArray())
                {
                    if (change.TryGetProperty("textDocument", out var document) && LspJson.String(document, "uri") is { } uri && change.TryGetProperty("edits", out var list))
                    {
                        Add(uri, list);
                    }
                }
            }
            else if (result.TryGetProperty("changes", out var byUri) && byUri.ValueKind == JsonValueKind.Object)
            {
                foreach (var file in byUri.EnumerateObject())
                {
                    Add(file.Name, file.Value);
                }
            }
        }

        return edits.ToDictionary(e => e.Key, e => (IReadOnlyList<LspTextEdit>)e.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A file's errors and warnings (it must be open).</summary>
    public async Task<IReadOnlyList<LspDiagnostic>> DiagnosticsAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await _connection.CallAsync("textDocument/diagnostic", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri(path) },
        }, cancellationToken, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        return result.ValueKind == JsonValueKind.Object && result.TryGetProperty("items", out var items) ? Diagnostics(items) : [];
    }

    private static List<LspDiagnostic> Diagnostics(JsonElement items)
    {
        var list = new List<LspDiagnostic>();
        if (items.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("range", out var range))
            {
                continue;
            }

            // Roslyn tags the ones it greys out (unused usings…) as hints: "unnecessary" is shown faded, not underlined.
            var severity = item.TryGetProperty("severity", out var s) && s.ValueKind == JsonValueKind.Number ? (LspSeverity)s.GetInt32() : LspSeverity.Error;
            var code = item.TryGetProperty("code", out var c) ? c.ValueKind == JsonValueKind.String ? c.GetString() : c.ToString() : null;
            var (line, column, endLine, endColumn) = LspJson.Range(range);
            list.Add(new LspDiagnostic(line, column, endLine, endColumn, severity, LspJson.String(item, "message") ?? "", code));
        }

        return list;
    }

    /// <summary>What could be typed at a place: when the user typed a character that asks for it, that character.</summary>
    public async Task<LspCompletionList> CompletionAsync(string path, int line, int column, string? triggerCharacter, CancellationToken cancellationToken = default)
    {
        var parameters = At(path, line, column);
        parameters["context"] = triggerCharacter is null
            ? new JsonObject { ["triggerKind"] = 1 }
            : new JsonObject { ["triggerKind"] = 2, ["triggerCharacter"] = triggerCharacter };
        var result = await _connection.CallAsync("textDocument/completion", parameters, cancellationToken, TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        JsonElement items;
        var incomplete = false;
        LspTextEdit? defaultRange = null;
        JsonElement? defaultData = null;
        if (result.ValueKind == JsonValueKind.Array)
        {
            items = result;
        }
        else if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("items", out items))
        {
            incomplete = result.TryGetProperty("isIncomplete", out var i) && i.ValueKind == JsonValueKind.True;
            if (result.TryGetProperty("itemDefaults", out var defaults))
            {
                if (defaults.TryGetProperty("editRange", out var range))
                {
                    var r = range.TryGetProperty("replace", out var replace) ? replace : range;
                    var (l, c, el, ec) = LspJson.Range(r);
                    defaultRange = new LspTextEdit(l, c, el, ec, "");
                }

                if (defaults.TryGetProperty("data", out var data))
                {
                    defaultData = data;
                }
            }
        }
        else
        {
            return new LspCompletionList([], false);
        }

        var list = new List<LspCompletion>();
        foreach (var item in items.EnumerateArray())
        {
            var label = LspJson.String(item, "label") ?? "";
            var edit = item.TryGetProperty("textEdit", out var textEdit) ? LspJson.Edit(textEdit) : null;
            var insert = edit?.NewText ?? LspJson.String(item, "textEditText") ?? LspJson.String(item, "insertText") ?? label;
            edit ??= defaultRange is null ? null : defaultRange with { NewText = insert };

            // Resolving sends the item back as it came: with the list's default data filled in.
            string raw;
            if (defaultData is { } data && !item.TryGetProperty("data", out _))
            {
                var node = JsonNode.Parse(item.GetRawText())!.AsObject();
                node["data"] = JsonNode.Parse(data.GetRawText());
                raw = node.ToJsonString();
            }
            else
            {
                raw = item.GetRawText();
            }

            list.Add(new LspCompletion(
                label,
                item.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.Number ? kind.GetInt32() : 1,
                LspJson.String(item, "filterText") ?? label,
                LspJson.String(item, "sortText") ?? label,
                insert,
                edit,
                LspJson.String(item, "detail"),
                item.TryGetProperty("preselect", out var preselect) && preselect.ValueKind == JsonValueKind.True,
                raw));
        }

        return new LspCompletionList(list, incomplete);
    }

    /// <summary>The overloads of the call the caret is in (null outside one): typed a '(' or ',' (the trigger), or moved within it.</summary>
    public async Task<LspSignatureHelp?> SignatureHelpAsync(string path, int line, int column, string? trigger, bool retrigger, CancellationToken cancellationToken = default)
    {
        var parameters = At(path, line, column);
        parameters["context"] = new JsonObject
        {
            ["triggerKind"] = trigger is null ? (retrigger ? 3 : 1) : 2,
            ["triggerCharacter"] = trigger,
            ["isRetrigger"] = retrigger,
        };
        var result = await _connection.CallAsync("textDocument/signatureHelp", parameters, cancellationToken, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("signatures", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
        {
            return null;
        }

        var signatures = new List<LspSignature>();
        foreach (var signature in list.EnumerateArray())
        {
            var label = LspJson.String(signature, "label") ?? "";
            var parts = new List<(int, int, string?)>();
            if (signature.TryGetProperty("parameters", out var ps) && ps.ValueKind == JsonValueKind.Array)
            {
                var from = 0;
                foreach (var p in ps.EnumerateArray())
                {
                    var doc = LspJson.Markup(p, "documentation");
                    if (p.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.Array && l.GetArrayLength() == 2)
                    {
                        parts.Add((l[0].GetInt32(), l[1].GetInt32(), doc));
                    }
                    else if (l.ValueKind == JsonValueKind.String && l.GetString() is { Length: > 0 } text && label.IndexOf(text, from, StringComparison.Ordinal) is var at and >= 0)
                    {
                        // A parameter named by its text: found in the declaration after the one before it.
                        parts.Add((at, at + text.Length, doc));
                        from = at + text.Length;
                    }
                    else
                    {
                        parts.Add((0, 0, doc));
                    }
                }
            }

            int? active = signature.TryGetProperty("activeParameter", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : null;
            signatures.Add(new LspSignature(label, LspJson.Markup(signature, "documentation"), parts, active));
        }

        var activeSignature = result.TryGetProperty("activeSignature", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0;
        var activeParameter = result.TryGetProperty("activeParameter", out var ap) && ap.ValueKind == JsonValueKind.Number ? ap.GetInt32() : 0;
        return new LspSignatureHelp(signatures, Math.Clamp(activeSignature, 0, signatures.Count - 1), activeParameter);
    }

    /// <summary>More about a completion that was chosen: the usings it brings, its documentation.</summary>
    public async Task<LspResolvedCompletion> ResolveAsync(LspCompletion completion, CancellationToken cancellationToken = default)
    {
        var result = await _connection.CallAsync("completionItem/resolve", JsonNode.Parse(completion.Raw), cancellationToken, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var edits = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("additionalTextEdits", out var more) && more.ValueKind == JsonValueKind.Array
            ? more.EnumerateArray().Select(LspJson.Edit).OfType<LspTextEdit>().ToList()
            : [];
        return new LspResolvedCompletion(edits, LspJson.Markup(result, "documentation"), LspJson.String(result, "detail"));
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
