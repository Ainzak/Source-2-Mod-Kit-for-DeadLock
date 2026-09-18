using S2ModKit.Domain;

namespace S2ModKit.Adapters.Source2;

internal enum CoupledRewriteFailurePoint
{
    None,
    AfterVisualRewrite,
    AfterCollisionRewrite,
    AfterEnvelopeRebuild,
    AfterReopenVerification,
}

internal static class Source2CoupledRewriteComposer
{
    public static byte[] Compose(
        Source2ResourceEnvelope envelope,
        PlannedCoupledTransformTarget plan,
        Func<IReadOnlyDictionary<int, ReadOnlyMemory<byte>>> rewriteVisual,
        Func<ReadOnlyMemory<byte>> rewriteCollision,
        Action<ReadOnlyMemory<byte>> reopenAndVerify,
        CoupledRewriteFailurePoint failurePoint = CoupledRewriteFailurePoint.None)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rewriteVisual);
        ArgumentNullException.ThrowIfNull(rewriteCollision);
        ArgumentNullException.ThrowIfNull(reopenAndVerify);
        ValidateTargetBlocks(envelope, plan);
        var inputHashes = envelope.Blocks.Select(block => ContentHash.Compute(block.Payload.Span)).ToArray();

        var visual = rewriteVisual();
        var expectedVisual = new[] { plan.Visual.MeshResourceBlockIndex, plan.Visual.MbufResourceBlockIndex };
        if (visual.Count != expectedVisual.Length || !visual.Keys.ToHashSet().SetEquals(expectedVisual))
        {
            throw Incomplete("The visual writer did not produce exactly the planned MDAT and MBUF replacements.");
        }

        Inject(failurePoint, CoupledRewriteFailurePoint.AfterVisualRewrite);
        var collision = rewriteCollision();
        if (collision.IsEmpty)
        {
            throw Incomplete("The collision writer produced an empty PHYS replacement.");
        }

        Inject(failurePoint, CoupledRewriteFailurePoint.AfterCollisionRewrite);
        var replacements = visual.ToDictionary(item => item.Key, item => item.Value);
        if (!replacements.TryAdd(plan.Collision.ResourceBlockIndex, collision)
            || !replacements.Keys.ToHashSet().SetEquals(plan.TargetBlocks.Select(block => block.Index)))
        {
            throw Incomplete("The coupled replacement set does not exactly match the planned target blocks.");
        }

        var candidate = ResourceEnvelopeWriter.Rebuild(envelope, replacements);
        Inject(failurePoint, CoupledRewriteFailurePoint.AfterEnvelopeRebuild);
        reopenAndVerify(candidate);
        Inject(failurePoint, CoupledRewriteFailurePoint.AfterReopenVerification);

        if (!envelope.Blocks.Select(block => ContentHash.Compute(block.Payload.Span)).SequenceEqual(inputHashes))
        {
            throw Incomplete("The immutable input envelope changed during coupled composition.");
        }

        return candidate;
    }

    private static void ValidateTargetBlocks(Source2ResourceEnvelope envelope, PlannedCoupledTransformTarget plan)
    {
        var expected = new Dictionary<int, (string Type, ContentHash Hash)>
        {
            [plan.Visual.MeshResourceBlockIndex] = ("MDAT", plan.Visual.MeshBlockInputHash),
            [plan.Visual.MbufResourceBlockIndex] = ("MBUF", plan.Visual.MbufBlockInputHash),
            [plan.Collision.ResourceBlockIndex] = ("PHYS", plan.Collision.PayloadInputHash),
        };
        if (plan.TargetBlocks.Count != expected.Count
            || plan.TargetBlocks.Select(block => block.Index).Distinct().Count() != expected.Count)
        {
            throw Incomplete("The coupled plan does not contain exactly one MDAT, MBUF, and PHYS target.");
        }

        foreach (var target in plan.TargetBlocks)
        {
            if (!expected.TryGetValue(target.Index, out var facts)
                || (uint)target.Index >= (uint)envelope.Blocks.Count
                || !string.Equals(target.Type, facts.Type, StringComparison.Ordinal)
                || target.InputHash != facts.Hash)
            {
                throw Incomplete("The coupled plan target-block set is inconsistent.");
            }

            var block = envelope.Blocks[target.Index];
            if (!string.Equals(block.Type, facts.Type, StringComparison.Ordinal)
                || ContentHash.Compute(block.Payload.Span) != facts.Hash)
            {
                throw Incomplete($"Planned {facts.Type} block {target.Index} drifted before composition.");
            }
        }
    }

    private static void Inject(CoupledRewriteFailurePoint actual, CoupledRewriteFailurePoint boundary)
    {
        if (actual == boundary)
        {
            throw Incomplete($"Injected failure at coupled boundary {boundary}.");
        }
    }

    private static S2ModKitException Incomplete(string summary) => Errors.Verification(
        "COUPLED_TRANSFORM_INCOMPLETE",
        summary,
        "Discard the unpublished candidate and regenerate the complete coupled plan.");
}
