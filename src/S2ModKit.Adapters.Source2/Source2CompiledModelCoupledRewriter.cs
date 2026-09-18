using S2ModKit.Application;
using S2ModKit.Domain;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    internal RewriteCandidate RewriteCoupledTransform(
        ArtifactContent input,
        ModelSnapshot model,
        PlannedCoupledTransformTarget plan,
        CoupledRewriteFailurePoint failurePoint = CoupledRewriteFailurePoint.None)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(plan);
        if (ContentHash.Compute(input.Bytes.Span) != input.ContentHash
            || input.ContentHash != model.Artifact.ContentHash)
        {
            throw Errors.Input("INPUT_HASH_DRIFT", "The coupled rewrite input no longer matches its immutable identity.", "Reload the input and regenerate the plan.");
        }

        using var parsed = Parse(input, retainGeometryAnalysis: true);
        ValidateSnapshotAgreement(model, parsed.Snapshot);
        var mesh = parsed.MeshesByOrdinal.Values.SingleOrDefault();
        var control = parsed.Resource.Blocks.OfType<BinaryKV3>()
            .SingleOrDefault(block => block.Type.ToString() == "CTRL"
                && block.Data.Root.TryGetValue("embedded_meshes", out _)
                && block.Data.Root.TryGetValue("embedded_physics", out _));
        if (mesh?.RawMbufAnalysis is null
            || mesh.PhysicsAnalysis is null
            || mesh.WholeMeshTransformAnalysis is null
            || control is null
            || parsed.Resource.Blocks[plan.Collision.ResourceBlockIndex] is not PhysAggregateData physics)
        {
            throw Incomplete("The parsed resource does not expose every planned coupled rewrite boundary.");
        }

        ModelSnapshot? candidateSnapshot = null;
        var visualResult = default(Source2RawMbufRewriteResult);
        var collisionResult = default(Source2ConvexPhysRewriteResult);
        var candidateBytes = Source2CoupledRewriteComposer.Compose(
            parsed.Envelope,
            plan,
            () =>
            {
                visualResult = Source2RawMbufWriter.Rewrite(
                    parsed.Envelope.Blocks[plan.Visual.MbufResourceBlockIndex],
                    mesh.DrawCalls.Select(item => new GeometryDrawCallInput(item.Snapshot, item.Data)).ToArray(),
                    mesh.Block.Data,
                    mesh.WholeMeshTransformAnalysis,
                    plan.Visual);
                var mdat = SerializeDeterministically(
                    mesh.Block.Serialize,
                    $"coupled MDAT block {plan.Visual.MeshResourceBlockIndex}");
                mdat = BinaryKv3ContainerCounts.CompleteVersion4Header(mdat, mesh.Block.Data);
                return new Dictionary<int, ReadOnlyMemory<byte>>
                {
                    [plan.Visual.MeshResourceBlockIndex] = mdat,
                    [plan.Visual.MbufResourceBlockIndex] = visualResult.Payload,
                };
            },
            () =>
            {
                collisionResult = Source2ConvexPhysWriter.Rewrite(
                    physics,
                    control.Data.Root,
                    parsed.Envelope,
                    plan.Visual.BeforeBounds,
                    plan.Visual.ExpectedAfterBounds,
                    plan.Collision);
                return collisionResult.Payload;
            },
            candidate =>
            {
                var artifact = new ArtifactContent(input.LogicalPath, ContentHash.Compute(candidate.Span), candidate);
                using var reopened = Parse(artifact, retainGeometryAnalysis: true);
                VerifyCoupledReopen(parsed, reopened, plan, visualResult!, collisionResult!);
                candidateSnapshot = reopened.Snapshot;
            },
            failurePoint);
        if (candidateSnapshot is null)
        {
            throw Incomplete("The coupled candidate was not reopened and verified.");
        }

        return new RewriteCandidate(input.LogicalPath, candidateBytes, candidateSnapshot);
    }

    private static void VerifyCoupledReopen(
        ParsedModel before,
        ParsedModel after,
        PlannedCoupledTransformTarget plan,
        Source2RawMbufRewriteResult visual,
        Source2ConvexPhysRewriteResult collision)
    {
        var mesh = after.MeshesByOrdinal.Values.SingleOrDefault();
        if (mesh?.RawMbufAnalysis is null
            || mesh.PhysicsAnalysis is null
            || mesh.WholeMeshTransformAnalysis is null
            || ContentHash.Compute(after.Envelope.Blocks[plan.Visual.MbufResourceBlockIndex].Payload.Span) != plan.Visual.ExpectedMbufBlockHash
            || mesh.RawMbufAnalysis.Geometry.VertexBuffers[0].Snapshot.DecodedHash != plan.Visual.ExpectedDecodedVertexBufferHash
            || mesh.RawMbufAnalysis.Geometry.IndexBuffers[0].Snapshot.DecodedHash != plan.Visual.DecodedIndexBufferHash
            || mesh.WholeMeshTransformAnalysis.SceneBounds != plan.Visual.ExpectedAfterBounds
            || KvSemanticHasher.ComputeComplete(mesh.Block.Data) != visual.VisualMetadataSemanticHash
            || mesh.PhysicsAnalysis.PayloadHash != ContentHash.Compute(collision.Payload.Span)
            || HashPoints(mesh.PhysicsAnalysis.VertexPositions) != plan.Collision.ExpectedPositionHash)
        {
            throw Incomplete("The fully reopened resource differs from the coupled visual/collision plan.");
        }

        var targetIndices = plan.TargetBlocks.Select(block => block.Index).ToHashSet();
        if (before.Envelope.Blocks.Count != after.Envelope.Blocks.Count)
        {
            throw Incomplete("The coupled rewrite changed the resource block count.");
        }

        for (var index = 0; index < before.Envelope.Blocks.Count; index++)
        {
            var source = before.Envelope.Blocks[index];
            var reopened = after.Envelope.Blocks[index];
            if (!string.Equals(source.Type, reopened.Type, StringComparison.Ordinal)
                || (!targetIndices.Contains(index) && !source.Payload.Span.SequenceEqual(reopened.Payload.Span)))
            {
                throw Incomplete($"The coupled rewrite changed unrelated block {index}.");
            }
        }

        VerifyCollisionAgainstPlan(mesh.PhysicsAnalysis, plan.Collision);
    }

    private static void VerifyCollisionAgainstPlan(Source2ConvexPhysAnalysis actual, PlannedConvexPhysTransformTarget plan)
    {
        if (actual.HullVertexEdgesHash != plan.HullVertexEdgesHash
            || actual.HalfEdgesHash != plan.HalfEdgesHash
            || actual.FacesHash != plan.FacesHash
            || actual.RegionNodesHash != plan.RegionNodesHash
            || actual.HalfEdgeCount != plan.HalfEdgeCount
            || actual.FaceCount != plan.FaceCount
            || actual.RegionNodeCount != plan.RegionNodeCount
            || actual.OrthographicAreaX != plan.UnchangedOrthographicAreaFractions.X
            || actual.OrthographicAreaY != plan.UnchangedOrthographicAreaFractions.Y
            || actual.OrthographicAreaZ != plan.UnchangedOrthographicAreaFractions.Z
            || !string.Equals(actual.CollisionGroupString, plan.CollisionGroupString, StringComparison.Ordinal)
            || actual.PositionBounds.Min.X != plan.ExpectedAfter.Bounds.Min.X
            || actual.PositionBounds.Min.Y != plan.ExpectedAfter.Bounds.Min.Y
            || actual.PositionBounds.Min.Z != plan.ExpectedAfter.Bounds.Min.Z
            || actual.PositionBounds.Max.X != plan.ExpectedAfter.Bounds.Max.X
            || actual.PositionBounds.Max.Y != plan.ExpectedAfter.Bounds.Max.Y
            || actual.PositionBounds.Max.Z != plan.ExpectedAfter.Bounds.Max.Z)
        {
            throw Incomplete("The reopened PHYS topology, attributes, or derived bounds differ from the coupled plan.");
        }
    }

    private static ContentHash HashPoints(S2ModKit.Geometry.Point3[] points)
    {
        var bytes = new byte[checked(points.Length * 12)];
        for (var index = 0; index < points.Length; index++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(index * 12), BitConverter.SingleToInt32Bits(points[index].X));
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan((index * 12) + 4), BitConverter.SingleToInt32Bits(points[index].Y));
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan((index * 12) + 8), BitConverter.SingleToInt32Bits(points[index].Z));
        }

        return ContentHash.Compute(bytes);
    }

    private static S2ModKitException Incomplete(string summary) => Errors.Verification(
        "COUPLED_TRANSFORM_INCOMPLETE",
        summary,
        "Discard the unpublished candidate and regenerate the complete coupled plan.");
}
