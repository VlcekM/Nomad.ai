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

    partial void OnDocumentChanged(CodeDocument oldValue, CodeDocument newValue)
    {
        oldValue.DirtyChanged -= OnDirtyChanged;

        newValue.DirtyChanged += OnDirtyChanged;
        IsDirty = newValue.IsDirty;
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

        _knownWrite = written;
        Document = doc;
        Note = note;
        ChangedOnDisk = false;
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
        _watcher?.Dispose();
        _watcher = null;
        _settle?.Stop();
        Document.DirtyChanged -= OnDirtyChanged;
    }
}

/// <summary>
/// A row of tabs over one file, as in Visual Studio: pinned tabs first, then the rest in the order opened, and at the
/// right end of the row the preview tab (a single click in the tree), which the next preview replaces. The Files view
/// has one group, or two side by side.
/// </summary>
public sealed partial class EditorGroup : ObservableObject
{
    private readonly FilesViewModel owner;

    public EditorGroup(FilesViewModel owner)
    {
        this.owner = owner;
        Tabs.CollectionChanged += (_, _) =>
        {
            for (var i = 0; i < Tabs.Count; i++)
            {
                Tabs[i].IsFirst = i == 0;
            }

            OnPropertyChanged(nameof(IsFirstActive));
        };
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

        var tab = new FileTab(path, owner.Folder) { IsPreview = !keep, Group = this };
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
