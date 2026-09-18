using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Infrastructure;

public sealed partial class FileSystemProjectWorkspace
{
    public async Task<VpkPackagePublicationResult> PublishPackageAsync(
        string projectRoot,
        VpkPackagePublication publication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        EnsureSafeSegment(publication.PackageId, "package id");
        if (publication.Mode is not ("replace_source" or "minimal")
            || (publication.Mode == "replace_source" && (publication.SourceVpkPath is null || publication.SourceVpkHash is null || publication.SourceEntryHash is null))
            || (publication.Mode == "minimal" && (publication.SourceVpkPath is not null || publication.SourceVpkHash is not null || publication.SourceEntryHash is not null)))
        {
            throw Errors.Input("VPK_PACKAGE_PUBLICATION_INVALID", "Package mode and source evidence are inconsistent.", "Use replace_source with complete source evidence or minimal without a source archive.");
        }

        var root = Path.GetFullPath(projectRoot);
        var expectedTempRoot = ResolveInside(root, "temp").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidatePath = Path.GetFullPath(publication.CandidateTemporaryPath);
        if (!candidatePath.StartsWith(expectedTempRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidatePath))
        {
            throw Errors.Input("VPK_TEMP_CANDIDATE_INVALID", "The package candidate is missing or outside the project temporary root.", "Create the candidate through the configured VPK adapter.");
        }

        var finalRoot = ResolveInside(root, $"packages/{publication.PackageId}");
        if (Directory.Exists(finalRoot))
        {
            var existing = await LoadPackageAsync(root, publication.PackageId, cancellationToken).ConfigureAwait(false);
            if (existing.Package.ContentHash == publication.CandidateHash
                && string.Equals(existing.Package.Mode, publication.Mode, StringComparison.Ordinal)
                && existing.Package.SourceVpkHash == publication.SourceVpkHash
                && existing.Package.ReplacementEntryHash == publication.ReplacementEntryHash
                && string.Equals(existing.Package.BuildId, publication.BuildId, StringComparison.Ordinal))
            {
                return existing;
            }

            throw Errors.Verification("VPK_PACKAGE_ID_COLLISION", $"Package id '{publication.PackageId}' already identifies different content.", "Do not overwrite it; inspect deterministic package identity generation.");
        }

        var evidenceJson = new UTF8Encoding(false).GetBytes(publication.EvidenceJson);
        var evidenceMarkdown = new UTF8Encoding(false).GetBytes(publication.EvidenceMarkdown);
        if (evidenceJson.LongLength > MaximumEvidenceBytes || evidenceMarkdown.LongLength > MaximumEvidenceBytes)
        {
            throw Errors.Verification("VPK_EVIDENCE_SIZE_UNSUPPORTED", "Package evidence exceeds the 16 MiB per-file limit.", "Reduce diagnostic output before publication.");
        }

        var temporaryRoot = ResolveInside(root, $"temp/publish-package-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var temporaryPackagePath = ResolveInside(temporaryRoot, "candidate.vpk");
            await CopyFileAsync(candidatePath, temporaryPackagePath, cancellationToken).ConfigureAwait(false);
            var size = new FileInfo(temporaryPackagePath).Length;
            var hash = await ComputeFileHashAsync(temporaryPackagePath, cancellationToken).ConfigureAwait(false);
            if (size != publication.CandidateSize || hash != publication.CandidateHash)
            {
                throw Errors.Verification("VPK_PUBLISHED_CONTENT_DRIFT", "Temporary package bytes do not match the verified candidate.", "Reject the package and inspect workspace storage integrity.");
            }

            var packageRelativePath = NormalizeRelative(Path.GetRelativePath(root, ResolveInside(finalRoot, "candidate.vpk")));
            var published = new PublishedVpkPackage(
                publication.PackageId,
                publication.BuildId,
                publication.SourceVpkPath is null ? null : Path.GetFullPath(publication.SourceVpkPath),
                publication.SourceVpkHash,
                StableIdentity.NormalizePath(publication.EntryLogicalPath),
                publication.SourceEntryHash,
                publication.ReplacementEntryHash,
                packageRelativePath,
                hash,
                size,
                ContentHash.Compute(evidenceJson),
                ContentHash.Compute(evidenceMarkdown))
            {
                SchemaVersion = 2,
                Mode = publication.Mode,
            };
            await File.WriteAllBytesAsync(ResolveInside(temporaryRoot, "package.s2mod.json"), JsonDefaults.SerializeToUtf8(published), cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(ResolveInside(temporaryRoot, "evidence.json"), evidenceJson, cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(ResolveInside(temporaryRoot, "evidence.md"), evidenceMarkdown, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(finalRoot)!);
            Directory.Move(temporaryRoot, finalRoot);
            return new VpkPackagePublicationResult(
                published,
                ResolveInside(root, published.PackageRelativePath),
                publication.EvidenceJson,
                publication.EvidenceMarkdown);
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    public async Task<VpkPackagePublicationResult> LoadPackageAsync(
        string projectRoot,
        string packageId,
        CancellationToken cancellationToken = default)
    {
        EnsureSafeSegment(packageId, "package id");
        var root = Path.GetFullPath(projectRoot);
        var packageRoot = ResolveInside(root, $"packages/{packageId}");
        var manifestPath = ResolveInside(packageRoot, "package.s2mod.json");
        if (!File.Exists(manifestPath))
        {
            throw Errors.Input("VPK_PACKAGE_NOT_FOUND", $"Package '{packageId}' does not exist.", "Provide an existing package id emitted by s2mod package create.");
        }

        var manifest = JsonDefaults.Deserialize<PublishedVpkPackage>(
            await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false),
            "VPK package manifest");
        var effectiveMode = manifest.SchemaVersion == 1 ? "replace_source" : manifest.Mode;
        if (!string.Equals(manifest.PackageId, packageId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(manifest.BuildId)
            || manifest.SchemaVersion is not (1 or 2)
            || effectiveMode is not ("replace_source" or "minimal")
            || (effectiveMode == "replace_source" && (string.IsNullOrWhiteSpace(manifest.SourceVpkPath) || manifest.SourceVpkHash is null || manifest.SourceEntryHash is null))
            || (effectiveMode == "minimal" && (manifest.SourceVpkPath is not null || manifest.SourceVpkHash is not null || manifest.SourceEntryHash is not null))
            || manifest.Size < 1)
        {
            throw Errors.Verification("VPK_PACKAGE_MANIFEST_INVALID", "The package manifest violates required identity fields.", "Do not use this package; recreate it from verified inputs.");
        }

        if (!string.Equals(manifest.Mode, effectiveMode, StringComparison.Ordinal))
        {
            manifest = manifest with { Mode = effectiveMode };
        }

        var packagePath = ResolveInside(root, manifest.PackageRelativePath);
        if (!File.Exists(packagePath)
            || new FileInfo(packagePath).Length != manifest.Size
            || await ComputeFileHashAsync(packagePath, cancellationToken).ConfigureAwait(false) != manifest.ContentHash)
        {
            throw Errors.Verification("VPK_PACKAGE_CONTENT_DRIFT", "The published VPK no longer matches its manifest hash and size.", "Do not install it; recreate the package from immutable inputs.");
        }

        var json = await ReadEvidenceFileAsync(ResolveInside(packageRoot, "evidence.json"), manifest.EvidenceJsonHash, cancellationToken).ConfigureAwait(false);
        var markdown = await ReadEvidenceFileAsync(ResolveInside(packageRoot, "evidence.md"), manifest.EvidenceMarkdownHash, cancellationToken).ConfigureAwait(false);
        return new VpkPackagePublicationResult(manifest, packagePath, Encoding.UTF8.GetString(json), Encoding.UTF8.GetString(markdown));
    }
}
