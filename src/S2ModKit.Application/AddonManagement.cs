using System.Text.RegularExpressions;
using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed record AddonFileStat(string FileName, string FullPath, long Size, DateTimeOffset LastWriteUtc, bool IsReparsePoint);

/// <summary>One file of an operation. <see cref="To"/> is null for a delete (the file goes to the Recycle Bin). Identity is
/// size plus last write time, not a hash: a rename keeps both, and hashing a multi-hundred-MB archive on every toggle would
/// cost a full read for no extra safety.</summary>
public sealed record AddonJournalEntry(string From, string? To, long Size, DateTimeOffset LastWriteUtc);

/// <summary>The journal of one addon operation (<c>addon-rename@1</c>, <c>addon-reorder@1</c> or <c>addon-delete@1</c>), written before the files are
/// touched (result <c>started</c>) and finished after verification.</summary>
public sealed record AddonOperationJournal
{
    public const string RenameOperation = "addon-rename@1";

    public const string DeleteOperation = "addon-delete@1";

    public const string ReorderOperation = "addon-reorder@1";

    public string Operation { get; init; } = RenameOperation;

    public string Id { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    /// <summary>The mod id (Mod Manager, HELLCAT) of the renamed mod, if it has one: with <see cref="Label"/> it lets the app keep a
    /// mod's name after the manager that knew it forgets the file.</summary>
    public string? ModId { get; init; }

    public string AddonsRoot { get; init; } = string.Empty;

    public DateTimeOffset StartedUtc { get; init; }

    public DateTimeOffset? CompletedUtc { get; init; }

    public IReadOnlyList<AddonJournalEntry> Entries { get; init; } = [];

    /// <summary>The single renames in the order they ran, when they differ from <see cref="Entries"/> (a reorder swaps names through
    /// temporary names). Empty when every entry is one rename. Rollback runs these backwards.</summary>
    public IReadOnlyList<AddonRename> Steps { get; init; } = [];

    /// <summary><c>started</c>, <c>completed</c>, <c>rolled_back</c> (a failure was undone), <c>rollback_incomplete</c> (an undo failed too)
    /// or <c>partial</c> (a delete stopped half way).</summary>
    public string Result { get; init; } = "started";

    /// <summary>True when the files were found under their new names with the recorded size and last write time and the old names were gone.</summary>
    public bool Verified { get; init; }

    public string? Note { get; init; }
}

public sealed record AddonRename(string From, string To);

public sealed record AddonOperationResult(string Status, string Summary, IReadOnlyList<AddonRename> Renames, string? JournalId);

/// <summary>The files of one mod for an operation: its archives (<c>pakNN_dir.vpk</c> or a disabled name; data chunks follow by
/// name), the Mod Manager mod id when the manager knows it, and the slot names that Mod Manager records reserve.</summary>
public sealed record AddonModTarget(string AddonsRoot, string Label, string? ModId, IReadOnlyList<string> Archives, IReadOnlySet<string> ReservedSlotNames);

public interface IAddonFileOperations
{
    /// <summary>Every <c>*.vpk</c> in the folder, data chunks (<c>pakNN_000.vpk</c>) included.</summary>
    Task<IReadOnlyList<AddonFileStat>> ListFilesAsync(string addonsRoot, CancellationToken cancellationToken = default);

    /// <summary>Renames inside the folder; never overwrites.</summary>
    Task RenameAsync(string addonsRoot, string fromName, string toName, CancellationToken cancellationToken = default);

    /// <summary>Copies a file into the folder under a new name (staging name, then an atomic move); never overwrites.</summary>
    Task CopyNewAsync(string sourcePath, string addonsRoot, string targetName, CancellationToken cancellationToken = default);

    /// <summary>Deletes a file the same operation just created (undo of a half-finished install).</summary>
    Task DeleteCreatedAsync(string addonsRoot, string fileName, CancellationToken cancellationToken = default);

