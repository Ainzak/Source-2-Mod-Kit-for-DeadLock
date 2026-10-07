using S2ModKit.Adapters.Source2;
using S2ModKit.Domain;
using ValveKeyValue;
using ValveResourceFormat.Utils;

namespace S2ModKit.Source2.Tests;

public sealed class PairedConsumerInventoryTests
{
    [Fact]
    public void NamedFeInventoryBindsAllInputsAndLabelsKnownUnmappedControlsUnverified()
    {
        var source = Physics(); var rows = new List<PairedConsumerRecord>(); var categories = new List<string>();
        Source2CompiledModelAdapter.AddPairedFeConsumers(source, 1, Roots(), (category, id, block, path, hash, references) =>
        {
            categories.Add(category); rows.Add(new(id, block, path, hash, hash, "preserve_unverified", (references ?? []).ToArray(), hash));
        });
        Assert.Equal(source["m_pFeModel"].Count, rows.Count);
        Assert.Contains("fe_controls", categories); Assert.Contains("fe_nodes", categories); Assert.Contains("fe_colliders", categories);
        var controls = rows.Single(r => r.FieldPath == "m_pFeModel.m_CtrlName").References;
        Assert.Equal(0, controls[0].RootBoneIndex); Assert.Equal(-1, controls[1].RootBoneIndex);
        Assert.All(controls, r => Assert.Equal("preserve_unverified", r.Disposition));
        Assert.Equal(1, rows.Single(r => r.FieldPath == "m_pFeModel.m_SphereRigids").References[0].SourceIndex);
    }

    [Theory]
    [InlineData("unknown-physics")]
    [InlineData("unknown-fe")]
    [InlineData("token")]
    [InlineData("rest-count")]
    [InlineData("node-count")]
    [InlineData("collider-driver")]
    [InlineData("jiggle")]
    [InlineData("jiggle-kind")]
    [InlineData("node-string")]
    [InlineData("node-fraction")]
    [InlineData("token-string")]
    [InlineData("parent-string")]
    public void NamedFeInventoryRejectsUnknownIncompleteOrAmbiguousSourceInputs(string defect)
    {
        var source = Physics(); var fe = source["m_pFeModel"];
        switch (defect)
        {
            case "unknown-physics": source["unknown_driver"] = (KVObject)1; break;
            case "unknown-fe": fe["unknown_driver"] = (KVObject)1; break;
            case "token": fe["m_CtrlHash"] = Array((KVObject)StringToken.Get("driver"), (KVObject)0u); break;
            case "rest-count": fe["m_InitPose"] = Array(); break;
            case "node-count": fe["m_nNodeCount"] = (KVObject)3; break;
            case "collider-driver": fe["m_SphereRigids"][0]["nNode"] = (KVObject)2; break;
            case "jiggle": fe["m_JiggleBones"] = Array((KVObject)1); break;
            case "jiggle-kind": fe["m_JiggleBones"] = (KVObject)"empty"; break;
            case "node-string": fe["m_nNodeCount"] = (KVObject)"2"; break;
            case "node-fraction": fe["m_nNodeCount"] = (KVObject)2.25f; break;
            case "token-string": fe["m_CtrlHash"] = Array((KVObject)StringToken.Get("driver"), (KVObject)StringToken.Get("authored_unmapped_control").ToString(System.Globalization.CultureInfo.InvariantCulture)); break;
            case "parent-string": fe["m_SkelParents"] = Array((KVObject)"-1", (KVObject)0); break;
        }
        Assert.Throws<S2ModKitException>(() => Source2CompiledModelAdapter.AddPairedFeConsumers(source, 1, Roots(), (_, _, _, _, _, _) => { }));
    }

    private static PairedRootBone[] Roots() => [new(0, "driver", -1, 0, false, ContentHash.Compute("bind"u8))];
    private static KVObject Array(params KVObject[] children)
    {
        var result = KVObject.Array(); foreach (var child in children) result.Add(child); return result;
    }
    private static KVObject Physics()
    {
        var pose = Array((KVObject)0f, (KVObject)0f, (KVObject)0f, (KVObject)1f, (KVObject)0f, (KVObject)0f, (KVObject)0f, (KVObject)1f);
        var collider = KVObject.Collection(); collider["nNode"] = (KVObject)1;
        var fe = KVObject.Collection();
        fe["m_CtrlName"] = Array((KVObject)"driver", (KVObject)"authored_unmapped_control");
        fe["m_CtrlHash"] = Array((KVObject)StringToken.Get("driver"), (KVObject)StringToken.Get("authored_unmapped_control"));
        fe["m_nNodeCount"] = (KVObject)2; fe["m_nStaticNodes"] = (KVObject)1;
        fe["m_InitPose"] = Array(pose, pose); fe["m_SkelParents"] = Array((KVObject)(-1), (KVObject)0);
        fe["m_SphereRigids"] = Array(collider);
        var source = KVObject.Collection(); source["m_boneNames"] = Array(); source["m_bonesHash"] = Array(); source["m_pFeModel"] = fe;
        return source;
    }
}
