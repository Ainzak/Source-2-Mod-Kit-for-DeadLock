using System.Text;
using System.Text.Json.Nodes;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed class CoordinatedIntentContractTests
{
    [Fact]
    public void EveryCommonFieldVariantHasExplicitTypedRoundTrip()
    {
        CoordinatedField[] fields = [Tilted(),
            new CoordinatedAxisRampField { Version = 1, CoordinateSpace = "model", Axis = "z", PinnedThrough = 0, FullFrom = 8, Pivot = new(), UniformScale = 1.5f },
            new CoordinatedEllipsoidField { Version = 1, CoordinateSpace = "model", Intent = new(new("ellipsoid", 1, "model", new(), new() { X = 8, Y = 8, Z = 8 }, 1f / 16), 2, new("ellipsoid_numeric", 1)) }];
        foreach (var field in fields)
        {
            var intent = Intent() with { Field = field };
            RecipeValidator.ValidateCoordinatedIntent(intent, 64);
            var json = JsonDefaults.Serialize(intent);
            Assert.Contains("\"kind\":", json, StringComparison.Ordinal);
            var reopened = Read(json);
            RecipeValidator.ValidateCoordinatedIntent(reopened, 64);
            Assert.Equal(json, JsonDefaults.Serialize(reopened));
        }
    }

    [Fact]
    public void IncompleteUnsortedDuplicateAndOverlappingMemberMappingsReject()
    {
        var intent = Intent();
        var first = intent.Members[0];
        var second = intent.Members[1];
        CoordinatedVisualTransform[] invalid = [
            intent with { Members = [first] },
            intent with { Members = [second, first] },
            intent with { Members = [first, first] },
            intent with { Members = [first, second with { Lods = [second.Lods[0]] }] },
            intent with { Members = [first, second with { Lods = [second.Lods[1], second.Lods[0]] }] },
            intent with { Members = [first with { Lods = [first.Lods[0] with { ExpectedVertices = 0 }, first.Lods[1]] }, second] },
            intent with { Members = [first, second with { Lods = [second.Lods[0] with { DrawCallIds = first.Lods[0].DrawCallIds }, second.Lods[1]] }] },
            intent with { Members = [first with { Lods = [first.Lods[0] with { DrawCallIds = ["ambiguous"] }, first.Lods[1]] }, second] },
            intent with { Members = [first with { Lods = [first.Lods[0] with { DrawCallIds = ["dc_ffffffffffffffffffffffff", first.Lods[0].DrawCallIds[0]] }, first.Lods[1]] }, second] }];
        foreach (var value in invalid)
            Assert.Throws<S2ModKitException>(() => RecipeValidator.ValidateCoordinatedIntent(value, 64));
    }

    [Fact]
    public void UnknownVariantsMissingFactsAndNullValuesCannotSubstituteDefaults()
    {
        var json = JsonDefaults.Serialize(Intent());
        foreach (var name in JsonNode.Parse(json)!["field"]!.AsObject().Select(property => property.Key).ToArray())
        {
            var node = JsonNode.Parse(json)!;
            node["field"]!.AsObject().Remove(name);
            Assert.Throws<S2ModKitException>(() => Read(node.ToJsonString()));
        }
        foreach (var path in new[] { "members", "field" })
        {
            var node = JsonNode.Parse(json)!;
            node[path] = null;
            Assert.Throws<S2ModKitException>(() => RecipeValidator.ValidateCoordinatedIntent(Read(node.ToJsonString()), 64));
        }
        Assert.Throws<S2ModKitException>(() => Read(json.Replace("tilted_ramp\"", "unknown_ramp\"", StringComparison.Ordinal)));
        var unknown = JsonNode.Parse(json)!;
        unknown["field"]!["rotation"] = new JsonObject();
        Assert.Throws<S2ModKitException>(() => Read(unknown.ToJsonString()));
    }

    [Fact]
    public void InvalidFieldVersionsSpacesAxesSignsPivotsAndLimitsReject()
    {
        var intent = Intent();
        var field = Tilted();
        CoordinatedField[] invalid = [field with { Version = 2 }, field with { CoordinateSpace = "bone" },
            field with { FirstAxis = "z", SecondAxis = "x" }, field with { FirstAxis = "x", SecondAxis = "x" },
            field with { FirstSign = 0 }, field with { SecondSign = 2 }, field with { NumericalPolicy = new("axis_ramp_numeric", 1) },
            field with { Pivot = new() { X = float.NaN } }, field with { FullFrom = float.PositiveInfinity },
            field with { PinnedThrough = 8, FullFrom = 8 }, field with { UniformScale = 1 }, field with { UniformScale = 2.01f }];
        foreach (var value in invalid)
            Assert.Throws<S2ModKitException>(() => RecipeValidator.ValidateCoordinatedIntent(intent with { Field = value }, 64));
        foreach (var limit in new[] { 0, -1, 65, float.NaN })
            Assert.Throws<S2ModKitException>(() => RecipeValidator.ValidateCoordinatedIntent(intent, limit));
    }

    private static CoordinatedTiltedRampField Tilted() => new()
    {
        Version = 1,
        CoordinateSpace = "model",
        FirstAxis = "x",
        FirstSign = 1,
        SecondAxis = "z",
        SecondSign = 1,
        PinnedThrough = 0,
        FullFrom = 8,
        Pivot = new(),
        UniformScale = 1.5f,
        NumericalPolicy = new("tilted_ramp_numeric", 1),
    };
    private static CoordinatedVisualTransform Intent() => new([
        new("member-a", [new(0, ["dc_000000000000000000000001"], 4), new(1, ["dc_000000000000000000000002"], 3)]),
        new("member-b", [new(0, ["dc_000000000000000000000003"], 5), new(1, ["dc_000000000000000000000004"], 4)])], Tilted());
    private static CoordinatedVisualTransform Read(string json) =>
        JsonDefaults.Deserialize<CoordinatedVisualTransform>(Encoding.UTF8.GetBytes(json), "Coordinated intent");
}
