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

        var neighbour = ordered[other];
        var summary = direction < 0
            ? $"'{ordered[index].Mod.Label}' now loads before '{neighbour.Mod.Label}'"
            : $"'{ordered[index].Mod.Label}' now loads after '{neighbour.Mod.Label}'";
        return Move(ordered, index, other, allNames, tempTag, neighbour.Mod.Label, summary);
    }

    /// <summary>
    /// Moves a mod to the first place (<paramref name="first"/> true: lowest pak number, wins over every other mod) or the last. The
    /// mods between keep their order and shift by one; the slots of all of them are shared out again in the new order, so every
    /// other mod keeps its slot.
    /// </summary>
    public static AddonReorderPlan PlanToEnd(IReadOnlyList<LoadOrderMod> enabled, string key, bool first, IReadOnlyCollection<string> allNames, string tempTag)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(allNames);
        var ordered = enabled.Select(mod => (Mod: mod, Slots: SlotsOf(mod))).OrderBy(item => item.Slots.Min()).ThenBy(item => item.Mod.Key, StringComparer.Ordinal).ToArray();
        var index = Array.FindIndex(ordered, item => item.Mod.Key == key);
        if (index < 0)
        {
            throw Errors.Input("ADDON_REORDER_NOT_ENABLED", "Only an enabled mod has a place in the load order.", "Enable the mod first.");
        }

        var target = first ? 0 : ordered.Length - 1;
        if (target == index)
        {
            throw Errors.Verification("ADDON_REORDER_RANGE", first ? $"'{ordered[index].Mod.Label}' already loads first." : $"'{ordered[index].Mod.Label}' already loads last.", "Nothing to move.");
        }

        var summary = first ? $"'{ordered[index].Mod.Label}' now loads first (wins over every other mod)" : $"'{ordered[index].Mod.Label}' now loads last";
        return Move(ordered, index, target, allNames, tempTag, ordered[target].Mod.Label, summary);
    }

    /// <summary>Moves a mod to the place of <paramref name="targetKey"/> (drag and drop): the mods between keep their order and shift by one;
    /// the slots are shared out again, as in <see cref="PlanToEnd"/>.</summary>
    public static AddonReorderPlan PlanToPlaceOf(IReadOnlyList<LoadOrderMod> enabled, string key, string targetKey, IReadOnlyCollection<string> allNames, string tempTag)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(allNames);
        var ordered = enabled.Select(mod => (Mod: mod, Slots: SlotsOf(mod))).OrderBy(item => item.Slots.Min()).ThenBy(item => item.Mod.Key, StringComparer.Ordinal).ToArray();
        var index = Array.FindIndex(ordered, item => item.Mod.Key == key);
        var target = Array.FindIndex(ordered, item => item.Mod.Key == targetKey);
        if (index < 0 || target < 0)
        {
            throw Errors.Input("ADDON_REORDER_NOT_ENABLED", "Only enabled mods have a place in the load order.", "Enable both mods first.");
        }

        if (index == target)
        {
            throw Errors.Verification("ADDON_REORDER_RANGE", "The mod is already there.", "Drop it on another mod.");
        }

        var summary = target < index
            ? $"'{ordered[index].Mod.Label}' now loads before '{ordered[target].Mod.Label}'"
            : $"'{ordered[index].Mod.Label}' now loads after '{ordered[target].Mod.Label}'";
        return Move(ordered, index, target, allNames, tempTag, ordered[target].Mod.Label, summary);
    }

    private static AddonReorderPlan Move((LoadOrderMod Mod, int[] Slots)[] ordered, int index, int target, IReadOnlyCollection<string> allNames, string tempTag, string neighbourLabel, string summaryStart)
    {
        var moved = ordered[index];
        var order = ordered.ToList();
        order.RemoveAt(index);
        order.Insert(target, moved);                                       // the new order
        var (low, high) = (Math.Min(index, target), Math.Max(index, target));
        var union = ordered[low..(high + 1)].SelectMany(item => item.Slots).Order().ToArray();
        var net = new List<AddonRename>();
        var at = 0;
        foreach (var item in order.Skip(low).Take(high - low + 1))
        {
            Assign(item.Mod, item.Slots, union[at..(at + item.Slots.Length)], allNames, net);
            at += item.Slots.Length;
        }

        return Steps(moved.Mod.Label, neighbourLabel, summaryStart, net, allNames, tempTag);
    }

    /// <summary>
    /// Puts a mod (one archive) at pak number <paramref name="slot"/> (1-99). A free slot: its files are renamed there. A slot an enabled
    /// mod (one archive) holds: refused unless <paramref name="swap"/>, then the two exchange slots. Mods in several archives, files of
    /// unknown mods in the slot and slots outside 01-99 are refused.
    /// </summary>
    public static AddonReorderPlan PlanToSlot(IReadOnlyList<LoadOrderMod> enabled, string key, int slot, bool swap, IReadOnlyCollection<string> allNames, string tempTag)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(allNames);
        if (slot is < 1 or > 99)
        {
            throw Errors.Input("ADDON_SLOT_RANGE", $"pak{slot:00} is not a load order slot.", "Pick a number from 1 to 99.");
        }

        var mods = enabled.Select(mod => (Mod: mod, Slots: SlotsOf(mod))).ToArray();
        var moved = mods.FirstOrDefault(item => item.Mod.Key == key);
        if (moved.Mod is null)
        {
            throw Errors.Input("ADDON_REORDER_NOT_ENABLED", "Only an enabled mod has a place in the load order.", "Enable the mod first.");
        }

        if (moved.Slots.Length != 1)
        {
            throw Errors.Input("ADDON_SLOT_SPLIT", $"'{moved.Mod.Label}' is in {moved.Slots.Length} archives (slots {string.Join(", ", moved.Slots.Select(item => $"pak{item:00}"))}).", "Move it one place at a time with Load earlier or Load later.");
        }

        var from = moved.Slots[0];
        if (from == slot)
        {
            throw Errors.Verification("ADDON_REORDER_RANGE", $"'{moved.Mod.Label}' is already pak{slot:00}.", "Nothing to move.");
        }

        var holder = mods.FirstOrDefault(item => item.Slots.Contains(slot));
        var net = new List<AddonRename>();
        Assign(moved.Mod, moved.Slots, [slot], allNames, net);
        if (holder.Mod is null)
        {
            if (allNames.Any(name => name.Equals($"pak{slot:00}_dir.vpk", StringComparison.OrdinalIgnoreCase)))
            {
                throw Errors.Verification("ADDON_SLOT_TAKEN", $"pak{slot:00}_dir.vpk belongs to no enabled mod in the list.", "Refresh the list, or pick another number.");
            }

            return Steps(moved.Mod.Label, $"pak{slot:00}", $"'{moved.Mod.Label}' is now pak{slot:00}", net, allNames, tempTag);
        }

        if (holder.Slots.Length != 1)
        {
            throw Errors.Input("ADDON_SLOT_SPLIT", $"pak{slot:00} belongs to '{holder.Mod.Label}', which is in {holder.Slots.Length} archives.", "Pick another number.");
        }

        if (!swap)
        {
            throw Errors.Verification("ADDON_SLOT_TAKEN", $"pak{slot:00} belongs to '{holder.Mod.Label}'.", "Swap the two mods, or pick another number.");
        }

        Assign(holder.Mod, holder.Slots, [from], allNames, net);
        return Steps(moved.Mod.Label, holder.Mod.Label, $"'{moved.Mod.Label}' is now pak{slot:00} and '{holder.Mod.Label}' pak{from:00}", net, allNames, tempTag);
    }

    /// <summary>The renames that reach <paramref name="net"/> one by one: direct ones first, files that move into each other's names through
    /// a temporary name; never onto an existing file.</summary>
    private static AddonReorderPlan Steps(string movedLabel, string neighbourLabel, string summaryStart, List<AddonRename> net, IReadOnlyCollection<string> allNames, string tempTag)
    {
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
        return new AddonReorderPlan(movedLabel, neighbourLabel, $"{summaryStart} ({changed.Length} file(s) renamed).", changed, steps);
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
