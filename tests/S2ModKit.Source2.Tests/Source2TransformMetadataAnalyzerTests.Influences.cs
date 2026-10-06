using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Source2.Tests;

public sealed partial class Source2TransformMetadataAnalyzerTests
{
    private static readonly Source2InfluenceRootBone[] InfluenceRoots = [new(0, "ordinary-root", false), new(1, "simulated-root", true)];
    private static CoordinatedFieldMath InfluenceField() => new(new CoordinatedAxisRampField
    {
        Version = 1,
        CoordinateSpace = "model",
        Axis = "z",
        PinnedThrough = 1,
        FullFrom = 10,
        UniformScale = 1.25f,
        Pivot = new(),
    }, 64);

    [Fact]
    public void InfluenceReportIncludesTinyWeightsAndSeparatesPinnedProceduralContributors()
    {
        var geometry = Geometry(SkinnedVertices(
            ((0, 0, -2), [0, 99, 99, 99], [255, 0, 0, 0]),
            ((0, 0, 4), [1, 0, 99, 99], [254, 1, 0, 0]),
            ((0, 0, 12), [1, 99, 99, 99], [255, 0, 0, 0])), [0, 1, 2]);
        var report = InfluenceMesh(geometry, [1, 0], InfluenceField());
        Assert.Equal("reported", report.Status);
        Assert.NotNull(report.RemapHash);
        var buffer = Assert.Single(report.Buffers);
        Assert.True(buffer.CompleteIndexedCoverage);
        Assert.Equal(2, buffer.ProceduralVertices);
        Assert.Contains("EXPERIMENTAL_PROCEDURAL_UNSUPPORTED", buffer.ObservedGateReasons);
        Assert.Equal("not_assessed_full_planner_required", buffer.AdmissionStatus);
        var effects = Assert.IsType<Source2InfluenceFieldEffects>(buffer.FieldEffects);
        Assert.Equal((1, 1, 1), (effects.FullVertices, effects.TransitionVertices, effects.PinnedVertices));
        Assert.Equal(1, effects.ChangedProceduralVertices);
        Assert.Equal("not_assessed", effects.FrameEffectsStatus);
        var simulated = Assert.Single(buffer.Bones, b => b.IsProceduralCloth);
        Assert.Equal(0, simulated.RenderBoneIndex);
        Assert.Equal(1, simulated.RootBoneIndex);
        Assert.Equal("simulated-root", simulated.RootBoneName);
        Assert.Equal(2, simulated.ContributorCount);
        Assert.Equal(1, simulated.ChangedPositionContributors);
        Assert.Equal(1, simulated.PinnedContributors);
    }

    [Fact]
    public void InfluenceReportCountsUnindexedVerticesAndDeduplicatesRepeatedBoneSlots()
    {
        var geometry = Geometry(SkinnedVertices(
            ((0, 0, 0), [1, 1, 99, 99], [254, 1, 0, 0]),
            ((1, 0, 0), [1, 99, 99, 99], [255, 0, 0, 0]),
            ((2, 0, 0), [0, 99, 99, 99], [255, 0, 0, 0])), [0, 1]);
        var buffer = Assert.Single(InfluenceMesh(geometry, [1, 0]).Buffers);
        Assert.False(buffer.CompleteIndexedCoverage);
        Assert.Equal(2, buffer.IndexedVertices);
        Assert.Equal(1, buffer.ProceduralVertices);
        Assert.Equal(2, Assert.Single(buffer.Bones, b => !b.IsProceduralCloth).ContributorCount);
        Assert.Null(buffer.FieldEffects);
        Assert.All(buffer.Bones, b => Assert.Null(b.ChangedPositionContributors));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InfluenceReportRejectsMissingOrInvalidRemapWithoutIdentityFallback(bool invalid)
    {
        var result = InfluenceMesh(RigidGeometry(((0, 0, 0), [0, 0, 0, 0])), invalid ? [9, 0] : null, rigid: true);
        Assert.Equal("unsupported", result.Status);
        Assert.Empty(result.Buffers);
        Assert.Contains("remap", result.Failure!, StringComparison.Ordinal);
    }

    [Fact]
    public void InfluenceReportRejectsAmbiguousRootNamesAndTruncatedActiveRemap()
    {
        var geometry = RigidGeometry(((0, 0, 0), [1, 0, 0, 0]));
        var ambiguous = InfluenceMesh(geometry, [0, 1], rigid: true,
            roots: [new(0, "duplicate", false), new(1, "duplicate", true)]);
        Assert.Equal("unsupported", ambiguous.Status);
        Assert.Empty(ambiguous.Buffers);
        var truncated = InfluenceMesh(geometry, [0], rigid: true);
        Assert.Equal("unsupported", truncated.Status);
        Assert.Empty(truncated.Buffers);
    }

    [Fact]
    public void InfluenceReportRejectsMalformedWeightsWithoutPublishingPartialBufferFacts()
    {
        var geometry = Geometry(SkinnedVertices(
            ((0, 0, 0), [0, 1, 0, 0], [255, 0, 0, 0]),
            ((1, 0, 0), [0, 1, 0, 0], [254, 0, 0, 0])), [0, 1]);
        var result = InfluenceMesh(geometry, [0, 1]);
        Assert.Equal("unsupported", result.Status);
        Assert.Contains("sum", result.Failure!, StringComparison.Ordinal);
        Assert.Empty(result.Buffers);
    }

    [Fact]
    public void InfluenceReportDoesNotConfuseFullStrengthPivotWithPinnedVertex()
    {
        var field = new CoordinatedFieldMath(new CoordinatedAxisRampField
        {
            Version = 1,
            CoordinateSpace = "model",
            Axis = "z",
            PinnedThrough = -2,
            FullFrom = -1,
            UniformScale = 0.75f,
            Pivot = new(),
        }, 64);
        var buffer = Assert.Single(InfluenceMesh(RigidGeometry(((0, 0, 0), [0, 0, 0, 0])), [0, 1], field, rigid: true).Buffers);
        Assert.Equal(1, buffer.FieldEffects!.FullVertices);
        Assert.Equal(0, buffer.FieldEffects.PinnedVertices);
        Assert.Equal(0, buffer.FieldEffects.ChangedPositionVertices);
        Assert.Equal(1, buffer.FieldEffects.UnchangedPositionVertices);
    }

    private static Source2InfluenceMesh InfluenceMesh(Source2GeometryAnalysis geometry, int[]? remap,
        CoordinatedFieldMath? field = null, bool rigid = false, Source2InfluenceRootBone[]? roots = null)
    {
        var mesh = MeshDataWithBoneBounds(Bounds((-5, -5, -5), (20, 20, 20)),
            ("render-sim", "", (0, 0, 0), (1, 1, 1), 1), ("render-ordinary", "render-sim", (0, 0, 0), (1, 1, 1), 1));
        if (rigid) WithWeightCount(mesh, 1);
        return Source2InfluenceDiagnostic.BuildMesh(0, 0, 3, 1,
            rigid ? RigidVertexDescriptor() : VertexDescriptor(), mesh, geometry, roots ?? InfluenceRoots, remap, field);
    }
}
