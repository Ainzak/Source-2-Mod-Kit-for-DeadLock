using System.CommandLine;
using S2ModKit.Adapters.Source2;
using S2ModKit.Adapters.Vpk;
using S2ModKit.Domain;

namespace S2ModKit.Cli;

public sealed partial class S2ModKitCli
{
    private static Command CreateAnimCommand(TextWriter output, TextWriter error)
    {
        var command = new Command("anim", "Animation clip helpers.");
        var events = new Command("clip-events", "Give an edited compiled clip the game clip's own events in the current format and per-track read offsets (writes a new file).");
        var clip = RequiredStringOption("--clip", "Edited compiled clip (.vnmclip_c), for example from the Deadlock SDK compiler.");
        var game = RequiredStringOption("--game", "Deadlock 'game' folder (contains 'citadel').");
        var path = RequiredStringOption("--path", "The clip's game path, for example models/heroes_wip/wraith/clips/slide_loop.vnmclip_c.");
        var outputPath = RequiredStringOption("--output", "New file to write (must not exist).");
        var format = CreateFormatOption();
        foreach (var option in new Option[] { clip, game, path, outputPath, format })
        {
            events.Options.Add(option);
        }

        events.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "anim.clip-events", IsJson(parseResult.GetRequiredValue(format)), output, error,
            async () =>
            {
                var target = parseResult.GetRequiredValue(outputPath);
                if (File.Exists(target))
                {
                    throw Errors.Input("CLIP_EVENTS_OUTPUT_EXISTS", $"'{target}' exists.", "Pass a new file; inputs are never overwritten.");
                }

                var logical = parseResult.GetRequiredValue(path).Replace('\\', '/');
                var gameClip = await new ValvePakAddonArchiveContent().ReadEntryAsync(
                    Path.Combine(parseResult.GetRequiredValue(game), "citadel", "pak01_dir.vpk"), logical, cancellationToken).ConfigureAwait(false);
                var (bytes, report) = CompiledClipEvents.Apply(await File.ReadAllBytesAsync(parseResult.GetRequiredValue(clip), cancellationToken).ConfigureAwait(false), gameClip, logical);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
                await File.WriteAllBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);
                return report;
            },
            (writer, report) => writer.WriteLine($"Clip events: {report.Events} events, {report.Tracks} tracks, {report.Frames} frames; sha256 {report.OutputSha256}."),
            cancellationToken));
        command.Subcommands.Add(events);
        return command;
    }
}
