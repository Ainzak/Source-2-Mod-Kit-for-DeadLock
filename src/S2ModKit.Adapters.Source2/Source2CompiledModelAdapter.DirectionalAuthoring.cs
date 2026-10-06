using System.Globalization;
using System.Numerics;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter : IDirectionalAuthoringSourceReader
{
    public Task<DirectionalAuthoringSource> ReadDirectionalAuthoringSourceAsync(ArtifactContent input, IReadOnlyList<CoordinatedMember> members, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (ContentHash.Compute(input.Bytes.Span) != input.ContentHash || members.Count is < 1 or > 16)
            throw DirectionalFailure("DIRECTIONAL_AUTHORING_SOURCE_INVALID", "Require immutable input and 1 through 16 complete members.");
        using var parsed = Parse(input, retainGeometryAnalysis: true);
        if (HasIncompleteMdatCoverage(parsed.Snapshot) || parsed.Envelope.Blocks.Any(b => b.Type == "MBUF")
            || parsed.MeshesByOrdinal.Values.Any(m => BitOperations.PopCount(m.LodMask) != 1)
            || members.Any(m => !m.Lods.Select(l => l.Lod).SequenceEqual(parsed.Snapshot.Lods.Select(l => l.Level).Order())))
            throw DirectionalFailure("EXPERIMENTAL_LAYOUT_UNSUPPORTED", "Source authoring requires complete ordinary all-LOD context.");
        CheckPreviewBudget(parsed.Snapshot.Lods.Sum(l => l.Meshes.Sum(m => m.Geometry!.VertexBuffers.Sum(b => (long)b.VertexCount))),
            parsed.Snapshot.Lods.Sum(l => l.Meshes.Sum(m => m.DrawCalls.Sum(c => (long)c.IndexCount))));
        var selected = new List<CoordinatedResolvedBuffer>();
        foreach (var member in members)
            foreach (var map in member.Lods)
            {
                token.ThrowIfCancellationRequested();
                var calls = parsed.Snapshot.Lods.SelectMany(l => l.Meshes.SelectMany(m => m.DrawCalls.Where(d => map.DrawCallIds.Contains(d.Id, StringComparer.Ordinal))
                    .Select(d => new SelectedDrawCall(l.Level, m.ResourcePath, m.MeshOrdinal, m.ResourceBlockIndex, d.Id, d.MaterialPath, d.DrawCallOrdinal, d.IndexStart, d.IndexCount)))).ToArray();
                if (calls.Length != map.DrawCallIds.Count || calls.Any(c => c.Lod != map.Lod) || calls.Select(c => c.MeshOrdinal).Distinct().Count() != 1)
                    throw DirectionalFailure("DIRECTIONAL_AUTHORING_SOURCE_INVALID", "Member source selection is incomplete or ambiguous.");
                var reader = new TransformComponentOperation
                {
                    Version = 5,
                    Granularity = "draw_call_vertices",
                    RuntimeMetadataPolicy = new("preserve_unverified", 1),
                    ExpectedVerticesByLod = member.Lods.ToDictionary(l => l.Lod.ToString(CultureInfo.InvariantCulture), l => l.ExpectedVertices)
                };
                var profile = CreateRootBufferProfile(new(input, parsed.Snapshot, reader, calls), parsed, calls, preserveAuthoredEnvelopes: true, completeOrdinaryBuffer: true);
                ValidateExperimentalVertexStreams(profile);
                selected.Add(new(member.MemberId, profile));
            }
        if (selected.Select(m => m.Profile.Vertices.Snapshot.ResourceBlockIndex).Distinct().Count() != selected.Count)
            throw DirectionalFailure("DIRECTIONAL_AUTHORING_SOURCE_INVALID", "Selected member storage overlaps.");
        var ordered = selected.OrderBy(m => m.Profile.Mesh.Lod).ThenBy(m => m.Profile.Mesh.MeshOrdinal).ThenBy(m => m.Profile.Vertices.Snapshot.Ordinal).ToArray();
        var (model, _, _) = ResolveExperimentalRootMetadata(parsed, ordered.Select(m => m.Profile).ToArray());
        var context = ReadDirectionalVerificationContext(input, parsed, parsed, model, ordered);
        var skeletonHash = KvSemanticHasher.ComputeComplete(ExperimentalCollection(model.Data, "m_modelSkeleton"));
        var bones = new List<DirectionalAuthoringBone>();
        for (var i = 0; i < model.Skeleton.Bones.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var name = model.Skeleton.Bones[i].Name;
            if (!context.Any(c => c.RootContributors.TryGetValue(i, out var indices) && indices.Length > 0))
            { bones.Add(new(name, i, null, "No nonzero source contributors.")); continue; }
            if (context.Any(c => c.Frame is null && c.RootContributors.TryGetValue(i, out var indices) && indices.Length > 0))
            { bones.Add(new(name, i, null, "A contributor context has an uncharacterized packed frame; no complete assertion can be supplied.")); continue; }
            var records = context.Select(c => AuditDirectionalFixedWords(c, c.RootContributors.GetValueOrDefault(i) ?? [])).ToArray();
            var assertion = new DirectionalBoneAssertion
            {
                Version = 1,
                AssertionId = "protect-root-" + i.ToString(CultureInfo.InvariantCulture),
                BoneName = name,
                BoneIndex = i,
                RootSkeletonHash = skeletonHash,
                Lods = records.GroupBy(r => r.Lod).OrderBy(g => g.Key)
                    .Select(g => new DirectionalBoneLod(g.Key, DirectionalContractValidator.ContributorSetHash(g), g.Sum(r => r.VertexCount))).ToArray()
            };
            bones.Add(new(name, i, assertion, null));
        }
        return Task.FromResult(new DirectionalAuthoringSource(input.ContentHash, members,
            context.Where(c => c.Facts.Selected).Select(c => new DirectionalAuthoringBuffer(c.MemberId!, c.Facts, c.SourcePoints)).ToArray(),
            context.Select(c => c.Facts).ToArray(), bones));
    }
}
