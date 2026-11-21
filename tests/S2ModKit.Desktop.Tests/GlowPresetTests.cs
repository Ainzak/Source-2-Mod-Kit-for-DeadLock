using S2ModKit.Adapters.Source2;

namespace S2ModKit.Desktop.Tests;

public sealed class GlowPresetTests : IDisposable
{
    private static readonly MaterialLook Violet = new(0.5f, 0.3f, 1f, 1.2f, 2.5f, 0.6f, true);
    private readonly string folder = Path.Combine(Path.GetTempPath(), "s2mk-glow-presets-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A saved glow reads back with every setting, and saving the same name again (any case) replaces it.</summary>
    [Fact]
    public void ASavedGlowReadsBackAndItsNameIsReplacedNotAddedTwice()
    {
        var presets = GlowPresets.Load(folder);
        Assert.Empty(presets.Items);
        presets.Save("Spectral violet", Violet);
        presets.Save("Red pulse", Violet with { R = 1f, G = 0f, B = 0f, Edges = false });
        presets.Save("  spectral VIOLET ", Violet with { Strength = 2f });

        var again = GlowPresets.Load(folder);
        Assert.Null(again.Problem);
        Assert.Equal(["spectral VIOLET", "Red pulse"], again.Items.Select(item => item.Name));
        Assert.Equal(Violet with { Strength = 2f }, again.Items[0].ToLook());
        Assert.False(again.Items[1].Edges);
    }

    [Fact]
    public void DeleteRemovesThePresetFromTheFile()
    {
        var presets = GlowPresets.Load(folder);
        presets.Save("Ice", Violet);
        presets.Save("Fire", Violet);
        presets.Delete("ice");

        Assert.Equal(["Fire"], GlowPresets.Load(folder).Items.Select(item => item.Name));
    }

    [Fact]
    public void AnEmptyNameOrAValueOutsideItsRangeIsRefused()
    {
        var presets = GlowPresets.Load(folder);

        Assert.Throws<ArgumentException>(() => presets.Save("   ", Violet));
        Assert.Throws<ArgumentException>(() => presets.Save("Too red", Violet with { R = 1.5f }));
        Assert.Throws<ArgumentException>(() => presets.Save("Not a number", Violet with { Strength = float.NaN }));
        Assert.False(File.Exists(Path.Combine(folder, "glow-presets.json")));
    }

    /// <summary>A file this does not understand is reported and never overwritten.</summary>
    [Fact]
    public void AnUnknownFileIsReportedAndLeftAlone()
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "glow-presets.json");
        File.WriteAllText(path, """{"Schema":"glow-presets@9","Presets":[]}""");

        var presets = GlowPresets.Load(folder);

        Assert.NotNull(presets.Problem);
        Assert.Throws<InvalidOperationException>(() => presets.Save("Ice", Violet));
        Assert.Equal("""{"Schema":"glow-presets@9","Presets":[]}""", File.ReadAllText(path));

        File.WriteAllText(path, "not json");
        Assert.Contains("could not be read", GlowPresets.Load(folder).Problem, StringComparison.Ordinal);
    }
}
