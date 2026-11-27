using Android.Content;
using Android.Provider;

namespace Nomad.Mobile.Android;

/// <summary>
/// Files the agent sent, kept in the phone's Downloads, in a Nomad folder, through Android's media store (no permission needed
/// from Android 10, the oldest the app runs on), under the type the app says, and opened in whichever app the user picks for it, with
/// leave to read that one file only.
/// </summary>
public sealed class AndroidFileSaver(Context context) : IFileSaver
{
    private readonly Context _app = context.ApplicationContext ?? context;

    public string Where => "Downloads › Nomad";

    public Task<string> SaveAsync(string name, string type, Stream content, CancellationToken ct = default) => Task.Run(async () =>
    {
        var resolver = _app.ContentResolver!;
        var values = new ContentValues();
        values.Put(MediaStore.IMediaColumns.DisplayName, name);
        if (type != "application/octet-stream")
        {
            // What every app that opens it is told it is (an unknown one is left to Android, which goes by the name).
            values.Put(MediaStore.IMediaColumns.MimeType, type);
        }

        values.Put(MediaStore.IMediaColumns.RelativePath, global::Android.OS.Environment.DirectoryDownloads + "/Nomad");
        values.Put(MediaStore.IMediaColumns.IsPending, 1);
        var uri = resolver.Insert(MediaStore.Downloads.ExternalContentUri, values) ?? throw new IOException("the phone wouldn't make the file.");
        try
        {
            await using (var output = resolver.OpenOutputStream(uri) ?? throw new IOException("the phone wouldn't write the file."))
            {
                await content.CopyToAsync(output, ct);
            }

            var done = new ContentValues();
            done.Put(MediaStore.IMediaColumns.IsPending, 0);
            resolver.Update(uri, done, null, null);
            return uri.ToString()!;
        }
        catch
        {
            resolver.Delete(uri, null, null); // Nothing half-written left in Downloads.
            throw;
        }
    }, ct);

    public bool Open(string saved, string type)
    {
        var uri = global::Android.Net.Uri.Parse(saved)!;
        if (type == "application/octet-stream")
        {
            // Not a type the app knows (only ones that can't run anything get here): Android may know it from the name.
            type = _app.ContentResolver!.GetType(uri) is { Length: > 0 } known && known != type ? known : "*/*";
        }

        var intent = new Intent(Intent.ActionView);
        intent.SetDataAndType(uri, type);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
        try
        {
            _app.StartActivity(intent);
            return true;
        }
        catch (ActivityNotFoundException)
        {
            return false;
        }
    }
}
