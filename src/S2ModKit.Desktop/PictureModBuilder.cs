using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using SteamDatabase.ValvePak;

namespace S2ModKit.Desktop;

/// <summary>A picture file to place and its brightness (1 = as it is).</summary>
internal sealed record PictureSource(string File, float Brightness = 1f);

/// <summary>
/// What to build: pictures on a painting (<paramref name="PaintingId"/>) or on a hero's pictures (<paramref name="HeroName"/>, the
/// kinds in <paramref name="HeroKinds"/>; all when empty). Glow: how strongly a painting lights itself (0 = only the room's light; the
/// painting's own glow, such as the Patron's eyes, is always off). Speed: 2 plays an animation twice as fast.
/// </summary>
internal sealed record PictureRequest(string Name, string? PaintingId, string? HeroName, IReadOnlyList<PictureSource> Sources, PictureFit Fit,
    float Glow, float Speed, IReadOnlyList<string> HeroKinds);

/// <summary>A built picture mod: the package, the first frame (for the mod card) and what was written.</summary>
internal sealed record PictureBuild(string Package, string Picture, IReadOnlyList<string> Written, string Message);

/// <summary>
/// Builds picture mods without the SDK compiler (<c>set_pictures@1</c>): pictures are written into the game's own textures at their
/// sizes (<see cref="InPlaceTexture"/>); an animation on a painting goes into a copy of a template texture of the right layout, laid
/// out as a grid of frames, and the painting's material gets the flipbook (<see cref="CompiledMaterialSettings.FlipbookOffsetExpression"/>,
/// fitted to the canvas's own texture span), its own glow off and the chosen glow from the picture's colours. Each build is a folder
/// under <c>picture-mods</c> in the data folder: the package, the source files (copied), <c>recipe.json</c> (<c>picture-recipe@1</c>,
/// to edit and build again) and <c>edit.json</c> (input and output hashes).
/// </summary>
internal sealed class PictureModBuilder(AppSettings settings, PictureTargets targets)
{
    public const string Operation = "set_pictures@1";
    public const string RecipeSchema = "picture-recipe@1";
    public const int MaxFrames = 256;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private string Game => Path.Combine(settings.Game, "citadel", "pak01_dir.vpk");

    /// <summary>The frames a painting would show (for the preview): composed at the canvas's shape, <paramref name="height"/> pixels tall.</summary>
    public static ComposedPicture PreviewPainting(PaintingTarget painting, IReadOnlyList<PictureLayer> layers, PictureFit fit, float speed, int height) =>
        PictureComposer.Compose(layers, Math.Max(1, (int)Math.Round(height * painting.Aspect)), height, fit, MaxFrames, speed);

    /// <summary>The game's picture files of a hero (kind, path, width, height).</summary>
    public IReadOnlyList<(HeroPictureKind Kind, string Path, int Width, int Height)> HeroFiles(Hero hero)
    {
        using var package = new Package();
        package.Read(Game);
        return [.. FindHeroPictures(package, hero).Select(item =>
        {
            var size = InPlaceTexture.ReadStored(Read(package, item.Path), item.Path);
            return (item.Kind, item.Path, size.Width, size.Height);
        })];
    }

    private IReadOnlyList<(HeroPictureKind Kind, string Path)> FindHeroPictures(Package package, Hero hero) =>
        targets.HeroFiles(package.Entries?.TryGetValue("vtex_c", out var entries) == true && entries is not null
            ? entries.Select(entry => entry.GetFullPath()) : [], hero.Folders);

    public IReadOnlySet<string> AvailableHeroKinds(Hero hero)
    {
        using var package = new Package();
        package.Read(Game);
        return FindHeroPictures(package, hero).Select(item => item.Kind.Id).ToHashSet(StringComparer.Ordinal);
    }

    public static IReadOnlyList<PictureLayer> Layers(IReadOnlyList<PictureSource> sources) =>
        [.. sources.Select(source => new PictureLayer(PictureFrames.Read(source.File), source.Brightness))];

    public Task<PictureBuild> BuildAsync(PictureRequest request, CancellationToken cancellationToken = default) => BuildAsync(request, null, cancellationToken);

    /// <summary>Builds with frames already read (<paramref name="decoded"/>, one layer per source; null: read the source files).</summary>
    internal async Task<PictureBuild> BuildAsync(PictureRequest request, IReadOnlyList<PictureLayer>? decoded, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Sources.Count is < 1 or > 2 || (decoded is not null && decoded.Count != request.Sources.Count))
        {
            throw S2ModKit.Domain.Errors.Input("PICTURE_SOURCES", "Use one or two pictures.", "Add a picture, or remove one.");
        }

