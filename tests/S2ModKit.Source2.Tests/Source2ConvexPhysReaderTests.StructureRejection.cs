using System.Buffers.Binary;
using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveKeyValue;
using ValveKeyValue.KeyValues3;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Source2.Tests;

public sealed partial class Source2ConvexPhysReaderTests
{
    [Fact]
    public void RejectsAdditionalPhysicsControl()
    {
        var coupled = ControlRoot();
        var extraPhysics = Object(("embedded_physics", Object(("phys_data_block", PhysBlockIndex))));

        var exception = Assert.Throws<S2ModKitException>(() => Source2ConvexPhysReader.RequireUniqueCoupledControl(
            [coupled, extraPhysics],
            coupled,
            "synthetic controls"));

        Assert.Equal("PHYS_COUPLING_AMBIGUOUS", exception.Error.Code);
    }

    [Fact]
    public void RejectsPhysicsInDifferentControl()
    {
        var meshControl = Object(("embedded_meshes", KvArray(Object(("name", "synthetic_hat")))));
        var physicsControl = Object(("embedded_physics", Object(("phys_data_block", PhysBlockIndex))));

        var exception = Assert.Throws<S2ModKitException>(() => Source2ConvexPhysReader.RequireUniqueCoupledControl(
            [meshControl, physicsControl],
            meshControl,
            "synthetic controls"));

        Assert.Equal("PHYS_COUPLING_AMBIGUOUS", exception.Error.Code);
    }

    [Fact]
    public void RejectsMissingEmbeddedPhysicsReference()
    {
        var controlWithoutPhysics = Object(
            ("embedded_meshes", KvArray(Object(("name", "synthetic_hat")))));

        var exception = Assert.Throws<S2ModKitException>(() => Source2ConvexPhysReader.AnalyzeDetailed(
            controlWithoutPhysics,
            Envelope("phys"u8.ToArray()),
            _ => PhysRoot(),
            VisualBounds(),
            "synthetic physics"));

        Assert.Equal("PHYS_REFERENCE_UNSUPPORTED", exception.Error.Code);
    }

    [Fact]
    public void RejectsAmbiguousEmbeddedPhysicsKeys()
    {
        var control = Object(("embedded_physics", Object(("phys_data_block", PhysBlockIndex), ("extra", 1))));

        var exception = Assert.Throws<S2ModKitException>(() => Source2ConvexPhysReader.AnalyzeDetailed(
            control,
            Envelope("phys"u8.ToArray()),
            _ => PhysRoot(),
            VisualBounds(),
            "synthetic physics"));

        Assert.Equal("PHYS_REFERENCE_UNSUPPORTED", exception.Error.Code);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    public void RejectsReferenceToMissingPhysBlock(int declaredIndex)
    {
        var exception = Assert.Throws<S2ModKitException>(() => Source2ConvexPhysReader.AnalyzeDetailed(
            ControlRoot(declaredIndex),
            Envelope("phys"u8.ToArray()),
            _ => PhysRoot(),
            VisualBounds(),
            "synthetic physics"));

        Assert.Equal("PHYS_REFERENCE_UNSUPPORTED", exception.Error.Code);
    }

    [Fact]
    public void RejectsSemanticRootWithoutCharacterizedPayload()
    {
        var exception = Assert.Throws<S2ModKitException>(() => Source2ConvexPhysReader.AnalyzeDetailed(
            ControlRoot(),
            Envelope("phys"u8.ToArray()),
            _ => null,
            VisualBounds(),
            "synthetic physics"));

        Assert.Equal("PHYS_REFERENCE_UNSUPPORTED", exception.Error.Code);
    }

    [Fact]
    public void RejectsSecondPhysBlock()
    {
        var payload = "phys"u8.ToArray();
        var envelope = new Source2ResourceEnvelope(
            12,
            1,
            16,
            16,
            ReadOnlyMemory<byte>.Empty,
            [
                Block(PhysBlockIndex, payload),
                Block(3, payload),
            ]);

        var exception = Assert.Throws<S2ModKitException>(() => Source2ConvexPhysReader.AnalyzeDetailed(
            ControlRoot(),
            envelope,
            _ => PhysRoot(),
            VisualBounds(),
            "synthetic physics"));

        Assert.Equal("PHYS_REFERENCE_UNSUPPORTED", exception.Error.Code);
    }

    [Fact]
    public void RejectsAdditionalPart()
    {
        var root = PhysRoot();
        var parts = (KVObject)root["m_parts"]!;
        parts.Add(parts[0]);

        AssertCode("PHYS_GRAPH_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsSphereInShape()
    {
        var root = PhysRoot();
        Shape(root)["m_spheres"] = KvArray(Object(("m_vCenter", Vector(0, 0, 0)), ("m_flRadius", 1f)));

        AssertCode("PHYS_GRAPH_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsNonZeroPartMass()
    {
        var root = PhysRoot();
        Part(root)["m_flMass"] = new KVObject(2f);

        AssertCode("PHYS_GRAPH_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsNonEmptyBindPose()
    {
        var root = PhysRoot();
        root["m_bindPose"] = KvArray(Vector(0, 0, 0));

        AssertCode("PHYS_COORDINATE_MAPPING_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsBoneParents()
    {
        var root = PhysRoot();
        root["m_boneParents"] = KvArray(new KVObject(1));

        AssertCode("PHYS_COORDINATE_MAPPING_UNSUPPORTED", root);
    }

    [Theory]
    [InlineData("m_joints")]
    [InlineData("m_constraints2")]
    public void RejectsJointedOrConstrainedGraph(string key)
    {
        var root = PhysRoot();
        root[key] = KvArray(Object(("placeholder", 1)));

        AssertCode("PHYS_GRAPH_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsDeformableFeModel()
    {
        var root = PhysRoot();
        root["m_pFeModel"] = Object(("placeholder", 1));

        AssertCode("PHYS_GRAPH_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsExtraCollisionAttribute()
    {
        var root = PhysRoot();
        var attributes = (KVObject)root["m_collisionAttributes"]!;
        attributes.Add(attributes[0]);
        root["m_surfacePropertyHashes"] = KvArray(new KVObject(1977497166u), new KVObject(1u));

        AssertCode("PHYS_GRAPH_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsUnknownRootKey()
    {
        var root = PhysRoot();
        root.Add("m_futureField", new KVObject(1));

        AssertCode("PHYS_GRAPH_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsNonZeroHullCollisionAttributeIndex()
    {
        var root = PhysRoot();
        HullDescriptor(root)["m_nCollisionAttributeIndex"] = new KVObject(1);

        AssertCode("PHYS_GRAPH_UNSUPPORTED", root);
    }

}
