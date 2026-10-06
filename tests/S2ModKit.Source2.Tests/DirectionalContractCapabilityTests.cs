using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Source2.Tests;

public sealed class DirectionalContractCapabilityTests
{
    [Fact]
    public void IncompleteDirectionalPlansDoNotAdvertiseCandidateWriting()
    {
        var hash = ContentHash.Compute("directional-contract-only"u8);
        var model = new ModelSnapshot(new("models/test.vmdl_c", hash, 12, []), []);
        var plan = new MutationPlan("test", hash, hash, [new("test", "transform_component", 9, [], [])]) { SchemaVersion = 6 };
        Assert.False(new Source2CompiledModelAdapter().CanRewrite(model, plan));
    }

    [Fact]
    public void IncompleteDirectionalIntentRejectsBeforeParsingOrLoadingANativeCodec()
    {
        byte[] bytes = [1, 2, 3]; var hash = ContentHash.Compute(bytes);
        var input = new ArtifactContent("models/test.vmdl_c", hash, bytes);
        var model = new ModelSnapshot(new(input.LogicalPath, hash, bytes.Length, []), []);
        var request = new TransformPlanningRequest(input, model, new()
        {
            OperationId = "invalid-directional",
            Version = 9,
            Transform = null!,
            Granularity = "directional_buffer_vertices",
            ExpectedMatchesByLod = new Dictionary<string, int> { ["0"] = 1 },
            ExpectedVerticesByLod = new Dictionary<string, int> { ["0"] = 3 },
            Selector = new() { Kind = "draw_call_ids", DrawCallIds = ["dc_000000000000000000000000"] },
        }, []);
        var error = Assert.Throws<S2ModKitException>(() => new Source2CompiledModelAdapter().PlanTransform(request));
        Assert.Equal("DIRECTIONAL_INTENT_INVALID", error.Error.Code);
    }
}
