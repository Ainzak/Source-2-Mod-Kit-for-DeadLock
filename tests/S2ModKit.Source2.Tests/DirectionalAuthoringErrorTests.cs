using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;
using ValveKeyValue;

namespace S2ModKit.Source2.Tests;

public sealed partial class Source2TransformMetadataAnalyzerTests
{
    [Theory]
    [InlineData(14u)]
    [InlineData(99u)]
    public void DirectionalAuthoringReportsUnsupportedMixedSkinningWithoutAdmittingIt(uint format)
    {
        var descriptor = MultiBufferDescriptor();
        descriptor["m_vertexBuffers"][1]["m_inputLayoutFields"][1]["m_Format"] = (KVObject)format;
        var geometry = MultiBufferGeometry();
        var before = geometry.VertexBuffers.Select(b => b.Decoded.ToArray()).ToArray();
        var failure = Assert.Throws<S2ModKitException>(() => Source2CompiledModelAdapter.ReadDirectionalAuthoringFacts(
            () => Source2TransformMetadataAnalyzer.AnalyzeVisualPreservationBuffers(descriptor, MultiBufferData(), geometry, "synthetic mixed skinning")));

        Assert.Equal("EXPERIMENTAL_LAYOUT_UNSUPPORTED", failure.Error.Code);
        Assert.Equal(ErrorCategory.UnsupportedCapability, failure.Error.Category);
        Assert.Equal("source2_adapter", failure.Error.Boundary);
        Assert.Contains($"BLENDINDICES format {format}", failure.Error.Summary, StringComparison.Ordinal);
        Assert.IsType<InvalidDataException>(failure.InnerException);
        for (var i = 0; i < before.Length; i++) Assert.Equal(before[i], geometry.VertexBuffers[i].Decoded);
        Assert.Throws<InvalidDataException>(() => Source2TransformMetadataAnalyzer.AnalyzeVisualPreservationBuffers(
            descriptor, MultiBufferData(), geometry, "unchanged raw reader"));
    }

    [Fact]
    public void DirectionalAuthoringPreservesSupportedCompleteMetadata()
    {
        var descriptor = MultiBufferDescriptor(); var data = MultiBufferData(); var geometry = MultiBufferGeometry();
        var before = geometry.VertexBuffers.Select(b => b.Decoded.ToArray()).ToArray();
        var expected = Source2TransformMetadataAnalyzer.AnalyzeVisualPreservationBuffers(descriptor, data, geometry, "synthetic mixed skinning");
        var actual = Source2CompiledModelAdapter.ReadDirectionalAuthoringFacts(
            () => Source2TransformMetadataAnalyzer.AnalyzeVisualPreservationBuffers(descriptor, data, geometry, "synthetic mixed skinning"));
        Assert.Equal(expected.VertexCount, actual.VertexCount);
        Assert.Equal(expected.SceneBounds, actual.SceneBounds);
        Assert.Equal(expected.BoneBounds.Select(b => b.BoneName), actual.BoneBounds.Select(b => b.BoneName));
        Assert.Equal(expected.BoneBounds.SelectMany(b => b.InfluencedVertices), actual.BoneBounds.SelectMany(b => b.InfluencedVertices));
        for (var i = 0; i < before.Length; i++) Assert.Equal(before[i], geometry.VertexBuffers[i].Decoded);
    }

    [Fact]
    public void DirectionalAuthoringDoesNotReclassifyTypedErrorsCancellationOrProgrammingFailures()
    {
        var typed = new S2ModKitException(new S2Error("EXPERIMENTAL_PROCEDURAL_UNSUPPORTED", "source2_adapter", "procedural", "reject", ErrorCategory.UnsupportedCapability));
        Assert.Same(typed, Assert.Throws<S2ModKitException>(() => Source2CompiledModelAdapter.ReadDirectionalAuthoringFacts<int>(() => throw typed)));
        var canceled = new OperationCanceledException();
        Assert.Same(canceled, Assert.Throws<OperationCanceledException>(() => Source2CompiledModelAdapter.ReadDirectionalAuthoringFacts<int>(() => throw canceled)));
        var unexpected = new InvalidOperationException("unexpected reader state");
        Assert.Same(unexpected, Assert.Throws<InvalidOperationException>(() => Source2CompiledModelAdapter.ReadDirectionalAuthoringFacts<int>(() => throw unexpected)));
    }
}
