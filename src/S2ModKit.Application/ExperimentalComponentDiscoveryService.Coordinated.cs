using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed partial class ExperimentalComponentDiscoveryService
{
    public async Task<ComponentDiscoveryResultV2> DiscoverCoordinatedAsync(ArtifactContent input, ModelSnapshot model, CancellationToken token = default)
    {
        var previous = await DiscoverEllipsoidAsync(input, model, token).ConfigureAwait(false);
        var candidates = new List<ComponentCandidateV2>();
        foreach (var candidate in previous.Candidates)
        {
            token.ThrowIfCancellationRequested();
            var capabilities = candidate.Capabilities.ToList();
            try
            {
                var union = ComponentCandidateUnionBuilder.Create(model, [candidate], 4);
                _ = CoordinatedSelection.ResolveMembers(model, union);
                var bounds = union.SelectedDrawCalls.Select(c => model.Lods.Single(l => l.Level == c.Lod).Meshes.Single(m => m.MeshOrdinal == c.MeshOrdinal)
                    .Geometry!.DrawCalls.Single(d => d.DrawCallId == c.DrawCallId).Bounds).ToArray();
                var full = MathF.BitDecrement(bounds.Min(b => b.Min.Z));
                var pinned = MathF.BitDecrement(full);
                var options = new CoordinatedScaffoldOptions(new("preserve_unverified", 1), new("reject", 1), new("reject", 1),
                    new CoordinatedAxisRampField
                    {
                        Version = 1,
                        CoordinateSpace = "model",
                        Axis = "z",
                        PinnedThrough = pinned,
                        FullFrom = full,
                        Pivot = new() { X = bounds[0].Min.X, Y = bounds[0].Min.Y, Z = full },
                        UniformScale = 1.01f
                    }, 64);
                capabilities.Add(Probe(input, model, union, options).Capability);
            }
            catch (S2ModKitException e) when (e.Error.Category is ErrorCategory.SelectionOrLod or ErrorCategory.UnsupportedCapability or ErrorCategory.CliOrSchema)
            { capabilities.Add(new("transform_component", 8, "unsupported", [new(e.Error.Code, e.Error.Summary)], [])); }
            var id = candidate switch
            {
                MaterialGroupComponentCandidateV2 m => ComponentCandidateIdentity.ComputeMaterialCandidateId(m.Model, m.MaterialPath, m.Lods.ToArray(), 5),
                MeshLineageComponentCandidateV2 m => ComponentCandidateIdentity.ComputeLineageCandidateId(m.Model, m.LineageKey, m.SourceLabel, m.Lods.ToArray(), 5),
                _ => throw new InvalidOperationException("Unknown candidate kind."),
            };
            candidates.Add(candidate with { CandidateId = id, Capabilities = capabilities });
        }
        var ordered = candidates.ToArray();
        return previous with
        {
            SchemaVersion = 5,
            Candidates = ordered,
            DiscoveryFingerprint = ComponentCandidateIdentity.ComputeDiscoveryFingerprint(previous.Model, previous.Analyzer, ordered, previous.LineageDiagnostics, 5)
        };
    }

    internal CoordinatedSelectionProbe Probe(ArtifactContent input, ModelSnapshot model, ComponentCandidateUnion union, CoordinatedScaffoldOptions options,
        ContentHash? discoveryFingerprint = null)
    {
        if (planner is null) throw Errors.Unsupported("EXPERIMENTAL_PLANNER_UNAVAILABLE", "No experimental planner is configured.", "Configure a compatible model adapter.");
        var recipe = CoordinatedSelection.CreateRecipe(input, model, union, options);
        var plan = MutationPlanner.CreatePlan(model, recipe, input, planner);
        _ = MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(plan));
        var target = plan.Operations.Single().CoordinatedTransformTarget!;
        return new(discoveryFingerprint ?? plan.Fingerprint, union.CandidateIds, target.CoordinatedTransform.Members,
            new("transform_component", 8, "available", [new("COORDINATED_UNION_PROBE_VERIFIED",
                "Exact common-field union planner probe passed. Complete mechanical buffers only; excluded siblings, procedural hair, attachments, clothing fit and live consumers are not qualified.")],
                target.Buffers.GroupBy(b => b.Lod).Select(g => new ComponentGeometryLodFacts(g.Key, g.Sum(b => b.VertexCount),
                    ContentHash.Compute(JsonDefaults.SerializeToUtf8(g.Select(b => b.VertexSetHash).ToArray())), true)).ToArray()));
    }
}
