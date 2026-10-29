using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Nomad.App.ViewModels;

/// <summary>
/// An open file in the Files view, as a tab. A preview tab (a single click in the tree) is replaced by the next file
/// opened that way; double-clicking, pinning or opening it again keeps it.
/// </summary>
public sealed partial class FileTab : ObservableObject
{
    /// <summary>Larger files show only their first part.</summary>
    private const int PreviewBytes = 256 * 1024;

    public FileTab(string path, string? folder)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        var parent = System.IO.Path.GetDirectoryName(path) ?? "";
        var relative = folder is null ? parent : System.IO.Path.GetRelativePath(folder, parent);
        RelativeFolder = relative == "." ? "" : relative.Replace(System.IO.Path.DirectorySeparatorChar, '/');
        _ = LoadAsync();
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

    /// <summary>Active in the group that has the focus: its tab carries the accent line.</summary>
    [ObservableProperty]
    public partial bool IsFocused { get; set; }

    /// <summary>The file's text (for now plain, until the code view comes).</summary>
    [ObservableProperty]
    public partial string Text { get; private set; } = "";

    /// <summary>Instead of the text: why there is none ("Binary file"), or that it is cut short.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string? Note { get; private set; }

    public bool HasNote => Note is not null;

    /// <summary>Where the file was scrolled to, kept while another tab is shown.</summary>
    public Avalonia.Vector ScrollOffset { get; set; }

    private async Task LoadAsync()
    {
        var (text, note) = await Task.Run(() => Read(Path));
        Text = text;
        Note = note;
    }

    private static (string Text, string? Note) Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[(int)Math.Min(stream.Length, PreviewBytes)];
            stream.ReadExactly(buffer);
            var utf16 = buffer.Length >= 2 && (buffer[0], buffer[1]) is (0xFF, 0xFE) or (0xFE, 0xFF);
            if (!utf16 && Array.IndexOf(buffer, (byte)0, 0, Math.Min(buffer.Length, 8000)) >= 0)
            {
                return ("", "Binary file");
            }

            var text = new StreamReader(new MemoryStream(buffer), Encoding.UTF8, detectEncodingFromByteOrderMarks: true).ReadToEnd();
            return (text, stream.Length > PreviewBytes ? $"Showing the first {PreviewBytes / 1024} KB" : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ("", e.Message);
        }
    }
}

/// <summary>
/// A row of tabs over one file, as in Visual Studio: pinned tabs first, then the rest in the order opened. The Files
/// view has one group, or two side by side.
/// </summary>
public sealed partial class EditorGroup(FilesViewModel owner) : ObservableObject
{
    public ObservableCollection<FileTab> Tabs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTabs))]
    public partial FileTab? ActiveTab { get; set; }

    public bool HasTabs => ActiveTab is not null;

    /// <summary>The group files from the tree open in (the last one clicked).</summary>
    [ObservableProperty]
    public partial bool IsFocused { get; set; }

    /// <summary>The second group can't open another to its side: there are at most two.</summary>
    public bool CanSplit => owner.Groups.Count < 2;

    public void OnSplitChanged() => OnPropertyChanged(nameof(CanSplit));

    partial void OnActiveTabChanged(FileTab? oldValue, FileTab? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsActive = false;
            oldValue.IsFocused = false;
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

    /// <summary>Shows a file: its tab if it has one, else a new one (taking the preview tab's place for a preview).</summary>
    public void Open(string path, bool keep)
    {
        if (Tabs.FirstOrDefault(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase)) is { } open)
        {
            if (keep)
            {
                open.IsPreview = false;
            }

            ActiveTab = open;
            return;
        }

        var tab = new FileTab(path, owner.Folder) { IsPreview = !keep, Group = this };
        if (!keep && Tabs.FirstOrDefault(t => t.IsPreview) is { } preview)
        {
            Tabs[Tabs.IndexOf(preview)] = tab;
        }
        else
        {
            Tabs.Add(tab);
        }

        ActiveTab = tab;
    }

    /// <summary>A click in the group: files from the tree open here now.</summary>
    public void TakeFocus() => owner.Focus(this);

    [RelayCommand]
    private void Activate(FileTab tab)
    {
        ActiveTab = tab;
        owner.Focus(this);
    }

    /// <summary>Keeps a preview tab (a double-click on it).</summary>
    [RelayCommand]
    private void Keep(FileTab tab) => tab.IsPreview = false;

    [RelayCommand]
    private void Close(FileTab tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        Tabs.RemoveAt(index);
        if (ActiveTab == tab)
        {
            ActiveTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        }

        if (Tabs.Count == 0)
        {
            owner.OnGroupEmptied(this);
        }
    }

    [RelayCommand]
    private void CloseOthers(FileTab tab)
    {
        foreach (var other in Tabs.Where(t => t != tab && !t.IsPinned).ToList())
        {
            Tabs.Remove(other);
        }

        ActiveTab = tab;
    }

    /// <summary>Pinned tabs stay at the front, in the order pinned, and "Close others" leaves them.</summary>
    [RelayCommand]
    private void TogglePin(FileTab tab)
    {
        tab.IsPinned = !tab.IsPinned;
        tab.IsPreview = false;
        var pinned = Tabs.Count(t => t.IsPinned && t != tab);
        var from = Tabs.IndexOf(tab);
        var to = tab.IsPinned ? pinned : Math.Max(pinned, 0);
        if (from >= 0 && from != to)
        {
            Tabs.Move(from, to);
        }
    }

    [RelayCommand]
    private void OpenToSide(FileTab tab) => owner.OpenToSide(this, tab);
}
