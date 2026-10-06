using System.Text.Json;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Infrastructure;

/// <summary>Publishes a new, complete diagnostic directory. Never replaces existing files.</summary>
public static class FileSystemEllipsoidPreviewPublisher
{
    public static async Task<EllipsoidPreviewPublication> PublishAsync(string outputRoot, EllipsoidPreviewArtifacts artifacts, CancellationToken cancellationToken = default)
        => await PublishCoreAsync(outputRoot, artifacts, "ellipsoid", cancellationToken).ConfigureAwait(false);

    public static async Task<EllipsoidPreviewPublication> PublishCoordinatedAsync(string outputRoot, EllipsoidPreviewArtifacts artifacts, CancellationToken cancellationToken = default)
        => await PublishCoreAsync(outputRoot, artifacts, "coordinated", cancellationToken).ConfigureAwait(false);

    public static async Task<EllipsoidPreviewPublication> PublishDirectionalAsync(string outputRoot, EllipsoidPreviewArtifacts artifacts, CancellationToken cancellationToken = default)
        => await PublishCoreAsync(outputRoot, artifacts, "directional", cancellationToken).ConfigureAwait(false);

    private static async Task<EllipsoidPreviewPublication> PublishCoreAsync(string outputRoot, EllipsoidPreviewArtifacts artifacts, string prefix, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        if (ContentHash.Compute(artifacts.SummaryJson.Span) != artifacts.SummaryHash || ContentHash.Compute(artifacts.ContactSheetSvg.Span) != artifacts.ContactSheetHash)
            throw Errors.Verification("ELLIPSOID_PREVIEW_INVALID", "Diagnostic artifact bytes differ from their declared hashes.", "Regenerate the contact sheet.");
        using var summary = JsonDocument.Parse(artifacts.SummaryJson);
        if (summary.RootElement.GetProperty("previewFingerprint").GetString() != artifacts.PreviewFingerprint.Value
            || summary.RootElement.GetProperty("contactSheetHash").GetString() != artifacts.ContactSheetHash.Value)
            throw Errors.Verification("ELLIPSOID_PREVIEW_INVALID", "Summary identities do not bind these artifact bytes.", "Regenerate the contact sheet.");
        var root = Path.GetFullPath(outputRoot);
        var destination = Path.Combine(root, $"{prefix}-{artifacts.PreviewFingerprint.Value}");
        var json = Path.Combine(destination, "selection-summary.json");
        var svg = Path.Combine(destination, "selection-contact-sheet.svg");
        if (File.Exists(destination))
            throw Errors.Verification("ELLIPSOID_PREVIEW_OUTPUT_CONFLICT", "A file occupies the content-addressed preview directory.", "Choose another output root; existing files will not be overwritten.");
        if (Directory.Exists(destination))
        {
            if (!File.Exists(json) || !File.Exists(svg)
                || ContentHash.Compute(await File.ReadAllBytesAsync(json, cancellationToken).ConfigureAwait(false)) != artifacts.SummaryHash
                || ContentHash.Compute(await File.ReadAllBytesAsync(svg, cancellationToken).ConfigureAwait(false)) != artifacts.ContactSheetHash)
                throw Errors.Verification("ELLIPSOID_PREVIEW_OUTPUT_CONFLICT", "An existing preview directory contains different or incomplete artifacts.", "Choose another output root; existing files will not be overwritten.");
            return new(destination, json, svg, artifacts.PreviewFingerprint, artifacts.SummaryHash, artifacts.ContactSheetHash);
        }
        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, $".ellipsoid-preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(staging, "selection-summary.json"), artifacts.SummaryJson.ToArray(), cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(staging, "selection-contact-sheet.svg"), artifacts.ContactSheetSvg.ToArray(), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
        }
        finally
        {
            // Only this invocation's freshly created staging path, directly under the
            // resolved caller root, may be removed. Existing output/input paths are untouched.
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
        return new(destination, json, svg, artifacts.PreviewFingerprint, artifacts.SummaryHash, artifacts.ContactSheetHash);
    }
}
