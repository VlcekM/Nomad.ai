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

    /// <summary>
    /// The generation of the transcript whose page listed it (null until the chat says): the PC sends it only while the transcript
    /// is still that generation, so a reload or rewind never sends another file in its place.
    /// </summary>
    public int? Generation { get; internal set; }

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

    /// <summary>Its entry has gone: nothing more is shown in it.</summary>
    internal bool IsDisposed => _disposed;

    public void Dispose()
    {
        _disposed = true;
        var picture = Picture;
        Picture = null;
        picture?.Dispose();
    }
}

/// <summary>
/// How a file the agent sent is kept and opened on the phone. Nothing in one runs: only kinds known to be safe to open (pictures,
/// documents, media, archives, plain text) open as themselves; anything else, a web page, a script, a vector image or a name the app
/// doesn't know (or none), is kept as a text file (its name with ".txt" added, and the type text/plain, which is what a browser goes
/// by, whatever it is asked to open it as); and an app package is only saved, never handed to the installer.
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

    private static readonly HashSet<string> Packages = new(StringComparer.OrdinalIgnoreCase) { ".apk", ".apks", ".apkm", ".xapk", ".aab" };

    /// <summary>The kinds that open as themselves, with their types: none of them runs anything in the app that opens it.</summary>
    private static readonly Dictionary<string, string> Safe = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".heic"] = "image/heic",
        [".pdf"] = "application/pdf",
        [".txt"] = "text/plain",
        [".md"] = "text/plain",
        [".log"] = "text/plain",
        [".csv"] = "text/csv",
        [".json"] = "application/json",
        [".zip"] = "application/zip",
        [".7z"] = "application/x-7z-compressed",
        [".gz"] = "application/gzip",
        [".tar"] = "application/x-tar",
        [".mp3"] = "audio/mpeg",
        [".m4a"] = "audio/mp4",
        [".ogg"] = "audio/ogg",
        [".flac"] = "audio/flac",
        [".wav"] = "audio/wav",
        [".mp4"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".mkv"] = "video/x-matroska",
        [".webm"] = "video/webm",
        [".doc"] = "application/msword",
        [".xls"] = "application/vnd.ms-excel",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".odt"] = "application/vnd.oasis.opendocument.text",
        [".ods"] = "application/vnd.oasis.opendocument.spreadsheet",
    };

    public static Opening Of(string name) => Path.GetExtension(name) is var ext && Packages.Contains(ext) ? Opening.SavedOnly
        : Safe.ContainsKey(ext) ? Opening.AsItself
        : Opening.AsText;

    /// <summary>The name it is kept under on the phone: with ".txt" added for anything not known to be safe.</summary>
    public static string SavedName(string name) => Of(name) == Opening.AsText ? name + ".txt" : name;

    /// <summary>The type it is kept and opened as: its own for a safe kind, an app package's for one, else text.</summary>
    public static string OpenType(string name) => Of(name) switch
    {
        Opening.AsItself => Safe[Path.GetExtension(name)],
        Opening.SavedOnly => "application/vnd.android.package-archive",
        _ => "text/plain",
    };
}
