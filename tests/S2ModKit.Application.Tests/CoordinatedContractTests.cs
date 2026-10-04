using System.Text.Json.Nodes;
using NJsonSchema;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed partial class ExperimentalVisualContractTests
{
    [Fact]
    public async Task CoordinatedRecipesAreClosedAndAllCommonFieldVariantsRoundTrip()
    {
        var schema = await JsonSchema.FromFileAsync(SchemaPath("recipe.schema.json"), TestContext.Current.CancellationToken);
        CoordinatedField[] fields =
        [
            CoordinatedField(),
            new CoordinatedAxisRampField { Version = 1, CoordinateSpace = "model", Axis = "z", PinnedThrough = 0, FullFrom = 8, Pivot = new(), UniformScale = 1.5f },
            new CoordinatedEllipsoidField { Version = 1, CoordinateSpace = "model", Intent = EllipsoidIntent },
        ];
        foreach (var field in fields)
        {
            var recipe = CoordinatedRecipe(); var op = (TransformComponentOperation)recipe.Operations[0];
            var json = JsonDefaults.Serialize(recipe with { Operations = [op with { CoordinatedTransform = op.CoordinatedTransform! with { Field = field } }] });
            Assert.Equal(json, JsonDefaults.Serialize(ReadRecipe(json)));
            Assert.Empty(schema.Validate(json));
            foreach (var path in RequiredPaths(JsonNode.Parse(json)!["operations"]![0]!["coordinatedTransform"]!))
            {
                var node = JsonNode.Parse(json)!;
                RemoveNodePath(node["operations"]![0]!["coordinatedTransform"]!, path);
                Assert.Throws<S2ModKitException>(() => ReadRecipe(node.ToJsonString()));
                Assert.NotEmpty(schema.Validate(node.ToJsonString()));
            }
            foreach (var name in new[] { "transform", "region", "localTransform", "physicsPolicy", "connectedComponentIdsByLod" })
            {
                var node = JsonNode.Parse(json)!; node["operations"]![0]![name] = null;
                Assert.Throws<S2ModKitException>(() => ReadRecipe(node.ToJsonString()));
                Assert.NotEmpty(schema.Validate(node.ToJsonString()));
            }
        }
    }

    [Fact]
    public async Task CoordinatedPlanFreezesEveryFactAndBothFingerprints()
    {
        var plan = CoordinatedPlan(); var json = JsonDefaults.Serialize(plan);
        Assert.Equal(json, JsonDefaults.Serialize(ReadPlan(json)));
        var schema = await JsonSchema.FromFileAsync(SchemaPath("mutation-plan.schema.json"), TestContext.Current.CancellationToken);
        Assert.Empty(schema.Validate(json));
        foreach (var path in RequiredPaths(JsonNode.Parse(json)!["operations"]![0]!["coordinatedTransformTarget"]!))
        {
            var node = JsonNode.Parse(json)!; RemoveNodePath(node["operations"]![0]!["coordinatedTransformTarget"]!, path);
            Assert.Throws<S2ModKitException>(() => ReadPlan(node.ToJsonString()));
            Assert.NotEmpty(schema.Validate(node.ToJsonString()));
        }
        var target = plan.Operations[0].CoordinatedTransformTarget!;
        var drift = target with { SeamInventory = target.SeamInventory with { CoincidentSelectedRecordCount = 1 } };
        Assert.NotEqual(target.TargetFingerprint, MutationPlanJson.ComputeCoordinatedTargetFingerprint(drift));
        var altered = plan with { Operations = [plan.Operations[0] with { CoordinatedTransformTarget = drift }] };
        altered = altered with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(altered) };
        Assert.Throws<S2ModKitException>(() => ReadPlan(JsonDefaults.Serialize(altered)));
        foreach (var name in new[] { "experimentalTransformTarget", "ellipsoidTransformTarget", "affineTransformTarget", "coupledTransformTarget" })
        {
            var node = JsonNode.Parse(json)!; node["operations"]![0]![name] = null;
            Assert.Throws<S2ModKitException>(() => ReadPlan(node.ToJsonString()));
            Assert.NotEmpty(schema.Validate(node.ToJsonString()));
        }
    }

    [Fact]
    public void CoordinatedPlansRejectRehashedOverlapsMissingMembersAndForgedZeroFields()
    {
        var plan = CoordinatedPlan(); var t = plan.Operations[0].CoordinatedTransformTarget!;
        PlannedCoordinatedTransformTarget[] invalid =
        [
            t with { Buffers = [t.Buffers[0]] },
            t with { Buffers = [t.Buffers[0], t.Buffers[1] with { VertexBufferOrdinal = 0 }] },
            t with { Buffers = [t.Buffers[0], t.Buffers[1] with { VertexResourceBlockIndex = t.Buffers[0].VertexResourceBlockIndex }] },
            t with { Buffers = [t.Buffers[0] with { ChangedFrameCount = 2 }, t.Buffers[1]] },
            t with { SeamInventory = t.SeamInventory with { ExcludedMovingMates = 1 } },
            t with { FieldProof = t.FieldProof with { TiltedCertificate = t.FieldProof.TiltedCertificate! with { Numerator = "2" } } },
            t with { ZeroBoxTargets = [t.ZeroBoxTargets[0] with { OriginalWords = [0, 0, 0, 0, 0, 1] }] },
            t with { ZeroBoxTargets = [t.ZeroBoxTargets[0] with { ContributorCount = 2 }] },
            t with { ZeroBoxTargets = [t.ZeroBoxTargets[0] with { CoordinateMatrixWords = new uint[12] }] },
            t with { ZeroRenderSphereTargets = [t.ZeroRenderSphereTargets[0] with { OriginalWord = 0x8000000000000000 }] },
            t with { ZeroRenderSphereTargets = [t.ZeroRenderSphereTargets[0] with { PairedZeroBoxFieldPath = "other" }] },
            t with { ZeroRenderSpherePolicy = new("reject", 1) },
        ];
        foreach (var target in invalid)
        {
            var hashed = target with { TargetFingerprint = MutationPlanJson.ComputeCoordinatedTargetFingerprint(target) };
            var altered = plan with { Operations = [plan.Operations[0] with { CoordinatedTransformTarget = hashed }] };
            altered = altered with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(altered) };
            Assert.Throws<S2ModKitException>(() => ReadPlan(JsonDefaults.Serialize(altered)));
        }
    }

    [Fact]
    public async Task CoordinatedEvidenceKeepsPreservationSeparateFromUntestedConsumerRisks()
    {
        var schema = await JsonSchema.FromFileAsync(SchemaPath("evidence.schema.json"), TestContext.Current.CancellationToken);
        foreach (var built in new[] { false, true })
        {
            var report = CoordinatedReport(built); var json = JsonDefaults.Serialize(report);
            Assert.Equal(json, JsonDefaults.Serialize(ReadEvidence(json)));
            Assert.Empty(schema.Validate(json));
            foreach (var path in RequiredPaths(JsonNode.Parse(json)!["operations"]![0]!["coordinatedTransform"]!))
            {
                var node = JsonNode.Parse(json)!; RemoveNodePath(node["operations"]![0]!["coordinatedTransform"]!, path);
                Assert.Throws<S2ModKitException>(() => ReadEvidence(node.ToJsonString()));
                Assert.NotEmpty(schema.Validate(node.ToJsonString()));
            }
            foreach (var boundary in report.Boundaries)
            {
                Assert.Throws<S2ModKitException>(() => ReadEvidence(JsonDefaults.Serialize(report with { Boundaries = report.Boundaries.Where(b => b != boundary).ToArray() })));
                Assert.Throws<S2ModKitException>(() => ReadEvidence(JsonDefaults.Serialize(report with
                { Boundaries = report.Boundaries.Select(b => b == boundary ? b with { Status = b.Status == "passed" ? "untested" : "passed" } : b).ToArray() })));
            }
        }
        var valid = CoordinatedReport(true); var visual = valid.Operations[0].CoordinatedTransform!;
        foreach (var changed in new[]
        {
            visual with { ZeroBoxes = [visual.ZeroBoxes[0] with { ConsumerStatus = "passed" }] },
            visual with { ZeroRenderSpheres = [visual.ZeroRenderSpheres[0] with { ObservedStorage = "binary32" }] },
            visual with { ObservedBuffers = [visual.ObservedBuffers![0]] },
        }) Assert.Throws<S2ModKitException>(() => ReadEvidence(JsonDefaults.Serialize(valid with { Operations = [valid.Operations[0] with { CoordinatedTransform = changed }] })));
    }

    [Fact]
    public void HistoricalContractsRejectNewNullPropertiesAndNewRecipesRejectDuplicates()
    {
        foreach (var old in new[] { Recipe(), EllipsoidRecipe() })
            foreach (var name in new[] { "coordinatedTransform", "zeroBoneBoxPolicy", "zeroRenderSpherePolicy" })
            {
                var node = JsonNode.Parse(JsonDefaults.Serialize(old))!; node["operations"]![0]![name] = null;
                Assert.Throws<S2ModKitException>(() => ReadRecipe(node.ToJsonString()));
            }
        foreach (var old in new[] { Plan(), EllipsoidPlan() })
        {
            var node = JsonNode.Parse(JsonDefaults.Serialize(old))!; node["operations"]![0]!["coordinatedTransformTarget"] = null;
            Assert.Throws<S2ModKitException>(() => ReadPlan(node.ToJsonString()));
        }
        foreach (var old in new[] { ExperimentalReport(false), EllipsoidReport(true) })
        {
            var node = JsonNode.Parse(JsonDefaults.Serialize(old))!; node["operations"]![0]!["coordinatedTransform"] = null;
            Assert.Throws<S2ModKitException>(() => ReadEvidence(node.ToJsonString()));
        }
        var json = JsonDefaults.Serialize(CoordinatedRecipe());
        Assert.Throws<S2ModKitException>(() => ReadRecipe(json.Replace("\"firstSign\": 1", "\"firstSign\": 1, \"firstSign\": 1", StringComparison.Ordinal)));
        var unknown = JsonNode.Parse(json)!; unknown["operations"]![0]!["coordinatedTransform"]!["field"]!["normalize"] = true;
        Assert.Throws<S2ModKitException>(() => ReadRecipe(unknown.ToJsonString()));
    }

    private static CoordinatedTiltedRampField CoordinatedField() => new()
    {
        Version = 1,
        CoordinateSpace = "model",
        FirstAxis = "x",
        FirstSign = 1,
        SecondAxis = "z",
        SecondSign = 1,
        PinnedThrough = 0,
        FullFrom = 8,
        Pivot = new(),
        UniformScale = 1.5f,
        NumericalPolicy = new("tilted_ramp_numeric", 1)
    };

    internal static RecipeDocument CoordinatedRecipe() => Recipe(Operation() with
    {
        Version = 8,
        Granularity = "coordinated_buffer_vertices",
        Transform = null!,
        Selector = new() { Kind = "draw_call_ids", DrawCallIds = ["dc_000000000000000000000000", "dc_111111111111111111111111"] },
        ExpectedMatchesByLod = new Dictionary<string, int> { ["0"] = 2 },
        ExpectedVerticesByLod = new Dictionary<string, int> { ["0"] = 6 },
        CoordinatedTransform = new([new("first", [new(0, ["dc_000000000000000000000000"], 3)]), new("second", [new(0, ["dc_111111111111111111111111"], 3)])], CoordinatedField()),
        ZeroBoneBoxPolicy = new("preserve_all_zero_single_contributor_unverified", 1),
        ZeroRenderSpherePolicy = new("preserve_zero_render_sphere_with_zero_box_unverified", 1),
    }) with
    { SchemaVersion = 9 };

    internal static MutationPlan CoordinatedPlan()
    {
        var oldPlan = EllipsoidPlan(); var old = oldPlan.Operations[0].EllipsoidTransformTarget!; var b = old.Buffers[0];
        var op = (TransformComponentOperation)CoordinatedRecipe().Operations[0];
        var first = new PlannedCoordinatedBuffer("first", b.Lod, b.ResourcePath, b.MeshOrdinal, b.ResourceBlockIndex,
            b.VertexBufferOrdinal, b.IndexBufferOrdinal, b.VertexResourceBlockIndex, b.IndexResourceBlockIndex,
            b.VertexBlockInputHash, b.IndexBlockInputHash, b.InputDecodedVertexBufferHash, b.ExpectedDecodedVertexBufferHash, b.DecodedIndexBufferHash,
            b.VertexSetHash, b.VertexCount, b.OwnershipPolicy, b.PositionLayout, b.PackedFrameLayout, b.Codec, b.MaskHash, b.WeightHash,
            1, 1, 1, 2, 1, b.InputPositionHash, b.ExpectedPositionHash, b.InputPackedFrameHash, b.ExpectedPackedFrameHash, b.BeforeBounds, b.ExpectedAfterBounds, b.MaximumDisplacement);
        uint[] matrix = [0x3f800000, 0, 0, 0, 0, 0x3f800000, 0, 0, 0, 0, 0x3f800000, 0];
        var matrixBytes = matrix.SelectMany(w => BitConverter.GetBytes(w)).ToArray();
        const string boxPath = "m_skeleton.m_bones[1].m_bbox.m_vecCenter+m_vecSize";
        var zeroBox = new PlannedZeroBoneBoxPreservationTarget(0, 0, 1, 0, boxPath, "center_half_extent_f32", "render_inverse_bind",
            ContentHash.Compute(matrixBytes), matrix, Hash, 1, 4, Hash, new uint[6], op.ZeroBoneBoxPolicy!, "preserve_unverified");
        var zeroSphere = new PlannedZeroRenderSpherePreservationTarget(0, "m_skeleton.m_bones[1].m_flSphereRadius", boxPath,
            Hash, Hash, "binary64", 0, op.ZeroRenderSpherePolicy!, "preserve_unverified");
        var target = new PlannedCoordinatedTransformTarget(Hash, Hash, "root_coordinated_fields_visual", 1, "retain_expand_boxes_preserve_runtime_coordinated", 1,
            op.RuntimeMetadataPolicy!, op.ZeroBoneBoxPolicy!, op.ZeroRenderSpherePolicy!, op.Selector, op.CoordinatedTransform!,
            new CoordinatedFieldMath(op.CoordinatedTransform!.Field, 64).Proof, 1, 64,
            [first, first with { MemberId = "second", VertexBufferOrdinal = 1, IndexBufferOrdinal = 1, VertexResourceBlockIndex = 4, IndexResourceBlockIndex = 5 }],
            old.BoxTargets, [zeroBox], [zeroSphere], old.PreservationTargets, [.. old.SourceBlocks, new(4, "MVTX", Hash), new(5, "MIDX", Hash)], new(Hash, 6, 4, 0, 0));
        target = target with { TargetFingerprint = MutationPlanJson.ComputeCoordinatedTargetFingerprint(target) };
        var selected = oldPlan.Operations[0].SelectedDrawCalls[0];
        var plan = oldPlan with
        {
            SchemaVersion = 5,
            Operations = [oldPlan.Operations[0] with
        { Version = 8, EllipsoidTransformTarget = null, CoordinatedTransformTarget = target,
            SelectedDrawCalls = [selected, selected with { DrawCallOrdinal = 1, DrawCallId = "dc_111111111111111111111111" }],
            TargetBlocks = [.. oldPlan.Operations[0].TargetBlocks, new(4, "MVTX", Hash)] }]
        };
        return plan with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(plan) };
    }

    private static EvidenceReport CoordinatedReport(bool built)
    {
        var plan = CoordinatedPlan(); var t = plan.Operations[0].CoordinatedTransformTarget!;
        return ExperimentalReport(built) with
        {
            SchemaVersion = 10,
            PlanFingerprint = plan.Fingerprint,
            Output = built ? new("models/test.vmdl_c", EllipsoidOutputHash, 12) : null,
            Boundaries = CoordinatedContractValidator.PlannedBoundaries().Select(b => built && b.Status == "not_applicable" ? b with { Status = "passed" } : b).ToArray(),
            Operations = [new("scale", "transform_component", 8, t.Selector.DrawCallIds!, ["models/test.vmdl_c"])
            {
                CoordinatedTransform = new(t, built ? t.Buffers.Select(b => new CoordinatedBufferObservation(b.MemberId, b.Lod, b.MeshOrdinal, b.VertexBufferOrdinal,
                    b.ExpectedPositionHash, b.ExpectedPackedFrameHash, b.ExpectedDecodedVertexBufferHash, b.MaskHash, b.WeightHash, b.MaximumDisplacement)).ToArray() : null,
                    t.BoxTargets.Select(b => new ExperimentalBoxEvidence(b, built ? b.ExpectedWords : null, built ? "passed" : "planned")).ToArray(),
                    t.ZeroBoxTargets.Select(z => new ZeroBoneBoxPreservationEvidence(z, built ? z.OriginalWords : null, built ? "passed" : "planned", "untested", "untested")).ToArray(),
                    t.ZeroRenderSphereTargets.Select(z => new ZeroRenderSpherePreservationEvidence(z, built ? z.Storage : null, built ? z.OriginalWord : null, built ? "passed" : "planned", "untested", "untested")).ToArray(),
                    t.PreservationTargets.Select(p => new ExperimentalPreservationEvidence(p, built ? p.SourcePayloadHash : null, built ? p.OriginalWords : null, built ? "passed" : "planned")).ToArray()),
            }],
        };
    }
}
