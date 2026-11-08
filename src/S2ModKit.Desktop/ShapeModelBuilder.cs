using System.Text.RegularExpressions;
using S2ModKit.Adapters.Source2;
using S2ModKit.Adapters.Vpk;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Infrastructure;
using S2ModKit.Reporting;
using SteamDatabase.ValvePak;

namespace S2ModKit.Desktop;

/// <summary>One reshaped part for a build: its material (the CLI's material-group component), the change, the part's bind-pose box
/// (the pivot the preview used), the largest vertex movement the preview measured and what the checked operations allow.</summary>
internal sealed record PartShapeEdit(int Part, string Material, PartShape Shape, System.Numerics.Vector3 BoundsMin, System.Numerics.Vector3 BoundsMax, float Displacement, ShapeLevel Level);

/// <summary>
/// Builds a reshaped model through the CLI's own typed, versioned operations, one part per round (the checked rewriter takes one
/// transform operation per build): the model is imported into a project (the game's archive directly; a mod's files from a copy
/// of its archive, with the game files it needs), the part becomes one scaffolded operation (`transform_component@4` for per-axis
/// size, rotation and a move around the preview's pivot; `@1` for an even size and a move), and the recipe is planned, built and
/// verified exactly as `s2mod build` and `s2mod verify` do. The next round imports the verified result. Returns the last verified
/// model's bytes; every round's project (recipe, plan, evidence) stays in the work folder.
/// </summary>
internal static partial class ShapeModelBuilder
{
    private const int MaximumDependencies = 400;
    private static readonly string[] GameRoots = ["citadel", "core"];

