using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Infrastructure;

public sealed class AtomicPositionAuthoringPublisher : IPositionAuthoringPublisher
{
    public async Task<PositionAuthoringExport> PublishAsync(string outputRoot, ReadOnlyMemory<byte> bundle, ReadOnlyMemory<byte> selection,
        ReadOnlyMemory<byte> sourceFacts, CancellationToken token = default)
    {
        var root = Path.GetFullPath(outputRoot);
        if (File.Exists(root) || Directory.Exists(root)) throw Errors.Input("IMPORT_OUTPUT_EXISTS", "Authoring output root already exists.", "Choose an absent output directory.");
        if (new[] { bundle.Length, selection.Length, sourceFacts.Length }.Any(n => n is < 2 or > PositionAuthoringJson.MaximumArtifactBytes))
            throw PositionAuthoringJson.Invalid("IMPORT_EXPORT_BUDGET", "Export artifact exceeds its byte budget.");
        var parent = Path.GetDirectoryName(root) ?? throw new ArgumentException("Output parent is absent."); Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, ".s2mod-authoring-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temporary);
        var result = new PositionAuthoringExport(root, ContentHash.Compute(bundle.Span), ContentHash.Compute(selection.Span), ContentHash.Compute(sourceFacts.Span));
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(temporary, "bundle.json"), bundle.ToArray(), token).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(temporary, "selection.json"), selection.ToArray(), token).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(temporary, "source-facts.json"), sourceFacts.ToArray(), token).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(temporary, "pins.json"), PositionAuthoringJson.Canonical(result), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); Directory.Move(temporary, root); return result;
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
    }
}
