using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Infrastructure;

public static class FileSystemDirectionalAuthoringPublisher
{
    public static async Task<(string Path, ContentHash Hash)> PublishAsync(string outputRoot, DirectionalAuthoringSource source, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        var bytes = JsonDefaults.SerializeToUtf8(new { SchemaVersion = 1, Kind = "directional_authoring_source", Source = source });
        if (bytes.Length > 64 * 1024 * 1024)
            throw Errors.Input("DIRECTIONAL_REPORT_TOO_LARGE", "Source authoring report exceeds 64 MiB.", "Choose a bounded complete selection; do not truncate source facts.");
        var hash = ContentHash.Compute(bytes);
        var root = System.IO.Path.GetFullPath(outputRoot);
        var destination = System.IO.Path.Combine(root, "directional-source-" + hash.Value + ".json");
        if (File.Exists(destination))
        {
            if (ContentHash.Compute(await File.ReadAllBytesAsync(destination, token).ConfigureAwait(false)) != hash)
                throw Errors.Verification("DIRECTIONAL_REPORT_OUTPUT_CONFLICT", "Existing source report differs.", "Choose another output root.");
            return (destination, hash);
        }
        Directory.CreateDirectory(root);
        var staging = System.IO.Path.Combine(root, ".directional-source-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(staging, bytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(staging, destination, overwrite: false);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
        return (destination, hash);
    }
}
