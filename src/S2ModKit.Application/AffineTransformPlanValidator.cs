using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Application;

internal static class AffineTransformPlanValidator
{
    public static void Validate(
        TransformPlanningResult result,
        IReadOnlyList<SelectedDrawCall> selected,
        IReadOnlyDictionary<int, ResourceBlockSnapshot> blocksByIndex,
        TransformComponentOperation operation)
    {
        ArgumentNullException.ThrowIfNull(result);
        var target = result.AffineTransformTarget;
        if (target is null
            || result.GeometryTargets.Count != 0
            || result.DistanceFieldTargets.Count != 0
            || result.CoupledTransformTarget is not null
            || target.GeometryTargets is null
            || target.GeometryTargets.Count == 0)
        {
            throw Errors.Unsupported("TRANSFORM_PLAN_INCOMPLETE", "The affine planner returned an incomplete or mixed-version plan.", "Return exactly one version-4 affine target and no legacy geometry or coupled targets.");
        }

        if (target.SelectionKind != operation.Granularity
            || string.IsNullOrWhiteSpace(target.StructuralProfileId)
            || target.StructuralProfileVersion < 1
            || target.Pivot.Kind != operation.Transform.Pivot.Kind
            || target.Pivot.CoordinateSpace != "model"
            || target.Pivot.ReferenceLod != operation.Transform.Pivot.ReferenceLod
            || target.Frame.Kind != operation.Transform.Frame!.Kind
            || !string.Equals(target.Frame.BoneName, operation.Transform.Frame.BoneName, StringComparison.Ordinal)
            || target.Scale != operation.Transform.Scale
            || target.Rotation != operation.Transform.Rotation
            || target.Translation != operation.Transform.Translation
            || !IsValidHash(target.Pivot.SourceHash)
            || !IsValidHash(target.Frame.SourceHash)
            || string.IsNullOrWhiteSpace(target.Pivot.SourceIdentity)
            || string.IsNullOrWhiteSpace(target.Frame.SourceIdentity))
        {
            throw Errors.Unsupported("AFFINE_RESULT_DRIFT", "The affine plan does not match the typed recipe intent and resolved evidence.", "Re-resolve the current pivot, frame, and transform from immutable input evidence.");
        }

        if (operation.Transform.Pivot.Kind == "explicit_point" && target.Pivot.Point != operation.Transform.Pivot.Point)
        {
            throw Errors.Unsupported("AFFINE_PIVOT_DRIFT", "The resolved explicit pivot differs from the recipe point.", "Regenerate the plan without changing the typed pivot.");
        }

        var expectedLods = operation.ExpectedVerticesByLod.Keys.Select(ParseLod).Order().ToArray();
        var resolver = new AffineEvidenceResolver();
        if (operation.Transform.Pivot.Kind == "explicit_point"
            && target.Pivot != resolver.ResolvePivot(new TypedPivotResolutionRequest(operation.Transform.Pivot, expectedLods, [], [])))
        {
            throw Errors.Unsupported("AFFINE_PIVOT_DRIFT", "The resolved explicit pivot identity or hash differs from its canonical recipe point.", "Regenerate the plan without changing the typed pivot.");
        }

        if (operation.Transform.Frame!.Kind == "model"
            && target.Frame != resolver.ResolveFrame(new TypedFrameResolutionRequest(operation.Transform.Frame, expectedLods, [])))
        {
            throw Errors.Unsupported("AFFINE_FRAME_DRIFT", "The resolved model frame differs from its canonical identity evidence.", "Regenerate the affine plan from the current typed frame.");
        }

        ValidateMath(target);
        ValidateGeometry(target, selected, blocksByIndex, operation);
        ValidateTargetBlocks(result.TargetBlocks, blocksByIndex);
    }

