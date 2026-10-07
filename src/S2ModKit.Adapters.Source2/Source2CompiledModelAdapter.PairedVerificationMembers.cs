using System.Globalization;
using System.Numerics;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    // Only the characterized ordinary-buffer reader is shared; no planner/member resolver is called.
    private static CoordinatedResolvedBuffer[] DiscoverPairedVerificationMembers(ArtifactContent input, ParsedModel parsed, PlannedOperation operation)
    {
        var intent = PairedContractValidator.Operation(operation);
        if (HasIncompleteMdatCoverage(parsed.Snapshot) || parsed.Envelope.Blocks.Any(b => b.Type == "MBUF")
            || parsed.MeshesByOrdinal.Values.Any(m => BitOperations.PopCount(m.LodMask) != 1))
            throw PairedDrift("Incomplete/excluded/shared-LOD or raw MBUF storage is outside this profile.");
        if (!parsed.Snapshot.Lods.Select(l => l.Level).Order().SequenceEqual(intent.PairedTransform!.Members[0].Lods.Select(l => l.Lod)))
            throw PairedDrift("Source/output all-present LOD coverage differs.");
        var selected = parsed.Snapshot.Lods.SelectMany(l => l.Meshes.SelectMany(m => m.DrawCalls
            .Where(d => intent.Selector.DrawCallIds!.Contains(d.Id, StringComparer.Ordinal))
            .Select(d => new SelectedDrawCall(l.Level, m.ResourcePath, m.MeshOrdinal, m.ResourceBlockIndex, d.Id, d.MaterialPath, d.DrawCallOrdinal, d.IndexStart, d.IndexCount))))
            .OrderBy(c => c.Lod).ThenBy(c => c.ResourcePath, StringComparer.Ordinal).ThenBy(c => c.MeshOrdinal).ThenBy(c => c.DrawCallOrdinal).ToArray();
        SameDirectional(selected, operation.SelectedDrawCalls, "exact draw-call selection");
        var result = new List<CoordinatedResolvedBuffer>();
        foreach (var member in intent.PairedTransform.Members)
            foreach (var map in member.Lods)
            {
                var calls = selected.Where(c => map.DrawCallIds.Contains(c.DrawCallId, StringComparer.Ordinal)).ToArray();
                if (calls.Length != map.DrawCallIds.Count || calls.Any(c => c.Lod != map.Lod) || calls.Select(c => c.MeshOrdinal).Distinct().Count() != 1)
                    throw PairedDrift("Member mapping is incomplete or ambiguous.");
                var reader = new TransformComponentOperation
                {
                    Version = 5,
                    Granularity = "draw_call_vertices",
                    RuntimeMetadataPolicy = intent.RuntimeMetadataPolicy,
                    ExpectedVerticesByLod = member.Lods.ToDictionary(l => l.Lod.ToString(CultureInfo.InvariantCulture), l => l.ExpectedVertices)
                };
                var profile = CreateRootBufferProfile(new(input, parsed.Snapshot, reader, calls), parsed, calls, preserveAuthoredEnvelopes: true, completeOrdinaryBuffer: true, pairedPreservation: true);
                ValidateExperimentalVertexStreams(profile, pairedPreservation: true);
                result.Add(new(member.MemberId, profile));
            }
        if (result.Select(m => (m.Profile.Mesh.MeshOrdinal, m.Profile.Vertices.Snapshot.Ordinal)).Distinct().Count() != result.Count
            || result.Select(m => m.Profile.Vertices.Snapshot.ResourceBlockIndex).Distinct().Count() != result.Count)
            throw PairedDrift("Selected member storage aliases another member.");
        return result.OrderBy(m => m.Profile.Mesh.Lod).ThenBy(m => m.Profile.Mesh.MeshOrdinal).ThenBy(m => m.Profile.Vertices.Snapshot.Ordinal).ToArray();
    }

}
