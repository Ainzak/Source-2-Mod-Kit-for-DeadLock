using System.Buffers.Binary;
using System.Text;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Application;

/// <summary>Resolves typed affine pivots and frames from adapter-neutral immutable evidence.</summary>
public sealed class AffineEvidenceResolver : IAffineEvidenceResolver
{
    public ResolvedTransformPivot ResolvePivot(TypedPivotResolutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateExpectedLods(request.ExpectedLods);
        return request.Pivot switch
        {
            { Kind: "selection_bounds_center", ReferenceLod: not null, Point: null, Face: null, BoneName: null } => ResolveBoundsPivot(request, null),
            { Kind: "bounds_face", ReferenceLod: not null, Point: null, Face: not null, BoneName: null } => ResolveBoundsPivot(request, request.Pivot.Face),
            { Kind: "explicit_point", ReferenceLod: null, Point: not null, Face: null, BoneName: null } => ResolveExplicitPivot(request.Pivot),
            { Kind: "bone_origin", ReferenceLod: not null, Point: null, Face: null, BoneName: not null } => ResolveBonePivot(request),
            _ => throw Errors.Unsupported("AFFINE_PIVOT_UNSUPPORTED", $"Pivot kind '{request.Pivot.Kind}' is not supported.", "Use selection_bounds_center, explicit_point, bounds_face, or bone_origin."),
        };
    }

    public ResolvedTransformFrame ResolveFrame(TypedFrameResolutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateExpectedLods(request.ExpectedLods);
        if (request.Frame is { Kind: "model", BoneName: null })
        {
            var identity = ToContract(Matrix3.Identity);
            return new ResolvedTransformFrame(
                "model", null, identity, identity, "model", HashMatrix(Matrix3.Identity));
        }

        if (request.Frame.Kind != "bone_bind" || string.IsNullOrWhiteSpace(request.Frame.BoneName))
        {
            throw Errors.Unsupported("AFFINE_FRAME_UNSUPPORTED", $"Frame kind '{request.Frame.Kind}' is not supported.", "Use model or one exact named bone_bind frame.");
        }

        var bone = ResolveBoneEvidence(request.Frame.BoneName, request.ExpectedLods, request.BoneBindings, "AFFINE_FRAME");
        var inverseRotation = RotationOf(bone.InverseBindPose);
        RigidFrame rigid;
        try
        {
            rigid = new RigidFrame(inverseRotation.Transpose());
        }
        catch (ArgumentException exception)
        {
            throw new S2ModKitException(
                new S2Error("AFFINE_FRAME_UNSUPPORTED", "source2_adapter", "The named bone bind frame is not finite, rigid, orthonormal, and proper.", "Choose a rigid bone frame or the model frame.", ErrorCategory.UnsupportedCapability),
                exception);
        }

        return new ResolvedTransformFrame(
            "bone_bind",
            bone.BoneName,
            ToContract(rigid.ToModel),
            ToContract(rigid.ToFrame),
            $"bone:{bone.SkeletonIdentity}:{bone.BoneName}",
            bone.InverseBindPoseHash);
    }

    private static ResolvedTransformPivot ResolveBoundsPivot(TypedPivotResolutionRequest request, string? face)
    {
        if (request.Pivot.ReferenceLod is not { } lod)
        {
            throw Errors.Unsupported("AFFINE_PIVOT_UNSUPPORTED", "The selected pivot requires a reference LOD.", "Choose a present reference LOD.");
        }

        var matches = request.SelectionBounds.Where(item => item.Lod == lod).ToArray();
        if (matches.Length > 1)
        {
            throw Errors.Unsupported("AFFINE_PIVOT_AMBIGUOUS", $"Reference LOD {lod} has more than one selection-bounds record.", "Re-inspect the exact selected vertex set.");
        }

        if (matches.Length == 0 || !request.ExpectedLods.Contains(lod))
        {
            throw Errors.Unsupported("AFFINE_PIVOT_DRIFT", $"Reference LOD {lod} has no current selection-bounds evidence.", "Regenerate the recipe and plan from the current input.");
        }

        var evidence = matches[0];
        Point3 point;
        try
        {
            var bounds = ToBounds(evidence.Bounds);
            point = face is null ? bounds.Center : AffineTransform.ResolveBoundsFace(bounds, face);
        }
        catch (ArgumentException exception)
        {
            throw new S2ModKitException(
                new S2Error("AFFINE_PIVOT_DRIFT", "source2_adapter", "Selection-bounds pivot evidence is non-finite or malformed.", "Re-inspect the current selected vertices.", ErrorCategory.UnsupportedCapability),
                exception);
        }
        var kind = face is null ? "selection_bounds_center" : "bounds_face";
        var source = face is null ? $"selection:{lod}" : $"selection:{lod}:{face}";
        return new ResolvedTransformPivot(
            kind,
            ToVector(point),
            "model",
            source,
            HashBoundsEvidence(evidence, face),
            lod);
    }

