using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    private static List<ExperimentalBoxEvidence>
        VerifyCoordinatedPositiveBoxes(ArtifactContent input, Source2AffineProfile source,
            Source2AffineProfile observed, Source2BoneBoundsAnalysis[] affected, IReadOnlyList<PlannedExperimentalBoxTarget> targets)
    {
        var boxEvidence = new List<ExperimentalBoxEvidence>();
        var sourceBoxes = targets.Where(box => box.ResourceBlockIndex == source.Mesh.BlockIndex).ToArray();
        var requiredFields = affected.Select(bone => $"m_skeleton.m_bones[{bone.BoneIndex}].m_bbox.m_vecCenter+m_vecSize")
            .Prepend("m_sceneObjects[0].m_vMinBounds+m_vMaxBounds").ToHashSet(StringComparer.Ordinal);
        if (!requiredFields.SetEquals(sourceBoxes.Select(box => box.FieldPath)) || sourceBoxes.Length != requiredFields.Count)
            throw CoordinatedDrift("An affected box target is missing, duplicated or outside the independently resolved closure.");
        foreach (var box in sourceBoxes)
        {
            var originalWords = ReadExperimentalBox(source.Mesh.Block.Data, box);
            var outputWords = ReadExperimentalBox(observed.Mesh.Block.Data, box);
            if (!originalWords.SequenceEqual(box.OriginalWords) || !outputWords.SequenceEqual(box.ExpectedWords))
                throw CoordinatedDrift("A raw original or observed box differs from the frozen words.");
            Point3[] contributors;
            float[] matrix;
            int[] membership;
            EnvelopeVerification result;
            if (box.Storage == "min_max")
            {
                matrix = ExperimentalIdentityMatrix;
                membership = Enumerable.Range(0, observed.AllBeforePositions.Length).ToArray();
                contributors = observed.AllBeforePositions;
                result = CullingEnvelopeVerifier.Verify(
                    new Bounds3(ExperimentalPointWords(originalWords, 0), ExperimentalPointWords(originalWords, 3)), contributors,
                    new Bounds3(ExperimentalPointWords(outputWords, 0), ExperimentalPointWords(outputWords, 3)));
            }
            else
            {
                var bone = affected.Single(item => box.FieldPath == $"m_skeleton.m_bones[{item.BoneIndex}].m_bbox.m_vecCenter+m_vecSize");
                matrix = bone.InverseBindPose.ToArray();
                membership = bone.InfluencedVertices.ToArray();
                contributors = membership.SelectMany(index =>
                {
                    var enclosure = AffinePointEnclosure.Enclose(observed.AllBeforePositions[index], matrix);
                    return new[] { enclosure.Min, enclosure.Max };
                }).ToArray();
                result = CullingEnvelopeVerifier.Verify(
                    new CenterHalfExtentBounds(ExperimentalPointWords(originalWords, 0), ExperimentalPointWords(originalWords, 3)), contributors,
                    new CenterHalfExtentBounds(ExperimentalPointWords(outputWords, 0), ExperimentalPointWords(outputWords, 3)));
            }
            if (result.Status != EnvelopeVerificationStatus.Passed || box.ContributorCount != membership.Length
                || box.ContributorSetHash != ExperimentalContributorsHash(input, source, membership)
                || box.CoordinateMatrixHash != HashExperimentalWords(matrix) || !box.CoordinateMatrixWords.SequenceEqual(Words(matrix)))
                throw CoordinatedDrift($"Independent containment/policy or contributor identity failed for {box.FieldPath}: {result.Status}.");
            VerifyEllipsoidBoxGrowth(box);
            boxEvidence.Add(new(box, outputWords, "passed"));
        }
        return boxEvidence;
    }

}
