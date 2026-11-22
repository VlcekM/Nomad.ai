using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Nomad.App.Controls;

/// <summary>
/// A small GitHub avatar that fades in once decoded.
/// </summary>
public sealed class AssigneeAvatar : Image
{
    /// <summary>
    /// The HTTPS URL provided by GitHub.
    /// </summary>
    public static readonly StyledProperty<string?> UrlProperty = AvaloniaProperty.Register<AssigneeAvatar, string?>(nameof(Url));

    /// <summary>
    /// The shared client, with a limit on response size and download time.
    /// </summary>
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        MaxResponseContentBufferSize = 1024 * 1024,
    };

    /// <summary>
    /// The fetch for the current URL while the avatar appears in the tree.
    /// </summary>
    private CancellationTokenSource? _loading;

    /// <summary>
    /// The HTTPS URL provided by GitHub.
    /// </summary>
    public string? Url { get => GetValue(UrlProperty); set => SetValue(UrlProperty, value); }

    /// <summary>
    /// Starts loading once the row is attached.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _ = LoadAsync();
    }

    /// <summary>
    /// Stops outstanding work and releases the bitmap when the row is removed.
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Clear();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// Switches to the new avatar when the binding changes.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == UrlProperty && VisualRoot is not null) _ = LoadAsync();
    }

    /// <summary>
    /// Downloads and decodes the avatar, retaining the placeholder on failure.
    /// </summary>
    private async Task LoadAsync()
    {
        Clear();
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;

        using var loading = new CancellationTokenSource();
        _loading = loading;
        var token = loading.Token;
        try
        {
            var bytes = await Http.GetByteArrayAsync(uri, token);
            var bitmap = await Task.Run(() => Decode(bytes), token);
            if (token.IsCancellationRequested)
            {
                bitmap.Dispose();
                return;
            }

            Source = bitmap;
            Opacity = 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or ArgumentException
            or InvalidOperationException)
        {
            // The assignee's initials remain visible.
        }
        finally
        {
            if (ReferenceEquals(_loading, loading)) _loading = null;
        }
    }

    /// <summary>
    /// Decodes the image at twice the display size or larger for high-density displays.
    /// </summary>
    private static Bitmap Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return Bitmap.DecodeToWidth(stream, 48);
    }

    /// <summary>
    /// Clears the previous source and cancels its pending fetch.
    /// </summary>
    private void Clear()
    {
        _loading?.Cancel();
        _loading = null;

        var bitmap = Source as Bitmap;
        Source = null;
        Opacity = 0;
        bitmap?.Dispose();
    }
}
