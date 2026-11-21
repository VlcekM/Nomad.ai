using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace Nomad.App.Infrastructure;

/// <summary>An image to save: the shown bitmap, and the original file's bytes when they are known (saved as they are).</summary>
public sealed record SavableImage(Bitmap Image, string Name, byte[]? Bytes = null, string? Extension = null);

/// <summary>"Save image as…": the original bytes in their own format when known, otherwise the bitmap as PNG.</summary>
public static class ImageSaving
{
    public static async Task<bool> SaveAsync(TopLevel? top, SavableImage image)
    {
        if (top is null)
        {
            return false;
        }

        var extension = image.Bytes is not null && image.Extension is { Length: > 1 } known ? known.ToLowerInvariant() : ".png";
        var name = Path.GetFileNameWithoutExtension(image.Name);
        foreach (var bad in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(bad, '_');
        }

        var type = new FilePickerFileType(extension.TrimStart('.').ToUpperInvariant() + " image") { Patterns = ["*" + extension] };
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save image",
            SuggestedFileName = (string.IsNullOrWhiteSpace(name) ? "image" : name) + extension,
            DefaultExtension = extension.TrimStart('.'),
            FileTypeChoices = [type],
            ShowOverwritePrompt = true,
        });
        if (file is null)
        {
            return false;
        }

        try
        {
            await using var stream = await file.OpenWriteAsync();
            if (image.Bytes is not null && image.Extension is { Length: > 1 })
            {
                await stream.WriteAsync(image.Bytes);
            }
            else
            {
                image.Image.Save(stream);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
