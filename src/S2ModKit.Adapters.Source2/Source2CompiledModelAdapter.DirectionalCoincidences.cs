using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    private sealed record DirectionalPositionRecord(int Mesh, int Buffer, int Vertex, bool Selected, Point3 Source, Point3 Output);
    private static (uint X, uint Y, uint Z) DirectionalCoincidenceKey(Point3 p) =>
        (p.X == 0 ? 0 : BitConverter.SingleToUInt32Bits(p.X), p.Y == 0 ? 0 : BitConverter.SingleToUInt32Bits(p.Y), p.Z == 0 ? 0 : BitConverter.SingleToUInt32Bits(p.Z));

    internal static DirectionalCoincidenceLod[] DirectionalCoincidences(IReadOnlyList<DirectionalSourceBuffer> context)
    {
        var result = new List<DirectionalCoincidenceLod>();
        foreach (var lod in context.GroupBy(c => c.Facts.Lod).OrderBy(g => g.Key))
        {
            var records = lod.OrderBy(c => c.Facts.MeshOrdinal).ThenBy(c => c.Facts.VertexBufferOrdinal)
                .SelectMany(c => Enumerable.Range(0, c.Facts.VertexCount).Select(i => new DirectionalPositionRecord(c.Facts.MeshOrdinal, c.Facts.VertexBufferOrdinal,
                    i, c.Facts.Selected, c.SourcePoints[i], c.FinalPoints[i]))).ToArray();
            var groups = records.GroupBy(r => DirectionalCoincidenceKey(r.Source)).Where(g => g.Count() > 1).OrderBy(g => g.Key).ToArray();
            long pairs = 0;
            foreach (var group in groups)
            {
                pairs = checked(pairs + (long)group.Count() * (group.Count() - 1) / 2);
                if (group.Any(r => r.Selected && DirectionalCoincidenceKey(r.Source) != DirectionalCoincidenceKey(r.Output)) && group.Any(r => !r.Selected))
                    throw DirectionalFailure("DIRECTIONAL_EXCLUDED_COINCIDENT_MATE", $"LOD {lod.Key}: a moving selected record has an excluded exact source-position mate.");
                if (group.Select(r => DirectionalCoincidenceKey(r.Output)).Distinct().Count() != 1)
                    throw DirectionalFailure("DIRECTIONAL_RESULT_DRIFT", $"LOD {lod.Key}: common-field source copies produce different final positions.");
            }
            // Canonical equality groups specify the complete unordered pair set without quadratic
            // materialization. Group ordering and every independent record identity enter its hash.
            var pairHash = MutationPlanJson.ComputeDirectionalFactsHash(groups.Select(g => new
            {
                X = g.Key.X,
                Y = g.Key.Y,
                Z = g.Key.Z,
                Records = g.Select(r => new { r.Mesh, r.Buffer, r.Vertex }).ToArray(),
            }).ToArray());
            var sourceHash = MutationPlanJson.ComputeDirectionalFactsHash(records.Select(r => new
            { r.Mesh, r.Buffer, r.Vertex, X = BitConverter.SingleToUInt32Bits(r.Source.X), Y = BitConverter.SingleToUInt32Bits(r.Source.Y), Z = BitConverter.SingleToUInt32Bits(r.Source.Z) }).ToArray());
            result.Add(new(lod.Key, sourceHash, pairHash, records.Length, pairs,
                records.Count(r => r.Selected && RegionPositionWordsChanged(r.Source, r.Output)), 0));
        }
        return result.ToArray();
    }
}
