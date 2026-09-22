using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Utils;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    internal TransformPlanningResult PlanTransform(TransformPlanningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return PlanTransformCore(request);
        }
        catch (S2ModKitException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidDataException
            or InvalidOperationException
            or OverflowException
            or IndexOutOfRangeException
            or KeyNotFoundException)
        {
            throw new S2ModKitException(
                new S2Error(
                    "TRANSFORM_SOURCE2_PROFILE_UNSUPPORTED",
                    "source2_adapter",
                    $"The selected component does not satisfy the supported Source 2 transform profile: {exception.Message}",
                    "Keep all LOD expectations explicit and select every draw call of one complete, independently bounded mesh per LOD.",
                    ErrorCategory.UnsupportedCapability),
                exception);
        }
    }

    internal PlannedCoupledTransformTarget PlanCoupledTransform(
        ArtifactContent input,
        ModelSnapshot model,
        TransformVector3 pivot,
        float uniformScale,
        float maximumVertexDisplacement,
        float maximumCollisionDisplacement)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(pivot);
        using var parsed = Parse(input, retainGeometryAnalysis: true);
        ValidateSnapshotAgreement(model, parsed.Snapshot);
        var mesh = parsed.MeshesByOrdinal.Values.SingleOrDefault();
        if (mesh?.RawMbufAnalysis is null
            || mesh.PhysicsAnalysis is null
            || mesh.WholeMeshTransformAnalysis is null)
        {
            throw Errors.Unsupported(
                "COUPLED_TRANSFORM_INCOMPLETE",
                "The resource does not expose both predictable raw-MBUF and convex-PHYS halves.",
                "Use an intact resource matching the accepted coupled profile.");
        }

        var mbufIndex = mesh.RawMbufAnalysis.Geometry.VertexBuffers[0].Snapshot.ResourceBlockIndex;
        return Source2CoupledTransformPlanner.Plan(
            mesh.BlockIndex,
            ContentHash.Compute(parsed.Envelope.Blocks[mesh.BlockIndex].Payload.Span),
            parsed.Envelope.Blocks[mbufIndex],
            mesh.RawMbufAnalysis,
            mesh.WholeMeshTransformAnalysis,
            mesh.PhysicsAnalysis,
            new Point3(pivot.X, pivot.Y, pivot.Z),
            uniformScale,
            maximumVertexDisplacement,
            maximumCollisionDisplacement);
    }

    private TransformPlanningResult PlanTransformCore(TransformPlanningRequest request)
    {
        if (request.Operation is null || request.Model is null || request.Input is null || request.SelectedDrawCalls is null)
        {
            throw new ArgumentException("A complete transform planning request is required.", nameof(request));
        }

        if (ContentHash.Compute(request.Input.Bytes.Span) != request.Input.ContentHash
            || request.Input.ContentHash != request.Model.Artifact.ContentHash)
        {
            throw Errors.Input("INPUT_HASH_DRIFT", "The Source 2 transform request no longer matches its immutable input.", "Reload the input and regenerate the plan.");
        }

        using var parsed = Parse(request.Input, retainGeometryAnalysis: true);
        ValidateSnapshotAgreement(request.Model, parsed.Snapshot);
        var selectedIds = request.SelectedDrawCalls.Select(item => item.DrawCallId).ToHashSet(StringComparer.Ordinal);
        if (selectedIds.Count != request.SelectedDrawCalls.Count || selectedIds.Count == 0)
        {
            throw Errors.Selection("TRANSFORM_SELECTION_INVALID", "The transform selection is empty or contains duplicate draw-call identities.", "Regenerate a non-empty canonical selection.");
        }

        if (request.Operation.Version == 4)
        {
            return PlanAffineTransform(request, parsed);
        }

        if (request.Operation.Version == 2)
        {
            var mesh = parsed.MeshesByOrdinal.Values.SingleOrDefault();
            if (mesh?.RawMbufAnalysis is null
                || mesh.PhysicsAnalysis is null
                || mesh.WholeMeshTransformAnalysis is null
                || request.SelectedDrawCalls.Count != 1
                || request.SelectedDrawCalls[0].DrawCallId != mesh.RawMbufAnalysis.DrawCall.Id
                || request.Operation.ExpectedVerticesByLod.Count != 1
                || !request.Operation.ExpectedVerticesByLod.TryGetValue(mesh.Lod.ToString(CultureInfo.InvariantCulture), out var expectedVertices)
                || expectedVertices != mesh.WholeMeshTransformAnalysis.VertexCount
                || request.Operation.Transform.Pivot.ReferenceLod != mesh.Lod
                || request.Operation.Limits.MaximumCollisionDisplacement is not { } collisionLimit)
            {
                throw Errors.Unsupported(
                    "COUPLED_TRANSFORM_INCOMPLETE",
                    "The selected component does not expose the complete raw-MBUF/convex-PHYS profile required by transform_component@2.",
                    "Select the sole complete coupled component reported as available by current discovery.");
            }

            var coupledPivot = Center(mesh.WholeMeshTransformAnalysis.SceneBounds);
            var mbufIndex = mesh.RawMbufAnalysis.Geometry.VertexBuffers[0].Snapshot.ResourceBlockIndex;
            var coupled = Source2CoupledTransformPlanner.Plan(
                mesh.BlockIndex,
                ContentHash.Compute(parsed.Envelope.Blocks[mesh.BlockIndex].Payload.Span),
                parsed.Envelope.Blocks[mbufIndex],
                mesh.RawMbufAnalysis,
                mesh.WholeMeshTransformAnalysis,
                mesh.PhysicsAnalysis,
                ToPoint(coupledPivot),
                request.Operation.Transform.UniformScale,
                request.Operation.Limits.MaximumVertexDisplacement,
                collisionLimit);
            return new TransformPlanningResult([], [], coupled.TargetBlocks)
            {
                CoupledTransformTarget = coupled,
            };
        }

        if (request.Operation.Version == 3)
        {
            return PlanConnectedComponentTransform(request, parsed);
        }

        var profiles = new List<TransformMeshProfile>();
        foreach (var group in request.SelectedDrawCalls.GroupBy(item => item.MeshOrdinal).OrderBy(group => group.Key))
        {
            if (!parsed.MeshesByOrdinal.TryGetValue(group.Key, out var mesh)
                || group.Any(item => item.Lod != mesh.Lod
                    || item.ResourceBlockIndex != mesh.BlockIndex
                    || !string.Equals(
                        StableIdentity.NormalizePath(item.ResourcePath),
                        StableIdentity.NormalizePath(request.Input.LogicalPath),
                        StringComparison.Ordinal)))
            {
                throw Errors.Verification("TRANSFORM_SELECTION_LOCATION_DRIFT", $"Selected mesh {group.Key} no longer matches its planned resource, LOD, or MDAT block.", "Re-inspect the immutable input and regenerate the plan.");
            }

            var groupIds = group.Select(item => item.DrawCallId).ToHashSet(StringComparer.Ordinal);
            if (!groupIds.SetEquals(mesh.DrawCalls.Select(item => item.Snapshot.Id)))
            {
                throw Errors.Unsupported(
                    "TRANSFORM_PARTIAL_MESH_UNSUPPORTED",
                    $"Embedded mesh {mesh.MeshOrdinal} is only partially selected; the first transform profile requires every draw call in its independently bounded scene.",
                    "Select all component draw calls in this mesh or wait for a characterized partial-scene bounds profile.");
            }

            if (mesh.GeometryAnalysis is null || mesh.Geometry.Codec is null)
            {
                throw Errors.Unsupported("TRANSFORM_GEOMETRY_UNAVAILABLE", $"Embedded mesh {mesh.MeshOrdinal} has no retained decoded geometry.", "Configure the exact meshoptimizer codec reported by s2mod doctor and re-inspect the model.");
            }

            var metadata = Source2TransformMetadataAnalyzer.AnalyzeWholeMesh(
                mesh.Descriptor,
                mesh.Block.Data,
                mesh.GeometryAnalysis,
                $"embedded mesh {mesh.MeshOrdinal}");
            profiles.Add(new TransformMeshProfile(mesh, metadata));
        }

        var expectedLods = request.Operation.ExpectedVerticesByLod.Keys.Select(ParseCanonicalLod).Order().ToArray();
        var actualLods = profiles.Select(profile => profile.Mesh.Lod).Order().ToArray();
        if (!actualLods.SequenceEqual(expectedLods) || profiles.GroupBy(profile => profile.Mesh.Lod).Any(group => group.Count() != 1))
        {
            throw Errors.Selection("TRANSFORM_GEOMETRY_LOD_COVERAGE_INCOMPLETE", "The selected Source 2 meshes do not map to exactly one complete vertex buffer per declared LOD.", "Select one semantically equivalent complete component mesh in every present LOD.");
        }

        var roots = profiles.Select(profile => profile.Metadata.LocalSkinningRootBone).Distinct(StringComparer.Ordinal).ToArray();
        if (roots.Length != 1)
        {
            throw Errors.Unsupported("TRANSFORM_SKINNING_ROOT_DRIFT", $"Selected LOD meshes use different local skinning roots: {string.Join(", ", roots)}.", "Use a component whose mesh-local skeleton ancestry is stable across LODs.");
        }

        var reference = profiles.Single(profile => profile.Mesh.Lod == request.Operation.Transform.Pivot.ReferenceLod);
        var pivot = Center(reference.Metadata.SceneBounds);
        var transform = new UniformTransform(
            ToPoint(pivot),
            request.Operation.Transform.UniformScale,
            ToPoint(request.Operation.Transform.Translation));
        var targets = new List<PlannedGeometryTarget>(profiles.Count);
        var targetBlocks = new Dictionary<(string Type, int Index), PlannedTargetBlock>();
        foreach (var profile in profiles.OrderBy(profile => profile.Mesh.Lod))
        {
            var geometry = profile.Mesh.GeometryAnalysis!;
            var vertices = geometry.VertexBuffers[0];
            var indices = geometry.IndexBuffers[0];
            var selectedVertices = Enumerable.Range(0, vertices.Snapshot.VertexCount).ToArray();
            var transformed = DecodedPositionBufferTransformer.Apply(
                vertices.Decoded,
                vertices.Snapshot.VertexCount,
                vertices.Snapshot.PositionLayout,
                selectedVertices,
                transform);
            if (transformed.ChangedVertexCount == 0 || transformed.MaximumDisplacement <= 0f)
            {
                throw Errors.Selection("TRANSFORM_EMPTY_SELECTION_EFFECT", $"The transform does not change any selected position in LOD {profile.Mesh.Lod}.", "Choose a transform that moves at least one selected vertex in every LOD.");
            }

            if (transformed.MaximumDisplacement > request.Operation.Limits.MaximumVertexDisplacement
                || transformed.MaximumDisplacement > RecipeValidator.MaximumTransformDisplacement)
            {
                throw Errors.Selection("TRANSFORM_DISPLACEMENT_EXCEEDED", $"LOD {profile.Mesh.Lod} would move a vertex by up to {transformed.MaximumDisplacement.ToString("R", CultureInfo.InvariantCulture)} Source units.", "Reduce the transform or raise the recipe cap within the hard safety limit.");
            }

            var actualExpected = request.Operation.ExpectedVerticesByLod[profile.Mesh.Lod.ToString(CultureInfo.InvariantCulture)];
            if (profile.Metadata.VertexCount != actualExpected)
            {
                throw Errors.Selection("VERTEX_CARDINALITY_MISMATCH", $"LOD {profile.Mesh.Lod} contains {profile.Metadata.VertexCount} selected vertices; expected {actualExpected}.", "Copy the current counts from inspect evidence into the recipe deliberately.");
            }

            var vertexBlock = parsed.Envelope.Blocks[vertices.Snapshot.ResourceBlockIndex];
            var indexBlock = parsed.Envelope.Blocks[indices.Snapshot.ResourceBlockIndex];
            var boneTargets = profile.Metadata.BoneBounds.Select(bone => PlanBoneBounds(
                bone,
                vertices,
                transform,
                pivot,
                request.Operation.Transform.Translation)).ToArray();
            targets.Add(new PlannedGeometryTarget(
                profile.Mesh.Lod,
                request.Input.LogicalPath,
                profile.Mesh.MeshOrdinal,
                profile.Mesh.BlockIndex,
                vertices.Snapshot.Ordinal,
                indices.Snapshot.Ordinal,
                vertices.Snapshot.ResourceBlockIndex,
                indices.Snapshot.ResourceBlockIndex,
                ContentHash.Compute(vertexBlock.Payload.Span),
                ContentHash.Compute(indexBlock.Payload.Span),
                vertices.Snapshot.DecodedHash,
                transformed.OutputHash,
                indices.Snapshot.DecodedHash,
                profile.Metadata.VertexSetHash,
                profile.Metadata.VertexCount,
                vertices.Snapshot.PositionLayout,
                profile.Metadata.SceneBounds,
                transformed.BoundsAfter,
                pivot,
                request.Operation.Transform.UniformScale,
                request.Operation.Transform.Translation,
                transformed.MaximumDisplacement,
                ["position"],
                profile.Mesh.Geometry.Codec!)
            {
                BoneBoundsTargets = boneTargets,
            });
            AddTargetBlock(targetBlocks, parsed, profile.Mesh.BlockIndex);
            AddTargetBlock(targetBlocks, parsed, vertices.Snapshot.ResourceBlockIndex);
        }

        var affectedBoneHashes = profiles.SelectMany(profile => profile.Metadata.LocalInfluencingBones)
            .Append(roots[0])
            .Distinct(StringComparer.Ordinal)
            .Select(name => (Name: name, Hash: StringToken.Get(name)))
            .GroupBy(item => item.Hash)
            .ToArray();
        var collision = affectedBoneHashes.FirstOrDefault(group => group.Select(item => item.Name).Distinct(StringComparer.Ordinal).Skip(1).Any());
        if (collision is not null)
        {
            throw Errors.Unsupported("TRANSFORM_BONE_HASH_AMBIGUOUS", $"Multiple affected local bones map to Source 2 string token {collision.Key}.", "Use a component without an ambiguous distance-field bone token.");
        }

        var affectedHashes = affectedBoneHashes.Select(group => group.Key).ToHashSet();
        var distanceTargets = new List<PlannedDistanceFieldTarget>();
        for (var blockIndex = 0; blockIndex < parsed.Resource.Blocks.Count; blockIndex++)
        {
            if (parsed.Resource.Blocks[blockIndex] is not BinaryKV3 binary
                || !string.Equals(binary.Type.ToString(), "DSTF", StringComparison.Ordinal))
            {
                continue;
            }

            var fields = Source2TransformMetadataAnalyzer.ReadDistanceFields(binary.Data.Root, blockIndex, $"DSTF block {blockIndex}");
            foreach (var field in fields.Where(field => affectedHashes.Contains(field.ParentBoneNameHash)))
            {
                var expectedBounds = TransformBounds(field.Bounds, transform);
                var expectedCellSize = checked(field.GridCellSize * request.Operation.Transform.UniformScale);
                var expectedMaximumDistance = checked(field.MaximumQuantizedDistance * request.Operation.Transform.UniformScale);
                if (!float.IsFinite(expectedCellSize) || !float.IsFinite(expectedMaximumDistance))
                {
                    throw new OverflowException("Scaled distance-field metadata is non-finite.");
                }

                distanceTargets.Add(new PlannedDistanceFieldTarget(
                    field.ResourceBlockIndex,
                    field.FieldIndex,
                    field.ParentBoneNameHash,
                    field.BodyGroupIndex,
                    field.BodyGroupChoice,
                    field.Bounds,
                    expectedBounds,
                    field.ResolutionX,
                    field.ResolutionY,
                    field.ResolutionZ,
                    field.GridCellSize,
                    expectedCellSize,
                    field.MaximumQuantizedDistance,
                    expectedMaximumDistance,
                    field.SurfaceBias,
                    field.IsTwoSided,
                    field.IsFarFieldOnly,
                    field.UseForOcclusion,
                    field.UseForCollision,
                    field.QuantizedDataHash,
                    field.QuantizedDataLength));
                AddTargetBlock(targetBlocks, parsed, blockIndex);
            }
        }

        return new TransformPlanningResult(
            targets,
            distanceTargets,
            targetBlocks.Values.OrderBy(block => block.Index).ThenBy(block => block.Type, StringComparer.Ordinal).ToArray());
    }

    private static TransformPlanningResult PlanConnectedComponentTransform(
        TransformPlanningRequest request,
        ParsedModel parsed)
    {
        var componentIdsByLod = request.Operation.ConnectedComponentIdsByLod
            ?? throw Errors.InvalidRecipe("CONNECTED_COMPONENT_SELECTION_INVALID", "The connected-component transform has no per-LOD component identities.", "Regenerate the recipe from current inspect evidence.");
        var profiles = new List<(ParsedMesh Mesh, Source2VertexBufferAnalysis Vertices, Source2IndexBufferAnalysis Indices, string[] ComponentIds, int[] SelectedVertices, GeometryBounds Bounds)>();
        foreach (var group in request.SelectedDrawCalls.GroupBy(item => item.MeshOrdinal).OrderBy(group => group.Key))
        {
            if (!parsed.MeshesByOrdinal.TryGetValue(group.Key, out var mesh)
                || mesh.GeometryAnalysis is null
                || mesh.Geometry.Codec is null
                || group.Any(item => item.Lod != mesh.Lod || item.ResourceBlockIndex != mesh.BlockIndex))
            {
                throw Errors.Verification("TRANSFORM_SELECTION_LOCATION_DRIFT", $"Selected mesh {group.Key} no longer exposes decoded connected-component geometry.", "Re-inspect the immutable input and regenerate the plan.");
            }

            var lodKey = mesh.Lod.ToString(CultureInfo.InvariantCulture);
            if (!componentIdsByLod.TryGetValue(lodKey, out var requestedIds))
            {
                throw Errors.Selection("CONNECTED_COMPONENT_LOD_MISSING", $"LOD {mesh.Lod} has no connected-component selection.", "Declare component IDs for every present LOD.");
            }

            var drawCallIds = group.Select(item => item.DrawCallId).ToHashSet(StringComparer.Ordinal);
            var byId = mesh.GeometryAnalysis.ConnectedComponents.ToDictionary(item => item.Snapshot.Id, StringComparer.Ordinal);
            var missing = requestedIds.Where(id => !byId.ContainsKey(id)).Order(StringComparer.Ordinal).ToArray();
            if (missing.Length > 0)
            {
                throw Errors.Selection("CONNECTED_COMPONENT_ID_NOT_FOUND", $"LOD {mesh.Lod} references unknown component IDs: {string.Join(", ", missing)}.", "Copy component IDs from current inspect evidence.");
            }

            var components = requestedIds.Select(id => byId[id]).ToArray();
            if (components.Any(component => !drawCallIds.Contains(component.Snapshot.DrawCallId)
                || !component.Snapshot.ExclusivelyOwned
                || component.Snapshot.VertexBufferOrdinal != 0
                || component.Snapshot.IndexBufferOrdinal != 0))
            {
                throw Errors.Unsupported("CONNECTED_COMPONENT_OWNERSHIP_UNSUPPORTED", $"LOD {mesh.Lod} component selection is not an exclusively owned subset of the selected draw call and primary buffers.", "Select only exclusive components reported inside the chosen draw call.");
            }

            var selectedVertices = components.SelectMany(component => component.VertexIndices).Distinct().Order().ToArray();
            var expected = request.Operation.ExpectedVerticesByLod[lodKey];
            if (selectedVertices.Length != expected)
            {
                throw Errors.Selection("VERTEX_CARDINALITY_MISMATCH", $"LOD {mesh.Lod} contains {selectedVertices.Length} selected component vertices; expected {expected}.", "Copy the current connected-component vertex total into the recipe.");
            }

            var vertices = mesh.GeometryAnalysis.VertexBuffers[0];
            var indices = mesh.GeometryAnalysis.IndexBuffers[0];
            var bounds3 = Bounds3.FromPoints(selectedVertices.Select(vertex => Source2GeometryAnalyzer.ReadPosition(vertices, vertex)).ToArray());
            profiles.Add((
                mesh,
                vertices,
                indices,
                requestedIds.Order(StringComparer.Ordinal).ToArray(),
                selectedVertices,
                new GeometryBounds(ToDomainVector(bounds3.Min), ToDomainVector(bounds3.Max))));
        }

        var expectedLods = request.Operation.ExpectedVerticesByLod.Keys.Select(ParseCanonicalLod).Order().ToArray();
        var actualLods = profiles.Select(profile => profile.Mesh.Lod).Order().ToArray();
        if (!actualLods.SequenceEqual(expectedLods) || profiles.GroupBy(profile => profile.Mesh.Lod).Any(group => group.Count() != 1))
        {
            throw Errors.Selection("TRANSFORM_GEOMETRY_LOD_COVERAGE_INCOMPLETE", "The connected-component selection does not map to exactly one decoded vertex buffer per declared LOD.", "Select one component group in every present LOD.");
        }

        var reference = profiles.Single(profile => profile.Mesh.Lod == request.Operation.Transform.Pivot.ReferenceLod);
        var pivot = Center(reference.Bounds);
        var transform = new UniformTransform(ToPoint(pivot), request.Operation.Transform.UniformScale, ToPoint(request.Operation.Transform.Translation));
        var targets = new List<PlannedGeometryTarget>(profiles.Count);
        var targetBlocks = new Dictionary<(string Type, int Index), PlannedTargetBlock>();
        foreach (var profile in profiles.OrderBy(profile => profile.Mesh.Lod))
        {
            var transformed = DecodedPositionBufferTransformer.Apply(
                profile.Vertices.Decoded,
                profile.Vertices.Snapshot.VertexCount,
                profile.Vertices.Snapshot.PositionLayout,
                profile.SelectedVertices,
                transform);
            if (transformed.ChangedVertexCount == 0
                || transformed.MaximumDisplacement <= 0f
                || transformed.MaximumDisplacement > request.Operation.Limits.MaximumVertexDisplacement
                || transformed.MaximumDisplacement > RecipeValidator.MaximumTransformDisplacement)
            {
                throw Errors.Selection("TRANSFORM_DISPLACEMENT_EXCEEDED", $"LOD {profile.Mesh.Lod} connected-component transform is empty or exceeds its displacement limit.", "Choose a bounded non-identity transform.");
            }

            var vertexBlock = parsed.Envelope.Blocks[profile.Vertices.Snapshot.ResourceBlockIndex];
            var indexBlock = parsed.Envelope.Blocks[profile.Indices.Snapshot.ResourceBlockIndex];
            targets.Add(new PlannedGeometryTarget(
                profile.Mesh.Lod,
                request.Input.LogicalPath,
                profile.Mesh.MeshOrdinal,
                profile.Mesh.BlockIndex,
                profile.Vertices.Snapshot.Ordinal,
                profile.Indices.Snapshot.Ordinal,
                profile.Vertices.Snapshot.ResourceBlockIndex,
                profile.Indices.Snapshot.ResourceBlockIndex,
                ContentHash.Compute(vertexBlock.Payload.Span),
                ContentHash.Compute(indexBlock.Payload.Span),
                profile.Vertices.Snapshot.DecodedHash,
                transformed.OutputHash,
                profile.Indices.Snapshot.DecodedHash,
                transformed.SelectedVertexSetHash,
                transformed.SelectedVertexCount,
                profile.Vertices.Snapshot.PositionLayout,
                transformed.BoundsBefore,
                transformed.BoundsAfter,
                pivot,
                request.Operation.Transform.UniformScale,
                request.Operation.Transform.Translation,
                transformed.MaximumDisplacement,
                ["position"],
                profile.Mesh.Geometry.Codec!)
            {
                ConnectedComponentIds = profile.ComponentIds,
            });
            AddTargetBlock(targetBlocks, parsed, profile.Vertices.Snapshot.ResourceBlockIndex);
        }

        return new TransformPlanningResult(
            targets,
            [],
            targetBlocks.Values.OrderBy(block => block.Index).ThenBy(block => block.Type, StringComparer.Ordinal).ToArray());
    }

    private static int ParseCanonicalLod(string value)
    {
        if (!int.TryParse(value, System.Globalization.NumberStyles.None, CultureInfo.InvariantCulture, out var lod)
            || lod < 0
            || value != lod.ToString(CultureInfo.InvariantCulture))
        {
            throw Errors.InvalidRecipe("LOD_EXPECTATION_INVALID", $"LOD key '{value}' is not a canonical non-negative integer.", "Use keys such as 0, 1, and 2 without leading zeroes.");
        }

        return lod;
    }

    private static PlannedBoneBoundsTarget PlanBoneBounds(
        Source2BoneBoundsAnalysis bone,
        Source2VertexBufferAnalysis vertices,
        UniformTransform transform,
        TransformVector3 pivot,
        TransformVector3 translation)
    {
        var localPivotPoint = Source2TransformMetadataAnalyzer.TransformPoint(ToPoint(pivot), bone.InverseBindPose);
        var localTranslationPoint = Source2TransformMetadataAnalyzer.TransformDirection(ToPoint(translation), bone.InverseBindPose);
        var localTransform = new UniformTransform(localPivotPoint, transform.Scale, localTranslationPoint);
        var expectedCenter = ToDomainVector(localTransform.Apply(ToPoint(bone.LocalBoundsCenter)));
        var expectedSize = new TransformVector3
        {
            X = checked(bone.LocalBoundsSize.X * transform.Scale),
            Y = checked(bone.LocalBoundsSize.Y * transform.Scale),
            Z = checked(bone.LocalBoundsSize.Z * transform.Scale),
        };
        var expectedBounds = BoundsFromCenterSize(expectedCenter, expectedSize);
        var expectedRadius = bone.InfluencedVertices.Max(vertex =>
        {
            var position = Source2GeometryAnalyzer.ReadPosition(vertices, vertex);
            var transformed = transform.Apply(position);
            var local = Source2TransformMetadataAnalyzer.TransformPoint(transformed, bone.InverseBindPose);
            return MathF.Sqrt((local.X * local.X) + (local.Y * local.Y) + (local.Z * local.Z));
        });
        if (!float.IsFinite(expectedRadius))
        {
            throw new OverflowException($"Transformed culling sphere for bone '{bone.BoneName}' is non-finite.");
        }

        return new PlannedBoneBoundsTarget(
            bone.BoneIndex,
            bone.BoneName,
            bone.InverseBindPoseHash,
            new ContentHash(VertexSetHash.Compute(bone.InfluencedVertices)),
            bone.InfluencedVertices.Length,
            bone.LocalBoundsCenter,
            bone.LocalBoundsSize,
            bone.LocalBounds,
            expectedCenter,
            expectedSize,
            expectedBounds,
            ToDomainVector(localPivotPoint),
            ToDomainVector(localTranslationPoint),
            bone.SphereRadius,
            expectedRadius);
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

    private static void AddTargetBlock(
        IDictionary<(string Type, int Index), PlannedTargetBlock> targetBlocks,
        ParsedModel parsed,
        int blockIndex)
    {
        var block = parsed.Envelope.Blocks[blockIndex];
        targetBlocks.TryAdd(
            (block.Type, block.Index),
            new PlannedTargetBlock(block.Index, block.Type, ContentHash.Compute(block.Payload.Span)));
    }

    private static TransformVector3 Center(GeometryBounds bounds) => new()
    {
        X = Midpoint(bounds.Min.X, bounds.Max.X),
        Y = Midpoint(bounds.Min.Y, bounds.Max.Y),
        Z = Midpoint(bounds.Min.Z, bounds.Max.Z),
    };

    private static float Midpoint(float left, float right)
    {
        var value = (float)(((double)left + right) * 0.5d);
        if (!float.IsFinite(value))
        {
            throw new OverflowException("Bounds midpoint is non-finite.");
        }

        return value;
    }

    private static Point3 ToPoint(TransformVector3 value) => new(value.X, value.Y, value.Z);

    private static GeometryBounds TransformBounds(GeometryBounds bounds, UniformTransform transform)
    {
        var transformed = transform.Apply([ToPoint(bounds.Min), ToPoint(bounds.Max)]);
        return new GeometryBounds(ToDomainVector(transformed[0]), ToDomainVector(transformed[1]));
    }

    private static TransformVector3 ToDomainVector(Point3 point) => new()
    {
        X = point.X,
        Y = point.Y,
        Z = point.Z,
    };

    private sealed record TransformMeshProfile(
        ParsedMesh Mesh,
        Source2WholeMeshTransformAnalysis Metadata);
}
