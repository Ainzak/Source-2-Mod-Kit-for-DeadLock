using S2ModKit.Application;
using S2ModKit.Domain;
using ValveKeyValue;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    private static (PlannedZeroBoneBoxPreservationTarget Box, PlannedZeroRenderSpherePreservationTarget? Sphere) CoordinatedZeroTargets(
        ArtifactContent input, ParsedModel parsed, Source2AffineProfile profile, Source2BoneBoundsAnalysis bone, TransformComponentOperation operation)
    {
        var rawBone = ExperimentalArray(ExperimentalCollection(profile.Mesh.Block.Data, "m_skeleton"), "m_bones")[bone.BoneIndex];
        var rawBox = ExperimentalCollection(rawBone, "m_bbox");
        var source = ReadCoordinatedZeroBoxSource(rawBox, bone.InfluencedVertices, profile.AllBeforePositions,
            ExperimentalArray(rawBone, "m_invBindPose"), bone.InverseBindPose, bone.SphereRadius, false,
            operation.ZeroBoneBoxPolicy, rawBone["m_flSphereRadius"], operation.ZeroRenderSpherePolicy);
        var path = $"m_skeleton.m_bones[{bone.BoneIndex}].m_bbox.m_vecCenter+m_vecSize";
        var payloadHash = ContentHash.Compute(parsed.Envelope.Blocks[profile.Mesh.BlockIndex].Payload.Span);
        var contributors = ExperimentalContributorsHash(input, profile, bone.InfluencedVertices);
        var box = new PlannedZeroBoneBoxPreservationTarget(profile.Mesh.Lod, profile.Mesh.MeshOrdinal, bone.BoneIndex, profile.Mesh.BlockIndex,
            path, "center_half_extent_f32", "render_inverse_bind", HashExperimentalWords(bone.InverseBindPose.ToArray()), source.MatrixWords,
            contributors, 1, source.ContributorMeshVertexIndex, payloadHash, source.OriginalWords, operation.ZeroBoneBoxPolicy!, "preserve_unverified");
        var sphere = source.ZeroRenderSphere is { } zeroSphere ? new PlannedZeroRenderSpherePreservationTarget(profile.Mesh.BlockIndex,
            $"m_skeleton.m_bones[{bone.BoneIndex}].m_flSphereRadius", path, contributors, payloadHash,
            zeroSphere.Storage, zeroSphere.OriginalWord, operation.ZeroRenderSpherePolicy!, "preserve_unverified") : null;
        return (box, sphere);
    }

    private static void AddCoordinatedRenderSphereTargets(ParsedModel parsed, Source2AffineProfile profile, HashSet<int> affectedBones,
        IReadOnlyList<PlannedZeroRenderSpherePreservationTarget> zeroSpheres, List<PlannedExperimentalPreservationTarget> targets)
    {
        var bones = ExperimentalArray(ExperimentalCollection(profile.Mesh.Block.Data, "m_skeleton"), "m_bones");
        var hash = ContentHash.Compute(parsed.Envelope.Blocks[profile.Mesh.BlockIndex].Payload.Span);
        for (var index = 0; index < bones.Count; index++)
        {
            var path = $"m_skeleton.m_bones[{index}].m_flSphereRadius";
            if (zeroSpheres.Any(s => s.ResourceBlockIndex == profile.Mesh.BlockIndex && s.FieldPath == path)) continue;
            var radius = ExperimentalFloat(bones[index]["m_flSphereRadius"]);
            var affected = affectedBones.Contains(index);
            ValidateExperimentalRadius(radius, affected);
            targets.Add(new(profile.Mesh.BlockIndex, path, "render_sphere", affected ? "affected" : "resource", "preserve_unverified", hash,
                [BitConverter.SingleToUInt32Bits(radius)]));
        }
    }
}
