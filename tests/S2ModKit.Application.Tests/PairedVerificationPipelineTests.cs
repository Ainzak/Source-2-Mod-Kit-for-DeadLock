using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Infrastructure;

namespace S2ModKit.Application.Tests;

public sealed partial class SyntheticPipelineTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    public async Task PairedBuildAndReverifyPublishOnlyCompleteIndependentObservations(int members, int lods)
    {
        var adapter = new PairedLifecycleAdapter(members, lods); var workspace = new MemoryWorkspace(adapter.Input);
        var app = DirectionalApplication(workspace, adapter); var token = TestContext.Current.CancellationToken;
        var planned = await app.PlanAsync("memory", adapter.PairedRecipe, token);
        Assert.Null(planned.Evidence.Output); Assert.Null(planned.Evidence.Operations[0].PairedTransform!.Observed);
        var built = await app.BuildAsync("memory", adapter.PairedRecipe, token);
        Assert.Single(workspace.Builds); Assert.Equal(12, built.Evidence.SchemaVersion);
        Assert.NotNull(built.Evidence.Output); Assert.NotNull(built.Evidence.Operations[0].PairedTransform!.Observed);
        PairedContractValidator.ValidateEvidence(built.Evidence);
        var json = JsonDefaults.Serialize(built.Evidence);
        Assert.DoesNotContain('\n', json);
        PairedContractValidator.ValidateEvidence(JsonDefaults.Deserialize<EvidenceReport>(System.Text.Encoding.UTF8.GetBytes(json), "paired evidence"));
        var verified = await app.VerifyAsync("memory", built.Build.BuildId, token);
        PairedContractValidator.ValidateEvidence(verified.Evidence); Assert.Equal(2, adapter.AuditCount);
        Assert.Equal("untested", verified.Evidence.Boundaries.Single(b => b.Name == "procedural_simulation").Status);
    }

    [Theory]
    [InlineData("throws")]
    [InlineData("failed")]
    [InlineData("missing_boundary")]
    [InlineData("skipped_mandatory")]
    [InlineData("risk_passed")]
    [InlineData("missing_buffer")]
    [InlineData("wrong_dispatch")]
    [InlineData("missing_partner")]
    [InlineData("missing_protection")]
    [InlineData("missing_context")]
    [InlineData("missing_closure")]
    [InlineData("missing_box")]
    [InlineData("missing_preservation")]
    [InlineData("missing_procedural")]
    [InlineData("missing_consumer")]
    [InlineData("wrong_partition")]
    public async Task PairedPublicationRejectsIncompleteFailedOrForgedIndependentEvidence(string failure)
    {
        var adapter = new PairedLifecycleAdapter(2, 2) { AuditFailure = failure }; var workspace = new MemoryWorkspace(adapter.Input);
        await Assert.ThrowsAsync<S2ModKitException>(() => DirectionalApplication(workspace, adapter).BuildAsync("memory", adapter.PairedRecipe, TestContext.Current.CancellationToken));
        Assert.Empty(workspace.Builds); Assert.Equal(1, adapter.AuditCount);
        Assert.Equal(ContentHash.Compute(adapter.Input.Bytes.Span), adapter.Input.ContentHash);
    }

    [Fact]
    public async Task PairedReverifyRejectsIncompleteLaterObservationsWithoutSavingEvidence()
    {
        var adapter = new PairedLifecycleAdapter(1, 1); var workspace = new MemoryWorkspace(adapter.Input);
        var app = DirectionalApplication(workspace, adapter); var token = TestContext.Current.CancellationToken;
        var built = await app.BuildAsync("memory", adapter.PairedRecipe, token);
        adapter.AuditFailure = "missing_consumer";
        await Assert.ThrowsAsync<S2ModKitException>(() => app.VerifyAsync("memory", built.Build.BuildId, token));
        Assert.Single(workspace.Builds); Assert.Equal(0, workspace.EvidenceWriteCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringIndependentAuditDoesNotPublishOrSave(bool reverify)
    {
        var adapter = new PairedLifecycleAdapter(1, 1); var workspace = new MemoryWorkspace(adapter.Input);
        var app = DirectionalApplication(workspace, adapter); var token = TestContext.Current.CancellationToken;
        var built = reverify ? await app.BuildAsync("memory", adapter.PairedRecipe, token) : null;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        adapter.CancelDuringAudit = cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reverify
            ? app.VerifyAsync("memory", built!.Build.BuildId, cancel.Token)
            : (Task)app.BuildAsync("memory", adapter.PairedRecipe, cancel.Token));
        if (reverify) Assert.Single(workspace.Builds); else Assert.Empty(workspace.Builds);
        Assert.Equal(0, workspace.EvidenceWriteCount);
    }

    [Fact]
    public async Task CompleteLargePairedEvidencePublishesAndReopensWhileOtherFormatsRetainTheirLimit()
    {
        var adapter = new PairedLifecycleAdapter(1, 1); var memory = new MemoryWorkspace(adapter.Input);
        var token = TestContext.Current.CancellationToken;
        var built = await DirectionalApplication(memory, adapter).BuildAsync("memory", adapter.PairedRecipe, token);
        var large = built.Evidence with { Warnings = [new string('x', 17 * 1024 * 1024)] };
        var json = JsonDefaults.Serialize(large);
        var (build, content) = await memory.LoadBuildAsync("memory", built.Build.BuildId, token);
        var plan = await memory.LoadPlanAsync("memory", build.PlanFingerprint, token);
        var candidate = new RewriteCandidate(content.LogicalPath, content.Bytes, await adapter.InspectAsync(content, token));
        var publication = new BuildPublication(build.BuildId, plan, candidate, json, "complete observations in JSON");
        var root = Path.Combine(Path.GetTempPath(), "s2mod-paired-evidence", Guid.NewGuid().ToString("N"));
        var workspace = new FileSystemProjectWorkspace();
        try
        {
            var first = await workspace.PublishBuildAsync(root, publication, token);
            var second = await workspace.PublishBuildAsync(root, publication, token);
            Assert.Equal(first, second);
            Assert.Equal(content.ContentHash, (await workspace.LoadBuildAsync(root, build.BuildId, token)).Content.ContentHash);
            await workspace.SaveEvidenceAsync(root, "reverify", json, "reverify", token);
            await Assert.ThrowsAsync<S2ModKitException>(() => workspace.SaveEvidenceAsync(root, "oversized-markdown", json, new string('x', 17 * 1024 * 1024), token));
            await Assert.ThrowsAsync<S2ModKitException>(() => workspace.PublishBuildAsync(root, publication with
            {
                BuildId = "legacy-too-large",
                EvidenceJson = "{\"schemaVersion\":6,\"padding\":\"" + new string('x', 17 * 1024 * 1024) + "\"}"
            }, token));
            Assert.False(Directory.Exists(Path.Combine(root, "builds", "legacy-too-large")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "temp")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private sealed class PairedLifecycleAdapter : PairedPlanningAdapter, IPairedTransformVerifier
    {
        private readonly byte[] candidateBytes = "synthetic-paired-output"u8.ToArray();
        private readonly ModelSnapshot after;
        public PairedLifecycleAdapter(int members, int lods) : base(members, lods)
        {
            after = snapshot with
            {
                Artifact = snapshot.Artifact with
                {
                    ContentHash = ContentHash.Compute(candidateBytes),
                    Size = candidateBytes.Length,
                    Blocks = snapshot.Artifact.Blocks.Select(b => paired.Buffers.FirstOrDefault(v => v.VertexResourceBlockIndex == b.Index) is { } v
                        ? b with { ContentHash = v.ExpectedDecodedVertexBufferHash } : b).ToArray()
                },
                Lods = snapshot.Lods.Select(l => l with
                {
                    Meshes = l.Meshes.Select(m => m with
                    {
                        Geometry = m.Geometry! with
                        {
                            VertexBuffers = m.Geometry.VertexBuffers.Select(v => paired.Buffers.FirstOrDefault(b => b.Lod == l.Level && b.MeshOrdinal == m.MeshOrdinal && b.VertexBufferOrdinal == v.Ordinal) is { } b
                                ? v with { EncodedHash = b.ExpectedDecodedVertexBufferHash, DecodedHash = b.ExpectedDecodedVertexBufferHash } : v).ToArray()
                        }
                    }).ToArray()
                }).ToArray()
            };
        }
        public string? AuditFailure { get; set; }
        public CancellationTokenSource? CancelDuringAudit { get; set; }
        public int AuditCount { get; private set; }
        public override Task<ModelSnapshot> InspectAsync(ArtifactContent artifact, CancellationToken cancellationToken = default) => Task.FromResult(artifact.ContentHash == Input.ContentHash ? snapshot : after);
        public override Task<RewriteCandidate> RewriteAsync(ArtifactContent input, ModelSnapshot model, MutationPlan plan, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RewriteCandidate(input.LogicalPath, candidateBytes, after));
        public Task<PairedTransformVerification> VerifyPairedTransformAsync(ArtifactContent input, ArtifactContent output, MutationPlan plan, CancellationToken cancellationToken = default)
        {
            AuditCount++;
            CancelDuringAudit?.Cancel();
            if (AuditFailure == "throws") throw Errors.Verification("PAIRED_RESULT_DRIFT", "Injected independent audit failure.", "Reject.");
            var boundaries = PairedContractValidator.PlannedBoundaries().Where(b => b.Name != "runtime")
                .Select(b => b.Status == "not_applicable" ? b with { Status = AuditFailure == "failed" ? "failed" : "passed" } : b)
                .Where(b => AuditFailure != "missing_boundary" || b.Name != "paired_frames")
                .Select(b => AuditFailure == "skipped_mandatory" && b.Name == "paired_frames" ? b with { Status = "skipped" } : b)
                .Select(b => AuditFailure == "risk_passed" && b.Name == "procedural_simulation" ? b with { Status = "passed" } : b).ToArray();
            var observations = new PairedDirectionalObservation(paired.Buffers.Select(b => new CoordinatedBufferObservation(b.MemberId, b.Lod, b.MeshOrdinal, b.VertexBufferOrdinal,
                b.ExpectedPositionHash, b.ExpectedPackedFrameHash, b.ExpectedDecodedVertexBufferHash, b.MaskHash, b.WeightHash, b.MaximumDisplacement)).ToArray(),
                paired.Dispatch, paired.WordAudits, paired.Protection, paired.ContextBuffers, paired.Coincidences, paired.BoxClosures, paired.Fields, paired.Separation,
                paired.SourceTriangles, paired.ProceduralInputs);
            observations = AuditFailure switch
            {
                "missing_buffer" => observations with { Buffers = [] },
                "wrong_dispatch" => observations with { Dispatch = [] },
                "missing_partner" => observations with { Fields = paired.Fields.Take(1).ToArray() },
                "missing_protection" => observations with { Protection = paired.Protection with { Union = [] } },
                "missing_context" => observations with { ContextBuffers = [] },
                "missing_closure" => observations with { BoxClosures = [] },
                "missing_procedural" => observations with { ProceduralInputs = paired.ProceduralInputs with { Union = [] } },
                "missing_consumer" => observations with { ProceduralInputs = paired.ProceduralInputs with { ConsumerFamilies = [] } },
                "wrong_partition" => observations with { SourceTriangles = [] },
                _ => observations,
            };
            return Task.FromResult(new PairedTransformVerification(boundaries, observations,
                AuditFailure == "missing_box" ? [] : paired.BoxTargets.Select(b => new ExperimentalBoxEvidence(b, b.ExpectedWords, "passed")).ToArray(),
                AuditFailure == "missing_preservation" ? [] : paired.PreservationTargets.Select(p => new ExperimentalPreservationEvidence(p, p.SourcePayloadHash, p.OriginalWords, "passed")).ToArray()));
        }
    }
}
