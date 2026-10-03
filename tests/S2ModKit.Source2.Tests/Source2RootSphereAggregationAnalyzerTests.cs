using System.Text.Json;
using NJsonSchema;
using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Source2.Tests;

public sealed class Source2RootSphereAggregationAnalyzerTests
{
    private const string ResourcePath = "models/test.vmdl_c";
    private static readonly ContentHash Hash = ContentHash.Compute("root-sphere-aggregation-tests"u8);
    private static readonly string[] ExtensionPropertyNames =
    [
        "evidenceScope", "reasonCode", "relation", "rows", "schemaVersion", "status",
    ];

    [Fact]
    public void AggregatesMappedRenderMaximaAcrossNonidentityRemapsAndBothLods()
    {
        var diagnostic = Source2RootSphereAggregationAnalyzer.Analyze(CanonicalDocument());

        Assert.Equal(1, diagnostic.SchemaVersion);
        Assert.Equal("mapped_render_radius_max", diagnostic.Relation);
        Assert.Equal("offline_static", diagnostic.EvidenceScope);
        Assert.Equal("complete", diagnostic.Status);
        Assert.Null(diagnostic.ReasonCode);
        Assert.Equal(3, diagnostic.Rows.Count);

        var rootA = diagnostic.Rows[0];
        Assert.Equal("m_modelSkeleton.m_boneSphere[0]", rootA.RootField.FieldPath);
        Assert.Equal("root-a", rootA.RootBoneName);
        Assert.Equal(5f, rootA.StoredRadius);
        Assert.Equal(5f, rootA.MappedMaximum);
        Assert.Equal("matched", rootA.Status);
        Assert.Equal(2, rootA.Sources.Count);
        // The farther LOD (mesh 1, LOD 1) supplies the maximum; sources sort by mesh ordinal,
        // resource block index and local field ordinal.
        Assert.Equal("m_skeleton.m_bones[1].m_flSphereRadius", rootA.Sources[0].FieldPath);
        Assert.Equal(0, rootA.Sources[0].MeshOrdinal);
        Assert.Equal(3, rootA.Sources[0].ResourceBlockIndex);
        Assert.Equal("m_skeleton.m_bones[0].m_flSphereRadius", rootA.Sources[1].FieldPath);
        Assert.Equal(1, rootA.Sources[1].MeshOrdinal);
        Assert.Equal([1], rootA.Sources[1].CoveredLods);

        var rootB = diagnostic.Rows[1];
        Assert.Equal("root-b", rootB.RootBoneName);
        Assert.Equal(4f, rootB.StoredRadius);
        Assert.Equal(4f, rootB.MappedMaximum);
        Assert.Equal("matched", rootB.Status);
        var rootBSource = Assert.Single(rootB.Sources);
        Assert.Equal("m_skeleton.m_bones[0].m_flSphereRadius", rootBSource.FieldPath);
        Assert.Equal(0, rootBSource.MeshOrdinal);
        Assert.Equal(3, rootBSource.ResourceBlockIndex);
        Assert.Equal(0, rootBSource.FieldOrdinal);

        var rootC = diagnostic.Rows[2];
        Assert.Equal("root-c", rootC.RootBoneName);
        Assert.Equal(7f, rootC.StoredRadius);
        Assert.Null(rootC.MappedMaximum);
        Assert.Equal("unmapped", rootC.Status);
        Assert.Empty(rootC.Sources);
    }

    [Fact]
    public void ReorderedFieldsAndRemapsProduceIdenticalCanonicalExtensionJson()
    {
        var canonical = CanonicalDocument();
        var reordered = canonical with
        {
            Fields = canonical.Fields.Reverse().ToArray(),
            BoneRemaps = canonical.BoneRemaps.Reverse().ToArray(),
        };

        var canonicalJson = JsonDefaults.Serialize(Source2RootSphereAggregationAnalyzer.Analyze(canonical));
        var reorderedJson = JsonDefaults.Serialize(Source2RootSphereAggregationAnalyzer.Analyze(reordered));

        Assert.NotEqual(string.Empty, canonicalJson);
        Assert.Equal(canonicalJson, reorderedJson);
    }

