using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Desktop.Tests;

/// <summary>
/// Local checks of <c>clip_particle_event@1</c> on the game's own clips (proprietary inputs, nothing installed):
/// S2MODKIT_TEST_GAME is the Deadlock game folder.
/// </summary>
public sealed class ClipParticleTests
{
    private const string Idle = "models/heroes_staging/astro/clips/out_of_combat_stand_idle.vnmclip_c";
    private const string Template = "models/heroes_staging/archer/clips/ability_guided_arrow_intro.vnmclip_c";
    private const string Snow = "particles/abilities/kelvin/kelvin_ice_path_buff_snow.vpcf";

    private static byte[]? Read(string path)
    {
        var game = Environment.GetEnvironmentVariable("S2MODKIT_TEST_GAME");
        if (string.IsNullOrWhiteSpace(game))
        {
            return null;
        }

        using var package = new Package();
        package.Read(Path.Combine(game, "citadel", "pak01_dir.vpk"));
        package.ReadEntry(package.FindEntry(path)!, out var bytes);
        return bytes;
    }

    [Fact]
    public void AParticleStartIsAddedWithItsFileAndEverythingElseKept()
    {
        if (Read(Idle) is not { } idle || Read(Template) is not { } template)
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME to enable this live check.");
            return;
        }

        var (bytes, report) = CompiledClipParticle.Add(idle, Idle, template, Template, Snow, 0.5f, string.Empty, "center_follow", stopOnSequenceChange: true);

        using var before = new Resource { FileName = Idle };
        before.Read(new MemoryStream(idle));
        using var after = new Resource { FileName = Idle };
        after.Read(new MemoryStream(bytes));
        var root = ((BinaryKV3)after.DataBlock!).Data.Root;
        var added = root["m_events"].Values.Last();
        Assert.Equal(CompiledClipParticle.EventClass, added["m_animEventClassName"].ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(Snow, added["m_KV"]["name"].ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("center_follow", added["m_KV"]["attachment_type"].ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(report.Events, root["m_events"].Count);
        Assert.Contains(after.ExternalReferences!.ResourceRefInfoList, reference => reference.Name == Snow);
        Assert.Equal(before.ExternalReferences!.ResourceRefInfoList.Count + 1, after.ExternalReferences.ResourceRefInfoList.Count);
        var poseBefore = ((BinaryKV3)before.DataBlock!).Data.Root["m_compressedPoseData"];
        Assert.Equal(poseBefore.Count, root["m_compressedPoseData"].Count);     // pose data untouched

        Assert.Throws<S2ModKitException>(() => CompiledClipParticle.Add(idle, Idle, template, Template, "materials/x.vmat", 0f, string.Empty, "center_follow", true));
        Assert.Throws<S2ModKitException>(() => CompiledClipParticle.Add(idle, Idle, template, Template, Snow, 999f, string.Empty, "center_follow", true));
        Assert.Throws<S2ModKitException>(() => CompiledClipParticle.Add(idle, Idle, idle, Idle, Snow, 0f, string.Empty, "center_follow", true));   // no particle event to copy
    }
}
