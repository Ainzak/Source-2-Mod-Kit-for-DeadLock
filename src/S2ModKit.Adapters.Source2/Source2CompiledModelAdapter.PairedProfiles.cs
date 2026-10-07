using System.Globalization;
using System.Numerics;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    private static CoordinatedResolvedBuffer[] ResolvePairedProfiles(TransformPlanningRequest request, ParsedModel parsed)
    {
        PairedContractValidator.ValidateRecipe(new() { SchemaVersion = 11, RecipeId = "directional", InputHash = request.Input.ContentHash, Operations = [request.Operation] });
        if (HasIncompleteMdatCoverage(parsed.Snapshot) || parsed.Envelope.Blocks.Any(b => b.Type == "MBUF")
            || parsed.MeshesByOrdinal.Values.Any(m => BitOperations.PopCount(m.LodMask) != 1))
            throw DirectionalFailure("EXPERIMENTAL_LAYOUT_UNSUPPORTED", "Incomplete, shared-LOD and raw MBUF source context is unsupported.");
        var declared = request.Operation.PairedTransform!;
        if (!parsed.Snapshot.Lods.Select(l => l.Level).Order().SequenceEqual(declared.Members[0].Lods.Select(l => l.Lod)))
            throw DirectionalFailure("LOD_COVERAGE_INCOMPLETE", "Member maps must cover every actual LOD.");
        var result = new List<CoordinatedResolvedBuffer>();
        foreach (var member in declared.Members)
        {
            var readerOperation = new TransformComponentOperation
            {
                Version = 5,
                Granularity = "draw_call_vertices",
                RuntimeMetadataPolicy = request.Operation.RuntimeMetadataPolicy,
                ExpectedVerticesByLod = member.Lods.ToDictionary(l => l.Lod.ToString(CultureInfo.InvariantCulture), l => l.ExpectedVertices),
            };
            foreach (var map in member.Lods)
            {
                var calls = request.SelectedDrawCalls.Where(c => map.DrawCallIds.Contains(c.DrawCallId, StringComparer.Ordinal)).ToArray();
                if (calls.Length != map.DrawCallIds.Count || calls.Any(c => c.Lod != map.Lod) || calls.Select(c => c.MeshOrdinal).Distinct().Count() != 1)
                    throw DirectionalFailure("AFFINE_MULTI_BUFFER_OWNERSHIP_UNSUPPORTED", "Each member/LOD must resolve to exactly one complete ordinary buffer.");
                var profile = CreateRootBufferProfile(new(request.Input, request.Model, readerOperation, calls), parsed, calls,
                    preserveAuthoredEnvelopes: true, completeOrdinaryBuffer: true, pairedPreservation: true);
                ValidateExperimentalVertexStreams(profile, pairedPreservation: true);
                result.Add(new(member.MemberId, profile));
            }
        }
        if (result.Select(m => (m.Profile.Mesh.MeshOrdinal, m.Profile.Vertices.Snapshot.Ordinal)).Distinct().Count() != result.Count
            || result.Select(m => m.Profile.Vertices.Snapshot.ResourceBlockIndex).Distinct().Count() != result.Count)
            throw DirectionalFailure("AFFINE_MULTI_BUFFER_OWNERSHIP_UNSUPPORTED", "Member vertex blocks or buffer ownership overlap.");
        return result.OrderBy(m => m.Profile.Mesh.Lod).ThenBy(m => m.Profile.Mesh.MeshOrdinal).ThenBy(m => m.Profile.Vertices.Snapshot.Ordinal).ToArray();
    }

}
