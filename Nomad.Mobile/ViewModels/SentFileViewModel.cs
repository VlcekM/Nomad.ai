using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nomad.Hub.Contracts;

namespace Nomad.Mobile.ViewModels;

/// <summary>
/// A file the agent sent the user, as the desktop shows it under the message: an image as a picture (fetched when the entry shows),
/// any other file as a chip. A tap saves it in the phone's Downloads and opens it, through the chat it is in.
/// </summary>
public sealed partial class SentFileViewModel(ChatSentFile file, int entryIndex, int number) : ObservableObject, IDisposable
{
    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

    private bool _disposed;

    public ChatSentFile File { get; } = file;

    /// <summary>The index of the entry it is in, which the PC finds it by with <see cref="Number"/>.</summary>
    public int EntryIndex { get; } = entryIndex;

    /// <summary>Its place among the entry's files.</summary>
    public int Number { get; } = number;

    public string Name => File.Name;

    /// <summary>An image the phone can show (PNG, JPEG, GIF, WebP or BMP).</summary>
    public bool IsImage => File.Image && ImageTypes.Contains(Path.GetExtension(Name));

    /// <summary>The picture, once fetched; null for a file, or an image that couldn't be fetched or read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPicture), nameof(ShowsChip))]
    public partial Bitmap? Picture { get; set; }

    public bool HasPicture => Picture is not null;

    public bool ShowsChip => Picture is null;

    /// <summary>The picture is being asked for (or was): asked once.</summary>
    internal bool PictureAsked { get; set; }

    /// <summary>Being fetched to open now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>Where it was saved on the phone, once it has been (opened from there again, not fetched again).</summary>
    internal string? Saved { get; set; }

    /// <summary>The chip's second line: "PDF", or what is happening to it.</summary>
    public string Detail => IsBusy ? "Downloading…" : Path.GetExtension(Name).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? ext : "File";

    /// <summary>Saves and opens it; set by the chat it is in.</summary>
    internal Func<SentFileViewModel, Task>? Opener { get; set; }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private Task OpenAsync() => Opener is { } open ? open(this) : Task.CompletedTask;

    private bool CanOpen => !IsBusy;

    /// <summary>Shows the picture, unless the entry went meanwhile.</summary>
    internal void Show(Bitmap picture)
    {
        if (_disposed)
        {
            picture.Dispose();
            return;
        }

        var old = Picture;
        Picture = picture;
        old?.Dispose();
    }

    public void Dispose()
    {
        _disposed = true;
        var picture = Picture;
        Picture = null;
        picture?.Dispose();
    }
}

/// <summary>
/// How a file the agent sent is kept and opened on the phone. Nothing in one runs: a web page, a script or a vector image (which can
/// carry one) is kept as a text file (its name with ".txt" added, and the type text/plain, which is what a browser goes by, whatever
/// it is asked to open it as), and an app package is only saved, never handed to the installer.
/// </summary>
public static class SentFileKinds
{
    public enum Opening
    {
        /// <summary>In whatever app opens its kind of file.</summary>
        AsItself,

        /// <summary>As plain text, so nothing in it runs.</summary>
        AsText,

        /// <summary>Saved only.</summary>
        SavedOnly,
    }

    private static readonly HashSet<string> Active = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".xhtml", ".shtml", ".mht", ".mhtml", ".svg", ".svgz", ".xml", ".xsl", ".xslt", ".js", ".mjs", ".cjs", ".hta",
        ".php", ".jsp", ".asp", ".aspx",
    };

    private static readonly HashSet<string> Packages = new(StringComparer.OrdinalIgnoreCase) { ".apk", ".apks", ".apkm", ".xapk", ".aab" };

    public static Opening Of(string name) => Path.GetExtension(name) is var ext && Packages.Contains(ext) ? Opening.SavedOnly
        : Active.Contains(ext) ? Opening.AsText
        : Opening.AsItself;

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".pdf"] = "application/pdf",
        [".txt"] = "text/plain",
        [".md"] = "text/plain",
        [".log"] = "text/plain",
        [".csv"] = "text/csv",
        [".json"] = "application/json",
        [".zip"] = "application/zip",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
    };

    /// <summary>The name it is kept under on the phone: with ".txt" added for anything that could run.</summary>
    public static string SavedName(string name) => Of(name) == Opening.AsText ? name + ".txt" : name;

    /// <summary>The type it is kept and opened as: text for anything that could run, else its own (any app's choice when unknown).</summary>
    public static string OpenType(string name) => Of(name) == Opening.AsText ? "text/plain"
        : Types.TryGetValue(Path.GetExtension(name), out var type) ? type : "application/octet-stream";
}
