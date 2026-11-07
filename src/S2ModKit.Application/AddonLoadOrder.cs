using System.Globalization;
using System.Text.RegularExpressions;
using S2ModKit.Domain;

namespace S2ModKit.Application;

/// <summary>An enabled mod for load-order planning: a list key, a display label and its enabled archives (<c>pakNN_dir.vpk</c>).</summary>
public sealed record LoadOrderMod(string Key, string Label, IReadOnlyList<string> Archives);

/// <summary><see cref="Net"/> is the result (each file that changes name, old to new); <see cref="Steps"/> are the single renames that
/// reach it in a safe order (targets still taken go through <c>s2mk_tmp_</c> names).</summary>
public sealed record AddonReorderPlan(string Moved, string Neighbour, string Summary, IReadOnlyList<AddonRename> Net, IReadOnlyList<AddonRename> Steps);

/// <summary>
/// Moving a mod one place in the load order. Deadlock loads <c>pakNN_dir.vpk</c> and the lower number wins a shared path, so
/// "earlier" means a lower number. The mod and its neighbour (the next enabled mod by lowest slot, in the direction of the move)
/// exchange slots: together they own the same slots as before, split so that the first mod in the new order takes the lowest ones
/// (a mod with several archives keeps them in order, contiguous among the two). No other mod changes name, so the relative order
/// of all other enabled mods is untouched, and no slot leaves 01..99 because only slots already in use are reused.
/// Example: A = pak27, B = pak28, move B earlier: B -> pak27, A -> pak28, as pak27 -> tmp, pak28 -> pak27, tmp -> pak28.
/// A data chunk (<c>pakNN_000.vpk</c>) follows its <c>_dir</c> archive to the new number.
/// </summary>
public static partial class AddonLoadOrder
{
    public static AddonReorderPlan Plan(IReadOnlyList<LoadOrderMod> enabled, string key, int direction, IReadOnlyCollection<string> allNames, string tempTag)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(allNames);
        if (direction is not (-1 or 1))
        {
            throw Errors.Input("ADDON_REORDER_DIRECTION", "A mod moves one place earlier (-1) or later (1).", "Use the move up or move down command.");
        }

        var ordered = enabled.Select(mod => (Mod: mod, Slots: SlotsOf(mod))).OrderBy(item => item.Slots.Min()).ThenBy(item => item.Mod.Key, StringComparer.Ordinal).ToArray();
        var index = Array.FindIndex(ordered, item => item.Mod.Key == key);
        if (index < 0)
        {
            throw Errors.Input("ADDON_REORDER_NOT_ENABLED", "Only an enabled mod has a place in the load order.", "Enable the mod first.");
        }

        var other = index + direction;
        if (other < 0 || other >= ordered.Length)
        {
            throw Errors.Verification("ADDON_REORDER_RANGE", direction < 0 ? $"'{ordered[index].Mod.Label}' already loads first." : $"'{ordered[index].Mod.Label}' already loads last.",
                direction < 0 ? "There is no lower slot to move to." : "There is no mod after it to move behind.");
        }

        var moved = ordered[index];
        var neighbour = ordered[other];
        var (first, second) = direction < 0 ? (moved, neighbour) : (neighbour, moved);   // the new order
        var union = first.Slots.Concat(second.Slots).Order().ToArray();
        var net = new List<AddonRename>();
        Assign(first.Mod, first.Slots, union[..first.Slots.Length], allNames, net);
        Assign(second.Mod, second.Slots, union[first.Slots.Length..], allNames, net);
        var changed = net.Where(item => !item.From.Equals(item.To, StringComparison.OrdinalIgnoreCase)).ToArray();

        var names = new HashSet<string>(allNames, StringComparer.OrdinalIgnoreCase);
        var sources = changed.Select(item => item.From).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var direct = changed.Where(item => !sources.Contains(item.To)).ToArray();
        var cyclic = changed.Where(item => sources.Contains(item.To)).ToArray();
        var clash = direct.FirstOrDefault(item => names.Contains(item.To));
        if (clash is not null)
        {
            throw Errors.Verification("ADDON_RENAME_TARGET_EXISTS", $"'{clash.To}' already exists.", "S2ModKit never overwrites an existing file.");
        }

        var temporary = cyclic.Select(item => new AddonRename(item.From, $"s2mk_tmp_{tempTag}_{item.From}")).ToArray();
        if (temporary.FirstOrDefault(item => names.Contains(item.To)) is { } taken)
        {
            throw Errors.Verification("ADDON_RENAME_TARGET_EXISTS", $"'{taken.To}' already exists.", "S2ModKit never overwrites an existing file.");
        }

        AddonRename[] steps = [.. direct, .. temporary, .. cyclic.Select((item, i) => new AddonRename(temporary[i].To, item.To))];
        var summary = direction < 0
            ? $"'{moved.Mod.Label}' now loads before '{neighbour.Mod.Label}' ({changed.Length} file(s) renamed)."
            : $"'{moved.Mod.Label}' now loads after '{neighbour.Mod.Label}' ({changed.Length} file(s) renamed).";
        return new AddonReorderPlan(moved.Mod.Label, neighbour.Mod.Label, summary, changed, steps);
    }

    private static void Assign(LoadOrderMod mod, int[] oldSlots, int[] newSlots, IReadOnlyCollection<string> allNames, List<AddonRename> net)
    {
        var archives = mod.Archives.OrderBy(Slot).ToArray();
        for (var i = 0; i < archives.Length; i++)
        {
            if (oldSlots[i] == newSlots[i])
            {
                continue;
            }

            net.Add(new AddonRename(archives[i], $"pak{newSlots[i]:00}_dir.vpk"));
            foreach (var chunk in allNames.Where(name => PakChunk().Match(name) is { Success: true } match && int.Parse(match.Groups["slot"].ValueSpan, CultureInfo.InvariantCulture) == oldSlots[i]).Order(StringComparer.OrdinalIgnoreCase))
            {
                net.Add(new AddonRename(chunk, $"pak{newSlots[i]:00}_{PakChunk().Match(chunk).Groups["n"].Value}.vpk"));
            }
        }
    }

    private static int[] SlotsOf(LoadOrderMod mod)
    {
        if (mod.Archives.Count == 0)
        {
            throw Errors.Input("ADDON_REORDER_NOT_ENABLED", $"'{mod.Label}' has no enabled archive.", "Only an enabled mod has a place in the load order.");
        }

        return [.. mod.Archives.Select(Slot).Order()];
    }

    private static int Slot(string archive) =>
        AddonFileApplication.TryParseSlot(archive) ?? throw Errors.Input("ADDON_NOT_ENABLED", $"'{archive}' is not an enabled archive (pakNN_dir.vpk).", "Only a loaded archive has a place in the load order.");

    [GeneratedRegex(@"^pak(?<slot>\d\d)_(?<n>\d{3})\.vpk$", RegexOptions.IgnoreCase)]
    private static partial Regex PakChunk();
}
