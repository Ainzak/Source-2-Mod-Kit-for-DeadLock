using System.Globalization;
using System.Text;
using S2ModKit.Domain;

namespace S2ModKit.Application;

/// <summary>Explicit opt-in discovery. Availability proves a bounded probe, never arbitrary anatomy or all parameters.</summary>
public sealed class ExperimentalComponentDiscoveryService(IComponentCapabilityAnalyzer? analyzer, ITransformOperationPlanner? planner)
{
    public async Task<ComponentDiscoveryResultV2> DiscoverAsync(ArtifactContent input, ModelSnapshot model, CancellationToken token = default)
    {
        var strict = await new PreciseComponentDiscoveryService(analyzer).DiscoverAsync(input, model, token).ConfigureAwait(false);
        var candidates = new List<ComponentCandidateV2>();
        foreach (var candidate in strict.Candidates)
        {
            token.ThrowIfCancellationRequested();
            var capabilities = candidate.Capabilities.ToList();
            foreach (var version in new[] { 5, 6 })
            {
                try
                {
                    if (planner is null) throw Errors.Unsupported("EXPERIMENTAL_PLANNER_UNAVAILABLE", "No experimental planner is configured.", "Configure a compatible model adapter.");
                    var union = ComponentCandidateUnionBuilder.Create(model, [candidate]);
                    var options = new ExperimentalScaffoldOptions(new("preserve_unverified", 1),
                        new() { Kind = "selection_bounds_center", ReferenceLod = model.Lods.Min(lod => lod.Level) });
                    if (version == 6)
                    {
                        var bounds = Geometry(model, union).SelectMany(item => item.Draws.Select(draw => draw.Bounds)).ToArray();
                        var full = MathF.BitDecrement(bounds.Min(box => box.Min.Z));
                        var pinned = MathF.BitDecrement(full);
                        // Keep the ramp pivot at its upper threshold: even an empty,
                        // adjacent-float transition must satisfy the global derivative bound.
                        var center = new TransformVector3
                        {
                            X = (float)(((double)bounds.Min(box => box.Min.X) + bounds.Max(box => box.Max.X)) / 2),
                            Y = (float)(((double)bounds.Min(box => box.Min.Y) + bounds.Max(box => box.Max.Y)) / 2),
                            Z = full,
                        };
                        options = options with { Pivot = new() { Kind = "explicit_point", Point = center }, Region = new("axis_ramp", 1, "z", pinned, full) };
                    }
                    // Full-strength region probe only establishes enclosing storage/triangle
                    // eligibility. A caller's partial mask still needs its own exact dry-run.
                    var recipe = CreateRecipe(input, model, union, options, 1.01f, 64);
                    var plan = MutationPlanner.CreatePlan(model, recipe, input, planner);
                    var target = plan.Operations.Single().ExperimentalTransformTarget
                        ?? throw Errors.Unsupported("EXPERIMENTAL_PLAN_INCOMPLETE", "Planner did not return experimental facts.", "Reject the incomplete probe.");
                    capabilities.Add(new("transform_component", version, "available",
                        [new("EXPERIMENTAL_PROBE_VERIFIED", "Bounded planner probe passed; custom parameters require a dry-run. Spheres, proxies, collision and visual quality remain unverified.")],
                        target.GeometryTargets.Select(g => new ComponentGeometryLodFacts(g.Lod, g.SelectedVertexCount, g.VertexSetHash, true)).ToArray()));
                }
                catch (S2ModKitException error) when (error.Error.Category is ErrorCategory.UnsupportedCapability or ErrorCategory.SelectionOrLod or ErrorCategory.CliOrSchema)
                {
                    capabilities.Add(new("transform_component", version, "unsupported", [new(error.Error.Code, error.Error.Summary)], []));
                }
            }
            var id = candidate switch
            {
                MaterialGroupComponentCandidateV2 material => ComponentCandidateIdentity.ComputeMaterialCandidateId(material.Model, material.MaterialPath, material.Lods.ToArray(), 3),
                MeshLineageComponentCandidateV2 lineage => ComponentCandidateIdentity.ComputeLineageCandidateId(lineage.Model, lineage.LineageKey, lineage.SourceLabel, lineage.Lods.ToArray(), 3),
                _ => throw new InvalidOperationException("Unknown candidate kind."),
            };
            candidates.Add(candidate with { CandidateId = id, Capabilities = capabilities });
        }
        var ordered = candidates.ToArray();
        return strict with
        {
            SchemaVersion = 3,
            Candidates = ordered,
            ExperimentalPolicy = new("preserve_unverified", 1),
            DiscoveryFingerprint = ComponentCandidateIdentity.ComputeDiscoveryFingerprint(strict.Model, strict.Analyzer, ordered, strict.LineageDiagnostics, 3)
        };
    }

