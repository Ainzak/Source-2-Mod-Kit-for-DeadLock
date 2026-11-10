using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using SteamDatabase.ValvePak;

namespace S2ModKit.Desktop.Tests;

public sealed class EffectColorTests
{
    private static readonly RgbColor Blue = new(0.1f, 0.3f, 1f);

    [Fact]
    public void AHueChangeKeepsBrightnessAndStrengthAndLeavesGreysAlone()
    {
        var change = new EffectColorChange(Blue);
        var red = EffectColors.Apply((200, 40, 40), change);
        var (h, s, v) = EffectColors.ToHsv(red.R / 255f, red.G / 255f, red.B / 255f);
        Assert.InRange(h, 220, 230);                                           // the target's hue
        Assert.InRange(s, 0.79f, 0.81f);                                       // the original strength
        Assert.InRange(v, 0.78f, 0.79f);                                       // the original brightness
        Assert.Equal((240, 240, 240), EffectColors.Apply((240, 240, 240), change));   // white stays white
        Assert.Equal((26, 76, 255), EffectColors.Apply((12, 200, 30), change with { Exact = true }));
    }

    [Fact]
    public async Task ABuildWithEffectsTheGameDoesNotShipIsRefusedAndNothingIsInstalled()
    {
        using var root = new ModTestRoot();
        var effects = new Dictionary<string, EffectColorChange> { ["particles/abilities/nobody/missing.vpcf"] = new(Blue) };
        var refusal = await Assert.ThrowsAsync<S2ModKit.Domain.S2ModKitException>(() =>
            root.Catalog().BuildEditsAsync(null, ModTestRoot.Model, new HashSet<int>(), [], null, effects, null, TestContext.Current.CancellationToken));
        Assert.Equal("EFFECT_EDIT_NOTHING", refusal.Error.Code);
        Assert.Empty(root.AddonNames());
    }

    /// <summary>Live check on the game's effects (S2MODKIT_TEST_GAME): Lady Geist's effects recolored to blue read back with every
    /// colour changed as computed and the other blocks byte for byte; S2MODKIT_TEST_EFFECTS_ALL=1 tries every effect in the game.</summary>
    [Fact]
    public void GameEffectsRecolorAndReadBack()
    {
        var game = Environment.GetEnvironmentVariable("S2MODKIT_TEST_GAME");
        if (string.IsNullOrWhiteSpace(game))
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME to enable this live check.");
            return;
        }

        using var package = new Package();
        package.Read(Path.Combine(game, "citadel", "pak01_dir.vpk"));
        var all = Environment.GetEnvironmentVariable("S2MODKIT_TEST_EFFECTS_ALL") == "1";
        var entries = package.Entries!["vpcf_c"].Where(entry => all || entry.GetFullPath().Contains("abilities/ghost/", StringComparison.Ordinal)).ToArray();
        var change = new EffectColorChange(Blue);
        var (recolored, failures) = (0, new List<string>());
        foreach (var entry in entries)
        {
            var path = entry.GetFullPath();
            package.ReadEntry(entry, out var bytes);
            try
            {
                var before = CompiledParticleEdit.ReadColors(bytes, path);
                var (after, changed) = CompiledParticleEdit.Recolor(bytes, path, change);
                var read = CompiledParticleEdit.ReadColors(after, path);
                Assert.Equal(before.Count, read.Count);
                for (var i = 0; i < before.Count; i++)
                {
                    Assert.Equal(before[i].Field, read[i].Field);
                    Assert.Equal(EffectColors.Apply((before[i].R, before[i].G, before[i].B), change), (read[i].R, read[i].G, read[i].B));
                }

                recolored += changed > 0 ? 1 : 0;
                var (simple, _) = CompiledParticleEdit.ForPreview(after, path);           // the preview's copy keeps every colour
                Assert.Equal(read.Select(value => (value.R, value.G, value.B)), CompiledParticleEdit.ReadColors(simple, path).Select(value => (value.R, value.G, value.B)));
            }
#pragma warning disable CA1031 // collected and reported
            catch (Exception exception)
#pragma warning restore CA1031
            {
                failures.Add($"{path}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        if (Environment.GetEnvironmentVariable("S2MODKIT_TEST_EFFECTS_REPORT") is { Length: > 0 } report)
        {
            File.WriteAllLines(report, [$"{entries.Length} effects, {recolored} recolored, {failures.Count} failed", .. failures.Take(200)]);
        }

        Assert.True(recolored > 10, $"only {recolored} effects had colours");
        Assert.Empty(failures);
    }
}
