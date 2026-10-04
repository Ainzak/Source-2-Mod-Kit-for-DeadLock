using System.Globalization;
using System.Text;
using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed partial class ExperimentalComponentDiscoveryService
{
    public async Task<ComponentDiscoveryResultV2> DiscoverEllipsoidAsync(ArtifactContent input, ModelSnapshot model, CancellationToken token = default)
    {
        var previous = await DiscoverAsync(input, model, token).ConfigureAwait(false);
        var candidates = new List<ComponentCandidateV2>();
        foreach (var candidate in previous.Candidates)
        {
            token.ThrowIfCancellationRequested();
            var capabilities = candidate.Capabilities.ToList();
            try
            {
                if (planner is null) throw Errors.Unsupported("EXPERIMENTAL_PLANNER_UNAVAILABLE", "No experimental planner is configured.", "Configure a compatible model adapter.");
                var union = ComponentCandidateUnionBuilder.Create(model, [candidate], 3);
                var bounds = Geometry(model, union).SelectMany(g => g.Draws.Select(d => d.Bounds)).ToArray();
                var center = new TransformVector3
                {
                    X = (float)(((double)bounds.Min(b => b.Min.X) + bounds.Max(b => b.Max.X)) / 2),
                    Y = (float)(((double)bounds.Min(b => b.Min.Y) + bounds.Max(b => b.Max.Y)) / 2),
                    Z = (float)(((double)bounds.Min(b => b.Min.Z) + bounds.Max(b => b.Max.Z)) / 2),
                };
                // A spherical probe surrounds the mechanical selection. It establishes
                // storage eligibility only, never an anatomical field or user defaults.
                var radius = MathF.BitIncrement((float)Math.Max((double)bounds.Max(b => b.Max.X) - bounds.Min(b => b.Min.X),
                    Math.Max((double)bounds.Max(b => b.Max.Y) - bounds.Min(b => b.Min.Y), (double)bounds.Max(b => b.Max.Z) - bounds.Min(b => b.Min.Z))));
                var options = new EllipsoidScaffoldOptions(new("preserve_unverified", 1),
                    new(new("ellipsoid", 1, "model", center, new() { X = radius, Y = radius, Z = radius }, 1f / 16), 1.01f, new("ellipsoid_numeric", 1)));
                var recipe = CreateEllipsoidRecipe(input, model, union, options, 64);
                var plan = MutationPlanner.CreatePlan(model, recipe, input, planner);
                _ = MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(plan));
                var target = plan.Operations.Single().EllipsoidTransformTarget
                    ?? throw Errors.Unsupported("ELLIPSOID_PLAN_INCOMPLETE", "Planner did not return single-field facts.", "Reject the incomplete probe.");
                capabilities.Add(new("transform_component", 7, "available",
                    [new("ELLIPSOID_PROBE_VERIFIED", "Bounded single-field planner probe passed. Explicit center/radii/falloff still require exact planning; this is mechanical geometry, not anatomy or runtime qualification.")],
                    target.Buffers.Select(b => new ComponentGeometryLodFacts(b.Lod, b.VertexCount, b.VertexSetHash, true)).ToArray()));
            }
            catch (S2ModKitException error) when (error.Error.Category is ErrorCategory.UnsupportedCapability or ErrorCategory.SelectionOrLod or ErrorCategory.CliOrSchema)
            {
                capabilities.Add(new("transform_component", 7, "unsupported", [new(error.Error.Code, error.Error.Summary)], []));
            }
            var id = candidate switch
            {
                MaterialGroupComponentCandidateV2 material => ComponentCandidateIdentity.ComputeMaterialCandidateId(material.Model, material.MaterialPath, material.Lods.ToArray(), 4),
                MeshLineageComponentCandidateV2 lineage => ComponentCandidateIdentity.ComputeLineageCandidateId(lineage.Model, lineage.LineageKey, lineage.SourceLabel, lineage.Lods.ToArray(), 4),
                _ => throw new InvalidOperationException("Unknown candidate kind."),
            };
            candidates.Add(candidate with { CandidateId = id, Capabilities = capabilities });
        }
        var ordered = candidates.ToArray();
        return previous with
        {
            SchemaVersion = 4,
            Candidates = ordered,
            DiscoveryFingerprint = ComponentCandidateIdentity.ComputeDiscoveryFingerprint(previous.Model, previous.Analyzer, ordered, previous.LineageDiagnostics, 4),
        };
    }

    internal static RecipeDocument CreateEllipsoidRecipe(ArtifactContent input, ModelSnapshot model, ComponentCandidateUnion union, EllipsoidScaffoldOptions options, float limit)
    {
        var geometry = Geometry(model, union);
        var seed = new StringBuilder("ellipsoid_scaffold@1\n").Append(input.ContentHash).Append('\n');
        foreach (var call in union.SelectedDrawCalls) seed.Append(call.DrawCallId).Append('\n');
        seed.Append(JsonDefaults.Serialize(options)).Append('\n').Append(limit.ToString("R", CultureInfo.InvariantCulture));
        var id = ContentHash.Compute(Encoding.UTF8.GetBytes(seed.ToString())).Value[..16];
        var recipe = new RecipeDocument
        {
            SchemaVersion = 8,
            RecipeId = $"scaffold-{id}",
            InputHash = input.ContentHash,
            Operations = [new TransformComponentOperation
            {
                OperationId = $"transform-{id}", Version = 7, Granularity = "ellipsoid_vertices", Transform = null!,
                Selector = new() { Kind = "draw_call_ids", DrawCallIds = union.SelectedDrawCalls.Select(c => c.DrawCallId).ToArray() },
                ExpectedMatchesByLod = union.SelectedDrawCalls.GroupBy(c => c.Lod).ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => g.Count()),
                ExpectedVerticesByLod = geometry.ToDictionary(g => g.Lod.ToString(CultureInfo.InvariantCulture), g => g.Buffer.VertexCount),
                RuntimeMetadataPolicy = options.Policy, LocalTransform = options.LocalTransform, Limits = new() { MaximumVertexDisplacement = limit },
            }],
        };
        EllipsoidContractValidator.ValidateRecipe(recipe);
        return recipe;
    }
}
