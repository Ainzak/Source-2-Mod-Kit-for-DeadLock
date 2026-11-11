using System.Text.Json;
using S2ModKit.Application;

namespace S2ModKit.Desktop;

/// <summary>One recolored part of a recipe: its colour texture and material, the change and its colour range ("only this colour").</summary>
internal sealed record RecipeRecolor(int Part, string Texture, string? Material, ColorAdjustment Change, RgbColor? RangeTarget = null, float RangeTolerance = 0);

/// <summary>One shiny part of a recipe: its normal-roughness texture (path without <c>_c</c>) and the shine (0-1).</summary>
internal sealed record RecipeShine(int Part, string Texture, float Shine);

/// <summary>One reshaped part of a recipe (numbers as arrays: x, y, z).</summary>
internal sealed record RecipeShape(int Part, string Material, float[] Scale, float[] RotationDegrees, float[] Move, ShapePivot Pivot, float[] BoundsMin, float[] BoundsMax,
    float Displacement, ShapeLevel Level);

/// <summary>
/// Everything a Build did (<c>recipe.json</c> beside the built package, <c>app-recipe@1</c>): the game build it was made for, the base
/// (the game, or a mod by id, key and name), the model, hidden parts, recolors, shapes and effect colours. Re-apply edits builds it
/// again on the current game files (or the base mod's current file) after a game update.
/// </summary>
internal sealed record EditRecipe(
    string Schema,
    int? GameBuild,
    string Model,
    string? BaseName,
    string? BaseModId,
    string? BaseKey,
    int[] Hidden,
    RecipeRecolor[] Recolors,
    RecipeShape[] Shapes,
    Dictionary<string, EffectColorChange> Effects,
    DateTimeOffset Built,
    RecipeShine[]? Shines = null,
    RecipeAddedEffect[]? Added = null,
    RecipeOpacity[]? Opacities = null)
{
    public const string FileName = "recipe.json";
    public const string CurrentSchema = "app-recipe@1";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public bool HasEdits => Hidden.Length > 0 || Recolors.Length > 0 || Shapes.Length > 0 || Effects.Count > 0 || Shines is { Length: > 0 } || Added is { Length: > 0 } || Opacities is { Length: > 0 };

    public void Write(string folder) => File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(this, Indented));

    /// <summary>The recipe of a mod the app built (beside its installed package), or null.</summary>
    public static EditRecipe? Of(ModEntry mod)
    {
        ArgumentNullException.ThrowIfNull(mod);
        if (mod.Install?.Receipt.PackagePath is not { Length: > 0 } package || Path.GetDirectoryName(package) is not { } folder)
        {
            return null;
        }

        var path = Path.Combine(folder, FileName);
        try
        {
            return File.Exists(path) && JsonSerializer.Deserialize<EditRecipe>(File.ReadAllText(path)) is { Schema: CurrentSchema } recipe ? recipe : null;
        }
        catch (JsonException exception)
        {
            AppLog.Error($"Reading the recipe of '{mod.Name}'", exception);
            return null;
        }
    }

    /// <summary>The enabled mods the app built for another game build than <paramref name="build"/> (null: unknown, none).</summary>
    public static IReadOnlyList<(ModEntry Mod, EditRecipe Recipe)> Outdated(IReadOnlyList<ModEntry> mods, int? build) =>
        build is null ? [] : [.. mods.Where(mod => mod.Loaded).Select(mod => (Mod: mod, Recipe: Of(mod))).Where(item => item.Recipe is { GameBuild: { } made } && made != build).Select(item => (item.Mod, item.Recipe!))];

    /// <summary>The base mod of a recipe among <paramref name="mods"/> (by mod id, then key, then name), never the edited mod itself.</summary>
    public ModEntry? BaseIn(IReadOnlyList<ModEntry> mods, ModEntry edited)
    {
        ArgumentNullException.ThrowIfNull(mods);
        if (BaseName is null)
        {
            return null;
        }

        var others = mods.Where(mod => mod.Key != edited.Key && mod.Install?.Receipt.PackagePath != edited.Install?.Receipt.PackagePath).ToArray();
        return others.FirstOrDefault(mod => BaseModId is not null && mod.ModId == BaseModId)
            ?? others.FirstOrDefault(mod => BaseKey is not null && mod.Key == BaseKey)
            ?? others.FirstOrDefault(mod => mod.Name == BaseName);
    }
}

/// <summary>An always-on effect added to the model: the effect file, the bone it sits on (null: follows the hero) and its place in
/// that bone's space; <c>Source</c>: the mod archive or loose file it was copied from (null: the game or the edited mod);
/// <c>Parent</c>: the ability effect it plays with (null: always on).</summary>
internal sealed record RecipeAddedEffect(string Effect, string? Bone, float[] Offset, string? Source = null, string? Parent = null);

/// <summary>A see-through part: its material and the opacity it is drawn with (1 solid, 0 invisible).</summary>
internal sealed record RecipeOpacity(int Part, string Material, float Opacity);
