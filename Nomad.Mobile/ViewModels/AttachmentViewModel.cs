using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Nomad.Hub.Contracts;

namespace Nomad.Mobile.ViewModels;

/// <summary>A file picked on the phone: its name, size if known, and how to read it (as often as needed).</summary>
public sealed record PickedFile(string Name, long? Size, Func<Task<Stream>> OpenAsync);

/// <summary>
/// A file attached to the message box, as the desktop composer's tray shows it: an image as a thumbnail, any other file as a
/// chip. It goes onto the hub when the message is sent, once: a retry sends the copy already there.
/// </summary>
public sealed partial class AttachmentViewModel : ObservableObject, IDisposable
{
    /// <summary>Larger images show as a chip: a thumbnail would mean reading all of it into memory first.</summary>
    private const long MaxPreviewSource = 15L * 1024 * 1024;

    private bool _disposed;

    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

    public AttachmentViewModel(PickedFile file)
    {
        File = file;
    }

    public PickedFile File { get; }

    public string Name => File.Name;

    public bool IsImage => ImageTypes.Contains(Path.GetExtension(Name));

    /// <summary>The chip's second line: "PDF · 1.2 MB".</summary>
    public string Detail
    {
        get
        {
            var kind = Path.GetExtension(Name).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? ext : "File";
            return File.Size is { } size ? $"{kind} · {SizeText(size)}" : kind;
        }
    }

    /// <summary>The image's thumbnail, once read; null for a file, or an image that couldn't be read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview), nameof(ShowsChip))]
    public partial Bitmap? Preview { get; set; }

    public bool HasPreview => Preview is not null;

    public bool ShowsChip => Preview is null;

    /// <summary>The copy on the hub, once it is there.</summary>
    public ChatFile? Uploaded { get; set; }

    /// <summary>Reads the thumbnail of an image; anything that isn't one stays a chip.</summary>
    public async Task LoadPreviewAsync()
    {
        if (!IsImage || File.Size is null or > MaxPreviewSource)
        {
            return;
        }

        try
        {
            await using var stream = await File.OpenAsync();
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            copy.Position = 0;
            var preview = await Task.Run(() => Bitmap.DecodeToHeight(copy, 192));
            AppServices.Post(() =>
            {
                if (_disposed)
                {
                    preview.Dispose(); // Removed meanwhile.
                }
                else
                {
                    Preview = preview;
                }
            });
        }
        catch (Exception)
        {
            // Not an image the phone can read: shown as a file.
        }
    }

    /// <summary>Removed or sent: its thumbnail is freed.</summary>
    public void Dispose()
    {
        _disposed = true;
        var preview = Preview;
        Preview = null;
        preview?.Dispose();
    }

    internal static string SizeText(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes / (1024.0 * 1024):0.0} MB",
    };
}
