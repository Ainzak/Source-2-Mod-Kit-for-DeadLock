using System.Text;
using System.Text.Json.Nodes;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using S2ModKit.Infrastructure;

namespace S2ModKit.Application.Tests;

public sealed class PositionReturnValidatorTests
{
    private static readonly ContentHash Hash = ContentHash.Compute("source"u8);
    internal static (PositionAuthoringSource Source, PositionAuthoringSelection Selection, PositionAuthoringBundle Bundle, byte[] Bytes, PositionAuthoringDelta Delta) Fixture()
    {
        var p = ExperimentalVisualContractTests.PairedPlan().Operations[0].PairedTransformTarget!;
        Point3[] positions = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)];
        var facts = p.ContextBuffers.First(b => b.Selected) with { Lod = 0, VertexCount = 3, DecodedBufferHash = Hash };
        CoordinatedMember[] members = [new("body", [new(0, ["dc_0123456789abcdef01234567"], 3)])];
        var buffer = new PositionSourceBuffer("body", facts, positions, [0, 1, 2], [42, 42, 42], Enumerable.Repeat(new TangentFrame(new(0, 0, 1), new(1, 0, 0), 1), 3).ToArray(), [], []);
        var source = new PositionAuthoringSource(Hash, members, [buffer], p.ProceduralInputs, p.SourceBlocks);
        var selection = new PositionAuthoringSelection(1, Hash.Value, members, 2, [new("body", 0, Hash.Value, [0])], new("keep_positions_and_frames", 1),
            new("preserve_unverified", 1), new("preserve_serialized_inputs_unverified", 1), new("preserve_source_coincidence", 1), new("reject", 1), new("reject", 1));
        var bundle = PositionReturnValidator.CreateBundle(source, selection, Hash); var bytes = PositionAuthoringJson.Canonical(bundle);
        var delta = new PositionAuthoringDelta(1, "geometry_authoring_delta", new("author_positions", 1), Hash.Value, ContentHash.Compute(bytes).Value, "synthetic", "S2ModKit_Edit",
            [new(bundle.Members[0].MemberId, positions.Select(PositionAuthoringJson.Words).ToArray())]);
        return (source, selection, bundle, bytes, delta);
    }
    private static PositionValidationResult Validate(PositionAuthoringSource source, PositionAuthoringSelection selection, byte[] bundle, PositionAuthoringDelta delta, uint encoded = 42) =>
        PositionReturnValidator.Validate(source, selection, bundle, ContentHash.Compute(bundle), PositionAuthoringJson.Canonical(delta), _ => encoded);

    [Fact]
    public void IndependentSourceNoOpAndFiniteFlatEditPassWithoutModelBytes()
    {
        var f = Fixture(); var result = Validate(f.Source, f.Selection, f.Bytes, f.Delta);
        Assert.False(result.ModelBytesBuilt); Assert.Empty(result.Buffers[0].FrameClosure); Assert.Equal(0, result.Buffers[0].ChangedPositions);
        var positions = f.Delta.Members[0].Positions.Select(p => p.ToArray()).ToArray(); positions[1] = PositionAuthoringJson.Words(new(1.2f, 0, 0));
        var edit = f.Delta with { Members = [f.Delta.Members[0] with { Positions = positions }] };
        Assert.Equal(1, Validate(f.Source, f.Selection, f.Bytes, edit).Buffers[0].ChangedPositions);
    }

    [Theory]
    [InlineData("inputHash")]
    [InlineData("targetKey")]
    [InlineData("bundleSha256")]
    public void StaleDeltaIdentityRejects(string property)
    {
        var f = Fixture(); var json = JsonNode.Parse(PositionAuthoringJson.Canonical(f.Delta))!; json[property] = "wrong";
        Assert.Throws<S2ModKitException>(() => PositionReturnValidator.Validate(f.Source, f.Selection, f.Bytes, ContentHash.Compute(f.Bytes), PositionAuthoringJson.Canonical(json), _ => 42));
    }

    [Fact]
    public void TamperedSourceBasisTriangleOrderProtectionAndUnknownMemberReject()
    {
        var f = Fixture();
        foreach (var name in new[] { "positions", "triangles", "protectedIds", "memberId" })
        {
            var json = JsonNode.Parse(f.Bytes)!; var member = json["members"]![0]!;
            if (name == "positions") member[name]![1]![0] = "00000040";
            else if (name == "triangles") member[name] = new JsonArray(0, 2, 1);
            else if (name == "protectedIds") member[name] = new JsonArray(1);
            else member[name] = "unknown";
            var bytes = PositionAuthoringJson.Canonical(json);
            Assert.Throws<S2ModKitException>(() => PositionReturnValidator.Validate(f.Source, f.Selection, bytes, ContentHash.Compute(bytes), PositionAuthoringJson.Canonical(f.Delta with { BundleSha256 = ContentHash.Compute(bytes).Value }), _ => 42));
        }
        Assert.Throws<S2ModKitException>(() => Validate(f.Source, f.Selection, f.Bytes, f.Delta with { Members = [] }));
    }

    [Fact]
    public void ExplicitStationaryBoundaryPermissionDoesNotWaiveProceduralOrMovingProtection()
    {
        var f = Fixture(); var positions = f.Delta.Members[0].Positions.Select(p => p.ToArray()).ToArray(); positions[1] = PositionAuthoringJson.Words(new(1, 0, .1f));
        var delta = f.Delta with { Members = [f.Delta.Members[0] with { Positions = positions }] };
        Assert.Throws<S2ModKitException>(() => Validate(f.Source, f.Selection, f.Bytes, delta, 43));
        var selection = f.Selection with { BoundaryFramePolicy = new("keep_positions_allow_source_derived_boundary_frames", 1, [new("body", 0, Hash.Value, [0])]) };
        Assert.Equal([0], Validate(f.Source, selection, f.Bytes, delta, 43).Buffers[0].BoundaryFrameChanges);
        var procedural = f.Source with { Buffers = [f.Source.Buffers[0] with { ProceduralIds = [0] }] };
        Assert.Throws<S2ModKitException>(() => Validate(procedural, selection, f.Bytes, delta, 43));
        positions[0] = PositionAuthoringJson.Words(new(0, 0, .1f));
        Assert.Throws<S2ModKitException>(() => Validate(f.Source, selection, f.Bytes, delta, 43));
    }

    [Fact]
    public void ContextCoincidenceSplitsAndNewMergesReject()
    {
        var f = Fixture(); var context = f.Source.Buffers[0] with { SelectedMemberId = null, Facts = f.Source.Buffers[0].Facts with { Selected = false, VertexBufferOrdinal = 1 }, Frames = [], FrameWords = [] };
        var source = f.Source with { Buffers = [f.Source.Buffers[0], context] };
        var bundle = PositionReturnValidator.CreateBundle(source, f.Selection, Hash); var bytes = PositionAuthoringJson.Canonical(bundle);
        var positions = f.Delta.Members[0].Positions.Select(p => p.ToArray()).ToArray(); positions[1] = PositionAuthoringJson.Words(new(1.1f, 0, 0));
        var delta = f.Delta with { BundleSha256 = ContentHash.Compute(bytes).Value, Members = [f.Delta.Members[0] with { Positions = positions }] };
        Assert.Throws<S2ModKitException>(() => Validate(source, f.Selection, bytes, delta));
        positions[1] = positions[2]; Assert.Throws<S2ModKitException>(() => Validate(f.Source, f.Selection, f.Bytes, f.Delta with { Members = [f.Delta.Members[0] with { Positions = positions }] }));
    }

    [Fact]
    public void ProtectedSignedZeroNonfiniteCapsAndMalformedJsonFailClosed()
    {
        var f = Fixture(); var positions = f.Delta.Members[0].Positions.Select(p => p.ToArray()).ToArray(); positions[0][0] = "00000080";
        Assert.Throws<S2ModKitException>(() => Validate(f.Source, f.Selection, f.Bytes, f.Delta with { Members = [f.Delta.Members[0] with { Positions = positions }] }));
        positions[0][0] = "0000807f"; Assert.Throws<S2ModKitException>(() => Validate(f.Source, f.Selection, f.Bytes, f.Delta with { Members = [f.Delta.Members[0] with { Positions = positions }] }));
        positions[0] = f.Delta.Members[0].Positions[0]; positions[1] = PositionAuthoringJson.Words(new(5, 0, 0));
        Assert.Throws<S2ModKitException>(() => Validate(f.Source, f.Selection, f.Bytes, f.Delta with { Members = [f.Delta.Members[0] with { Positions = positions }] }));
        var json = Encoding.UTF8.GetString(PositionAuthoringJson.Canonical(f.Selection));
        Assert.Throws<S2ModKitException>(() => PositionReturnValidator.ReadSelection(Encoding.UTF8.GetBytes(json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal))));
        Assert.Throws<S2ModKitException>(() => PositionAuthoringJson.Read<PositionAuthoringDelta>(Encoding.UTF8.GetBytes("{}")));
        Assert.Throws<S2ModKitException>(() => PositionAuthoringJson.Read<PositionAuthoringDelta>(new byte[20], budget: 16));
    }

    [Fact]
    public void CompleteLodMapsAndExplicitPoliciesCannotBeOmittedOrWeakened()
    {
        var f = Fixture();
        var selection = f.Selection with { Members = [f.Selection.Members[0] with { Lods = [f.Selection.Members[0].Lods[0], new(1, ["dc_1123456789abcdef01234567"], 3)] }] };
        Assert.Throws<S2ModKitException>(() => PositionReturnValidator.ReadSelection(PositionAuthoringJson.Canonical(selection)));
        var json = JsonNode.Parse(PositionAuthoringJson.Canonical(f.Selection))!;
        json["boundaryFramePolicy"]!["allowedBoundary"] = null;
        Assert.Throws<S2ModKitException>(() => PositionReturnValidator.ReadSelection(PositionAuthoringJson.Canonical(json)));
        foreach (var name in new[] { "runtimeMetadataPolicy", "proceduralInputPolicy", "sourceTrianglePolicy", "zeroBoneBoxPolicy", "zeroRenderSpherePolicy" })
        {
            json = JsonNode.Parse(PositionAuthoringJson.Canonical(f.Selection))!; json.AsObject().Remove(name);
            Assert.Throws<S2ModKitException>(() => PositionReturnValidator.ReadSelection(PositionAuthoringJson.Canonical(json)));
            json[name] = new JsonObject { ["kind"] = "ignore", ["version"] = 1 };
            Assert.Throws<S2ModKitException>(() => PositionReturnValidator.ReadSelection(PositionAuthoringJson.Canonical(json)));
        }
        var stale = f.Selection with { Protection = [f.Selection.Protection[0] with { SourceDecodedHash = ContentHash.Compute("stale"u8).Value }] };
        Assert.Throws<S2ModKitException>(() => Validate(f.Source, stale, f.Bytes, f.Delta));
    }

    [Fact]
    public void AllLodReturnRequiresOriginalOrderAndEveryRecord()
    {
        var f = Fixture(); var second = new CoordinatedMemberLod(1, ["dc_1123456789abcdef01234567"], 3);
        var members = new[] { f.Selection.Members[0] with { Lods = [f.Selection.Members[0].Lods[0], second] } };
        var selection = f.Selection with { Members = members, Protection = [f.Selection.Protection[0], f.Selection.Protection[0] with { Lod = 1 }] };
        var source = f.Source with { Members = members, Buffers = [f.Source.Buffers[0], f.Source.Buffers[0] with { Facts = f.Source.Buffers[0].Facts with { Lod = 1 } }] };
        var bytes = PositionAuthoringJson.Canonical(PositionReturnValidator.CreateBundle(source, selection, Hash));
        var delta = f.Delta with { BundleSha256 = ContentHash.Compute(bytes).Value, Members = [f.Delta.Members[0], f.Delta.Members[0] with { MemberId = PositionAuthoringJson.BufferId(1, source.Buffers[1].Facts.MeshOrdinal, source.Buffers[1].Facts.VertexBufferOrdinal) }] };
        Assert.Equal(2, Validate(source, selection, bytes, delta).Buffers.Count);
        Assert.Throws<S2ModKitException>(() => Validate(source, selection, bytes, delta with { Members = [delta.Members[0]] }));
        Assert.Throws<S2ModKitException>(() => Validate(source, selection, bytes, delta with { Members = delta.Members.Reverse().ToArray() }));
        Assert.Throws<S2ModKitException>(() => Validate(source, selection, bytes, delta with { Members = [delta.Members[0] with { Positions = delta.Members[0].Positions.Take(2).ToArray() }, delta.Members[1]] }));
    }

    [Fact]
    public async Task CancelledExportDoesNotPublishPartialDirectory()
    {
        var f = Fixture(); var parent = Path.Combine(Environment.GetEnvironmentVariable("S2MODKIT_TEST_TEMP_ROOT") ?? Path.GetTempPath(), "s2mod-authoring-cancel-" + Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AtomicPositionAuthoringPublisher().PublishAsync(Path.Combine(parent, "output"), f.Bytes, PositionAuthoringJson.Canonical(f.Selection), "{}"u8.ToArray(), cancellation.Token));
            Assert.Empty(Directory.GetFileSystemEntries(parent));
        }
        finally { if (Directory.Exists(parent)) Directory.Delete(parent, true); }
    }

    [Fact]
    public async Task ExportPublishesAllArtifactsTogetherAndRefusesExistingOutputs()
    {
        var f = Fixture(); var root = Path.Combine(Environment.GetEnvironmentVariable("S2MODKIT_TEST_TEMP_ROOT") ?? Path.GetTempPath(), "s2mod-authoring-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var publisher = new AtomicPositionAuthoringPublisher();
            var result = await publisher.PublishAsync(root, f.Bytes, PositionAuthoringJson.Canonical(f.Selection), "{}"u8.ToArray(), TestContext.Current.CancellationToken);
            Assert.Equal(ContentHash.Compute(File.ReadAllBytes(Path.Combine(root, "bundle.json"))), result.BundleSha256);
            Assert.Equal(f.Selection, PositionReturnValidator.ReadSelection(File.ReadAllBytes(Path.Combine(root, "selection.json"))) with { Members = f.Selection.Members, Protection = f.Selection.Protection });
            await Assert.ThrowsAsync<S2ModKitException>(() => publisher.PublishAsync(root, f.Bytes, "{}"u8.ToArray(), "{}"u8.ToArray(), TestContext.Current.CancellationToken));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
