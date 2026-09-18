using System.Security.Cryptography;
using System.Text;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Application;

public sealed class MutationPlanner
{
    public static MutationPlan CreatePlan(
        ModelSnapshot model,
        RecipeDocument recipe,
        IReadOnlyList<ArtifactContent>? dependencies = null) =>
        CreatePlanCore(model, recipe, dependencies, null, null);

    public static MutationPlan CreatePlan(
        ModelSnapshot model,
        RecipeDocument recipe,
        ArtifactContent input,
        ITransformOperationPlanner transformPlanner,
        IReadOnlyList<ArtifactContent>? dependencies = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(transformPlanner);
        if (input.ContentHash != model.Artifact.ContentHash
            || !string.Equals(StableIdentity.NormalizePath(input.LogicalPath), model.Artifact.LogicalPath, StringComparison.Ordinal)
            || ContentHash.Compute(input.Bytes.Span) != input.ContentHash)
        {
            throw Errors.Input("INPUT_HASH_DRIFT", "The transform planner input does not match the inspected model snapshot.", "Reload the immutable project graph and inspect it again before planning.");
        }

        return CreatePlanCore(model, recipe, dependencies, input, transformPlanner);
    }

    private static MutationPlan CreatePlanCore(
        ModelSnapshot model,
        RecipeDocument recipe,
        IReadOnlyList<ArtifactContent>? dependencies,
        ArtifactContent? input,
        ITransformOperationPlanner? transformPlanner)
    {
        ArgumentNullException.ThrowIfNull(model);
        RecipeValidator.Validate(recipe);

        if (recipe.InputHash != model.Artifact.ContentHash)
        {
            throw Errors.Input("INPUT_HASH_DRIFT", "The recipe input hash does not match the inspected artifact.", "Re-inspect the input and regenerate or update the recipe deliberately.");
        }

        ValidateSnapshotIdentities(model);
        var inputs = CreateInputFingerprints(model, dependencies ?? []);
        var blocksByIndex = ValidateBlockReferences(model);
        var actualLods = model.Lods.Select(lod => lod.Level).Order().ToArray();
        var alreadySelected = new HashSet<string>(StringComparer.Ordinal);
        var plannedOperations = new List<PlannedOperation>(recipe.Operations.Count);

        foreach (var operation in recipe.Operations)
        {
            var expectedLods = operation.ExpectedMatchesByLod.Keys.Select(ParseLod).Order().ToArray();
            if (!actualLods.SequenceEqual(expectedLods))
            {
                throw Errors.Selection(
                    "LOD_COVERAGE_INCOMPLETE",
                    $"Operation '{operation.OperationId}' declares LODs [{string.Join(", ", expectedLods)}], but the model contains [{string.Join(", ", actualLods)}].",
                    "Declare an expected match count for every present LOD.");
            }

            var selected = Select(model, operation).OrderBy(item => item.Lod)
                .ThenBy(item => item.ResourcePath, StringComparer.Ordinal)
                .ThenBy(item => item.MeshOrdinal)
                .ThenBy(item => item.DrawCallOrdinal)
                .ToArray();

            foreach (var lod in actualLods)
            {
                var actual = selected.Count(item => item.Lod == lod);
                var expected = operation.ExpectedMatchesByLod[lod.ToString(System.Globalization.CultureInfo.InvariantCulture)];
                if (actual != expected)
                {
                    throw Errors.Selection(
                        "SELECTOR_CARDINALITY_MISMATCH",
                        $"Operation '{operation.OperationId}' selected {actual} draw calls in LOD {lod}; expected {expected}.",
                        "Inspect the model, narrow the selector, and update explicit per-LOD expectations.");
                }
            }

            foreach (var item in selected)
            {
                if (!alreadySelected.Add(item.DrawCallId))
                {
                    throw Errors.Selection("SELECTION_OVERLAP", $"Draw call '{item.DrawCallId}' is selected by more than one operation.", "Combine the intent into one operation or use non-overlapping selectors.");
                }
            }

            if (operation is RemoveComponentOperation)
            {
                var targetBlocks = CreateRemovalTargetBlocks(selected, blocksByIndex);
                plannedOperations.Add(new PlannedOperation(operation.OperationId, operation.OperationKind, operation.Version, selected, targetBlocks));
                continue;
            }

            if (operation is not TransformComponentOperation transform || input is null || transformPlanner is null)
            {
                throw Errors.Unsupported(
                    "TRANSFORM_PLANNING_UNAVAILABLE",
                    "transform_component@1 requires a configured adapter-specific geometry planner.",
                    "Configure the Source 2 geometry codec and rerun inspect before planning; no plan or build was published.");
            }

            var result = transformPlanner.PlanTransform(new TransformPlanningRequest(input, model, transform, selected));
            if (transform.Version == 2)
            {
                ValidateCoupledTransformPlanningResult(result, selected, blocksByIndex, transform);
            }
            else
            {
                ValidateTransformPlanningResult(result, selected, blocksByIndex, transform);
            }
            plannedOperations.Add(new PlannedOperation(
                operation.OperationId,
                operation.OperationKind,
                operation.Version,
                selected,
                result.TargetBlocks.OrderBy(block => block.Index).ThenBy(block => block.Type, StringComparer.Ordinal).ToArray())
            {
                GeometryTargets = result.GeometryTargets.OrderBy(target => target.Lod).ThenBy(target => target.MeshOrdinal).ThenBy(target => target.VertexBufferOrdinal).ToArray(),
                DistanceFieldTargets = result.DistanceFieldTargets.OrderBy(target => target.ResourceBlockIndex).ThenBy(target => target.FieldIndex).ToArray(),
                CoupledTransformTarget = result.CoupledTransformTarget,
            });
        }

        var fingerprint = ComputeFingerprint(recipe, inputs, plannedOperations);
        return new MutationPlan(recipe.RecipeId, recipe.InputHash, fingerprint, plannedOperations)
        {
            Inputs = inputs,
        };
    }

