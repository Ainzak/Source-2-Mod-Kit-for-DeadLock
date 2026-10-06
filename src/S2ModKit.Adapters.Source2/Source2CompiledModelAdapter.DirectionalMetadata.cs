using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    private static (List<PlannedExperimentalBoxTarget> Boxes, List<DirectionalBoxClosure> Closures,
        List<PlannedExperimentalPreservationTarget> Preserved) PlanDirectionalMetadata(ArtifactContent input, ParsedModel parsed,
        IReadOnlyList<CoordinatedResolvedBuffer> members, Dictionary<(int Mesh, int Buffer), DirectionalWordCalculation> calculations,
        IReadOnlyList<DirectionalSourceBuffer> context)
    {
        var boxes = new List<PlannedExperimentalBoxTarget>(); var closures = new List<DirectionalBoxClosure>();
        var preserved = new List<PlannedExperimentalPreservationTarget>();
        foreach (var group in members.GroupBy(m => m.Profile.Mesh.MeshOrdinal))
        {
            var first = group.First().Profile;
            var final = (Point3[])first.AllBeforePositions.Clone();
            foreach (var member in group)
                calculations[(member.Profile.Mesh.MeshOrdinal, member.Profile.Vertices.Snapshot.Ordinal)].Points.CopyTo(final, member.Profile.BufferBaseOffset);
            AddBox(PlanExperimentalSceneBox(input, first, final), Enumerable.Range(0, final.Length).ToArray());
            var selected = group.SelectMany(m => m.Profile.SelectedVertices.Select(i => i + m.Profile.BufferBaseOffset)).ToHashSet();
            var affected = first.Metadata.BoneBounds.Where(b => b.InfluencedVertices.Any(selected.Contains)).ToArray();
            if (affected.Length == 0) throw DirectionalFailure("EXPERIMENTAL_SKINNING_UNSUPPORTED", "Complete affected bone closure is absent.");
            foreach (var bone in affected) AddBox(PlanExperimentalBoneBox(input, first, bone, final), bone.InfluencedVertices);
            AddExperimentalRenderSphereTargets(parsed, first, affected.Select(b => b.BoneIndex).ToHashSet(), preserved);

            void AddBox(PlannedExperimentalBoxTarget box, IReadOnlyList<int> indices)
            {
                var rows = new List<DirectionalBoxContributor>(); var baseIndex = 0;
                foreach (var c in context.Where(c => c.Facts.MeshOrdinal == first.Mesh.MeshOrdinal))
                {
                    var membersInBuffer = indices.Where(i => i >= baseIndex && i < baseIndex + c.Facts.VertexCount).Select(i => i - baseIndex).ToArray();
                    rows.Add(new(c.Facts.Lod, c.Facts.MeshOrdinal, c.Facts.VertexBufferOrdinal, membersInBuffer.Length,
                        DirectionalContractValidator.VertexSetHash(membersInBuffer), c.Facts.PositionHash,
                        DirectionalPositionWords(c.After, c.Position, Enumerable.Range(0, c.Facts.VertexCount).ToArray())));
                    baseIndex = checked(baseIndex + c.Facts.VertexCount);
                }
                var hash = MutationPlanJson.ComputeDirectionalFactsHash(rows);
                boxes.Add(box with { ContributorSetHash = hash });
                closures.Add(new(box.ResourceBlockIndex, box.FieldPath, first.Mesh.Lod, first.Mesh.MeshOrdinal, rows, hash));
            }
        }
        return (boxes, closures, preserved);
    }
}
