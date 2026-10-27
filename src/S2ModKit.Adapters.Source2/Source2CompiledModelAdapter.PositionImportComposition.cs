using S2ModKit.Application;
using S2ModKit.Domain;
using ValveKeyValue;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter : IPositionImportCompositionAdapter
{
    public Task<IReadOnlyList<PositionImportDrawProjection>> PlanImportDrawProjectionAsync(ArtifactContent input, PlannedOperation removal, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var parsed = Parse(input, retainGeometryAnalysis: true);
        var standalone = new MutationPlan("removal", input.ContentHash, input.ContentHash, [removal]);
        if (!CanRewrite(parsed.Snapshot, standalone)) throw ImportDrift("Removal source layout is unsupported.");
        return Task.FromResult<IReadOnlyList<PositionImportDrawProjection>>(ReadImportDrawProjection(input, parsed, removal));
    }
    private static List<PositionImportDrawProjection> ReadImportDrawProjection(ArtifactContent input, ParsedModel source, PlannedOperation removal)
    {
        if (ContentHash.Compute(input.Bytes.Span) != input.ContentHash) throw ImportDrift("Source hash differs.");
        var removed = removal.SelectedDrawCalls.ToDictionary(d => d.DrawCallId, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal); var result = new List<PositionImportDrawProjection>();
        foreach (var mesh in source.MeshesByOrdinal.Values.OrderBy(m => m.Lod).ThenBy(m => m.MeshOrdinal))
        {
            var ordinal = 0;
            foreach (var d in mesh.DrawCalls)
            {
                var original = new SelectedDrawCall(mesh.Lod, input.LogicalPath, mesh.MeshOrdinal, mesh.BlockIndex, d.Snapshot.Id,
                    d.Snapshot.MaterialPath, d.Snapshot.DrawCallOrdinal, d.Snapshot.IndexStart, d.Snapshot.IndexCount);
                var remove = removed.TryGetValue(original.DrawCallId, out var selected);
                if (remove && original != selected) throw ImportDrift("Original removal selector differs from source.");
                if (remove) seen.Add(original.DrawCallId);
                result.Add(new(original, remove ? null : PositionImportCompositionContract.Draw(original, ordinal++), KvSemanticHasher.ComputeComplete(d.Data)));
            }
        }
        if (!seen.SetEquals(removed.Keys)) throw ImportDrift("Original removal selector is incomplete.");
        return result;
    }
    public async Task<RewriteCandidate> RewritePositionImportCompositionAsync(ArtifactContent input, ModelSnapshot snapshot, MutationPlan plan, PositionImportAssets assets, CancellationToken token = default)
    {
        PositionImportCompositionContract.ValidatePlan(plan); var c = plan.PositionImportComposition!; var op = plan.Operations[0]; var target = op.PositionImportTarget!;
        var selection = PositionReturnValidator.ReadSelection(assets.Selection);
        var intent = (ImportPositionsOperation)PositionImportContract.Recipe(selection, target.PositionImport.Binding).Operations[0];
        var fresh = await PlanPositionImportAsync(input, snapshot, intent with { OperationId = op.OperationId }, assets, token).ConfigureAwait(false);
        SameImport(fresh, op, "composition import preflight");
        using var parsed = Parse(input, retainGeometryAnalysis: true);
        SameImport(ReadImportDrawProjection(input, parsed, plan.Operations[1]), c.Draws, "composition source draw preflight");
        var members = ReadPositionImportMembers(input, parsed, target.PositionImport); var replacements = new Dictionary<int, ReadOnlyMemory<byte>>();
        ApplyPositionImportFields(members, op, target, assets, replacements, token);
        var removed = plan.Operations[1].SelectedDrawCalls.Select(d => d.DrawCallId).ToHashSet(StringComparer.Ordinal);
        var collections = new Dictionary<KVObject, List<int>>(ReferenceEqualityComparer.Instance);
        foreach (var d in parsed.MeshesByOrdinal.Values.SelectMany(m => m.DrawCalls).Where(d => removed.Contains(d.Snapshot.Id)))
        {
            if (!collections.TryGetValue(d.Collection, out var indices)) { indices = []; collections.Add(d.Collection, indices); }
            indices.Add(d.CollectionIndex);
        }
        foreach (var collection in collections)
            foreach (var index in collection.Value.OrderDescending()) { token.ThrowIfCancellationRequested(); collection.Key.RemoveAt(index); }
        foreach (var mesh in parsed.MeshesByOrdinal.Values.Where(m => c.TargetBlocks.Any(b => b.Index == m.BlockIndex)).OrderBy(m => m.BlockIndex))
        {
            token.ThrowIfCancellationRequested(); replacements.Add(mesh.BlockIndex, SerializeDeterministically(mesh.Block.Serialize, "composed import/removal metadata"));
            ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.MetadataSerialized);
        }
        if (!replacements.Keys.ToHashSet().SetEquals(c.TargetBlocks.Select(b => b.Index))) throw ImportDrift("Combined replacement closure differs.");
        token.ThrowIfCancellationRequested(); var bytes = ResourceEnvelopeWriter.Rebuild(parsed.Envelope, replacements); ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.EnvelopeRebuilt);
        var output = new ArtifactContent(input.LogicalPath, ContentHash.Compute(bytes), bytes); using var reopened = Parse(output, retainGeometryAnalysis: true); ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.OutputReopened);
        token.ThrowIfCancellationRequested(); return new(input.LogicalPath, bytes, reopened.Snapshot);
    }
    public async Task<PositionImportCompositionVerification> VerifyPositionImportCompositionAsync(ArtifactContent input, ArtifactContent output, MutationPlan plan, PositionImportAssets assets, CancellationToken token = default)
    {
        try
        {
            PositionImportCompositionContract.ValidatePlan(plan); token.ThrowIfCancellationRequested(); var c = plan.PositionImportComposition!;
            if (input.ContentHash != plan.InputHash || input.LogicalPath != output.LogicalPath || ContentHash.Compute(output.Bytes.Span) != output.ContentHash) throw ImportDrift("Combined output identity differs.");
            using var source = Parse(input, retainGeometryAnalysis: true); using var actual = Parse(output, retainGeometryAnalysis: true);
            _ = VerifyExperimentalEnvelope(source, actual.Envelope, output, c.ImportPlan.Operations[0].PositionImportTarget!.SourceBlocks, c.TargetBlocks);
            SameImport(ReadImportDrawProjection(input, source, plan.Operations[1]), c.Draws, "independently rediscovered original draw projection");
            if (!source.MeshesByOrdinal.Keys.ToHashSet().SetEquals(actual.MeshesByOrdinal.Keys)) throw ImportDrift("Combined mesh inventory differs.");
            var restore = new Dictionary<int, ReadOnlyMemory<byte>>();
            foreach (var mesh in source.MeshesByOrdinal.Values.OrderBy(m => m.MeshOrdinal))
            {
                token.ThrowIfCancellationRequested(); var observed = actual.MeshesByOrdinal[mesh.MeshOrdinal];
                var mapping = c.Draws.Where(d => d.Source.MeshOrdinal == mesh.MeshOrdinal && d.Source.Lod == mesh.Lod).ToArray();
                var expected = mapping.Where(d => d.Output is not null).ToArray();
                if (observed.Lod != mesh.Lod || observed.BlockIndex != mesh.BlockIndex || observed.DrawCalls.Count != expected.Length) throw ImportDrift("Retained draw count/mesh differs.");
                for (var i = 0; i < expected.Length; i++)
                {
                    var d = observed.DrawCalls[i]; var e = expected[i].Output!;
                    if (!Matches(d.Snapshot, e) || KvSemanticHasher.ComputeComplete(d.Data) != expected[i].EntryHash) throw ImportDrift("Retained draw entry/order changed outside removal.");
                }
                var scenes = RequireArray(mesh.Block.Data, "m_sceneObjects", "composition source scenes");
                var outputScenes = RequireArray(observed.Block.Data, "m_sceneObjects", "composition observed scenes");
                if (scenes.Count != outputScenes.Count) throw ImportDrift("Scene-object inventory differs.");
                var removed = mapping.Where(d => d.Output is null).Select(d => d.Source.DrawCallId).ToHashSet(StringComparer.Ordinal);
                for (var i = 0; i < scenes.Count; i++)
                {
                    var scene = RequireCollection(scenes[i], "source scene"); var current = RequireCollection(outputScenes[i], "observed scene");
                    var sourceDraws = RequireArray(scene, "m_drawCalls", "source draws"); var currentDraws = RequireArray(current, "m_drawCalls", "observed draws");
                    var retained = mesh.DrawCalls.Where(d => ReferenceEquals(d.Collection, sourceDraws) && !removed.Contains(d.Snapshot.Id)).ToArray();
                    if (retained.Length != currentDraws.Count) throw ImportDrift("Draw entries crossed a scene-object boundary.");
                    for (var j = 0; j < retained.Length; j++)
                        if (KvSemanticHasher.ComputeComplete(retained[j].Data) != KvSemanticHasher.ComputeComplete(currentDraws[j])) throw ImportDrift("Scene draw contents differ.");
                    // Restore only audited draw arrays. Keep actual positions, boxes and every other field.
                    current["m_drawCalls"] = scene["m_drawCalls"];
                }
                if (!c.TargetBlocks.Any(b => b.Index == mesh.BlockIndex)) continue;
                restore.Add(mesh.BlockIndex, KvSemanticHasher.ComputeComplete(mesh.Block.Data) == KvSemanticHasher.ComputeComplete(observed.Block.Data)
                    ? source.Envelope.Blocks.Single(b => b.Index == mesh.BlockIndex).Payload
                    : SerializeDeterministically(observed.Block.Serialize, "independent draw audit projection"));
            }
            byte[] projectedBytes;
            if (c.ImportPlan.Operations[0].TargetBlocks.Count == 0)
            {
                // The envelope audit proved non-target payloads byte-exact. Every restored
                // removal block must also equal source before using its original padding
                // and representation for the byte-exact unchanged-import proof.
                if (!restore.Keys.ToHashSet().SetEquals(c.TargetBlocks.Select(b => b.Index))
                    || restore.Any(b => !b.Value.Span.SequenceEqual(source.Envelope.Blocks.Single(s => s.Index == b.Key).Payload.Span)))
                    throw ImportDrift("Unchanged import projection contains a non-removal difference.");
                projectedBytes = input.Bytes.ToArray();
            }
            else projectedBytes = RebuildImportAuditProjection(source.Envelope, actual.Envelope, restore);
            var projected = new ArtifactContent(output.LogicalPath, ContentHash.Compute(projectedBytes), projectedBytes);
            var verified = await VerifyPositionImportAsync(input, projected, c.ImportPlan, assets, token).ConfigureAwait(false);
            using var reopened = Parse(projected, retainGeometryAnalysis: true); token.ThrowIfCancellationRequested();
            return new(new(projected.LogicalPath, projected.Bytes, reopened.Snapshot), verified,
                [new("import_composition_draws", "passed", "Every original/retained typed draw entry, order and scene boundary independently checked."),
                 new("import_composition_envelope", "passed", "Actual combined resource has only the compatible merged block changes; separate audit projection binds geometry proof.")]);
        }
        catch (S2ModKitException e) when (!e.Error.Code.StartsWith("IMPORT_", StringComparison.Ordinal)) { throw ImportDrift(e.Message); }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or InvalidOperationException or NotSupportedException or OverflowException or IndexOutOfRangeException or KeyNotFoundException or ValveResourceFormat.Utils.UnexpectedMagicException) { throw ImportDrift(e.Message); }
    }

    internal static byte[] RebuildImportAuditProjection(Source2ResourceEnvelope source, Source2ResourceEnvelope actual,
        IReadOnlyDictionary<int, ReadOnlyMemory<byte>> restoredMetadata)
    {
        if (source.Blocks.Count != actual.Blocks.Count || source.Blocks.Where((b, i) => b.Type != actual.Blocks[i].Type).Any()
            || restoredMetadata.Keys.Any(i => i < 0 || i >= source.Blocks.Count))
            throw ImportDrift("Audit projection block inventory differs.");
        // The caller has already audited the actual envelope and draw arrays. Preserve its
        // observed payloads, but use original padding: rebuilding from the combined envelope
        // would retain newly inserted alignment bytes as opaque padding on a second rebuild.
        var replacements = actual.Blocks.Where(b => !b.Payload.Span.SequenceEqual(source.Blocks[b.Index].Payload.Span))
            .ToDictionary(b => b.Index, b => b.Payload);
        foreach (var restored in restoredMetadata) replacements[restored.Key] = restored.Value;
        return ResourceEnvelopeWriter.Rebuild(source, replacements);
    }
}