    private static ResolvedTransformPivot ResolveExplicitPivot(TransformPivot pivot)
    {
        if (pivot.Point is not { } point)
        {
            throw Errors.Unsupported("AFFINE_PIVOT_UNSUPPORTED", "The explicit pivot has no point.", "Provide one finite model-space point.");
        }

        Point3 geometryPoint;
        try
        {
            geometryPoint = ToPoint(point);
        }
        catch (ArgumentException exception)
        {
            throw new S2ModKitException(
                new S2Error("AFFINE_PIVOT_UNSUPPORTED", "source2_adapter", "The explicit pivot point is non-finite.", "Provide one finite model-space point.", ErrorCategory.UnsupportedCapability),
                exception);
        }
        return new ResolvedTransformPivot(
            "explicit_point", point, "model", "explicit", HashPoint(geometryPoint), null);
    }

    private static ResolvedTransformPivot ResolveBonePivot(TypedPivotResolutionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Pivot.BoneName) || request.Pivot.ReferenceLod is not { } lod)
        {
            throw Errors.Unsupported("AFFINE_PIVOT_UNSUPPORTED", "Bone-origin pivot requires one exact bone name and reference LOD.", "Choose an influencing bone from current inspection evidence.");
        }

        if (!request.ExpectedLods.Contains(lod))
        {
            throw Errors.Unsupported("AFFINE_PIVOT_DRIFT", $"Reference LOD {lod} is not in the selected LOD set.", "Regenerate the recipe from current selection evidence.");
        }

        var bone = ResolveBoneEvidence(request.Pivot.BoneName, request.ExpectedLods, request.BoneBindings, "AFFINE_PIVOT");
        Matrix3 inverseRotation;
        try
        {
            inverseRotation = RotationOf(bone.InverseBindPose);
            _ = new RigidFrame(inverseRotation.Transpose());
        }
        catch (ArgumentException exception)
        {
            throw new S2ModKitException(
                new S2Error("AFFINE_PIVOT_UNSUPPORTED", "source2_adapter", "The named bone inverse bind matrix is not a supported rigid transform.", "Choose a rigid influencing bone or another pivot kind.", ErrorCategory.UnsupportedCapability),
                exception);
        }

        var translation = new Point3(bone.InverseBindPose.M14, bone.InverseBindPose.M24, bone.InverseBindPose.M34);
        var origin = inverseRotation.Transpose().Apply(new Point3(-translation.X, -translation.Y, -translation.Z));
        return new ResolvedTransformPivot(
            "bone_origin",
            ToVector(origin),
            "model",
            $"bone:{bone.SkeletonIdentity}:{bone.BoneName}",
            bone.InverseBindPoseHash,
            lod);
    }

    private static AffineBoneBindEvidence ResolveBoneEvidence(
        string boneName,
        IReadOnlyList<int> expectedLods,
        IReadOnlyList<AffineBoneBindEvidence> evidence,
        string codePrefix)
    {
        var matches = evidence.Where(item => string.Equals(item.BoneName, boneName, StringComparison.Ordinal))
            .OrderBy(item => item.Lod)
            .ToArray();
        if (matches.Length == 0)
        {
            throw Errors.Unsupported($"{codePrefix}_UNSUPPORTED", $"Bone '{boneName}' is absent from current affine evidence.", "Choose an exact influencing bone from current inspection evidence.");
        }

        if (matches.GroupBy(item => item.Lod).Any(group => group.Count() != 1))
        {
            throw Errors.Unsupported($"{codePrefix}_AMBIGUOUS", $"Bone '{boneName}' has duplicate evidence in one or more LODs.", "Re-inspect an unambiguous skeleton.");
        }

        if (!matches.Select(item => item.Lod).SequenceEqual(expectedLods.Order()))
        {
            throw Errors.Unsupported($"{codePrefix}_DRIFT", $"Bone '{boneName}' does not have complete selected-LOD coverage.", "Regenerate the plan from the current complete LOD set.");
        }

        if (matches.Any(item => !item.InfluencesSelection))
        {
            throw Errors.Unsupported($"{codePrefix}_UNSUPPORTED", $"Bone '{boneName}' does not influence the selection in every LOD.", "Choose a bone that influences every selected LOD.");
        }

        var first = matches[0];
        if (matches.Any(item => !string.Equals(item.SkeletonIdentity, first.SkeletonIdentity, StringComparison.Ordinal)
            || item.InverseBindPoseHash != first.InverseBindPoseHash
            || !HasIdenticalBits(item.InverseBindPose, first.InverseBindPose)
            || HashBindMatrix(item.InverseBindPose) != item.InverseBindPoseHash))
        {
            throw Errors.Unsupported($"{codePrefix}_DRIFT", $"Bone '{boneName}' bind evidence differs across LODs or from its frozen hash.", "Re-inspect one stable skeleton and regenerate the plan.");
        }

        return first;
    }

    private static void ValidateExpectedLods(IReadOnlyList<int> lods)
    {
        if (lods is null || lods.Count == 0 || lods.Any(lod => lod < 0) || lods.Distinct().Count() != lods.Count)
        {
            throw Errors.Unsupported("AFFINE_PIVOT_DRIFT", "Expected LOD evidence is empty, negative, or duplicated.", "Use one complete canonical selected-LOD set.");
        }
    }

    private static Matrix3 RotationOf(AffineBindMatrix value) => new(
        value.M11, value.M12, value.M13,
        value.M21, value.M22, value.M23,
        value.M31, value.M32, value.M33);

    private static TransformMatrix3 ToContract(Matrix3 value) => new(
        value.M11, value.M12, value.M13,
        value.M21, value.M22, value.M23,
        value.M31, value.M32, value.M33);

    private static Bounds3 ToBounds(GeometryBounds value) => new(ToPoint(value.Min), ToPoint(value.Max));
    private static Point3 ToPoint(TransformVector3 value) => new(value.X, value.Y, value.Z);
    private static TransformVector3 ToVector(Point3 value) => new() { X = value.X, Y = value.Y, Z = value.Z };

    private static ContentHash HashBoundsEvidence(AffineSelectionBoundsEvidence value, string? face) =>
        ContentHash.Compute(Encoding.UTF8.GetBytes($"affine-bounds-v1|{value.Lod}|{value.VertexSetHash}|{Bits(value.Bounds.Min.X)},{Bits(value.Bounds.Min.Y)},{Bits(value.Bounds.Min.Z)}|{Bits(value.Bounds.Max.X)},{Bits(value.Bounds.Max.Y)},{Bits(value.Bounds.Max.Z)}|{face ?? string.Empty}"));

    private static ContentHash HashPoint(Point3 value) =>
        ContentHash.Compute(Encoding.UTF8.GetBytes($"affine-point-v1|{Bits(value.X)},{Bits(value.Y)},{Bits(value.Z)}"));

    private static ContentHash HashMatrix(Matrix3 value) => ContentHash.Compute(Encoding.UTF8.GetBytes(
        $"affine-matrix3-v1|{Bits(value.M11)},{Bits(value.M12)},{Bits(value.M13)},{Bits(value.M21)},{Bits(value.M22)},{Bits(value.M23)},{Bits(value.M31)},{Bits(value.M32)},{Bits(value.M33)}"));

    public static ContentHash HashBindMatrix(AffineBindMatrix value)
    {
        Span<byte> bytes = stackalloc byte[12 * sizeof(int)];
        var values = new[] { value.M11, value.M12, value.M13, value.M14, value.M21, value.M22, value.M23, value.M24, value.M31, value.M32, value.M33, value.M34 };
        for (var index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes[(index * sizeof(int))..], BitConverter.SingleToInt32Bits(values[index]));
        }

        return ContentHash.Compute(bytes);
    }

    private static bool HasIdenticalBits(AffineBindMatrix left, AffineBindMatrix right) =>
        HashBindMatrix(left) == HashBindMatrix(right);

    private static string Bits(float value) => BitConverter.SingleToInt32Bits(value).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
}