    private static void ValidateGeometry(
        PlannedAffineTransformTarget target,
        IReadOnlyList<SelectedDrawCall> selected,
        IReadOnlyDictionary<int, ResourceBlockSnapshot> blocksByIndex,
        TransformComponentOperation operation)
    {
        var positionOnly = target.Rotation.Kind == "identity"
            && target.Scale.X == target.Scale.Y
            && target.Scale.Y == target.Scale.Z;
        var expectedAttributes = positionOnly
            ? new[] { "position" }
            : ["normal_tangent", "position"];
        var expectedLods = operation.ExpectedVerticesByLod.Keys.Select(ParseLod).Order().ToArray();
        var actualLods = target.GeometryTargets.Select(item => item.Lod).Distinct().Order().ToArray();
        if (!actualLods.SequenceEqual(expectedLods))
        {
            throw Errors.Selection("TRANSFORM_GEOMETRY_LOD_COVERAGE_INCOMPLETE", "The affine plan does not cover every declared LOD.", "Return complete affine geometry evidence for every present LOD.");
        }

        foreach (var lod in expectedLods)
        {
            var expected = operation.ExpectedVerticesByLod[lod.ToString(System.Globalization.CultureInfo.InvariantCulture)];
            var actual = target.GeometryTargets.Where(item => item.Lod == lod).Sum(item => item.SelectedVertexCount);
            if (actual != expected)
            {
                throw Errors.Selection("VERTEX_CARDINALITY_MISMATCH", $"Affine plan selected {actual} vertices in LOD {lod}; expected {expected}.", "Regenerate the plan from current selection evidence.");
            }
        }

        var selectedLocations = selected.Select(item => (item.Lod, item.ResourcePath, item.MeshOrdinal, item.ResourceBlockIndex)).Distinct().ToHashSet();
        foreach (var geometry in target.GeometryTargets)
        {
            if (!selectedLocations.Contains((geometry.Lod, StableIdentity.NormalizePath(geometry.ResourcePath), geometry.MeshOrdinal, geometry.ResourceBlockIndex))
                || geometry.SelectedVertexCount < 1
                || !IsValidHash(geometry.VertexBlockInputHash)
                || !IsValidHash(geometry.IndexBlockInputHash)
                || !IsValidHash(geometry.DecodedVertexBufferHash)
                || !IsValidHash(geometry.ExpectedDecodedVertexBufferHash)
                || geometry.DecodedVertexBufferHash == geometry.ExpectedDecodedVertexBufferHash
                || !IsValidHash(geometry.DecodedIndexBufferHash)
                || !IsValidHash(geometry.VertexSetHash)
                || !IsValidHash(geometry.InputPackedFrameHash)
                || !IsValidHash(geometry.ExpectedPackedFrameHash)
                || (positionOnly
                    ? geometry.InputPackedFrameHash != geometry.ExpectedPackedFrameHash
                    : geometry.InputPackedFrameHash == geometry.ExpectedPackedFrameHash)
                || geometry.PositionLayout is not { Format: "R32G32B32_FLOAT" }
                || geometry.PackedFrameLayout is not { Format: "R32_UINT" }
                || string.IsNullOrWhiteSpace(geometry.PackedFrameLayout.EncodingProfile)
                || !IsValidBounds(geometry.SelectionBeforeBounds)
                || !IsValidBounds(geometry.SelectionExpectedAfterBounds)
                || !IsValidBounds(geometry.MeshBeforeBounds)
                || !IsValidBounds(geometry.MeshExpectedAfterBounds)
                || !float.IsFinite(geometry.MaximumDisplacement)
                || geometry.MaximumDisplacement <= 0f
                || geometry.MaximumDisplacement > target.DisplacementLimit
                || !geometry.AllowedChangedAttributes.SequenceEqual(expectedAttributes, StringComparer.Ordinal)
                || geometry.BoneBoundsTargets.Count == 0
                || geometry.Codec is null)
            {
                throw Errors.Unsupported("AFFINE_LAYOUT_UNSUPPORTED", "The affine planner returned malformed, stale, or unsupported geometry facts.", "Use the characterized root MVTX/MIDX affine profile and regenerate the plan.");
            }

            if (!blocksByIndex.TryGetValue(geometry.VertexResourceBlockIndex, out var vertexBlock)
                || vertexBlock.ContentHash != geometry.VertexBlockInputHash
                || !blocksByIndex.TryGetValue(geometry.IndexResourceBlockIndex, out var indexBlock)
                || indexBlock.ContentHash != geometry.IndexBlockInputHash)
            {
                throw Errors.Unsupported("AFFINE_RESULT_DRIFT", "An affine buffer identity or input hash drifted.", "Re-inspect the immutable input and regenerate the plan.");
            }
        }

        if (!target.GeometryTargets.SequenceEqual(target.GeometryTargets
                .OrderBy(item => item.Lod)
                .ThenBy(item => item.ResourcePath, StringComparer.Ordinal)
                .ThenBy(item => item.MeshOrdinal)
                .ThenBy(item => item.VertexBufferOrdinal))
            || target.GeometryTargets.GroupBy(item => (item.Lod, item.ResourcePath, item.MeshOrdinal, item.VertexBufferOrdinal)).Any(group => group.Count() != 1))
        {
            throw Errors.Unsupported("AFFINE_MULTI_BUFFER_OWNERSHIP_UNSUPPORTED", "Affine geometry targets are duplicated or not in canonical order.", "Return one canonical target per owned vertex buffer.");
        }
    }

