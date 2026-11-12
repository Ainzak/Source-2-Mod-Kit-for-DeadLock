using S2ModKit.Application;
using S2ModKit.Desktop;

namespace S2ModKit.Desktop.Tests;

public sealed class InstallReceiptMatchTests
{
    private static AddonArchiveSummary Archive(long size, int entries) =>
        new("pak95_dir.vpk", size, true, 95, null, null, entries, new Dictionary<string, int>());

    [Fact]
    public void AReceiptDescribesItsFileOnlyWhileTheFileIsStillItsPackage()
    {
        var folder = Path.Combine(Path.GetTempPath(), "s2mk-receipt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var package = Path.Combine(folder, "package_dir.vpk");
            File.WriteAllBytes(package, new byte[1234]);
            var receipt = new AddonFileInstallReceipt { TargetFileName = "pak95_dir.vpk", PackagePath = package, EntryCount = 3, Status = "installed" };

            Assert.True(ModCatalog.Describes(receipt, Archive(1234, 3)));
            Assert.False(ModCatalog.Describes(receipt, Archive(1234, 7)));                    // another mod took the slot
            Assert.False(ModCatalog.Describes(receipt, Archive(99999, 3)));                   // same count, other file

            File.Delete(package);
            Assert.True(ModCatalog.Describes(receipt, Archive(99999, 3)));                    // package gone: the file count decides
            Assert.False(ModCatalog.Describes(receipt, Archive(99999, 4)));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
