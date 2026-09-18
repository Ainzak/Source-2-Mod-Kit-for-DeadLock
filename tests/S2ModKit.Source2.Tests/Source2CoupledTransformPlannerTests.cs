using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveKeyValue;

namespace S2ModKit.Source2.Tests;

public sealed class Source2CoupledTransformPlannerTests
{
    private const int VertexDataOffset = 0x120;
    private const int IndexDataOffset = 0x168;

    [Fact]
    public void FreezesBothHalvesAndEveryAllowedByteClass()
    {
        var fixture = Fixture();

        var plan = Source2CoupledTransformPlanner.Plan(
            4,
            ContentHash.Compute([4]),
            fixture.Block,
            fixture.Visual,
            fixture.Metadata,
            fixture.Collision,
            new Point3(0f, 0f, 0f),
            2f,
            20f,
            20f);

        Assert.Equal([4, 7, 9], plan.TargetBlocks.Select(target => target.Index));
        Assert.Equal(3, plan.Visual.VertexCount);
        Assert.NotEqual(plan.Visual.MbufBlockInputHash, plan.Visual.ExpectedMbufBlockHash);
        Assert.Equal(4, plan.Visual.AllowedByteClasses.Count);
        Assert.Single(plan.Visual.BoneBoundsTargets);
        Assert.Equal(4, plan.Collision.VertexCount);
        Assert.NotEqual(plan.Collision.PositionInputHash, plan.Collision.ExpectedPositionHash);
        Assert.Equal(9, plan.Collision.AllowedByteClasses.Count);
        Assert.Equal(8f / 6f, plan.Collision.ExpectedAfter.Volume, 5);
        Assert.Equal(plan.Collision.HullPlanes.Count, plan.Collision.RegionPlanes.Count);
    }

    [Fact]
    public void IsDeterministicForTheSameImmutableFacts()
    {
        var fixture = Fixture();

        var first = Plan(fixture, 0.5f, 20f, 20f);
        var second = Plan(fixture, 0.5f, 20f, 20f);

        Assert.Equal(first.Visual.ExpectedMbufBlockHash, second.Visual.ExpectedMbufBlockHash);
        Assert.Equal(first.Collision.ExpectedPositionHash, second.Collision.ExpectedPositionHash);
        Assert.Equal(first.Collision.ExpectedAfter.MassProperties, second.Collision.ExpectedAfter.MassProperties);
        Assert.Equal(first.Collision.RegionPlanes, second.Collision.RegionPlanes);
    }

    [Fact]
    public void IndependentStructuralProfilesReuseTheCoupledPathWithoutFixedNamesOrBoneIndex()
    {
        var firstFixture = Fixture(
            "models/synthetic/first_accessory.vmdl_c",
            "materials/synthetic/first_surface.vmat",
            "first_anchor",
            0,
            0x51);
        var secondFixture = Fixture(
            "models/independent/second_ornament.vmdl_c",
            "materials/independent/second_surface.vmat",
            "second_socket",
            3,
            0x62);

        var first = Plan(firstFixture, 2f, 20f, 20f);
        var second = Plan(secondFixture, 2f, 20f, 20f);
        var rewritten = Source2RawMbufWriter.Rewrite(
            secondFixture.Block,
            [DrawCall(secondFixture.ResourcePath, secondFixture.MaterialPath)],
            MeshData(second.Visual),
            secondFixture.Metadata,
            second.Visual);

        Assert.Equal(0, Assert.Single(first.Visual.BoneBoundsTargets).BoneIndex);
        Assert.Equal(3, Assert.Single(second.Visual.BoneBoundsTargets).BoneIndex);
        Assert.Equal("second_socket", second.Visual.BoneBoundsTargets[0].BoneName);
        Assert.Equal(second.Visual.ExpectedMbufBlockHash, ContentHash.Compute(rewritten.Payload.Span));
        Assert.NotEqual(first.Visual.DrawCallId, second.Visual.DrawCallId);
        Assert.NotEqual(first.Visual.MbufBlockInputHash, second.Visual.MbufBlockInputHash);
        Assert.Equal(first.TargetBlocks.Select(block => block.Type), second.TargetBlocks.Select(block => block.Type));
    }

