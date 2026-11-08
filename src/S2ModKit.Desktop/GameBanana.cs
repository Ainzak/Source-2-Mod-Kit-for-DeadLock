using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace S2ModKit.Desktop;

/// <summary>A Deadlock mod in a GameBanana listing. <see cref="Visibility"/> is GameBanana's own: "show", or "warn"/"hide" for mods
/// with content ratings (hidden by default in the app).</summary>
internal sealed record GameBananaMod(long Id, string Name, string Author, string? Thumbnail, int Likes, int Views,
    DateTimeOffset Added, DateTimeOffset Updated, string Category, string? Hero, string Visibility, string Version)
{
    public bool Mature => Visibility != "show";
}

/// <summary>One downloadable file of a mod (a variant): size, MD5 and GameBanana's virus-scan result.</summary>
internal sealed record GameBananaFile(long Id, string Name, long Size, string Md5, Uri Download, DateTimeOffset Added, string Description, int Downloads, string Scan);

/// <summary>A mod's page: description as plain text, screenshots, files and the author's permissions.</summary>
internal sealed record GameBananaPage(GameBananaMod Mod, string Description, IReadOnlyList<string> Images, IReadOnlyList<GameBananaFile> Files, Uri Page, IReadOnlyList<string> Credits);

/// <summary>A category (a hero under Skins, or a top-level category) with its mod count.</summary>
internal sealed record GameBananaCategory(long Id, string Name, int Count);

/// <summary>
/// Read-only access to GameBanana's public JSON API (the one its own site uses) for Deadlock (game 20948): listings, search,
/// categories, mod pages, images and downloads. Requests carry a plain user agent (the app and its version, no personal data),
/// responses and images are cached for the session and images on disk, and nothing runs in the background. Downloads are checked
/// against the size and MD5 GameBanana lists before they are used.
/// </summary>
internal sealed partial class GameBananaClient
{
    public const long Deadlock = 20948;
    public const long SkinsCategory = 33295;
    private const string Api = "https://gamebanana.com/apiv11/";
    private static readonly HttpClient Http = CreateClient();   // one connection pool for the app's lifetime
    private readonly Dictionary<string, string> responses = new(StringComparer.Ordinal);
    private readonly string imageCache;

    public GameBananaClient(string cacheFolder)
    {
        imageCache = Path.Combine(cacheFolder, "images");
    }

    /// <summary>Sort orders of listings, as shown and as the API names them.</summary>
    public static IReadOnlyList<(string Label, string Key)> Sorts { get; } =
    [
        ("Newest", "Generic_Newest"),
        ("Recently updated", "Generic_LatestModified"),
        ("Most liked", "Generic_MostLiked"),
        ("Most viewed", "Generic_MostViewed"),
    ];

    /// <summary>One page (15 mods) of Deadlock mods, optionally in one category (a hero under Skins, or a top-level category).</summary>
    public async Task<(IReadOnlyList<GameBananaMod> Mods, int Total)> ListAsync(string sort, long? category, int page, CancellationToken cancellationToken = default)
    {
        var filter = category is { } id ? $"_aFilters%5BGeneric_Category%5D={id}" : $"_aFilters%5BGeneric_Game%5D={Deadlock}";
        return Records(await GetAsync($"Mod/Index?_nPage={page}&_nPerpage=15&{filter}&_sSort={Uri.EscapeDataString(sort)}", cancellationToken).ConfigureAwait(false));
    }

    /// <summary>One page of Deadlock mods matching <paramref name="text"/> (GameBanana's own search).</summary>
    public async Task<(IReadOnlyList<GameBananaMod> Mods, int Total)> SearchAsync(string text, int page, CancellationToken cancellationToken = default) =>
        Records(await GetAsync($"Util/Search/Results?_sModelName=Mod&_sOrder=best_match&_idGameRow={Deadlock}&_sSearchString={Uri.EscapeDataString(text)}&_nPage={page}", cancellationToken).ConfigureAwait(false));

