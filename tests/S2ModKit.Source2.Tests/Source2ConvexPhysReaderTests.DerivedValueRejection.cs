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
    public void RejectsDeclaredBoundsDrift()
    {
        var root = PhysRoot();
        BoundsCollection(root)["m_vMinBounds"] = Vector(0.5f, 0f, 0f);

        AssertCode("PHYS_DERIVED_FIELD_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsNonFiniteDerivedVolume()
    {
        var root = PhysRoot();
        Hull(root)["m_flVolume"] = new KVObject(float.NaN);

        AssertCode("PHYS_DERIVED_FIELD_UNSUPPORTED", root);
    }

    [Theory]
    [InlineData("m_flMaxAngularRadius", 1f)]
    [InlineData("m_flVolume", 0.25f)]
    [InlineData("m_flSurfaceArea", 3f)]
    public void RejectsFiniteDerivedScalarDrift(string field, float value)
    {
        var root = PhysRoot();
        Hull(root)[field] = new KVObject(value);

        AssertCode("PHYS_DERIVED_FIELD_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsCentroidDrift()
    {
        var root = PhysRoot();
        Hull(root)["m_vCentroid"] = Vector(0.5f, 0.25f, 0.25f);

        AssertCode("PHYS_DERIVED_FIELD_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsMassPropertyDrift()
    {
        var root = PhysRoot();
        var mass = (KVObject)Hull(root)["m_MassProperties"]!;
        var values = mass.Values.ToArray();
        values[0] = new KVObject(1f);
        Hull(root)["m_MassProperties"] = KvArray(values);

        AssertCode("PHYS_DERIVED_FIELD_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsHullPlaneAssignedToWrongFace()
    {
        var root = PhysRoot();
        var planes = Planes();
        var first = planes[..16];
        planes.AsSpan(16, 16).CopyTo(planes);
        first.CopyTo(planes, 16);
        Hull(root)["m_Planes"] = KVObject.Blob(planes);

        AssertCode("PHYS_DERIVED_FIELD_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsZeroOrthographicArea()
    {
        var root = PhysRoot();
        Hull(root)["m_vOrthographicAreas"] = Vector(0f, 0.5f, 0.5f);

        AssertCode("PHYS_DERIVED_FIELD_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsUnknownMassPropertyLayout()
    {
        var root = PhysRoot();
        var values = new KVObject[11];
        Array.Fill(values, new KVObject(0f));
        Hull(root)["m_MassProperties"] = KvArray(values);

        AssertCode("PHYS_DERIVED_FIELD_UNSUPPORTED", root);
    }

    [Fact]
    public void RejectsDisjointVisualBounds()
    {
        var payload = "phys"u8.ToArray();
        var bounds = new GeometryBounds(
            new TransformVector3 { X = 10f, Y = 10f, Z = 10f },
            new TransformVector3 { X = 20f, Y = 20f, Z = 20f });

        var exception = Assert.Throws<S2ModKitException>(() => Source2ConvexPhysReader.AnalyzeDetailed(
            ControlRoot(),
            Envelope(payload),
            _ => PhysRoot(),
            bounds,
            "synthetic physics"));

        Assert.Equal("PHYS_COUPLING_AMBIGUOUS", exception.Error.Code);
    }

}
