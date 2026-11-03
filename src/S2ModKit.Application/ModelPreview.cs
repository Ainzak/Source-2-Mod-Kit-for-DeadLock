using System.Globalization;
using System.Text.Json;
using S2ModKit.Domain;

namespace S2ModKit.Application;

/// <summary>One camera around the model: yaw 0 looks at the model's front (+X), 90 its left side; pitch raises the camera;
/// zoom 2 is twice as close; height is the aim point as a fraction of the model's height (0.5 = middle, 0.7 = chest).</summary>
public sealed record PreviewView(string Name, float YawDegrees, float PitchDegrees = 5f, float Zoom = 1f, float Height = 0.5f);

/// <summary>
/// A read-only preview of a compiled model as the game files resolve it. <c>Archives</c> is the search order (first
/// wins): addon VPKs in the game's order (lower pak number first), then the game archives. Nothing is written except
/// the new <c>OutputDirectory</c>.
/// </summary>
public sealed record ModelPreviewRequest(
    IReadOnlyList<string> Archives,
    string ModelPath,
    string? ClipPath,
    IReadOnlyList<PreviewView> Views,
    int Frames,
    int FramesPerSecond,
    int Width,
    int Height,
    string? EnvironmentMap,
    string OutputDirectory,
    int SheetRows = 4,
    string? SceneMap = null,
    float OutlineThickness = 0.15f);

/// <summary>Top-down RGBA8 pixels.</summary>
public sealed record PreviewImage(int Width, int Height, byte[] Rgba);

public sealed record PreviewRenderSpec(
    IReadOnlyList<string> Archives,
    string ModelPath,
    string? ClipPath,
    IReadOnlyList<PreviewView> Views,
    int Frames,
    int FramesPerSecond,
    int Width,
    int Height,
    string? EnvironmentMap,
    string? SceneMap = null,
    float OutlineThickness = 0.15f);

/// <summary>Frames per view (same order as the spec's views; frame i at <c>Times[i]</c>, i / fps wrapped at the clip's end;
/// one frame without a clip) and what the renderer resolved.</summary>
public sealed record RenderedPreview(
    IReadOnlyList<IReadOnlyList<PreviewImage>> Frames,
    IReadOnlyList<float> Times,
    float ClipSeconds,
    string Renderer,
    IReadOnlyList<PreviewPart> Parts);

public sealed record PreviewPart(string Mesh, string Material, string Shader, int Indices);

public sealed record ModelPreviewResult(string OutputDirectory, IReadOnlyList<string> Files, float ClipSeconds, int Frames, string Renderer);

/// <summary>Renders frames of a compiled model (adapter: OpenGL renderer).</summary>
public interface IModelPreviewRenderer
{
    RenderedPreview Render(PreviewRenderSpec spec, CancellationToken cancellationToken = default);
}

public interface IPreviewImageEncoder
{
    byte[] EncodePng(PreviewImage image);

    /// <summary>Animated PNG, looping; every frame the same size.</summary>
    byte[] EncodeAnimatedPng(IReadOnlyList<PreviewImage> frames, int framesPerSecond);
}

/// <summary>Renders an animated preview per view and one sheet (views across, sampled frames down).</summary>
public sealed class ModelPreviewApplication(IModelPreviewRenderer renderer, IPreviewImageEncoder encoder)
{
    public const int MaxFrames = 600;
    public const int MaxSize = 2048;

    public ModelPreviewResult Render(ModelPreviewRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        var output = Path.GetFullPath(request.OutputDirectory);
        var archives = request.Archives.Select(Path.GetFullPath).ToArray();
        var hashes = archives.ToDictionary(path => path, path => ContentHash.Compute(File.ReadAllBytes(path)).Value, StringComparer.OrdinalIgnoreCase);

        var rendered = renderer.Render(
            new PreviewRenderSpec(archives, request.ModelPath, request.ClipPath, request.Views, request.ClipPath is null ? 1 : request.Frames, request.FramesPerSecond, request.Width, request.Height, request.EnvironmentMap, request.SceneMap, request.OutlineThickness),
            cancellationToken);
        var seconds = rendered.ClipSeconds;
        var times = rendered.Times;
        if (rendered.Frames.Count != request.Views.Count || times.Count == 0 || rendered.Frames.Any(view => view.Count != times.Count))
        {
            throw Errors.Verification("PREVIEW_FRAME_COUNT", "The renderer returned a different number of frames than requested.", "Report this preview request.");
        }

        var parent = Path.GetDirectoryName(output) ?? throw Errors.Input("PREVIEW_OUTPUT_INVALID", $"'{output}' has no parent folder.", "Pass a folder path below an existing folder.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            var files = new List<string>();
            void Write(string name, byte[] bytes)
            {
                File.WriteAllBytes(Path.Combine(staging, name), bytes);
                files.Add(name);
            }

            for (var v = 0; v < request.Views.Count; v++)
            {
                var view = rendered.Frames[v];
                Write($"{request.Views[v].Name}.png", view.Count == 1 ? encoder.EncodePng(view[0]) : encoder.EncodeAnimatedPng(view, request.FramesPerSecond));
            }

            var rows = Math.Min(request.SheetRows, times.Count);
            var picks = Enumerable.Range(0, rows).Select(r => r * times.Count / rows).ToArray();
            Write("sheet.png", encoder.EncodePng(Sheet(rendered.Frames, picks)));
            var record = new
            {
                operation = "model_preview@1",
                model = request.ModelPath,
                clip = request.ClipPath,
                clipSeconds = seconds,
                framesPerSecond = request.FramesPerSecond,
                times,
                sheetFrames = picks.Select(i => times[i]).ToArray(),
                views = request.Views,
                width = request.Width,
                height = request.Height,
                environmentMap = request.SceneMap is null ? request.EnvironmentMap : null,
                scene = request.SceneMap,
                outlineThickness = request.OutlineThickness,
                archives = archives.Select(path => new { path, sha256 = hashes[path] }).ToArray(),
                renderer = rendered.Renderer,
                parts = rendered.Parts,
                proof = "offline preview; not a game frame",
            };
            Write("preview.json", JsonSerializer.SerializeToUtf8Bytes(record, JsonDefaults.Options));
            Directory.Move(staging, output);
            return new ModelPreviewResult(output, files, seconds, times.Count, rendered.Renderer);
        }
        catch
        {
            Directory.Delete(staging, true);
            throw;
        }
    }