    [Fact]
    public void ExtraRemapEntriesAreAcceptedButNeverBecomePhantomSources()
    {
        var document = CanonicalDocument() with
        {
            BoneRemaps =
            [
                Remap(0, 3, [0], [1, 0, 2], ["root-b", "root-a"]),
                Remap(1, 7, [1], [0, 1], ["root-a"]),
            ],
        };

        var diagnostic = Source2RootSphereAggregationAnalyzer.Analyze(document);

        Assert.Equal("complete", diagnostic.Status);
        Assert.Null(diagnostic.ReasonCode);
        Assert.Equal("matched", diagnostic.Rows[1].Status);
        Assert.Single(diagnostic.Rows[1].Sources);
        Assert.Equal("unmapped", diagnostic.Rows[2].Status);
        Assert.Null(diagnostic.Rows[2].MappedMaximum);
        Assert.Empty(diagnostic.Rows[2].Sources);
    }

    [Fact]
    public void TruncatedOutOfRangeMissingAndMismatchedRemapsAreRejected()
    {
        AssertInconsistent(CanonicalDocument() with
        {
            BoneRemaps = [Remap(0, 3, [0], [1], ["root-b", "root-a"]), Remap(1, 7, [1], [0], ["root-a"])],
        });
        AssertInconsistent(CanonicalDocument() with
        {
            BoneRemaps = [Remap(0, 3, [0], [-1, 0], ["root-b", "root-a"]), Remap(1, 7, [1], [0], ["root-a"])],
        });
        AssertInconsistent(CanonicalDocument() with
        {
            BoneRemaps = [Remap(0, 3, [0], [9, 0], ["root-b", "root-a"]), Remap(1, 7, [1], [0], ["root-a"])],
        });
        AssertInconsistent(CanonicalDocument() with
        {
            BoneRemaps =
            [
                Remap(0, 3, [0], [1, 0], ["root-b", "root-a"], status: "absent"),
                Remap(1, 7, [1], [0], ["root-a"]),
            ],
        });
        AssertInconsistent(CanonicalDocument() with
        {
            BoneRemaps = [Remap(0, 3, [0], [1, 0], ["renamed", "root-a"]), Remap(1, 7, [1], [0], ["root-a"])],
        });
    }

    [Fact]
    public void UninfluencedStoredRenderRadiusParticipatesAndUnmappedRootsStayUnmapped()
    {
        var document = CanonicalDocument() with
        {
            Fields =
            [
                RenderSphereField(0, 3, 0, [0], 4f, contributorCount: 0),
                RenderSphereField(0, 3, 1, [0], 1f, contributorCount: 0),
                RenderSphereField(1, 7, 0, [1], 5f, contributorCount: 0),
                RootSphereField(0, [0, 1], 5f),
                RootSphereField(1, [0, 1], 4f),
                RootSphereField(2, [0, 1], 7f),
            ],
        };

        var diagnostic = Source2RootSphereAggregationAnalyzer.Analyze(document);

        Assert.Equal("complete", diagnostic.Status);
        Assert.Equal(5f, diagnostic.Rows[0].MappedMaximum);
        Assert.Equal("matched", diagnostic.Rows[0].Status);
        Assert.Equal("matched", diagnostic.Rows[1].Status);
        Assert.Equal("unmapped", diagnostic.Rows[2].Status);
        Assert.Empty(diagnostic.Rows[2].Sources);
    }

    [Fact]
    public void StaleRootRadiusIsReportedAsMismatchWithoutRepair()
    {
        var document = CanonicalDocument() with
        {
            Fields =
            [
                RenderSphereField(0, 3, 0, [0], 4f),
                RenderSphereField(0, 3, 1, [0], 1f),
                RenderSphereField(1, 7, 0, [1], 5f),
                RootSphereField(0, [0, 1], 4.5f),
                RootSphereField(1, [0, 1], 4f),
                RootSphereField(2, [0, 1], 7f),
            ],
        };

        var diagnostic = Source2RootSphereAggregationAnalyzer.Analyze(document);

        Assert.Equal("complete", diagnostic.Status);
        Assert.Equal("mismatch", diagnostic.Rows[0].Status);
        Assert.Equal(4.5f, diagnostic.Rows[0].StoredRadius);
        Assert.Equal(5f, diagnostic.Rows[0].MappedMaximum);
        Assert.Equal(2, diagnostic.Rows[0].Sources.Count);
    }

