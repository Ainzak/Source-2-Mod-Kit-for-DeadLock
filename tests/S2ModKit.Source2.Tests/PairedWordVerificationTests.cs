using System.Buffers.Binary;
using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Source2.Tests;

public sealed partial class DirectionalSourceBoundaryTests
{
    [Fact]
    public void IndependentPairWordAuditAcceptsManuallyAuthoredCoreWordsAndPins()
    {
        var (source, output, fields) = ManualPairWords(); var immutable = source.ToArray();
        var result = Source2CompiledModelAdapter.AuditPairedStoredWords(source, output, Position, Frame, 3, fields, 12, "manual", 0);
        Assert.Equal([2], result.PinnedIndices); Assert.Equal([0], result.Fields[0].ChangedPositionIndices); Assert.Equal([1], result.Fields[1].ChangedPositionIndices);
        Assert.All(result.Fields, f => Assert.Empty(f.ChangedFrameIndices)); Assert.Equal(immutable, source);
    }

    [Theory]
    [InlineData("second_position")]
    [InlineData("exterior_signed_zero")]
    [InlineData("hidden_attribute")]
    public void IndependentPairWordAuditRejectsWrongSecondFieldExteriorAndImmutableWords(string defect)
    {
        var (source, output, fields) = ManualPairWords(); var immutable = source.ToArray();
        if (defect == "second_position") BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(24 + 4), 1.51f);
        else if (defect == "exterior_signed_zero") BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(48 + 8), 0u);
        else output[24 + 16] ^= 1;
        var error = Assert.Throws<S2ModKitException>(() => Source2CompiledModelAdapter.AuditPairedStoredWords(source, output, Position, Frame, 3, fields, 12, "manual", 0));
        Assert.Equal("PAIRED_RESULT_DRIFT", error.Error.Code); Assert.Equal(immutable, source);
    }

    private static (byte[] Source, byte[] Output, PairedDirectionalField[] Fields) ManualPairWords()
    {
        var source = Bytes([new(-6, 1, -0f), new(6, 1, -0f), new(0, 0, -0f)]);
        var frame = Source2PackedFrameCodec.Encode(new(new(0, 0, 1), new(1, 0, 0), 1));
        for (var i = 0; i < 3; i++) BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(i * 24 + 12), frame);
        var output = source.ToArray();
        BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(4), 1.25f);
        BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(24 + 4), 1.5f);
        PairedDirectionalField Field(string id, float center, TransformVector3 scale) => new(id,
            new("directional_ellipsoid", 1, "model", new() { Kind = "explicit_point", Point = new() { X = center } },
                new() { X = 4, Y = 4, Z = 4 }, .4f, scale, new("directional_ellipsoid_numeric", 1)));
        return (source, output, [Field("left", -6, new() { X = 1.5f, Y = 1.25f, Z = 1 }), Field("right", 6, new() { X = 1.25f, Y = 1.5f, Z = 1 })]);
    }
}
