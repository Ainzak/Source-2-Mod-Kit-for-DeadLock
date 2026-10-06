using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed partial class SyntheticPipelineTests
{
    private static MeshGeometrySnapshot SyntheticDirectionalGeometry(int lod, int mesh, PlannedDirectionalTransformTarget target)
    {
        var rows = target.ContextBuffers.Where(c => c.Lod == lod && c.MeshOrdinal == mesh).ToArray();
        var layout = target.Buffers.First(b => b.Lod == lod && b.MeshOrdinal == mesh);
        return new("ready", "synthetic directional lifecycle",
            rows.Select(c => new VertexBufferSnapshot(c.VertexBufferOrdinal, c.VertexResourceBlockIndex, c.VertexCount, layout.PositionLayout.Stride,
                target.SourceBlocks.Single(s => s.Index == c.VertexResourceBlockIndex).InputHash, c.DecodedBufferHash, layout.PositionLayout)).ToArray(),
            rows.Select(c => new IndexBufferSnapshot(c.VertexBufferOrdinal, c.IndexResourceBlockIndex, 3, 2,
                target.SourceBlocks.Single(s => s.Index == c.IndexResourceBlockIndex).InputHash, c.IndexHash)).ToArray(), [], layout.Codec);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    public async Task DirectionalBuildAndReverifyRequireCompleteIndependentObservedEvidence(int members, int lods)
    {
        var adapter = new DirectionalLifecycleAdapter(members, lods); var workspace = new MemoryWorkspace(adapter.Input);
        var app = DirectionalApplication(workspace, adapter); var token = TestContext.Current.CancellationToken;
        var built = await app.BuildAsync("memory", adapter.Recipe, token);
        Assert.Single(workspace.Builds); Assert.Equal(11, built.Evidence.SchemaVersion);
        Assert.NotNull(built.Evidence.Output); Assert.NotNull(built.Evidence.Operations[0].DirectionalTransform!.Observed);
        DirectionalContractValidator.ValidateEvidence(built.Evidence);
        var verified = await app.VerifyAsync("memory", built.Build.BuildId, token);
        Assert.Equal("passed", verified.Evidence.Status); Assert.Equal(2, adapter.AuditCount);
        Assert.Equal("untested", verified.Evidence.Boundaries.Single(b => b.Name == "garment_pose_fit").Status);
    }

    [Theory]
    [InlineData("throws")]
    [InlineData("failed")]
    [InlineData("missing_boundary")]
    [InlineData("skipped_mandatory")]
    [InlineData("risk_passed")]
    [InlineData("missing_buffer")]
    [InlineData("forged_weight")]
    [InlineData("missing_protection")]
    [InlineData("missing_context")]
    [InlineData("missing_closure")]
    [InlineData("missing_box")]
    [InlineData("missing_preservation")]
    public async Task DirectionalPublicationRejectsFailedIncompleteOrFabricatedObservations(string failure)
    {
        var adapter = new DirectionalLifecycleAdapter(2, 2) { AuditFailure = failure }; var workspace = new MemoryWorkspace(adapter.Input);
        await Assert.ThrowsAsync<S2ModKitException>(() => DirectionalApplication(workspace, adapter).BuildAsync("memory", adapter.Recipe, TestContext.Current.CancellationToken));
        Assert.Empty(workspace.Builds); Assert.Equal(1, adapter.AuditCount);
        Assert.Equal(ContentHash.Compute(adapter.Input.Bytes.Span), adapter.Input.ContentHash);
    }

    private sealed class DirectionalLifecycleAdapter : DirectionalPlanningAdapter, IDirectionalTransformVerifier
    {
        private readonly byte[] candidateBytes = "synthetic-directional-output"u8.ToArray();
        private readonly ModelSnapshot after;
        public DirectionalLifecycleAdapter(int members, int lods) : base(members, lods)
        {
            after = snapshot with
            {
                Artifact = snapshot.Artifact with
                {
                    ContentHash = ContentHash.Compute(candidateBytes),
                    Size = candidateBytes.Length,
                    Blocks = snapshot.Artifact.Blocks.Select(b => target.Buffers.FirstOrDefault(v => v.VertexResourceBlockIndex == b.Index) is { } v
                        ? b with { ContentHash = v.ExpectedDecodedVertexBufferHash } : b).ToArray()
                },
                Lods = snapshot.Lods.Select(l => l with
                {
                    Meshes = l.Meshes.Select(m => m with
                    {
                        Geometry = m.Geometry! with
                        {
                            VertexBuffers = m.Geometry.VertexBuffers.Select(v => target.Buffers.FirstOrDefault(b => b.Lod == l.Level && b.MeshOrdinal == m.MeshOrdinal && b.VertexBufferOrdinal == v.Ordinal) is { } b
                            ? v with { EncodedHash = b.ExpectedDecodedVertexBufferHash, DecodedHash = b.ExpectedDecodedVertexBufferHash } : v).ToArray()
                        }
                    }).ToArray()
                }).ToArray()
            };
        }
        public string? AuditFailure { get; init; }
        public int AuditCount { get; private set; }
        public override Task<ModelSnapshot> InspectAsync(ArtifactContent artifact, CancellationToken cancellationToken = default) => Task.FromResult(artifact.ContentHash == Input.ContentHash ? snapshot : after);
        public override Task<RewriteCandidate> RewriteAsync(ArtifactContent input, ModelSnapshot model, MutationPlan plan, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RewriteCandidate(input.LogicalPath, candidateBytes, after));
        public Task<DirectionalTransformVerification> VerifyDirectionalTransformAsync(ArtifactContent input, ArtifactContent output, MutationPlan plan, CancellationToken cancellationToken = default)
        {
            AuditCount++;
            if (AuditFailure == "throws") throw Errors.Verification("DIRECTIONAL_RESULT_DRIFT", "Injected resource audit failure.", "Reject.");
            var boundaries = DirectionalContractValidator.PlannedBoundaries().Where(b => b.Name != "runtime")
                .Select(b => b.Status == "not_applicable" ? b with { Status = AuditFailure == "failed" ? "failed" : "passed" } : b)
                .Where(b => AuditFailure != "missing_boundary" || b.Name != "directional_frames")
                .Select(b => AuditFailure == "skipped_mandatory" && b.Name == "directional_frames" ? b with { Status = "skipped" } : b)
                .Select(b => AuditFailure == "risk_passed" && b.Name == "sphere_containment" ? b with { Status = "passed" } : b).ToArray();
            var observations = new DirectionalObservation(target.Buffers.Select(b => new CoordinatedBufferObservation(b.MemberId, b.Lod, b.MeshOrdinal, b.VertexBufferOrdinal,
                b.ExpectedPositionHash, b.ExpectedPackedFrameHash, b.ExpectedDecodedVertexBufferHash, b.MaskHash,
                AuditFailure == "forged_weight" ? ContentHash.Compute("forged"u8) : b.WeightHash, b.MaximumDisplacement)).ToArray(),
                target.WordAudits, target.Protection, target.ContextBuffers, target.Coincidences, target.BoxClosures, target.Certificate);
            observations = AuditFailure switch
            {
                "missing_buffer" => observations with { Buffers = [] },
                "missing_protection" => observations with { Protection = target.Protection with { Union = [] } },
                "missing_context" => observations with { ContextBuffers = [] },
                "missing_closure" => observations with { BoxClosures = [] },
                _ => observations,
            };
            return Task.FromResult(new DirectionalTransformVerification(boundaries, observations,
                AuditFailure == "missing_box" ? [] : target.BoxTargets.Select(b => new ExperimentalBoxEvidence(b, b.ExpectedWords, "passed")).ToArray(),
                AuditFailure == "missing_preservation" ? [] : target.PreservationTargets.Select(p => new ExperimentalPreservationEvidence(p, p.SourcePayloadHash, p.OriginalWords, "passed")).ToArray()));
        }
    }
}