    [Fact]
    public void NegativeNonfiniteOrMissingRawSphereValuesAreRejected()
    {
        AssertInconsistent(ReplaceField(CanonicalDocument(), "m_skeleton.m_bones[0].m_flSphereRadius", 0, RenderSphereField(0, 3, 0, [0], -1f)));
        AssertInconsistent(ReplaceField(CanonicalDocument(), "m_skeleton.m_bones[0].m_flSphereRadius", 0, RenderSphereField(0, 3, 0, [0], float.NaN)));
        AssertInconsistent(ReplaceField(CanonicalDocument(), "m_skeleton.m_bones[0].m_flSphereRadius", 0, RenderSphereField(0, 3, 0, [0], float.PositiveInfinity)));
        AssertInconsistent(ReplaceField(
            CanonicalDocument(),
            "m_skeleton.m_bones[1].m_flSphereRadius",
            0,
            RenderSphereField(0, 3, 1, [0], 1f) with { RawOriginalValue = null }));
        AssertInconsistent(ReplaceField(CanonicalDocument(), "m_modelSkeleton.m_boneSphere[0]", null, RootSphereField(0, [0, 1], -3f)));
        AssertInconsistent(ReplaceField(
            CanonicalDocument(),
            "m_modelSkeleton.m_boneSphere[0]",
            null,
            RootSphereField(0, [0, 1], 5f) with { RawOriginalValue = null }));
        AssertInconsistent(ReplaceField(
            CanonicalDocument(),
            "m_modelSkeleton.m_boneSphere[0]",
            null,
            RootSphereField(0, [0, 1], 5f) with
            {
                RawOriginalValue = new CullingRawFieldValue(
                    "aabb_center_half_extents",
                    null,
                    null,
                    new TransformVector3(),
                    new TransformVector3(),
                    null),
            }));
    }

    [Fact]
    public void DuplicateConflictingIdentitiesHashMismatchAndInconsistentTablesAreRejected()
    {
        AssertInconsistent(CanonicalDocument() with
        {
            BoneRemaps = [Remap(0, 3, [0], [1, 0], ["root-b", "root-a"]), Remap(0, 7, [1], [0], ["root-a"])],
        });
        AssertInconsistent(CanonicalDocument() with
        {
            Fields = [.. CanonicalDocument().Fields, RenderSphereField(0, 3, 0, [0], 4f)],
        });
        AssertInconsistent(CanonicalDocument() with
        {
            Fields =
            [
                RenderSphereField(0, 3, 0, [0], 4f),
                RenderSphereField(0, 3, 1, [0], 1f),
                RenderSphereField(1, 7, 0, [1], 5f),
                RootSphereField(0, [0, 1], 5f),
                RootSphereField(1, [0, 1], 4f, blockIndex: 2),
                RootSphereField(2, [0, 1], 7f),
            ],
        });
        AssertInconsistent(ReplaceField(
            CanonicalDocument(),
            "m_skeleton.m_bones[0].m_flSphereRadius",
            0,
            RenderSphereField(0, 3, 0, [0], 4f, hash: ContentHash.Compute("different-resource"u8))));
        AssertInconsistent(ReplaceField(
            CanonicalDocument(),
            "m_skeleton.m_bones[0].m_flSphereRadius",
            1,
            RenderSphereField(1, 7, 0, [0], 5f)));
        AssertInconsistent(ReplaceField(
            CanonicalDocument(),
            "m_modelSkeleton.m_boneSphere[0]",
            null,
            RootSphereField(0, [0], 5f)));
        AssertInconsistent(CanonicalDocument() with
        {
            BoneRemaps =
            [
                Remap(0, 3, [0], [1, 0], ["root-b", "root-a"]),
                Remap(1, 7, [1], [0], ["root-a"], modelNames: ["root-a", "root-b", "root-d"]),
            ],
        });
    }

    [Fact]
    public void OmittedRepresentedMeshIsRejectedEvenWhenItsLodsAreAlreadyCovered()
    {
        var canonical = CanonicalDocument();

        AssertInconsistent(canonical with { Fields = [.. canonical.Fields, SceneField(9, 11, [0])] });
        AssertInconsistent(canonical with { Fields = [.. canonical.Fields, SceneField(9, 11, [2])] });
    }