    /// <summary>The top-level categories (HUD, Skins, ...) and the heroes under Skins.</summary>
    public async Task<(IReadOnlyList<GameBananaCategory> Top, IReadOnlyList<GameBananaCategory> Heroes)> CategoriesAsync(CancellationToken cancellationToken = default)
    {
        static IReadOnlyList<GameBananaCategory> Read(string json)
        {
            using var document = JsonDocument.Parse(json);
            return [.. document.RootElement.EnumerateArray().Select(item => new GameBananaCategory(Long(item, "_idRow"), Text(item, "_sName"), (int)Long(item, "_nItemCount")))];
        }

        var top = Read(await GetAsync($"Mod/Categories?_idGameRow={Deadlock}&_sSort=a_to_z&_bShowEmpty=true", cancellationToken).ConfigureAwait(false));
        var heroes = Read(await GetAsync($"Mod/Categories?_idCategoryRow={SkinsCategory}&_sSort=a_to_z&_bShowEmpty=true", cancellationToken).ConfigureAwait(false));
        return (top, heroes);
    }

    /// <summary>A mod's page: description, screenshots, files and credits.</summary>
    public async Task<GameBananaPage> PageAsync(long id, CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(await GetAsync($"Mod/{id}/ProfilePage", cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        var files = root.TryGetProperty("_aFiles", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(file => new GameBananaFile(Long(file, "_idRow"), Text(file, "_sFile"), Long(file, "_nFilesize"), Text(file, "_sMd5Checksum"),
                new Uri(Text(file, "_sDownloadUrl")), Time(file, "_tsDateAdded"), Text(file, "_sDescription"), (int)Long(file, "_nDownloadCount"), Text(file, "_sAvResult"))).ToArray()
            : [];
        var credits = new List<string>();
        if (root.TryGetProperty("_aCredits", out var groups) && groups.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in groups.EnumerateArray())
            {
                if (group.TryGetProperty("_aAuthors", out var authors) && authors.ValueKind == JsonValueKind.Array)
                {
                    credits.AddRange(authors.EnumerateArray().Select(author => $"{Text(group, "_sGroupName")}: {Text(author, "_sName")}{(Text(author, "_sRole") is { Length: > 0 } role ? $" ({role})" : string.Empty)}"));
                }
            }
        }

        return new GameBananaPage(Mod(root), PlainText(Text(root, "_sText")), Images(root, "_sFile530"), files, new Uri(Text(root, "_sProfileUrl")), credits);
    }

    /// <summary>An image's bytes, from the disk cache when it was fetched before.</summary>
    public async Task<byte[]?> ImageAsync(string url, CancellationToken cancellationToken = default)
    {
        var file = Path.Combine(imageCache, Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url)))[..32] + Path.GetExtension(new Uri(url).AbsolutePath));
        if (File.Exists(file))
        {
            return await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var bytes = await Http.GetByteArrayAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(imageCache);
            await File.WriteAllBytesAsync(file, bytes, cancellationToken).ConfigureAwait(false);
            return bytes;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>Downloads <paramref name="file"/> into <paramref name="folder"/> and checks its size and MD5 against GameBanana's listing.</summary>
    public static async Task<string> DownloadAsync(GameBananaFile file, string folder, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        Directory.CreateDirectory(folder);
        var name = string.Concat(file.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var target = Path.Combine(folder, $"gb-{file.Id}-{name}");
        using var response = await Http.GetAsync(file.Download, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using (var output = File.Create(target))
        using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                done += read;
                progress?.Report(file.Size > 0 ? (double)done / file.Size : 0);
            }
        }

        var size = new FileInfo(target).Length;
        string md5;
        using (var stream = File.OpenRead(target))
        {
#pragma warning disable CA5351 // GameBanana publishes MD5 checksums; it only confirms the download is the listed file
            md5 = Convert.ToHexStringLower(await MD5.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
#pragma warning restore CA5351
        }

        if ((file.Size > 0 && size != file.Size) || (file.Md5.Length > 0 && !string.Equals(md5, file.Md5, StringComparison.OrdinalIgnoreCase)))
        {
            File.Delete(target);
            throw new InvalidDataException($"The download of '{file.Name}' does not match GameBanana's listing (size or checksum); nothing was installed.");
        }

        return target;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"S2ModKit/{AppUpdates.Current.ToString(3)}");
        return client;
    }

    private async Task<string> GetAsync(string query, CancellationToken cancellationToken)
    {
        if (responses.TryGetValue(query, out var cached))
        {
            return cached;
        }

        using var response = await Http.GetAsync(new Uri(Api + query), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new HttpRequestException("GameBanana asks to slow down; try again in a minute.");
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        responses[query] = json;
        return json;
    }

    private static (IReadOnlyList<GameBananaMod> Mods, int Total) Records(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var total = root.TryGetProperty("_aMetadata", out var meta) ? (int)Long(meta, "_nRecordCount") : 0;
        var mods = root.TryGetProperty("_aRecords", out var records) && records.ValueKind == JsonValueKind.Array
            ? records.EnumerateArray().Where(record => Text(record, "_sModelName") == "Mod").Select(Mod).ToArray()
            : [];
        return (mods, total);
    }

    private static GameBananaMod Mod(JsonElement record)
    {
        var images = Images(record, "_sFile220");
        var root = record.TryGetProperty("_aRootCategory", out var r) ? Text(r, "_sName") : record.TryGetProperty("_aCategory", out var c) ? Text(c, "_sName") : string.Empty;
        var sub = record.TryGetProperty("_aSubCategory", out var s) ? Text(s, "_sName") : null;
        return new GameBananaMod(Long(record, "_idRow"), Text(record, "_sName"),
            record.TryGetProperty("_aSubmitter", out var submitter) ? Text(submitter, "_sName") : string.Empty,
            images.Count > 0 ? images[0] : null, (int)Long(record, "_nLikeCount"), (int)Long(record, "_nViewCount"), Time(record, "_tsDateAdded"),
            Time(record, record.TryGetProperty("_tsDateUpdated", out _) ? "_tsDateUpdated" : "_tsDateModified"), root, string.IsNullOrEmpty(sub) ? null : sub,
            record.TryGetProperty("_sInitialVisibility", out var visibility) ? visibility.GetString() ?? "show" : "show", Text(record, "_sVersion"));
    }

    private static IReadOnlyList<string> Images(JsonElement record, string size)
    {
        if (!record.TryGetProperty("_aPreviewMedia", out var media) || media.ValueKind != JsonValueKind.Object
            || !media.TryGetProperty("_aImages", out var images) || images.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. images.EnumerateArray().Select(image => $"{Text(image, "_sBaseUrl")}/{(Text(image, size) is { Length: > 0 } sized ? sized : Text(image, "_sFile"))}")];
    }

    /// <summary>The description's HTML as readable text: line breaks kept, tags removed, entities decoded.</summary>
    internal static string PlainText(string html)
    {
        var text = BreakTags().Replace(html, "\n");
        text = Tags().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        return BlankLines().Replace(text, "\n\n").Trim();
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty,
        } : string.Empty;

    private static long Long(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.Number ? value.GetInt64()
            : value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0
            : 0;

    private static DateTimeOffset Time(JsonElement element, string name) => DateTimeOffset.FromUnixTimeSeconds(Long(element, name));

    [GeneratedRegex(@"<\s*(br|/p|/div|/li|/h\d)\s*/?\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BreakTags();

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex Tags();

    [GeneratedRegex(@"\n\s*\n\s*(\n\s*)+", RegexOptions.CultureInvariant)]
    private static partial Regex BlankLines();
}