    private static List<SelectedDrawCall> Select(ModelSnapshot model, RecipeOperation operation)
    {
        var material = operation.Selector.MaterialPath is null ? null : StableIdentity.NormalizePath(operation.Selector.MaterialPath);
        var ids = operation.Selector.DrawCallIds is null
            ? null
            : new HashSet<string>(operation.Selector.DrawCallIds, StringComparer.Ordinal);
        var selected = new List<SelectedDrawCall>();

        foreach (var lod in model.Lods)
        {
            foreach (var mesh in lod.Meshes)
            {
                foreach (var drawCall in mesh.DrawCalls)
                {
                    var matches = operation.Selector.Kind switch
                    {
                        "material_exact" => string.Equals(drawCall.MaterialPath, material, StringComparison.Ordinal),
                        "draw_call_ids" => ids!.Contains(drawCall.Id),
                        _ => false,
                    };

                    if (matches)
                    {
                        selected.Add(new SelectedDrawCall(
                            lod.Level,
                            StableIdentity.NormalizePath(mesh.ResourcePath),
                            mesh.MeshOrdinal,
                            mesh.ResourceBlockIndex,
                            drawCall.Id,
                            drawCall.MaterialPath,
                            drawCall.DrawCallOrdinal,
                            drawCall.IndexStart,
                            drawCall.IndexCount));
                    }
                }
            }
        }

        if (ids is not null)
        {
            var missing = ids.Except(selected.Select(item => item.DrawCallId), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (missing.Length > 0)
            {
                throw Errors.Selection("DRAW_CALL_ID_NOT_FOUND", $"The selector references unknown draw-call IDs: {string.Join(", ", missing)}.", "Copy IDs from the current inspect output.");
            }
        }

        return selected;
    }

    private static PlannedTargetBlock[] CreateRemovalTargetBlocks(
        SelectedDrawCall[] selected,
        Dictionary<int, ResourceBlockSnapshot> blocksByIndex) =>
        selected.Select(item => item.ResourceBlockIndex)
            .Distinct()
            .Order()
            .Select(index =>
            {
                var block = blocksByIndex[index];
                return new PlannedTargetBlock(block.Index, block.Type, block.ContentHash);
            })
            .ToArray();

    private static void ValidateTransformPlanningResult(
        TransformPlanningResult result,
        SelectedDrawCall[] selected,
        Dictionary<int, ResourceBlockSnapshot> blocksByIndex,
        TransformComponentOperation operation)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.GeometryTargets is null || result.GeometryTargets.Count == 0
            || result.DistanceFieldTargets is null
            || result.TargetBlocks is null || result.TargetBlocks.Count == 0)
        {
            throw Errors.Unsupported("TRANSFORM_PLAN_INCOMPLETE", "The geometry planner returned an incomplete transform plan.", "Reject the plan and use a reviewed adapter profile that reports geometry, distance fields, and target blocks explicitly.");
        }

        var actualLods = result.GeometryTargets.Select(target => target.Lod).Order().ToArray();
        var expectedLods = operation.ExpectedVerticesByLod.Keys.Select(ParseLod).Order().ToArray();
        if (!actualLods.SequenceEqual(expectedLods)
            || result.GeometryTargets.GroupBy(target => target.Lod).Any(group => group.Count() != 1))
        {
            throw Errors.Selection("TRANSFORM_GEOMETRY_LOD_COVERAGE_INCOMPLETE", "The geometry planner did not return exactly one target buffer for every declared LOD.", "Use a supported component with one complete target vertex buffer per LOD.");
        }

