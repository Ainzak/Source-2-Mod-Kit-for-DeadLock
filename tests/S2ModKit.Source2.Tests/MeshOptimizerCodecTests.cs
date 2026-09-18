using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;

namespace S2ModKit.Source2.Tests;

public sealed class MeshOptimizerCodecTests
{
    [Fact]
    public void ProbeReportsNotConfiguredWithoutSearchingTheMachine()
    {
        var capability = MeshOptimizerCodecProbe.Probe(null);

        Assert.Equal("not_configured", capability.Status);
        Assert.Null(capability.Identity);
        Assert.Contains("S2MODKIT_MESHOPTIMIZER_PATH", capability.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeReportsMissingAndNonNativeFilesAsMisconfigured()
    {
        var missing = Path.Combine(AppContext.BaseDirectory, $"missing-{Guid.NewGuid():N}.dll");

        var missingCapability = MeshOptimizerCodecProbe.Probe(missing);
        var managedCapability = MeshOptimizerCodecProbe.Probe(typeof(MeshOptimizerCodecTests).Assembly.Location);

        Assert.Equal("misconfigured", missingCapability.Status);
        Assert.Equal("misconfigured", managedCapability.Status);
        Assert.Null(missingCapability.Identity);
        Assert.Null(managedCapability.Identity);
    }

    [Fact]
    public void AdapterReportsMalformedConfiguredPathWithoutThrowing()
    {
        var adapter = new Source2CompiledModelAdapter("\0");

        Assert.Equal("misconfigured", adapter.GeometryCodecCapability.Status);
        Assert.Null(adapter.GeometryCodecCapability.Identity);
    }

    [Fact]
    public void AdapterRefusesGeometryCodecWhenItWasNotConfigured()
    {
        var adapter = new Source2CompiledModelAdapter();

        var exception = Assert.Throws<S2ModKitException>(() => adapter.OpenGeometryCodec());

        Assert.Equal("MESHOPTIMIZER_CAPABILITY_UNAVAILABLE", exception.Error.Code);
        Assert.Equal(ErrorCategory.UnsupportedCapability, exception.Error.Category);
    }

    [Fact]
    public void UnchangedRoundTripReturnsStableDecodedEvidence()
    {
        using var codec = new FakeCodec(corruptSecondDecode: false);
        var encoded = new byte[] { 1, 2, 3, 4 };

        var result = MeshOptimizerRoundTrip.VerifyUnchangedVertexBuffer(codec, encoded, 1, 4, 1);

        Assert.Equal(encoded, result.Decoded);
        Assert.Equal(encoded, result.Encoded);
        Assert.Equal(ContentHash.Compute(encoded), result.DecodedHash);
    }

    [Fact]
    public void UnchangedRoundTripRejectsDecodedDrift()
    {
        using var codec = new FakeCodec(corruptSecondDecode: true);

        Assert.Throws<InvalidDataException>(
            () => MeshOptimizerRoundTrip.VerifyUnchangedVertexBuffer(codec, [1, 2, 3, 4], 1, 4, 1));
    }

    private sealed class FakeCodec(bool corruptSecondDecode) : IMeshOptimizerCodec
    {
        private int decodeCount;

        public GeometryCodecIdentity Identity { get; } = new(
            "fake",
            "test",
            "test",
            ContentHash.Compute("fake-codec"u8),
            "1");

        public byte[] DecodeVertexBuffer(byte[] encoded, int vertexCount, int vertexStride)
        {
            var result = encoded.ToArray();
            if (corruptSecondDecode && ++decodeCount == 2)
            {
                result[0] ^= 0xff;
            }

            return result;
        }

        public byte[] DecodeIndexBuffer(byte[] encoded, int indexCount, int indexStride) => encoded.ToArray();

        public byte[] EncodeVertexBuffer(byte[] decoded, int vertexCount, int vertexStride, int version) => decoded.ToArray();

        public void Dispose()
        {
        }
    }
}
