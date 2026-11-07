using S2ModKit.Desktop;

namespace S2ModKit.Desktop.Tests;

public sealed class AppUpdateAndSettingsTests
{
    private const string Release = """
        {"tag_name":"v0.8.0","html_url":"https://github.com/o/r/releases/tag/v0.8.0","draft":false,"prerelease":false,
         "assets":[{"name":"S2ModKit-0.8.0-win-x64.zip","browser_download_url":"https://example.invalid/zip","size":10},
                   {"name":"S2ModKit-0.8.0-setup.exe","browser_download_url":"https://example.invalid/setup","size":1234,
                    "digest":"sha256:ABCDEF"}]}
        """;

    [Fact]
    public void ANewerReleaseOffersItsSetupWithSizeAndChecksum()
    {
        var update = AppUpdates.Parse(Release, new Version(0, 7, 0));

        Assert.NotNull(update);
        Assert.Equal(new Version(0, 8, 0), update.Version);
        Assert.Equal("S2ModKit-0.8.0-setup.exe", update.SetupName);
        Assert.Equal(1234, update.SetupSize);
        Assert.Equal("abcdef", update.Sha256);
    }

    [Fact]
    public void TheSameOrAnOlderVersionADraftOrAPrereleaseIsNoUpdate()
    {
        Assert.Null(AppUpdates.Parse(Release, new Version(0, 8, 0)));
        Assert.Null(AppUpdates.Parse(Release, new Version(0, 9, 1)));
        Assert.Null(AppUpdates.Parse(Release.Replace("\"prerelease\":false", "\"prerelease\":true", StringComparison.Ordinal), new Version(0, 7, 0)));
        Assert.Null(AppUpdates.Parse(Release.Replace("\"draft\":false", "\"draft\":true", StringComparison.Ordinal), new Version(0, 7, 0)));
        var noSetup = AppUpdates.Parse(Release.Replace("-setup.exe", ".msi", StringComparison.Ordinal), new Version(0, 7, 0));
        Assert.NotNull(noSetup);
        Assert.Null(noSetup.Setup);                                            // a release without a setup only links its page
    }

    [Theory]
    [InlineData("v0.7.0", "0.7.0")]
    [InlineData("0.7.0+abc123", "0.7.0")]
    [InlineData("v1.2", "1.2.0")]
    public void VersionsReadFromTagsAndBuildStamps(string text, string expected) =>
        Assert.Equal(Version.Parse(expected), AppUpdates.ParseVersion(text));

    [Fact]
    public void DataMovesOnlyIntoAnEmptyFolderAndTheOldFolderStays()
    {
        var root = Path.Combine(Path.GetTempPath(), "s2mk-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var old = Path.Combine(root, "old");
            Directory.CreateDirectory(Path.Combine(old, "journal"));
            File.WriteAllText(Path.Combine(old, "journal", "a.json"), "{}");
            var settings = new AppSettings(Path.Combine(root, "game"), old);

            var moved = settings.MoveData(Path.Combine(root, "new"));

            Assert.Equal(Path.Combine(root, "new"), moved.Data);
            Assert.True(File.Exists(Path.Combine(root, "new", "journal", "a.json")));
            Assert.True(File.Exists(Path.Combine(old, "journal", "a.json")));
            var busy = Path.Combine(root, "busy");
            Directory.CreateDirectory(busy);
            File.WriteAllText(Path.Combine(busy, "x.txt"), "x");
            Assert.Throws<IOException>(() => settings.MoveData(busy));
            Assert.True(AppSettings.IsPortable(old) is false && AppSettings.IsPortable(root) is false);
            File.WriteAllText(Path.Combine(root, "portable"), string.Empty);
            Assert.True(AppSettings.IsPortable(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
