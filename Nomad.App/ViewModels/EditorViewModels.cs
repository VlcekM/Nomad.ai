using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nomad.App.Ide;

namespace Nomad.App.ViewModels;

/// <summary>
/// An open file in the Files view, as a tab. A preview tab (a single click in the tree) is replaced by the next file
/// opened that way; double-clicking, pinning, opening it again or editing it keeps it. Its text is a
/// <see cref="CodeDocument"/>, saved with Ctrl+S in the file's own encoding and line breaks; a change on disk reloads a
/// clean tab and is pointed out on an edited one.
/// </summary>
public sealed partial class FileTab : ObservableObject, IDisposable
{
    /// <summary>Larger files show only their first part, and can't be edited.</summary>
    private const int PreviewBytes = 256 * 1024;

    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _settle;

    /// <summary>The file's last write time as this tab last read or wrote it, to tell others' writes from its own.</summary>
    private DateTime _knownWrite;

    public FileTab(string path, string? folder)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        var parent = System.IO.Path.GetDirectoryName(path) ?? "";
        var relative = folder is null ? parent : System.IO.Path.GetRelativePath(folder, parent);
        RelativeFolder = relative == "." ? "" : relative.Replace(System.IO.Path.DirectorySeparatorChar, '/');
        _ = LoadAsync();
        Watch(parent);
    }

    public string Path { get; }

    public string Name { get; }

    /// <summary>The file's folder, relative to the session's ("" for its root).</summary>
    public string RelativeFolder { get; }

    public bool HasRelativeFolder => RelativeFolder.Length > 0;

    /// <summary>The group it is in (its commands, for the tab's buttons and menu).</summary>
    public EditorGroup? Group { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinText))]
    public partial bool IsPinned { get; set; }

    public string PinText => IsPinned ? "Unpin tab" : "Pin tab";

    [ObservableProperty]
    public partial bool IsPreview { get; set; }

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>The leftmost tab, flush with the card's edge.</summary>
    [ObservableProperty]
    public partial bool IsFirst { get; set; }

    /// <summary>Active in the group that has the focus: its tab carries the accent line.</summary>
    [ObservableProperty]
    public partial bool IsFocused { get; set; }

    /// <summary>The file's text, with its caret and undo history; read-only until it has loaded.</summary>
    [ObservableProperty]
    public partial CodeDocument Document { get; private set; } = CodeDocument.Empty;

    /// <summary>Edited since it was loaded or saved.</summary>
    [ObservableProperty]
    public partial bool IsDirty { get; private set; }

    /// <summary>Another program (Claude, say) wrote the file while it had unsaved edits here.</summary>
    [ObservableProperty]
    public partial bool ChangedOnDisk { get; private set; }

    /// <summary>Closing it, with unsaved edits: the card asks whether to save them first.</summary>
    [ObservableProperty]
    public partial bool IsAskingToClose { get; set; }

    /// <summary>Instead of the text: why there is none ("Binary file"), or why it can't be edited.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string? Note { get; private set; }

    public bool HasNote => Note is not null;

    /// <summary>Where the file was scrolled to, kept while another tab is shown.</summary>
    public Avalonia.Vector ScrollOffset { get; set; }

    /// <summary>The type the caret is in, for the navigation bar ("FileTree"); null outside one.</summary>
    [ObservableProperty]
    public partial Nomad.Core.Code.CodeSymbol? CaretType { get; private set; }

    /// <summary>The member the caret is in ("Visible"); null outside one.</summary>
    [ObservableProperty]
    public partial Nomad.Core.Code.CodeSymbol? CaretMember { get; private set; }

    // The file's declarations, read again a moment after its text changes; the caret's place among them after it moves.
    private List<Nomad.Core.Code.CodeSymbol> _symbols = [];
    private DispatcherTimer? _crumbTimer;
    private bool _rescan = true;

    private void OnTextChanged()
    {
        _rescan = true;
        if (_languageOpen)
        {
            Language?.Change(Path, Document.Text);
        }

        ScheduleCrumbs(300);
        ScheduleGitHunks();
    }

    private void ScheduleCrumbs(int ms)
    {
        if (!Nomad.Core.Code.SymbolScanner.CanScan(Path))
        {
            return;
        }

        if (_crumbTimer is null)
        {
            _crumbTimer = new DispatcherTimer();
            _crumbTimer.Tick += (_, _) =>
            {
                _crumbTimer.Stop();
                UpdateCrumbs();
            };
        }

        // A pending rescan keeps its own (longer) wait.
        if (!_crumbTimer.IsEnabled || _crumbTimer.Interval.TotalMilliseconds < ms)
        {
            _crumbTimer.Stop();
            _crumbTimer.Interval = TimeSpan.FromMilliseconds(ms);
            _crumbTimer.Start();
        }
    }

    private void UpdateCrumbs()
    {
        var doc = Document;
        if (_rescan)
        {
            _rescan = false;
            _symbols = doc == CodeDocument.Empty ? [] : Nomad.Core.Code.SymbolScanner.Scan(Path, string.Join('\n', doc.Lines));
        }

        // Enclosing: declared at or above the caret, with its block (its folding region) reaching down to it.
        var line = doc.Caret.Line;
        Nomad.Core.Code.CodeSymbol? type = null, member = null;
        foreach (var symbol in _symbols)
        {
            if (symbol.Line > line)
            {
                break;
            }

            var encloses = symbol.Line == line || doc.Folding.RegionAt(symbol.Line) is { } region && line <= region.End;
            if (!encloses)
            {
                continue;
            }

            if (symbol.IsType)
            {
                type = symbol;
                member = null;
            }
            else if (symbol.Kind is not (Nomad.Core.Code.SymbolKind.EnumMember or Nomad.Core.Code.SymbolKind.Field or Nomad.Core.Code.SymbolKind.Constant))
            {
                member = symbol;
            }
        }

        CaretType = type;
        CaretMember = member;
    }

    partial void OnDocumentChanged(CodeDocument oldValue, CodeDocument newValue)
    {
        oldValue.Changed -= OnTextChanged;
        oldValue.CaretChanged -= OnCaretChanged;
        newValue.Changed += OnTextChanged;
        newValue.CaretChanged += OnCaretChanged;
        _rescan = true;
        ScheduleCrumbs(50);
        oldValue.DirtyChanged -= OnDirtyChanged;

        newValue.DirtyChanged += OnDirtyChanged;
        IsDirty = newValue.IsDirty;
        SyncLanguage();
        OnDiagnosticsChanged(Path);
    }

    private void OnCaretChanged() => ScheduleCrumbs(80);

    // The C# language server, told of this file while it is open here (C# files only).
    private bool _languageOpen;

    public Nomad.App.Ide.CSharpLanguage? Language
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            if (field is not null)
            {
                field.DiagnosticsChanged -= OnDiagnosticsChanged;
            }

            if (_languageOpen)
            {
                field?.Close(Path);
                _languageOpen = false;
            }

            field = value;
            if (field is not null)
            {
                field.DiagnosticsChanged += OnDiagnosticsChanged;
            }

            SyncLanguage();
        }
    }

    private void OnDiagnosticsChanged(string path)
    {
        if (string.Equals(path, Path, StringComparison.OrdinalIgnoreCase) && Language is { } language && Document != CodeDocument.Empty)
        {
            Document.SetDiagnostics(language.DiagnosticsOf(Path));
        }
    }

    private void SyncLanguage()
    {
        if (Language is not { } language || !Nomad.App.Ide.CSharpLanguage.Handles(Path) || Document == CodeDocument.Empty)
        {
            return;
        }

        if (_languageOpen)
        {
            language.Change(Path, Document.Text);
        }
        else
        {
            language.Open(Path, Document.Text);
            _languageOpen = true;
        }
    }

    // The file as last committed, and how the text differs from it (the gutter's git margin, the diff view).
    private string[]? _gitBase;
    private DispatcherTimer? _gitTimer;

    /// <summary>The file is committed in git: it can be compared with HEAD.</summary>
    [ObservableProperty]
    public partial bool HasGitBase { get; private set; }

    /// <summary>The diff with HEAD shows in place of the text.</summary>
    [ObservableProperty]
    public partial bool ShowDiff { get; set; }

    /// <summary>The diff view's rows (made when it is shown, and again as the text changes).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<DiffRow> DiffRows { get; private set; } = [];

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ToggleDiff() => ShowDiff = !ShowDiff && HasGitBase;

    private bool _wantDiff;

    /// <summary>Shows the diff with HEAD (now, or once HEAD's version has been read).</summary>
    public void RequestDiff()
    {
        if (HasGitBase)
        {
            ShowDiff = true;
        }
        else
        {
            _wantDiff = true;
        }
    }

    partial void OnShowDiffChanged(bool value) => UpdateGitHunks();

    /// <summary>Reads HEAD's version again (after a load, a save, a commit) and compares.</summary>
    public async Task RefreshGitBaseAsync()
    {
        _gitBase = await Nomad.Core.Git.GitInfo.HeadLinesAsync(Path);
        HasGitBase = _gitBase is not null;
        if (!HasGitBase)
        {
            ShowDiff = false;
        }
        else if (_wantDiff)
        {
            ShowDiff = true;
        }

        _wantDiff = false;
        UpdateGitHunks();
    }

    private void ScheduleGitHunks()
    {
        if (_gitBase is null)
        {
            return;
        }

        if (_gitTimer is null)
        {
            _gitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _gitTimer.Tick += (_, _) =>
            {
                _gitTimer.Stop();
                UpdateGitHunks();
            };
        }

        _gitTimer.Stop();
        _gitTimer.Start();
    }

    private void UpdateGitHunks()
    {
        var doc = Document;
        if (_gitBase is not { } head || doc == CodeDocument.Empty)
        {
            doc.SetGitHunks(null);
            DiffRows = [];
            return;
        }

        var lines = doc.Lines.ToList();
        var hunks = Nomad.Core.Code.LineDiff.Compute(head, lines);
        doc.SetGitHunks(hunks);
        DiffRows = ShowDiff ? DiffRow.Build(head, lines, hunks) : [];
    }

    private void OnDirtyChanged()
    {
        IsDirty = Document.IsDirty;

        // Edited: no longer a preview the next click in the tree would replace.
        if (IsDirty && IsPreview)
        {
            Group?.KeepCommand.Execute(this);
        }
    }

    // A Go To asked for before the file was read: done once it is.
    private (TextPos At, int Length)? _pendingGoTo;
    private bool _loaded;

    /// <summary>
    /// Puts the caret at a line and column (0-based), selecting that many characters from there (a find's match), and
    /// shows it, now or once the file is read.
    /// </summary>
    public void GoTo(int line, int column, int length = 0)
    {
        if (_loaded && Document != CodeDocument.Empty)
        {
            Show(Document, new TextPos(line, column), length);
        }
        else
        {
            _pendingGoTo = (new TextPos(line, column), length);
        }
    }

    private static void Show(CodeDocument doc, TextPos at, int length)
    {
        if (length > 0)
        {
            doc.Select(at, new TextPos(at.Line, at.Index + length), focus: true);
        }
        else
        {
            doc.GoTo(at);
        }
    }

    private async Task LoadAsync(bool keepPlace = false)
    {
        var (doc, note, written) = await Task.Run(() => Read(Path));
        if (keepPlace)
        {
            doc.Anchor = doc.Clamp(Document.Anchor);
            doc.Caret = doc.Clamp(Document.Caret);
        }

        if (doc != CodeDocument.Empty) // Binary, or unreadable: nothing to color.
        {
            doc.Highlighting = Highlighter.ForFile(Path) is { } grammar ? new CodeHighlighting(grammar) : null;
        }

        // Reloaded under the editor (the agent wrote it): the lines that changed flash and join the change bar.
        var before = keepPlace && Document != CodeDocument.Empty ? Document.Lines.ToList() : null;

        _knownWrite = written;
        Document = doc;
        Note = note;
        ChangedOnDisk = false;
        _loaded = true;
        if (before is not null && doc != CodeDocument.Empty)
        {
            var lines = doc.Lines;
            var hunks = await Task.Run(() => Nomad.Core.Code.LineDiff.Compute(before, lines));
            var flashed = hunks.SelectMany(h => h.NewCount > 0 ? Enumerable.Range(h.NewStart, h.NewCount) : [Math.Max(0, h.NewStart - 1)]).Distinct().ToList();
            if (flashed.Count > 0 && Document == doc)
            {
                doc.Flash(flashed);
            }
        }

        _ = RefreshGitBaseAsync();
        if (_pendingGoTo is { } pending && doc != CodeDocument.Empty)
        {
            _pendingGoTo = null;
            Dispatcher.UIThread.Post(() => Show(doc, pending.At, pending.Length), DispatcherPriority.Loaded);
        }
    }

    private static (CodeDocument Doc, string? Note, DateTime Written) Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var written = File.GetLastWriteTimeUtc(path);
            var buffer = new byte[(int)Math.Min(stream.Length, PreviewBytes)];
            stream.ReadExactly(buffer);
            var truncated = stream.Length > PreviewBytes;

            var (encoding, bom) = buffer switch
            {
                [0xEF, 0xBB, 0xBF, ..] => (new UTF8Encoding(true), 3),
                [0xFF, 0xFE, ..] => (Encoding.Unicode, 2),
                [0xFE, 0xFF, ..] => (Encoding.BigEndianUnicode, 2),
                _ => ((Encoding)new UTF8Encoding(false, throwOnInvalidBytes: true), 0),
            };
            if (bom == 0 && Array.IndexOf(buffer, (byte)0, 0, Math.Min(buffer.Length, 8000)) >= 0)
            {
                return (CodeDocument.Empty, "Binary file", written);
            }

            string text;
            string? note = truncated ? $"Showing the first {PreviewBytes / 1024} KB, read-only" : null;
            try
            {
                text = encoding.GetString(buffer, bom, buffer.Length - bom);
            }
            catch (DecoderFallbackException)
            {
                // Another code page: shown as well as UTF-8 can, but saving it would change its bytes.
                text = Encoding.UTF8.GetString(buffer);
                note ??= "Not UTF-8, read-only";
                truncated = true;
            }

            // A file cut short (mid-character, too) or not UTF-8 is only shown.
            return (new CodeDocument(text, readOnly: truncated, encoding, bom > 0), note, written);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (CodeDocument.Empty, e.Message, default);
        }
    }

    [RelayCommand]
    private Task Save() => SaveAsync();

    /// <summary>Writes the edits to the file (through a temporary file beside it, so a failed write leaves it whole).</summary>
    public async Task<bool> SaveAsync()
    {
        var doc = Document;
        if (doc.IsReadOnly || !doc.IsDirty && !ChangedOnDisk)
        {
            return true;
        }

        var bytes = doc.Encode();
        var version = doc.Text;
        FileTree.NoteOwnWrite(Path);
        try
        {
            _knownWrite = await Task.Run(() =>
            {
                var temp = Path + ".nomad-save";
                File.WriteAllBytes(temp, bytes);
                if (File.Exists(Path))
                {
                    File.Replace(temp, Path, null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temp, Path);
                }

                return File.GetLastWriteTimeUtc(Path);
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Note = "Couldn't save: " + e.Message;
            return false;
        }

        // Typing during the write leaves it dirty.
        if (doc.Text == version)
        {
            doc.MarkSaved();
        }

        ChangedOnDisk = false;
        if (Note?.StartsWith("Couldn't save", StringComparison.Ordinal) == true)
        {
            Note = null;
        }

        return true;
    }

    /// <summary>Takes the file as it is on disk now, dropping the edits here.</summary>
    [RelayCommand]
    private Task Reload() => LoadAsync(keepPlace: true);

    /// <summary>Keeps the edits here over the change on disk (saving overwrites it).</summary>
    [RelayCommand]
    private void KeepMine() => ChangedOnDisk = false;

    // Changes on disk: a clean tab follows them; an edited one says so (saving then overwrites them).
    private void Watch(string folder)
    {
        try
        {
            _watcher = new FileSystemWatcher(folder, Name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
        }
        catch (Exception e) when (e is ArgumentException or IOException)
        {
            return;
        }

        FileSystemEventHandler changed = (_, _) => Dispatcher.UIThread.Post(Settle);
        _watcher.Changed += changed;
        _watcher.Created += changed;
        _watcher.Renamed += (_, _) => Dispatcher.UIThread.Post(Settle);
    }

    // A write comes as several events: act once they stop.
    private void Settle()
    {
        if (_settle is null)
        {
            _settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _settle.Tick += (_, _) =>
            {
                _settle.Stop();
                OnDiskChanged();
            };
        }

        _settle.Stop();
        _settle.Start();
    }

    private void OnDiskChanged()
    {
        DateTime written;
        try
        {
            written = File.GetLastWriteTimeUtc(Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (written == _knownWrite || !File.Exists(Path))
        {
            return;
        }

        if (IsDirty)
        {
            _knownWrite = written;
            ChangedOnDisk = true;
        }
        else
        {
            _ = LoadAsync(keepPlace: true);
        }
    }

    public void Dispose()
    {
        Language = null;
        _watcher?.Dispose();
        _watcher = null;
        _settle?.Stop();
        Document.DirtyChanged -= OnDirtyChanged;
    }
}

/// <summary>A row of the diff with HEAD: a line both have, one only HEAD had, one only the text has, or a gap.</summary>
public sealed record DiffRow(string Kind, string OldNumber, string NewNumber, string Text)
{
    private const int Context = 3;

    public bool IsAdded => Kind == "+";

    public bool IsRemoved => Kind == "-";

    public bool IsGap => Kind == "...";

    /// <summary>Each hunk with three lines around it, the gaps between them folded to one row.</summary>
    public static List<DiffRow> Build(IReadOnlyList<string> head, IReadOnlyList<string> text, IReadOnlyList<Nomad.Core.Code.DiffHunk> hunks)
    {
        var rows = new List<DiffRow>();
        static string N(int i) => (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        int oldAt = 0, newAt = 0;
        foreach (var hunk in hunks)
        {
            // The common lines before it: the last few shown, any more folded into a gap.
            var common = hunk.NewStart - newAt;
            var skip = rows.Count == 0 ? Math.Max(0, common - Context) : Math.Max(0, common - 2 * Context);
            for (var i = 0; i < common; i++)
            {
                if (skip > 0 && i == (rows.Count == 0 ? 0 : Context))
                {
                    rows.Add(new DiffRow("...", "", "", $"{skip} unchanged lines"));
                    i += skip - 1;
                    continue;
                }

                rows.Add(new DiffRow(" ", N(oldAt + i), N(newAt + i), text[newAt + i]));
            }

            for (var i = 0; i < hunk.OldCount; i++)
            {
                rows.Add(new DiffRow("-", N(hunk.OldStart + i), "", head[hunk.OldStart + i]));
            }

            for (var i = 0; i < hunk.NewCount; i++)
            {
                rows.Add(new DiffRow("+", "", N(hunk.NewStart + i), text[hunk.NewStart + i]));
            }

            oldAt = hunk.OldStart + hunk.OldCount;
            newAt = hunk.NewStart + hunk.NewCount;
        }

        // A few lines after the last change.
        var tail = Math.Min(Context, text.Count - newAt);
        for (var i = 0; i < tail; i++)
        {
            rows.Add(new DiffRow(" ", N(oldAt + i), N(newAt + i), text[newAt + i]));
        }

        if (text.Count - newAt > tail)
        {
            rows.Add(new DiffRow("...", "", "", $"{text.Count - newAt - tail} unchanged lines"));
        }

        if (hunks.Count == 0)
        {
            rows.Clear();
            rows.Add(new DiffRow("...", "", "", "No changes since the last commit"));
        }

        return rows;
    }
}

/// <summary>
/// A row of tabs over one file, as in Visual Studio: pinned tabs first, then the rest in the order opened, and at the
/// right end of the row the preview tab (a single click in the tree), which the next preview replaces. The Files view
/// has one group, or two side by side.
/// </summary>
public sealed partial class EditorGroup : ObservableObject, Nomad.App.Ide.ICodeNavigator
{
    private readonly FilesViewModel owner;

    public EditorGroup(FilesViewModel owner)
    {
        this.owner = owner;
        Find = new FindViewModel(this);
        Tabs.CollectionChanged += (_, _) =>
        {
            for (var i = 0; i < Tabs.Count; i++)
            {
                Tabs[i].IsFirst = i == 0;
            }

            OnPropertyChanged(nameof(IsFirstActive));
        };
    }

    /// <summary>Find and replace in the file shown (Ctrl+F, Ctrl+H).</summary>
    public FindViewModel Find { get; }

    // ------------------------------------------------------------------ navigation (ICodeNavigator)

    // The C# language server for the file shown, once it has loaded the solution; until then (and for other files)
    // the code search's guess stands in.
    private Nomad.App.Ide.CSharpLanguage? Server(FileTab tab) =>
        Nomad.App.Ide.CSharpLanguage.Handles(tab.Path) && owner.Language is { IsReady: true } language ? language : null;

    private List<Nomad.Core.Code.CodeSymbol> Guess(FileTab tab, Nomad.App.Ide.CodeSpot spot) =>
        owner.CodeSearch.Resolve(new Nomad.Core.Code.CodeReference(spot.Name, spot.Receiver, tab.Path, spot.Line), tab.Document.Lines);

    public async Task<bool> LeadsSomewhereAsync(Nomad.App.Ide.CodeSpot spot, CancellationToken cancellationToken)
    {
        if (ActiveTab is not { } tab)
        {
            return false;
        }

        if (Server(tab) is { } server && await server.DefinitionAsync(tab.Path, spot.Line, spot.Column, cancellationToken) is { } found)
        {
            return found.Count > 0;
        }

        return Guess(tab, spot).Count > 0;
    }

    public async Task<Nomad.App.Ide.QuickInfoView?> QuickInfoAsync(Nomad.App.Ide.CodeSpot spot, CancellationToken cancellationToken)
    {
        if (ActiveTab is not { } tab)
        {
            return null;
        }

        if (Server(tab) is { } server)
        {
            var hover = await server.HoverAsync(tab.Path, spot.Line, spot.Column, cancellationToken);
            return Nomad.Core.Lsp.HoverText.Parse(hover) is { } parsed
                ? new Nomad.App.Ide.QuickInfoView(parsed.Info.Signature, parsed.Info.Summary, parsed.Kind, tab.Path)
                : null;
        }

        var symbols = Guess(tab, spot);
        if (symbols.Count == 0)
        {
            return null;
        }

        var info = owner.CodeSearch.Describe(symbols[0], tab.Path, tab.Document.Lines);
        return new Nomad.App.Ide.QuickInfoView(info.Signature, info.Summary, symbols[0].Kind, symbols[0].Path, symbols.Count - 1);
    }

    /// <summary>F12 or Ctrl+click on a name in the code: off to its declaration (a library's opens decompiled).</summary>
    public void GoToDefinition(Nomad.App.Ide.CodeSpot spot) => _ = GoToDefinitionAsync(spot);

    private async Task GoToDefinitionAsync(Nomad.App.Ide.CodeSpot spot)
    {
        TakeFocus();
        if (ActiveTab is not { } tab)
        {
            return;
        }

        if (Server(tab) is { } server && await server.DefinitionAsync(tab.Path, spot.Line, spot.Column) is { } found)
        {
            // Several for a partial type: its first part, as Visual Studio's F12 does.
            if (found.Count > 0)
            {
                Open(found[0]);
            }

            return;
        }

        await owner.CodeSearch.GoToDefinitionAsync(new Nomad.Core.Code.CodeReference(spot.Name, spot.Receiver, tab.Path, spot.Line), tab.Document.Lines);
    }

    /// <summary>Ctrl+F12: what implements the interface or member (or overrides it); several are listed to pick from.</summary>
    public void GoToImplementation(Nomad.App.Ide.CodeSpot spot) => _ = GoToImplementationAsync(spot);

    private async Task GoToImplementationAsync(Nomad.App.Ide.CodeSpot spot)
    {
        TakeFocus();
        if (ActiveTab is not { } tab)
        {
            return;
        }

        if (Server(tab) is not { } server)
        {
            // Without the server: whatever goes by the name.
            owner.CodeSearch.Open(spot.Name);
            return;
        }

        if (await server.ImplementationAsync(tab.Path, spot.Line, spot.Column) is not { Count: > 0 } found)
        {
            return;
        }

        if (found.Count == 1)
        {
            Open(found[0]);
        }
        else
        {
            owner.FindInFiles.ShowLocations($"Implementations of {spot.Name}", found);
        }
    }

    /// <summary>Shift+F12: every place the name is used, listed (without the server: the files' text, by whole word).</summary>
    public void FindReferences(Nomad.App.Ide.CodeSpot spot) => _ = FindReferencesAsync(spot);

    private async Task FindReferencesAsync(Nomad.App.Ide.CodeSpot spot)
    {
        if (ActiveTab is not { } tab)
        {
            return;
        }

        if (Server(tab) is not { } server)
        {
            owner.FindInFiles.WholeWord = true;
            owner.FindInFiles.MatchCase = true;
            owner.FindInFiles.UseRegex = false;
            owner.FindInFiles.Open(spot.Name);
            return;
        }

        if (await server.ReferencesAsync(tab.Path, spot.Line, spot.Column) is { Count: > 0 } found)
        {
            owner.FindInFiles.ShowLocations($"References to {spot.Name}", found);
        }
    }

    /// <summary>F2 on a name: asks for its new one, then renames it everywhere (open files as an undo step, the rest on disk).</summary>
    public void Rename(Nomad.App.Ide.CodeSpot spot) => _ = RenameAsync(spot);

    private async Task RenameAsync(Nomad.App.Ide.CodeSpot spot)
    {
        if (ActiveTab is not { } tab || Server(tab) is not { } server)
        {
            return;
        }

        if (await server.PrepareRenameAsync(tab.Path, spot.Line, spot.Column) is not { } range || range.EndLine != range.Line)
        {
            owner.Prompt.AskConfirm($"Can't rename {spot.Name}", "Only names declared in this solution can be renamed.", "OK", danger: false, () => Task.FromResult<string?>(null));
            return;
        }

        var lines = tab.Document.Lines;
        var name = range.Line < lines.Count && range.EndColumn <= lines[range.Line].Length ? lines[range.Line][range.Column..range.EndColumn] : spot.Name;
        owner.Prompt.AskText($"Rename {name}", name, "Rename", async newName =>
        {
            if (newName == name)
            {
                return null;
            }

            if (await server.RenameAsync(tab.Path, range.Line, range.Column, newName) is not { Count: > 0 } edits)
            {
                return $"Couldn't rename it to {newName}.";
            }

            var error = owner.ApplyEdits(edits);
            tab.Document.GoTo(tab.Document.Caret);
            return error;
        });
    }

    public async Task<Nomad.Core.Lsp.LspCompletionList?> CompleteAsync(int line, int column, string? trigger, CancellationToken cancellationToken) =>
        ActiveTab is { } tab && Server(tab) is { } server ? await server.CompletionAsync(tab.Path, line, column, trigger, cancellationToken) : null;

    public async Task<Nomad.Core.Lsp.LspResolvedCompletion?> ResolveCompletionAsync(Nomad.Core.Lsp.LspCompletion item, CancellationToken cancellationToken) =>
        ActiveTab is { } tab && Server(tab) is { } server ? await server.ResolveAsync(item, cancellationToken) : null;

    public async Task<Nomad.Core.Lsp.LspSignatureHelp?> SignatureHelpAsync(int line, int column, string? trigger, bool retrigger, CancellationToken cancellationToken) =>
        ActiveTab is { } tab && Server(tab) is { } server ? await server.SignatureHelpAsync(tab.Path, line, column, trigger, retrigger, cancellationToken) : null;

    private void Open(Nomad.Core.Lsp.LspLocation location) =>
        owner.OpenAt(location.Path, (location.Line, location.Column),
            length: location.EndLine == location.Line ? Math.Max(0, location.EndColumn - location.Column) : 0);

    /// <summary>"Ask about this" in the file shown: its lines go to the chat's composer.</summary>
    public void AskAbout(int from, int to, string text)
    {
        if (ActiveTab is { } tab)
        {
            owner.AskAbout(tab.Path, from, to, text);
        }
    }

    /// <summary>The kept tabs, pinned ones first.</summary>
    public ObservableCollection<FileTab> Tabs { get; } = [];

    /// <summary>The preview tab, on its own at the right end of the row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    public partial FileTab? PreviewTab { get; private set; }

    public bool HasPreview => PreviewTab is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTabs))]
    [NotifyPropertyChangedFor(nameof(IsFirstActive))]
    public partial FileTab? ActiveTab { get; set; }

    public bool HasTabs => ActiveTab is not null;

    /// <summary>The first tab of the row is the one shown: it is joined to the card's top-left corner, which squares off.</summary>
    public bool IsFirstActive => ActiveTab is not null && Tabs.Count > 0 && Tabs[0] == ActiveTab;

    /// <summary>The group files from the tree open in (the last one clicked).</summary>
    [ObservableProperty]
    public partial bool IsFocused { get; set; }

    /// <summary>The second group can't open another to its side: there are at most two.</summary>
    public bool CanSplit => owner.Groups.Count < 2;

    public void OnSplitChanged() => OnPropertyChanged(nameof(CanSplit));

    /// <summary>Every tab, the preview too.</summary>
    public IEnumerable<FileTab> AllTabs => PreviewTab is { } preview ? Tabs.Append(preview) : Tabs;

    partial void OnActiveTabChanged(FileTab? oldValue, FileTab? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsActive = false;
            oldValue.IsFocused = false;
            oldValue.IsAskingToClose = false;
        }

        if (newValue is not null)
        {
            newValue.IsActive = true;
            newValue.IsFocused = IsFocused;
        }

        Find.OnActiveTabChanged();
    }

    partial void OnIsFocusedChanged(bool value)
    {
        if (ActiveTab is { } tab)
        {
            tab.IsFocused = value;
        }
    }

    /// <summary>
    /// Shows a file: its tab if it has one; else, to keep, a new tab at the end of the row, or as a preview, in the
    /// preview slot (replacing the one there).
    /// </summary>
    public void Open(string path, bool keep)
    {
        if (Tabs.FirstOrDefault(t => SamePath(t, path)) is { } open)
        {
            ActiveTab = open;
            return;
        }

        if (PreviewTab is { } preview && SamePath(preview, path))
        {
            if (keep)
            {
                Keep(preview);
            }

            ActiveTab = preview;
            return;
        }

        var tab = new FileTab(path, owner.Folder) { IsPreview = !keep, Group = this, Language = owner.Language };
        if (keep)
        {
            Tabs.Add(tab);
        }
        else
        {
            PreviewTab?.Dispose();
            PreviewTab = tab;
        }

        ActiveTab = tab;
    }

    private static bool SamePath(FileTab tab, string path) => string.Equals(tab.Path, path, StringComparison.OrdinalIgnoreCase);

    /// <summary>A click in the group: files from the tree open here now.</summary>
    public void TakeFocus() => owner.Focus(this);

    [RelayCommand]
    private void Activate(FileTab tab)
    {
        ActiveTab = tab;
        owner.Focus(this);
    }

    /// <summary>Keeps the preview tab (a double-click on it, pinning or editing it): it joins the row's end.</summary>
    [RelayCommand]
    private void Keep(FileTab tab)
    {
        if (tab != PreviewTab)
        {
            return;
        }

        var wasActive = ActiveTab == tab;
        PreviewTab = null;
        tab.IsPreview = false;
        Tabs.Add(tab);
        if (wasActive || ActiveTab is null)
        {
            ActiveTab = tab;
        }
    }

    /// <summary>Closes a tab; one with unsaved edits is shown and asks first.</summary>
    [RelayCommand]
    private void Close(FileTab tab)
    {
        if (tab.IsDirty)
        {
            ActiveTab = tab;
            tab.IsAskingToClose = true;
            return;
        }

        Remove(tab);
    }

    [RelayCommand]
    private async Task SaveAndClose(FileTab tab)
    {
        if (await tab.SaveAsync())
        {
            Remove(tab);
        }
    }

    [RelayCommand]
    private void CloseWithoutSaving(FileTab tab) => Remove(tab);

    /// <summary>Closes a tab without asking (its file was moved or deleted).</summary>
    public void Drop(FileTab tab) => Remove(tab);

    [RelayCommand]
    private void CancelClose(FileTab tab) => tab.IsAskingToClose = false;

    private void Remove(FileTab tab)
    {
        tab.IsAskingToClose = false;
        if (tab == PreviewTab)
        {
            PreviewTab = null;
            if (ActiveTab == tab)
            {
                ActiveTab = Tabs.LastOrDefault();
            }
        }
        else
        {
            var index = Tabs.IndexOf(tab);
            if (index < 0)
            {
                return;
            }

            Tabs.RemoveAt(index);
            if (ActiveTab == tab)
            {
                ActiveTab = Tabs.Count > 0 ? Tabs[Math.Min(index, Tabs.Count - 1)] : PreviewTab;
            }
        }

        tab.Dispose();
        if (Tabs.Count == 0 && PreviewTab is null)
        {
            owner.OnGroupEmptied(this);
        }
    }

    /// <summary>Closes the other tabs, except pinned ones and those with unsaved edits.</summary>
    [RelayCommand]
    private void CloseOthers(FileTab tab)
    {
        foreach (var other in Tabs.Where(t => t != tab && !t.IsPinned && !t.IsDirty).ToList())
        {
            Tabs.Remove(other);
            other.Dispose();
        }

        if (PreviewTab is { } preview && preview != tab && !preview.IsDirty)
        {
            PreviewTab = null;
            preview.Dispose();
        }

        ActiveTab = tab;
    }

    /// <summary>Pinned tabs stay at the front, in the order pinned, and "Close others" leaves them.</summary>
    [RelayCommand]
    private void TogglePin(FileTab tab)
    {
        Keep(tab);
        tab.IsPinned = !tab.IsPinned;
        var pinned = Tabs.Count(t => t.IsPinned && t != tab);
        var from = Tabs.IndexOf(tab);
        if (from >= 0 && from != pinned)
        {
            Tabs.Move(from, pinned);
        }
    }

    /// <summary>Moves the file to the group beside this one (made if needed); its tab here closes.</summary>
    [RelayCommand]
    private void OpenToSide(FileTab tab) => owner.OpenToSide(this, tab);

    /// <summary>
    /// Takes in a tab from the other group as it is (its edits, caret and undo with it), at the end of the row.
    /// </summary>
    public void Adopt(FileTab tab)
    {
        // The file is open here too: the copy with unsaved edits stays (the other one, if both have some).
        if (Tabs.FirstOrDefault(t => SamePath(t, tab.Path)) is { } open)
        {
            if (!tab.IsDirty || open.IsDirty)
            {
                ActiveTab = open;
                tab.Dispose();
                return;
            }

            Tabs.Remove(open);
            open.Dispose();
        }

        tab.Group = this;
        tab.IsPreview = false;
        tab.IsActive = false;
        tab.IsFocused = false;
        Tabs.Add(tab);
        ActiveTab = tab;
    }

    /// <summary>Lets a tab go to the other group without closing it (or asking about its edits).</summary>
    public void Release(FileTab tab)
    {
        if (tab == PreviewTab)
        {
            PreviewTab = null;
        }
        else
        {
            Tabs.Remove(tab);
        }

        if (ActiveTab == tab)
        {
            ActiveTab = Tabs.LastOrDefault() ?? PreviewTab;
        }

        if (Tabs.Count == 0 && PreviewTab is null)
        {
            owner.OnGroupEmptied(this);
        }
    }
}
