using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nomad.App.Ide;

namespace Nomad.App.ViewModels;

/// <summary>
/// A session's Files view: the tree of its folder and the files open in tabs, in one group or two side by side. The
/// tree is made the first time the view is shown, and watches the folder only while it is on screen.
/// </summary>
public sealed partial class FilesViewModel : ObservableObject
{
    private bool _shown;

    public FilesViewModel()
    {
        CodeSearch = new CodeSearchViewModel(this);
        var first = new EditorGroup(this) { IsFocused = true };
        Groups.Add(first);
        ActiveGroup = first;
    }

    [ObservableProperty]
    public partial FileTree? Tree { get; private set; }

    /// <summary>Ctrl+T in the Files view: types, members and files by name.</summary>
    public CodeSearchViewModel CodeSearch { get; }

    /// <summary>The session's folder: tabs show their file's folder relative to it.</summary>
    public string? Folder { get; private set; }

    public ObservableCollection<EditorGroup> Groups { get; } = [];

    /// <summary>The open files with unsaved edits.</summary>
    public IEnumerable<FileTab> EditedFiles => Groups.SelectMany(g => g.AllTabs).Where(t => t.IsDirty);

    /// <summary>The group files from the tree open in.</summary>
    public EditorGroup ActiveGroup { get; private set; }

