using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Infrastructure;

public sealed partial class FileSystemProjectWorkspace
{
    public async Task SaveEvidenceAsync(string projectRoot, string reportId, string json, string markdown, CancellationToken cancellationToken = default)
    {
        EnsureSafeSegment(reportId, "report id");
        var root = Path.GetFullPath(projectRoot);
        await WriteReplaceAtomicAsync(ResolveInside(root, $"reports/{reportId}.json"), Encoding.UTF8.GetBytes(json), cancellationToken).ConfigureAwait(false);
        await WriteReplaceAtomicAsync(ResolveInside(root, $"reports/{reportId}.md"), Encoding.UTF8.GetBytes(markdown), cancellationToken).ConfigureAwait(false);
    }
    private static async Task<(string Json, string Markdown)> LoadPublishedEvidenceAsync(
        string projectRoot,
        string buildRoot,
        PublishedBuild build,
        CancellationToken cancellationToken)
    {
        var embeddedJson = await ReadEvidenceFileAsync(ResolveInside(buildRoot, "evidence.json"), build.EvidenceJsonHash, cancellationToken).ConfigureAwait(false);
        var embeddedMarkdown = await ReadEvidenceFileAsync(ResolveInside(buildRoot, "evidence.md"), build.EvidenceMarkdownHash, cancellationToken).ConfigureAwait(false);
        var reportJson = await ReadEvidenceFileAsync(ResolveInside(projectRoot, $"reports/{build.BuildId}.json"), build.EvidenceJsonHash, cancellationToken).ConfigureAwait(false);
        var reportMarkdown = await ReadEvidenceFileAsync(ResolveInside(projectRoot, $"reports/{build.BuildId}.md"), build.EvidenceMarkdownHash, cancellationToken).ConfigureAwait(false);
        if (!embeddedJson.AsSpan().SequenceEqual(reportJson) || !embeddedMarkdown.AsSpan().SequenceEqual(reportMarkdown))
        {
            throw Errors.Verification("BUILD_EVIDENCE_COPY_DRIFT", $"Embedded and top-level evidence for build '{build.BuildId}' differ.", "Do not use the build; restore its immutable evidence or rebuild from verified inputs.");
        }

        return (Encoding.UTF8.GetString(embeddedJson), Encoding.UTF8.GetString(embeddedMarkdown));
    }

    private static async Task<byte[]> ReadEvidenceFileAsync(string path, ContentHash expectedHash, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw Errors.Verification("BUILD_EVIDENCE_MISSING", $"Published evidence file '{path}' is missing.", "Do not use the build; restore its evidence or rebuild from verified inputs.");
        }

        var size = new FileInfo(path).Length;
        if (size > MaximumEvidenceBytes)
        {
            throw Errors.Verification("BUILD_EVIDENCE_SIZE_UNSUPPORTED", $"Published evidence file '{path}' exceeds the 16 MiB verification limit.", "Do not use the build; inspect the workspace for corruption.");
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (ContentHash.Compute(bytes) != expectedHash)
        {
            throw Errors.Verification("BUILD_EVIDENCE_HASH_DRIFT", $"Published evidence file '{path}' no longer matches its manifest hash.", "Do not use the build; restore its immutable evidence or rebuild from verified inputs.");
        }

        return bytes;
    }
}
