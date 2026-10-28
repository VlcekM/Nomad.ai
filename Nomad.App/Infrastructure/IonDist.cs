using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Nomad.App.Infrastructure;

/// <summary>
/// One Claude app install's web bundle (<c>ion-dist</c>): a folder in older versions, an Electron ASAR archive
/// (<c>ion-dist.asar</c>) since 4.29. Paths are relative to the bundle's root, with forward slashes.
/// </summary>
public abstract class IonDist
{
    /// <summary>The bundle at <paramref name="resources"/> (an app's <c>resources</c> folder), or null.</summary>
    public static IonDist? At(string resources)
    {
        var folder = Path.Combine(resources, "ion-dist");
        if (Directory.Exists(folder))
        {
            return new Folder(folder);
        }

        var archive = folder + ".asar";
        return File.Exists(archive) ? Asar.Open(archive) : null;
    }

    /// <summary>The files under <paramref name="directory"/> ending in <paramref name="extension"/>, with their sizes.</summary>
    public abstract IEnumerable<(string Path, long Length)> Files(string directory, string extension);

    public abstract bool Exists(string path);

    public abstract byte[] Read(string path);

    public string ReadText(string path) => Encoding.UTF8.GetString(Read(path));

    private sealed class Folder(string root) : IonDist
    {
        public override IEnumerable<(string, long)> Files(string directory, string extension)
        {
            var start = Path.Combine(root, directory);
            return Directory.Exists(start)
                ? Directory.EnumerateFiles(start, "*" + extension, SearchOption.AllDirectories)
                    .Select(f => (Path.GetRelativePath(root, f).Replace('\\', '/'), new FileInfo(f).Length))
                : [];
        }

        public override bool Exists(string path) => File.Exists(Full(path));

        public override byte[] Read(string path) => File.ReadAllBytes(Full(path));

        private string Full(string path) => Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// An ASAR archive: a JSON header of nested <c>files</c> (each file its size and offset into the data that follows
    /// the header, or <c>unpacked</c> into the <c>.asar.unpacked</c> folder beside it), then the files' bytes.
    /// </summary>
    private sealed class Asar : IonDist
    {
        private readonly string _archive;
        private readonly long _dataStart;
        private readonly Dictionary<string, (long Offset, long Length, bool Unpacked)> _entries = new(StringComparer.Ordinal);

        private Asar(string archive, long dataStart)
        {
            _archive = archive;
            _dataStart = dataStart;
        }

        public static Asar? Open(string archive)
        {
            using var stream = File.OpenRead(archive);
            Span<byte> head = stackalloc byte[16];
            stream.ReadExactly(head);

            // A pickle holding the header's size, then the header's pickle: its payload size, the JSON's length, the JSON.
            var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
            var jsonLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(head[12..]);
            var json = new byte[jsonLength];
            stream.ReadExactly(json);

            var asar = new Asar(archive, 8 + headerSize);
            using var header = JsonDocument.Parse(json);
            asar.Add(header.RootElement, "");
            return asar;
        }

        private void Add(JsonElement directory, string prefix)
        {
            if (!directory.TryGetProperty("files", out var files))
            {
                return;
            }

            foreach (var entry in files.EnumerateObject())
            {
                var path = prefix + entry.Name;
                if (entry.Value.TryGetProperty("files", out _))
                {
                    Add(entry.Value, path + "/");
                }
                else if (entry.Value.TryGetProperty("size", out var size))
                {
                    var unpacked = entry.Value.TryGetProperty("unpacked", out var u) && u.ValueKind == JsonValueKind.True;
                    var offset = entry.Value.TryGetProperty("offset", out var o) && long.TryParse(o.GetString(), out var value) ? value : 0;
                    _entries[path] = (offset, size.GetInt64(), unpacked);
                }
            }
        }

        public override IEnumerable<(string, long)> Files(string directory, string extension)
        {
            var prefix = directory.Length == 0 ? "" : directory.TrimEnd('/') + "/";
            return _entries
                .Where(e => e.Key.StartsWith(prefix, StringComparison.Ordinal) && e.Key.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                .Select(e => (e.Key, e.Value.Length));
        }

        public override bool Exists(string path) => _entries.ContainsKey(path);

        public override byte[] Read(string path)
        {
            var (offset, length, unpacked) = _entries[path];
            if (unpacked)
            {
                return File.ReadAllBytes(Path.Combine(_archive + ".unpacked", path.Replace('/', Path.DirectorySeparatorChar)));
            }

            using var stream = File.OpenRead(_archive);
            stream.Position = _dataStart + offset;
            var bytes = new byte[length];
            stream.ReadExactly(bytes);
            return bytes;
        }
    }
}
