using Nomad.App.Ide;
using Nomad.App.Infrastructure;
using Nomad.Core.Git;

namespace Nomad.App.ViewModels;

// What the tree's menu does with its files: make, rename, move and delete them, and stage, commit or undo their changes.
public sealed partial class FilesViewModel
{
    /// <summary>The card asking for a name, a commit message or a yes.</summary>
    public FilePromptViewModel Prompt { get; } = new();

    private static bool IsUnder(string file, string path) =>
        string.Equals(file, path, StringComparison.OrdinalIgnoreCase) ||
        file.StartsWith(Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private List<FileTab> TabsUnder(string path) => Groups.SelectMany(g => g.AllTabs).Where(t => IsUnder(t.Path, path)).ToList();

    // Files with unsaved edits stay where they are: moving or deleting them under the editor would lose them.
    private string? EditedUnder(string path) =>
        TabsUnder(path).FirstOrDefault(t => t.IsDirty) is { } tab ? $"{tab.Name} has unsaved edits: save or close it first." : null;

    // A name typed for a new or renamed item: no path tricks, nothing Windows can't name a file.
    private static string? BadName(string name, bool allowFolders)
    {
        var parts = name.Split('/', '\\');
        if (!allowFolders && parts.Length > 1)
        {
            return "A name can't have \\ or / in it.";
        }

        return parts.Any(p => p.Length == 0 || p is "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || p.EndsWith('.') || p.EndsWith(' '))
            ? "That isn't a name a file can have."
            : null;
    }

    private static string NameOf(string path) => Path.GetFileName(Path.TrimEndingDirectorySeparator(path));

    /// <summary>Asks for a name, then makes an empty file in the folder (and the folders a "a/b.cs" name needs) and opens it.</summary>
    public void NewFile(string folder) =>
        Prompt.AskText("New file", "", "Create", async name =>
        {
            if (BadName(name, allowFolders: true) is { } bad)
            {
                return bad;
            }

            var path = Path.GetFullPath(Path.Combine(folder, name));
            if (File.Exists(path) || Directory.Exists(path))
            {
                return $"{NameOf(path)} is already there.";
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            FileTree.NoteOwnWrite(path);
            await File.WriteAllTextAsync(path, "");
            OpenAt(path, null);
            return null;
        }, watermark: "Name, or folder/name");

    /// <summary>Asks for a name, then makes a folder (with any below it a "a/b" name needs).</summary>
    public void NewFolder(string folder) =>
        Prompt.AskText("New folder", "", "Create", name =>
        {
            if (BadName(name, allowFolders: true) is { } bad)
            {
                return Task.FromResult<string?>(bad);
            }

            var path = Path.GetFullPath(Path.Combine(folder, name));
            if (File.Exists(path) || Directory.Exists(path))
            {
                return Task.FromResult<string?>($"{NameOf(path)} is already there.");
            }

            FileTree.NoteOwnWrite(path);
            Directory.CreateDirectory(path);
            _ = Tree?.RevealAsync(path);
            return Task.FromResult<string?>(null);
        }, watermark: "Name, or folder/name");

    /// <summary>Asks for a new name (its name selected up to the extension), then renames the file or folder.</summary>
    public void Rename(string path)
    {
        var name = NameOf(path);
        var dot = Directory.Exists(path) ? -1 : name.LastIndexOf('.');
        Prompt.AskText("Rename", name, "Rename", newName =>
        {
            if (newName == name)
            {
                return Task.FromResult<string?>(null);
            }

            return Task.FromResult(BadName(newName, allowFolders: false) ?? Move(path, Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path))!, newName)));
        }, select: dot > 0 ? dot : name.Length);
    }

    /// <summary>
    /// Moves (or renames) a file or folder; tabs showing it, or files in it, follow it. Null when done, or why not.
    /// </summary>
    public string? Move(string from, string to)
    {
        from = Path.TrimEndingDirectorySeparator(Path.GetFullPath(from));
        to = Path.TrimEndingDirectorySeparator(Path.GetFullPath(to));
        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return null;
        }

