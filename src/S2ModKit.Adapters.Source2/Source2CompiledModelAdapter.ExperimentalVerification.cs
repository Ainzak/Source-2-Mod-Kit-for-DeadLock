using System.Globalization;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    public Task<ExperimentalTransformVerification> VerifyExperimentalTransformAsync(
        ArtifactContent input, ArtifactContent output, MutationPlan plan, CancellationToken cancellationToken = default)
    {
        try { return Task.FromResult(VerifyExperimentalTransformCore(input, output, plan, cancellationToken)); }
        catch (S2ModKitException) { throw; }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException
            or NotSupportedException or OverflowException or IndexOutOfRangeException or KeyNotFoundException
            or ValveResourceFormat.Utils.UnexpectedMagicException)
        {
            throw new S2ModKitException(new S2Error("EXPERIMENTAL_REOPEN_AUDIT_FAILED", "source2_adapter",
                $"Independent experimental reopen failed: {exception.Message}",
                "Reject the output; no publication is allowed.", ErrorCategory.RewriteOrVerification), exception);
        }
    }

    private ExperimentalTransformVerification VerifyExperimentalTransformCore(
        ArtifactContent input, ArtifactContent output, MutationPlan plan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _ = MutationPlanJson.Read(JsonDefaults.SerializeToUtf8(plan));
        if (!IsExperimentalTransformPlan(plan) || ContentHash.Compute(input.Bytes.Span) != input.ContentHash
            || ContentHash.Compute(output.Bytes.Span) != output.ContentHash || input.ContentHash != plan.InputHash
            || output.LogicalPath != input.LogicalPath)
            throw ExperimentalAuditFailure("Source/output identities or the versioned experimental contract drifted.");
        using var before = Parse(input, retainGeometryAnalysis: true);
        var outputEnvelope = ResourceEnvelopeReader.Read(output.Bytes);
        var operation = plan.Operations.Single();
        var target = operation.ExperimentalTransformTarget!;
        var mutable = VerifyExperimentalEnvelope(before, outputEnvelope, output, operation);
        using var after = Parse(output, retainGeometryAnalysis: true);

        var request = ExperimentalRequest(input, before.Snapshot, operation);
        RecipeValidator.Validate(new RecipeDocument
        {
            SchemaVersion = plan.SchemaVersion == 3 ? 7 : 6,
            RecipeId = plan.RecipeId,
            InputHash = plan.InputHash,
            Operations = [request.Operation]
        });
        var sourceProfiles = ExperimentalProfiles(request, before);
        var outputProfiles = ExperimentalProfiles(request with { Input = output, Model = after.Snapshot }, after)
            .ToDictionary(profile => profile.Mesh.MeshOrdinal);
        if (HasIncompleteMdatCoverage(before.Snapshot) || SelectsSharedLodMesh(before.Snapshot, plan)
            || before.Envelope.Blocks.Any(block => block.Type == "MBUF")
            || !sourceProfiles.Select(profile => profile.Mesh.Lod).Order().SequenceEqual(before.Snapshot.Lods.Select(lod => lod.Level).Order()))
            throw ExperimentalAuditFailure("The complete-buffer ownership or LOD profile is unavailable.");
        var expectedMutable = sourceProfiles.SelectMany(profile => new[] { profile.Mesh.BlockIndex, profile.Vertices.Snapshot.ResourceBlockIndex }).ToHashSet();
        if (!expectedMutable.SetEquals(mutable)) throw ExperimentalAuditFailure("The changed-block allowlist does not match independently resolved geometry.");
        var boxEvidence = new List<ExperimentalBoxEvidence>();
        var expectedPreservation = new List<PlannedExperimentalPreservationTarget>();
        var expectedBoxKeys = new HashSet<(int Block, string Field)>();
        foreach (var source in sourceProfiles)
        {
            token.ThrowIfCancellationRequested();
            VerifyExperimentalGeometry(source, observed: outputProfiles[source.Mesh.MeshOrdinal], target);
            var observed = outputProfiles[source.Mesh.MeshOrdinal];
            var (affected, sourceBoxes, evidence) = VerifyExperimentalBoxes(input, source, observed, target.BoxTargets);
            boxEvidence.AddRange(evidence);
            foreach (var box in sourceBoxes) expectedBoxKeys.Add((box.ResourceBlockIndex, box.FieldPath));
            // Undo only the reviewed float fields in an independent output tree. The complete
            // semantic hash then proves all other KV members, flags and numeric types unchanged.
            foreach (var box in sourceBoxes) ReplaceExperimentalBox(observed.Mesh.Block.Data, box, reverse: true);
            if (KvSemanticHasher.ComputeComplete(source.Mesh.Block.Data) != KvSemanticHasher.ComputeComplete(observed.Mesh.Block.Data))
                throw ExperimentalAuditFailure("MDAT changed outside the precise float-field allowlist.");
            AddExperimentalRenderSphereTargets(before, source, affected.Select(bone => bone.BoneIndex).ToHashSet(), expectedPreservation);
        }
        if (!expectedBoxKeys.SetEquals(target.BoxTargets.Select(box => (box.ResourceBlockIndex, box.FieldPath))))
            throw ExperimentalAuditFailure("The plan has an extra box outside the affected resource closure.");
        AddExperimentalVerificationRootPreservation(before, sourceProfiles, expectedPreservation);
        var sortedPreservation = expectedPreservation.OrderBy(field => field.ResourceBlockIndex).ThenBy(field => field.FieldPath, StringComparer.Ordinal).ToArray();
        if (JsonDefaults.Serialize(sortedPreservation) != JsonDefaults.Serialize(target.PreservationTargets))
            throw ExperimentalAuditFailure("Preservation targets are not the independently inventoried complete set.");
        var preservationEvidence = sortedPreservation.Select(field =>
            VerifyExperimentalPreservedField(after, sourceProfiles, outputProfiles, field)).ToArray();
        return new(
            [
                new("experimental_reopen", "passed", "Original and output envelopes were independently reopened."),
                new("experimental_geometry", "passed", target.Region is null
                    ? "Complete selected buffers match prescribed positions; all other decoded words remain unchanged."
                    : "Independent region masks, pinned records, positions, differential packed frames and exact triangle orientation pass; undeclared words remain unchanged."),
                new("experimental_box_policy", "passed", "Original envelopes and all contributors are contained; exact retain-and-expand policy verified."),
                new("experimental_metadata_preservation", "passed", "Inventoried sphere words and full occlusion/collision payloads are preserved, not proved coherent."),
                new("experimental_unchanged_data", "passed", "Non-target payloads and all MDAT members outside the exact box allowlist remain unchanged."),
                new("sphere_containment", "untested", "Preserved sphere coordinates/containment are not qualified."),
                new("proxy_coherence", "untested", "Authored occlusion proxies were preserved, not regenerated."),
                new("collision_correspondence", "untested", "Collision payloads remain unchanged; correspondence to edited visuals is unverified."),
            ],
            boxEvidence.OrderBy(box => box.Target.ResourceBlockIndex).ThenBy(box => box.Target.FieldPath, StringComparer.Ordinal).ToArray(),
            preservationEvidence);
    }
    private static HashSet<int> VerifyExperimentalEnvelope(ParsedModel before,
        Source2ResourceEnvelope outputEnvelope, ArtifactContent output, PlannedOperation operation)
        => VerifyExperimentalEnvelope(before, outputEnvelope, output, operation.ExperimentalTransformTarget!.SourceBlocks, operation.TargetBlocks);

    private static HashSet<int> VerifyExperimentalEnvelope(ParsedModel before,
        Source2ResourceEnvelope outputEnvelope, ArtifactContent output, IReadOnlyList<PlannedTargetBlock> frozenSources,
        IReadOnlyList<PlannedTargetBlock> mutationBlocks)
    {
        var sourceBlocks = before.Envelope.Blocks.Select(block => new PlannedTargetBlock(block.Index, block.Type, ContentHash.Compute(block.Payload.Span))).ToArray();
        if (!sourceBlocks.SequenceEqual(frozenSources) || before.Envelope.Blocks.Count != outputEnvelope.Blocks.Count
            || before.Envelope.HeaderVersion != outputEnvelope.HeaderVersion || before.Envelope.ResourceVersion != outputEnvelope.ResourceVersion
            || before.Envelope.TableStart != outputEnvelope.TableStart)
            throw ExperimentalAuditFailure("The complete original resource inventory differs from the frozen source.");
        var mutable = mutationBlocks.Select(block => block.Index).ToHashSet();
        foreach (var block in before.Envelope.Blocks)
        {
            var observed = outputEnvelope.Blocks[block.Index];
            if (observed.Type != block.Type || (!mutable.Contains(block.Index) && !observed.Payload.Span.SequenceEqual(block.Payload.Span)))
                throw ExperimentalAuditFailure("A non-target payload or resource block identity changed.");
        }
        // Only canonical envelope offsets/sizes implied by the replacement payloads may change.
        var observedReplacements = mutable.ToDictionary(index => index, index => outputEnvelope.Blocks[index].Payload);
        if (!ResourceEnvelopeWriter.Rebuild(before.Envelope, observedReplacements).AsSpan().SequenceEqual(output.Bytes.Span))
            throw ExperimentalAuditFailure("Resource headers, padding or offsets changed outside the canonical rebuild contract.");
        return mutable;
    }

    private static void VerifyExperimentalGeometry(Source2AffineProfile source,
        Source2AffineProfile observed, PlannedExperimentalTransformTarget target)
    {
        ValidateExperimentalVertexStreams(source);
        if (source.SelectedVertices.Length != source.Vertices.Snapshot.VertexCount
            || source.SelectedVertices.Where((vertex, index) => vertex != index).Any())
            throw ExperimentalAuditFailure("The selection is not an exclusively owned complete buffer.");
        var geometry = target.GeometryTargets.Single(item => item.MeshOrdinal == source.Mesh.MeshOrdinal);
        if (geometry.Codec != source.Mesh.Geometry.Codec || observed.Mesh.Geometry.Codec != geometry.Codec
            || geometry.DecodedVertexBufferHash != source.Vertices.Snapshot.DecodedHash
            || geometry.ExpectedDecodedVertexBufferHash != observed.Vertices.Snapshot.DecodedHash)
            throw ExperimentalAuditFailure("The observed codec or decoded position identity differs from the frozen contract.");
        // Recompute position words from original serialized positions, not the planned output hash.
        var expectedPositions = target.Region is null ? ExperimentalPositions(source, target) : VerifyRegionBuffer(source, observed, target, geometry);
        if (!observed.Vertices.Decoded.AsSpan().SequenceEqual(expectedPositions))
            throw ExperimentalAuditFailure("Observed selected positions or immutable vertex words differ from the prescribed uniform edit.");
        if (source.Mesh.GeometryAnalysis!.VertexBuffers.Count != observed.Mesh.GeometryAnalysis!.VertexBuffers.Count)
            throw ExperimentalAuditFailure("Participating buffer cardinality changed.");
        for (var index = 0; index < source.Mesh.GeometryAnalysis.VertexBuffers.Count; index++)
        {
            var oldBuffer = source.Mesh.GeometryAnalysis.VertexBuffers[index];
            var newBuffer = observed.Mesh.GeometryAnalysis.VertexBuffers[index];
            VerifyExperimentalBufferLayout(oldBuffer.Snapshot, newBuffer.Snapshot,
                oldBuffer.Decoded, newBuffer.Decoded, selected: index == source.Vertices.Snapshot.Ordinal);
        }
    }

    // Selected words have already passed the independent position/frame audit above.
    // This check freezes every layout and requires all other buffers to remain byte-identical.
    internal static void VerifyExperimentalBufferLayout(VertexBufferSnapshot original, VertexBufferSnapshot observed,
        ReadOnlySpan<byte> originalBytes, ReadOnlySpan<byte> observedBytes, bool selected)
    {
        if (original.Ordinal != observed.Ordinal || original.VertexCount != observed.VertexCount
            || original.Stride != observed.Stride || original.PositionLayout != observed.PositionLayout
            || (!selected && !originalBytes.SequenceEqual(observedBytes)))
            throw ExperimentalAuditFailure("An unchanged participating buffer or vertex layout changed.");
    }

    private static (Source2BoneBoundsAnalysis[] Affected, PlannedExperimentalBoxTarget[] Boxes, List<ExperimentalBoxEvidence> Evidence)
        VerifyExperimentalBoxes(ArtifactContent input, Source2AffineProfile source,
            Source2AffineProfile observed, IReadOnlyList<PlannedExperimentalBoxTarget> targets)
    {
        var boxEvidence = new List<ExperimentalBoxEvidence>();
        var affected = source.Metadata.BoneBounds.Where(bone => bone.InfluencedVertices.Any(vertex =>
            vertex >= source.BufferBaseOffset && vertex < source.BufferBaseOffset + source.Vertices.Snapshot.VertexCount)).ToArray();
        var sourceBoxes = targets.Where(box => box.ResourceBlockIndex == source.Mesh.BlockIndex).ToArray();
        var requiredFields = affected.Select(bone => $"m_skeleton.m_bones[{bone.BoneIndex}].m_bbox.m_vecCenter+m_vecSize")
            .Prepend("m_sceneObjects[0].m_vMinBounds+m_vMaxBounds").ToHashSet(StringComparer.Ordinal);
        if (!requiredFields.SetEquals(sourceBoxes.Select(box => box.FieldPath)) || sourceBoxes.Length != requiredFields.Count)
            throw ExperimentalAuditFailure("An affected box target is missing, duplicated or outside the independently resolved closure.");
        foreach (var box in sourceBoxes)
        {
            var originalWords = ReadExperimentalBox(source.Mesh.Block.Data, box);
            var outputWords = ReadExperimentalBox(observed.Mesh.Block.Data, box);
            if (!originalWords.SequenceEqual(box.OriginalWords) || !outputWords.SequenceEqual(box.ExpectedWords))
                throw ExperimentalAuditFailure("A raw original or observed box differs from the frozen words.");
            Point3[] contributors;
            float[] matrix;
            int[] membership;
            EnvelopeVerification result;
            if (box.Storage == "min_max")
            {
                matrix = ExperimentalIdentityMatrix;
                membership = Enumerable.Range(0, observed.AllBeforePositions.Length).ToArray();
                contributors = observed.AllBeforePositions;
                result = CullingEnvelopeVerifier.Verify(
                    new Bounds3(ExperimentalPointWords(originalWords, 0), ExperimentalPointWords(originalWords, 3)), contributors,
                    new Bounds3(ExperimentalPointWords(outputWords, 0), ExperimentalPointWords(outputWords, 3)));
            }
            else
            {
                var bone = affected.Single(item => box.FieldPath == $"m_skeleton.m_bones[{item.BoneIndex}].m_bbox.m_vecCenter+m_vecSize");
                matrix = bone.InverseBindPose.ToArray();
                membership = bone.InfluencedVertices.ToArray();
                contributors = membership.SelectMany(index =>
                {
                    var enclosure = AffinePointEnclosure.Enclose(observed.AllBeforePositions[index], matrix);
                    return new[] { enclosure.Min, enclosure.Max };
                }).ToArray();
                result = CullingEnvelopeVerifier.Verify(
                    new CenterHalfExtentBounds(ExperimentalPointWords(originalWords, 0), ExperimentalPointWords(originalWords, 3)), contributors,
                    new CenterHalfExtentBounds(ExperimentalPointWords(outputWords, 0), ExperimentalPointWords(outputWords, 3)));
            }
            if (result.Status != EnvelopeVerificationStatus.Passed || box.ContributorCount != membership.Length
                || box.ContributorSetHash != ExperimentalContributorsHash(input, source, membership)
                || box.CoordinateMatrixHash != HashExperimentalWords(matrix) || !box.CoordinateMatrixWords.SequenceEqual(Words(matrix)))
                throw ExperimentalAuditFailure($"Independent containment/policy or contributor identity failed for {box.FieldPath}: {result.Status}.");
            boxEvidence.Add(new(box, outputWords, "passed"));
        }
        return (affected, sourceBoxes, boxEvidence);
    }

    private static void AddExperimentalVerificationRootPreservation(ParsedModel before,
        IReadOnlyList<Source2AffineProfile> sourceProfiles, List<PlannedExperimentalPreservationTarget> expectedPreservation)
    {
        var model = before.Resource.Blocks.OfType<Model>().Single();
        if (HasMorphData(model.Data) || model.Data.ContainsKey("m_vMinBounds") || model.Data.ContainsKey("m_vMaxBounds"))
            throw ExperimentalAuditFailure("The root contains an unsupported derived family.");
        var spheres = ExperimentalArray(ExperimentalCollection(model.Data, "m_modelSkeleton"), "m_boneSphere");
        var rootIndex = before.Resource.Blocks.Select((block, index) => (block, index)).Single(item => ReferenceEquals(item.block, model)).index;
        var affectedRoots = ResolveExperimentalAffectedRoots(model, sourceProfiles, spheres.Count);
        for (var index = 0; index < spheres.Count; index++)
        {
            var radius = ExperimentalFloat(spheres[index]);
            ValidateExperimentalRadius(radius, affectedRoots.Contains(index));
            expectedPreservation.Add(new(rootIndex, $"m_modelSkeleton.m_boneSphere[{index}]", "root_sphere",
                affectedRoots.Contains(index) ? "affected" : "resource", "preserve_unverified",
                ContentHash.Compute(before.Envelope.Blocks[rootIndex].Payload.Span), Words(radius)));
        }
        AddExperimentalBlockPreservation(before, expectedPreservation);
    }

    private static ExperimentalPreservationEvidence VerifyExperimentalPreservedField(ParsedModel after,
        IReadOnlyList<Source2AffineProfile> sourceProfiles, IReadOnlyDictionary<int, Source2AffineProfile> outputProfiles,
        PlannedExperimentalPreservationTarget field)
    {
        uint[] observedWords;
        if (field.Category == "render_sphere")
        {
            var source = sourceProfiles.Single(profile => profile.Mesh.BlockIndex == field.ResourceBlockIndex);
            var outputProfile = outputProfiles[source.Mesh.MeshOrdinal];
            var bones = ExperimentalArray(ExperimentalCollection(outputProfile.Mesh.Block.Data, "m_skeleton"), "m_bones");
            var boneIndex = int.Parse(field.FieldPath.AsSpan("m_skeleton.m_bones[".Length,
                field.FieldPath.IndexOf(']') - "m_skeleton.m_bones[".Length), CultureInfo.InvariantCulture);
            observedWords = Words(ExperimentalFloat(bones[boneIndex]["m_flSphereRadius"]));
        }
        else if (field.Category == "root_sphere")
        {
            var root = after.Resource.Blocks.OfType<Model>().Single();
            var index = int.Parse(field.FieldPath.AsSpan("m_modelSkeleton.m_boneSphere[".Length,
                field.FieldPath.Length - "m_modelSkeleton.m_boneSphere[".Length - 1), CultureInfo.InvariantCulture);
            observedWords = Words(ExperimentalFloat(ExperimentalArray(ExperimentalCollection(root.Data, "m_modelSkeleton"), "m_boneSphere")[index]));
        }
        else observedWords = [];
        var observedHash = ContentHash.Compute(after.Envelope.Blocks[field.ResourceBlockIndex].Payload.Span);
        if (!observedWords.SequenceEqual(field.OriginalWords) || (field.FieldPath == "$payload" && observedHash != field.SourcePayloadHash))
            throw ExperimentalAuditFailure("An intentionally preserved field or full payload changed.");
        return new ExperimentalPreservationEvidence(field, observedHash, observedWords, "passed");
    }


}
