using System.Text.Json;

namespace S2ModKit.Desktop;

/// <summary>A file Deadlock Mod Manager's compatibility layer patched from a mod: the mod's id and the patched game path.</summary>
internal sealed record ManagerRepair(string ModId, string FilePath);

/// <summary>
/// Deadlock Mod Manager's compatibility layer (<c>citadel/dmm_localization_default/pak01_dir.vpk</c>, described by
/// <c>compatibility.json</c> beside it): patched copies of files from mods (for example a mod's model with fixed sound tags). The game
/// mounts that folder before <c>addons</c>, so a patched model stays in game after its mod is turned off here, without the mod's
/// materials: the hero shows as a red wireframe until Mod Manager rebuilds the layer. Only read; never written.
/// </summary>
internal static class ManagerCompatibility
{
    /// <summary>The layer's patched files (empty when there is no layer or it cannot be read).</summary>
    public static IReadOnlyList<ManagerRepair> Read(string game)
    {
        var path = Path.Combine(game, "citadel", "dmm_localization_default", "compatibility.json");
        try
        {
            if (!File.Exists(path) || !File.Exists(Path.Combine(game, "citadel", "dmm_localization_default", "pak01_dir.vpk")))
            {
                return [];
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("repairs", out var repairs) && repairs.ValueKind == JsonValueKind.Array
                ? [.. repairs.EnumerateArray()
                    .Where(item => item.TryGetProperty("modId", out _) && item.TryGetProperty("filePath", out _))
                    .Select(item => new ManagerRepair(item.GetProperty("modId").ToString(), item.GetProperty("filePath").GetString() ?? string.Empty))]
                : [];
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            AppLog.Error("Reading Mod Manager's compatibility layer", exception);
            return [];
        }
    }

    /// <summary>The mods whose files the layer still patches although they are no longer enabled (a warning for the window, or null).</summary>
    public static string? StaleWarning(IReadOnlyList<ManagerRepair> repairs, IReadOnlyList<ModEntry> mods)
    {
        var stale = repairs.GroupBy(repair => repair.ModId, StringComparer.Ordinal)
            .Select(group => (Mod: mods.FirstOrDefault(mod => mod.ModId == group.Key), Files: group.Select(repair => repair.FilePath).ToArray()))
            .Where(item => item.Mod is null || !item.Mod.Loaded)
            .ToArray();
        if (stale.Length == 0)
        {
            return null;
        }

        var names = stale.Select(item => item.Mod is { } mod ? $"'{mod.Name}'" : "a removed mod").Distinct().ToArray();
        var models = stale.SelectMany(item => item.Files).Where(file => file.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase)).Select(Path.GetFileNameWithoutExtension).Distinct().ToArray();
        return $"Deadlock Mod Manager's compatibility fixes still contain files of {string.Join(", ", names)}, which {(names.Length == 1 ? "is" : "are")} turned off"
            + (models.Length > 0 ? $": the {string.Join(", ", models)} model{(models.Length == 1 ? string.Empty : "s")} may show as a red wireframe in game" : string.Empty)
            + ". Open Deadlock Mod Manager once so it rebuilds them.";
    }
}