    private static void Validate(ModelPreviewRequest request)
    {
        if (request.Archives.Count == 0)
        {
            throw Errors.Input("PREVIEW_ARCHIVES_REQUIRED", "No archive to load the model from.", "Pass --game <game/citadel folder> and any --addon VPKs.");
        }

        foreach (var archive in request.Archives.Where(path => !File.Exists(path)))
        {
            throw Errors.Input("PREVIEW_ARCHIVE_MISSING", $"'{archive}' does not exist.", "Pass existing *_dir.vpk files.");
        }

        if (request.Views.Count == 0 || request.Views.Select(v => v.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Views.Count
            || request.Views.Any(v => v.Zoom is < 0.25f or > 10f || v.Height is < 0f or > 1f)
            || request.Views.Any(v => v.Name.Length == 0 || v.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || v.Name.Equals("sheet", StringComparison.OrdinalIgnoreCase)))
        {
            throw Errors.Input("PREVIEW_VIEWS_INVALID", "Views must be one or more distinct file-name-safe names (not 'sheet'), zoom 0.25-10, height 0-1.", "Example: --view front=0 --view side=90 --view chest=0,5,3,0.7");
        }

        if (request.Frames is < 0 or > MaxFrames || request.FramesPerSecond is < 1 or > 60 || request.SheetRows is < 1 or > 16)
        {
            throw Errors.Input("PREVIEW_FRAMES_INVALID", string.Create(CultureInfo.InvariantCulture, $"Frames must be 0 (one loop of the clip) to {MaxFrames}, frames per second 1-60, sheet rows 1-16."), "Lower --frames, --fps or --sheet-rows.");
        }

        if (request.OutlineThickness is < 0 or > 5 || float.IsNaN(request.OutlineThickness))
        {
            throw Errors.Input("PREVIEW_OUTLINE_INVALID", "Outline thickness must be 0-5 model units.", "Example: --outline 0.15 (0 turns it off).");
        }

        if (request.Width is < 16 or > MaxSize || request.Height is < 16 or > MaxSize)
        {
            throw Errors.Input("PREVIEW_SIZE_INVALID", string.Create(CultureInfo.InvariantCulture, $"Width and height must be 16-{MaxSize}."), "Example: --size 480x720");
        }

        var output = Path.GetFullPath(request.OutputDirectory);
        if (File.Exists(output) || Directory.Exists(output))
        {
            throw Errors.Verification("PREVIEW_OUTPUT_EXISTS", $"'{output}' already exists.", "Choose a new output folder; existing files are never overwritten.");
        }
    }

    /// <summary>Views across, the picked frames down.</summary>
    public static PreviewImage Sheet(IReadOnlyList<IReadOnlyList<PreviewImage>> frames, IReadOnlyList<int> picks)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(picks);
        var w = frames[0][0].Width;
        var h = frames[0][0].Height;
        var sheet = new byte[w * frames.Count * h * picks.Count * 4];
        var stride = w * frames.Count * 4;
        for (var c = 0; c < frames.Count; c++)
        {
            for (var r = 0; r < picks.Count; r++)
            {
                var image = frames[c][picks[r]];
                for (var y = 0; y < h; y++)
                {
                    Buffer.BlockCopy(image.Rgba, y * w * 4, sheet, ((r * h) + y) * stride + c * w * 4, w * 4);
                }
            }
        }

        return new PreviewImage(w * frames.Count, h * picks.Count, sheet);
    }
}
