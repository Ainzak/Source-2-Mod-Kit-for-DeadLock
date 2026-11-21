using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace S2ModKit.Desktop;

/// <summary>A game texture of a known layout that animated pictures are written into (in place, no compiler).</summary>
internal sealed record PictureTemplate(int Width, int Height, string Path);

/// <summary>A painting in a map: its material, colour texture, the path its frames are written to, the texture span its canvas uses
/// (u min, v min, u max, v max), its real shape (width, height in map units), the default glow and the room's light (for the
/// preview).</summary>
internal sealed record PaintingTarget(string Id, string Name, string Map, string Place, string Material, string ColorTexture, string FramesTexture,
    float[] UvRange, float[] Canvas, float Glow, float[] RoomTint)
{
    /// <summary>The canvas's width / height.</summary>
    public float Aspect => Canvas[0] / Canvas[1];
}

/// <summary>A kind of hero picture (card, background...): a file pattern with <c>{hero}</c> for the hero's internal name.</summary>
internal sealed record HeroPictureKind(string Id, string Name, string Pattern);

/// <summary>
/// The surfaces S2ModKit can put pictures on (<c>catalogues/pictures.json</c>, shipped with the app, schema <c>picture-targets@1</c>):
/// measured paintings, the template textures for animations and the kinds of hero pictures. Game paths live in that file, not in
/// code.
/// </summary>
internal sealed record PictureTargets(string WhiteMask, IReadOnlyList<PictureTemplate> Templates, IReadOnlyList<PaintingTarget> Paintings, IReadOnlyList<HeroPictureKind> HeroPictures)
{
    public const string Schema = "picture-targets@1";

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static PictureTargets Shipped { get; } = Load(System.IO.Path.Combine(AppContext.BaseDirectory, "catalogues", "pictures.json"));

    public static PictureTargets Load(string path)
    {
        var file = JsonSerializer.Deserialize<TargetsFile>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException($"'{path}' is empty.");
        if (file.Schema != Schema)
        {
            throw new InvalidDataException($"'{path}' is not a {Schema} file.");
        }

        foreach (var painting in file.Paintings)
        {
            if (painting.UvRange.Length != 4 || painting.Canvas.Length != 2 || painting.RoomTint.Length != 3 || painting.Canvas.Any(size => size <= 0))
            {
                throw new InvalidDataException($"'{path}': painting '{painting.Id}' has an incomplete measurement.");
            }
        }

        return new PictureTargets(file.WhiteMask, [.. file.Templates.OrderBy(template => template.Width * template.Height)], file.Paintings, file.HeroPictures);
    }

    /// <summary>The smallest template that holds <paramref name="frames"/> cells of at least 256 x 256 pixels (null: none does).</summary>
    public PictureTemplate? TemplateFor(int frames) =>
        Templates.FirstOrDefault(template => template.Width / 256 * (template.Height / 256) >= frames);

    /// <summary>The files of each hero picture kind for one hero (by any of its folder names), from the game archive's file list.</summary>
    public IReadOnlyList<(HeroPictureKind Kind, string Path)> HeroFiles(IEnumerable<string> archivePaths, IReadOnlyList<string> heroFolders)
    {
        var patterns = HeroPictures.SelectMany(kind => heroFolders.Select(folder =>
            (kind, regex: new Regex("^" + kind.Pattern.Replace("{hero}", Regex.Escape(folder), StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))).ToArray();
        return [.. archivePaths.SelectMany(path => patterns.Where(pattern => pattern.regex.IsMatch(path)).Select(pattern => (pattern.kind, path)))
            .DistinctBy(item => item.path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => HeroPictures.ToList().IndexOf(item.kind)).ThenBy(item => item.path, StringComparer.Ordinal)];
    }

    private sealed record TargetsFile(string Schema, string WhiteMask, List<PictureTemplate> Templates, List<PaintingTarget> Paintings, List<HeroPictureKind> HeroPictures)
    {
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Rest { get; init; }
    }
}
