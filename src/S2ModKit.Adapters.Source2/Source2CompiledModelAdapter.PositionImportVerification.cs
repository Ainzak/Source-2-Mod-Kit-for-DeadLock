using System.Buffers.Binary;
using S2ModKit.Application;
using S2ModKit.Domain;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    public async Task<PositionImportVerification> VerifyPositionImportAsync(ArtifactContent input, ArtifactContent output, MutationPlan plan, PositionImportAssets assets, CancellationToken token = default)
    {
        try { return await VerifyPositionImportCore(input, output, plan, assets, token).ConfigureAwait(false); }
        catch (S2ModKitException e) when (!e.Error.Code.StartsWith("IMPORT_", StringComparison.Ordinal)) { throw ImportDrift(e.Message); }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or InvalidOperationException or NotSupportedException or OverflowException or IndexOutOfRangeException or KeyNotFoundException or ValveResourceFormat.Utils.UnexpectedMagicException) { throw ImportDrift(e.Message); }
    }
    private async Task<PositionImportVerification> VerifyPositionImportCore(ArtifactContent input, ArtifactContent output, MutationPlan plan, PositionImportAssets assets, CancellationToken token)
    {
        PositionImportContract.ValidatePlan(plan); token.ThrowIfCancellationRequested();
        if (ContentHash.Compute(input.Bytes.Span) != input.ContentHash || ContentHash.Compute(output.Bytes.Span) != output.ContentHash || input.ContentHash != plan.InputHash || input.LogicalPath != output.LogicalPath) throw ImportDrift("Source/output identities differ.");
        var op = plan.Operations.Single(); var target = op.PositionImportTarget!;
        var selection = PositionReturnValidator.ReadSelection(assets.Selection); var recipe = PositionImportContract.Recipe(selection, target.PositionImport.Binding); PositionImportContract.ValidateRecipe(recipe);
        _ = PositionImportContract.Selection((ImportPositionsOperation)recipe.Operations.Single(), assets);
        if (PositionImportContract.FactsHash(((ImportPositionsOperation)recipe.Operations[0]).PositionImport) != PositionImportContract.FactsHash(target.PositionImport)) throw ImportDrift("Trusted intent differs from target.");
        var sourceFacts = await ReadPositionAuthoringSourceAsync(input, selection.Members, token).ConfigureAwait(false);
        var checkedReturn = PositionReturnValidator.Validate(sourceFacts, selection, assets.Bundle, target.PositionImport.Binding.BundleSha256, assets.Delta, EncodePositionAuthoringFrame);
        var delta = PositionAuthoringJson.Read<PositionAuthoringDelta>(assets.Delta);
        using var source = Parse(input, retainGeometryAnalysis: true); using var observed = Parse(output, retainGeometryAnalysis: true);
        var members = ReadPositionImportMembers(input, source, target.PositionImport); var actual = ReadPositionImportMembers(output, observed, target.PositionImport).ToDictionary(m => (m.MemberId, m.Profile.Mesh.Lod));
        if (PositionImportContract.FactsHash(PositionImportDraws(source.Snapshot, target.PositionImport)) != PositionImportContract.FactsHash(op.SelectedDrawCalls)) throw ImportDrift("Source selection differs from frozen union.");
        if (op.TargetBlocks.Count == 0)
        {
            if (!input.Bytes.Span.SequenceEqual(output.Bytes.Span)) throw ImportDrift("Unchanged return must retain every source byte.");
        }
        else _ = VerifyExperimentalEnvelope(source, observed.Envelope, output, target.SourceBlocks, op.TargetBlocks);
        SameImport(sourceFacts.SourceBlocks, target.SourceBlocks, "complete source blocks");
        var observations = new List<PositionImportObservedBuffer>(); var mutable = new HashSet<int>();
        using var codec = OpenGeometryCodec();
        foreach (var member in members)
        {
            token.ThrowIfCancellationRequested(); var p = member.Profile; var current = actual[(member.MemberId, p.Mesh.Lod)].Profile;
            var facts = sourceFacts.Buffers.Single(b => b.SelectedMemberId == member.MemberId && b.Facts.Lod == p.Mesh.Lod);
            var returned = delta.Members.Single(d => d.MemberId == PositionAuthoringJson.BufferId(p.Mesh.Lod, p.Mesh.MeshOrdinal, p.Vertices.Snapshot.Ordinal));
            var expected = checkedReturn.Buffers.Single(b => b.MemberId == member.MemberId && b.Lod == p.Mesh.Lod);
            // Reconstruct independently from immutable source and checked canonical return, never writer bytes/masks.
            var prescribed = p.Vertices.Decoded.ToArray(); var layout = p.Vertices.Snapshot.PositionLayout;
            for (var i = 0; i < returned.Positions.Count; i++)
                for (var axis = 0; axis < 3; axis++) Convert.FromHexString(returned.Positions[i][axis]).CopyTo(prescribed, i * layout.Stride + layout.Offset + axis * 4);
            foreach (var f in expected.Frames) BinaryPrimitives.WriteUInt32LittleEndian(prescribed.AsSpan(f.VertexId * p.PackedFrameLayout.Stride + p.PackedFrameLayout.Offset, 4), f.ExpectedWord);
            if (!prescribed.AsSpan().SequenceEqual(current.Vertices.Decoded)) throw ImportDrift("Reopened positions/frames or immutable attributes differ from source-derived prescribed words.");
            SameImport(ImportBufferFacts(codec.Identity, member, facts, expected, prescribed, selection), target.Buffers.Single(b => b.MemberId == member.MemberId && b.Lod == p.Mesh.Lod), "complete buffer/protection/closure facts");
            var sourceTriangles = ImportTriangleFacts(member, facts.Positions, returned.Positions.Select(PositionAuthoringJson.Point).ToArray(), facts.Triangles);
            SameImport(sourceTriangles, target.SourceTriangles.Single(b => b.MemberId == member.MemberId && b.Lod == p.Mesh.Lod), "source triangle partitions");
            if (!current.Indices.Indices.AsSpan().SequenceEqual(p.Indices.Indices)) throw ImportDrift("Reopened source index words differ.");
            var attrs = current.Vertices.Decoded.ToArray();
            for (var i = 0; i < p.Vertices.Snapshot.VertexCount; i++) { attrs.AsSpan(i * layout.Stride + layout.Offset, 12).Clear(); attrs.AsSpan(i * p.PackedFrameLayout.Stride + p.PackedFrameLayout.Offset, 4).Clear(); }
            if (!prescribed.AsSpan().SequenceEqual(p.Vertices.Decoded))
            {
                mutable.Add(p.Vertices.Snapshot.ResourceBlockIndex); var encoded = EncodeDeterministically(codec, current.Vertices.Decoded, current.Vertices.Snapshot, "independent position import verification");
                if (!codec.DecodeVertexBuffer(encoded, current.Vertices.Snapshot.VertexCount, current.Vertices.Snapshot.Stride).AsSpan().SequenceEqual(current.Vertices.Decoded)) throw ImportDrift("Observed words failed native codec round trip.");
            }
            observations.Add(new(member.MemberId, p.Mesh.Lod, ContentHash.Compute(current.Vertices.Decoded), expected.ExpectedPositionHash, expected.ExpectedFrameHash, ContentHash.Compute(attrs), "passed"));
        }
        var model = source.Resource.Blocks.OfType<Model>().Single(); var context = ReadDirectionalVerificationContext(input, source, observed, model, members, pairedPreservation: true);
        SameImport(context.Select(c => c.Facts).ToArray(), target.ContextBuffers, "complete source/excluded context and skinning");
        SameImport(AuditDirectionalCoincidences(context), target.Coincidences, "source/context coincidence cohorts");
        SameImport(AuditPairedProcedural(source, model, context), target.ProceduralInputs, "procedural fixed words and consumer inputs");
        var (boxes, closures, preserved) = AuditCompleteContributorMetadata(source, observed, members, actual, context, target.BoxTargets, target.PreservationTargets, true);
        SameImport(closures, target.BoxClosures, "independent complete-contributor bounds");
        foreach (var b in boxes.Where(b => !b.Target.OriginalWords.SequenceEqual(b.ObservedWords!))) mutable.Add(b.Target.ResourceBlockIndex);
        if (!mutable.SetEquals(op.TargetBlocks.Select(b => b.Index))) throw ImportDrift("Independent mutation closure differs from frozen blocks.");
        token.ThrowIfCancellationRequested();
        var boundaries = new[]{new BoundaryEvidence("import_reopen","passed","Complete source/returned/output buffer words independently reconstructed."),new BoundaryEvidence("import_frames","passed","Source-relative closure, packed frames, protection and handedness checked."),
            new BoundaryEvidence("import_boxes","passed","Complete contributor retain/expand policy independently checked."),new BoundaryEvidence("import_preservation","passed","Context, UV/index/skin/rig and serialized consumers retained."),
            new BoundaryEvidence("import_consumers","untested","Preserved serialized inputs do not qualify culling, collision or simulation."),new BoundaryEvidence("import_pose","untested","No live animated fit qualification.")};
        return new(boundaries, new(input.ContentHash, output.ContentHash, plan.Fingerprint, target.TargetFingerprint, target.PositionImport.Binding, observations, "passed", "passed", "passed", "untested", "untested", "untested"), boxes, preserved);
    }
    private static void SameImport<T>(T observed, T expected, string name) { if (PositionImportContract.FactsHash(observed) != PositionImportContract.FactsHash(expected)) throw ImportDrift("Independent " + name + " differs."); }
}
