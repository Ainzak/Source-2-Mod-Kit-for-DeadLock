using S2ModKit.Desktop;

namespace S2ModKit.Desktop.Tests;

public sealed class ModPicturesTests
{
    [Fact]
    public void AChosenPictureIsCopiedInReplacedAndRemoved()
    {
        var folder = Path.Combine(Path.GetTempPath(), "s2mk-pictures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var png = Path.Combine(folder, "a.png");
            var jpg = Path.Combine(folder, "b.jpg");
            File.WriteAllBytes(png, [1, 2, 3]);
            File.WriteAllBytes(jpg, [4, 5]);
            var pictures = new ModPictures(Path.Combine(folder, "store"));

            Assert.Null(pictures.UserPicture("package:x"));
            pictures.SetUserPicture("package:x", png);
            File.Delete(png);                                                // the original can go: the store has its own copy
            Assert.Equal([1, 2, 3], File.ReadAllBytes(pictures.UserPicture("package:x")!));

            pictures.SetUserPicture("package:x", jpg);                        // another type replaces the first
            Assert.EndsWith(".jpg", pictures.UserPicture("package:x"), StringComparison.Ordinal);
            Assert.Single(Directory.GetFiles(Path.Combine(folder, "store")));

            pictures.SetUserPicture("package:x", null);
            Assert.Null(pictures.UserPicture("package:x"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void ANonPictureFileIsRefusedAndAddressesAreRemembered()
    {
        var folder = Path.Combine(Path.GetTempPath(), "s2mk-pictures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var text = Path.Combine(folder, "notes.txt");
            File.WriteAllText(text, "x");
            var pictures = new ModPictures(Path.Combine(folder, "store"));

            Assert.Throws<InvalidDataException>(() => pictures.SetUserPicture("mod:1", text));
            pictures.RememberThumbnail(42, "https://images.example/a.jpg");
            Assert.Equal("https://images.example/a.jpg", new ModPictures(Path.Combine(folder, "store")).Thumbnail(42));
            Assert.Null(pictures.Thumbnail(43));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
