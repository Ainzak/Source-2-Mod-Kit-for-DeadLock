using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed class CompatibilityContractTests
{
    [Fact]
    public void ScanEntryAssociatesNavigationIdentityWithoutChangingStructuralSignature()
    {
        var hash = ContentHash.Compute("model"u8);
        var signature = StructuralSignature.Create(
            "source2.model",
            [new StructuralFact("storage", "mvtx_midx")]);
        var compatibility = new StructuralCompatibilityAssessment(signature, [], []);

        var first = new CompatibilityScanEntry(
            "hero-a.primary",
            "models/heroes/a/a.vmdl_c",
            hash,
            5,
            compatibility);
        var second = first with
        {
            ResourceId = "hero-b.primary",
            LogicalPath = "models/heroes/b/b.vmdl_c",
        };

        Assert.Equal(first.Compatibility.Signature, second.Compatibility.Signature);
        Assert.NotEqual(first.ResourceId, second.ResourceId);
    }

    [Fact]
    public void StructuralAnalyzerRequestContainsOnlyArtifactAndInspectedModel()
    {
        var parameters = typeof(StructuralProfileAnalysisRequest)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(static parameter => parameter.ParameterType)
            .ToArray();

        Assert.Equal([typeof(ArtifactContent), typeof(ModelSnapshot)], parameters);
    }
}
