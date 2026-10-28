using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Nomad.App.Ide;

namespace Nomad.App.ViewModels;

/// <summary>
/// A session's Files view: the tree of its folder and the file picked in it. The tree is made the first time the
/// view is shown, and watches the folder only while it is on screen.
/// </summary>
public sealed partial class FilesViewModel : ObservableObject
{
    /// <summary>Larger files show only their first part.</summary>
    private const int PreviewBytes = 256 * 1024;

    private string? _folder;
    private bool _shown;
    private int _generation;

    [ObservableProperty]
    public partial FileTree? Tree { get; private set; }

    /// <summary>The file shown on the file side (a full path); set by a click in the tree.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenFile))]
    [NotifyPropertyChangedFor(nameof(OpenFileName))]
    [NotifyPropertyChangedFor(nameof(OpenFileFolder))]
    public partial string? OpenFile { get; set; }

    public bool HasOpenFile => OpenFile is not null;

    public string OpenFileName => Path.GetFileName(OpenFile) ?? "";

    /// <summary>The open file's folder, relative to the session's.</summary>
    public string OpenFileFolder =>
        OpenFile is null || _folder is null ? "" : Path.GetRelativePath(_folder, Path.GetDirectoryName(OpenFile) ?? _folder) is var p && p != "." ? p.Replace(Path.DirectorySeparatorChar, '/') : "";

    /// <summary>The open file's text (for now plain, until the code view comes).</summary>
    [ObservableProperty]
    public partial string PreviewText { get; private set; } = "";

    /// <summary>Instead of the text: why there is none ("Binary file") or that it is cut short.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewNote))]
    public partial string? PreviewNote { get; private set; }

    public bool HasPreviewNote => PreviewNote is not null;

    /// <summary>The session's folder changed (a new session pointed elsewhere): start over there.</summary>
    public void SetFolder(string? folder)
    {
        if (string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _folder = folder;
        Tree?.Dispose();
        Tree = null;
        OpenFile = null;
        if (_shown)
        {
            Show(true);
        }
    }

    /// <summary>The view came on screen or left it.</summary>
    public void Show(bool shown)
    {
        _shown = shown;
        if (shown && Tree is null && _folder is not null && Directory.Exists(_folder))
        {
            Tree = new FileTree(_folder);
        }

        Tree?.Watch(shown);
    }

    /// <summary>Opens a file in the folder, expanding the tree down to it.</summary>
    public async Task RevealAsync(string path)
    {
        if (Tree is { } tree && await tree.RevealAsync(path) && File.Exists(path))
        {
            OpenFile = path;
        }
    }

    partial void OnOpenFileChanged(string? value) => _ = LoadPreviewAsync(value);

    private async Task LoadPreviewAsync(string? path)
    {
        var generation = ++_generation;
        if (path is null)
        {
            PreviewText = "";
            PreviewNote = null;
            return;
        }

        var (text, note) = await Task.Run(() => Read(path));
        if (generation == _generation)
        {
            PreviewText = text;
            PreviewNote = note;
        }
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