        var layers = decoded ?? Layers(request.Sources);
        var folder = Path.Combine(settings.Data, "picture-mods", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Slug(request.Name)}");
        Directory.CreateDirectory(folder);
        var entries = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        string message;
        using (var package = new Package())
        {
            package.Read(Game);
            message = request.PaintingId is { } id
                ? BuildPainting(package, targets.Paintings.FirstOrDefault(p => p.Id == id) ?? throw S2ModKit.Domain.Errors.Input("PICTURE_TARGET", $"There is no painting '{id}'.", "Pick a painting from the list."), layers, request, entries)
                : BuildHero(package, request, layers, entries);
        }

        var picture = Path.Combine(folder, "picture.png");
        var first = PictureComposer.Compose(layers, 512, 512, PictureFit.Fill, 1).Frames[0];
        await File.WriteAllBytesAsync(picture, new S2ModKit.Infrastructure.PngPreviewImageEncoder().EncodePng(first), cancellationToken).ConfigureAwait(false);

        var output = Path.Combine(folder, Slug(request.Name) + "_dir.vpk");
        await S2ModKit.Adapters.Vpk.CompactVpkArchiveIO.WriteAsync(output, entries, cancellationToken).ConfigureAwait(false);
        await WriteRecordsAsync(folder, request, entries, cancellationToken).ConfigureAwait(false);
        return new PictureBuild(output, picture, [.. entries.Keys.Order(StringComparer.Ordinal)], message);
    }

    private string BuildPainting(Package game, PaintingTarget painting, IReadOnlyList<PictureLayer> layers, PictureRequest request, Dictionary<string, ReadOnlyMemory<byte>> entries)
    {
        var range = new Vector4(painting.UvRange[0], painting.UvRange[1], painting.UvRange[2], painting.UvRange[3]);
        var count = PictureComposer.FrameCount(layers, MaxFrames);
        var vectors = new Dictionary<string, Vector4> { ["g_vSelfIllumTint1"] = new(1f, 1f, 1f, 0f), ["g_vAlbedoScrollSpeed1"] = Vector4.Zero };
        var textures = new Dictionary<string, string> { ["g_tSelfIllumMask"] = targets.WhiteMask };
        var expressions = new Dictionary<string, byte[]>();
        string message;
        if (count == 1)
        {
            // a still picture: into the painting's own colour texture, at its size, fitted to the canvas's span
            var stock = Read(game, painting.ColorTexture);
            var size = InPlaceTexture.ReadStored(stock, painting.ColorTexture);
            var frame = PictureComposer.Compose(layers, Math.Max(1, (int)Math.Round(size.Height * painting.Aspect)), size.Height, request.Fit, 1).Frames[0];
            entries[painting.ColorTexture] = InPlaceTexture.Write(stock, painting.ColorTexture, size.Width, size.Height, PictureComposer.Resize(frame, size.Width, size.Height).Rgba);
            vectors["g_vAlbedoTexcoordScale1"] = CompiledMaterialSettings.FlipbookScale(1, 1, range);
            vectors["g_vAlbedoTexcoordOffset1"] = new Vector4(((0.5f - range.X) / (range.Z - range.X)) - 0.5f, ((0.5f - range.Y) / (range.W - range.Y)) - 0.5f, 0f, 0f);
            message = "a still picture";
        }
        else
        {
            var template = targets.TemplateFor(count) ?? throw S2ModKit.Domain.Errors.Input("PICTURE_FRAMES", $"{count} frames are too many.", "Use a shorter animation.");
            var grid = PictureComposer.Grid(count, template.Width, template.Height, painting.Aspect);
            var composed = PictureComposer.Compose(layers, Math.Max(1, (int)Math.Round(grid.CellHeight * painting.Aspect)), grid.CellHeight, request.Fit, MaxFrames, request.Speed);
            var atlas = PictureComposer.Atlas(composed.Frames, template.Width, template.Height, grid);
            entries[painting.FramesTexture] = InPlaceTexture.Write(Read(game, template.Path), painting.FramesTexture, template.Width, template.Height, atlas.Rgba, fastCopy: true);
            textures["g_tColor"] = painting.FramesTexture[..^2];
            vectors["g_vAlbedoTexcoordScale1"] = CompiledMaterialSettings.FlipbookScale(grid.Columns, grid.Rows, range);
            vectors["g_vAlbedoTexcoordOffset1"] = Vector4.Zero;
            expressions["g_vAlbedoTexcoordOffset1"] = CompiledMaterialSettings.FlipbookOffsetExpression(composed.Frames.Count, composed.FramesPerSecond, grid.Columns, grid.Rows, range);
            message = string.Create(CultureInfo.InvariantCulture, $"{composed.Frames.Count} frames, {composed.LoopSeconds:0.##} s loop");
        }

        entries[painting.Material] = CompiledMaterialSettings.Apply(Read(game, painting.Material), painting.Material, new MaterialSettings(
            Ints: new Dictionary<string, int> { ["F_ENABLE_TEXTURE_TRANSFORMS"] = 1, ["F_SELF_ILLUM"] = 1 },
            Floats: new Dictionary<string, float> { ["g_flAlbedoScrollQuantize1"] = 0f, ["g_flSelfIllumScale1"] = Math.Clamp(request.Glow, 0f, 4f), ["g_flSelfIllumAlbedoFactor1"] = 1f },
            Vectors: vectors, Textures: textures, Expressions: expressions));
        return $"{painting.Name}: {message}";
    }

    private string BuildHero(Package game, PictureRequest request, IReadOnlyList<PictureLayer> layers, Dictionary<string, ReadOnlyMemory<byte>> entries)
    {
        var hero = Hero.Catalogue.FirstOrDefault(h => string.Equals(h.Name, request.HeroName, StringComparison.OrdinalIgnoreCase))
            ?? throw S2ModKit.Domain.Errors.Input("PICTURE_TARGET", $"There is no hero '{request.HeroName}'.", "Pick a hero from the list.");
        var still = layers.Select(layer => layer with { Frames = [layer.Frames[0]] }).ToArray();   // hero pictures are still pictures
        var files = FindHeroPictures(game, hero).Where(file => request.HeroKinds.Count == 0 || request.HeroKinds.Contains(file.Kind.Id)).ToArray();
        if (files.Length == 0)
        {
            throw S2ModKit.Domain.Errors.Input("PICTURE_TARGET", $"No pictures of {hero.Name} were found in the game.", "Pick another hero or other kinds.");
        }

        foreach (var (_, path) in files)
        {
            var original = Read(game, path);
            var size = InPlaceTexture.ReadStored(original, path);
            var frame = PictureComposer.Compose(still, size.Width, size.Height, request.Fit, 1).Frames[0];
            entries[path] = InPlaceTexture.Write(original, path, size.Width, size.Height, frame.Rgba);
        }

        var written = string.Join(", ", files.Select(file => file.Kind.Name).Distinct());
        var missing = targets.HeroPictures.Where(kind => request.HeroKinds.Contains(kind.Id) && files.All(file => file.Kind.Id != kind.Id))
            .Select(kind => kind.Name).ToArray();
        return $"{hero.Name}: {files.Length} picture(s) ({written})" +
            (missing.Length > 0 ? $". Missing from the game archive: {string.Join(", ", missing)}" : string.Empty);
    }

    private static async Task WriteRecordsAsync(string folder, PictureRequest request, Dictionary<string, ReadOnlyMemory<byte>> entries, CancellationToken cancellationToken)
    {
        var sources = new List<object>();
        for (var i = 0; i < request.Sources.Count; i++)
        {
            var copy = Path.Combine(folder, $"source{i + 1}{Path.GetExtension(request.Sources[i].File).ToLowerInvariant()}");
            File.Copy(request.Sources[i].File, copy, overwrite: false);
            sources.Add(new { file = Path.GetFileName(copy), original = Path.GetFileName(request.Sources[i].File), brightness = request.Sources[i].Brightness, sha256 = Hash(await File.ReadAllBytesAsync(copy, cancellationToken).ConfigureAwait(false)) });
        }

        var recipe = new { schema = RecipeSchema, name = request.Name, painting = request.PaintingId, hero = request.HeroName, heroKinds = request.HeroKinds, fit = request.Fit.ToString(), glow = request.Glow, speed = request.Speed, sources };
        await File.WriteAllTextAsync(Path.Combine(folder, "recipe.json"), JsonSerializer.Serialize(recipe, Indented), cancellationToken).ConfigureAwait(false);
        var edit = new
        {
            operation = Operation,
            built = DateTime.UtcNow,
            sources,
            outputs = entries.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => new { path = entry.Key, size = entry.Value.Length, sha256 = Hash(entry.Value.Span) }),
        };
        await File.WriteAllTextAsync(Path.Combine(folder, "edit.json"), JsonSerializer.Serialize(edit, Indented), cancellationToken).ConfigureAwait(false);
    }

    private static byte[] Read(Package package, string path)
    {
        var entry = package.FindEntry(path) ?? throw S2ModKit.Domain.Errors.Input("PICTURE_GAME_FILE", $"'{path}' is not in the game.", "The game may have changed: update the app, or report it.");
        package.ReadEntry(entry, out var bytes);
        return bytes;
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Slug(string name)
    {
        var slug = new string([.. name.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')]).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return slug.Length == 0 ? "pictures" : slug[..Math.Min(40, slug.Length)];
    }
}
