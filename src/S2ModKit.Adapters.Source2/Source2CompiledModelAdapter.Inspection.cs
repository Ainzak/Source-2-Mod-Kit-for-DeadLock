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
    public Task<ModelSnapshot> InspectAsync(ArtifactContent artifact, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var parsed = Parse(artifact, allowLodExcludedMeshes: true);
        return Task.FromResult(parsed.Snapshot);
    }

    TransformPlanningResult ITransformOperationPlanner.PlanTransform(TransformPlanningRequest request) =>
        PlanTransform(request);

    private ParsedModel Parse(
        ArtifactContent artifact,
        bool retainGeometryAnalysis = false,
        bool allowLodExcludedMeshes = false)
    {
        var envelope = ResourceEnvelopeReader.Read(artifact.Bytes);
        var stream = new MemoryStream(artifact.Bytes.ToArray(), writable: false);
        var resource = new Resource { FileName = artifact.LogicalPath };
        try
        {
            resource.Read(stream, verifyFileSize: true, leaveOpen: true);
            var analysis = Analyze(resource, envelope, artifact, retainGeometryAnalysis, allowLodExcludedMeshes);
            return new ParsedModel(stream, resource, envelope, analysis.Snapshot, analysis.MeshesByOrdinal);
        }
        catch (S2ModKitException)
        {
            resource.Dispose();
            stream.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException or NotSupportedException or ArgumentException or InvalidOperationException or OverflowException or IndexOutOfRangeException or KeyNotFoundException)
        {
            resource.Dispose();
            stream.Dispose();
            throw new S2ModKitException(
                new S2Error("SOURCE2_SEMANTIC_LAYOUT_UNSUPPORTED", "source2_adapter", "VRF could not parse the compiled model through a supported semantic profile.", "Inspect the input with a compatible Source 2 tool and add a reviewed layout profile before mutation.", ErrorCategory.UnsupportedCapability),
                exception);
        }
    }

    private (ModelSnapshot Snapshot, Dictionary<int, ParsedMesh> MeshesByOrdinal) Analyze(
        Resource resource,
        Source2ResourceEnvelope envelope,
        ArtifactContent artifact,
        bool retainGeometryAnalysis,
        bool allowLodExcludedMeshes)
    {
        if (resource.ResourceType != ResourceType.Model)
        {
            throw Errors.Unsupported("RESOURCE_TYPE_UNSUPPORTED", $"Resource type '{resource.ResourceType}' is not a compiled model.", "Provide a standalone .vmdl_c compiled model.");
        }

        if (resource.Blocks.Count != envelope.Blocks.Count || resource.FileSize != artifact.Bytes.Length)
        {
            throw Errors.Unsupported("VRF_ENVELOPE_DISAGREEMENT", "VRF and the raw envelope parser disagree about resource size or block count.", "Reject the input and inspect it with a compatible Source 2 tool.");
        }

        for (var index = 0; index < resource.Blocks.Count; index++)
        {
            var semantic = resource.Blocks[index];
            var raw = envelope.Blocks[index];
            if (!string.Equals(semantic.Type.ToString(), raw.Type, StringComparison.Ordinal)
                || semantic.Offset != raw.Offset
                || semantic.Size != raw.Payload.Length)
            {
                throw Errors.Unsupported("VRF_BLOCK_TABLE_DISAGREEMENT", $"VRF and the raw parser disagree about block {index}.", "Reject the input and add a reviewed resource-envelope profile.");
            }
        }

        var models = resource.Blocks.OfType<ValveResourceFormat.ResourceTypes.Model>().ToArray();
        if (models.Length != 1)
        {
            throw Errors.Unsupported("MODEL_DATA_BLOCK_UNSUPPORTED", $"Expected one model DATA block; found {models.Length}.", "Use a standalone compiled model with one model DATA block.");
        }

        var modelData = models[0].Data;
        var layout = Source2MeshLayoutClassifier.Classify(
            modelData,
            envelope.Blocks.Select(block => block.Type).ToArray());
        if (string.Equals(layout.Kind, Source2MeshLayoutKind.EmbeddedMbuf, StringComparison.Ordinal))
        {
            return AnalyzeEmbeddedMbuf(resource, envelope, artifact, modelData, retainGeometryAnalysis);
        }

        Source2MeshLayoutClassifier.RequireSupportedRoot(layout);

        var controls = resource.Blocks.OfType<BinaryKV3>()
            .Where(block => block.Type.ToString() == "CTRL" && block.Data.Root.TryGetValue("embedded_meshes", out _))
            .ToArray();
        if (controls.Length != 1)
        {
            throw Errors.Unsupported("EMBEDDED_MESH_CONTROL_UNSUPPORTED", $"Expected one CTRL block with embedded_meshes; found {controls.Length}.", "Use a compiled model with one unambiguous embedded-mesh table.");
        }

        var embeddedMeshes = RequireArray(controls[0].Data.Root, "embedded_meshes", "CTRL");
        var lodMasks = RequireArray(modelData, "m_refLODGroupMasks", "model DATA");
        var lodDistances = RequireArray(modelData, "m_lodGroupSwitchDistances", "model DATA");
        var meshBlockIndices = resource.Blocks.Select((block, index) => (block, index)).Where(item => item.block is Mesh).Select(item => item.index).ToArray();

        var referencedBlocks = new HashSet<int>();
        var meshes = new Dictionary<int, ParsedMesh>();
        var lodLevels = new HashSet<int>();
        // The descriptor table is validated before the native geometry codec is acquired, so a
        // rejected input (for example a strict zero-mask rejection) never opens, and can never
        // leak, the codec's native-library handle.
        var table = ValidateRootMeshTable(
            embeddedMeshes,
            lodMasks,
            lodDistances.Count,
            new RootMeshTableFacts(
                resource.Blocks.Count,
                envelope.Blocks.Select(block => block.Type).ToArray(),
                meshBlockIndices),
            allowLodExcludedMeshes);
        referencedBlocks.UnionWith(table.Select(entry => entry.BlockIndex));

        IMeshOptimizerCodec? geometryCodec = null;
        string? geometryCodecFailure = null;
        if (GeometryCodecCapability.Status == "ready")
        {
            try
            {
                geometryCodec = OpenGeometryCodec();
            }
            catch (Exception exception) when (exception is S2ModKitException
                or ArgumentException
                or BadImageFormatException
                or DllNotFoundException
                or EntryPointNotFoundException
                or FileNotFoundException
                or IOException
                or NotSupportedException
                or UnauthorizedAccessException)
            {
                geometryCodecFailure = exception is S2ModKitException s2modKitException
                    ? s2modKitException.Error.Summary
                    : exception.Message;
            }
        }

        try
        {
            // Zero-mask descriptors stay excluded: their block identities participate in the
            // reference accounting above, but their geometry stays opaque and no mesh, LOD, or
            // draw-call identity is fabricated for them.
            foreach (var entry in table.Where(entry => entry.LodMask != 0))
            {
                var mesh = (Mesh)resource.Blocks[entry.BlockIndex];
                var lod = entry.MeshLods[0];
                var lineage = Source2MeshLineageExtractor.Extract(entry.Descriptor, lod, $"embedded_meshes[{entry.MeshOrdinal}]");
                lodLevels.UnionWith(entry.MeshLods);
                var drawCalls = ReadDrawCalls(mesh, artifact.LogicalPath, lod, entry.MeshOrdinal);
                var geometry = AnalyzeGeometry(
                    entry.Descriptor,
                    envelope,
                    drawCalls,
                    geometryCodec,
                    geometryCodecFailure,
                    $"embedded mesh {entry.MeshOrdinal}");
                meshes.Add(
                    entry.MeshOrdinal,
                    new ParsedMesh(
                        entry.MeshOrdinal,
                        lod,
                        entry.BlockIndex,
                        entry.Descriptor,
                        mesh,
                        drawCalls,
                        geometry.Snapshot,
                        retainGeometryAnalysis ? geometry.Analysis : null,
                        lineage)
                    {
                        LodMask = entry.LodMask,
                    });
            }
        }
        finally
        {
            geometryCodec?.Dispose();
        }

        if (!referencedBlocks.SetEquals(meshBlockIndices))
        {
            throw Errors.Unsupported("UNREFERENCED_MDAT_BLOCK", "At least one MDAT block is not represented by the embedded-mesh table.", "Use a model whose CTRL table accounts for every MDAT block.");
        }

        var orderedLods = CollectActiveLodLevels(lodLevels, lodDistances.Count);
        var duplicateId = meshes.Values.SelectMany(mesh => mesh.DrawCalls).GroupBy(item => item.Snapshot.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicateId is not null)
        {
            throw Errors.Unsupported("DRAW_CALL_ID_COLLISION", $"Stable draw-call ID '{duplicateId.Key}' is duplicated.", "Use a newer stable-identity profile for this layout.");
        }

        var lods = orderedLods.Select(lod => new LodSnapshot(
            lod,
            meshes.Values.Where(mesh => (mesh.LodMask & (1UL << lod)) != 0).OrderBy(mesh => mesh.MeshOrdinal)
                .Select(mesh => new MeshSnapshot(
                    artifact.LogicalPath,
                    mesh.MeshOrdinal,
                    mesh.BlockIndex,
                    KvSemanticHasher.Compute(mesh.Block.Data, "m_drawCalls"),
                    (mesh.Lod == lod
                        ? mesh.DrawCalls
                        : ReadDrawCalls(mesh.Block, artifact.LogicalPath, lod, mesh.MeshOrdinal))
                        .Select(drawCall => drawCall.Snapshot).ToArray())
                {
                    Geometry = BitOperations.PopCount(mesh.LodMask) == 1 ? mesh.Geometry : null,
                    MechanicalLineage = mesh.Lod == lod
                        ? mesh.Lineage
                        : Source2MeshLineageExtractor.Extract(mesh.Descriptor, lod, $"embedded_meshes[{mesh.MeshOrdinal}]"),
                })
                .ToArray())).ToArray();
        var snapshot = new ModelSnapshot(
            new ArtifactSnapshot(artifact.LogicalPath, artifact.ContentHash, artifact.Bytes.Length, envelope.CreateBlockSnapshots()),
            lods);
        return (snapshot, meshes);
    }

    // Active-LOD inventory projection: excluded meshes contribute nothing, all-zero models are
    // rejected explicitly before an empty projection is indexed, and the remaining active levels
    // must be contiguous and agree with the declared switch-distance count.
    internal static int[] CollectActiveLodLevels(IReadOnlyCollection<int> lodLevels, int lodDistanceCount)
    {
        if (lodLevels.Count == 0)
        {
            throw Errors.Unsupported("LOD_INVENTORY_UNSUPPORTED", "Every embedded mesh is excluded from all declared LOD groups.", "The model has no active LOD projection; it is not inspectable or mutable through this profile.");
        }

        var orderedLods = lodLevels.Order().ToArray();
        var expectedLods = Enumerable.Range(0, orderedLods[^1] + 1).ToArray();
        if (!orderedLods.SequenceEqual(expectedLods) || lodDistanceCount != orderedLods.Length)
        {
            throw Errors.Unsupported("LOD_INVENTORY_UNSUPPORTED", $"LOD masks identify [{string.Join(", ", orderedLods)}] with {lodDistanceCount} switch distances.", "Use a model with contiguous LOD levels and one switch distance per level.");
        }

        return orderedLods;
    }

    // Root-MDAT descriptor-table preflight. The whole table — cardinality, sequential ordinals,
    // MDAT block types/indices, unique references, declared LOD count and every nonzero mask range
    // — is validated before any zero mask is admitted (inspection) or rejected (strict), so a
    // later malformed descriptor can never be hidden by an earlier excluded mesh.
    internal static RootMeshTableEntry[] ValidateRootMeshTable(
        KVObject embeddedMeshes,
        KVObject lodMasks,
        int lodDistanceCount,
        RootMeshTableFacts blocks,
        bool allowLodExcludedMeshes)
    {
        ArgumentNullException.ThrowIfNull(embeddedMeshes);
        ArgumentNullException.ThrowIfNull(lodMasks);
        ArgumentNullException.ThrowIfNull(blocks);
        if (embeddedMeshes.Count is < 1 or > MaximumEmbeddedMeshCount
            || embeddedMeshes.Count != lodMasks.Count
            || embeddedMeshes.Count != blocks.MeshBlockIndices.Count)
        {
            throw Errors.Unsupported("EMBEDDED_MESH_COUNT_UNSUPPORTED", $"Embedded descriptors={embeddedMeshes.Count}, LOD masks={lodMasks.Count}, MDAT blocks={blocks.MeshBlockIndices.Count}.", "Use a model with one descriptor and LOD mask per embedded MDAT block.");
        }

        if (lodDistanceCount is < 1 or > 64)
        {
            throw Errors.Unsupported("LOD_MASK_UNSUPPORTED", $"The model declares {lodDistanceCount} LOD switch distances.", "Use a model with one to 64 declared LOD levels.");
        }

        var entries = new List<RootMeshTableEntry>(embeddedMeshes.Count);
        var referencedBlocks = new HashSet<int>();
        for (var meshOrdinal = 0; meshOrdinal < embeddedMeshes.Count; meshOrdinal++)
        {
            var descriptor = RequireCollection(embeddedMeshes[meshOrdinal], $"embedded_meshes[{meshOrdinal}]");
            var declaredOrdinal = RequireInt32(descriptor, "m_nMeshIndex", $"embedded_meshes[{meshOrdinal}]");
            var blockIndex = RequireInt32(descriptor, "m_nDataBlock", $"embedded_meshes[{meshOrdinal}]");
            if (declaredOrdinal != meshOrdinal
                || blockIndex < 0
                || blockIndex >= blocks.BlockCount
                || !blocks.MeshBlockIndices.Contains(blockIndex)
                || !string.Equals(blocks.BlockTypes[blockIndex], "MDAT", StringComparison.Ordinal)
                || !referencedBlocks.Add(blockIndex))
            {
                throw Errors.Unsupported("EMBEDDED_MESH_REFERENCE_UNSUPPORTED", $"Embedded mesh {meshOrdinal} does not map uniquely to one MDAT block.", "Use an intact model with sequential mesh indices and unique MDAT references.");
            }

            var mask = ReadUnsignedInteger(lodMasks[meshOrdinal], $"m_refLODGroupMasks[{meshOrdinal}]");
            entries.Add(new RootMeshTableEntry(
                meshOrdinal,
                descriptor,
                blockIndex,
                mask,
                mask == 0 ? [] : ExpandRootLodMask(mask, lodDistanceCount, meshOrdinal)));
        }

        if (!allowLodExcludedMeshes && entries.Any(entry => entry.LodMask == 0))
        {
            var firstExcluded = entries.FindIndex(entry => entry.LodMask == 0);
            throw Errors.Unsupported(
                "ZERO_LOD_MESH_READ_ONLY",
                $"Embedded mesh {firstExcluded} has LOD mask 0 and belongs to no LOD group.",
                "This layout is inspection-only; LOD-excluded meshes have no verified mutation path.");
        }

        return entries.ToArray();
    }

    internal static IReadOnlyList<int> ExpandRootLodMask(ulong mask, int lodCount, int meshOrdinal)
    {
        if (mask == 0 || lodCount is < 1 or > 64 || BitOperations.Log2(mask) >= lodCount)
        {
            throw Errors.Unsupported(
                "LOD_MASK_UNSUPPORTED",
                $"Embedded mesh {meshOrdinal} has LOD mask {mask} outside the declared LOD switch-distance range.",
                "Use a model with a nonzero mask confined to its declared LOD levels.");
        }

        return Enumerable.Range(0, lodCount)
            .Where(lod => (mask & (1UL << lod)) != 0)
            .ToArray();
    }

    private static (ModelSnapshot Snapshot, Dictionary<int, ParsedMesh> MeshesByOrdinal) AnalyzeEmbeddedMbuf(
        Resource resource,
        Source2ResourceEnvelope envelope,
        ArtifactContent artifact,
        KVObject modelData,
        bool retainGeometryAnalysis)
    {
        var allControls = resource.Blocks.OfType<BinaryKV3>()
            .Where(block => block.Type.ToString() == "CTRL")
            .ToArray();
        var controls = allControls
            .Where(block => block.Data.Root.TryGetValue("embedded_meshes", out _))
            .ToArray();
        if (controls.Length != 1)
        {
            throw Errors.Unsupported(
                "MBUF_LAYOUT_UNSUPPORTED",
                $"Expected one CTRL block with an embedded-MBUF table; found {controls.Length}.",
                "Use an intact raw embedded-MBUF model matching the accepted bounded profile.");
        }

        Source2ConvexPhysReader.RequireUniqueCoupledControl(
            allControls.Select(block => block.Data.Root).ToArray(),
            controls[0].Data.Root,
            "embedded MBUF/PHYS model");

        var embeddedMeshes = RequireArray(controls[0].Data.Root, "embedded_meshes", "CTRL");
        var lodMasks = RequireArray(modelData, "m_refLODGroupMasks", "model DATA");
        var lodDistances = RequireArray(modelData, "m_lodGroupSwitchDistances", "model DATA");
        var mdatIndices = resource.Blocks.Select((block, index) => (block, index))
            .Where(item => item.block is Mesh)
            .Select(item => item.index)
            .ToArray();
        var mbufIndices = envelope.Blocks.Where(block => string.Equals(block.Type, "MBUF", StringComparison.Ordinal))
            .Select(block => block.Index)
            .ToArray();
        if (embeddedMeshes.Count != 1
            || lodMasks.Count != 1
            || lodDistances.Count != 0
            || mdatIndices.Length != 1
            || mbufIndices.Length != 1)
        {
            throw Errors.Unsupported(
                "MBUF_COUNT_UNSUPPORTED",
                $"The bounded profile requires one descriptor, one LOD0 mask, no switch distances, one MDAT, and one MBUF; observed {embeddedMeshes.Count}/{lodMasks.Count}/{lodDistances.Count}/{mdatIndices.Length}/{mbufIndices.Length}.",
                "Use one complete raw embedded-MBUF visual component.");
        }

        var descriptor = RequireCollection(embeddedMeshes[0], "embedded_meshes[0]");
        var meshOrdinal = RequireInt32(descriptor, "mesh_index", "embedded_meshes[0]");
        var mdatIndex = RequireInt32(descriptor, "data_block", "embedded_meshes[0]");
        var mbufIndex = RequireInt32(descriptor, "vbib_block", "embedded_meshes[0]");
        if (meshOrdinal != 0
            || mdatIndex != mdatIndices[0]
            || mbufIndex != mbufIndices[0]
            || resource.Blocks[mdatIndex] is not Mesh mesh)
        {
            throw Errors.Unsupported(
                "MBUF_LAYOUT_UNSUPPORTED",
                "The embedded descriptor does not resolve uniquely to the sole MDAT and MBUF blocks.",
                "Use an intact raw embedded-MBUF model matching the accepted bounded profile.");
        }

        var mask = ReadUnsignedInteger(lodMasks[0], "m_refLODGroupMasks[0]");
        if (mask != byte.MaxValue)
        {
            throw Errors.Unsupported(
                "MBUF_LAYOUT_UNSUPPORTED",
                $"The embedded-MBUF profile requires the characterized sole-mesh LOD mask 255; observed {mask}.",
                "Use one complete raw embedded-MBUF visual component at LOD0.");
        }

        var lineage = Source2MeshLineageExtractor.Extract(
            descriptor,
            0,
            "embedded_meshes[0]",
            sourceNameKey: "name");
        var drawCalls = ReadDrawCalls(mesh, artifact.LogicalPath, 0, 0);
        var raw = Source2RawMbufReader.AnalyzeDetailed(
            envelope.Blocks[mbufIndex],
            drawCalls.Select(item => new GeometryDrawCallInput(item.Snapshot, item.Data)).ToArray(),
            "embedded mesh 0");
        var transformMetadata = Source2TransformMetadataAnalyzer.AnalyzeRawMbufWholeMesh(
            mesh.Data,
            raw,
            "embedded mesh 0");
        var physics = Source2ConvexPhysReader.AnalyzeDetailed(
            controls[0].Data.Root,
            envelope,
            index => resource.Blocks[index] is PhysAggregateData aggregate ? aggregate.Data : null,
            raw.Geometry.DrawCalls[0].Snapshot.Bounds,
            "embedded physics 0");
        var parsedMesh = new ParsedMesh(
            0,
            0,
            mdatIndex,
            descriptor,
            mesh,
            drawCalls,
            raw.Geometry.Snapshot,
            retainGeometryAnalysis ? raw.Geometry : null,
            lineage)
        {
            LodMask = mask,
            PhysicsAnalysis = retainGeometryAnalysis ? physics : null,
            RawMbufAnalysis = retainGeometryAnalysis ? raw : null,
            WholeMeshTransformAnalysis = retainGeometryAnalysis ? transformMetadata : null,
        };
        var snapshot = new ModelSnapshot(
            new ArtifactSnapshot(
                artifact.LogicalPath,
                artifact.ContentHash,
                artifact.Bytes.Length,
                envelope.CreateBlockSnapshots()),
            [
                new LodSnapshot(
                    0,
                    [
                        new MeshSnapshot(
                            artifact.LogicalPath,
                            0,
                            mdatIndex,
                            KvSemanticHasher.Compute(mesh.Data, "m_drawCalls"),
                            drawCalls.Select(drawCall => drawCall.Snapshot).ToArray())
                        {
                            Geometry = raw.Geometry.Snapshot,
                            MechanicalLineage = lineage,
                        },
                    ]),
            ]);
        return (snapshot, new Dictionary<int, ParsedMesh> { [0] = parsedMesh });
    }

    private static List<DrawCallLocation> ReadDrawCalls(Mesh mesh, string resourcePath, int lod, int meshOrdinal)
    {
        var sceneObjects = RequireArray(mesh.Data, "m_sceneObjects", $"MDAT mesh {meshOrdinal}");
        if (sceneObjects.Count == 0)
        {
            throw Errors.Unsupported("SCENE_OBJECT_LAYOUT_UNSUPPORTED", $"MDAT mesh {meshOrdinal} contains no scene objects.", "Use a mesh with explicit scene-object draw-call collections.");
        }

        var result = new List<DrawCallLocation>();
        for (var sceneObjectIndex = 0; sceneObjectIndex < sceneObjects.Count; sceneObjectIndex++)
        {
            var sceneObject = RequireCollection(sceneObjects[sceneObjectIndex], $"mesh {meshOrdinal} scene object {sceneObjectIndex}");
            var collection = RequireArray(sceneObject, "m_drawCalls", $"mesh {meshOrdinal} scene object {sceneObjectIndex}");
            for (var index = 0; index < collection.Count; index++)
            {
                if (result.Count >= MaximumDrawCallsPerMesh)
                {
                    throw Errors.Unsupported("DRAW_CALL_COUNT_UNSUPPORTED", $"MDAT mesh {meshOrdinal} exceeds {MaximumDrawCallsPerMesh} draw calls.", "Use a bounded model or add a reviewed larger-layout profile.");
                }

                var drawCall = RequireCollection(collection[index], $"mesh {meshOrdinal} draw call {result.Count}");
                var material = RequireString(drawCall, "m_material", $"mesh {meshOrdinal} draw call {result.Count}");
                var indexStart = RequireInt64(drawCall, "m_nStartIndex", $"mesh {meshOrdinal} draw call {result.Count}");
                var indexCount = RequireInt64(drawCall, "m_nIndexCount", $"mesh {meshOrdinal} draw call {result.Count}");
                if (indexStart < 0 || indexCount <= 0 || indexStart > long.MaxValue - indexCount)
                {
                    throw Errors.Unsupported("DRAW_CALL_RANGE_UNSUPPORTED", $"MDAT mesh {meshOrdinal} draw call {result.Count} has invalid index range {indexStart}+{indexCount}.", "Use an intact mesh with non-negative bounded draw-call ranges.");
                }

                var snapshot = DrawCallSnapshot.Create(resourcePath, lod, meshOrdinal, result.Count, material, indexStart, indexCount);
                result.Add(new DrawCallLocation(collection, index, snapshot, drawCall));
            }
        }

        return result;
    }

    private static void ValidateSnapshotAgreement(ModelSnapshot expected, ModelSnapshot actual)
    {
        var expectedBlocks = expected.Artifact.Blocks.OrderBy(block => block.Index).Select(block => (block.Index, block.Type, block.ContentHash)).ToArray();
        var actualBlocks = actual.Artifact.Blocks.OrderBy(block => block.Index).Select(block => (block.Index, block.Type, block.ContentHash)).ToArray();
        var expectedCalls = Flatten(expected).ToArray();
        var actualCalls = Flatten(actual).ToArray();
        var expectedMeshes = FlattenMeshes(expected).ToArray();
        var actualMeshes = FlattenMeshes(actual).ToArray();
        if (expected.Artifact.ContentHash != actual.Artifact.ContentHash
            || !expectedBlocks.SequenceEqual(actualBlocks)
            || !expectedMeshes.SequenceEqual(actualMeshes)
            || !expectedCalls.SequenceEqual(actualCalls))
        {
            throw Errors.Verification("INSPECTION_SNAPSHOT_DRIFT", "The supplied model snapshot differs from a fresh inspection of the immutable bytes.", "Regenerate the plan from a fresh inspection.");
        }
    }

    private static IEnumerable<(int Lod, int Mesh, int Block, string Id, string Material, long Start, long Count)> Flatten(ModelSnapshot model) =>
        model.Lods.OrderBy(lod => lod.Level).SelectMany(lod => lod.Meshes.OrderBy(mesh => mesh.MeshOrdinal).SelectMany(mesh => mesh.DrawCalls.OrderBy(drawCall => drawCall.DrawCallOrdinal)
            .Select(drawCall => (lod.Level, mesh.MeshOrdinal, mesh.ResourceBlockIndex, drawCall.Id, drawCall.MaterialPath, drawCall.IndexStart, drawCall.IndexCount))));

    private static IEnumerable<(int Lod, string Resource, int Mesh, int Block, ContentHash SemanticHash, string? LineageKey, string? SourceLabel, string? SourceName)> FlattenMeshes(ModelSnapshot model) =>
        model.Lods.OrderBy(lod => lod.Level).SelectMany(lod => lod.Meshes
            .OrderBy(mesh => mesh.ResourcePath, StringComparer.Ordinal)
            .ThenBy(mesh => mesh.MeshOrdinal)
            .ThenBy(mesh => mesh.ResourceBlockIndex)
            .Select(mesh => (
                lod.Level,
                StableIdentity.NormalizePath(mesh.ResourcePath),
                mesh.MeshOrdinal,
                mesh.ResourceBlockIndex,
                mesh.ImmutableSemanticHash,
                mesh.MechanicalLineage?.Key,
                mesh.MechanicalLineage?.SourceLabel,
                mesh.MechanicalLineage?.SourceName)));

    private static bool Matches(DrawCallSnapshot actual, SelectedDrawCall planned) =>
        string.Equals(actual.Id, planned.DrawCallId, StringComparison.Ordinal)
        && string.Equals(actual.MaterialPath, planned.MaterialPath, StringComparison.Ordinal)
        && actual.DrawCallOrdinal == planned.DrawCallOrdinal
        && actual.IndexStart == planned.IndexStart
        && actual.IndexCount == planned.IndexCount;

    private GeometryInspection AnalyzeGeometry(
        KVObject descriptor,
        Source2ResourceEnvelope envelope,
        IReadOnlyList<DrawCallLocation> drawCalls,
        IMeshOptimizerCodec? codec,
        string? codecFailure,
        string context)
    {
        if (codec is null)
        {
            return new GeometryInspection(
                new MeshGeometrySnapshot(
                    "unavailable",
                    codecFailure ?? GeometryCodecCapability.Summary,
                    [],
                    [],
                    [],
                    GeometryCodecCapability.Identity),
                null);
        }

        try
        {
            var analysis = Source2GeometryAnalyzer.AnalyzeDetailed(
                descriptor,
                envelope,
                drawCalls.Select(item => new GeometryDrawCallInput(item.Snapshot, item.Data)).ToArray(),
                codec,
                context);
            return new GeometryInspection(analysis.Snapshot, analysis);
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidDataException
            or InvalidOperationException
            or OverflowException
            or IndexOutOfRangeException)
        {
            return new GeometryInspection(
                new MeshGeometrySnapshot(
                    "unsupported",
                    exception.Message,
                    [],
                    [],
                    [],
                    codec.Identity),
                null);
        }
    }

    private sealed record GeometryInspection(
        MeshGeometrySnapshot Snapshot,
        Source2GeometryAnalysis? Analysis);

    private sealed record DrawCallLocation(
        KVObject Collection,
        int CollectionIndex,
        DrawCallSnapshot Snapshot,
        KVObject Data);

    private sealed record ParsedMesh(
        int MeshOrdinal,
        int Lod,
        int BlockIndex,
        KVObject Descriptor,
        Mesh Block,
        IReadOnlyList<DrawCallLocation> DrawCalls,
        MeshGeometrySnapshot Geometry,
        Source2GeometryAnalysis? GeometryAnalysis,
        MechanicalMeshLineage Lineage)
    {
        public ulong LodMask { get; init; }

        public Source2ConvexPhysAnalysis? PhysicsAnalysis { get; init; }

        public Source2RawMbufAnalysis? RawMbufAnalysis { get; init; }

        public Source2WholeMeshTransformAnalysis? WholeMeshTransformAnalysis { get; init; }
    }

}

internal sealed record RootMeshTableEntry(
    int MeshOrdinal,
    KVObject Descriptor,
    int BlockIndex,
    ulong LodMask,
    IReadOnlyList<int> MeshLods);

internal sealed record RootMeshTableFacts(
    int BlockCount,
    IReadOnlyList<string> BlockTypes,
    IReadOnlyList<int> MeshBlockIndices);