    [Fact]
    public void MalformedSphereFamilyPathClaimsAreRejectedInsteadOfIgnored()
    {
        var canonical = CanonicalDocument();

        AssertInconsistent(canonical with { Fields = [.. canonical.Fields, FieldWithPath("m_modelSkeleton.m_boneSphere[zero]", null)] });
        AssertInconsistent(canonical with { Fields = [.. canonical.Fields, FieldWithPath("m_modelSkeleton.m_boneSphere[0].extra", null)] });
        AssertInconsistent(canonical with { Fields = [.. canonical.Fields, FieldWithPath("m_skeleton.m_bones[x].m_flSphereRadius", 0)] });
        AssertInconsistent(canonical with { Fields = [.. canonical.Fields, FieldWithPath("m_skeleton.m_bones[].m_flSphereRadius", 0)] });
        // A malformed claim never masks missing input: availability is still checked first.
        AssertUnavailable(canonical with
        {
            Fields = [.. canonical.Fields, FieldWithPath("m_modelSkeleton.m_boneSphere[zero]", null)],
            BoneRemaps = [],
        });

        // Render-bone box fields legitimately share the render prefix without claiming the exact
        // sphere suffix, and unrelated meshes are covered by the remap presence check above.
        var withBoxField = canonical with
        {
            Fields = [.. canonical.Fields, FieldWithPath("m_skeleton.m_bones[0].m_bbox.m_vecCenter+m_vecSize", 0)],
        };
        Assert.Equal("complete", Source2RootSphereAggregationAnalyzer.Analyze(withBoxField).Status);
    }

    [Fact]
    public void WrongBlockTypesForSphereFamiliesAreRejected()
    {
        AssertInconsistent(ReplaceField(
            CanonicalDocument(),
            "m_modelSkeleton.m_boneSphere[0]",
            null,
            RootSphereField(0, [0, 1], 5f, blockType: "MDAT")));
        AssertInconsistent(ReplaceField(
            CanonicalDocument(),
            "m_skeleton.m_bones[0].m_flSphereRadius",
            0,
            RenderSphereField(0, 3, 0, [0], 4f, blockType: "DATA")));
    }

    [Fact]
    public void MissingRootSpheresOrMeshRemapsReportInputUnavailable()
    {
        var canonical = CanonicalDocument();
        AssertUnavailable(canonical with { Fields = [RenderSphereField(0, 3, 0, [0], 4f)] });
        AssertUnavailable(canonical with { BoneRemaps = [] });
        AssertUnavailable(new CullingInventoryDocument(
            CullingEnvelopeContract.InventorySchemaVersion,
            canonical.Resource,
            "unsupported",
            [RenderSphereField(0, 3, 0, [0], 4f)],
            [],
            new Dictionary<string, JsonElement>()));
    }

