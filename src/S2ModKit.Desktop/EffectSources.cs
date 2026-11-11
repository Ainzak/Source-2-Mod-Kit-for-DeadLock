using System.Security.Cryptography;
using System.Text;
using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;
using SteamDatabase.ValvePak;

namespace S2ModKit.Desktop;

/// <summary>
/// Where an added effect comes from when it is not the game's own: a mod's archive (<c>.vpk</c>) or a loose compiled effect file
/// (<c>.vpcf_c</c>; its other files are looked for beside it, under the folder that holds its <c>particles</c> folder).
/// </summary>
internal sealed class EffectSource : IDisposable
{
    private readonly Package? package;
    private readonly string? root;
    private readonly string? single;

    private EffectSource(Package? package, string? root, string? single, string? top)
    {
        this.package = package;
        this.root = root;
        this.single = single;
        Top = top;
    }

    /// <summary>For a loose file: the effect's path as the game would know it (<c>particles/...vpcf</c>); null for an archive.</summary>
    public string? Top { get; }

    public static EffectSource Open(string path)
    {
        if (path.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
        {
            var package = new Package();
            try
            {
                package.Read(path);
                return new EffectSource(package, null, null, null);
            }
            catch
            {
                package.Dispose();
                throw;
            }
        }

        if (!path.EndsWith(".vpcf_c", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            throw Errors.Input("ADDED_EFFECT_SOURCE", $"'{path}' is not a mod archive or a compiled effect file (.vpcf_c) that exists.", "Choose the effect again.");
        }

        var full = Path.GetFullPath(path).Replace('\\', '/');
        var at = full.LastIndexOf("/particles/", StringComparison.OrdinalIgnoreCase);
#pragma warning disable CA1308 // game paths are lower case
        return at >= 0
            ? new EffectSource(null, full[..at], null, full[(at + 1)..^2].ToLowerInvariant())
            : new EffectSource(null, null, full, "particles/loose/" + Path.GetFileName(full)[..^2].ToLowerInvariant());
#pragma warning restore CA1308
    }

    /// <summary>A compiled file of the source (a game path with <c>_c</c>), or null.</summary>
    public byte[]? Read(string compiledPath)
    {
        if (package is not null)
        {
            if (package.FindEntry(compiledPath) is not { } entry)
            {
                return null;
            }

            package.ReadEntry(entry, out var bytes);
            return bytes;
        }

        var file = root is not null ? Path.Combine(root, compiledPath) : string.Equals(compiledPath, Top + "_c", StringComparison.OrdinalIgnoreCase) ? single! : null;
        return file is not null && File.Exists(file) ? File.ReadAllBytes(file) : null;
    }

    public void Dispose() => package?.Dispose();
}

/// <summary>An effect taken from a source: its effect files the source ships (the effect itself and child effects, any depth) and
/// the other files those refer to directly that the source also ships (textures, materials, models). Paths without <c>_c</c>.</summary>
internal sealed record EffectClosure(string Effect, IReadOnlyDictionary<string, byte[]> Effects, IReadOnlyDictionary<string, byte[]> Extras)
{
    private const int Limit = 400;

    public static EffectClosure Read(string sourcePath, string? effect)
    {
        using var source = EffectSource.Open(sourcePath);
#pragma warning disable CA1308 // game paths are lower case
        var top = (effect ?? source.Top ?? throw Errors.Input("ADDED_EFFECT_SOURCE", "No effect was chosen from the mod.", "Choose the effect again.")).Replace('\\', '/').ToLowerInvariant();
        var effects = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var extras = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var queue = new Queue<string>([top]);
        while (queue.TryDequeue(out var next) && effects.Count < Limit)
        {
            if (effects.ContainsKey(next) || source.Read(next + "_c") is not { } bytes)
            {
                continue;                                                   // not shipped by the source: the game's own file is used
            }

            effects[next] = bytes;
            foreach (var reference in CompiledAddedEffects.References(bytes, next + "_c").Select(name => name.Replace('\\', '/').ToLowerInvariant()))
            {
                if (reference.EndsWith(".vpcf", StringComparison.Ordinal))
                {
                    queue.Enqueue(reference);
                }
                else if (!extras.ContainsKey(reference) && source.Read(reference + "_c") is { } extra)
                {
                    extras[reference] = extra;
                }
            }
        }
#pragma warning restore CA1308

        return effects.ContainsKey(top)
            ? new EffectClosure(top, effects, extras)
            : throw Errors.Input("ADDED_EFFECT_MISSING", $"The effect '{top}' is not in '{Path.GetFileName(sourcePath)}'.", "Choose the effect again; the mod may have been updated or removed.");
    }

    /// <summary>
    /// The files to put into a built mod (compiled paths) and the effect's new path. Effect files get new names under
    /// <paramref name="folder"/> so the copy never replaces the game's or another mod's effect of the same path; other files
    /// keep their path when the game has no file there (<paramref name="inGame"/>) and get a new name when they would replace one.
    /// Files those other files refer to (a material's textures) are not followed: the game's are used.
    /// </summary>
    public (string Effect, Dictionary<string, byte[]> Files) Copy(string folder, Func<string, bool> inGame)
    {
        ArgumentNullException.ThrowIfNull(inGame);
        var tag = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Effect + "\n" + string.Concat(Effects.Values.Select(bytes => bytes.Length)))))[..8];
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.Ordinal);
        string Renamed(string path)
        {
            var name = Path.GetFileName(path);
            for (var i = 2; !used.Add(name); i++)
            {
                name = $"{Path.GetFileNameWithoutExtension(path)}_{i}{Path.GetExtension(path)}";
            }

            return $"{folder}{tag}/{name}";
        }

        foreach (var path in Effects.Keys.Order(StringComparer.Ordinal))
        {
            renames[path] = Renamed(path);
        }

        foreach (var path in Extras.Keys.Order(StringComparer.Ordinal).Where(path => inGame(path + "_c")))
        {
            renames[path] = Renamed(path);
        }

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (path, bytes) in Effects)
        {
            files[renames[path] + "_c"] = CompiledAddedEffects.Rename(bytes, path + "_c", renames);
        }

        foreach (var (path, bytes) in Extras)
        {
            files[renames.GetValueOrDefault(path, path) + "_c"] = bytes;
        }

        return (renames[Effect], files);
    }
}
