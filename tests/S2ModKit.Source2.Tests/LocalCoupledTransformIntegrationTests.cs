using System.Buffers.Binary;
using S2ModKit.Adapters.Source2;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Source2.Tests;

public sealed class LocalCoupledTransformIntegrationTests
{
    [Fact]
    public async Task ConfiguredCoupledModelPlansRewritesAndReopensWithoutChangingInput()
    {
        var modelVariable = Environment.GetEnvironmentVariable("S2MODKIT_TEST_COUPLED_MODEL");
        var logicalPathVariable = Environment.GetEnvironmentVariable("S2MODKIT_TEST_COUPLED_LOGICAL_PATH");
        if (string.IsNullOrWhiteSpace(modelVariable) || string.IsNullOrWhiteSpace(logicalPathVariable))
        {
            Assert.Skip("Set S2MODKIT_TEST_COUPLED_MODEL and S2MODKIT_TEST_COUPLED_LOGICAL_PATH to enable the proprietary coupled-transform check.");
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var modelPath = Path.GetFullPath(modelVariable);
        Assert.True(File.Exists(modelPath));
        var before = await File.ReadAllBytesAsync(modelPath, cancellationToken);
        var inputHash = ContentHash.Compute(before);
        var input = new ArtifactContent(StableIdentity.NormalizePath(logicalPathVariable), inputHash, before);
        var adapter = new Source2CompiledModelAdapter();
        var snapshot = await adapter.InspectAsync(input, cancellationToken);
        var bounds = Assert.Single(Assert.Single(snapshot.Lods).Meshes).Geometry!.DrawCalls.Single().Bounds;
        var pivot = new TransformVector3
        {
            X = (bounds.Min.X + bounds.Max.X) / 2f,
            Y = (bounds.Min.Y + bounds.Max.Y) / 2f,
            Z = (bounds.Min.Z + bounds.Max.Z) / 2f,
        };

        var plan = adapter.PlanCoupledTransform(input, snapshot, pivot, 2f, 256f, 256f);
        var candidate = adapter.RewriteCoupledTransform(input, snapshot, plan);
        var after = await File.ReadAllBytesAsync(modelPath, cancellationToken);

        Assert.NotEqual(inputHash, ContentHash.Compute(candidate.Content.Span));
        Assert.Equal(ContentHash.Compute(candidate.Content.Span), candidate.Snapshot.Artifact.ContentHash);
        Assert.Equal(inputHash, ContentHash.Compute(after));
        Assert.Equal(["MDAT", "MBUF", "PHYS"], plan.TargetBlocks.Select(block => block.Type));

        var sourceEnvelope = ResourceEnvelopeReader.Read(before);
        var candidateEnvelope = ResourceEnvelopeReader.Read(candidate.Content);
        foreach (var sourceBlock in sourceEnvelope.Blocks.Where(block => block.Type is "MDAT" or "PHYS"))
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(sourceBlock.Payload.Span) != 0x4B563304)
            {
                continue;
            }

            // Uniform transforms preserve container topology. Inspect encoded counts directly:
            // VRF's semantic reader ignores them and cannot detect the serializer's zero counts.
            var rewritten = candidateEnvelope.Blocks[sourceBlock.Index].Payload;
            Assert.NotEqual(0, BinaryPrimitives.ReadUInt16LittleEndian(sourceBlock.Payload.Span.Slice(44, 2)));
            Assert.Equal(sourceBlock.Payload.Slice(44, 4).ToArray(), rewritten.Slice(44, 4).ToArray());
        }
    }
}
