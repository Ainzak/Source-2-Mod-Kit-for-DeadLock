using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace S2ModKit.Desktop;

/// <summary>A file that more than one of the merged mods has: the mod whose copy is kept and the mods whose copies are left out.</summary>
internal sealed record MergeConflict(string Path, string Winner, IReadOnlyList<string> Losers);

/// <summary>One mod inside a merged mod: its archives (with SHA-256 and size, to find them again for Split).</summary>
internal sealed record MergedPart(string Name, string Key, bool WasEnabled, int? Slot, IReadOnlyList<MergedFile> Files);

internal sealed record MergedFile(string FileName, long Size, string Sha256);

/// <summary>
/// What a merged mod is made of (<c>merge_addons@1</c>), kept beside its package as <c>merge.json</c>: the mods in load order,
/// the file conflicts and which copy was kept. Split uses it to find the originals and turn them back on.
/// </summary>
internal sealed record MergeRecord(string Schema, string Operation, string Name, DateTimeOffset CreatedUtc, IReadOnlyList<MergedPart> Parts,
    IReadOnlyList<MergeConflict> Conflicts, int Files, string OutputSha256)
{
    public const string CurrentSchema = "s2modkit-merge@1";
    public const string FileName = "merge.json";

    public static MergeRecord? Of(ModEntry mod)
    {
        ArgumentNullException.ThrowIfNull(mod);
        if (mod.Install?.Receipt.PackagePath is not { Length: > 0 } package || Path.GetDirectoryName(package) is not { } folder)
        {
            return null;
        }

        var path = Path.Combine(folder, FileName);
        try
        {
            return File.Exists(path) && JsonSerializer.Deserialize<MergeRecord>(File.ReadAllText(path)) is { Schema: CurrentSchema } record ? record : null;
        }
        catch (JsonException exception)
        {
            AppLog.Error($"Reading the merge record of '{mod.Name}'", exception);
            return null;
        }
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public void Write(string folder) =>
        File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(this, Indented));
}

internal static class ModMerge
{
    /// <summary>The mods in the order the game loads them: enabled ones by their lowest pak number (lower wins), then disabled ones.</summary>
    public static IReadOnlyList<ModEntry> InLoadOrder(IEnumerable<ModEntry> mods) =>
        [.. mods.OrderBy(mod => mod.Loaded ? 0 : 1).ThenBy(mod => mod.FirstSlot).ThenBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>The files more than one mod has, for mods given in load order (the first one that has a file keeps it).</summary>
    public static IReadOnlyList<MergeConflict> Conflicts(IReadOnlyList<(string Name, IReadOnlyList<string> Entries)> ordered)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        var owners = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, entries) in ordered)
        {
            foreach (var entry in entries.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!owners.TryGetValue(entry, out var list))
                {
                    owners[entry] = list = [];
                }

                list.Add(name);
            }
        }

        return [.. owners.Where(pair => pair.Value.Count > 1).OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new MergeConflict(pair.Key, pair.Value[0], [.. pair.Value.Skip(1)]))];
    }

    /// <summary>A mod's archives in the order they win (lowest pak number first).</summary>
    public static IEnumerable<string> FilesInOrder(ModEntry mod) =>
        mod.Files.Select((file, index) => (file, slot: index < mod.Slots.Count ? mod.Slots[index] : null)).OrderBy(item => item.slot ?? int.MaxValue).Select(item => item.file);

    public static async Task<MergedFile> DescribeAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new MergedFile(Path.GetFileName(path), stream.Length, Convert.ToHexStringLower(hash));
    }
}

