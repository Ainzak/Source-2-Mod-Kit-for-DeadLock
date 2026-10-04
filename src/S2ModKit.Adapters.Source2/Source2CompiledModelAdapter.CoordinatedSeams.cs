using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    private static PlannedCoordinatedSeamInventory ResolveCoordinatedSeams(ParsedModel parsed, IReadOnlyList<CoordinatedResolvedBuffer> members,
        Dictionary<(int Mesh, int Buffer), Point3[]> predicted)
    {
        var selected = members.ToDictionary(m => (m.Profile.Mesh.MeshOrdinal, m.Profile.Vertices.Snapshot.Ordinal), m => m.Profile);
        var moving = new Dictionary<Point3, int>();
        int selectedCount = 0;
        foreach (var member in members)
        {
            var p = member.Profile;
            var after = predicted[(p.Mesh.MeshOrdinal, p.Vertices.Snapshot.Ordinal)];
            selectedCount = checked(selectedCount + p.SelectedVertices.Length);
            foreach (var index in p.SelectedVertices)
            {
                var before = Source2GeometryAnalyzer.ReadPosition(p.Vertices, index);
                if (before == after[index]) continue; // Geometric equality includes signed zero.
                moving[before] = moving.GetValueOrDefault(before) + 1;
            }
        }
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("coordinated-source-position-records-v1");
            foreach (var mesh in parsed.MeshesByOrdinal.Values.OrderBy(m => m.MeshOrdinal))
            {
                if (mesh.GeometryAnalysis is null) throw CoordinatedUnsupported("Complete resource position records are required for exact coincident-mate inspection.");
                foreach (var buffer in mesh.GeometryAnalysis.VertexBuffers.OrderBy(b => b.Snapshot.Ordinal))
                {
                    var chosen = selected.ContainsKey((mesh.MeshOrdinal, buffer.Snapshot.Ordinal));
                    writer.Write(mesh.MeshOrdinal); writer.Write(buffer.Snapshot.Ordinal); writer.Write(buffer.Snapshot.ResourceBlockIndex); writer.Write(buffer.Snapshot.VertexCount);
                    for (var index = 0; index < buffer.Snapshot.VertexCount; index++)
                    {
                        var point = Source2GeometryAnalyzer.ReadPosition(buffer, index);
                        writer.Write(index); writer.Write(BitConverter.SingleToUInt32Bits(point.X)); writer.Write(BitConverter.SingleToUInt32Bits(point.Y)); writer.Write(BitConverter.SingleToUInt32Bits(point.Z));
                        if (!chosen && moving.ContainsKey(point)) throw CoordinatedUnsupported("An excluded exact coincident position record has a moving selected mate.");
                    }
                }
            }
        }
        return new(ContentHash.Compute(bytes.ToArray()), selectedCount, moving.Count, moving.Values.Where(count => count > 1).Sum(), 0);
    }
}
