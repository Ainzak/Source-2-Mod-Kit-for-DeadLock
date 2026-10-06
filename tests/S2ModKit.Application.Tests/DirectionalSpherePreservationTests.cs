using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed partial class ExperimentalVisualContractTests
{
    [Theory]
    [InlineData("root_sphere")]
    [InlineData("render_sphere")]
    public void DirectionalContractsPreserveUnchangedResourceZeroSpheresButRejectAffectedZeroSpheres(string category)
    {
        var plan = DirectionalPlan(); var t = plan.Operations[0].DirectionalTransformTarget!;
        var original = t.PreservationTargets[0];
        var target = original with
        {
            Category = category,
            Scope = "resource",
            OriginalWords = [0],
            ResourceBlockIndex = category == "root_sphere" ? original.ResourceBlockIndex : t.Buffers[0].ResourceBlockIndex,
            FieldPath = category == "root_sphere" ? "m_modelSkeleton.m_boneSphere[0]" : "m_skeleton.m_bones[0].m_flSphereRadius",
            SourcePayloadHash = category == "root_sphere" ? original.SourcePayloadHash : t.SourceBlocks.Single(b => b.Index == t.Buffers[0].ResourceBlockIndex).InputHash
        };
        var retained = RehashDirectional(plan, t with { PreservationTargets = [target] });
        _ = ReadPlan(JsonDefaults.Serialize(retained));
        Assert.Throws<S2ModKitException>(() => DirectionalContractValidator.ValidatePlan(
            RehashDirectional(plan, t with { PreservationTargets = [target with { Scope = "affected" }] })));
    }
}
