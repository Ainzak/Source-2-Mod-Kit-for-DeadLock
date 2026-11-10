using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;
using SteamDatabase.ValvePak;

namespace S2ModKit.Desktop;

/// <summary>What a part allows: nothing, or the full per-axis size, rotation and move. (Recipes written before the direct reshape may
/// also hold the even-size levels the CLI operations used.)</summary>
internal enum ShapeLevel
{
    None,
    EvenSize,
    EvenSizeAndMove,
    Full,
}

/// <summary>One part's shape capability; <see cref="Reason"/> says in plain words why it cannot be reshaped.</summary>
internal sealed record PartShapeCapability(ShapeLevel Level, string Reason);

/// <summary>
/// Which parts of a model the direct reshape (<see cref="CompiledPartReshape"/>, <c>shape_part@1</c>) can change: every level-0 part
/// whose own vertices it can rewrite in every level of detail. Read-only.
/// </summary>
internal static class ShapeCapabilities
{
    public static readonly PartShapeCapability Allowed = new(ShapeLevel.Full, "This part can be reshaped.");

    /// <summary>The parts that cannot be reshaped, by part index, with their reason; a reason for the whole model when it is refused.</summary>
    public static Task<(IReadOnlyDictionary<int, PartShapeCapability> Refused, string? ModelRefusal)> ReadAsync(
        IReadOnlyList<string> archives, string modelPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
            return Task.FromResult<(IReadOnlyDictionary<int, PartShapeCapability>, string?)>((new Dictionary<int, PartShapeCapability>(), "The model file could not be found."));
        }

        try
        {
            var refused = CompiledPartReshape.Refusals(bytes, logical).ToDictionary(pair => pair.Key, pair => new PartShapeCapability(ShapeLevel.None, pair.Value));
            return Task.FromResult<(IReadOnlyDictionary<int, PartShapeCapability>, string?)>((refused, CodecPath() is null ? "The mesh codec (meshoptimizer.dll) is missing beside the app; reinstall it to reshape parts." : null));
        }
        catch (S2ModKitException exception)
        {
            return Task.FromResult<(IReadOnlyDictionary<int, PartShapeCapability>, string?)>((new Dictionary<int, PartShapeCapability>(), exception.Error.Summary));
        }
    }

    /// <summary>The native mesh codec: <c>S2MODKIT_MESHOPTIMIZER_PATH</c>, else <c>meshoptimizer.dll</c> beside the app (the release
    /// ships it there).</summary>
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
}
