using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Nomad.App.Controls;

/// <summary>A transcript image, loaded off the UI thread. Local files, data URLs and web images share the same viewer.</summary>
public sealed class ChatImage : Border
{
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<ChatImage, string?>(nameof(Source));

    public static readonly StyledProperty<string?> CaptionProperty =
        AvaloniaProperty.Register<ChatImage, string?>(nameof(Caption));

    public static readonly AttachedProperty<string?> BaseDirectoryProperty =
        AvaloniaProperty.RegisterAttached<ChatImage, Control, string?>("BaseDirectory", inherits: true);

    public static string? GetBaseDirectory(Control control) => control.GetValue(BaseDirectoryProperty);
    public static void SetBaseDirectory(Control control, string? value) => control.SetValue(BaseDirectoryProperty, value);

    public static readonly RoutedEvent<RoutedEventArgs> PreviewRequestedEvent =
        RoutedEvent.Register<ChatImage, RoutedEventArgs>("PreviewRequested", RoutingStrategies.Bubble);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private const int MaxBytes = 32 * 1024 * 1024;
    private readonly Image _image = new() { Stretch = Stretch.Uniform, MaxHeight = 400, IsVisible = false };
    private readonly TextBlock _status = new() { Text = "Loading image…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) };
    private CancellationTokenSource? _loading;
    private bool _attached;

    public ChatImage()
    {
        Classes.Add("tap"); // The transcript's text-selection helper leaves image clicks alone.
        MaxWidth = 560;
        HorizontalAlignment = HorizontalAlignment.Left;
        CornerRadius = new CornerRadius(8);
        ClipToBounds = true;
        BorderThickness = new Thickness(1);
        this.Bind(BorderBrushProperty, this.GetResourceObservable("BorderCardBrush"));
        _status.Classes.Add("muted");
        var panel = new StackPanel();
        panel.Children.Add(_status);
        panel.Children.Add(_image);
        Child = panel;
        Fold.SetIsOpen(_status, true);
        Tapped += (_, e) =>
        {
            if (Bitmap is not null)
            {
                RaiseEvent(new RoutedEventArgs(PreviewRequestedEvent));
                e.Handled = true;
            }
        };
    }

    public string? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public string? Caption { get => GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
    public Bitmap? Bitmap => _image.Source as Bitmap;

    protected override Size MeasureOverride(Size availableSize)
    {
        var measured = base.MeasureOverride(availableSize);
        return new Size(Math.Min(560, availableSize.Width), measured.Height);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if ((change.Property == SourceProperty || change.Property == BaseDirectoryProperty) && _attached)
        {
            _ = LoadAsync();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _ = LoadAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = null;
        // Keep the decoded image while the transcript container is measured or temporarily detached.
        base.OnDetachedFromVisualTree(e);
    }

    private string? _loadedSource;
    private string? _loadedDirectory;

    private async Task LoadAsync()
    {
        var source = Source;
        var directory = GetBaseDirectory(this);
        if (string.IsNullOrWhiteSpace(source) || (Bitmap is not null && _loadedSource == source && _loadedDirectory == directory))
        {
            return;
        }

        _loading?.Cancel();
        _loading?.Dispose();
        var loading = _loading = new CancellationTokenSource();
        var token = loading.Token;
        _status.Text = "Loading image…";
        Fold.SetIsOpen(_status, true);
        Fold.SetIsOpen(_image, false);
        try
        {
            var bitmap = await Task.Run(() => ReadAsync(source, directory, token), token);
            if (token.IsCancellationRequested)
            {
                bitmap.Dispose();
                return;
            }

            var previous = Bitmap;
            _image.Source = bitmap;
            previous?.Dispose();
            _loadedSource = source;
            _loadedDirectory = directory;
            Fold.SetIsOpen(_status, false);
            Fold.SetIsOpen(_image, true);
            Cursor = new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(this, string.IsNullOrWhiteSpace(Caption) ? "Open image" : Caption);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                _status.Text = string.IsNullOrWhiteSpace(Caption) ? "Image unavailable" : $"Image unavailable: {Caption}";
                ToolTip.SetTip(this, ex.Message);
            }
        }
    }

    private static async Task<Bitmap> ReadAsync(string source, string? directory, CancellationToken token)
    {
        byte[] bytes;
        if (source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            var comma = source.IndexOf(',');
            if (comma < 0 || !source[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase) || source.Length - comma > MaxBytes * 4L / 3 + 4)
            {
                throw new InvalidDataException("Invalid image data URL.");
            }

            bytes = Convert.FromBase64String(source[(comma + 1)..]);
        }
        else if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxBytes)
            {
                throw new InvalidDataException("The image is too large to preview.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(token);
            bytes = await ReadBytesAsync(stream, token);
        }
        else
        {
            var path = Uri.TryCreate(source, UriKind.Absolute, out var file) && file.IsFile
                ? file.LocalPath : Uri.UnescapeDataString(source);
            if (!Path.IsPathRooted(path) && directory is not null)
            {
                path = Path.Combine(directory, path);
            }

            await using var stream = File.OpenRead(path);
            bytes = await ReadBytesAsync(stream, token);
        }

        token.ThrowIfCancellationRequested();
        using var data = new MemoryStream(bytes);
        return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(data, 1600, BitmapInterpolationMode.HighQuality);
    }

    private static async Task<byte[]> ReadBytesAsync(Stream stream, CancellationToken token)
    {
        using var data = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (data.Length + count > MaxBytes)
            {
                throw new InvalidDataException("The image is too large to preview.");
            }

            await data.WriteAsync(buffer.AsMemory(0, count), token);
        }

        return data.ToArray();
    }
}
