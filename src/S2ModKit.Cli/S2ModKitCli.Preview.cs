using System.CommandLine;
using System.Globalization;
using S2ModKit.Adapters.Preview;
using S2ModKit.Application;
using S2ModKit.Infrastructure;

namespace S2ModKit.Cli;

public sealed partial class S2ModKitCli
{
    private ModelPreviewApplication? modelPreviewApplication;

    /// <summary>Renders model previews (defaults to the OpenGL renderer and the PNG encoder).</summary>
    public ModelPreviewApplication ModelPreviewApplication
    {
        get => modelPreviewApplication ??= new ModelPreviewApplication(new VrfModelPreviewRenderer(), new PngPreviewImageEncoder());
        init => modelPreviewApplication = value;
    }

    private Command CreatePreviewCommand(TextWriter output, TextWriter error)
    {
        var command = new Command("preview", "Offline previews of compiled models (read-only).");
        var render = new Command("render", "Render a model as the game files resolve it (addons first): one animated PNG per view and a frame sheet.");
        var game = RequiredStringOption("--game", "Deadlock 'game' folder (contains 'citadel' and 'core').");
        var addons = new Option<string[]>("--addon") { Description = "Addon VPK loaded before the game (repeat; first wins, like a lower pak number)." };
        var model = RequiredStringOption("--model", "Model path inside the archives, for example models/heroes_staging/astro/astro.vmdl.");
        var clip = new Option<string?>("--clip") { Description = "Animation clip (.vnmclip path in the archives, or an animation name of the model)." };
        var views = new Option<string[]>("--view") { Description = "name=yaw[,pitch[,zoom[,height]]]: degrees, closeness (2 = twice as close), aim height as a fraction of the model (0.7 = chest); yaw 0 faces the model's front (repeat). Default: front=0 side=90 back=180." };
        var frames = new Option<int>("--frames") { Description = "Frames per view; 0 = one loop of the clip (one frame without --clip).", DefaultValueFactory = _ => 0 };
        var fps = new Option<int>("--fps") { Description = "Frames per second of clip time and playback.", DefaultValueFactory = _ => 30 };
        var size = new Option<string>("--size") { Description = "WIDTHxHEIGHT of each view.", DefaultValueFactory = _ => "480x720" };
        var rows = new Option<int>("--sheet-rows") { Description = "Sampled frames in the sheet.", DefaultValueFactory = _ => 4 };
        var scene = new Option<string>("--scene") { Description = "Lighting scene: a map under citadel/maps/ui (hero_select = the game's hero showcase), or 'none' for --envmap.", DefaultValueFactory = _ => "hero_select" };
        var envmap = new Option<string?>("--envmap") { Description = $"Light probe: .vtex_c file or texture path in the archives (default {PreviewSession.DefaultEnvironmentMap})." };
        var outline = new Option<float>("--outline") { Description = "Ink outline thickness in model units before the material mask (0 = off).", DefaultValueFactory = _ => PreviewSession.DefaultOutlineThickness };
        var outputPath = RequiredStringOption("--output", "New output folder (must not exist).");
        var format = CreateFormatOption();
        foreach (var option in new Option[] { game, addons, model, clip, views, frames, fps, size, rows, scene, envmap, outline, outputPath, format })
        {
            render.Options.Add(option);
        }

        render.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "preview.render", IsJson(parseResult.GetRequiredValue(format)), output, error,
            () => Task.FromResult(ModelPreviewApplication.Render(
                new ModelPreviewRequest(
                    PreviewArchives(parseResult.GetRequiredValue(game), parseResult.GetValue(addons) ?? [], SceneName(parseResult.GetValue(scene))),
                    parseResult.GetRequiredValue(model),
                    parseResult.GetValue(clip),
                    ParseViews(parseResult.GetValue(views) ?? []),
                    parseResult.GetValue(frames),
                    parseResult.GetValue(fps),
                    ParseSize(parseResult.GetValue(size)!).Width,
                    ParseSize(parseResult.GetValue(size)!).Height,
                    parseResult.GetValue(envmap),
                    parseResult.GetRequiredValue(outputPath),
                    parseResult.GetValue(rows),
                    SceneName(parseResult.GetValue(scene)) is { } name ? $"maps/ui/{name}.vmap" : null,
                    parseResult.GetValue(outline)),
                cancellationToken)),
            RenderModelPreview,
            cancellationToken));
        command.Subcommands.Add(render);
        return command;
    }

    private static string[] PreviewArchives(string game, IEnumerable<string> addons, string? scene) =>
        [.. addons, Path.Combine(game, "citadel", "pak01_dir.vpk"), Path.Combine(game, "core", "pak01_dir.vpk"),
            .. scene is null ? Array.Empty<string>() : [Path.Combine(game, "citadel", "maps", "ui", scene + ".vpk")]];

    private static string? SceneName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (value.IndexOfAny(['/', '\\', '.']) >= 0)
        {
            throw Domain.Errors.Input("PREVIEW_SCENE_INVALID", $"'{value}' is not a map name.", "Example: --scene hero_select");
        }

        return value;
    }

    private static PreviewView[] ParseViews(string[] values)
    {
        if (values.Length == 0)
        {
            return [new("front", 0), new("side", 90), new("back", 180)];
        }

        return values.Select(value =>
        {
            var parts = value.Split('=', 2);
            var numbers = parts.Length == 2 ? parts[1].Split(',') : [];
            var parsed = new float[numbers.Length];
            if (parts.Length != 2 || numbers.Length is < 1 or > 4
                || numbers.Select((text, i) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i])).Any(ok => !ok))
            {
                throw Domain.Errors.Input("PREVIEW_VIEWS_INVALID", $"'{value}' is not name=yaw[,pitch[,zoom[,height]]].", "Example: --view front=0 --view chest=0,5,3,0.7");
            }

            return new PreviewView(parts[0], parsed[0], numbers.Length > 1 ? parsed[1] : 5f, numbers.Length > 2 ? parsed[2] : 1f, numbers.Length > 3 ? parsed[3] : 0.5f);
        }).ToArray();
    }

    private static (int Width, int Height) ParseSize(string value)
    {
        var parts = value.Split('x', 'X');
        if (parts.Length != 2 || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var width) || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var height))
        {
            throw Domain.Errors.Input("PREVIEW_SIZE_INVALID", $"'{value}' is not WIDTHxHEIGHT.", "Example: --size 480x720");
        }

        return (width, height);
    }

    private static void RenderModelPreview(TextWriter output, ModelPreviewResult result)
    {
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Preview: {result.Frames} frame(s), clip {result.ClipSeconds:0.###} s -> {result.OutputDirectory}"));
        output.WriteLine(string.Join(", ", result.Files));
        output.WriteLine($"Renderer: {result.Renderer}");
    }
}
