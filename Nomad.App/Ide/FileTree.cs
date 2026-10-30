using Avalonia.Threading;
using Nomad.Core.Files;
using Nomad.Core.Git;

namespace Nomad.App.Ide;

/// <summary>A file or folder in a <see cref="FileTree"/>.</summary>
public sealed class FileNode(string name, string fullPath, bool isDirectory, FileNode? parent)
{
    public string Name { get; } = name;
    public string FullPath { get; } = fullPath;
    public bool IsDirectory { get; } = isDirectory;
    public FileNode? Parent { get; } = parent;

    /// <summary>The root's own entries are at 0 (the root itself, never shown, at -1).</summary>
    public int Depth { get; } = parent is null ? -1 : parent.Depth + 1;

    /// <summary>A folder's entries, in display order; null until it is first listed.</summary>
    public List<FileNode>? Children { get; internal set; }

    public bool IsExpanded { get; set; }

    public bool IsAncestorOf(FileNode node)
    {
        for (var p = node.Parent; p is not null; p = p.Parent)
        {
            if (p == this)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// A session folder's files, listed a folder at a time as they are expanded, and kept current while
/// <see cref="Watch"/> is on: changes under the folder re-list the folders they touched. Used on the UI thread.
/// </summary>
public sealed class FileTree : IDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(200);

    // A commit or a checkout elsewhere (a worktree's index is outside its folder) is caught up this often.
    private static readonly TimeSpan GitPoll = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, FileNode> _listed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<FileNode, Task> _loading = [];
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _settle;
    private bool _dirtyAll;
    private bool _gitDirty;
    private bool _gitReading;
    private DispatcherTimer? _gitPoll;

    public FileTree(string folder)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        Root = new FileNode(Path.GetFileName(path), path, isDirectory: true, parent: null) { IsExpanded = true };
    }

    public FileNode Root { get; }

    /// <summary>The highlighted row, kept here so it survives the view being rebuilt.</summary>
    public FileNode? Selected { get; set; }

    /// <summary>The view's scroll position, likewise.</summary>
    public double ScrollOffset { get; set; }

    /// <summary>Entries were listed, or came or went: the visible rows may differ.</summary>
    public event Action? Changed;

    /// <summary>The files' git state (null outside a repository), kept current while <see cref="Watch"/> is on.</summary>
    public GitFiles? Git { get; private set; }

    /// <summary><see cref="Git"/> was read again.</summary>
    public event Action? GitChanged;

    /// <summary>Lists a folder that has not been listed yet (or waits for the listing already under way).</summary>
    public Task LoadAsync(FileNode folder)
    {
        if (!folder.IsDirectory || folder.Children is not null)
        {
            return Task.CompletedTask;
        }

        if (!_loading.TryGetValue(folder, out var loading))
        {
            _loading[folder] = loading = List(folder);
        }

        return loading;
    }

    private async Task List(FileNode folder)
    {
        try
        {
            var entries = await Task.Run(() => DirectoryListing.List(folder.FullPath));
            Apply(folder, entries);
        }
        finally
        {
            _loading.Remove(folder);
        }

        Changed?.Invoke();
    }

    /// <summary>Expands the folders down to a file or folder under the root and selects it; false if it isn't there.</summary>
    public async Task<bool> RevealAsync(string path)
    {
        var relative = Path.GetRelativePath(Root.FullPath, Path.GetFullPath(path));
        if (relative == "." || Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
        {
            return false;
        }

        var node = Root;
        foreach (var name in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            await LoadAsync(node);
            if (node.Children?.Find(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is not { } child)
            {
                return false;
            }

            node.IsExpanded = true;
            node = child;
        }

        Selected = node;
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Every file is shown; off, those Visual Studio leaves out of a project (build output, dot folders) are not
    /// (<see cref="DirectoryListing.IsExcluded"/>).
    /// </summary>
    public bool ShowAll
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>The rows to show: every entry whose folders are all expanded, in order.</summary>
    public List<FileNode> Visible()
    {
        var rows = new List<FileNode>();
        Add(Root);
        return rows;

        void Add(FileNode folder)
        {
            foreach (var child in folder.Children ?? [])
            {
                if (!ShowAll && DirectoryListing.IsExcluded(child.Name, child.IsDirectory))
                {
                    continue;
                }

                rows.Add(child);
                if (child.IsExpanded)
                {
                    Add(child);
                }
            }
        }
    }

    /// <summary>
    /// On: changes on disk keep the tree current (and whatever changed while it was off is caught up now). Off while
    /// the tree is not shown.
    /// </summary>
    public void Watch(bool on)
    {
        if (on == _watcher is not null)
        {
            return;
        }

        if (!on)
        {
            _watcher!.Dispose();
            _watcher = null;
            _gitPoll?.Stop();
            return;
        }

        if (_gitPoll is null)
        {
            _gitPoll = new DispatcherTimer { Interval = GitPoll };
            _gitPoll.Tick += (_, _) => _ = RefreshGitAsync();
        }

        _gitPoll.Start();

        try
        {
            _watcher = new FileSystemWatcher(Root.FullPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024,
            };
            _watcher.Created += (_, e) => Touched(e.FullPath);
            _watcher.Deleted += (_, e) => Touched(e.FullPath);
            _watcher.Renamed += (_, e) =>
            {
                Touched(e.OldFullPath);
                Touched(e.FullPath);
            };
            _watcher.Changed += (_, _) => GitTouched();
            _watcher.Error += (_, _) => TouchedAll();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is ArgumentException or IOException or PlatformNotSupportedException)
        {
            _watcher?.Dispose();
            _watcher = null;
        }

        TouchedAll();
    }

    public void Dispose()
    {
        Watch(false);
        _settle?.Stop();
        _gitPoll = null;
    }

    // Watcher threads: note the folder that changed, and re-list after the changes settle.
    private void Touched(string path)
    {
        GitTouched();
        if (Path.GetDirectoryName(path) is not { } folder || IsUnderGit(path))
        {
            return;
        }

        lock (_dirty)
        {
            _dirty.Add(folder);
        }

        Dispatcher.UIThread.Post(Schedule);
    }

    // A file was written, or anything came or went (in .git too: a commit, a checkout): git's view may differ.
    private void GitTouched()
    {
        lock (_dirty)
        {
            _gitDirty = true;
        }

        Dispatcher.UIThread.Post(Schedule);
    }

    private void TouchedAll()
    {
        lock (_dirty)
        {
            _dirtyAll = true;
            _gitDirty = true;
        }

        Dispatcher.UIThread.Post(Schedule);
    }

    private bool IsUnderGit(string path)
    {
        var relative = Path.GetRelativePath(Root.FullPath, path);
        var first = relative.Split(Path.DirectorySeparatorChar, 2)[0];
        return DirectoryListing.IsHidden(first);
    }

    private void Schedule()
    {
        if (_settle is null)
        {
            _settle = new DispatcherTimer { Interval = Settle };
            _settle.Tick += (_, _) =>
            {
                _settle.Stop();
                _ = RefreshAsync();
            };
        }

        _settle.Stop();
        _settle.Start();
    }

    private async Task RefreshAsync()
    {
        List<FileNode> folders;
        lock (_dirty)
        {
            if (_gitDirty)
            {
                _gitDirty = false;
                _ = RefreshGitAsync();
            }

            folders = _dirtyAll
                ? [.. _listed.Values]
                : [.. _dirty.Select(path => _listed.GetValueOrDefault(path)).OfType<FileNode>()];
            _dirty.Clear();
            _dirtyAll = false;
        }

        if (folders.Count == 0)
        {
            return;
        }

        var paths = folders.Select(f => f.FullPath).ToArray();
        var listings = await Task.Run(() => paths.Select(DirectoryListing.List).ToArray());
        for (var i = 0; i < folders.Count; i++)
        {
            // Gone from the tree meanwhile (its parent re-listed without it): nothing to update.
            if (_listed.GetValueOrDefault(folders[i].FullPath) == folders[i])
            {
                Apply(folders[i], listings[i]);
            }
        }

        Changed?.Invoke();
    }

    private async Task RefreshGitAsync()
    {
        // One read at a time; a change meanwhile reads again after it.
        if (_gitReading)
        {
            lock (_dirty)
            {
                _gitDirty = true;
            }

            return;
        }

        _gitReading = true;
        try
        {
            Git = await GitFiles.ReadAsync(Root.FullPath);
        }
        finally
        {
            _gitReading = false;
        }

        GitChanged?.Invoke();
        lock (_dirty)
        {
            if (_gitDirty && _watcher is not null)
            {
                Schedule();
            }
        }
    }

    // Keeps the nodes still there (with their own children and expansion), adds the new ones, forgets the rest.
    private void Apply(FileNode folder, List<DirectoryEntry> entries)
    {
        var old = new Dictionary<string, FileNode>(StringComparer.Ordinal);
        foreach (var child in folder.Children ?? [])
        {
            old[child.Name] = child;
        }

        var children = new List<FileNode>(entries.Count);
        foreach (var entry in entries)
        {
            if (old.Remove(entry.Name, out var kept))
            {
                if (kept.IsDirectory == entry.IsDirectory)
                {
                    children.Add(kept);
                    continue;
                }

                Forget(kept);
            }

            children.Add(new FileNode(entry.Name, entry.FullPath, entry.IsDirectory, folder));
        }

        foreach (var gone in old.Values)
        {
            Forget(gone);
        }

        folder.Children = children;
        _listed[folder.FullPath] = folder;
    }

    private void Forget(FileNode node)
    {
        if (Selected is { } selected && (selected == node || node.IsAncestorOf(selected)))
        {
            Selected = null;
        }

        if (node.IsDirectory && _listed.GetValueOrDefault(node.FullPath) == node)
        {
            _listed.Remove(node.FullPath);
        }

        foreach (var child in node.Children ?? [])
        {
            Forget(child);
        }
    }
}
