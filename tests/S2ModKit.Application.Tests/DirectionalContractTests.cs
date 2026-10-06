using System.Text.Json;
using System.Text.Json.Nodes;
using NJsonSchema;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed partial class ExperimentalVisualContractTests
{
    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(2, 3, false)]
    [InlineData(1, 3, true)]
    [InlineData(16, 1, true)]
    public async Task DirectionalRecipeAndPlanRoundTripCompleteMemberAndProtectionVariants(int members, int lods, bool bone)
    {
        var recipe = DirectionalRecipe(members, lods, bone); var plan = DirectionalPlan(members, lods, bone);
        var recipeJson = JsonDefaults.Serialize(recipe); var planJson = JsonDefaults.Serialize(plan);
        Assert.Equal(recipeJson, JsonDefaults.Serialize(ReadRecipe(recipeJson)));
        Assert.Equal(planJson, JsonDefaults.Serialize(ReadPlan(planJson)));
        var recipeSchema = await JsonSchema.FromFileAsync(SchemaPath("recipe.schema.json"), TestContext.Current.CancellationToken);
        var planSchema = await JsonSchema.FromFileAsync(SchemaPath("mutation-plan.schema.json"), TestContext.Current.CancellationToken);
        Assert.Empty(recipeSchema.Validate(recipeJson)); Assert.Empty(planSchema.Validate(planJson));
        var legacy = await JsonSchema.FromFileAsync(SchemaPath("v9/recipe.schema.json"), TestContext.Current.CancellationToken);
        Assert.NotEmpty(legacy.Validate(recipeJson));
        Assert.True(plan.Operations[0].DirectionalTransformTarget!.Buffers[0].ChangedFrameCount > plan.Operations[0].DirectionalTransformTarget!.Buffers[0].TransitionVertexCount);
    }

    [Fact]
    public async Task DirectionalTypedPivotsRequireTheirExactClosedShape()
    {
        var recipe = DirectionalRecipe(); var op = (TransformComponentOperation)recipe.Operations[0]; var intent = op.DirectionalTransform!;
        var schema = await JsonSchema.FromFileAsync(SchemaPath("recipe.schema.json"), TestContext.Current.CancellationToken);
        TransformPivot[] pivots = [new() { Kind = "explicit_point", Point = new() }, new() { Kind = "selection_bounds_center", ReferenceLod = 0 },
            new() { Kind = "bounds_face", ReferenceLod = 0, Face = "min_x" }, new() { Kind = "bone_origin", ReferenceLod = 0, BoneName = "exact_source_bone" }];
        foreach (var pivot in pivots)
        {
            var json = JsonDefaults.Serialize(recipe with { Operations = [op with { DirectionalTransform = intent with { Field = intent.Field with { Pivot = pivot } } }] });
            Assert.Equal(json, JsonDefaults.Serialize(ReadRecipe(json))); Assert.Empty(schema.Validate(json));
            var node = JsonNode.Parse(json)!; node["operations"]![0]!["directionalTransform"]!["field"]!["pivot"]!["center"] = null;
            Assert.Throws<S2ModKitException>(() => ReadRecipe(node.ToJsonString())); Assert.NotEmpty(schema.Validate(node.ToJsonString()));
        }
    }

    [Fact]
    public async Task DirectionalSchemasAndReadersRejectEveryMissingRequiredFact()
    {
        var documents = new[] { (File: "recipe.schema.json", Json: JsonDefaults.Serialize(DirectionalRecipe()), Property: "directionalTransform"),
            (File: "mutation-plan.schema.json", Json: JsonDefaults.Serialize(DirectionalPlan()), Property: "directionalTransformTarget"),
            (File: "evidence.schema.json", Json: JsonDefaults.Serialize(DirectionalReport(true)), Property: "directionalTransform") };
        foreach (var doc in documents)
        {
            var schema = await JsonSchema.FromFileAsync(SchemaPath(doc.File), TestContext.Current.CancellationToken);
            Assert.Empty(schema.Validate(doc.Json));
            var target = JsonNode.Parse(doc.Json)!["operations"]![0]![doc.Property]!;
            foreach (var path in RequiredPaths(target))
            {
                var node = JsonNode.Parse(doc.Json)!; RemoveNodePath(node["operations"]![0]![doc.Property]!, path);
                Assert.NotEmpty(schema.Validate(node.ToJsonString()));
                Assert.Throws<S2ModKitException>(() => ReadDirectionalDocument(doc.File, node.ToJsonString()));
            }
        }
    }

    [Theory]
    [InlineData("transform")]
    [InlineData("coordinatedTransform")]
    [InlineData("region")]
    [InlineData("localTransform")]
    [InlineData("physicsPolicy")]
    [InlineData("connectedComponentIdsByLod")]
    public async Task DirectionalRecipeRejectsLegacyPropertiesIncludingNull(string property)
    {
        var node = JsonNode.Parse(JsonDefaults.Serialize(DirectionalRecipe()))!; node["operations"]![0]![property] = null;
        Assert.Throws<S2ModKitException>(() => ReadRecipe(node.ToJsonString()));
        var schema = await JsonSchema.FromFileAsync(SchemaPath("recipe.schema.json"), TestContext.Current.CancellationToken);
        Assert.NotEmpty(schema.Validate(node.ToJsonString()));
    }

    [Fact]
    public void DirectionalIntentRejectsInvalidParametersMemberMapsAndPolicies()
    {
        var recipe = DirectionalRecipe(2, 2); var op = (TransformComponentOperation)recipe.Operations[0]; var intent = op.DirectionalTransform!;
        foreach (var scale in new[] { new TransformVector3 { X = 1, Y = 1, Z = 1 }, new() { X = 0, Y = 1, Z = 1 }, new() { X = 2.01f, Y = 1, Z = 1 } })
            Assert.Throws<S2ModKitException>(() => ReadRecipe(Raw(recipe with { Operations = [op with { DirectionalTransform = intent with { Field = intent.Field with { Scale = scale } } }] })));
        TransformComponentOperation[] invalid = [op with { ZeroBoneBoxPolicy = new("preserve_all_zero_single_contributor_unverified", 1) },
            op with { ZeroRenderSpherePolicy = new("preserve_zero_render_sphere_with_zero_box_unverified", 1) },
            op with { RuntimeMetadataPolicy = new("verified", 1) }, op with { Limits = new() { MaximumVertexDisplacement = 65 } },
            op with { DirectionalTransform = intent with { Members = [intent.Members[0], intent.Members[0]] } },
            op with { DirectionalTransform = intent with { Members = [intent.Members[0] with { Lods = [intent.Members[0].Lods[1]] }, intent.Members[1]] } },
            op with { DirectionalTransform = intent with { Field = intent.Field with { CoordinateSpace = "bone_bind" } } },
            op with { DirectionalTransform = intent with { Field = intent.Field with { NumericalPolicy = new("ellipsoid_numeric", 1) } } },
            op with { DirectionalTransform = intent with { Field = intent.Field with { OuterRadii = new() { X = 9, Y = 1, Z = 1 } } } },
            op with { DirectionalTransform = intent with { Field = intent.Field with { CoreFraction = 1 } } }];
        foreach (var operation in invalid) Assert.Throws<S2ModKitException>(() => ReadRecipe(Raw(recipe with { Operations = [operation] })));
        var folding = intent.Field with { Scale = new() { X = 2, Y = 1, Z = 1 }, CoreFraction = 0.25f };
        var error = Assert.Throws<S2ModKitException>(() => ReadRecipe(Raw(recipe with { Operations = [op with { DirectionalTransform = intent with { Field = folding } }] })));
        Assert.Equal("DIRECTIONAL_JACOBIAN_UNPROVEN", error.Error.Code);
    }

    [Fact]
    public void DirectionalProtectionRejectsDuplicatesStaleCountsHashesAndMissingLods()
    {
        var recipe = DirectionalRecipe(1, 2); var op = (TransformComponentOperation)recipe.Operations[0]; var t = op.DirectionalTransform!;
        var a = (DirectionalVertexAssertion)t.Protection.Assertions[0]; var s = a.Sets[0];
        DirectionalVertexAssertion[] invalid = [a with { Sets = [s] }, a with { Sets = [s with { VertexIndices = [2, 2], VertexCount = 2 }, a.Sets[1]] },
            a with { Sets = [s with { VertexIndices = [3] }, a.Sets[1]] }, a with { Sets = [s with { VertexCount = 0 }, a.Sets[1]] },
            a with { Sets = [s with { VertexSetHash = Hash }, a.Sets[1]] }, a with { Sets = [s with { SourceDecodedBufferHash = default }, a.Sets[1]] }];
        foreach (var assertion in invalid)
            Assert.Throws<S2ModKitException>(() => ReadRecipe(Raw(recipe with { Operations = [op with { DirectionalTransform = t with { Protection = t.Protection with { Assertions = [assertion] } } }] })));
        Assert.Throws<S2ModKitException>(() => ReadRecipe(Raw(recipe with { Operations = [op with { DirectionalTransform = t with { Protection = t.Protection with { Assertions = [a, a] } } }] })));
    }

    [Fact]
    public void DirectionalPlanRejectsRehashedForgedCertificatesOwnershipProtectionAndClosure()
    {
        var plan = DirectionalPlan(); var t = plan.Operations[0].DirectionalTransformTarget!; var s = t.Protection.Union[0];
        PlannedDirectionalTransformTarget[] invalid = [t with { Certificate = t.Certificate with { BetaLowerBound = new("1", "1") } },
            t with { Pivot = t.Pivot with { SourceHash = Hash } },
            t with { WordAudits = [] },
            t with { WordAudits = [t.WordAudits[0] with { ExpectedPinnedFrameHash = EllipsoidOutputHash }] },
            t with { WordAudits = [t.WordAudits[0] with { ExpectedUnchangedAttributesHash = EllipsoidOutputHash }] },
            t with { WordAudits = [t.WordAudits[0] with { ChangedPositionIndices = [0, 2], ChangedPositionSetHash = DirectionalContractValidator.VertexSetHash([0, 2]) }] },
            t with { Certificate = t.Certificate with { AxisLowerBounds = [new("01", "1"), .. t.Certificate.AxisLowerBounds.Skip(1)] } },
            t with { Pivot = t.Pivot with { Point = new() { X = 1 } } }, t with { Buffers = [t.Buffers[0] with { ChangedPositionCount = 0 }] },
            t with { Buffers = [t.Buffers[0] with { ChangedFrameCount = 3 }] }, t with { Buffers = [t.Buffers[0] with { VertexBlockInputHash = EllipsoidOutputHash }] },
            t with { ContextBuffers = [t.ContextBuffers[0]] }, t with { Coincidences = [t.Coincidences[0] with { ExcludedMovingMates = 1 }] },
            t with { ContextBuffers = [t.ContextBuffers[0], t.ContextBuffers[1] with { VertexResourceBlockIndex = t.ContextBuffers[0].VertexResourceBlockIndex }] },
            t with { Coincidences = [t.Coincidences[0] with { RecordCount = 3 }] },
            t with { Protection = t.Protection with { Union = [s with { ExpectedPackedFrameHash = EllipsoidOutputHash }, t.Protection.Union[1]] } },
            t with { Protection = t.Protection with { Assertions = [] } }, t with { BoxClosures = [] },
            t with { BoxClosures = [t.BoxClosures[0] with { Contributors = [t.BoxClosures[0].Contributors[0]] }] },
            t with { BoxTargets = [t.BoxTargets[0], t.BoxTargets[0]] },
            t with { PreservationTargets = [t.PreservationTargets[0] with { OriginalWords = [0] }] }];
        foreach (var target in invalid) Assert.Throws<S2ModKitException>(() => ReadPlan(Raw(RehashDirectional(plan, target))));
        var changed = t with { Coincidences = [t.Coincidences[0] with { PairSetHash = EllipsoidOutputHash }] };
        Assert.NotEqual(t.TargetFingerprint, MutationPlanJson.ComputeDirectionalTargetFingerprint(changed));
        var stale = plan with { Operations = [plan.Operations[0] with { DirectionalTransformTarget = changed }] };
        stale = stale with { Fingerprint = MutationPlanJson.ComputeExperimentalFingerprint(stale) };
        Assert.Throws<S2ModKitException>(() => ReadPlan(Raw(stale)));
    }

    [Fact]
    public void DirectionalRootBoneProtectionIncludesExcludedContributorRows()
    {
        var plan = DirectionalPlan(1, 2, true); var t = plan.Operations[0].DirectionalTransformTarget!;
        Assert.Equal(4, t.Protection.Assertions[0].Sets.Count); Assert.Equal(2, t.DirectionalTransform.Protection.Assertions.OfType<DirectionalBoneAssertion>().Single().Lods[0].ContributorCount);
        var assertion = t.Protection.Assertions[0] with { Sets = t.Protection.Assertions[0].Sets.Where(s => s.VertexBufferOrdinal == 0).ToArray() };
        assertion = assertion with { ContributorSetHash = DirectionalContractValidator.ContributorSetHash(assertion.Sets) };
        var missing = t with { Protection = t.Protection with { Assertions = [assertion] } };
        Assert.Throws<S2ModKitException>(() => ReadPlan(Raw(RehashDirectional(plan, missing))));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DirectionalEvidenceRoundTripsPlannedAndObservedFactsWithoutConsumerQualification(bool built, bool bone)
    {
        var report = DirectionalReport(built, bone); var json = JsonDefaults.Serialize(report);
        Assert.Equal(json, JsonDefaults.Serialize(ReadEvidence(json)));
        var schema = await JsonSchema.FromFileAsync(SchemaPath("evidence.schema.json"), TestContext.Current.CancellationToken);
        Assert.Empty(schema.Validate(json));
        var old = await JsonSchema.FromFileAsync(SchemaPath("v10/evidence.schema.json"), TestContext.Current.CancellationToken); Assert.NotEmpty(old.Validate(json));
        foreach (var boundary in report.Boundaries)
        {
            Assert.Throws<S2ModKitException>(() => ReadEvidence(Raw(report with { Boundaries = report.Boundaries.Where(b => b != boundary).ToArray() })));
            Assert.Throws<S2ModKitException>(() => ReadEvidence(Raw(report with { Boundaries = report.Boundaries.Select(b => b == boundary ? b with { Status = b.Status == "passed" ? "untested" : "passed" } : b).ToArray() })));
        }
    }

    [Fact]
    public void DirectionalEvidenceRejectsInventedMissingDriftedObservationsAndPlanBindings()
    {
        var report = DirectionalReport(true); var visual = report.Operations[0].DirectionalTransform!;
        DirectionalTransformEvidence[] invalid = [visual with { Observed = null }, visual with { Observed = visual.Observed! with { Buffers = [] } },
            visual with { Observed = visual.Observed! with { Protection = visual.Target.Protection with { UnionHash = EllipsoidOutputHash } } },
            visual with { PlanBinding = visual.PlanBinding with { RecipeId = "forged" } }, visual with { PlanBinding = visual.PlanBinding with { Inputs = [] } },
            visual with { Boxes = [visual.Boxes[0] with { ObservedWords = [0, 0, 0, 0, 0, 0] }, .. visual.Boxes.Skip(1)] }];
        foreach (var v in invalid) Assert.Throws<S2ModKitException>(() => ReadEvidence(Raw(report with { Operations = [report.Operations[0] with { DirectionalTransform = v }] })));
        var planned = DirectionalReport(false);
        Assert.Throws<S2ModKitException>(() => ReadEvidence(Raw(planned with { Operations = [planned.Operations[0] with { DirectionalTransform = planned.Operations[0].DirectionalTransform! with { Observed = visual.Observed } }] })));
    }

    [Fact]
    public void HistoricalContractsKeepCanonicalBytesAndRejectDirectionalPropertiesIncludingNull()
    {
        foreach (var recipe in new[] { Recipe(), EllipsoidRecipe(), CoordinatedRecipe() })
        {
            var json = JsonDefaults.Serialize(recipe); Assert.Equal(json, JsonDefaults.Serialize(ReadRecipe(json)));
            var node = JsonNode.Parse(json)!; node["operations"]![0]!["directionalTransform"] = null;
            Assert.Throws<S2ModKitException>(() => ReadRecipe(node.ToJsonString()));
        }
        foreach (var plan in new[] { Plan(), EllipsoidPlan(), CoordinatedPlan() })
        {
            var json = JsonDefaults.Serialize(plan); Assert.Equal(json, JsonDefaults.Serialize(ReadPlan(json)));
            Assert.Equal(plan.Fingerprint, MutationPlanJson.ComputeExperimentalFingerprint(plan));
            var node = JsonNode.Parse(json)!; node["operations"]![0]!["directionalTransformTarget"] = null;
            Assert.Throws<S2ModKitException>(() => ReadPlan(node.ToJsonString()));
        }
        foreach (var report in new[] { ExperimentalReport(true), EllipsoidReport(true), CoordinatedReport(true) })
        {
            var json = JsonDefaults.Serialize(report); Assert.Equal(json, JsonDefaults.Serialize(ReadEvidence(json)));
            var node = JsonNode.Parse(json)!; node["operations"]![0]!["directionalTransform"] = null;
            Assert.Throws<S2ModKitException>(() => ReadEvidence(node.ToJsonString()));
        }
    }

    [Fact]
    public void DirectionalContractsRejectVersionNullDuplicatesUnknownFieldsAndMixedTargets()
    {
        var documents = new[] { (File: "recipe.schema.json", Json: JsonDefaults.Serialize(DirectionalRecipe())),
            (File: "mutation-plan.schema.json", Json: JsonDefaults.Serialize(DirectionalPlan())), (File: "evidence.schema.json", Json: JsonDefaults.Serialize(DirectionalReport(true))) };
        foreach (var doc in documents)
        {
            foreach (var version in new JsonNode?[] { null, JsonValue.Create(999), JsonValue.Create("6") })
            {
                var node = JsonNode.Parse(doc.Json)!; node["schemaVersion"] = version?.DeepClone();
                Assert.Throws<S2ModKitException>(() => ReadDirectionalDocument(doc.File, node.ToJsonString()));
            }
            var duplicated = doc.Json.Replace("\"schemaVersion\":", "\"schemaVersion\": 999, \"schemaVersion\":", StringComparison.Ordinal);
            Assert.Throws<S2ModKitException>(() => ReadDirectionalDocument(doc.File, duplicated));
            var unknown = JsonNode.Parse(doc.Json)!; unknown["operations"]![0]!["unknown"] = null;
            Assert.Throws<S2ModKitException>(() => ReadDirectionalDocument(doc.File, unknown.ToJsonString()));
            var mixed = JsonNode.Parse(doc.Json)!; mixed["operations"]![0]![doc.File == "mutation-plan.schema.json" ? "coordinatedTransformTarget" : "coordinatedTransform"] = null;
            Assert.Throws<S2ModKitException>(() => ReadDirectionalDocument(doc.File, mixed.ToJsonString()));
        }
    }

    [Fact]
    public void DirectionalPlanningAndSnapshotChecksRejectIncompleteSource()
    {
        var model = new ModelSnapshot(new("models/test.vmdl_c", Hash, 12, []), []);
        Assert.Equal("LOD_COVERAGE_INCOMPLETE", Assert.Throws<S2ModKitException>(() => MutationPlanner.CreatePlan(model, DirectionalRecipe())).Error.Code);
        Assert.False(ModelVerifier.Verify(model, model, DirectionalPlan()).IsValid);
    }

    [Fact]
    public void DirectionalWritersRejectInvalidNewAndLegacySmuggledFacts()
    {
        var recipe = DirectionalRecipe(); var plan = DirectionalPlan(); var report = DirectionalReport(true);
        Assert.Throws<S2ModKitException>(() => JsonDefaults.Serialize(recipe with { SchemaVersion = 9 }));
        Assert.Throws<S2ModKitException>(() => JsonDefaults.SerializeToUtf8(plan with { SchemaVersion = 5 }));
        Assert.Throws<S2ModKitException>(() => JsonDefaults.Serialize(report with { SchemaVersion = 10 }));
        var target = plan.Operations[0].DirectionalTransformTarget!;
        var changed = RehashDirectional(plan, target with { Protection = target.Protection with { UnionHash = EllipsoidOutputHash } });
        Assert.Throws<S2ModKitException>(() => JsonDefaults.Serialize(changed));
        Assert.Throws<S2ModKitException>(() => JsonDefaults.Serialize(report with { Command = "plan" }));
        var old = JsonDefaults.SerializeToUtf8(Plan());
        Assert.Equal(old, JsonDefaults.SerializeToUtf8(JsonDefaults.Deserialize<MutationPlan>(old, "Legacy plan")));
    }

    [Fact]
    public void DirectionalRootBoneCountsAreAssertionsNotMaskSubtraction()
    {
        var plan = DirectionalPlan(1, 1, true); var target = plan.Operations[0].DirectionalTransformTarget!;
        var asserted = target.Protection.Assertions[0]; var fixedSource = asserted.Sets[0];
        var changed = fixedSource with { ExpectedPositionHash = EllipsoidOutputHash };
        asserted = asserted with { Sets = [changed, asserted.Sets[1]] };
        var t = target with { Protection = target.Protection with { Assertions = [asserted] } };
        Assert.Throws<S2ModKitException>(() => ReadPlan(Raw(RehashDirectional(plan, t))));
        var bone = (DirectionalBoneAssertion)target.DirectionalTransform.Protection.Assertions[0];
        bone = bone with { Lods = [bone.Lods[0] with { ContributorCount = 1 }] };
        t = target with { DirectionalTransform = target.DirectionalTransform with { Protection = target.DirectionalTransform.Protection with { Assertions = [bone] } } };
        Assert.Throws<S2ModKitException>(() => ReadPlan(Raw(RehashDirectional(plan, t))));
    }

    [Fact]
    public void DirectionalCardinalityOverflowAndAnEmptyProtectedLodRejectWithoutDefaulting()
    {
        var plan = DirectionalPlan(2, 2); var target = plan.Operations[0].DirectionalTransformTarget!;
        var huge = target.DirectionalTransform.Members.Select(m => m with { Lods = m.Lods.Select(l => l with { ExpectedVertices = int.MaxValue }).ToArray() }).ToArray();
        var forged = target with { DirectionalTransform = target.DirectionalTransform with { Members = huge } };
        Assert.Throws<S2ModKitException>(() => ReadPlan(Raw(RehashDirectional(plan, forged))));
        var recipe = DirectionalRecipe(1, 2); var op = (TransformComponentOperation)recipe.Operations[0]; var a = (DirectionalVertexAssertion)op.DirectionalTransform!.Protection.Assertions[0];
        a = a with { Sets = [a.Sets[0], a.Sets[1] with { VertexIndices = [], VertexSetHash = ContentHash.Compute([]), VertexCount = 0 }] };
        Assert.Throws<S2ModKitException>(() => ReadRecipe(Raw(recipe with
        {
            Operations = [op with { DirectionalTransform = op.DirectionalTransform with
            { Protection = op.DirectionalTransform.Protection with { Assertions = [a] } } }]
        })));
    }

    private static string Raw<T>(T value) => JsonSerializer.Serialize(value, JsonDefaults.Options);
    private static void ReadDirectionalDocument(string file, string json)
    {
        if (file == "recipe.schema.json") _ = ReadRecipe(json);
        else if (file == "mutation-plan.schema.json") _ = ReadPlan(json);
        else _ = ReadEvidence(json);
    }
}
