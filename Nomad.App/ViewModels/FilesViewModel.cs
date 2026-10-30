using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
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
        var first = new EditorGroup(this) { IsFocused = true };
        Groups.Add(first);
        ActiveGroup = first;
    }

    [ObservableProperty]
    public partial FileTree? Tree { get; private set; }

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

    /// <summary>The view came on screen or left it.</summary>
    public void Show(bool shown)
    {
        _shown = shown;
        if (shown && Tree is null && Folder is not null && Directory.Exists(Folder))
        {
            Tree = new FileTree(Folder);
        }

        Tree?.Watch(shown);
    }

    /// <summary>Opens a file from the tree in the focused group (as a preview unless it is to be kept).</summary>
    public void Open(string path, bool keep) => ActiveGroup.Open(path, keep);

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