    private static void ValidateMath(PlannedAffineTransformTarget target)
    {
        try
        {
            var toModel = ToMatrix(target.Frame.ToModel);
            var frame = new RigidFrame(toModel);
            if (!HasIdenticalBits(frame.ToFrame, ToMatrix(target.Frame.ToFrame)))
            {
                throw new ArgumentException("Frame inverse differs from its transpose.");
            }

            var rotation = target.Rotation.Kind == "identity"
                ? AxisAngleRotation.Identity
                : new AxisAngleRotation(ToPoint(target.Rotation.Axis!), target.Rotation.Degrees!.Value);
            var transform = new AffineTransform(
                ToPoint(target.Pivot.Point),
                new AffineScale(target.Scale.X, target.Scale.Y, target.Scale.Z),
                rotation,
                frame,
                ToPoint(target.Translation));
            if (transform.IsIdentity
                || !HasIdenticalBits(transform.LinearMap, ToMatrix(target.LinearMap))
                || !float.IsFinite(target.MaximumDisplacement)
                || target.MaximumDisplacement <= 0f
                || !float.IsFinite(target.DisplacementLimit)
                || target.DisplacementLimit <= 0f
                || target.DisplacementLimit > RecipeValidator.MaximumTransformDisplacement
                || target.MaximumDisplacement > target.DisplacementLimit)
            {
                throw new ArgumentException("Affine matrix or displacement facts do not match the intent.");
            }
        }
        catch (ArgumentException exception)
        {
            throw new S2ModKitException(
                new S2Error("AFFINE_RESULT_DRIFT", "source2_adapter", "The resolved affine matrix, frame, or displacement facts are invalid.", "Recompute the affine plan from the typed intent and frozen evidence.", ErrorCategory.UnsupportedCapability),
                exception);
        }
    }

    private static void ValidateTargetBlocks(
        IReadOnlyList<PlannedTargetBlock> targets,
        IReadOnlyDictionary<int, ResourceBlockSnapshot> blocksByIndex)
    {
        var ordered = targets.OrderBy(item => item.Index).ThenBy(item => item.Type, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0
            || ordered.GroupBy(item => (item.Index, item.Type)).Any(group => group.Count() != 1)
            || ordered.Any(item => !blocksByIndex.TryGetValue(item.Index, out var block)
                || item.Type != block.Type || item.InputHash != block.ContentHash))
        {
            throw Errors.Unsupported("TRANSFORM_TARGET_BLOCK_INVALID", "The affine target-block set is missing, duplicated, stale, or unexplained.", "Regenerate a complete affine plan from the current resource envelope.");
        }
    }

    private static int ParseLod(string value) =>
        int.Parse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);

    private static bool IsValidHash(ContentHash hash) =>
        hash.Value is { Length: ContentHash.HexLength } value && value.All(Uri.IsHexDigit);

    private static bool IsValidBounds(GeometryBounds? bounds) =>
        bounds is not null
        && IsValidVector(bounds.Min) && IsValidVector(bounds.Max)
        && bounds.Min.X <= bounds.Max.X && bounds.Min.Y <= bounds.Max.Y && bounds.Min.Z <= bounds.Max.Z;

    private static bool IsValidVector(TransformVector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static Point3 ToPoint(TransformVector3 value) => new(value.X, value.Y, value.Z);

    private static Matrix3 ToMatrix(TransformMatrix3 value) => new(
        value.M11, value.M12, value.M13, value.M21, value.M22, value.M23, value.M31, value.M32, value.M33);

    private static bool HasIdenticalBits(Matrix3 left, Matrix3 right) =>
        BitConverter.SingleToInt32Bits(left.M11) == BitConverter.SingleToInt32Bits(right.M11)
        && BitConverter.SingleToInt32Bits(left.M12) == BitConverter.SingleToInt32Bits(right.M12)
        && BitConverter.SingleToInt32Bits(left.M13) == BitConverter.SingleToInt32Bits(right.M13)
        && BitConverter.SingleToInt32Bits(left.M21) == BitConverter.SingleToInt32Bits(right.M21)
        && BitConverter.SingleToInt32Bits(left.M22) == BitConverter.SingleToInt32Bits(right.M22)
        && BitConverter.SingleToInt32Bits(left.M23) == BitConverter.SingleToInt32Bits(right.M23)
        && BitConverter.SingleToInt32Bits(left.M31) == BitConverter.SingleToInt32Bits(right.M31)
        && BitConverter.SingleToInt32Bits(left.M32) == BitConverter.SingleToInt32Bits(right.M32)
        && BitConverter.SingleToInt32Bits(left.M33) == BitConverter.SingleToInt32Bits(right.M33);
}
