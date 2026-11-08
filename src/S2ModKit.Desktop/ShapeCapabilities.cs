using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;
using SteamDatabase.ValvePak;

namespace S2ModKit.Desktop;

/// <summary>What the CLI's verified operations allow for a part: nothing, an even resize, an even resize and a move, or the full
/// per-axis size, rotation and move.</summary>
internal enum ShapeLevel
{
    None,
    EvenSize,
    EvenSizeAndMove,
    Full,
}

/// <summary>One part's shape capability; <see cref="Reason"/> says in plain words why more is not possible.</summary>
internal sealed record PartShapeCapability(ShapeLevel Level, string Reason);

/// <summary>
/// Asks the CLI's component discovery (the same verified profiles `s2mod compatibility scan` reports) which parts of a model can
/// be reshaped: `transform_component@4` allows per-axis size, rotation and a move; `@1` an even size and a move; `@2` an even size
/// only. Parts are matched by material (a part is the material group of the same material in every level of detail). Read-only.
/// </summary>
internal static class ShapeCapabilities
{
    /// <summary>Capabilities by material path (lower case, with <c>.vmat</c>); every material maps to <see cref="ShapeLevel.None"/>
    /// with one shared reason when the whole model is refused.</summary>
    public static async Task<(IReadOnlyDictionary<string, PartShapeCapability> ByMaterial, string? ModelRefusal)> ReadAsync(
        IReadOnlyList<string> archives, string modelPath, CancellationToken cancellationToken = default)
    {
        var logical = (modelPath.EndsWith("_c", StringComparison.Ordinal) ? modelPath : modelPath + "_c").Replace('\\', '/');
        byte[]? bytes = null;
        foreach (var archive in archives.Where(File.Exists))
        {
            using var package = new Package();
            package.Read(archive);
            if (package.FindEntry(logical) is { } entry)
            {
                package.ReadEntry(entry, out bytes);
                break;
            }
        }

        if (bytes is null)
        {
            return (new Dictionary<string, PartShapeCapability>(), "The model file could not be found.");
        }

        var adapter = new Source2CompiledModelAdapter(CodecPath());
        var input = new ArtifactContent(logical, ContentHash.Compute(bytes), bytes);
        try
        {
            if (!adapter.CanInspect(input))
            {
                return (new Dictionary<string, PartShapeCapability>(), "This model's format cannot be read for reshaping.");
            }

            var model = await adapter.InspectAsync(input, cancellationToken).ConfigureAwait(false);
            var discovery = await new PreciseComponentDiscoveryService(adapter).DiscoverAsync(input, model, cancellationToken).ConfigureAwait(false);
            var result = new Dictionary<string, PartShapeCapability>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in discovery.Candidates.OfType<MaterialGroupComponentCandidateV2>())
            {
                result[Normalize(candidate.MaterialPath)] = candidate.Lods.FirstOrDefault(lod => lod.Lod == 0) is { DrawCallCount: > 1 }
                    ? new PartShapeCapability(ShapeLevel.None, "Several parts share this part's material; the checked tools would reshape them together, which the app does not offer yet.")
                    : Level(candidate.Capabilities);
            }

            return (result, null);
        }
        catch (S2ModKitException exception)
        {
            return (new Dictionary<string, PartShapeCapability>(), Plain(exception.Error.Code, exception.Error.Summary));
        }
    }

    /// <summary>The native mesh codec, found as the CLI finds it: <c>S2MODKIT_MESHOPTIMIZER_PATH</c>, else <c>meshoptimizer.dll</c> beside
    /// the app (the release ships it there).</summary>
    public static string? CodecPath()
    {
        var configured = Environment.GetEnvironmentVariable("S2MODKIT_MESHOPTIMIZER_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, "meshoptimizer.dll");
        return File.Exists(bundled) ? bundled : null;
    }

    public static string Normalize(string material)
    {
        var path = material.Replace('\\', '/').ToLowerInvariant();
        return path.EndsWith("_c", StringComparison.Ordinal) ? path[..^2] : path;
    }

    internal static PartShapeCapability Level(IReadOnlyList<ComponentCapability> capabilities)
    {
        var transforms = capabilities.Where(capability => capability.OperationKind == "transform_component").ToArray();
        bool Available(int version) => transforms.Any(capability => capability.OperationVersion == version && capability.Availability == CapabilityAvailability.Available);
        var level = Available(4) ? ShapeLevel.Full : Available(1) ? ShapeLevel.EvenSizeAndMove : Available(2) ? ShapeLevel.EvenSize : ShapeLevel.None;
        var refused = transforms.Where(capability => capability.Availability != CapabilityAvailability.Available)
            .OrderBy(capability => capability.OperationVersion == 4 && level != ShapeLevel.None ? 0 : capability.OperationVersion)   // the reason for what is missing
            .SelectMany(capability => capability.Reasons).FirstOrDefault();
        var reason = level switch
        {
            ShapeLevel.Full => "This part can be reshaped.",
            _ when refused is null => "This part cannot be reshaped.",
            _ => Plain(refused.Code, refused.Summary),
        };
        return new PartShapeCapability(level, level is ShapeLevel.Full or ShapeLevel.None ? reason : $"Only an even size{(level == ShapeLevel.EvenSizeAndMove ? " and a move" : string.Empty)}: {Lower(reason)}");
    }

    /// <summary>A refusal code in words a player understands; unknown codes keep the analyzer's own sentence.</summary>
    internal static string Plain(string code, string summary) => code switch
    {
        "TRANSFORM_SOURCE2_PROFILE_UNSUPPORTED" => "This part shares its internal mesh with other parts; only whole meshes can be reshaped for now.",
        "AFFINE_LAYOUT_UNSUPPORTED" or "TRANSFORM_GEOMETRY_UNAVAILABLE" when summary.Contains("weight", StringComparison.OrdinalIgnoreCase) || summary.Contains("influence", StringComparison.OrdinalIgnoreCase)
            => "This part bends with several bones (like hair or clothes); only parts that follow one bone can be reshaped for now.",
        "AFFINE_LAYOUT_UNSUPPORTED" => "This part's vertex layout is not one the checked tools can change yet.",
        "AFFINE_BOUNDS_UNSUPPORTED" => "The size data stored for this part does not match its shape exactly, so it is not changed for safety.",
        "TRANSFORM_GEOMETRY_LOD_COVERAGE_INCOMPLETE" or "COMPONENT_INCOMPLETE_LOD_COVERAGE" or "COMPONENT_LINEAGE_INCOMPLETE_LOD_COVERAGE"
            => "This part is missing from some levels of detail.",
        "SHARED_LOD_MESH_READ_ONLY" => "This part's mesh is reused by several levels of detail.",
        "ZERO_LOD_MESH_READ_ONLY" => "This part's mesh belongs to no level of detail.",
        "AFFINE_DISTANCE_FIELD_UNSUPPORTED" => "A bone of this part carries collision data that cannot be stretched.",
        "AFFINE_MULTI_BUFFER_OWNERSHIP_UNSUPPORTED" => "This part's vertices are spread over several buffers.",
        "AFFINE_PACKED_FRAME_UNSUPPORTED" => "This part's vertices are stored in a packed format the checked tools cannot change yet.",
        "LOD_MASK_UNSUPPORTED" => "This model has no levels of detail (common for exported mods); the checked tools cannot reshape it yet.",
        "RESOURCE_DEPENDENCY_UNRESOLVED" => "This model refers to a file that neither the mod nor the game has.",
        _ => summary,
    };

    private static string Lower(string text) => text.Length > 0 ? char.ToLowerInvariant(text[0]) + text[1..] : text;
}