    public static async Task<(byte[] Model, string WorkFolder)> BuildAsync(AppSettings settings, ModEntry? mod, string modelPath,
        IReadOnlyList<PartShapeEdit> shapes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(shapes);
        if (shapes.Count == 0)
        {
            throw Errors.Input("SHAPE_NOTHING", "No part is reshaped.", "Reshape a part first.");
        }

        var logical = (modelPath.EndsWith("_c", StringComparison.Ordinal) ? modelPath : modelPath + "_c").Replace('\\', '/');
        var work = Path.Combine(settings.Data, "shape-work", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
        var owned = Path.Combine(work, "owned");                          // the model (and a mod's other files) each round imports
        var runtime = Path.Combine(work, "runtime");                      // game files the import asks for, shared by all rounds
        Directory.CreateDirectory(runtime);
        var workspace = new FileSystemProjectWorkspace();
        var adapter = new Source2CompiledModelAdapter(ShapeCapabilities.CodecPath());
        var application = new S2ModKitApplication(workspace, adapter, adapter, adapter, new SkippedExternalVerifier(), new EvidenceReportRenderer(), new SystemClock(),
            new DirectoryProjectResourceSourceFactory(), new VpkProjectResourceSourceFactory(), adapter, adapter, new AtomicRecipeDocumentWriter());
        if (mod is not null)
        {
            await CopyModAsync(mod, owned, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(Path.Combine(owned, logical)))
            {
                throw Errors.Input("SHAPE_MODEL_MISSING", $"'{logical}' is not in '{mod.Name}'.", "Choose the model the mod ships.");
            }
        }

        byte[]? model = null;
        var round = 0;
        foreach (var edit in shapes.OrderBy(edit => edit.Part))
        {
            round++;
            var project = Path.Combine(work, $"round-{round}-part-{edit.Part + 1}");
            if (mod is null && model is null)
            {
                await application.CreateVpkProjectAsync(project, Path.Combine(settings.Game, "citadel", "pak01_dir.vpk"), logical, null, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (model is not null)
                {
                    var target = Path.Combine(owned, logical);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await File.WriteAllBytesAsync(target, model, cancellationToken).ConfigureAwait(false);   // the previous round's verified result
                }

                await ImportFolderAsync(application, settings, Path.Combine(owned, logical), owned, runtime, project, cancellationToken).ConfigureAwait(false);
            }

            var recipe = await ScaffoldAsync(application, project, edit, Path.Combine(work, "recipes", $"round-{round}-part-{edit.Part + 1}.json"), cancellationToken).ConfigureAwait(false);
            await application.PlanAsync(project, recipe, cancellationToken).ConfigureAwait(false);
            var built = await application.BuildAsync(project, recipe, cancellationToken).ConfigureAwait(false);
            await application.VerifyAsync(project, built.Build.BuildId, cancellationToken).ConfigureAwait(false);
            var (_, content) = await workspace.LoadBuildAsync(project, built.Build.BuildId, cancellationToken).ConfigureAwait(false);
            model = content.Bytes.ToArray();
        }

        return (model!, work);
    }

    /// <summary>One part as one checked operation, scaffolded (and dry-run planned) by the CLI's own scaffolder.</summary>
    private static async Task<RecipeDocument> ScaffoldAsync(S2ModKitApplication application, string project, PartShapeEdit edit, string recipePath, CancellationToken cancellationToken)
    {
        var discovery = await application.DiscoverComponentsAsync(project, cancellationToken).ConfigureAwait(false);
        var candidate = discovery.Candidates.OfType<MaterialGroupComponentCandidateV2>()
            .FirstOrDefault(item => ShapeCapabilities.Normalize(item.MaterialPath) == ShapeCapabilities.Normalize(edit.Material))
            ?? throw Errors.Unsupported("SHAPE_PART_NOT_FOUND", $"Part {edit.Part + 1} ({Path.GetFileName(edit.Material)}) is not a component the checked tools know.", "Reset this part's shape.");
        var cap = (edit.Displacement * 1.1f) + 1f;                          // the preview's largest movement, with room for other levels of detail
        if (cap > 256f)
        {
            throw Errors.Unsupported("SHAPE_TOO_LARGE", $"Part {edit.Part + 1} moves more than 256 units.", "Use a smaller size, move or rotation.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(recipePath)!);
        var shape = edit.Shape;
        RecipeScaffoldRequest request;
        if (edit.Level == ShapeLevel.Full)
        {
            var pivot = shape.PivotPoint(edit.BoundsMin, edit.BoundsMax);
            var rotation = shape.AxisAngle() is { } turn
                ? new TransformRotation { Kind = "axis_angle", Axis = new TransformVector3 { X = turn.Axis.X, Y = turn.Axis.Y, Z = turn.Axis.Z }, Degrees = turn.Degrees }
                : new TransformRotation { Kind = "identity" };
            request = new RecipeScaffoldRequest([candidate.CandidateId], RecipeScaffoldContract.AffineIntent, recipePath,
                TranslationX: shape.Move.X, TranslationY: shape.Move.Y, TranslationZ: shape.Move.Z, MaximumVertexDisplacement: cap,
                Affine: new AffineScaffoldOptions(
                    new TransformVector3 { X = shape.Scale.X, Y = shape.Scale.Y, Z = shape.Scale.Z },
                    rotation,
                    new TransformPivot { Kind = "explicit_point", Point = new TransformVector3 { X = pivot.X, Y = pivot.Y, Z = pivot.Z } },
                    new TransformFrame { Kind = "model" }));
        }
        else if (edit.Level == ShapeLevel.EvenSizeAndMove)
        {
            if (shape.Scale.X != shape.Scale.Y || shape.Scale.Y != shape.Scale.Z || shape.RotationDegrees != System.Numerics.Vector3.Zero || shape.Pivot != ShapePivot.Centre)
            {
                throw Errors.Unsupported("SHAPE_LEVEL_EXCEEDED", $"Part {edit.Part + 1} allows only an even size and a move.", "Keep proportions, no rotation, around the centre.");
            }

            request = new RecipeScaffoldRequest([candidate.CandidateId], RecipeScaffoldContract.UniformScaleIntent, recipePath,
                UniformScale: shape.Scale.X, TranslationX: shape.Move.X, TranslationY: shape.Move.Y, TranslationZ: shape.Move.Z,
                ReferenceLod: 0, MaximumVertexDisplacement: cap);
        }
        else
        {
            throw Errors.Unsupported("SHAPE_LEVEL_UNSUPPORTED", $"Part {edit.Part + 1} cannot be built with the checked operations.", "Reset this part's shape.");
        }

        return (await application.ScaffoldRecipeAsync(project, request, cancellationToken).ConfigureAwait(false)).Recipe;
    }

    /// <summary>Copies a mod's files out of its archive (files in the archive root, such as README.txt, are not game resources).</summary>
    private static async Task CopyModAsync(ModEntry mod, string owned, CancellationToken cancellationToken)
    {
        using var archive = new Package();
        archive.Read(mod.Files[0]);
        foreach (var entry in archive.Entries!.SelectMany(group => group.Value))
        {
            var path = entry.GetFullPath().Replace('\\', '/');
            if (!path.Contains('/', StringComparison.Ordinal))
            {
                continue;
            }

            archive.ReadEntry(entry, out var bytes);
            var target = Path.Combine(owned, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Imports <paramref name="input"/> from the <paramref name="owned"/> folder; every game file the import asks for is copied
    /// from the game's archives into <paramref name="runtime"/>, one at a time, until the import succeeds.</summary>
    private static async Task ImportFolderAsync(S2ModKitApplication application, AppSettings settings, string input, string owned, string runtime, string project, CancellationToken cancellationToken)
    {
        var games = GameRoots.Select(root => Path.Combine(settings.Game, root, "pak01_dir.vpk")).Where(File.Exists).Select(path =>
        {
            var package = new Package();
            package.Read(path);
            return package;
        }).ToArray();
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                if (Directory.Exists(project))
                {
                    Directory.Delete(project, recursive: true);             // only the project this build created
                }

                try
                {
                    await application.CreateProjectAsync(project, input, owned, [runtime], cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (S2ModKitException exception) when (exception.Error.Code == "RESOURCE_DEPENDENCY_UNRESOLVED" && attempt < MaximumDependencies
                    && MissingDependency().Match(exception.Error.Summary) is { Success: true } missing)
                {
                    var path = missing.Groups[1].Value.Replace('\\', '/');
                    var found = games.Select(package => (package, entry: package.FindEntry(path))).FirstOrDefault(item => item.entry is not null);
                    if (found.entry is null)
                    {
                        throw Errors.Unsupported("SHAPE_DEPENDENCY_MISSING", $"The model refers to '{path}', which neither the mod nor the game has.", "This model cannot be reshaped by the checked tools.");
                    }

                    found.package.ReadEntry(found.entry, out var bytes);
                    var target = Path.Combine(runtime, path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await File.WriteAllBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            foreach (var package in games)
            {
                package.Dispose();
            }
        }
    }

    [GeneratedRegex("references '(.+?)', which was not found", RegexOptions.CultureInvariant)]
    private static partial Regex MissingDependency();
}
