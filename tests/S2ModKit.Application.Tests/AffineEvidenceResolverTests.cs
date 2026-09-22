using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Application.Tests;

public sealed class AffineEvidenceResolverTests
{
    private readonly AffineEvidenceResolver resolver = new();

    [Fact]
    public void ResolvesSelectionCenterAndBoundsFaceFromExactReferenceLod()
    {
        var evidence = new[]
        {
            Bounds(1, -4, -2, 0, 4, 6, 8),
            Bounds(0, -2, 0, 1, 2, 4, 5),
        };

        var center = resolver.ResolvePivot(new TypedPivotResolutionRequest(
            new TransformPivot { Kind = "selection_bounds_center", ReferenceLod = 0 },
            [0, 1], evidence, []));
        var face = resolver.ResolvePivot(new TypedPivotResolutionRequest(
            new TransformPivot { Kind = "bounds_face", ReferenceLod = 1, Face = "max_z" },
            [0, 1], evidence.Reverse().ToArray(), []));

        Assert.Equal(new TransformVector3 { X = 0, Y = 2, Z = 3 }, center.Point);
        Assert.Equal(new TransformVector3 { X = 0, Y = 2, Z = 8 }, face.Point);
        Assert.Equal("model", center.CoordinateSpace);
        Assert.Equal(0, center.ReferenceLod);
        Assert.Equal(1, face.ReferenceLod);
    }

    [Fact]
    public void ResolvesExplicitPointWithoutReferenceLod()
    {
        var point = new TransformVector3 { X = 1.5f, Y = -2, Z = 7 };

        var resolved = resolver.ResolvePivot(new TypedPivotResolutionRequest(
            new TransformPivot { Kind = "explicit_point", Point = point }, [0], [], []));

        Assert.Equal(point, resolved.Point);
        Assert.Null(resolved.ReferenceLod);
        Assert.Equal("explicit", resolved.SourceIdentity);
    }

    [Fact]
    public void ResolvesBoneOriginAndFrameIndependentlyOfEnumerationOrder()
    {
        var matrix = new AffineBindMatrix(1, 0, 0, -5, 0, 1, 0, 2, 0, 0, 1, -3);
        var bones = BoneEvidence(matrix).Reverse().ToArray();

        var pivot = resolver.ResolvePivot(new TypedPivotResolutionRequest(
            new TransformPivot { Kind = "bone_origin", ReferenceLod = 0, BoneName = "weapon" },
            [0, 1], [], bones));
        var frame = resolver.ResolveFrame(new TypedFrameResolutionRequest(
            new TransformFrame { Kind = "bone_bind", BoneName = "weapon" },
            [0, 1], bones));

        Assert.Equal(new TransformVector3 { X = 5, Y = -2, Z = 3 }, pivot.Point);
        Assert.Equal("bone_bind", frame.Kind);
        Assert.Equal("weapon", frame.BoneName);
        Assert.Equal(frame.ToModel, frame.ToFrame);
        Assert.Equal(AffineEvidenceResolver.HashBindMatrix(matrix), pivot.SourceHash);
    }

    [Fact]
    public void ModelFrameIsCanonical()
    {
        var first = resolver.ResolveFrame(new TypedFrameResolutionRequest(new TransformFrame { Kind = "model" }, [1, 0], []));
        var second = resolver.ResolveFrame(new TypedFrameResolutionRequest(new TransformFrame { Kind = "model" }, [0, 1], []));

        Assert.Equal(first, second);
        Assert.Equal(new TransformMatrix3(1, 0, 0, 0, 1, 0, 0, 0, 1), first.ToModel);
    }

    [Fact]
    public void MissingDuplicateAndDriftedEvidenceFailWithStableReasons()
    {
        var pivot = new TransformPivot { Kind = "bone_origin", ReferenceLod = 0, BoneName = "weapon" };
        var missing = Assert.Throws<S2ModKitException>(() => resolver.ResolvePivot(
            new TypedPivotResolutionRequest(pivot, [0, 1], [], [])));

        var matrix = new AffineBindMatrix(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0);
        var normal = BoneEvidence(matrix);
        var duplicate = Assert.Throws<S2ModKitException>(() => resolver.ResolvePivot(
            new TypedPivotResolutionRequest(pivot, [0, 1], [], [normal[0], normal[0], normal[1]])));
        var crossSkeleton = Assert.Throws<S2ModKitException>(() => resolver.ResolvePivot(
            new TypedPivotResolutionRequest(pivot, [0, 1], [], [normal[0], normal[1] with { SkeletonIdentity = "other" }])));

        Assert.Equal("AFFINE_PIVOT_UNSUPPORTED", missing.Error.Code);
        Assert.Equal("AFFINE_PIVOT_AMBIGUOUS", duplicate.Error.Code);
        Assert.Equal("AFFINE_PIVOT_DRIFT", crossSkeleton.Error.Code);
    }

    [Fact]
    public void StaleMatrixHashAndNonRigidFrameFailClosed()
    {
        var rigid = new AffineBindMatrix(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0);
        var stale = BoneEvidence(rigid).Select(item => item with { InverseBindPoseHash = ContentHash.Compute("stale"u8) }).ToArray();
        var drift = Assert.Throws<S2ModKitException>(() => resolver.ResolveFrame(
            new TypedFrameResolutionRequest(new TransformFrame { Kind = "bone_bind", BoneName = "weapon" }, [0, 1], stale)));

        var scaled = new AffineBindMatrix(2, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0);
        var unsupported = Assert.Throws<S2ModKitException>(() => resolver.ResolveFrame(
            new TypedFrameResolutionRequest(new TransformFrame { Kind = "bone_bind", BoneName = "weapon" }, [0, 1], BoneEvidence(scaled))));

        Assert.Equal("AFFINE_FRAME_DRIFT", drift.Error.Code);
        Assert.Equal("AFFINE_FRAME_UNSUPPORTED", unsupported.Error.Code);
    }

    [Fact]
    public void UnsupportedAttachmentNeverFallsBack()
    {
        var exception = Assert.Throws<S2ModKitException>(() => resolver.ResolvePivot(
            new TypedPivotResolutionRequest(new TransformPivot { Kind = "attachment" }, [0], [Bounds(0, 0, 0, 0, 1, 1, 1)], [])));

        Assert.Equal("AFFINE_PIVOT_UNSUPPORTED", exception.Error.Code);
    }

    private static AffineSelectionBoundsEvidence Bounds(
        int lod, float minX, float minY, float minZ, float maxX, float maxY, float maxZ) => new(
            lod,
            new GeometryBounds(
                new TransformVector3 { X = minX, Y = minY, Z = minZ },
                new TransformVector3 { X = maxX, Y = maxY, Z = maxZ }),
            ContentHash.Compute(System.Text.Encoding.UTF8.GetBytes($"vertices-{lod}")));

    private static AffineBoneBindEvidence[] BoneEvidence(AffineBindMatrix matrix)
    {
        var hash = AffineEvidenceResolver.HashBindMatrix(matrix);
        return
        [
            new AffineBoneBindEvidence(0, "skeleton", "weapon", true, hash, matrix),
            new AffineBoneBindEvidence(1, "skeleton", "weapon", true, hash, matrix),
        ];
    }
}
