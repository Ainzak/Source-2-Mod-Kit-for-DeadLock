using S2ModKit.Application;
using S2ModKit.Domain;
using ValveKeyValue;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    private static (ZeroBoneBoxPreservationEvidence Box, ZeroRenderSpherePreservationEvidence? Sphere) VerifyCoordinatedZeroFields(
        ArtifactContent input, ParsedModel before, Source2AffineProfile source, Source2AffineProfile observed, Source2BoneBoundsAnalysis bone,
        PlannedCoordinatedTransformTarget target, PlannedOperation operation)
    {
        var intent = CoordinatedContractValidator.Operation(operation);
        var sourceBone = ExperimentalArray(ExperimentalCollection(source.Mesh.Block.Data, "m_skeleton"), "m_bones")[bone.BoneIndex];
        var outputBone = ExperimentalArray(ExperimentalCollection(observed.Mesh.Block.Data, "m_skeleton"), "m_bones")[bone.BoneIndex];
        var outputAnalysis = observed.Metadata.BoneBounds.Single(b => b.BoneIndex == bone.BoneIndex);
        CoordinatedZeroBoxSource Read(KVObject raw, Source2AffineProfile profile, Source2BoneBoundsAnalysis analysis) => ReadCoordinatedZeroBoxSource(
            ExperimentalCollection(raw, "m_bbox"), analysis.InfluencedVertices, profile.AllBeforePositions,
            ExperimentalArray(raw, "m_invBindPose"), analysis.InverseBindPose, analysis.SphereRadius, false,
            intent.ZeroBoneBoxPolicy, raw["m_flSphereRadius"], intent.ZeroRenderSpherePolicy);
        // Admission is independently repeated on both serialized trees. No zero field
        // is repaired, narrowed or reconstructed from the moved point.
        var original = Read(sourceBone, source, bone); var current = Read(outputBone, observed, outputAnalysis);
        var path = $"m_skeleton.m_bones[{bone.BoneIndex}].m_bbox.m_vecCenter+m_vecSize";
        var planned = target.ZeroBoxTargets.Single(z => z.ResourceBlockIndex == source.Mesh.BlockIndex && z.FieldPath == path);
        var payload = ContentHash.Compute(before.Envelope.Blocks[source.Mesh.BlockIndex].Payload.Span);
        var contributors = ExperimentalContributorsHash(input, source, bone.InfluencedVertices);
        if (planned.Lod != source.Mesh.Lod || planned.MeshOrdinal != source.Mesh.MeshOrdinal || planned.BoneIndex != bone.BoneIndex
            || planned.Storage != "center_half_extent_f32" || planned.CoordinateSpace != "render_inverse_bind"
            || planned.SourcePayloadHash != payload || planned.ContributorCount != bone.InfluencedVertices.Length || planned.ContributorCount != 1
            || planned.ContributorMeshVertexIndex != original.ContributorMeshVertexIndex || current.ContributorMeshVertexIndex != original.ContributorMeshVertexIndex
            || planned.ContributorSetHash != contributors || planned.Policy != intent.ZeroBoneBoxPolicy || planned.Disposition != "preserve_unverified"
            || !bone.InfluencedVertices.SequenceEqual(outputAnalysis.InfluencedVertices)
            || planned.CoordinateMatrixHash != HashExperimentalWords(bone.InverseBindPose.ToArray())
            || !planned.CoordinateMatrixWords.SequenceEqual(original.MatrixWords) || !original.MatrixWords.SequenceEqual(current.MatrixWords)
            || !planned.OriginalWords.SequenceEqual(original.OriginalWords) || !original.OriginalWords.SequenceEqual(current.OriginalWords))
            throw CoordinatedDrift("Zero-box identity, single complete contributor, matrix or exact typed words changed.");
        ZeroRenderSpherePreservationEvidence? sphere = null;
        if (original.ZeroRenderSphere is { } originalSphere)
        {
            var field = $"m_skeleton.m_bones[{bone.BoneIndex}].m_flSphereRadius";
            var expected = target.ZeroRenderSphereTargets.Single(z => z.ResourceBlockIndex == source.Mesh.BlockIndex && z.FieldPath == field);
            if (current.ZeroRenderSphere != originalSphere || expected.PairedZeroBoxFieldPath != path || expected.ContributorSetHash != contributors
                || expected.SourcePayloadHash != payload || expected.Storage != originalSphere.Storage || expected.OriginalWord != originalSphere.OriginalWord
                || expected.Policy != intent.ZeroRenderSpherePolicy || expected.Disposition != "preserve_unverified")
                throw CoordinatedDrift("Paired zero sphere storage, exact word or contributor pairing changed.");
            sphere = new(expected, current.ZeroRenderSphere.Storage, current.ZeroRenderSphere.OriginalWord, "passed", "untested", "untested");
        }
        else if (current.ZeroRenderSphere is not null || target.ZeroRenderSphereTargets.Any(z => z.ResourceBlockIndex == source.Mesh.BlockIndex && z.PairedZeroBoxFieldPath == path))
            throw CoordinatedDrift("A paired zero sphere was invented or removed.");
        return (new(planned, current.OriginalWords, "passed", "untested", "untested"), sphere);
    }
}