    /// <summary>Full paths of the data chunks (<c>name_000.vpk</c>...) that belong to a <c>name_dir.vpk</c> package.</summary>
    IReadOnlyList<string> FindChunksBeside(string packagePath);

    Task WriteJournalAsync(string journalRoot, AddonOperationJournal journal, CancellationToken cancellationToken = default);
}

/// <summary>Whether Deadlock (<c>deadlock.exe</c>) is running; it holds the addon archives open.</summary>
public interface IGameProcessProbe
{
    bool IsGameRunning();
}

public interface IRecycleBin
{
    void Recycle(IReadOnlyList<string> paths);
}

/// <summary>
/// File names of disabled and enabled mods. The game loads only <c>pakNN_dir.vpk</c> (plus its <c>pakNN_000.vpk</c>... data
/// chunks). A disabled mod keeps its place in the folder under a prefixed name, so no file is moved or rewritten:
/// <list type="bullet">
/// <item>a mod Mod Manager knows (mod id such as <c>625513</c>, <c>snd-82100</c>, <c>local-guid</c>): <c>&lt;modId&gt;_pakNN_dir.vpk</c>, Mod Manager's own scheme;</item>
/// <item>any other mod: <c>off_pakNN_dir.vpk</c>; if a file of that name exists <c>off2_</c>, <c>off3_</c>... (the first free, up to <c>off99_</c>);</item>
/// <item>every file of the mod gets the same prefix: all archives of a split group and the data chunks of each archive.</item>
/// </list>
/// Enabling restores <c>pakNN_dir.vpk</c> in the slot named in the disabled name when that slot is free (no <c>pakNN_dir.vpk</c> or
/// chunk, and for a mod Mod Manager does not know, not reserved by a manager record); otherwise in a free slot, searched from 99 down
/// (the lowest priority: a lower pak number wins). A disabled name without a slot (<c>local-guid_graves_in_ui.vpk</c>) takes a free slot too.
/// </summary>
public static partial class AddonNaming
{
    private const string DirSuffix = "_dir.vpk";

    public static bool IsSafeModId(string? modId) => !string.IsNullOrWhiteSpace(modId) && modId != "?" && SafeId().IsMatch(modId);

    /// <summary>The name without <c>_dir.vpk</c> (the stem its data chunks share); null for a single-file archive.</summary>
    public static string? DirStem(string archiveName) =>
        archiveName.EndsWith(DirSuffix, StringComparison.OrdinalIgnoreCase) ? archiveName[..^DirSuffix.Length] : null;

