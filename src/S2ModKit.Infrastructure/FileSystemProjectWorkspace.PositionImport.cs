using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Infrastructure;

public sealed partial class FileSystemProjectWorkspace : IPositionImportArtifactStore
{
    public async Task StorePositionImportAsync(string projectRoot, PositionImportBinding binding, PositionImportAssets assets, CancellationToken token = default)
    {
        PositionImportContract.ValidateAssets(binding, assets);
        var root = Path.GetFullPath(projectRoot);
        foreach (var pair in new[] { (binding.SelectionSha256, assets.Selection), (binding.BundleSha256, assets.Bundle), (binding.DeltaSha256, assets.Delta) })
            await WriteIdempotentAsync(ResolveInside(root, $"authoring/{pair.Item1}.json"), pair.Item2.ToArray(), token).ConfigureAwait(false);
    }
    public async Task<PositionImportAssets> LoadPositionImportAsync(string projectRoot, PositionImportBinding binding, CancellationToken token = default)
    {
        var root = Path.GetFullPath(projectRoot);
        async Task<byte[]> Read(ContentHash hash)
        {
            var path = ResolveInside(root, $"authoring/{hash}.json");
            if (!File.Exists(path) || new FileInfo(path).Length > PositionAuthoringJson.MaximumArtifactBytes)
                throw PositionAuthoringJson.Invalid("IMPORT_ARTIFACT_UNAVAILABLE", "Required immutable authoring artifact is missing or oversized.");
            var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            if (ContentHash.Compute(bytes) != hash) throw PositionAuthoringJson.Invalid("IMPORT_ARTIFACT_DRIFT", "Stored authoring artifact hash differs.");
            return bytes;
        }
        var assets = new PositionImportAssets(await Read(binding.SelectionSha256).ConfigureAwait(false), await Read(binding.BundleSha256).ConfigureAwait(false), await Read(binding.DeltaSha256).ConfigureAwait(false));
        PositionImportContract.ValidateAssets(binding, assets); return assets;
    }
}
