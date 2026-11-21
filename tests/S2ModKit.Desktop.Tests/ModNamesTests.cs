using S2ModKit.Desktop;

namespace S2ModKit.Desktop.Tests;

public sealed class ModNamesTests
{
    private static ModEntry Mod(string key, string name) =>
        new(key, name, ModSource.Other, true, [key + "_dir.vpk"], [1], new Dictionary<string, int>(), [], [], null);

    [Fact]
    public void ANameShowsInTheListAndClearingItGivesTheOriginalBack()
    {
        var folder = Path.Combine(Path.GetTempPath(), "s2mk-names-" + Guid.NewGuid().ToString("N"));
        try
        {
            var names = new ModNames(Path.Combine(folder, "mod-names.json"));
            var mod = Mod("pak12", "pak12_dir");
            var other = Mod("pak13", "pak13_dir");

            names.Set(mod, "  Graves bunny suit  ");
            var named = names.Apply([mod, other]);

            Assert.Equal("Graves bunny suit", named[0].Name);
            Assert.Equal("pak12_dir", named[0].OriginalName);
            Assert.Equal("pak13_dir", named[1].Name);                     // others keep their names
            Assert.Null(named[1].OriginalName);

            names.Set(mod, "");
            Assert.Equal("pak12_dir", names.Apply([mod])[0].Name);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
    }

    [Fact]
    public void AnUnreadableNamesFileChangesNoName()
    {
        var folder = Path.Combine(Path.GetTempPath(), "s2mk-names-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "mod-names.json");
            File.WriteAllText(path, "{ not json");
            var mod = Mod("pak12", "pak12_dir");

            Assert.Equal("pak12_dir", new ModNames(path).Apply([mod])[0].Name);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
