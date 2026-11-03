using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using S2ModKit.Adapters.Source2;
using S2ModKit.Adapters.Vpk;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Cli;

public sealed partial class S2ModKitCli
{
    private static Command CreateRecolorCommand(TextWriter output, TextWriter error)
    {
        var command = new Command("recolor", "Recolor parts of colour textures into a new texture-only mod (inputs are never changed).");

        var textures = new Command("textures", "List a model's colour textures (per draw call: mesh, material, g_tColor) as the archives resolve them.");
        var listGame = RequiredStringOption("--game", "Deadlock 'game' folder (contains 'citadel' and 'core').");
        var listAddons = new Option<string[]>("--addon") { Description = "Mod VPK searched before the game (repeat; first wins)." };
        var listModel = RequiredStringOption("--model", "Model path, for example models/heroes_staging/hornet_v3/hornet.vmdl.");
        var listFormat = CreateFormatOption();
        foreach (var option in new Option[] { listGame, listAddons, listModel, listFormat })
        {
            textures.Options.Add(option);
        }

        textures.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "recolor.textures", IsJson(parseResult.GetRequiredValue(listFormat)), output, error,
            () => Task.FromResult(VrfTextureWorkshop.ListColorTextures(GameArchives(parseResult.GetRequiredValue(listGame), parseResult.GetValue(listAddons) ?? []),
                parseResult.GetRequiredValue(listModel))),
            RenderColorTextures,
            cancellationToken));

        var apply = new Command("apply", "Apply a recolor recipe: each part's texture recolored (optionally through a mask), compiled at its own path and packaged as a new mod.");
        var game = RequiredStringOption("--game", "Deadlock 'game' folder (contains 'citadel' and 'core').");
        var recipe = RequiredStringOption("--recipe", "Recipe JSON: {\"addons\": [mod VPKs], \"parts\": [{\"name\", \"texture\", \"mask\"?, \"hueShift\"?, \"saturation\"?, \"brightness\"?, \"tint\"?, \"tintStrength\"?, \"colorize\"?, \"colorizeStrength\"?}]}.");
        var outputPath = RequiredStringOption("--output", "New output folder (must not exist).");
        var compiler = new Option<string?>("--resourcecompiler") { Description = "Source 2 SDK resourcecompiler.exe (default: S2MODKIT_RESOURCECOMPILER)." };
        var sdkGame = new Option<string?>("--sdk-game") { Description = "The SDK's 'game' folder with citadel and core (default: S2MODKIT_SDK_GAME)." };
        var format = CreateFormatOption();
        foreach (var option in new Option[] { game, recipe, outputPath, compiler, sdkGame, format })
        {
            apply.Options.Add(option);
        }

        apply.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "recolor.apply", IsJson(parseResult.GetRequiredValue(format)), output, error,
            () =>
            {
                var (addons, parts) = ReadRecolorRecipe(parseResult.GetRequiredValue(recipe));
                var workshop = new VrfTextureWorkshop(
                    Setting(parseResult.GetValue(compiler), "S2MODKIT_RESOURCECOMPILER", "--resourcecompiler"),
                    Setting(parseResult.GetValue(sdkGame), "S2MODKIT_SDK_GAME", "--sdk-game"));
                var application = new RecolorApplication(workshop, new ValvePakAddonArchiveContent(), new SystemClock());
                return application.RunAsync(new RecolorRequest(GameArchives(parseResult.GetRequiredValue(game), addons), parts, parseResult.GetRequiredValue(outputPath)),
                    cancellationToken);
            },
            RenderRecolor,
            cancellationToken));

        command.Subcommands.Add(textures);
        command.Subcommands.Add(apply);
        return command;
    }

    private static string[] GameArchives(string game, IEnumerable<string> addons) =>
        [.. addons, Path.Combine(game, "citadel", "pak01_dir.vpk"), Path.Combine(game, "core", "pak01_dir.vpk")];

    private static string Setting(string? value, string variable, string option) =>
        !string.IsNullOrWhiteSpace(value) ? value
        : Environment.GetEnvironmentVariable(variable) is { Length: > 0 } fromEnvironment ? fromEnvironment
        : throw Errors.Input("RECOLOR_SDK_MISSING", $"No {option} given and {variable} is not set.", $"Pass {option} or set {variable}.");

    /// <summary>Reads a recipe; mask paths are relative to the recipe file.</summary>
    internal static (IReadOnlyList<string> Addons, IReadOnlyList<RecolorPart> Parts) ReadRecolorRecipe(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var addons = root.TryGetProperty("addons", out var a) ? a.EnumerateArray().Select(item => item.GetString()!).ToArray() : [];
        var parts = root.GetProperty("parts").EnumerateArray().Select(part =>
        {
            float Number(string name, float fallback) => part.TryGetProperty(name, out var v) ? v.GetSingle() : fallback;
            RgbColor? Color(string name) => part.TryGetProperty(name, out var v) ? RgbColor.Parse(v.GetString()!) : null;
            var mask = part.TryGetProperty("mask", out var m) ? Path.GetFullPath(Path.Combine(folder, m.GetString()!)) : null;
            return new RecolorPart(part.GetProperty("name").GetString()!, part.GetProperty("texture").GetString()!, mask,
                new ColorAdjustment(Number("hueShift", 0), Number("saturation", 1), Number("brightness", 1), Color("tint"), Number("tintStrength", 1),
                    Color("colorize"), Number("colorizeStrength", 1)));
        }).ToArray();
        return (addons, parts);
    }

    private static void RenderColorTextures(TextWriter output, IReadOnlyList<ColorTextureUse> uses)
    {
        foreach (var use in uses)
        {
            output.WriteLine($"{use.Mesh}  {use.Material}  {use.Texture}  [{Path.GetFileName(use.Archive)}]");
        }
    }

    private static void RenderRecolor(TextWriter output, RecolorResult result)
    {
        output.WriteLine($"Recolor written: {result.Package} ({result.Textures.Count} texture(s)). Install it in a lower slot than the source mod.");
        foreach (var part in result.Parts)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {part.Name}: {part.Texels} texels, mean {part.MeanBefore} -> {part.MeanAfter}"));
        }
    }
}
