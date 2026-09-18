using System.Text;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Infrastructure;

public sealed class FileSystemCompatibilityReportPublisher : ICompatibilityReportPublisher
{
    public async Task<CompatibilityReportPublication> PublishAsync(
        string outputRoot,
        CompatibilityReport report,
        string json,
        string markdown,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(markdown);
        var root = Path.GetFullPath(outputRoot);
        Directory.CreateDirectory(root);
        var jsonPath = Path.Combine(root, $"{report.ReportId}.json");
        var markdownPath = Path.Combine(root, $"{report.ReportId}.md");
        if (File.Exists(jsonPath) || File.Exists(markdownPath))
        {
            throw Errors.Input(
                "COMPATIBILITY_REPORT_EXISTS",
                $"Compatibility report '{report.ReportId}' already exists below the configured output root.",
                "Keep the immutable report or choose a different empty output root.");
        }

        var nonce = Guid.NewGuid().ToString("N");
        var jsonTemporaryPath = Path.Combine(root, $".{report.ReportId}.{nonce}.json.tmp");
        var markdownTemporaryPath = Path.Combine(root, $".{report.ReportId}.{nonce}.md.tmp");
        try
        {
            await WriteNewAsync(jsonTemporaryPath, json, cancellationToken).ConfigureAwait(false);
            await WriteNewAsync(markdownTemporaryPath, markdown, cancellationToken).ConfigureAwait(false);
            File.Move(jsonTemporaryPath, jsonPath, overwrite: false);
            File.Move(markdownTemporaryPath, markdownPath, overwrite: false);
            return new CompatibilityReportPublication(report, jsonPath, markdownPath);
        }
        catch
        {
            DeleteOwnedTemporary(jsonTemporaryPath);
            DeleteOwnedTemporary(markdownTemporaryPath);
            if (File.Exists(jsonPath) && !File.Exists(markdownPath))
            {
                File.Delete(jsonPath);
            }

            throw;
        }
    }

    private static async Task WriteNewAsync(string path, string content, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        var bytes = Encoding.UTF8.GetBytes(content);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void DeleteOwnedTemporary(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
