using System.Buffers.Binary;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    public async Task<RewriteCandidate> RewritePositionImportAsync(ArtifactContent input, ModelSnapshot snapshot, MutationPlan plan, PositionImportAssets assets, CancellationToken token = default)
    {
        PositionImportContract.ValidatePlan(plan); var op = plan.Operations.Single(); var target = op.PositionImportTarget!;
        var selection = PositionReturnValidator.ReadSelection(assets.Selection); var intent = (ImportPositionsOperation)PositionImportContract.Recipe(selection, target.PositionImport.Binding).Operations.Single();
        intent = intent with { OperationId = op.OperationId };
        var fresh = await PlanPositionImportAsync(input, snapshot, intent, assets, token).ConfigureAwait(false);
        if (PositionImportContract.FactsHash(fresh) != PositionImportContract.FactsHash(op)) throw ImportDrift("Frozen target did not reproduce before writing.");
        token.ThrowIfCancellationRequested();
        if (op.TargetBlocks.Count == 0) return new(input.LogicalPath, input.Bytes, snapshot);
        using var parsed = Parse(input, retainGeometryAnalysis: true); var members = ReadPositionImportMembers(input, parsed, target.PositionImport);
        var replacements = new Dictionary<int, ReadOnlyMemory<byte>>();
        ApplyPositionImportFields(members, op, target, assets, replacements, token);
        foreach (var mesh in members.Select(m => m.Profile.Mesh).DistinctBy(m => m.BlockIndex).Where(m => op.TargetBlocks.Any(b => b.Index == m.BlockIndex)))
        {
            replacements.Add(mesh.BlockIndex, SerializeDeterministically(mesh.Block.Serialize, "position import metadata"));
            ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.MetadataSerialized);
        }
        if (!replacements.Keys.ToHashSet().SetEquals(op.TargetBlocks.Select(b => b.Index))) throw ImportDrift("Writer replacement closure differs.");
        token.ThrowIfCancellationRequested(); var bytes = ResourceEnvelopeWriter.Rebuild(parsed.Envelope, replacements); ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.EnvelopeRebuilt);
        var output = new ArtifactContent(input.LogicalPath, ContentHash.Compute(bytes), bytes); using var reopened = Parse(output, retainGeometryAnalysis: true); ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.OutputReopened);
        token.ThrowIfCancellationRequested(); return new(input.LogicalPath, bytes, reopened.Snapshot);
    }
    private void ApplyPositionImportFields(IReadOnlyList<CoordinatedResolvedBuffer> members, PlannedOperation op,
        PlannedPositionImportTarget target, PositionImportAssets assets, Dictionary<int, ReadOnlyMemory<byte>> replacements, CancellationToken token)
    {
        var delta = PositionAuthoringJson.Read<PositionAuthoringDelta>(assets.Delta);
        using var codec = OpenGeometryCodec();
        foreach (var member in members)
        {
            var p = member.Profile; if (!op.TargetBlocks.Any(b => b.Index == p.Vertices.Snapshot.ResourceBlockIndex)) continue;
            token.ThrowIfCancellationRequested(); var b = target.Buffers.Single(b => b.MemberId == member.MemberId && b.Lod == p.Mesh.Lod);
            var frames = b.FrameWords.Where(f => f.SourceWord != f.ExpectedWord).Select(f => new PositionFrameChange(f.VertexId, BinaryPrimitives.ReadUInt32LittleEndian(Convert.FromHexString(f.SourceWord)), BinaryPrimitives.ReadUInt32LittleEndian(Convert.FromHexString(f.ExpectedWord)))).ToArray();
            var r = new PositionValidatedBuffer(b.MemberId, b.Lod, b.PositionChangeCount, 0, b.FrameClosureIds, frames, b.BoundaryChangedFrameIds, b.ExpectedPositionHash, b.ExpectedFrameHash);
            var returned = delta.Members.Single(d => d.MemberId == PositionAuthoringJson.BufferId(p.Mesh.Lod, p.Mesh.MeshOrdinal, p.Vertices.Snapshot.Ordinal));
            var intended = ApplyReturnedWords(p, returned, r);
            if (ContentHash.Compute(intended.Bytes) != b.ExpectedDecodedHash) throw ImportDrift("Intended complete buffer differs from frozen words.");
            ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.PositionsTransformed); ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.PackedFramesTransformed);
            var encoded = EncodeDeterministically(codec, intended.Bytes, p.Vertices.Snapshot, "position import writer");
            if (!codec.DecodeVertexBuffer(encoded, p.Vertices.Snapshot.VertexCount, p.Vertices.Snapshot.Stride).AsSpan().SequenceEqual(intended.Bytes)) throw ImportDrift("Writer codec lost intended words.");
            replacements.Add(p.Vertices.Snapshot.ResourceBlockIndex, encoded); ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.VertexBufferEncoded);
        }
        foreach (var group in members.GroupBy(m => m.Profile.Mesh.BlockIndex))
        {
            var mesh = group.First().Profile.Mesh; if (!op.TargetBlocks.Any(b => b.Index == mesh.BlockIndex)) continue;
            token.ThrowIfCancellationRequested(); foreach (var box in target.BoxTargets.Where(b => b.ResourceBlockIndex == mesh.BlockIndex)) ReplaceExperimentalBox(mesh.Block.Data, box, reverse: false);
            ReachAffineRewriteCheckpoint(AffineRewriteCheckpoint.BoundsUpdated);
        }
    }

}