    /// <summary>The archive and its data chunks among <paramref name="allNames"/>.</summary>
    public static IReadOnlyList<string> GroupFiles(string archiveName, IEnumerable<string> allNames)
    {
        var stem = DirStem(archiveName);
        var chunks = stem is null
            ? []
            : allNames.Where(name => Chunk().Match(name) is { Success: true } match && match.Groups["stem"].Value.Equals(stem, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return [archiveName, .. chunks];
    }

    public static IReadOnlyList<AddonRename> PlanDisable(IReadOnlyCollection<string> archives, string? modId, IReadOnlyCollection<string> allNames)
    {
        var taken = new HashSet<string>(allNames, StringComparer.OrdinalIgnoreCase);
        var group = archives.SelectMany(archive =>
        {
            if (AddonFileApplication.TryParseSlot(archive) is null)
            {
                throw Errors.Input("ADDON_NOT_ENABLED", $"'{archive}' is not an enabled archive (pakNN_dir.vpk).", "Only a loaded archive can be disabled.");
            }

            return GroupFiles(archive, allNames);
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var prefix = DisabledPrefix(modId, group, taken);
        return [.. group.Select(name => new AddonRename(name, prefix + name))];
    }

    public static IReadOnlyList<AddonRename> PlanEnable(IReadOnlyCollection<string> archives, string? modId, IReadOnlyCollection<string> allNames, IReadOnlySet<string> reservedSlotNames)
    {
        var names = new HashSet<string>(allNames, StringComparer.OrdinalIgnoreCase);
        var usedSlots = names.Select(name => SlotOfAnyFile(name)).Where(slot => slot is not null).Select(slot => slot!.Value).ToHashSet();
        var ordered = archives.OrderBy(archive => OriginalSlot(archive, modId) ?? int.MaxValue).ThenBy(archive => archive, StringComparer.OrdinalIgnoreCase).ToArray();
        var assigned = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var displaced = new List<string>();
        foreach (var archive in ordered)
        {
            if (AddonFileApplication.TryParseSlot(archive) is not null)
            {
                throw Errors.Input("ADDON_ALREADY_ENABLED", $"'{archive}' is already enabled.", "Only a disabled archive can be enabled.");
            }

            var wanted = OriginalSlot(archive, modId);
            var reserved = wanted is { } slot && reservedSlotNames.Contains($"pak{slot:00}_dir.vpk");
            if (wanted is { } free && !usedSlots.Contains(free) && (modId is not null || !reserved))
            {
                assigned[archive] = free;
                usedSlots.Add(free);
            }
            else
            {
                displaced.Add(archive);
            }
        }

        if (displaced.Count > 0)
        {
            var freeSlots = Enumerable.Range(1, 99).OrderDescending()              // pak01..pak99, as Mod Manager numbers
                .Where(slot => !usedSlots.Contains(slot) && !reservedSlotNames.Contains($"pak{slot:00}_dir.vpk")).Take(displaced.Count).Order().ToArray();
            if (freeSlots.Length < displaced.Count)
            {
                throw Errors.Verification("ADDON_NO_FREE_SLOT", "There is no free pak slot (01 to 99) for this mod.", "Delete a mod you no longer use, then enable it again.");
            }

            for (var i = 0; i < displaced.Count; i++)
            {
                assigned[displaced[i]] = freeSlots[i];
            }
        }

        var plan = new List<AddonRename>();
        foreach (var archive in ordered)
        {
            var slot = assigned[archive];
            plan.Add(new AddonRename(archive, $"pak{slot:00}_dir.vpk"));
            foreach (var chunk in GroupFiles(archive, allNames).Skip(1))
            {
                plan.Add(new AddonRename(chunk, $"pak{slot:00}_{Chunk().Match(chunk).Groups["n"].Value}.vpk"));
            }
        }

        var clash = plan.FirstOrDefault(item => names.Contains(item.To));
        return clash is null ? plan : throw Errors.Verification("ADDON_RENAME_TARGET_EXISTS", $"'{clash.To}' already exists.", "S2ModKit never overwrites an existing file.");
    }

    /// <summary>The slot a disabled archive had: the number in <c>&lt;modId&gt;_pakNN_dir.vpk</c> or <c>offN_pakNN_dir.vpk</c>; null otherwise.</summary>
    public static int? OriginalSlot(string archiveName, string? modId)
    {
        var rest = archiveName;
        if (IsSafeModId(modId) && archiveName.StartsWith(modId + "_", StringComparison.OrdinalIgnoreCase))
        {
            rest = archiveName[(modId!.Length + 1)..];
        }
        else if (OffPrefix().Match(archiveName) is { Success: true } match)
        {
            rest = archiveName[match.Length..];
        }

        return AddonFileApplication.TryParseSlot(rest);
    }

    /// <summary>The key a not-managed mod has in lists and in the mod library: its archive's file name.</summary>
    public static string FileKey(string archiveName) => "file:" + archiveName;

    /// <summary>A not-managed mod's display name from its archive: the file name without <c>off_</c> and <c>.vpk</c>.</summary>
    public static string DisplayName(string archiveName) => Path.GetFileNameWithoutExtension(OffPrefix().Replace(archiveName, string.Empty, 1));

    private static string DisabledPrefix(string? modId, IReadOnlyCollection<string> group, HashSet<string> taken)
    {
        if (IsSafeModId(modId))
        {
            var prefix = modId + "_";
            return group.Any(name => taken.Contains(prefix + name))
                ? throw Errors.Verification("ADDON_DISABLED_NAME_TAKEN", $"A file named '{prefix}{group.First(name => taken.Contains(prefix + name))}' already exists.", "Rename or remove that file, then disable the mod again.")
                : prefix;
        }

        for (var n = 1; n <= 99; n++)
        {
            var prefix = n == 1 ? "off_" : $"off{n}_";
            if (!group.Any(name => taken.Contains(prefix + name)))
            {
                return prefix;
            }
        }

        throw Errors.Verification("ADDON_DISABLED_NAME_TAKEN", "off_ to off99_ names are all taken for this mod.", "Delete old disabled copies, then disable the mod again.");
    }

    private static int? SlotOfAnyFile(string name) =>
        SlotFile().Match(name) is { Success: true } match ? int.Parse(match.Groups["slot"].ValueSpan, System.Globalization.CultureInfo.InvariantCulture) : null;

    [GeneratedRegex(@"^(?<stem>.+)_(?<n>\d{3})\.vpk$", RegexOptions.IgnoreCase)]
    private static partial Regex Chunk();

    [GeneratedRegex(@"^off\d*_", RegexOptions.IgnoreCase)]
    private static partial Regex OffPrefix();

    [GeneratedRegex(@"^pak(?<slot>\d\d)_(?:dir|\d{3})\.vpk$", RegexOptions.IgnoreCase)]
    private static partial Regex SlotFile();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-]*$")]
    private static partial Regex SafeId();
}

/// <summary>
/// Enable, disable and delete of any mod in the addons folder: renames in place (a typed <c>addon-rename@1</c> operation with a
/// journal and a verification after), deletes to the Recycle Bin (<c>addon-delete@1</c>), all refused while the game runs. It never
/// writes Mod Manager's state, never overwrites a file and, when a rename fails, puts back the renames already done.
/// </summary>
public sealed class AddonModOperations(IAddonFileOperations files, IGameProcessProbe game, IRecycleBin recycleBin, IClock clock)
{
    public void EnsureGameStopped()
    {
        if (game.IsGameRunning())
        {
            throw Errors.Verification("GAME_RUNNING", "Deadlock is running.", "Close the game, then try again: it keeps its mod files open.");
        }
    }

    /// <summary>The renames a disable would do, without doing them.</summary>
    public async Task<IReadOnlyList<AddonRename>> PlanDisableAsync(AddonModTarget target, CancellationToken cancellationToken = default)
    {
        var names = await NamesAsync(target.AddonsRoot, cancellationToken).ConfigureAwait(false);
        var enabled = target.Archives.Where(archive => AddonFileApplication.TryParseSlot(archive) is not null).ToArray();
        return enabled.Length == 0 ? [] : AddonNaming.PlanDisable(enabled, target.ModId, names);
    }

    public async Task<IReadOnlyList<AddonRename>> PlanEnableAsync(AddonModTarget target, CancellationToken cancellationToken = default)
    {
        var names = await NamesAsync(target.AddonsRoot, cancellationToken).ConfigureAwait(false);
        var disabled = target.Archives.Where(archive => AddonFileApplication.TryParseSlot(archive) is null).ToArray();
        return disabled.Length == 0 ? [] : AddonNaming.PlanEnable(disabled, target.ModId, names, target.ReservedSlotNames);
    }

    public async Task<AddonOperationResult> DisableAsync(AddonModTarget target, string journalRoot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        EnsureGameStopped();
        var plan = await PlanDisableAsync(target, cancellationToken).ConfigureAwait(false);
        return plan.Count == 0
            ? new AddonOperationResult("unchanged", $"'{target.Label}' is already disabled. No files changed.", [], null)
            : await RenameAsync(target, plan, journalRoot, $"Disabled '{target.Label}'. Restart Deadlock if it is running.", cancellationToken).ConfigureAwait(false);
    }

    public async Task<AddonOperationResult> EnableAsync(AddonModTarget target, string journalRoot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        EnsureGameStopped();
        var plan = await PlanEnableAsync(target, cancellationToken).ConfigureAwait(false);
        return plan.Count == 0
            ? new AddonOperationResult("unchanged", $"'{target.Label}' is already enabled. No files changed.", [], null)
            : await RenameAsync(target, plan, journalRoot, $"Enabled '{target.Label}'.", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Makes one version of a mod the active one: disables the enabled <paramref name="siblings"/>, then enables
    /// <paramref name="chosen"/>, in one journaled operation (the chosen version may take a slot a sibling frees).</summary>
    public async Task<AddonOperationResult> SwitchVersionAsync(AddonModTarget chosen, IReadOnlyList<AddonModTarget> siblings, string journalRoot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        ArgumentNullException.ThrowIfNull(siblings);
        EnsureGameStopped();
        var plan = await PlanSetAsync(chosen.AddonsRoot, siblings, [chosen], cancellationToken).ConfigureAwait(false);
        return plan.Count == 0
            ? new AddonOperationResult("unchanged", $"'{chosen.Label}' is already the active version. No files changed.", [], null)
            : await RenameAsync(chosen, plan, journalRoot, $"'{chosen.Label}' is now the active version.", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Switches a set of mods (a profile): disables every enabled mod of <paramref name="disable"/>, then enables every disabled
    /// mod of <paramref name="enable"/> in the given order (each takes the lowest free slot, so the order is the load order among them),
    /// in one journaled <c>addon-rename@1</c> operation; a failure puts every file back.</summary>
    public async Task<AddonOperationResult> SwitchSetAsync(string addonsRoot, string label, IReadOnlyList<AddonModTarget> disable, IReadOnlyList<AddonModTarget> enable,
        string journalRoot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disable);
        ArgumentNullException.ThrowIfNull(enable);
        EnsureGameStopped();
        var plan = await PlanSetAsync(addonsRoot, disable, enable, cancellationToken).ConfigureAwait(false);
        var target = new AddonModTarget(addonsRoot, label, null, [], new HashSet<string>());
        return plan.Count == 0
            ? new AddonOperationResult("unchanged", $"The mods already match '{label}'. No files changed.", [], null)
            : await RenameAsync(target, plan, journalRoot, $"Switched to '{label}'.", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The renames that disable <paramref name="disable"/> and then enable <paramref name="enable"/>, each planned against the
    /// names left by the ones before it.</summary>
    public async Task<IReadOnlyList<AddonRename>> PlanSetAsync(string addonsRoot, IReadOnlyList<AddonModTarget> disable, IReadOnlyList<AddonModTarget> enable, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disable);
        ArgumentNullException.ThrowIfNull(enable);
        var names = (await NamesAsync(addonsRoot, cancellationToken).ConfigureAwait(false)).ToList();
        var plan = new List<AddonRename>();
        void Add(IReadOnlyList<AddonRename> part)
        {
            plan.AddRange(part);
            names = [.. names.Except(part.Select(item => item.From), StringComparer.OrdinalIgnoreCase), .. part.Select(item => item.To)];
        }

        foreach (var target in disable)
        {
            var enabled = target.Archives.Where(archive => AddonFileApplication.TryParseSlot(archive) is not null).ToArray();
            if (enabled.Length > 0)
            {
                Add(AddonNaming.PlanDisable(enabled, target.ModId, names));
            }
        }

        foreach (var target in enable)
        {
            var disabled = target.Archives.Where(archive => AddonFileApplication.TryParseSlot(archive) is null).ToArray();
            if (disabled.Length > 0)
            {
                Add(AddonNaming.PlanEnable(disabled, target.ModId, names, target.ReservedSlotNames));
            }
        }

        return plan;
    }

    /// <summary>The renames moving a mod one place in the load order would do, without doing them.</summary>
    public async Task<AddonReorderPlan> PlanReorderAsync(string addonsRoot, IReadOnlyList<LoadOrderMod> enabled, string key, int direction, CancellationToken cancellationToken = default) =>
        AddonLoadOrder.Plan(enabled, key, direction, await NamesAsync(addonsRoot, cancellationToken).ConfigureAwait(false), Guid.NewGuid().ToString("N")[..8]);

    /// <summary>Moves a mod one place earlier (<paramref name="direction"/> -1, lower pak number, wins more) or later (+1) among the
    /// enabled mods, in one journaled <c>addon-reorder@1</c> operation: the mod and its neighbour exchange their slots, every file
    /// passes through a temporary name where a target is still taken, and a failure puts every file back.</summary>
    public async Task<AddonOperationResult> ReorderAsync(string addonsRoot, IReadOnlyList<LoadOrderMod> enabled, string key, int direction, string journalRoot, CancellationToken cancellationToken = default)
    {
        EnsureGameStopped();
        var plan = await PlanReorderAsync(addonsRoot, enabled, key, direction, cancellationToken).ConfigureAwait(false);
        var target = new AddonModTarget(addonsRoot, plan.Moved, null, [], new HashSet<string>());
        return await RenameAsync(target, plan.Net, journalRoot, plan.Summary, cancellationToken, AddonOperationJournal.ReorderOperation, plan.Steps).ConfigureAwait(false);
    }

    /// <summary>Sends every file of the mod (archives and data chunks) to the Recycle Bin.</summary>
    public async Task<AddonOperationResult> DeleteAsync(AddonModTarget target, string journalRoot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        EnsureGameStopped();
        var stats = (await files.ListFilesAsync(target.AddonsRoot, cancellationToken).ConfigureAwait(false)).ToDictionary(item => item.FileName, StringComparer.OrdinalIgnoreCase);
        var names = target.Archives.SelectMany(archive => AddonNaming.GroupFiles(archive, stats.Keys)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var missing = names.FirstOrDefault(name => !stats.ContainsKey(name));
        if (missing is not null || names.Length == 0)
        {
            throw Errors.Verification("ADDON_FILE_MISSING", $"'{missing ?? target.Label}' is not in the addons folder.", "Refresh the list; the folder changed.");
        }

        RequireRegular(names.Select(name => stats[name]));
        var journal = NewJournal(AddonOperationJournal.DeleteOperation, target, names.Select(name => new AddonJournalEntry(name, null, stats[name].Size, stats[name].LastWriteUtc)).ToArray());
        await files.WriteJournalAsync(journalRoot, journal, cancellationToken).ConfigureAwait(false);
        try
        {
            recycleBin.Recycle([.. names.Select(name => stats[name].FullPath)]);
        }
#pragma warning disable CA1031 // journaled and rethrown as a typed error below
        catch (Exception exception)
#pragma warning restore CA1031
        {
            var left = (await files.ListFilesAsync(target.AddonsRoot, CancellationToken.None).ConfigureAwait(false)).Select(item => item.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var gone = names.Count(name => !left.Contains(name));
            await files.WriteJournalAsync(journalRoot, journal with { Result = gone == 0 ? "rolled_back" : "partial", CompletedUtc = clock.UtcNow, Note = $"{gone} of {names.Length} file(s) were sent to the Recycle Bin before: {exception.Message}" }, CancellationToken.None).ConfigureAwait(false);
            throw Errors.Verification("ADDON_DELETE_FAILED", $"Could not delete '{target.Label}': {exception.Message}", gone == 0 ? "Nothing was deleted." : $"{gone} file(s) are already in the Recycle Bin; restore them from there.");
        }

        var after = (await files.ListFilesAsync(target.AddonsRoot, CancellationToken.None).ConfigureAwait(false)).Select(item => item.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var verified = names.All(name => !after.Contains(name));
        await files.WriteJournalAsync(journalRoot, journal with { Result = verified ? "completed" : "partial", Verified = verified, CompletedUtc = clock.UtcNow }, CancellationToken.None).ConfigureAwait(false);
        return verified
            ? new AddonOperationResult("deleted", $"Deleted '{target.Label}' ({names.Length} file(s)) to the Recycle Bin.", [], journal.Id)
            : throw Errors.Verification("ADDON_DELETE_UNVERIFIED", $"Some files of '{target.Label}' are still in the addons folder.", "The shell did not delete them (cancelled or in use); check the folder.");
    }

    /// <summary>Copies the data chunks that sit beside <paramref name="packagePath"/> (<c>x_000.vpk</c>...) next to the
    /// installed <c>pakNN_dir.vpk</c> as <c>pakNN_000.vpk</c>...; on a failure removes the chunks it copied and rethrows.</summary>
    public async Task<IReadOnlyList<string>> CopyChunksAsync(string packagePath, string addonsRoot, string installedDirName, CancellationToken cancellationToken = default)
    {
        var stem = AddonNaming.DirStem(installedDirName) ?? throw Errors.Input("ADDON_NOT_SPLIT", $"'{installedDirName}' is not a _dir archive.", "Chunks belong to a _dir archive.");
        var copied = new List<string>();
        try
        {
            foreach (var chunk in files.FindChunksBeside(packagePath))
            {
                var name = $"{stem}_{Path.GetFileNameWithoutExtension(chunk)[^3..]}.vpk";
                await files.CopyNewAsync(chunk, addonsRoot, name, cancellationToken).ConfigureAwait(false);
                copied.Add(name);
            }
        }
        catch
        {
            foreach (var name in copied)
            {
                await files.DeleteCreatedAsync(addonsRoot, name, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        return copied;
    }

    /// <summary><paramref name="plan"/> is the net result (journal entries and verification); <paramref name="steps"/> are the single renames
    /// that reach it (default: the plan itself), run in order and, on a failure, undone backwards.</summary>
    private async Task<AddonOperationResult> RenameAsync(AddonModTarget target, IReadOnlyList<AddonRename> plan, string journalRoot, string done, CancellationToken cancellationToken,
        string operation = AddonOperationJournal.RenameOperation, IReadOnlyList<AddonRename>? steps = null)
    {
        var stats = (await files.ListFilesAsync(target.AddonsRoot, cancellationToken).ConfigureAwait(false)).ToDictionary(item => item.FileName, StringComparer.OrdinalIgnoreCase);
        var present = new HashSet<string>(stats.Keys, StringComparer.OrdinalIgnoreCase);
        var single = steps ?? plan;
        foreach (var item in plan)
        {
            if (!stats.TryGetValue(item.From, out var stat))
            {
                throw Errors.Verification("ADDON_FILE_MISSING", $"'{item.From}' is not in the addons folder.", "Refresh the list; the folder changed.");
            }

            if (stat.IsReparsePoint)
            {
                throw Errors.Verification("ADDON_ARCHIVE_REPARSE_POINT", $"Addon archive '{stat.FullPath}' is a reparse point.", "Replace it with a regular file.");
            }
        }

        foreach (var item in single)                                        // the steps must work one by one on the real folder
        {
            if (!present.Remove(item.From))
            {
                throw Errors.Verification("ADDON_FILE_MISSING", $"'{item.From}' is not in the addons folder.", "Refresh the list; the folder changed.");
            }

            if (!present.Add(item.To))
            {
                throw Errors.Verification("ADDON_RENAME_TARGET_EXISTS", $"'{item.To}' already exists.", "S2ModKit never overwrites an existing file.");
            }
        }

        var journal = NewJournal(operation, target, [.. plan.Select(item => new AddonJournalEntry(item.From, item.To, stats[item.From].Size, stats[item.From].LastWriteUtc))]) with
        {
            Steps = steps ?? [],
        };
        await files.WriteJournalAsync(journalRoot, journal, cancellationToken).ConfigureAwait(false);
        var applied = new List<AddonRename>();
        try
        {
            foreach (var item in single)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await files.RenameAsync(target.AddonsRoot, item.From, item.To, CancellationToken.None).ConfigureAwait(false);
                applied.Add(item);
            }

            await VerifyAsync(target.AddonsRoot, journal.Entries, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // every failure is undone, journaled and rethrown as a typed error
        catch (Exception exception)
#pragma warning restore CA1031
        {
            var stuck = new List<string>();
            foreach (var item in Enumerable.Reverse(applied))
            {
                try
                {
                    await files.RenameAsync(target.AddonsRoot, item.To, item.From, CancellationToken.None).ConfigureAwait(false);
                }
#pragma warning disable CA1031
                catch (Exception undo)
#pragma warning restore CA1031
                {
                    stuck.Add($"{item.To} (should be {item.From}): {undo.Message}");
                }
            }

            var note = $"{exception.Message}" + (stuck.Count > 0 ? $" Could not undo: {string.Join("; ", stuck)}" : string.Empty);
            await files.WriteJournalAsync(journalRoot, journal with { Result = stuck.Count == 0 ? "rolled_back" : "rollback_incomplete", CompletedUtc = clock.UtcNow, Note = note }, CancellationToken.None).ConfigureAwait(false);
            throw Errors.Verification(
                stuck.Count == 0 ? "ADDON_RENAME_FAILED" : "ADDON_RENAME_ROLLBACK_INCOMPLETE",
                $"Could not change '{target.Label}': {exception.Message}" + (stuck.Count > 0 ? $" Some files are left under new names: {string.Join("; ", stuck)}" : string.Empty),
                stuck.Count == 0 ? "The files were put back as they were. Close programs that use them (the game, a preview) and try again." : "See the journal in the data folder; rename the listed files back by hand.");
        }

        await files.WriteJournalAsync(journalRoot, journal with { Result = "completed", Verified = true, CompletedUtc = clock.UtcNow }, CancellationToken.None).ConfigureAwait(false);
        return new AddonOperationResult(operation == AddonOperationJournal.ReorderOperation ? "reordered" : "renamed", done, plan, journal.Id);
    }

    /// <summary>Each file is under its new name with the recorded size and last write time, and each old name is gone.</summary>
    private async Task VerifyAsync(string addonsRoot, IReadOnlyList<AddonJournalEntry> entries, CancellationToken cancellationToken)
    {
        var after = (await files.ListFilesAsync(addonsRoot, cancellationToken).ConfigureAwait(false)).ToDictionary(item => item.FileName, StringComparer.OrdinalIgnoreCase);
        var targets = entries.Select(entry => entry.To!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!after.TryGetValue(entry.To!, out var moved) || moved.Size != entry.Size || moved.LastWriteUtc.UtcTicks != entry.LastWriteUtc.UtcTicks)
            {
                throw Errors.Verification("ADDON_RENAME_VERIFY", $"'{entry.To}' is missing or differs in size or time from '{entry.From}'.", "The renames are undone.");
            }

            if (!targets.Contains(entry.From) && after.ContainsKey(entry.From))
            {
                throw Errors.Verification("ADDON_RENAME_VERIFY", $"'{entry.From}' still exists after the rename.", "The renames are undone.");
            }
        }
    }

    private async Task<IReadOnlyCollection<string>> NamesAsync(string addonsRoot, CancellationToken cancellationToken) =>
        [.. (await files.ListFilesAsync(addonsRoot, cancellationToken).ConfigureAwait(false)).Select(item => item.FileName)];

    private AddonOperationJournal NewJournal(string operation, AddonModTarget target, IReadOnlyList<AddonJournalEntry> entries) => new()
    {
        Operation = operation,
        Id = Guid.NewGuid().ToString("N"),
        Label = target.Label,
        ModId = target.ModId,
        AddonsRoot = Path.GetFullPath(target.AddonsRoot),
        StartedUtc = clock.UtcNow.ToUniversalTime(),
        Entries = entries,
    };

    private static void RequireRegular(IEnumerable<AddonFileStat> stats)
    {
        if (stats.FirstOrDefault(item => item.IsReparsePoint) is { } link)
        {
            throw Errors.Verification("ADDON_ARCHIVE_REPARSE_POINT", $"Addon archive '{link.FullPath}' is a reparse point.", "Replace it with a regular file.");
        }
    }
}
