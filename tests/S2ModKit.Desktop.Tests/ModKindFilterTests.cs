using S2ModKit.Application;
using S2ModKit.Desktop;

namespace S2ModKit.Desktop.Tests;

public sealed class ModKindFilterTests
{
    [Theory]
    [InlineData("models/props/example.vmdl_c", "model")]
    [InlineData("models/props/example.vnmclip_c", "animation")]
    [InlineData("sounds/example.vsnd_c", "sound")]
    [InlineData("panorama/images/heroes/astro_card_psd.vtex_c", "ui")]
    [InlineData("particles/example.vpcf_c", "particles")]
    [InlineData("models/hideout/materials/hideout_portrait_large.vmat_c", "material")]
    [InlineData("models/hideout/materials/hideout_portrait_large_color_psd_bc05d6c1.vtex_c", "material")]
    [InlineData("scripts/example.vdata_c", "data")]
    [InlineData("resource/localization/example.txt", "localization")]
    [InlineData("maps/example.vpk", "other")]
    public void EveryResourceKindCanBeFoundThroughTheMenu(string path, string kind)
    {
        var mod = Entry([path]);

        Assert.Contains(kind, ModKindFilter.Options);
        Assert.True(ModKindFilter.Matches(mod, ModKindFilter.AllKinds));
        Assert.Equal([kind], ModKindFilter.Options.Skip(1).Where(option => ModKindFilter.Matches(mod, option)));
    }

    [Fact]
    public void AModWithSeveralKindsMatchesEachAndIgnoresAnUnclassifiedExtraFile()
    {
        var mod = Entry(["models/example.vmdl_c", "sounds/example.vsnd_c", "notes.txt", "maps/ui/hero_prefabs/necro.vpk"]) with
        {
            HeroKinds = new Dictionary<string, IReadOnlyList<string>> { ["Graves"] = ["UI"] },
        };

        Assert.True(ModKindFilter.Matches(mod, "model"));
        Assert.True(ModKindFilter.Matches(mod, "sound"));
        Assert.True(ModKindFilter.Matches(mod, "ui"));
        Assert.False(ModKindFilter.Matches(mod, "other"));
        Assert.False(ModKindFilter.Matches(mod, "particles"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData((int)DoctorVerdict.Works, false)]
    [InlineData((int)DoctorVerdict.Updatable, false)]
    [InlineData((int)DoctorVerdict.Repairable, true)]
    [InlineData((int)DoctorVerdict.Broken, true)]
    [InlineData((int)DoctorVerdict.Look, true)]
    public void TheStatusFiltersUseTheirOwnChecks(int? verdictValue, bool flagged)
    {
        var mod = Entry(["models/hideout/materials/hideout_portrait_large.vmat_c"]);
        var verdict = (DoctorVerdict?)verdictValue;

        Assert.Equal(flagged, ModKindFilter.Matches(mod, ModKindFilter.NeedsAttention, verdict));
        Assert.False(ModKindFilter.Matches(mod, ModKindFilter.WithUpdates, verdict));
        Assert.True(ModKindFilter.Matches(mod, ModKindFilter.WithUpdates, verdict, hasUpdate: true));
    }

    private static ModEntry Entry(string[] entries) =>
        new("test", "Test mod", ModSource.Other, true, [], [], AddonResourceKinds.Count(entries), entries, [], null);
}
