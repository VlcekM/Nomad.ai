using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;

namespace Nomad.App.Infrastructure;

/// <summary>One stage of the working mark: a vertical strip of square frames, played at <see cref="FrameMs"/>.</summary>
public sealed record MarkSheet(Bitmap Strip, int Frames, double FrameMs, int[] Seams)
{
    /// <summary>The side of a frame in the strip, in pixels.</summary>
    public int FrameSize => Strip.PixelSize.Width;

    public double DurationMs => Frames * FrameMs;
}

/// <summary>
/// The desktop app's working mark (its "clay"): sprite sheets per activity ("think", "read", "search", "code",
/// "write", "compose", "blocked", "default"), each in three stages, "in", "loop" and "out". They are Anthropic's
/// artwork, so they are not shipped with Nomad: they are read from the installed Claude app at start, from the
/// script of its web bundle that holds them (base64 WebP, or a file beside it). Without the app,
/// <see cref="Current"/> stays null and the pixel spinner is drawn instead.
/// </summary>
public static partial class WorkingMarkSheets
{
    public static IReadOnlyDictionary<string, MarkSheet>? Current { get; private set; }

    public static event Action? Loaded;

    public static void LoadInBackground() => Task.Run(() =>
    {
        try
        {
            if (Find() is { } sheets)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    Current = sheets;
                    Loaded?.Invoke();
                });
            }
        }
        catch (Exception)
        {
            // An app version laid out differently: keep the pixel spinner.
        }
    });

    private static Dictionary<string, MarkSheet>? Find()
    {
        foreach (var dist in ClaudeFonts.IonDists())
        {
            // The sheets' script is some 60 KB; most of the bundle's thousands of files can be skipped by size.
            foreach (var (file, length) in dist.Files("assets", ".js"))
            {
                if (length is < 20_000 or > 400_000)
                {
                    continue;
                }

                var script = dist.ReadText(file);
                if (script.Contains("SHEET_FRAME", StringComparison.Ordinal) && script.Contains("\"think.loop\":{frames:", StringComparison.Ordinal))
                {
                    return Parse(script, dist, file[..(file.LastIndexOf('/') + 1)]);
                }
            }
        }

        return null;
    }

    private static Dictionary<string, MarkSheet>? Parse(string script, IonDist dist, string folder)
    {
        // "think.loop":{frames:110,speed:33.3333,seams:[54],rest:95}, and grow:{...} without quotes.
        var info = new Dictionary<string, (int Frames, double Speed, int[] Seams)>(StringComparer.Ordinal);
        foreach (Match m in SheetInfo().Matches(script))
        {
            var seams = m.Groups[4].Success
                ? m.Groups[4].Value.Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray()
                : [];
            info.TryAdd(m.Groups[1].Value, (int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), seams));
        }

        // x=new URL("data:image/webp;base64,..." or "/assets/v1/....webp"
        var urls = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in UrlVariable().Matches(script))
        {
            urls[m.Groups[1].Value] = m.Groups[2].Value;
        }

        // The sheet → image map: {"sphere.in":s,"sphere.loop":c,...,grow:u}
        var map = ImageMap().Match(script);
        if (!map.Success)
        {
            return null;
        }

        var sheets = new Dictionary<string, MarkSheet>(StringComparer.Ordinal);
        foreach (Match entry in MapEntry().Matches(map.Value))
        {
            var name = entry.Groups[1].Value;
            if (!info.TryGetValue(name, out var meta) && !info.TryGetValue(Alias(name), out meta))
            {
                continue;
            }

            if (urls.TryGetValue(entry.Groups[2].Value, out var url) && Load(url, dist, folder) is { } strip)
            {
                sheets[name] = new MarkSheet(strip, meta.Frames, meta.Speed, meta.Seams);
            }
        }

        return sheets.ContainsKey("default.loop") ? sheets : null;
    }

    // The script's own aliases: the sphere is drawn with the default sheets, compose with the quill's.
    private static string Alias(string name) =>
        name.StartsWith("sphere.", StringComparison.Ordinal) ? "default" + name[6..]
        : name.StartsWith("compose.", StringComparison.Ordinal) ? "quill" + name[7..]
        : name;

    private static Bitmap? Load(string url, IonDist dist, string folder)
    {
        byte[] bytes;
        if (url.StartsWith("data:", StringComparison.Ordinal))
        {
            bytes = Convert.FromBase64String(url[(url.IndexOf(',') + 1)..]);
        }
        else
        {
            var file = folder + url[(url.LastIndexOf('/') + 1)..];
            if (!dist.Exists(file))
            {
                return null;
            }

            bytes = dist.Read(file);
        }

        using var stream = new MemoryStream(bytes);
        return new Bitmap(stream);
    }

    [GeneratedRegex("\"?([a-z]+(?:\\.(?:in|loop|out))?)\"?:\\{frames:(\\d+),speed:([\\d.]+)(?:,seams:\\[([\\d,]+)\\])?(?:,rest:\\d+)?\\}")]
    private static partial Regex SheetInfo();

    [GeneratedRegex("([A-Za-z_$][\\w$]*)=new URL\\(\"([^\"]+)\"")]
    private static partial Regex UrlVariable();

    [GeneratedRegex("\\{\"sphere\\.in\":[A-Za-z_$][^{}]*\\}")]
    private static partial Regex ImageMap();

    [GeneratedRegex("\"?([a-z]+(?:\\.(?:in|loop|out))?)\"?:([A-Za-z_$][\\w$]*)")]
    private static partial Regex MapEntry();
}