    /// <summary>The session's folder changed: start over there.</summary>
    public void SetFolder(string? folder)
    {
        if (string.Equals(folder, Folder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Folder = folder;
        CodeSearch.Close();
        Tree?.Dispose();
        Tree = null;

        // Tabs with unsaved edits stay (their files are where they were); the rest go.
        var edited = Groups.SelectMany(g => g.AllTabs).Where(t => t.IsDirty).ToList();
        foreach (var tab in Groups.SelectMany(g => g.AllTabs).Except(edited).ToList())
        {
            tab.Dispose();
        }

        while (Groups.Count > 1)
        {
            Groups.RemoveAt(Groups.Count - 1);
        }

        Groups[0] = new EditorGroup(this);
        foreach (var tab in edited)
        {
            Groups[0].Adopt(tab);
        }

        ActiveGroup = Groups[0];
        ActiveGroup.IsFocused = true;
        NotifySplit();
        if (_shown)
        {
            Show(true);
        }
    }

    // Changes that land just after a turn ends (the last write settling) are still the agent's.
    private static readonly TimeSpan AgentGrace = TimeSpan.FromSeconds(3);
    private DateTime _agentStoppedUtc;

    /// <summary>A turn is running: changes on disk are the agent's (the session keeps it current).</summary>
    public bool IsAgentWorking
    {
        get;
        set
        {
            if (field && !value)
            {
                _agentStoppedUtc = DateTime.UtcNow;
            }

            field = value;
        }
    }

    /// <summary>The view came on screen or left it.</summary>
    public void Show(bool shown)
    {
        _shown = shown;
        if (shown && Tree is null && Folder is not null && Directory.Exists(Folder))
        {
            Tree = new FileTree(Folder) { IsAgentWorking = () => IsAgentWorking || DateTime.UtcNow - _agentStoppedUtc < AgentGrace };
        }

        Tree?.Watch(shown);
    }

    /// <summary>Opens a file from the tree in the focused group (as a preview unless it is to be kept).</summary>
    public void Open(string path, bool keep) => ActiveGroup.Open(path, keep);

    /// <summary>A place in a file: where a jump came from, for Back and Forward.</summary>
    public readonly record struct Place(string Path, int Line, int Column);

    private const int MaxHistory = 100;
    private readonly List<Place> _back = [];
    private readonly List<Place> _forward = [];

    private Place? Here() =>
        ActiveGroup.ActiveTab is { } tab ? new Place(tab.Path, tab.Document.Caret.Line, tab.Document.Caret.Index) : null;

    // A jump leaves where the caret was on the Back list (and a new jump forgets what Forward had).
    private void Remember()
    {
        if (Here() is { } here && (_back.Count == 0 || _back[^1] != here))
        {
            _back.Add(here);
            if (_back.Count > MaxHistory)
            {
                _back.RemoveAt(0);
            }
        }

        _forward.Clear();
    }

    /// <summary>Ctrl+- (or the mouse's back button): back to where the last jump came from.</summary>
    public void NavigateBack() => Navigate(_back, _forward);

    /// <summary>Ctrl+Shift+- (or the mouse's forward button): on to where Back came from.</summary>
    public void NavigateForward() => Navigate(_forward, _back);

    private void Navigate(List<Place> from, List<Place> to)
    {
        if (from.Count == 0)
        {
            return;
        }

        var place = from[^1];
        from.RemoveAt(from.Count - 1);
        if (Here() is { } here)
        {
            to.Add(here);
        }

        OpenAt(place.Path, (place.Line, place.Column), remember: false);
    }

    /// <summary>Ctrl+G: the caret to a line (1-based) of the file shown.</summary>
    public void GoToLine(int line)
    {
        if (ActiveGroup.ActiveTab is { } tab)
        {
            Remember();
            tab.GoTo(Math.Max(0, line - 1), 0);
        }
    }

    /// <summary>Opens a file (kept, in the focused group) with the caret at a line and column (0-based), as a Go To does.</summary>
    public void OpenAt(string path, (int Line, int Column)? at, bool remember = true)
    {
        if (remember)
        {
            Remember();
        }

        ActiveGroup.Open(path, keep: true);
        if (at is { } place && ActiveGroup.ActiveTab is { } tab && string.Equals(tab.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            tab.GoTo(place.Line, place.Column);
        }

        _ = Tree?.RevealAsync(path);
    }

    /// <summary>Opens a file with its diff against the last commit (the tree's red check clicked).</summary>
    public void OpenDiff(string path)
    {
        OpenAt(path, null);
        if (ActiveGroup.ActiveTab is { } tab && string.Equals(tab.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            tab.RequestDiff();
        }
    }

    /// <summary>The toolbar's Sync with Active Document: the tree opens down to the file shown and selects it.</summary>
    [RelayCommand]
    private void SyncWithActive()
    {
        if (ActiveGroup.ActiveTab is { } tab)
        {
            _ = Tree?.RevealAsync(tab.Path);
        }
    }

    /// <summary>The toolbar's Collapse All: folds every folder in the tree.</summary>
    [RelayCommand]
    private void CollapseAll() => Tree?.CollapseAll();

    /// <summary>The toolbar's "Open in Visual Studio" is there (Visual Studio is installed).</summary>
    public bool CanOpenInVisualStudio => VisualStudio.IsInstalled;

    /// <summary>Opens the folder's solution (or the folder) in Visual Studio.</summary>
    [RelayCommand]
    private async Task OpenInVisualStudio()
    {
        if (Folder is not null)
        {
            await VisualStudio.OpenAsync(Folder);
        }
    }

    /// <summary>Opens a file in the folder, expanding the tree down to it.</summary>
    public async Task RevealAsync(string path, bool keep = true)
    {
        if (Tree is { } tree && await tree.RevealAsync(path) && File.Exists(path))
        {
            Open(path, keep);
        }
    }

    public void Focus(EditorGroup group)
    {
        if (ActiveGroup == group)
        {
            return;
        }

        ActiveGroup.IsFocused = false;
        ActiveGroup = group;
        group.IsFocused = true;
    }

    /// <summary>Moves a file to the group beside this one (made if there is only one), which takes the focus.</summary>
    public void OpenToSide(EditorGroup from, FileTab tab)
    {
        var other = Groups.FirstOrDefault(g => g != from);
        if (other is null)
        {
            other = new EditorGroup(this);
            Groups.Add(other);
            NotifySplit();
        }

        // Moved, not copied, with its edits: its tab here goes (and this group with it, if that was its last).
        from.Release(tab);
        other.Adopt(tab);
        Focus(other);
    }

    /// <summary>A group's last tab closed: the second group goes away (the first stays, empty).</summary>
    public void OnGroupEmptied(EditorGroup group)
    {
        if (Groups.Count < 2)
        {
            return;
        }

        Groups.Remove(group);
        Focus(Groups[0]);
        NotifySplit();
    }

    private void NotifySplit()
    {
        foreach (var group in Groups)
        {
            group.OnSplitChanged();
        }
    }
}
