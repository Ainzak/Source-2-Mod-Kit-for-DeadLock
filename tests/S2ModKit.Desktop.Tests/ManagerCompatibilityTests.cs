namespace S2ModKit.Desktop.Tests;

public sealed class ManagerCompatibilityTests
{
    private static ModEntry Mod(string name, string modId, bool loaded) =>
        new(modId, name, ModSource.Manager, loaded, [modId + "_pak03_dir.vpk"], [loaded ? 3 : null], new Dictionary<string, int>(), [], [], null, ModId: modId);

    [Fact]
    public void ARepairFromATurnedOffModIsNamedWithItsModel()
    {
        var game = Path.Combine(Path.GetTempPath(), "s2mk-dmm-" + Guid.NewGuid().ToString("N"));
        try
        {
            var layer = Path.Combine(game, "citadel", "dmm_localization_default");
            Directory.CreateDirectory(layer);
            File.WriteAllBytes(Path.Combine(layer, "pak01_dir.vpk"), [0x34, 0x12, 0xAA, 0x55]);
            File.WriteAllText(Path.Combine(layer, "compatibility.json"), """
                {"revision": 40, "repairs": [
                  {"modId": "673069", "sourceVpk": "pak03_dir.vpk", "filePath": "models/heroes_wip/doorman_v2/doorman.vmdl_c", "kind": "taggedSounds"},
                  {"modId": "555", "sourceVpk": "pak07_dir.vpk", "filePath": "models/heroes_wip/fencer/fencer.vmdl_c", "kind": "taggedSounds"}]}
                """);

            var repairs = ManagerCompatibility.Read(game);
            Assert.Equal(2, repairs.Count);
            var warning = ManagerCompatibility.StaleWarning(repairs, [Mod("Gojo Doorman", "673069", loaded: false), Mod("Apollo skin", "555", loaded: true)]);
            Assert.NotNull(warning);
            Assert.Contains("'Gojo Doorman'", warning, StringComparison.Ordinal);
            Assert.Contains("the doorman model may show as a red wireframe", warning, StringComparison.Ordinal);
            Assert.DoesNotContain("Apollo", warning, StringComparison.Ordinal);                    // still enabled: its repair is current
            Assert.Null(ManagerCompatibility.StaleWarning(repairs, [Mod("Gojo Doorman", "673069", loaded: true), Mod("Apollo skin", "555", loaded: true)]));

            File.WriteAllText(Path.Combine(layer, "compatibility.json"), "not json");
            Assert.Empty(ManagerCompatibility.Read(game));                                          // unreadable: no warning, no failure
        }
        finally
        {
            Directory.Delete(game, true);
        }
    }
}
