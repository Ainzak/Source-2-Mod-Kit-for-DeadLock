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
        command.Subcommands.Add(CreateAddParticleCommand(output, error));
        return command;
    }

    private static Command CreateAddParticleCommand(TextWriter output, TextWriter error)
    {
        var add = new Command("add-particle", "Add a particle start to a game clip (the game's own AE_CL_CREATE_PARTICLE_EFFECT event layout); writes a new compiled clip.");
        var game = RequiredStringOption("--game", "Deadlock 'game' folder (contains 'citadel').");
        var path = RequiredStringOption("--path", "The clip's game path (models/.../clips/<name>.vnmclip_c).");
        var effect = RequiredStringOption("--effect", "Game effect to start (particles/....vpcf).");
        var template = RequiredStringOption("--template", "A game clip that starts a particle (an ability intro or a hero-select pose); its event layout is copied.");
        var time = new Option<float>("--time") { Description = "Start time in seconds.", DefaultValueFactory = _ => 0f };
        var attachment = new Option<string>("--attachment") { Description = "Model attachment name (empty for none).", DefaultValueFactory = _ => string.Empty };
        var attachmentType = new Option<string>("--attachment-type") { Description = "point_follow, absorigin_follow, center_follow, ...", DefaultValueFactory = _ => "center_follow" };
        var keep = new Option<bool>("--keep-on-change") { Description = "Keep the effect running when the clip changes (default: stop it)." };
        var outputPath = RequiredStringOption("--output", "New file to write (must not exist).");
        var format = CreateFormatOption();
        foreach (var option in new Option[] { game, path, effect, template, time, attachment, attachmentType, keep, outputPath, format })
        {
            add.Options.Add(option);
        }

        add.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            "anim.add-particle", IsJson(parseResult.GetRequiredValue(format)), output, error,
            async () =>
            {
                var target = parseResult.GetRequiredValue(outputPath);
                if (File.Exists(target))
                {
                    throw Errors.Input("CLIP_PARTICLE_OUTPUT_EXISTS", $"'{target}' exists.", "Pass a new file; inputs are never overwritten.");
                }

                var archive = Path.Combine(parseResult.GetRequiredValue(game), "citadel", "pak01_dir.vpk");
                var content = new ValvePakAddonArchiveContent();
                var logical = parseResult.GetRequiredValue(path).Replace('\\', '/');
                var templatePath = parseResult.GetRequiredValue(template).Replace('\\', '/');
                var clip = await content.ReadEntryAsync(archive, logical, cancellationToken).ConfigureAwait(false);
                var templateClip = await content.ReadEntryAsync(archive, templatePath, cancellationToken).ConfigureAwait(false);
                var (bytes, report) = CompiledClipParticle.Add(clip, logical, templateClip, templatePath, parseResult.GetRequiredValue(effect),
                    parseResult.GetValue(time), parseResult.GetValue(attachment) ?? string.Empty, parseResult.GetValue(attachmentType)!, !parseResult.GetValue(keep));
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
                await File.WriteAllBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);
                return report;
            },
            (writer, report) => writer.WriteLine($"Clip particle: {report.Effect} added ({report.Events} events); sha256 {report.OutputSha256}."),
            cancellationToken));
        return add;
    }
}
