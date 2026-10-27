using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Klodi.Core.Updates;

/// <summary>A published GitHub release tagged <c>v&lt;version&gt;</c>.</summary>
public sealed record ReleaseInfo(
    string Tag,
    string Version,
    string Title,
    bool Prerelease,
    DateTimeOffset Published,
    string Notes,
    string PageUrl);

/// <summary>
/// The releases <see cref="ReleaseFeed.FetchAsync"/> found, newest first; <paramref name="Problem"/> is a one-line
/// reason when GitHub could not be asked (the cached list is used then); <paramref name="FromCache"/> is true when
/// nothing new was read (304 Not Modified, or an error).
/// </summary>
public sealed record ReleaseList(IReadOnlyList<ReleaseInfo> Releases, string? Problem, bool FromCache);

/// <summary>
/// A repository's GitHub releases, read anonymously through the REST API, 100 per page and at most 5 pages. The last
/// answer and its ETag are cached on disk: an unchanged list costs no rate limit (304) and still shows offline.
/// Mirrors Meitou's launcher (<c>launcher/Meitou.Launcher/Releases.cs</c>).
/// </summary>
public sealed class ReleaseFeed(HttpClient http, string repository, string cacheFile)
{
    public const int PageSize = 100;
    public const int MaxPages = 5;

    public string Repository => repository;

    /// <summary>The repository's releases page in a browser.</summary>
    public string PageUrl => $"https://github.com/{repository}/releases";

    /// <summary>An HTTP client for the API: GitHub refuses requests without a User-Agent.</summary>
    public static HttpClient CreateClient(string productVersion)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(KlodiInfo.Name, productVersion));
        return client;
    }

    /// <summary>Every published release, newest first. Falls back to the cached list when GitHub cannot be reached or refuses.</summary>
    public async Task<ReleaseList> FetchAsync(CancellationToken cancellationToken = default)
    {
        var cached = ReadCache();
        var body = cached?.Body;
        string? problem = null;
        var fromCache = true;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            // Page 1 is asked for with the cached ETag: unchanged (304, free of rate limit) means no release came or
            // went, so the cached pages stand. Otherwise pages are read until a short one and cached as one array.
            var pages = new List<string>();
            string? etag = null;
            for (var page = 1; page <= MaxPages; page++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases?per_page={PageSize}&page={page}");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
                if (page == 1 && cached?.ETag is { } cachedTag)
                {
                    request.Headers.TryAddWithoutValidation("If-None-Match", cachedTag);
                }

                using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                if (page == 1 && response.StatusCode == HttpStatusCode.NotModified)
                {
                    break;
                }

                if (!response.IsSuccessStatusCode)
                {
                    problem = $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}" + (body is null ? "" : "; showing the last list");
                    pages.Clear();
                    break;
                }

                if (page == 1)
                {
                    etag = response.Headers.ETag?.ToString();
                }

                var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                pages.Add(text);
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() < PageSize)
                {
                    break;
                }
            }

            if (pages.Count > 0)
            {
                body = Join(pages);
                fromCache = false;
                WriteCache(new ReleaseCache(etag, body));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or JsonException or UnauthorizedAccessException)
        {
            problem = $"GitHub not reachable ({FirstLine(ex.Message)})" + (body is null ? "" : "; showing the last list");
        }

        List<ReleaseInfo> releases = [];
        if (body is not null)
        {
            try
            {
                releases = Parse(body);
            }
            catch (JsonException ex)
            {
                problem ??= "The release list could not be read: " + FirstLine(ex.Message);
            }
        }

        return new ReleaseList(releases, problem, fromCache);
    }

    /// <summary>The API's release array: drafts and tags other than <c>v&lt;digit&gt;…</c> left out, newest first.</summary>
    public static List<ReleaseInfo> Parse(string json)
    {
        var list = new List<ReleaseInfo>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object || Bool(r, "draft"))
            {
                continue;
            }

            var tag = Str(r, "tag_name") ?? "";
            if (tag.Length < 2 || tag[0] != 'v' || !char.IsAsciiDigit(tag[1]))
            {
                continue;
            }

            list.Add(new ReleaseInfo(
                tag,
                tag[1..],
                Str(r, "name") is { Length: > 0 } name ? name : tag,
                Bool(r, "prerelease"),
                Date(r, "published_at") ?? Date(r, "created_at") ?? default,
                Str(r, "body") ?? "",
                Str(r, "html_url") ?? ""));
        }

        return [.. list.OrderByDescending(r => r.Published)];
    }

    /// <summary>
    /// Release notes as Klodi shows them: without what is only for the release page, its <c>## Download</c> section
    /// (up to the next <c>## </c> heading) and GitHub's <c>**Full Changelog**</c> line.
    /// </summary>
    public static string TrimNotes(string markdown)
    {
        var lines = new List<string>();
        var download = false;
        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                download = line.Trim() == "## Download";
            }

            if (download || line.StartsWith("**Full Changelog**", StringComparison.Ordinal))
            {
                continue;
            }

            lines.Add(line);
        }

        return string.Join("\n", lines).Trim();
    }

    /// <summary>The pages as one array, each release cut down to the fields <see cref="Parse"/> reads (the API's are ~40 KB each).</summary>
    private static string Join(List<string> pages)
    {
        string[] keep = ["tag_name", "name", "draft", "prerelease", "published_at", "created_at", "body", "html_url"];
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartArray();
            foreach (var page in pages)
            {
                using var doc = JsonDocument.Parse(page);
                foreach (var release in doc.RootElement.EnumerateArray())
                {
                    json.WriteStartObject();
                    foreach (var name in keep)
                    {
                        if (release.TryGetProperty(name, out var value))
                        {
                            json.WritePropertyName(name);
                            value.WriteTo(json);
                        }
                    }

                    json.WriteEndObject();
                }
            }

            json.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private ReleaseCache? ReadCache()
    {
        try
        {
            return File.Exists(cacheFile) ? JsonSerializer.Deserialize(File.ReadAllText(cacheFile), ReleaseJsonContext.Default.ReleaseCache) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void WriteCache(ReleaseCache cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cacheFile))!);
            var temp = cacheFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(cache, ReleaseJsonContext.Default.ReleaseCache));
            File.Move(temp, cacheFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not fatal: the list is read again next time.
        }
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Date(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.TryGetDateTimeOffset(out var d) ? d : null;

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }
}

/// <summary>The last API answer (all pages as one array) and page 1's ETag.</summary>
internal sealed record ReleaseCache(string? ETag, string Body);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ReleaseCache))]
internal sealed partial class ReleaseJsonContext : JsonSerializerContext;
