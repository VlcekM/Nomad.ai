using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;

namespace Nomad.App.Infrastructure;

/// <summary>
/// Anthropic Sans and Anthropic Mono, taken from a locally installed Claude desktop app. It ships
/// them as WOFF2 only, which Skia cannot open, so DirectWrite unpacks them to TrueType once into
/// <c>%LOCALAPPDATA%\Nomad\fonts</c>. Nothing is bundled with Nomad.ai.
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
            foreach (var (family, source) in assets)
            {
                // The source name carries a content hash, so a new app version gives a new file.
                var target = Path.Combine(cache, $"{family.Replace(' ', '-')}-{Path.GetFileNameWithoutExtension(source)}.ttf");
                if (!File.Exists(target))
                {
                    Directory.CreateDirectory(cache);
                    File.WriteAllBytes(target, Woff2.Unpack(File.ReadAllBytes(source)));
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
    /// The Claude app's web bundles (<c>ion-dist</c>): the Microsoft Store (MSIX) install first, then the Squirrel
    /// installs in <c>%LOCALAPPDATA%\AnthropicClaude</c>, newest first.
    /// </summary>
    public static List<string> IonDists()
    {
        var dists = new List<string>();
        if (StorePackagePath() is { } package)
        {
            dists.Add(Path.Combine(package, "app", "resources", "ion-dist"));
        }

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnthropicClaude");
        if (Directory.Exists(root))
        {
            dists.AddRange(Directory.GetDirectories(root, "app-*")
                .Select(d => (Dir: Path.Combine(d, "resources", "ion-dist"), Version: Version.TryParse(Path.GetFileName(d)[4..], out var v) ? v : new Version()))
                .OrderByDescending(d => d.Version)
                .Select(d => d.Dir));
        }

        return dists.Where(Directory.Exists).ToList();
    }

    /// <summary>Upright Anthropic Sans and Mono from the stylesheets of the first Claude app install that has them.</summary>
    private static Dictionary<string, string>? FindAssets()
    {
        foreach (var dist in IonDists())
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var css in Directory.EnumerateFiles(dist, "*.css", SearchOption.AllDirectories))
            {
                foreach (Match m in FontFace().Matches(File.ReadAllText(css)))
                {
                    var family = m.Groups[1].Value == "sans" ? Sans : Mono;
                    var file = Path.Combine(dist, m.Groups[2].Value.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                    if (!found.ContainsKey(family) && File.Exists(file))
                    {
                        found[family] = file;
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

    /// <summary>The two families, each one variable typeface; bold is synthesized.</summary>
    private sealed class Collection : FontCollectionBase
    {
        private readonly Dictionary<string, IGlyphTypeface> _typefaces = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<FontFamily> _families = [];

        public override Uri Key => ClaudeFonts.Key;

        public override int Count => _families.Count;

        public override FontFamily this[int index] => _families[index];

        public override void Initialize(IFontManagerImpl fontManager)
        {
            // Avalonia keeps the stream overload out of its public surface, so it is reached by reflection
            // (a literal name on typeof(), which NativeAOT keeps).
            var create = typeof(IFontManagerImpl).GetMethod(
                "TryCreateGlyphTypeface",
                [typeof(Stream), typeof(FontSimulations), typeof(IGlyphTypeface).MakeByRefType()]);
            if (create is null)
            {
                return;
            }

            foreach (var (family, file) in Files)
            {
                using var stream = File.OpenRead(file);
                object?[] args = [stream, FontSimulations.None, null];
                if (create.Invoke(fontManager, args) is true && args[2] is IGlyphTypeface typeface)
                {
                    _typefaces[family] = typeface;
                    _families.Add(new FontFamily(Key, family));
                }
            }
        }

        public override bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight, FontStretch stretch, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IGlyphTypeface? glyphTypeface)
        {
            if (!_typefaces.TryGetValue(familyName, out glyphTypeface))
            {
                return false;
            }

            if ((weight >= FontWeight.SemiBold || style != FontStyle.Normal)
                && TryCreateSyntheticGlyphTypeface(glyphTypeface, style, weight, stretch, out var synthetic) && synthetic is not null)
            {
                glyphTypeface = synthetic;
            }

            return true;
        }

        public override IEnumerator<FontFamily> GetEnumerator() => _families.GetEnumerator();
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