        if (IsUnder(to, from) && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            return "A folder can't go into itself.";
        }

        // Only the case changes: Windows names it so in place.
        var sameName = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
        if (!sameName && (File.Exists(to) || Directory.Exists(to)))
        {
            return $"{NameOf(to)} is already there.";
        }

        if (EditedUnder(from) is { } edited)
        {
            return edited;
        }

        var tabs = TabsUnder(from).Select(t => (Tab: t, t.Group, t.IsPreview, Active: t.Group?.ActiveTab == t)).ToList();
        FileTree.NoteOwnWrite(from);
        FileTree.NoteOwnWrite(to);
        if (Directory.Exists(from))
        {
            Directory.Move(from, to);
        }
        else
        {
            File.Move(from, to);
        }

        // Each tab closes and opens again at the new place, in its own group.
        foreach (var (tab, group, preview, active) in tabs)
        {
            group?.Drop(tab);
        }

        foreach (var (tab, group, preview, active) in tabs.OrderBy(t => t.Active))
        {
            if (group is not null && Groups.Contains(group))
            {
                group.Open(to + tab.Path[from.Length..], keep: !preview);
            }
            else
            {
                ActiveGroup.Open(to + tab.Path[from.Length..], keep: !preview);
            }
        }

        _ = Tree?.RevealAsync(to);
        return null;
    }

    /// <summary>Asks, then sends a file or folder to the Recycle Bin; its tabs close.</summary>
    public void Delete(string path)
    {
        var folder = Directory.Exists(path);
        Prompt.AskConfirm($"Delete {NameOf(path)}?", folder ? "The folder and everything in it go to the Recycle Bin." : "It goes to the Recycle Bin.", "Delete", danger: true, () =>
        {
            if (EditedUnder(path) is { } edited)
            {
                return Task.FromResult<string?>(edited);
            }

            foreach (var tab in TabsUnder(path))
            {
                tab.Group?.Drop(tab);
            }

            FileTree.NoteOwnWrite(path);
            return Task.Run<string?>(() => RecycleBin.Delete([path]) ? null : "Couldn't delete it: it may be open in another program.");
        });
    }

    /// <summary>Stages a file or folder's changes.</summary>
    public Task StageAsync(string path) => GitAsync("Couldn't stage it", GitFileOps.StageAsync(path));

    /// <summary>Takes a file or folder's changes out of the index.</summary>
    public Task UnstageAsync(string path) => GitAsync("Couldn't unstage it", GitFileOps.UnstageAsync(path));

    /// <summary>Asks, then puts a file or folder back as it was last committed.</summary>
    public void Revert(string path) =>
        Prompt.AskConfirm($"Undo the changes to {NameOf(path)}?", "It goes back to how it was last committed. The changes are lost; new files stay.", "Undo changes", danger: true, async () =>
        {
            if (EditedUnder(path) is { } edited)
            {
                return edited;
            }

            var error = await GitFileOps.RevertAsync(path);
            Tree?.RefreshGitSoon();
            return error;
        });

    /// <summary>Asks for a message, then commits a file or folder's changes (only those).</summary>
    public void Commit(string path) =>
        Prompt.AskText($"Commit {NameOf(path)}", "", "Commit", async message =>
        {
            var error = await GitFileOps.CommitAsync(path, message);
            Tree?.RefreshGitSoon();
            return error;
        }, watermark: "Message", message: "Its changes are staged and committed; nothing else is.");

    private async Task GitAsync(string failed, Task<string?> run)
    {
        var error = await run;
        Tree?.RefreshGitSoon();
        if (error is not null)
        {
            Prompt.AskConfirm(failed, error, "OK", danger: false, () => Task.FromResult<string?>(null));
        }
    }
}
