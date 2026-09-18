using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed class ModelVerifierTransformTests
{
    [Fact]
    public void TransformProfileAcceptsPlannedPositionAndMetadataChanges()
    {
        var fixture = CreateFixture();

        var result = ModelVerifier.Verify(fixture.Before, fixture.After, fixture.Plan);

        Assert.True(result.IsValid);
        Assert.Contains(result.Boundaries, boundary => boundary.Name == "selected_draw_calls_preserved" && boundary.Status == "passed");
        Assert.Contains(result.Boundaries, boundary => boundary.Name == "index_payloads_byte_identical" && boundary.Status == "passed");
        Assert.Contains(result.Boundaries, boundary => boundary.Name == "transform_geometry_postconditions" && boundary.Status == "passed");
    }

    [Fact]
    public void TransformProfileRejectsIndexPayloadChange()
    {
        var fixture = CreateFixture();
        var driftedIndexHash = ContentHash.Compute("drifted-index"u8);
        var mesh = fixture.After.Lods[0].Meshes[0];
        var geometry = mesh.Geometry!;
        var driftedGeometry = geometry with
        {
            IndexBuffers = [geometry.IndexBuffers[0] with { EncodedHash = driftedIndexHash }],
        };
        var driftedMesh = mesh with { Geometry = driftedGeometry };
        var driftedBlocks = fixture.After.Artifact.Blocks.Select(block =>
            block.Index == 2 ? block with { ContentHash = driftedIndexHash } : block).ToArray();
        var drifted = fixture.After with
        {
            Artifact = fixture.After.Artifact with { Blocks = driftedBlocks },
            Lods = [fixture.After.Lods[0] with { Meshes = [driftedMesh] }],
        };

        var result = ModelVerifier.Verify(fixture.Before, drifted, fixture.Plan);

        Assert.False(result.IsValid);
        Assert.Contains(result.Boundaries, boundary => boundary.Name == "index_payloads_byte_identical" && boundary.Status == "failed");
        Assert.Contains(result.Boundaries, boundary => boundary.Name == "non_target_blocks_byte_identical" && boundary.Status == "failed");
    }

    [Fact]
    public void TransformProfileRejectsUnexpectedDecodedVertexResult()
    {
        var fixture = CreateFixture();
        var mesh = fixture.After.Lods[0].Meshes[0];
        var geometry = mesh.Geometry!;
        var driftedGeometry = geometry with
        {
            VertexBuffers =
            [
                geometry.VertexBuffers[0] with
                {
                    DecodedHash = ContentHash.Compute("plausible-but-unplanned-positions"u8),
                },
            ],
        };
        var drifted = fixture.After with
        {
            Lods = [fixture.After.Lods[0] with { Meshes = [mesh with { Geometry = driftedGeometry }] }],
        };

        var result = ModelVerifier.Verify(fixture.Before, drifted, fixture.Plan);

        Assert.False(result.IsValid);
        Assert.Contains(result.Boundaries, boundary => boundary.Name == "transform_geometry_postconditions" && boundary.Status == "failed");
    }

    private static TransformFixture CreateFixture()
    {
        const string resourcePath = "models/test/accessory.vmdl_c";
        var inputHash = ContentHash.Compute("input"u8);
        var outputHash = ContentHash.Compute("output"u8);
        var beforeMdat = ContentHash.Compute("before-mdat"u8);
        var afterMdat = ContentHash.Compute("after-mdat"u8);
        var beforeMvtx = ContentHash.Compute("before-mvtx"u8);
        var afterMvtx = ContentHash.Compute("after-mvtx"u8);
        var midx = ContentHash.Compute("midx"u8);
        var data = ContentHash.Compute("data"u8);
        var beforeDecoded = ContentHash.Compute("before-decoded"u8);
        var afterDecoded = ContentHash.Compute("after-decoded"u8);
        var decodedIndex = ContentHash.Compute("decoded-index"u8);
        var vertexSet = ContentHash.Compute("vertex-set"u8);
        var codec = new GeometryCodecIdentity("meshoptimizer", "source2-vertex-v1", "portable", ContentHash.Compute("codec"u8), "1");
        var beforeBounds = Bounds(-1f, 1f);
        var afterBounds = Bounds(-2f, 2f);
        var drawCall = DrawCallSnapshot.Create(resourcePath, 0, 0, 0, "materials/accessory.vmat", 0, 3);
        var beforeDrawGeometry = new DrawCallGeometrySnapshot(drawCall.Id, 0, 0, 0, 3, 3, vertexSet, beforeBounds, true);
        var afterDrawGeometry = beforeDrawGeometry with { Bounds = afterBounds };
        var beforeGeometry = new MeshGeometrySnapshot(
            "ready",
            "synthetic",
            [new VertexBufferSnapshot(0, 1, 3, 12, beforeMvtx, beforeDecoded, new PositionLayout("R32G32B32_FLOAT", 0, 12))],
            [new IndexBufferSnapshot(0, 2, 3, 2, midx, decodedIndex)],
            [beforeDrawGeometry],
            codec);
        var afterGeometry = beforeGeometry with
        {
            VertexBuffers = [beforeGeometry.VertexBuffers[0] with { EncodedHash = afterMvtx, DecodedHash = afterDecoded }],
            DrawCalls = [afterDrawGeometry],
        };
        var beforeMesh = new MeshSnapshot(resourcePath, 0, 0, ContentHash.Compute("before-semantics"u8), [drawCall]) { Geometry = beforeGeometry };
        var afterMesh = beforeMesh with { ImmutableSemanticHash = ContentHash.Compute("after-semantics"u8), Geometry = afterGeometry };
        var beforeBlocks = new[]
        {
            Block("MDAT", 0, beforeMdat),
            Block("MVTX", 1, beforeMvtx),
            Block("MIDX", 2, midx),
            Block("DATA", 3, data),
        };
        var afterBlocks = new[]
        {
            Block("MDAT", 0, afterMdat),
            Block("MVTX", 1, afterMvtx),
            Block("MIDX", 2, midx),
            Block("DATA", 3, data),
        };
        var before = new ModelSnapshot(new ArtifactSnapshot(resourcePath, inputHash, 100, beforeBlocks), [new LodSnapshot(0, [beforeMesh])]);
        var after = new ModelSnapshot(new ArtifactSnapshot(resourcePath, outputHash, 110, afterBlocks), [new LodSnapshot(0, [afterMesh])]);
        var selected = new SelectedDrawCall(0, resourcePath, 0, 0, drawCall.Id, drawCall.MaterialPath, 0, 0, 3);
        var target = new PlannedGeometryTarget(
            0,
            resourcePath,
            0,
            0,
            0,
            0,
            1,
            2,
            beforeMvtx,
            midx,
            beforeDecoded,
            afterDecoded,
            decodedIndex,
            vertexSet,
            3,
            new PositionLayout("R32G32B32_FLOAT", 0, 12),
            beforeBounds,
            afterBounds,
            new TransformVector3(),
            2f,
            new TransformVector3(),
            1.7320508f,
            ["position"],
            codec);
        var operation = new PlannedOperation(
            "scale-accessory",
            "transform_component",
            1,
            [selected],
            [new PlannedTargetBlock(0, "MDAT", beforeMdat), new PlannedTargetBlock(1, "MVTX", beforeMvtx)])
        {
            GeometryTargets = [target],
        };
        var plan = new MutationPlan("synthetic-transform", inputHash, ContentHash.Compute("plan"u8), [operation]);
        return new TransformFixture(before, after, plan);
    }

    private static GeometryBounds Bounds(float min, float max) => new(
        new TransformVector3 { X = min, Y = min, Z = min },
        new TransformVector3 { X = max, Y = max, Z = max });

    private static ResourceBlockSnapshot Block(string type, int index, ContentHash hash) => new(type, index, index * 16, 16, hash);

    private sealed record TransformFixture(ModelSnapshot Before, ModelSnapshot After, MutationPlan Plan);
}