        foreach (var target in result.GeometryTargets)
        {
            var expectedVertices = operation.ExpectedVerticesByLod[target.Lod.ToString(System.Globalization.CultureInfo.InvariantCulture)];
            if (target.SelectedVertexCount != expectedVertices)
            {
                throw Errors.Selection("VERTEX_CARDINALITY_MISMATCH", $"Operation '{operation.OperationId}' selected {target.SelectedVertexCount} vertices in LOD {target.Lod}; expected {expectedVertices}.", "Inspect the current model and update the explicit per-LOD vertex expectations.");
            }


            if (operation.Version == 3)
            {
                var lod = target.Lod.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (operation.ConnectedComponentIdsByLod is null
                    || !operation.ConnectedComponentIdsByLod.TryGetValue(lod, out var expectedComponents)
                    || !target.ConnectedComponentIds.Order(StringComparer.Ordinal).SequenceEqual(expectedComponents.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                {
                    throw Errors.Selection("CONNECTED_COMPONENT_SELECTION_DRIFT", $"Operation '{operation.OperationId}' connected-component selection drifted in LOD {target.Lod}.", "Regenerate the plan from current inspect evidence.");
                }
            }

            if (!float.IsFinite(target.MaximumDisplacement)
                || target.MaximumDisplacement <= 0f
                || target.MaximumDisplacement > operation.Limits.MaximumVertexDisplacement
                || target.MaximumDisplacement > RecipeValidator.MaximumTransformDisplacement)
            {
                throw Errors.Selection("TRANSFORM_DISPLACEMENT_EXCEEDED", $"Operation '{operation.OperationId}' would move a vertex by up to {target.MaximumDisplacement.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} Source units.", "Reduce the transform or increase the recipe cap within the hard safety limit.");
            }

            if (target.AllowedChangedAttributes is null
                || !target.AllowedChangedAttributes.SequenceEqual(["position"], StringComparer.Ordinal))
            {
                throw Errors.Unsupported("TRANSFORM_ALLOWED_CHANGE_UNSUPPORTED", "The geometry planner requested changes outside the position attribute.", "Use a position-only transform adapter profile.");
            }

            if (target.UniformScale != operation.Transform.UniformScale
                || target.Translation != operation.Transform.Translation
                || target.PositionLayout is null
                || target.Codec is null
                || target.BoneBoundsTargets is null
                || (operation.Version != 3 && target.BoneBoundsTargets.Count == 0)
                || (operation.Version == 3 && (target.ConnectedComponentIds is null || target.ConnectedComponentIds.Count == 0))
                || !IsValidBounds(target.BeforeBounds)
                || !IsValidBounds(target.ExpectedAfterBounds)
                || !IsValidVector(target.FrozenPivot)
                || !IsValidHash(target.VertexBlockInputHash)
                || !IsValidHash(target.IndexBlockInputHash)
                || !IsValidHash(target.DecodedVertexBufferHash)
                || !IsValidHash(target.ExpectedDecodedVertexBufferHash)
                || target.ExpectedDecodedVertexBufferHash == target.DecodedVertexBufferHash
                || !IsValidHash(target.DecodedIndexBufferHash)
                || !IsValidHash(target.VertexSetHash)
                || !HasExpectedTransformedBounds(
                    target.BeforeBounds,
                    target.ExpectedAfterBounds,
                    target.FrozenPivot,
                    target.UniformScale,
                    target.Translation))
            {
                throw Errors.Unsupported("TRANSFORM_GEOMETRY_FACTS_INVALID", "The geometry planner returned non-finite, malformed, or recipe-inconsistent transform facts.", "Reject the adapter result and regenerate the plan from a reviewed profile.");
            }

            if (target.BoneBoundsTargets.GroupBy(item => (item.BoneIndex, item.BoneName)).Any(group => group.Count() != 1)
                || target.BoneBoundsTargets.Any(item => !ValidateBoneBoundsTarget(item, target.UniformScale)))
            {
                throw Errors.Unsupported("TRANSFORM_BONE_BOUNDS_FACTS_INVALID", "The geometry planner returned duplicate, malformed, or transform-inconsistent bone culling facts.", "Reject the adapter result and regenerate the plan from a reviewed skinned-mesh profile.");
            }

            if (!blocksByIndex.TryGetValue(target.ResourceBlockIndex, out var meshBlock)
                || !blocksByIndex.TryGetValue(target.VertexResourceBlockIndex, out var vertexBlock)
                || !blocksByIndex.TryGetValue(target.IndexResourceBlockIndex, out var indexBlock)
                || vertexBlock.ContentHash != target.VertexBlockInputHash
                || indexBlock.ContentHash != target.IndexBlockInputHash
                || meshBlock.Index == vertexBlock.Index
                || meshBlock.Index == indexBlock.Index
                || vertexBlock.Index == indexBlock.Index)
            {
                throw Errors.Unsupported("TRANSFORM_GEOMETRY_BLOCK_INVALID", "A planned geometry target does not map to three distinct current mesh, vertex, and index blocks.", "Re-inspect the immutable model with a compatible adapter.");
            }
        }

        var duplicateTargetBlock = result.TargetBlocks.GroupBy(block => (block.Type, block.Index)).FirstOrDefault(group => group.Count() > 1);
        if (duplicateTargetBlock is not null || result.TargetBlocks.Any(block =>
            !blocksByIndex.TryGetValue(block.Index, out var actual)
            || !string.Equals(block.Type, actual.Type, StringComparison.Ordinal)
            || block.InputHash != actual.ContentHash))
        {
            throw Errors.Unsupported("TRANSFORM_TARGET_BLOCK_INVALID", "The geometry planner returned a duplicate, missing, or stale resource block target.", "Re-inspect the immutable resource and regenerate the transform plan.");
        }

        var selectedLocations = selected.Select(item => (item.Lod, item.ResourcePath, item.MeshOrdinal, item.ResourceBlockIndex)).Distinct().ToHashSet();
        if (result.GeometryTargets.Any(target => !selectedLocations.Contains((target.Lod, StableIdentity.NormalizePath(target.ResourcePath), target.MeshOrdinal, target.ResourceBlockIndex))))
        {
            throw Errors.Unsupported("TRANSFORM_GEOMETRY_LOCATION_INVALID", "The geometry planner returned a target outside the selected draw-call locations.", "Reject the adapter result and regenerate the plan.");
        }

        if (result.GeometryTargets.Select(target => target.FrozenPivot).Distinct().Count() != 1
            || result.GeometryTargets.Select(target => target.Codec).Distinct().Count() != 1)
        {
            throw Errors.Unsupported("TRANSFORM_CROSS_LOD_FACTS_DRIFT", "The geometry planner did not freeze one pivot and codec identity across every LOD.", "Re-inspect with one configured codec and regenerate the plan.");
        }

        if (result.DistanceFieldTargets.GroupBy(target => (target.ResourceBlockIndex, target.FieldIndex)).Any(group => group.Count() != 1)
            || result.DistanceFieldTargets.Any(target => !ValidateDistanceFieldTarget(
                target,
                operation,
                result.GeometryTargets[0].FrozenPivot,
                blocksByIndex)))
        {
            throw Errors.Unsupported("TRANSFORM_DISTANCE_FIELD_FACTS_INVALID", "The geometry planner returned duplicate, stale, non-finite, or recipe-inconsistent distance-field facts.", "Reject the adapter result and add a reviewed distance-field profile.");
        }

        var requiredTargetIndices = (operation.Version == 3
                ? result.GeometryTargets.Select(target => target.VertexResourceBlockIndex)
                : result.GeometryTargets.SelectMany(target => new[] { target.ResourceBlockIndex, target.VertexResourceBlockIndex }))
            .Concat(result.DistanceFieldTargets.Select(target => target.ResourceBlockIndex))
            .ToHashSet();
        if (!requiredTargetIndices.SetEquals(result.TargetBlocks.Select(block => block.Index)))
        {
            throw Errors.Unsupported("TRANSFORM_TARGET_BLOCK_SET_INVALID", "The transform block set contains an unexplained target or omits a required changed block.", "Reject the adapter result and regenerate a complete bounds-aware plan.");
        }
    }

    private static void ValidateCoupledTransformPlanningResult(
        TransformPlanningResult result,
        SelectedDrawCall[] selected,
        Dictionary<int, ResourceBlockSnapshot> blocksByIndex,
        TransformComponentOperation operation)
    {
        ArgumentNullException.ThrowIfNull(result);
        var coupled = result.CoupledTransformTarget;
        if (coupled is null
            || result.GeometryTargets is null || result.GeometryTargets.Count != 0
            || result.DistanceFieldTargets is null || result.DistanceFieldTargets.Count != 0
            || result.TargetBlocks is null || result.TargetBlocks.Count == 0
            || operation.Limits.MaximumCollisionDisplacement is not { } collisionLimit)
        {
            throw Errors.Unsupported("COUPLED_TRANSFORM_INCOMPLETE", "The adapter did not return one complete coupled visual/collision plan.", "Reject the plan and use a resource matching the published transform_component@2 profile.");
        }

        var visual = coupled.Visual;
        var collision = coupled.Collision;
        if (selected.Length != 1
            || selected[0].DrawCallId != visual.DrawCallId
            || visual.UniformScale != operation.Transform.UniformScale
            || collision.UniformScale != operation.Transform.UniformScale
            || visual.FrozenPivot != collision.FrozenPivot
            || visual.VertexCount != operation.ExpectedVerticesByLod.Values.SingleOrDefault()
            || visual.MaximumDisplacement <= 0f
            || visual.MaximumDisplacement > operation.Limits.MaximumVertexDisplacement
            || collision.MaximumDisplacement <= 0f
            || collision.MaximumDisplacement > collisionLimit
            || visual.DisplacementLimit != operation.Limits.MaximumVertexDisplacement
            || collision.DisplacementLimit != collisionLimit
            || !IsValidVector(visual.FrozenPivot)
            || !IsValidBounds(visual.BeforeBounds)
            || !IsValidBounds(visual.ExpectedAfterBounds)
            || !IsValidBounds(collision.Before.Bounds)
            || !IsValidBounds(collision.ExpectedAfter.Bounds)
            || visual.ExpectedDecodedVertexBufferHash == visual.DecodedVertexBufferHash
            || collision.ExpectedPositionHash == collision.PositionInputHash)
        {
            throw Errors.Unsupported("COUPLED_TRANSFORM_INCOMPLETE", "The coupled plan is inconsistent with the recipe, selection, limits, or expected transformed values.", "Regenerate the plan from an intact immutable input.");
        }

        var plannedBlocks = result.TargetBlocks.OrderBy(block => block.Index).ThenBy(block => block.Type, StringComparer.Ordinal).ToArray();
        var coupledBlocks = coupled.TargetBlocks.OrderBy(block => block.Index).ThenBy(block => block.Type, StringComparer.Ordinal).ToArray();
        if (!plannedBlocks.SequenceEqual(coupledBlocks)
            || plannedBlocks.GroupBy(block => block.Index).Any(group => group.Count() != 1)
            || plannedBlocks.Any(block => !blocksByIndex.TryGetValue(block.Index, out var actual)
                || actual.Type != block.Type
                || actual.ContentHash != block.InputHash))
        {
            throw Errors.Unsupported("TRANSFORM_TARGET_BLOCK_INVALID", "The coupled plan contains a missing, duplicate, stale, or unexplained target block.", "Re-inspect the immutable resource and regenerate the complete coupled plan.");
        }
    }

    private static bool ValidateDistanceFieldTarget(
        PlannedDistanceFieldTarget target,
        TransformComponentOperation operation,
        TransformVector3 frozenPivot,
        Dictionary<int, ResourceBlockSnapshot> blocksByIndex)
    {
        var expectedCellSize = target.GridCellSize * operation.Transform.UniformScale;
        var expectedMaximumDistance = target.MaximumQuantizedDistance * operation.Transform.UniformScale;
        var expectedSampleCount = (long)target.ResolutionX * target.ResolutionY * target.ResolutionZ;
        return blocksByIndex.ContainsKey(target.ResourceBlockIndex)
            && target.FieldIndex >= 0
            && target.ResolutionX > 0
            && target.ResolutionY > 0
            && target.ResolutionZ > 0
            && target.QuantizedDataLength > 0
            && IsValidHash(target.QuantizedDataHash)
            && IsValidBounds(target.BeforeBounds)
            && IsValidBounds(target.ExpectedAfterBounds)
            && float.IsFinite(target.GridCellSize)
            && target.GridCellSize > 0f
            && float.IsFinite(target.MaximumQuantizedDistance)
            && target.MaximumQuantizedDistance > 0f
            && float.IsFinite(target.SurfaceBias)
            && float.IsFinite(expectedCellSize)
            && float.IsFinite(expectedMaximumDistance)
            && target.ExpectedGridCellSize == expectedCellSize
            && target.ExpectedMaximumQuantizedDistance == expectedMaximumDistance
            && target.QuantizedDataLength == expectedSampleCount
            && HasExpectedTransformedBounds(
                target.BeforeBounds,
                target.ExpectedAfterBounds,
                frozenPivot,
                operation.Transform.UniformScale,
                operation.Transform.Translation);
    }

    private static bool ValidateBoneBoundsTarget(PlannedBoneBoundsTarget target, float scale) =>
        target.BoneIndex >= 0
        && !string.IsNullOrWhiteSpace(target.BoneName)
        && IsValidHash(target.InverseBindPoseHash)
        && IsValidHash(target.InfluencedVertexSetHash)
        && target.InfluencedVertexCount > 0
        && IsValidVector(target.BeforeCenter)
        && IsValidNonNegativeVector(target.BeforeSize)
        && IsValidBounds(target.BeforeBounds)
        && IsValidVector(target.ExpectedCenter)
        && IsValidNonNegativeVector(target.ExpectedSize)
        && IsValidBounds(target.ExpectedAfterBounds)
        && IsValidVector(target.LocalPivot)
        && IsValidVector(target.LocalTranslation)
        && float.IsFinite(target.SphereRadius)
        && target.SphereRadius >= 0f
        && float.IsFinite(target.ExpectedSphereRadius)
        && target.ExpectedSphereRadius >= 0f
        && HasExpectedCenterSizeTransform(
            target,
            target.LocalPivot,
            scale,
            target.LocalTranslation);

    private static bool HasExpectedCenterSizeTransform(
        PlannedBoneBoundsTarget target,
        TransformVector3 pivot,
        float scale,
        TransformVector3 translation)
    {
        try
        {
            var transform = new UniformTransform(ToPoint(pivot), scale, ToPoint(translation));
            var expectedCenter = transform.Apply(ToPoint(target.BeforeCenter));
            var expectedSize = new Point3(
                target.BeforeSize.X * scale,
                target.BeforeSize.Y * scale,
                target.BeforeSize.Z * scale);
            return HasIdenticalBits(target.BeforeBounds, BoundsFromCenterSize(target.BeforeCenter, target.BeforeSize))
                && HasIdenticalBits(target.ExpectedCenter, expectedCenter)
                && HasIdenticalBits(target.ExpectedSize, expectedSize)
                && HasIdenticalBits(target.ExpectedAfterBounds, BoundsFromCenterSize(target.ExpectedCenter, target.ExpectedSize));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static GeometryBounds BoundsFromCenterSize(TransformVector3 center, TransformVector3 size) => new(
        new TransformVector3
        {
            X = center.X - (size.X * 0.5f),
            Y = center.Y - (size.Y * 0.5f),
            Z = center.Z - (size.Z * 0.5f),
        },
        new TransformVector3
        {
            X = center.X + (size.X * 0.5f),
            Y = center.Y + (size.Y * 0.5f),
            Z = center.Z + (size.Z * 0.5f),
        });

    private static bool HasExpectedTransformedBounds(
        GeometryBounds before,
        GeometryBounds expectedAfter,
        TransformVector3 pivot,
        float scale,
        TransformVector3 translation)
    {
        try
        {
            var transform = new UniformTransform(ToPoint(pivot), scale, ToPoint(translation));
            var expectedMin = transform.Apply(ToPoint(before.Min));
            var expectedMax = transform.Apply(ToPoint(before.Max));
            return HasIdenticalBits(expectedAfter.Min, expectedMin)
                && HasIdenticalBits(expectedAfter.Max, expectedMax);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static Point3 ToPoint(TransformVector3 value) => new(value.X, value.Y, value.Z);

    private static bool HasIdenticalBits(TransformVector3 value, Point3 expected) =>
        BitConverter.SingleToInt32Bits(value.X) == BitConverter.SingleToInt32Bits(expected.X)
        && BitConverter.SingleToInt32Bits(value.Y) == BitConverter.SingleToInt32Bits(expected.Y)
        && BitConverter.SingleToInt32Bits(value.Z) == BitConverter.SingleToInt32Bits(expected.Z);

    private static bool HasIdenticalBits(GeometryBounds value, GeometryBounds expected) =>
        HasIdenticalBits(value.Min, ToPoint(expected.Min))
        && HasIdenticalBits(value.Max, ToPoint(expected.Max));

    private static bool IsValidHash(ContentHash hash) =>
        hash.Value is { Length: ContentHash.HexLength } value && value.All(Uri.IsHexDigit);

    private static bool IsValidBounds(GeometryBounds? bounds) =>
        bounds is not null
        && IsValidVector(bounds.Min)
        && IsValidVector(bounds.Max)
        && bounds.Min.X <= bounds.Max.X
        && bounds.Min.Y <= bounds.Max.Y
        && bounds.Min.Z <= bounds.Max.Z;

    private static bool IsValidVector(TransformVector3? vector) =>
        vector is not null
        && float.IsFinite(vector.X)
        && float.IsFinite(vector.Y)
        && float.IsFinite(vector.Z);

    private static bool IsValidNonNegativeVector(TransformVector3? vector) =>
        vector is not null
        && IsValidVector(vector)
        && vector.X >= 0f
        && vector.Y >= 0f
        && vector.Z >= 0f;

    private static void ValidateSnapshotIdentities(ModelSnapshot model)
    {
        var duplicate = model.Lods.SelectMany(lod => lod.Meshes).SelectMany(mesh => mesh.DrawCalls)
            .GroupBy(drawCall => drawCall.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw Errors.Unsupported("DRAW_CALL_ID_COLLISION", $"Stable draw-call ID '{duplicate.Key}' is not unique.", "Use a newer adapter identity profile for this layout.");
        }
    }

    private static Dictionary<int, ResourceBlockSnapshot> ValidateBlockReferences(ModelSnapshot model)
    {
        var duplicateBlockIndex = model.Artifact.Blocks.GroupBy(block => block.Index).FirstOrDefault(group => group.Count() > 1);
        if (duplicateBlockIndex is not null)
        {
            throw Errors.Unsupported("RESOURCE_BLOCK_INDEX_DUPLICATE", $"Resource block index {duplicateBlockIndex.Key} is duplicated.", "Use an intact adapter snapshot with one identity per resource block.");
        }

        var blocksByIndex = model.Artifact.Blocks.ToDictionary(block => block.Index);
        foreach (var mesh in model.Lods.SelectMany(lod => lod.Meshes))
        {
            if (!blocksByIndex.ContainsKey(mesh.ResourceBlockIndex))
            {
                throw Errors.Unsupported("MESH_BLOCK_REFERENCE_INVALID", $"Mesh {mesh.MeshOrdinal} references missing resource block {mesh.ResourceBlockIndex}.", "Re-inspect the input with a compatible Source 2 adapter.");
            }
        }

        return blocksByIndex;
    }

    private static int ParseLod(string value)
    {
        if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var lod) || lod < 0)
        {
            throw Errors.InvalidRecipe("LOD_EXPECTATION_INVALID", $"LOD key '{value}' is not a non-negative integer.", "Use numeric keys such as 0, 1, and 2.");
        }

        return lod;
    }

    private static PlannedInput[] CreateInputFingerprints(ModelSnapshot model, IReadOnlyList<ArtifactContent> dependencies)
    {
        var values = new List<PlannedInput>(dependencies.Count + 1)
        {
            new(model.Artifact.LogicalPath, model.Artifact.ContentHash, model.Artifact.Size),
        };
        foreach (var dependency in dependencies)
        {
            if (ContentHash.Compute(dependency.Bytes.Span) != dependency.ContentHash)
            {
                throw Errors.Input("DEPENDENCY_HASH_DRIFT", $"Dependency '{dependency.LogicalPath}' does not match its supplied content hash.", "Reload the immutable project graph before planning.");
            }

            values.Add(new PlannedInput(StableIdentity.NormalizePath(dependency.LogicalPath), dependency.ContentHash, dependency.Bytes.Length));
        }

        var duplicate = values.GroupBy(input => input.LogicalPath, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw Errors.Input("PROJECT_INPUT_PATH_DUPLICATE", $"Project input path '{duplicate.Key}' appears more than once.", "Recreate the project with an unambiguous resource graph.");
        }

        return values.OrderBy(input => input.LogicalPath, StringComparer.Ordinal).ToArray();
    }

    private static ContentHash ComputeFingerprint(
        RecipeDocument recipe,
        IReadOnlyList<PlannedInput> inputs,
        IReadOnlyList<PlannedOperation> operations)
    {
        var text = new StringBuilder();
        text.Append("recipe-v1\n").Append(recipe.RecipeId).Append('\n').Append(recipe.InputHash).Append('\n');
        foreach (var input in inputs)
        {
            text.Append("input|").Append(input.LogicalPath).Append('|').Append(input.ContentHash).Append('|').Append(input.Size).Append('\n');
        }

        foreach (var operation in recipe.Operations)
        {
            text.Append(operation.OperationId).Append('|').Append(operation.OperationKind).Append('|').Append(operation.Version).Append('|')
                .Append(operation.Granularity).Append('|').Append(operation.LodPolicy).Append('|').Append(operation.Selector.Kind).Append('|');
            if (operation.Selector.MaterialPath is not null)
            {
                text.Append(StableIdentity.NormalizePath(operation.Selector.MaterialPath));
            }

            if (operation.Selector.DrawCallIds is not null)
            {
                foreach (var id in operation.Selector.DrawCallIds.Order(StringComparer.Ordinal))
                {
                    text.Append(id).Append(',');
                }
            }

            foreach (var expectation in operation.ExpectedMatchesByLod.OrderBy(item => int.Parse(item.Key, System.Globalization.CultureInfo.InvariantCulture)))
            {
                text.Append('|').Append(expectation.Key).Append('=').Append(expectation.Value);
            }

            if (operation is TransformComponentOperation transform)
            {
                foreach (var expectation in transform.ExpectedVerticesByLod.OrderBy(item => int.Parse(item.Key, System.Globalization.CultureInfo.InvariantCulture)))
                {
                    text.Append("|vertices:").Append(expectation.Key).Append('=').Append(expectation.Value);
                }

                if (transform.ConnectedComponentIdsByLod is not null)
                {
                    foreach (var entry in transform.ConnectedComponentIdsByLod.OrderBy(item => int.Parse(item.Key, System.Globalization.CultureInfo.InvariantCulture)))
                    {
                        text.Append("|components:").Append(entry.Key).Append('=')
                            .Append(string.Join(',', entry.Value.Order(StringComparer.Ordinal)));
                    }
                }

                text.Append("|ownership=").Append(transform.OwnershipPolicy)
                    .Append("|physics-policy=").Append(transform.PhysicsPolicy ?? string.Empty)
                    .Append("|pivot=").Append(transform.Transform.Pivot.Kind).Append(':').Append(transform.Transform.Pivot.ReferenceLod)
                    .Append("|scale=").Append(FormatSingle(transform.Transform.UniformScale))
                    .Append("|translation=");
                AppendVector(text, transform.Transform.Translation);
                text.Append("|maximum-displacement=").Append(FormatSingle(transform.Limits.MaximumVertexDisplacement));
                if (transform.Limits.MaximumCollisionDisplacement is { } collisionLimit)
                {
                    text.Append("|maximum-collision-displacement=").Append(FormatSingle(collisionLimit));
                }
            }

            text.Append('\n');
            var plan = operations.Single(item => item.OperationId == operation.OperationId);
            foreach (var selected in plan.SelectedDrawCalls)
            {
                text.Append(selected.DrawCallId).Append('\n');
            }

            foreach (var block in plan.TargetBlocks.OrderBy(block => block.Index).ThenBy(block => block.Type, StringComparer.Ordinal))
            {
                text.Append("block|").Append(block.Index).Append('|').Append(block.Type).Append('|').Append(block.InputHash).Append('\n');
            }

            foreach (var target in plan.GeometryTargets.OrderBy(target => target.Lod).ThenBy(target => target.MeshOrdinal).ThenBy(target => target.VertexBufferOrdinal))
            {
                text.Append("geometry|").Append(target.Lod).Append('|').Append(target.ResourcePath).Append('|')
                    .Append(target.MeshOrdinal).Append('|').Append(target.ResourceBlockIndex).Append('|')
                    .Append(target.VertexBufferOrdinal).Append('|').Append(target.IndexBufferOrdinal).Append('|')
                    .Append(target.VertexResourceBlockIndex).Append('|').Append(target.IndexResourceBlockIndex).Append('|')
                    .Append(target.VertexBlockInputHash).Append('|').Append(target.IndexBlockInputHash).Append('|')
                    .Append(target.DecodedVertexBufferHash).Append('|').Append(target.ExpectedDecodedVertexBufferHash).Append('|')
                    .Append(target.DecodedIndexBufferHash).Append('|')
                    .Append(target.VertexSetHash).Append('|').Append(target.SelectedVertexCount).Append('|')
                    .Append(target.PositionLayout.Format).Append('|').Append(target.PositionLayout.Offset).Append('|').Append(target.PositionLayout.Stride).Append('|');
                AppendBounds(text, target.BeforeBounds);
                text.Append('|');
                AppendBounds(text, target.ExpectedAfterBounds);
                text.Append('|').Append(string.Join(',', target.ConnectedComponentIds.Order(StringComparer.Ordinal)));
                text.Append('|');
                AppendVector(text, target.FrozenPivot);
                text.Append('|').Append(FormatSingle(target.UniformScale)).Append('|');
                AppendVector(text, target.Translation);
                text.Append('|').Append(FormatSingle(target.MaximumDisplacement)).Append('|')
                    .Append(string.Join(',', target.AllowedChangedAttributes.Order(StringComparer.Ordinal))).Append('|')
                    .Append(target.Codec.Name).Append('|').Append(target.Codec.ApiProfile).Append('|')
                    .Append(target.Codec.Platform).Append('|').Append(target.Codec.BinaryHash).Append('|')
                    .Append(target.Codec.Version).Append('\n');

                foreach (var bone in target.BoneBoundsTargets.OrderBy(item => item.BoneIndex).ThenBy(item => item.BoneName, StringComparer.Ordinal))
                {
                    text.Append("bone-bounds|").Append(bone.BoneIndex).Append('|').Append(bone.BoneName).Append('|')
                        .Append(bone.InverseBindPoseHash).Append('|').Append(bone.InfluencedVertexSetHash).Append('|')
                        .Append(bone.InfluencedVertexCount).Append('|');
                    AppendVector(text, bone.BeforeCenter);
                    text.Append('|');
                    AppendVector(text, bone.BeforeSize);
                    text.Append('|');
                    AppendBounds(text, bone.BeforeBounds);
                    text.Append('|');
                    AppendVector(text, bone.ExpectedCenter);
                    text.Append('|');
                    AppendVector(text, bone.ExpectedSize);
                    text.Append('|');
                    AppendBounds(text, bone.ExpectedAfterBounds);
                    text.Append('|');
                    AppendVector(text, bone.LocalPivot);
                    text.Append('|');
                    AppendVector(text, bone.LocalTranslation);
                    text.Append('|').Append(FormatSingle(bone.SphereRadius)).Append('|')
                        .Append(FormatSingle(bone.ExpectedSphereRadius)).Append('\n');
                }
            }

            foreach (var target in plan.DistanceFieldTargets.OrderBy(target => target.ResourceBlockIndex).ThenBy(target => target.FieldIndex))
            {
                text.Append("distance-field|").Append(target.ResourceBlockIndex).Append('|').Append(target.FieldIndex).Append('|')
                    .Append(target.ParentBoneNameHash).Append('|').Append(target.BodyGroupIndex).Append('|').Append(target.BodyGroupChoice).Append('|');
                AppendBounds(text, target.BeforeBounds);
                text.Append('|');
                AppendBounds(text, target.ExpectedAfterBounds);
                text.Append('|').Append(target.ResolutionX).Append('|').Append(target.ResolutionY).Append('|').Append(target.ResolutionZ)
                    .Append('|').Append(FormatSingle(target.GridCellSize)).Append('|').Append(FormatSingle(target.ExpectedGridCellSize))
                    .Append('|').Append(FormatSingle(target.MaximumQuantizedDistance)).Append('|').Append(FormatSingle(target.ExpectedMaximumQuantizedDistance))
                    .Append('|').Append(FormatSingle(target.SurfaceBias)).Append('|').Append(target.IsTwoSided)
                    .Append('|').Append(target.IsFarFieldOnly).Append('|').Append(target.UseForOcclusion).Append('|').Append(target.UseForCollision)
                    .Append('|').Append(target.QuantizedDataHash).Append('|').Append(target.QuantizedDataLength).Append('\n');
            }

            if (plan.CoupledTransformTarget is { } coupled)
            {
                text.Append("coupled|").Append(JsonDefaults.Serialize(coupled)).Append('\n');
            }
        }

        return ContentHash.Compute(Encoding.UTF8.GetBytes(text.ToString()));
    }

    private static void AppendBounds(StringBuilder text, GeometryBounds bounds)
    {
        AppendVector(text, bounds.Min);
        text.Append("..");
        AppendVector(text, bounds.Max);
    }

    private static void AppendVector(StringBuilder text, TransformVector3 vector) =>
        text.Append(FormatSingle(vector.X)).Append(',').Append(FormatSingle(vector.Y)).Append(',').Append(FormatSingle(vector.Z));

    private static string FormatSingle(float value) =>
        value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

}
