using S2ModKit.Adapters.Vpk;
using S2ModKit.Domain;

namespace S2ModKit.Vpk.Tests;

public sealed class LocalVpkIntegrationTests
{
    [Fact]
    public async Task ConfiguredSourceVpkPassesCompactProfileAndEntryHashVerification()
    {
        var sourcePath = Environment.GetEnvironmentVariable("S2MODKIT_TEST_SOURCE_VPK");
        var sourceHash = Environment.GetEnvironmentVariable("S2MODKIT_TEST_SOURCE_VPK_SHA256");
        var entryPath = Environment.GetEnvironmentVariable("S2MODKIT_TEST_SOURCE_VPK_ENTRY");
        var entryHash = Environment.GetEnvironmentVariable("S2MODKIT_TEST_SOURCE_VPK_ENTRY_SHA256");
        if (string.IsNullOrWhiteSpace(sourcePath)
            || string.IsNullOrWhiteSpace(sourceHash)
            || string.IsNullOrWhiteSpace(entryPath)
            || string.IsNullOrWhiteSpace(entryHash))
        {
            Assert.Skip("Set the four S2MODKIT_TEST_SOURCE_VPK variables to enable the proprietary local VPK check.");
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var fullPath = Path.GetFullPath(sourcePath);
        var originalHash = new ContentHash(sourceHash);
        var expectedEntryHash = new ContentHash(entryHash);
        var archive = await CompactVpkArchiveIO.ReadAndVerifyAsync(fullPath, cancellationToken);
        var entry = Assert.Single(archive.Entries, item => item.LogicalPath == CompactVpkArchiveIO.NormalizeVpkPath(entryPath));
        var after = await CompactVpkArchiveIO.ReadAndVerifyAsync(fullPath, cancellationToken);

        Assert.Equal(originalHash, archive.ContentHash);
        Assert.Equal(expectedEntryHash, entry.ContentHash);
        Assert.Equal(originalHash, after.ContentHash);
    }
}
