using S2ModKit.Desktop;
using SteamDatabase.ValvePak;

namespace S2ModKit.Desktop.Tests;

public sealed class SoundLibraryTests
{
    /// <summary>
    /// Local check with the real game (S2MODKIT_TEST_GAME: the Deadlock game folder): every mod in its addons folder that has
    /// sounds is read, its first sound file and first changed event decode from the mod and the game, and Windows opens
    /// the decoded data (without playing it).
    /// </summary>
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void ModSoundsAndTheGamesOriginalsDecodeToDataWindowsCanOpen()
    {
        var game = Environment.GetEnvironmentVariable("S2MODKIT_TEST_GAME");
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(game) || !File.Exists(Path.Combine(game, "citadel", "pak01_dir.vpk")))
        {
            Assert.Skip("Set S2MODKIT_TEST_GAME to the Deadlock game folder to enable this proprietary local check.");
        }

        var addons = Directory.EnumerateFiles(Path.Combine(game, "citadel", "addons"), "*.vpk").Where(HasSounds).Take(6).ToArray();
        Assert.NotEmpty(addons);
        var cache = Path.Combine(Path.GetTempPath(), "s2mk-sound-test-" + Guid.NewGuid().ToString("N"));
        var player = new SoundPlayer(cache);
        try
        {
            var checkedSounds = 0;
            foreach (var addon in addons)
            {
                using var library = SoundLibrary.Open([addon], game);
                Assert.True(library.Files.Count + library.Events.Count > 0, addon);
                var samples = library.Files.Take(1).Select(file => (file.Path, Game: file.ReplacesGame))
                    .Concat(library.Events.Take(1).SelectMany(change => change.ModSounds.Take(1).Select(path => (Path: path, Game: false))
                        .Concat(change.GameSounds.Take(1).Select(path => (Path: path, Game: true)))));
                foreach (var (path, fromGame) in samples)
                {
                    foreach (var source in fromGame ? new[] { false, true } : [false])
                    {
                        if (library.Decode(path, source) is not { } sound)
                        {
                            continue;                                    // an event may name a file neither ships
                        }

                        Assert.True(sound.Data.Length > 44, path);
                        Assert.True(sound.Extension != ".wav" || "RIFF"u8.SequenceEqual(sound.Data.AsSpan(0, 4)), path);
                        var error = ProbeOnUiStyleThread(player, sound.Data, sound.Extension);
                        Assert.True(error is null, $"{path} ({sound.Extension}): {error}");
                        checkedSounds++;
                    }
                }
            }

            Assert.True(checkedSounds > 0);
        }
        finally
        {
            player.Release();
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, true);
            }
        }
    }

    /// <summary>Opens the sound on a single-threaded apartment thread, as the app's UI thread is.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? ProbeOnUiStyleThread(SoundPlayer player, byte[] data, string extension)
    {
        string? error = null;
        var sta = new Thread(() => error = player.Probe(data, extension));
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join();
        return error;
    }

    private static bool HasSounds(string vpk)
    {
        try
        {
            using var package = new Package();
            package.Read(vpk);
            return package.Entries?.ContainsKey("vsnd_c") == true || package.Entries?.ContainsKey("vsndevts_c") == true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}
