using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed partial class S2ModKitApplication : IPositionAuthoringApplication
{
    public async Task<PositionAuthoringExport> ExportPositionAuthoringAsync(string projectRoot, ReadOnlyMemory<byte> selectionBytes, string outputRoot, CancellationToken token = default)
    {
        var s = PositionReturnValidator.ReadSelection(selectionBytes);
        var source = await PositionSource(projectRoot, s, token).ConfigureAwait(false);
        var facts = PositionAuthoringJson.Canonical(new
        {
            source.InputHash,
            source.Members,
            Buffers = source.Buffers.Select(b => new { b.SelectedMemberId, b.Facts, b.ProceduralIds, b.BoundGates }),
            source.ProceduralInputs,
            source.SourceBlocks
        });
        var bundle = PositionReturnValidator.CreateBundle(source, s, ContentHash.Compute(facts));
        if (positionAuthoringPublisher is null) throw Errors.Unsupported("IMPORT_EXPORT_UNAVAILABLE", "No authoring publisher configured.", "Use the normal CLI composition.");
        return await positionAuthoringPublisher.PublishAsync(outputRoot, PositionAuthoringJson.Canonical(bundle), PositionAuthoringJson.Canonical(s), facts, token).ConfigureAwait(false);
    }
    public async Task<PositionValidationResult> ValidatePositionReturnAsync(string projectRoot, ReadOnlyMemory<byte> selectionBytes,
        ReadOnlyMemory<byte> bundleBytes, ContentHash bundleHash, ReadOnlyMemory<byte> deltaBytes, CancellationToken token = default)
    {
        var selection = PositionReturnValidator.ReadSelection(selectionBytes);
        var source = await PositionSource(projectRoot, selection, token).ConfigureAwait(false);
        try { return PositionReturnValidator.Validate(source, selection, bundleBytes, bundleHash, deltaBytes, PositionReader.EncodePositionAuthoringFrame); }
        catch (ArgumentException e) { throw new S2ModKitException(PositionAuthoringJson.Invalid("IMPORT_GEOMETRY_INVALID", e.Message).Error, e); }
    }
    private IPositionAuthoringSourceReader PositionReader => inspector as IPositionAuthoringSourceReader
        ?? throw Errors.Unsupported("IMPORT_SOURCE_READER_UNAVAILABLE", "No characterized authoring source reader.", "Use the normal CLI composition.");
    private async Task<PositionAuthoringSource> PositionSource(string projectRoot, PositionAuthoringSelection selection, CancellationToken token)
    {
        var (_, input, _) = await LoadProjectGraphAsync(projectRoot, token).ConfigureAwait(false);
        if (input.ContentHash.Value != selection.InputHash) throw PositionAuthoringJson.Invalid("IMPORT_SOURCE_DRIFT", "Selection does not match the immutable project.");
        return await PositionReader.ReadPositionAuthoringSourceAsync(input, selection.Members, token).ConfigureAwait(false);
    }
}