/// <summary>Picks the mods to merge with <c>first</c> and a name; shows the shared files and which mod's copy is kept. The result of
/// <c>ShowDialog</c> is the chosen mods (including the first) and the name, or null when cancelled.</summary>
internal sealed class MergeDialog : Window
{
    public MergeDialog(ModEntry first, IReadOnlyList<ModEntry> all)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(all);
        Title = $"Merge '{first.Name}' with other mods";
        Width = 640;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var candidates = ModMerge.InLoadOrder(all.Where(mod => mod.Key != first.Key && mod.Files.Count > 0));
        var checks = new List<(CheckBox Box, ModEntry Mod)>();
        var list = new StackPanel { Spacing = 2 };
        var search = new TextBox { PlaceholderText = "Search mods" };
        var name = new TextBox { Text = $"Merged: {first.Name}" };
        var nameEdited = false;
        var conflicts = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var merge = new Button { Content = "Merge", IsEnabled = false };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        name.TextChanged += (_, _) => nameEdited = name.IsFocused || nameEdited;

        IReadOnlyList<ModEntry> Chosen() => ModMerge.InLoadOrder(checks.Where(item => item.Box.IsChecked == true).Select(item => item.Mod).Append(first));

        void Update()
        {
            var chosen = Chosen();
            merge.IsEnabled = chosen.Count > 1 && !string.IsNullOrWhiteSpace(name.Text);
            if (!nameEdited)
            {
                name.Text = "Merged: " + string.Join(" + ", chosen.Select(mod => mod.Name));
            }

            var shared = ModMerge.Conflicts([.. chosen.Select(mod => (mod.Name, mod.Entries))]);
            var order = "Load order (the first one that has a file keeps it): " + string.Join(", ", chosen.Select(mod => mod.Loaded ? $"{mod.Name} (pak{mod.FirstSlot:00})" : $"{mod.Name} (disabled)")) + ".";
            conflicts.Text = chosen.Count < 2 ? "Tick at least one more mod."
                : shared.Count == 0 ? order + Environment.NewLine + "No shared files: everything is kept."
                : order + Environment.NewLine + $"{shared.Count} shared file{(shared.Count == 1 ? string.Empty : "s")}:" + Environment.NewLine
                    + string.Join(Environment.NewLine, shared.Take(40).Select(conflict => $"{conflict.Path}: {conflict.Winner} kept, {string.Join(", ", conflict.Losers)} left out"))
                    + (shared.Count > 40 ? Environment.NewLine + $"... and {shared.Count - 40} more" : string.Empty);
        }

        foreach (var mod in candidates)
        {
            var box = new CheckBox { Content = $"{(mod.Loaded ? $"pak{mod.FirstSlot:00}" : "off")}  {mod.Name}" };
            box.IsCheckedChanged += (_, _) => Update();
            checks.Add((box, mod));
            list.Children.Add(box);
        }

        search.TextChanged += (_, _) =>
        {
            foreach (var (box, mod) in checks)
            {
                box.IsVisible = string.IsNullOrWhiteSpace(search.Text) || mod.Name.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase) || box.IsChecked == true;
            }
        };
        name.TextChanged += (_, _) => merge.IsEnabled = Chosen().Count > 1 && !string.IsNullOrWhiteSpace(name.Text);
        merge.Click += (_, _) => Close((Chosen(), name.Text!.Trim()));
        cancel.Click += (_, _) => Close(null);

        var layout = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto,Auto,Auto"), RowSpacing = 8 };
        void Row(Control control, int row)
        {
            Grid.SetRow(control, row);
            layout.Children.Add(control);
        }

        Row(new TextBlock
        {
            Text = "The chosen mods become one mod. Files they share follow the current load order. The originals are disabled, not deleted; "
                + "Split on the merged mod turns them back on.",
            TextWrapping = TextWrapping.Wrap,
        }, 0);
        Row(search, 1);
        Row(new ScrollViewer { Content = list }, 2);
        Row(new TextBlock { Text = "Name of the merged mod", Foreground = AppTheme.Muted, FontSize = 11 }, 3);
        Row(name, 4);
        Row(new ScrollViewer { Content = conflicts, MaxHeight = 160 }, 5);
        Row(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { merge, cancel } }, 6);
        Content = layout;
        Update();
    }
}
