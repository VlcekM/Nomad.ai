using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;

namespace Nomad.App.Infrastructure;

/// <summary>
/// Anthropic Sans and Anthropic Mono, taken from a locally installed Claude desktop app. It ships
/// them as WOFF2 only, which Skia cannot open, so they are unpacked to TrueType once into the local
/// Nomad fonts cache (via DirectWrite on Windows and <see cref="Woff2Decoder"/> elsewhere). Nothing
/// is bundled with Nomad.ai.
/// </summary>
public static partial class ClaudeFonts
{
    public const string Sans = "Anthropic Sans";
    public const string Mono = "Anthropic Mono";

    /// <summary>The font collection key: <c>fonts:claude#Anthropic Sans</c>.</summary>
    public static readonly Uri Key = new("fonts:claude");

    private static readonly Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase);

    public static bool HasSans => Files.ContainsKey(Sans);

    public static bool HasMono => Files.ContainsKey(Mono);

    /// <summary>Finds and unpacks the fonts, and registers them with Avalonia. Never throws.</summary>
    public static void Register()
    {
        try
        {
            if (FindAssets() is not { } assets)
            {
                return;
            }

            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nomad", "fonts");
            foreach (var (family, (dist, source)) in assets)
            {
                // The source name carries a content hash, so a new app version gives a new file.
                var target = Path.Combine(cache, $"{family.Replace(' ', '-')}-{Path.GetFileNameWithoutExtension(source)}.ttf");
                if (!File.Exists(target))
                {
                    Directory.CreateDirectory(cache);
                    var raw = dist.Read(source);
                    var unpacked = OperatingSystem.IsWindows() ? Woff2.Unpack(raw) : Woff2Decoder.Decode(raw);
                    File.WriteAllBytes(target, unpacked);
                }

                Files[family] = target;
            }

            if (Files.Count > 0)
            {
                FontManager.Current.AddFontCollection(new Collection());
            }
        }
        catch (Exception)
        {
            Files.Clear(); // No Claude fonts: the settings simply don't offer them.
        }
    }

    /// <summary>
    /// The Claude app's web bundles (<c>ion-dist</c>): on Windows the Microsoft Store (MSIX) install first, then
    /// Squirrel installs in <c>%LOCALAPPDATA%\AnthropicClaude</c> (newest first); on Linux standard system, user,
    /// Snap, Flatpak, and PATH-resolved installs.
    /// </summary>
    public static List<IonDist> IonDists()
    {
        var resources = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            if (StorePackagePath() is { } package)
            {
                resources.Add(Path.Combine(package, "app", "resources"));
            }

            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnthropicClaude");
            if (Directory.Exists(root))
            {
                resources.AddRange(Directory.GetDirectories(root, "app-*")
                    .Select(d => (Dir: Path.Combine(d, "resources"), Version: Version.TryParse(Path.GetFileName(d)[4..], out var v) ? v : new Version()))
                    .OrderByDescending(d => d.Version)
                    .Select(d => d.Dir));
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            resources.AddRange(LinuxResourceFolders());
        }

        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var dists = new List<IonDist>();
        foreach (var folder in resources)
        {
            if (!seen.Add(folder))
            {
                continue;
            }

            try
            {
                if (IonDist.At(folder) is { } dist)
                {
                    dists.Add(dist);
                }
            }
            catch (Exception)
            {
                // An archive that can't be read: try the next install.
            }
        }

        return dists;
    }

    private static IEnumerable<string> LinuxResourceFolders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidates =
        [
            "/usr/lib/claude-desktop/resources",
            "/usr/lib64/claude-desktop/resources",
            "/usr/share/claude-desktop/resources",
            "/opt/claude-desktop/resources",
            "/opt/Claude/resources",
            "/opt/claude/resources",
            "/usr/local/lib/claude-desktop/resources",
            "/usr/local/share/claude-desktop/resources",
            "/snap/claude-desktop/current/resources",
            "/snap/claude-desktop/current/usr/lib/claude-desktop/resources",
            "/var/lib/flatpak/app/com.anthropic.Claude/current/active/files/lib/claude-desktop/resources",
            Path.Combine(home, ".local", "lib", "claude-desktop", "resources"),
            Path.Combine(home, ".local", "share", "claude-desktop", "resources"),
            Path.Combine(home, ".local", "opt", "claude-desktop", "resources"),
            Path.Combine(home, ".local", "share", "flatpak", "app", "com.anthropic.Claude", "current", "active", "files", "lib", "claude-desktop", "resources"),
        ];

        foreach (var path in candidates)
        {
            yield return path;
        }

        // Resolve claude-desktop on PATH (covers NixOS, custom /opt prefixes, and user bin symlinks).
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
        {
            yield break;
        }

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var exe = Path.Combine(dir, "claude-desktop");
            if (!File.Exists(exe))
            {
                continue;
            }

            string? resolved = null;
            try
            {
                resolved = File.ResolveLinkTarget(exe, returnFinalTarget: true)?.FullName ?? exe;
            }
            catch (Exception)
            {
                resolved = exe;
            }

            if (Path.GetDirectoryName(resolved) is { } binDir)
            {
                yield return Path.Combine(binDir, "resources");
                yield return Path.GetFullPath(Path.Combine(binDir, "..", "lib", "claude-desktop", "resources"));
            }
        }
    }

    /// <summary>Upright Anthropic Sans and Mono from the stylesheets of the first Claude app install that has them.</summary>
    private static Dictionary<string, (IonDist Dist, string Path)>? FindAssets()
    {
        foreach (var dist in IonDists())
        {
            var found = new Dictionary<string, (IonDist, string)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (css, _) in dist.Files("", ".css"))
            {
                foreach (Match m in FontFace().Matches(dist.ReadText(css)))
                {
                    var family = m.Groups[1].Value == "sans" ? Sans : Mono;
                    var file = m.Groups[2].Value.TrimStart('/');
                    if (!found.ContainsKey(family) && dist.Exists(file))
                    {
                        found[family] = (dist, file);
                    }
                }

                if (found.Count == 2)
                {
                    break;
                }
            }

            if (found.Count > 0)
            {
                return found;
            }
        }

        return null;
    }

    // The Store app's package family (name and publisher hash); its install folder under WindowsApps can be read,
    // but not listed, so it is asked for by name.
    private const string StoreFamily = "Claude_pzs8sxrjxfjjc";

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern unsafe int GetPackagesByPackageFamily(char* family, uint* count, char** fullNames, uint* bufferLength, char* buffer);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern unsafe int GetPackagePathByFullName(char* fullName, uint* pathLength, char* path);

    /// <summary>The install folder of the Store app (the first package of its family), or null.</summary>
    private static unsafe string? StorePackagePath()
    {
        const int insufficientBuffer = 122;
        fixed (char* family = StoreFamily)
        {
            uint count = 0, length = 0;
            if (GetPackagesByPackageFamily(family, &count, null, &length, null) != insufficientBuffer || count == 0)
            {
                return null;
            }

            var names = new char*[count];
            var buffer = new char[length];
            fixed (char** pNames = names)
            fixed (char* pBuffer = buffer)
            {
                if (GetPackagesByPackageFamily(family, &count, pNames, &length, pBuffer) != 0)
                {
                    return null;
                }

                uint pathLength = 0;
                if (GetPackagePathByFullName(pNames[0], &pathLength, null) != insufficientBuffer)
                {
                    return null;
                }

                var path = new char[pathLength];
                fixed (char* pPath = path)
                {
                    return GetPackagePathByFullName(pNames[0], &pathLength, pPath) == 0 ? new string(pPath) : null;
                }
            }
        }
    }

    [GeneratedRegex(@"@font-face\{font-family:anthropic-(sans|mono);src:url\(([^)]+\.woff2)\)[^}]*font-style:normal")]
    private static partial Regex FontFace();

    /// <summary>
    /// The two families, each one variable typeface; bold and italic are synthesized by the base collection.
    /// </summary>
    private sealed class Collection : FontCollectionBase
    {
        /// <summary>
        /// Loads each unpacked file, and lists it under its family's name too: the fonts' own names are other ones
        /// (<c>Anthropic Interface Beta …</c>, <c>Anthropic Mono Web</c>).
        /// </summary>
        public Collection()
        {
            var regular = new FontCollectionKey(FontStyle.Normal, FontWeight.Normal, FontStretch.Normal);
            foreach (var (family, file) in Files)
            {
                using var stream = File.OpenRead(file);
                if (!TryAddGlyphTypeface(stream, out var typeface)) continue;

                if (TryAddGlyphTypeface(family, regular, typeface))
                {
                    AddFontFamily(new FontFamily(Key, family));
                }
            }
        }

        public override Uri Key => ClaudeFonts.Key;
    }

    /// <summary>WOFF2 to TrueType through DirectWrite (IDWriteFactory5::UnpackFontFile, Windows 10 1703+).</summary>
    private static unsafe class Woff2
    {
        private const int ContainerWoff2 = 2;

        // vtable slots: IUnknown 0-2, IDWriteFactory 3-23, ...1 24-25, ...2 26-30, ...3 31-39, ...4 40-42, ...5 43-47.
        private const int AnalyzeContainerTypeSlot = 46;
        private const int UnpackFontFileSlot = 47;

        private static readonly Guid FactoryFiveId = new("958DB99A-BE2A-4F09-AF7D-65189803D1D3");

        [DllImport("dwrite.dll", ExactSpelling = true)]
        private static extern int DWriteCreateFactory(int factoryType, Guid* iid, void** factory);

        public static byte[] Unpack(byte[] woff2)
        {
            void* factory = null;
            void* stream = null;
            var iid = FactoryFiveId;
            Check(DWriteCreateFactory(0, &iid, &factory));
            try
            {
                var vtable = *(void***)factory;
                fixed (byte* data = woff2)
                {
                    var analyze = (delegate* unmanaged[Stdcall]<void*, void*, uint, int>)vtable[AnalyzeContainerTypeSlot];
                    if (analyze(factory, data, (uint)woff2.Length) != ContainerWoff2)
                    {
                        throw new InvalidDataException("Not a WOFF2 font.");
                    }

                    var unpack = (delegate* unmanaged[Stdcall]<void*, int, void*, uint, void**, int>)vtable[UnpackFontFileSlot];
                    Check(unpack(factory, ContainerWoff2, data, (uint)woff2.Length, &stream));
                }

                // IDWriteFontFileStream: ReadFileFragment 3, ReleaseFileFragment 4, GetFileSize 5.
                var svt = *(void***)stream;
                ulong size;
                Check(((delegate* unmanaged[Stdcall]<void*, ulong*, int>)svt[5])(stream, &size));
                void* start;
                void* context;
                Check(((delegate* unmanaged[Stdcall]<void*, void**, ulong, ulong, void**, int>)svt[3])(stream, &start, 0, size, &context));
                var result = new ReadOnlySpan<byte>(start, checked((int)size)).ToArray();
                ((delegate* unmanaged[Stdcall]<void*, void*, void>)svt[4])(stream, context);
                return result;
            }
            finally
            {
                Release(stream);
                Release(factory);
            }
        }

        private static void Release(void* unknown)
        {
            if (unknown is not null)
            {
                ((delegate* unmanaged[Stdcall]<void*, uint>)(*(void***)unknown)[2])(unknown);
            }
        }

        private static void Check(int hr)
        {
            if (hr < 0)
            {
                throw new COMException("DirectWrite could not unpack the font.", hr);
            }
        }
    }
}
