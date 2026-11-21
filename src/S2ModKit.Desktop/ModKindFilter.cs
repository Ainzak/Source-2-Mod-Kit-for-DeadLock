namespace S2ModKit.Desktop;

internal static class ModKindFilter
{
    public const string AllKinds = "All kinds";
    public const string NeedsAttention = "Flagged by Check mods";
    public const string WithUpdates = "New files on GameBanana";

    public static IReadOnlyList<string> Options { get; } =
        [AllKinds, "model", "animation", "sound", "ui", "particles", "material", "data", "localization", "other", NeedsAttention, WithUpdates];

    public static bool IsStatusFilter(string kind) => kind is NeedsAttention or WithUpdates;

    /// <summary>Match every kind a mod contains, including recognized hero tags such as UI prefabs.</summary>
    public static bool Matches(ModEntry mod, string kind, DoctorVerdict? verdict = null, bool hasUpdate = false) =>
        kind switch
        {
            AllKinds => true,
            NeedsAttention => verdict is DoctorVerdict.Look or DoctorVerdict.Repairable or DoctorVerdict.Broken,
            WithUpdates => hasUpdate,
            "other" => mod.MainKind == "other" && mod.HeroKinds?.Values.Any(tags => tags.Count > 0) != true,
            _ => mod.Kinds.ContainsKey(kind) || (mod.HeroKinds?.Values.Any(tags =>
                tags.Contains(kind, StringComparer.OrdinalIgnoreCase)) ?? false),
        };
}
