using SteamDatabase.ValvePak;

namespace S2ModKit.Desktop;

/// <summary>Reads compiled effect files (<c>.vpcf</c> paths without <c>_c</c>) from archives in priority order: a mod's own copy wins
/// over the game's.</summary>
internal static class EffectFiles
{
    /// <summary>The bytes of each effect found, from the first archive that has it; effects in no archive are left out.</summary>
    public static Dictionary<string, byte[]> Read(IReadOnlyList<string> archives, IEnumerable<string> effects)
    {
        ArgumentNullException.ThrowIfNull(archives);
        var wanted = effects.Select(effect => effect.Replace('\\', '/').ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var found = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var archive in archives.Where(File.Exists))
        {
            if (wanted.Count == found.Count)
            {
                break;
            }

            using var package = new Package();
            package.Read(archive);
            foreach (var effect in wanted.Where(effect => !found.ContainsKey(effect)).ToArray())
            {
                if (package.FindEntry(effect + "_c") is { } entry)
                {
                    package.ReadEntry(entry, out var bytes);
                    found[effect] = bytes;
                }
            }
        }

        return found;
    }

    /// <summary>The distinct strong colours of some effects (greys left out), most frequent first, for swatches.</summary>
    public static IReadOnlyList<(int R, int G, int B)> Swatches(IEnumerable<(string Path, byte[] Bytes)> effects, int count)
    {
        var colours = new Dictionary<(int, int, int), int>();
        foreach (var (path, bytes) in effects)
        {
            try
            {
                foreach (var value in S2ModKit.Adapters.Source2.CompiledParticleEdit.ReadColors(bytes, path + "_c"))
                {
                    var (_, saturation, brightness) = S2ModKit.Application.EffectColors.ToHsv(value.R / 255f, value.G / 255f, value.B / 255f);
                    if (saturation >= S2ModKit.Application.EffectColors.GreyLimit && brightness > 0.15f)
                    {
                        var key = (value.R / 16 * 16, value.G / 16 * 16, value.B / 16 * 16);   // near-equal colours count once
                        colours[key] = colours.GetValueOrDefault(key) + 1;
                    }
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or S2ModKit.Domain.S2ModKitException or NotSupportedException)
            {
                AppLog.Error($"Effect colours of {path}", exception);
            }
        }

        return [.. colours.OrderByDescending(pair => pair.Value).Take(count).Select(pair => pair.Key)];
    }
}
