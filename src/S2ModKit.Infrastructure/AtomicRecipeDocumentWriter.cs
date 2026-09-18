using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Infrastructure;

public sealed class AtomicRecipeDocumentWriter : IRecipeDocumentWriter
{
    private const int MaximumRecipeBytes = 1024 * 1024;

    public async Task<string> WriteNewAsync(
        string outputPath,
        ReadOnlyMemory<byte> canonicalJson,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw Errors.Input(
                "RECIPE_OUTPUT_PATH_INVALID",
                "Recipe output path is required.",
                "Provide a new JSON output path.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(outputPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new S2ModKitException(
                new S2Error(
                    "RECIPE_OUTPUT_PATH_INVALID",
                    "input",
                    "Recipe output path is invalid.",
                    "Provide a valid new JSON output path.",
                    ErrorCategory.InputOrResolution),
                exception);
        }

        if (canonicalJson.Length is < 2 or > MaximumRecipeBytes)
        {
            throw Errors.Input(
                "RECIPE_OUTPUT_SIZE_INVALID",
                $"Canonical recipe length {canonicalJson.Length} is outside the supported range.",
                "Produce a recipe between 2 bytes and 1 MiB.");
        }

        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            throw OutputExists(fullPath);
        }

        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw Errors.Input(
                "RECIPE_OUTPUT_PATH_INVALID",
                "Recipe output path has no parent directory.",
                "Provide a complete output path.");
        }

        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(
            parent,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.s2modkit-staging");
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(canonicalJson, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporary, fullPath, overwrite: false);
            }
            catch (IOException exception) when (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                throw new S2ModKitException(OutputExists(fullPath).Error, exception);
            }

            return fullPath;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static S2ModKitException OutputExists(string fullPath) => Errors.Input(
        "RECIPE_OUTPUT_EXISTS",
        $"Recipe output '{fullPath}' already exists.",
        "Choose a new path; S2ModKit never overwrites an existing recipe.");
}
