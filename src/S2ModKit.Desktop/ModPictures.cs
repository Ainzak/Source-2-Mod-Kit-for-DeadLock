using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace S2ModKit.Desktop;

/// <summary>
/// Pictures for the mod cards (<c>mod-pictures</c> in the data folder): a picture the user chose for a mod (copied in, so the
/// original file can go), else the first picture of the mod's GameBanana page (its address is remembered in
/// <c>gamebanana.json</c> there, the image itself in the GameBanana client's image cache). Keyed like names and profiles.
/// </summary>
internal sealed class ModPictures(string folder)
{
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"];
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private readonly Lock gate = new();

    private string ThumbsFile => Path.Combine(folder, "gamebanana.json");

    /// <summary>The picture the user chose for a mod (by its identity), or null.</summary>
    public string? UserPicture(string identity) =>
        Directory.Exists(folder) ? Extensions.Select(extension => Path.Combine(folder, Stem(identity) + extension)).FirstOrDefault(File.Exists) : null;

    /// <summary>Copies <paramref name="image"/> in as the mod's picture (any earlier one goes); null removes it.</summary>
    public void SetUserPicture(string identity, string? image)
    {
        Directory.CreateDirectory(folder);
        foreach (var extension in Extensions)
        {
            var old = Path.Combine(folder, Stem(identity) + extension);
            if (File.Exists(old))
            {
                File.Delete(old);
            }
        }

        if (image is not null)
        {
            var extension = Path.GetExtension(image).ToLowerInvariant();
            if (!Extensions.Contains(extension))
            {
                throw new InvalidDataException($"'{Path.GetFileName(image)}' is not a picture the app can show (png, jpg, webp, gif, bmp).");
            }

            File.Copy(image, Path.Combine(folder, Stem(identity) + extension));
        }
    }

    /// <summary>The remembered address of a GameBanana mod's first picture, or null.</summary>
    public string? Thumbnail(long gameBananaId)
    {
        lock (gate)
        {
            return ReadThumbs().GetValueOrDefault(gameBananaId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    public void RememberThumbnail(long gameBananaId, string url)
    {
        lock (gate)
        {
            var thumbs = ReadThumbs();
            thumbs[gameBananaId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = url;
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(ThumbsFile, JsonSerializer.Serialize(thumbs, Indented));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLog.Error("Saving picture addresses", exception);       // only asked again next time
            }
        }
    }

    private Dictionary<string, string> ReadThumbs()
    {
        try
        {
            return File.Exists(ThumbsFile) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ThumbsFile)) ?? [] : [];
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string Stem(string identity) => "mod-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
}