    internal static RecipeDocument CreateRecipe(ArtifactContent input, ModelSnapshot model, ComponentCandidateUnion union,
        ExperimentalScaffoldOptions options, float scale, float limit)
    {
        var geometry = Geometry(model, union);
        if (options.Pivot.Kind != "explicit_point" && options.Pivot.ReferenceLod is null)
            options = options with { Pivot = options.Pivot with { ReferenceLod = model.Lods.Min(lod => lod.Level) } };
        var version = options.Region is null ? 5 : 6;
        var seed = new StringBuilder("experimental_scaffold@1\n").Append(input.ContentHash).Append('\n');
        foreach (var call in union.SelectedDrawCalls) seed.Append(call.DrawCallId).Append('\n');
        seed.Append(JsonDefaults.Serialize(options)).Append('\n').Append(scale.ToString("R", CultureInfo.InvariantCulture))
            .Append('\n').Append(limit.ToString("R", CultureInfo.InvariantCulture));
        var id = ContentHash.Compute(Encoding.UTF8.GetBytes(seed.ToString())).Value[..16];
        var recipe = new RecipeDocument
        {
            SchemaVersion = version == 5 ? 6 : 7,
            RecipeId = $"scaffold-{id}",
            InputHash = input.ContentHash,
            Operations = [new TransformComponentOperation { OperationId = $"transform-{id}", Version = version,
                Granularity = version == 5 ? "draw_call_vertices" : "axis_ramp_vertices",
                Selector = new() { Kind = "draw_call_ids", DrawCallIds = union.SelectedDrawCalls.Select(call => call.DrawCallId).ToArray() },
                ExpectedMatchesByLod = union.SelectedDrawCalls.GroupBy(call => call.Lod).ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => g.Count()),
                ExpectedVerticesByLod = geometry.ToDictionary(g => g.Lod.ToString(CultureInfo.InvariantCulture), g => g.Buffer.VertexCount),
                RuntimeMetadataPolicy = options.Policy, Region = options.Region,
                Transform = new() { Pivot = options.Pivot, UniformScale = scale }, Limits = new() { MaximumVertexDisplacement = limit } }]
        };
        RecipeValidator.Validate(recipe);
        return recipe;
    }

    private static (int Lod, VertexBufferSnapshot Buffer, DrawCallGeometrySnapshot[] Draws)[] Geometry(ModelSnapshot model, ComponentCandidateUnion union)
    {
        var groups = union.SelectedDrawCalls.GroupBy(call => call.Lod).OrderBy(group => group.Key).ToArray();
        if (!groups.Select(g => g.Key).SequenceEqual(model.Lods.Select(l => l.Level).Order()))
            throw Errors.Unsupported("COMPONENT_INCOMPLETE_LOD_COVERAGE", "The enclosing selection is absent from a present LOD.", "Choose complete all-LOD membership.");
        return groups.Select(group =>
        {
            var meshIds = group.Select(call => call.MeshOrdinal).Distinct().ToArray();
            if (meshIds.Length != 1) throw Layout();
            var mesh = model.Lods.Single(lod => lod.Level == group.Key).Meshes.Single(m => m.MeshOrdinal == meshIds[0]);
            if (mesh.Geometry is not { Status: "ready" } geometry) throw Layout();
            var draws = group.Select(call => geometry.DrawCalls.Single(draw => draw.DrawCallId == call.DrawCallId)).ToArray();
            var buffers = draws.Select(draw => draw.VertexBufferOrdinal).Distinct().ToArray();
            if (buffers.Length != 1) throw Layout();
            return (group.Key, geometry.VertexBuffers.Single(buffer => buffer.Ordinal == buffers[0]), draws);
        }).ToArray();
    }

    private static S2ModKitException Layout() => Errors.Unsupported("EXPERIMENTAL_COMPLETE_BUFFER_REQUIRED",
        "The selection needs one characterized ordinary buffer in one mesh per LOD.", "Choose another component; do not infer ownership from a label.");
}
