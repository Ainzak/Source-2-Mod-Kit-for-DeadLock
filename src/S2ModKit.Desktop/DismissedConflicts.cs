using System.Text.Json;

namespace S2ModKit.Desktop;

/// <summary>A conflict the user marked as fine: the two mods (lasting identities, see <see cref="ModProfiles.IdentityOf"/>) and how many
/// files they shared then.</summary>
internal sealed record DismissedConflict(string A, string B, int Shared);

/// <summary>
/// Conflicts marked as fine (<c>conflicts-ok.json</c> in the data folder, <c>dismissed-conflicts@1</c>): such a pair no longer shows in the
/// mod's warning, badges or panel, until the two mods share more files than when it was marked. Settings can show them all again.
/// </summary>
internal sealed class DismissedConflicts(string path)
{
    private const string Schema = "dismissed-conflicts@1";

    public IReadOnlyList<DismissedConflict> Load()
    {
        try
        {
            return File.Exists(path) && JsonSerializer.Deserialize<Document>(File.ReadAllText(path)) is { Schema: Schema } document ? document.Pairs : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public void Dismiss(ModEntry mod, ModEntry other, int shared)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(other);
        var (a, b) = Order(ModProfiles.IdentityOf(mod), ModProfiles.IdentityOf(other));
        Write([.. Load().Where(pair => pair.A != a || pair.B != b), new DismissedConflict(a, b, shared)]);
    }

    public void Clear() => Write([]);

    /// <summary>The mods' conflicts without the pairs marked as fine (a pair returns when it shares more files than when it was marked).</summary>
    public static IReadOnlyList<ModEntry> Apply(IReadOnlyList<ModEntry> mods, IReadOnlyList<DismissedConflict> dismissed)
    {
        ArgumentNullException.ThrowIfNull(mods);
        if (dismissed.Count == 0)
        {
            return mods;
        }

        var byKey = mods.ToDictionary(mod => mod.Key, StringComparer.Ordinal);
        bool Hidden(ModEntry mod, ConflictWith with)
        {
            if (!byKey.TryGetValue(with.OtherKey, out var other))
            {
                return false;
            }

            var (a, b) = Order(ModProfiles.IdentityOf(mod), ModProfiles.IdentityOf(other));
            return dismissed.Any(pair => pair.A == a && pair.B == b && with.Paths.Count <= pair.Shared);
        }

        return [.. mods.Select(mod => mod.Conflicts is { } conflicts && conflicts.With.Any(with => Hidden(mod, with))
            ? mod with { Conflicts = conflicts with { With = [.. conflicts.With.Where(with => !Hidden(mod, with))] } }
            : mod)];
    }

    private static (string, string) Order(string x, string y) => string.CompareOrdinal(x, y) <= 0 ? (x, y) : (y, x);

    private void Write(IReadOnlyList<DismissedConflict> pairs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Document(Schema, pairs)));
        File.Move(temporary, path, overwrite: true);
    }

    private sealed record Document(string Schema, IReadOnlyList<DismissedConflict> Pairs);
}