    [Theory]
    [InlineData(1f, 20f, 20f)]
    [InlineData(2f, 0f, 20f)]
    [InlineData(2f, 20f, 0f)]
    [InlineData(2f, 0.1f, 20f)]
    public void FailsClosedWhenEitherHalfIsNotPredictable(float scale, float visualLimit, float collisionLimit)
    {
        var fixture = Fixture();

        var exception = Assert.Throws<S2ModKitException>(() => Plan(fixture, scale, visualLimit, collisionLimit));

        Assert.Equal("COUPLED_TRANSFORM_INCOMPLETE", exception.Error.Code);
    }

    [Fact]
    public void RawWriterChangesOnlyPositionsAndRequiredVisualBounds()
    {
        var fixture = Fixture();
        var plan = Plan(fixture, 2f, 20f, 20f);
        var original = fixture.Block.Payload.ToArray();
        var meshData = MeshData(plan.Visual);

        var result = Source2RawMbufWriter.Rewrite(
            fixture.Block,
            [DrawCall()],
            meshData,
            fixture.Metadata,
            plan.Visual);

        Assert.Equal(plan.Visual.ExpectedMbufBlockHash, ContentHash.Compute(result.Payload.Span));
        Assert.Equal(plan.Visual.ExpectedDecodedVertexBufferHash, result.Reopened.Geometry.VertexBuffers[0].Snapshot.DecodedHash);
        Assert.Equal(plan.Visual.ExpectedAfterBounds, result.Reopened.Geometry.DrawCalls[0].Snapshot.Bounds);
        Assert.True(original.AsSpan().SequenceEqual(fixture.Block.Payload.Span));
        Assert.Equal(plan.Visual.ExpectedAfterBounds.Min.X, meshData["m_sceneObjects"][0]["m_vMinBounds"][0].ToDouble(CultureInfo.InvariantCulture));
        Assert.Equal(plan.Visual.BoneBoundsTargets[0].ExpectedSphereRadius, meshData["m_skeleton"]["m_bones"][0]["m_flSphereRadius"].ToDouble(CultureInfo.InvariantCulture));
        Assert.Equal("unchanged", meshData["m_materialSentinel"].ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void RawWriterRejectsPlanHashDriftWithoutMutatingInput()
    {
        var fixture = Fixture();
        var plan = Plan(fixture, 2f, 20f, 20f);
        var original = fixture.Block.Payload.ToArray();
        var drifted = plan.Visual with { ExpectedMbufBlockHash = ContentHash.Compute([99]) };

        var exception = Assert.Throws<S2ModKitException>(() => Source2RawMbufWriter.Rewrite(
            fixture.Block,
            [DrawCall()],
            MeshData(plan.Visual),
            fixture.Metadata,
            drifted));

        Assert.Equal("COUPLED_TRANSFORM_INCOMPLETE", exception.Error.Code);
        Assert.True(original.AsSpan().SequenceEqual(fixture.Block.Payload.Span));
    }

    private static PlannedCoupledTransformTarget Plan(FixtureData fixture, float scale, float visualLimit, float collisionLimit) =>
        Source2CoupledTransformPlanner.Plan(
            4,
            ContentHash.Compute([4]),
            fixture.Block,
            fixture.Visual,
            fixture.Metadata,
            fixture.Collision,
            new Point3(0f, 0f, 0f),
            scale,
            visualLimit,
            collisionLimit);

    private static FixtureData Fixture(
        string resourcePath = "models/synthetic.vmdl_c",
        string materialPath = "materials/synthetic.vmat",
        string boneName = "root",
        int boneIndex = 0,
        byte payloadMarker = 0x5a)
    {
        var payload = MbufPayload(payloadMarker);
        var block = new Source2ResourceBlock(7, "MBUF", 0, 0, payload, ReadOnlyMemory<byte>.Empty);
        var visual = Source2RawMbufReader.AnalyzeDetailed(block, [DrawCall(resourcePath, materialPath)], "coupled synthetic visual");
        var visualBounds = visual.Geometry.DrawCalls[0].Snapshot.Bounds;
        var identity = ImmutableArray.Create(1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f);
        var metadata = new Source2WholeMeshTransformAnalysis(
            0,
            visualBounds,
            visual.Geometry.DrawCalls[0].Snapshot.VertexSetHash,
            3,
            boneName,
            1,
            [boneName],
            [new Source2BoneBoundsAnalysis(
                boneIndex,
                boneName,
                ContentHash.Compute([1]),
                identity,
                [0, 1, 2],
                new TransformVector3 { X = 0.5f, Y = 0.5f, Z = 0f },
                new TransformVector3 { X = 1f, Y = 1f, Z = 0f },
                visualBounds,
                1f)]);
        var positions = new[]
        {
            new Point3(0f, 0f, 0f),
            new Point3(1f, 0f, 0f),
            new Point3(0f, 1f, 0f),
            new Point3(0f, 0f, 1f),
        };
        var faces = ImmutableArray.Create(
            new ConvexFace([0, 2, 1]),
            new ConvexFace([0, 1, 3]),
            new ConvexFace([0, 3, 2]),
            new ConvexFace([1, 2, 3]));
        var derived = ConvexHullGeometry.Derive(positions, faces);
        var planes = derived.FacePlanes.Select(plane => new Source2PhysPlane(plane.Normal, plane.Offset)).ToArray();
        var collision = new Source2ConvexPhysAnalysis(
            9,
            ContentHash.Compute([9]),
            positions,
            derived.Bounds,
            derived.VertexCentroid,
            derived.MaximumAngularRadius,
            derived.Volume,
            derived.SurfaceArea,
            1f,
            1f,
            1f,
            MassProperties(derived),
            planes,
            planes,
            faces,
            derived,
            12,
            4,
            4,
            1,
            ContentHash.Compute([10]),
            ContentHash.Compute([11]),
            ContentHash.Compute([12]),
            ContentHash.Compute([13]),
            ContentHash.Compute([14]),
            ContentHash.Compute([15]),
            "default");
        return new FixtureData(block, visual, metadata, collision, resourcePath, materialPath);
    }

    private static float[] MassProperties(ConvexHullDerivedValues values)
    {
        var inertia = values.MassProperties.Inertia;
        var center = values.MassProperties.CenterOfMass;
        return [inertia.XX, inertia.XY, inertia.XZ, center.X, inertia.XY, inertia.YY, inertia.YZ, center.Y, inertia.XZ, inertia.YZ, inertia.ZZ, center.Z];
    }

    private static KVObject MeshData(PlannedRawMbufTransformTarget target)
    {
        var bone = target.BoneBoundsTargets[0];
        var scene = Object(
            ("m_vMinBounds", Vector(target.BeforeBounds.Min)),
            ("m_vMaxBounds", Vector(target.BeforeBounds.Max)));
        var skeletonBone = Object(
            ("m_boneName", bone.BoneName),
            ("m_bbox", Object(
                ("m_vecCenter", Vector(bone.BeforeCenter)),
                ("m_vecSize", Vector(bone.BeforeSize)))),
            ("m_flSphereRadius", bone.SphereRadius));
        var bones = Enumerable.Range(0, bone.BoneIndex + 1)
            .Select(index => index == bone.BoneIndex ? skeletonBone : Object(("m_boneName", $"unused_{index}")))
            .ToArray();
        return Object(
            ("m_sceneObjects", Array(scene)),
            ("m_skeleton", Object(("m_bones", Array(bones)))),
            ("m_materialSentinel", "unchanged"));
    }

    private static GeometryDrawCallInput DrawCall(
        string resourcePath = "models/synthetic.vmdl_c",
        string materialPath = "materials/synthetic.vmat")
    {
        var snapshot = DrawCallSnapshot.Create(resourcePath, 0, 4, 0, materialPath, 0, 3);
        return new GeometryDrawCallInput(snapshot, Object(
            ("m_nPrimitiveType", "RENDER_PRIM_TRIANGLES"),
            ("m_nBaseVertex", 0),
            ("m_nVertexCount", 3),
            ("m_indexBuffer", BufferReference()),
            ("m_vertexBuffers", Array(BufferReference()))));
    }

    private static byte[] MbufPayload(byte payloadMarker = 0x5a)
    {
        var result = new byte[IndexDataOffset + 6];
        WriteRelative(result, 0, 0x10); WriteUInt32(result, 4, 1);
        WriteRelative(result, 8, 0x28); WriteUInt32(result, 12, 1);
        WriteUInt32(result, 0x10, 3); WriteUInt32(result, 0x14, 24); WriteRelative(result, 0x18, 0x40); WriteUInt32(result, 0x1c, 4); WriteRelative(result, 0x20, VertexDataOffset); WriteUInt32(result, 0x24, 72);
        WriteUInt32(result, 0x28, 3); WriteUInt32(result, 0x2c, 2); WriteInt32(result, 0x30, 0); WriteUInt32(result, 0x34, 0); WriteRelative(result, 0x38, IndexDataOffset); WriteUInt32(result, 0x3c, 6);
        WriteLayout(result, 0, "POSITION", 6, 0); WriteLayout(result, 1, "TEXCOORD", 37, 12); WriteLayout(result, 2, "NORMAL", 42, 16); WriteLayout(result, 3, "BLENDINDICES", 30, 20);
        WriteVertex(result, 0, 0, 0, 0, payloadMarker); WriteVertex(result, 1, 1, 0, 0, payloadMarker); WriteVertex(result, 2, 0, 1, 0, payloadMarker);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(IndexDataOffset), 0); BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(IndexDataOffset + 2), 1); BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(IndexDataOffset + 4), 2);
        return result;
    }

    private static void WriteLayout(byte[] bytes, int ordinal, string semantic, uint format, uint offset)
    {
        var start = 0x40 + (ordinal * 56); Encoding.ASCII.GetBytes(semantic).CopyTo(bytes, start); WriteUInt32(bytes, start + 36, format); WriteUInt32(bytes, start + 40, offset);
    }

    private static void WriteVertex(byte[] bytes, int ordinal, float x, float y, float z, byte payloadMarker)
    {
        var start = VertexDataOffset + (ordinal * 24); WriteSingle(bytes, start, x); WriteSingle(bytes, start + 4, y); WriteSingle(bytes, start + 8, z); bytes.AsSpan(start + 12, 8).Fill(payloadMarker); bytes[start + 20] = 0; bytes.AsSpan(start + 21, 3).Fill(0x7f);
    }

    private static void WriteRelative(byte[] bytes, int fieldOffset, int targetOffset) => WriteInt32(bytes, fieldOffset, targetOffset - fieldOffset);
    private static void WriteSingle(byte[] bytes, int offset, float value) => WriteInt32(bytes, offset, BitConverter.SingleToInt32Bits(value));
    private static void WriteInt32(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    private static void WriteUInt32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    private static KVObject BufferReference() => Object(("m_hBuffer", 0), ("m_nBindOffsetBytes", 0));
    private static KVObject Object(params (string Key, KVObject Value)[] values) { var result = KVObject.Collection(); foreach (var value in values) result.Add(value.Key, value.Value); return result; }
    private static KVObject Array(params KVObject[] values) { var result = KVObject.Array(); foreach (var value in values) result.Add(value); return result; }
    private static KVObject Vector(TransformVector3 value) => Array(value.X, value.Y, value.Z);

    private sealed record FixtureData(
        Source2ResourceBlock Block,
        Source2RawMbufAnalysis Visual,
        Source2WholeMeshTransformAnalysis Metadata,
        Source2ConvexPhysAnalysis Collision,
        string ResourcePath,
        string MaterialPath);
}