    [Fact]
    public void ZeroMaximumIsCanonicalizedAndNegativeZeroRootStaysAMismatch()
    {
        var document = CanonicalDocument() with
        {
            Fields =
            [
                RenderSphereField(0, 3, 0, [0], -0f),
                RenderSphereField(0, 3, 1, [0], 0f),
                RenderSphereField(1, 7, 0, [1], 0f),
                RootSphereField(0, [0, 1], 0f),
                RootSphereField(1, [0, 1], -0f),
                RootSphereField(2, [0, 1], 7f),
            ],
        };

        var diagnostic = Source2RootSphereAggregationAnalyzer.Analyze(document);
        var json = JsonDefaults.Serialize(diagnostic);

        Assert.Equal("complete", diagnostic.Status);
        Assert.Equal("matched", diagnostic.Rows[0].Status);
        Assert.Equal(0, BitConverter.SingleToInt32Bits(diagnostic.Rows[0].MappedMaximum!.Value));
        Assert.Contains("\"mappedMaximum\": 0", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"mappedMaximum\": -0", json, StringComparison.Ordinal);
        Assert.Equal("mismatch", diagnostic.Rows[1].Status);
        Assert.Equal(0, BitConverter.SingleToInt32Bits(diagnostic.Rows[1].MappedMaximum!.Value));
        Assert.Contains("\"storedRadius\": -0", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachingTheExtensionPreservesInventoryFactsAndOtherEntries()
    {
        var original = CanonicalDocument() with
        {
            Extensions = new Dictionary<string, JsonElement>
            {
                ["unrelated"] = JsonSerializer.SerializeToElement(new { note = "keep" }),
            },
        };

        var extended = original with { Extensions = Source2RootSphereAggregationAnalyzer.Attach(original) };

        Assert.True(extended.Extensions.ContainsKey("unrelated"));
        Assert.True(extended.Extensions.ContainsKey(Source2RootSphereAggregationAnalyzer.ExtensionKey));
        Assert.Equal(original.SchemaVersion, extended.SchemaVersion);
        Assert.Equal(original.Resource, extended.Resource);
        Assert.Equal(original.Status, extended.Status);
        Assert.Equal(original.Fields, extended.Fields);
        Assert.Equal(original.BoneRemaps, extended.BoneRemaps);
        Assert.All(extended.Fields, field => Assert.Equal("unsupported", field.Status));
        Assert.Equal(
            JsonDefaults.Serialize(Source2RootSphereAggregationAnalyzer.Analyze(original)),
            JsonDefaults.Serialize(Source2RootSphereAggregationAnalyzer.Analyze(extended)));
    }

    [Fact]
    public async Task SerializedExtendedInventoryPassesTheStrictContractSchema()
    {
        var schema = await JsonSchema.FromFileAsync(
            GetSchemaPath(), TestContext.Current.CancellationToken);
        var extended = CanonicalDocument() with
        {
            Extensions = Source2RootSphereAggregationAnalyzer.Attach(CanonicalDocument()),
        };

        var json = JsonDefaults.Serialize(extended);
        var extension = extended.Extensions[Source2RootSphereAggregationAnalyzer.ExtensionKey];

        Assert.Equal(
            ExtensionPropertyNames,
            extension.EnumerateObject().Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal(JsonValueKind.Null, extension.GetProperty("reasonCode").ValueKind);
        Assert.Empty(schema.Validate(json));
    }

    private static CullingInventoryDocument CanonicalDocument()
    {
        var fields = new List<CullingInventoryField>
        {
            RenderSphereField(0, 3, 0, [0], 4f),
            RenderSphereField(0, 3, 1, [0], 1f),
            RenderSphereField(1, 7, 0, [1], 5f),
            RootSphereField(0, [0, 1], 5f),
            RootSphereField(1, [0, 1], 4f),
            RootSphereField(2, [0, 1], 7f),
        };
        return Document(fields, [Remap(0, 3, [0], [1, 0], ["root-b", "root-a"]), Remap(1, 7, [1], [0], ["root-a"])]);
    }

    private static CullingInventoryDocument Document(
        IReadOnlyList<CullingInventoryField> fields,
        IReadOnlyList<CullingMeshBoneRemapInventory> remaps) => new(
        CullingEnvelopeContract.InventorySchemaVersion,
        new CullingResourceIdentity(ResourcePath, Hash, 96),
        "partial",
        fields,
        remaps,
        new Dictionary<string, JsonElement>());

    private static CullingFieldIdentity RootIdentity(
        int ordinal,
        IReadOnlyList<int> coveredLods,
        int blockIndex = 1,
        string blockType = "DATA") =>
        new(ResourcePath, Hash, blockIndex, blockType, $"m_modelSkeleton.m_boneSphere[{ordinal}]", null, ordinal, coveredLods);

    private static CullingFieldIdentity RenderIdentity(
        int meshOrdinal,
        int blockIndex,
        int ordinal,
        IReadOnlyList<int> coveredLods,
        string blockType = "MDAT") =>
        new(ResourcePath, Hash, blockIndex, blockType, $"m_skeleton.m_bones[{ordinal}].m_flSphereRadius", meshOrdinal, ordinal, coveredLods);

    private static CullingInventoryField RootSphereField(
        int ordinal,
        IReadOnlyList<int> coveredLods,
        float radius,
        int blockIndex = 1,
        string blockType = "DATA") => new(
        RootIdentity(ordinal, coveredLods, blockIndex, blockType),
        "unsupported",
        SphereValue(radius),
        new CullingCoordinateSpaceIdentity("unsupported", null, null, null, "MODEL_BONE_SPHERE_SPACE_NOT_VERIFIED"),
        UnsupportedContributors());

    private static CullingInventoryField RenderSphereField(
        int meshOrdinal,
        int blockIndex,
        int ordinal,
        IReadOnlyList<int> coveredLods,
        float radius,
        int contributorCount = 1,
        ContentHash? hash = null,
        string blockType = "MDAT") => new(
        RenderIdentity(meshOrdinal, blockIndex, ordinal, coveredLods, blockType) with { ResourceHash = hash ?? Hash },
        "unsupported",
        SphereValue(radius),
        new CullingCoordinateSpaceIdentity("unsupported", null, null, null, "RENDER_BONE_SPHERE_SPACE_NOT_VERIFIED"),
        Contributors(meshOrdinal, blockIndex, contributorCount));

    private static CullingInventoryField SceneField(int meshOrdinal, int blockIndex, IReadOnlyList<int> coveredLods) => new(
        new CullingFieldIdentity(
            ResourcePath,
            Hash,
            blockIndex,
            "MDAT",
            "m_sceneObjects[0].m_vMinBounds+m_vMaxBounds",
            meshOrdinal,
            0,
            coveredLods),
        "verified",
        new CullingRawFieldValue(
            "aabb_min_max",
            new TransformVector3 { X = -1f, Y = -1f, Z = -1f },
            new TransformVector3 { X = 1f, Y = 1f, Z = 1f },
            null,
            null,
            null),
        new CullingCoordinateSpaceIdentity(
            "verified",
            "source2_model",
            Hash,
            [1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f],
            null),
        Contributors(meshOrdinal, blockIndex, 4));

    private static CullingInventoryField FieldWithPath(string fieldPath, int? meshOrdinal) => new(
        new CullingFieldIdentity(ResourcePath, Hash, 3, "MDAT", fieldPath, meshOrdinal, null, [0]),
        "unsupported",
        SphereValue(2f),
        new CullingCoordinateSpaceIdentity("unsupported", null, null, null, "LAYOUT_NOT_VERIFIED"),
        UnsupportedContributors());

    private static CullingMeshBoneRemapInventory Remap(
        int meshOrdinal,
        int blockIndex,
        int[] coveredLods,
        int[] remapValues,
        string[] renderNames,
        string[]? modelNames = null,
        string status = "verified") => new(
        ResourcePath,
        Hash,
        meshOrdinal,
        blockIndex,
        coveredLods,
        status,
        status == "verified" ? Hash : null,
        status == "verified" ? remapValues : null,
        "verified",
        renderNames,
        "verified",
        modelNames ?? ["root-a", "root-b", "root-c"],
        status == "verified" ? null : "MESH_TO_MODEL_BONE_REMAP_UNSUPPORTED");

    private static CullingRawFieldValue SphereValue(float radius) =>
        new("sphere_radius", null, null, null, null, radius);

    private static CullingContributorSetIdentity UnsupportedContributors() =>
        new("unsupported", null, null, null, null, "ROOT_BONE_REMAP_OR_NAME_EVIDENCE_INCOMPLETE");

    private static CullingContributorSetIdentity Contributors(int meshOrdinal, int blockIndex, int count) => new(
        "verified",
        CullingEnvelopeContract.ContributorIdentityAlgorithm,
        Hash,
        count,
        [new CullingContributorSourceIdentity(
            ResourcePath,
            Hash,
            [0],
            meshOrdinal,
            blockIndex,
            0,
            blockIndex + 1,
            0,
            blockIndex + 2,
            4,
            count,
            Hash,
            Hash,
            Hash,
            Hash,
            14u,
            28u,
            "verified",
            Hash,
            null)],
        null);

    private static CullingInventoryDocument ReplaceField(
        CullingInventoryDocument document,
        string fieldPath,
        int? meshOrdinal,
        CullingInventoryField replacement) => document with
        {
            Fields = document.Fields
                .Select(field => field.Identity.FieldPath == fieldPath && field.Identity.MeshOrdinal == meshOrdinal ? replacement : field)
                .ToArray(),
        };

    private static void AssertInconsistent(CullingInventoryDocument document)
    {
        var diagnostic = Source2RootSphereAggregationAnalyzer.Analyze(document);

        Assert.Equal("unsupported", diagnostic.Status);
        Assert.Equal("ROOT_SPHERE_AGGREGATION_INPUT_INCONSISTENT", diagnostic.ReasonCode);
        Assert.Empty(diagnostic.Rows);
    }

    private static void AssertUnavailable(CullingInventoryDocument document)
    {
        var diagnostic = Source2RootSphereAggregationAnalyzer.Analyze(document);

        Assert.Equal("unsupported", diagnostic.Status);
        Assert.Equal("ROOT_SPHERE_AGGREGATION_INPUT_UNAVAILABLE", diagnostic.ReasonCode);
        Assert.Empty(diagnostic.Rows);
    }

    private static string GetSchemaPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "schemas", "culling-envelope-contracts.schema.json");
    }
}
