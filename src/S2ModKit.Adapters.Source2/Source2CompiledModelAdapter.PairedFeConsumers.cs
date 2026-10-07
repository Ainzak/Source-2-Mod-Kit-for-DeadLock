using System.Globalization;
using S2ModKit.Application;
using S2ModKit.Domain;
using ValveKeyValue;
using ValveResourceFormat.Utils;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    // Named compiled FE input families. This is an inventory/preservation boundary, not a solver.
    private const string PairedFeFields = "m_CtrlHash,m_CtrlName,m_nStaticNodeFlags,m_nDynamicNodeFlags,m_flLocalForce,m_flLocalRotation,m_nNodeCount,m_nStaticNodes,m_nRotLockStaticNodes,m_nFirstPositionDrivenNode,m_nSimdTriCount1,m_nSimdTriCount2,m_nSimdQuadCount1,m_nSimdQuadCount2,m_nQuadCount1,m_nQuadCount2,m_nTreeDepth,m_nNodeBaseJiggleboneDependsCount,m_nRopeCount,m_Ropes,m_NodeBases,m_SimdNodeBases,m_Quads,m_SimdQuads,m_SimdTris,m_Prisms,m_PrismVolumes,m_SimdPrisms,m_SimdPrismVolumes,m_SimdRods,m_SimdRodsAnim,m_InitPose,m_Rods,m_Twists,m_HingeLimits,m_AntiTunnelBytecode,m_DynKinLinks,m_BoneMergeLinks,m_AntiTunnelProbes,m_AntiTunnelTargetNodes,m_NodeStrayBoxes,m_AxialEdges,m_NodeInvMasses,m_CtrlOffsets,m_CtrlOsOffsets,m_FollowNodes,m_CollisionPlanes,m_NodeIntegrator,m_SpringIntegrator,m_SimdSpringIntegrator,m_WorldCollisionParams,m_LegacyStretchForce,m_NodeCollisionRadii,m_DynNodeFriction,m_LocalRotation,m_LocalForce,m_TaperedCapsuleStretches,m_TaperedCapsuleRigids,m_SphereRigids,m_WorldCollisionNodes,m_TreeParents,m_TreeCollisionMasks,m_TreeChildren,m_FreeNodes,m_FitMatrices,m_FitWeights,m_ReverseOffsets,m_AnimStrayRadii,m_SimdAnimStrayRadii,m_KelagerBends,m_CtrlSoftOffsets,m_JiggleBones,m_SourceElems,m_GoalDampedSpringIntegrators,m_Tris,m_nTriCount1,m_nTriCount2,m_nReservedUint8,m_nExtraPressureIterations,m_nExtraGoalIterations,m_nExtraIterations,m_SDFRigids,m_BoxRigids,m_DynNodeVertexSet,m_VertexSetNames,m_RigidColliderPriorities,m_MorphLayers,m_MorphSetData,m_VertexMaps,m_VertexMapValues,m_Effects,m_LockToParent,m_LockToGoal,m_SkelParents,m_DynNodeWindBases,m_SelfCollisionLayers,m_flInternalPressure,m_flDefaultTimeDilation,m_flWindage,m_flWindDrag,m_flDefaultSurfaceStretch,m_flDefaultThreadStretch,m_flDefaultGravityScale,m_flDefaultVelAirDrag,m_flDefaultExpAirDrag,m_flDefaultVelQuadAirDrag,m_flDefaultExpQuadAirDrag,m_flRodVelocitySmoothRate,m_flQuadVelocitySmoothRate,m_flAddWorldCollisionRadius,m_flDefaultVolumetricSolveAmount,m_flMotionSmoothCDT,m_flLocalDrag1,m_nRodVelocitySmoothIterations,m_nQuadVelocitySmoothIterations";
    private const string PairedPhysFields = "m_nFlags,m_nRefCounter,m_bCompoundsPacked,m_bonesHash,m_boneNames,m_indexNames,m_indexHash,m_bindPose,m_parts,m_shapeMarkups,m_constraints2,m_joints,m_pFeModel,m_boneParents,m_surfacePropertyHashes,m_collisionAttributes,m_debugPartNames,m_embeddedKeyvalues";

    internal static void AddPairedFeConsumers(KVObject physics, int block, IReadOnlyList<PairedRootBone> roots,
        Action<string, string, int, string, ContentHash, IEnumerable<PairedConsumerReference>?> add)
    {
        if (physics.Children.Any(k => !PairedPhysFields.Split(',').Contains(k.Key, StringComparer.Ordinal)))
            throw DirectionalFailure("PAIRED_CONSUMER_UNSUPPORTED", "Unknown PHYS input family.");
        var rootNames = roots.ToDictionary(r => r.Name, StringComparer.Ordinal);
        void TokenPairs(KVObject names, KVObject hashes)
        {
            if (!names.IsArray || !hashes.IsArray || names.Count != hashes.Count) throw DirectionalFailure("PAIRED_CONSUMER_UNSUPPORTED", "Incomplete authored token arrays.");
            var tokens = new HashSet<uint>();
            for (var i = 0; i < names.Count; i++)
                if (StringToken.Get(PairedSourceString(names[i])) != PairedSourceUInt(hashes[i]) || !tokens.Add(PairedSourceUInt(hashes[i])))
                    throw DirectionalFailure("PAIRED_CONSUMER_UNSUPPORTED", "Authored token/name identity drift.");
        }
        TokenPairs(ExperimentalArray(physics, "m_boneNames"), ExperimentalArray(physics, "m_bonesHash"));
        if (!physics.TryGetValue("m_pFeModel", out var fe) || fe.ValueType == KVValueType.Null) return;
        if (!fe.IsCollection || fe.Children.Any(k => !PairedFeFields.Split(',').Contains(k.Key, StringComparer.Ordinal)))
            throw DirectionalFailure("PAIRED_CONSUMER_UNSUPPORTED", "Unknown FE serialized-input family.");
        foreach (var unsupported in new[] { "m_JiggleBones", "m_MorphLayers", "m_MorphSetData" })
            if (fe.TryGetValue(unsupported, out var field) && (!field.IsArray || field.Count > 0))
                throw DirectionalFailure("PAIRED_CONSUMER_UNSUPPORTED", "Jiggle/morph consumers require separate characterization.");
        var names = ExperimentalArray(fe, "m_CtrlName"); var hashes = ExperimentalArray(fe, "m_CtrlHash");
        TokenPairs(names, hashes);
        var initial = ExperimentalArray(fe, "m_InitPose"); var parents = ExperimentalArray(fe, "m_SkelParents");
        var count = PairedSourceInt(fe["m_nNodeCount"]);
        var statics = PairedSourceInt(fe["m_nStaticNodes"]);
        if (count <= 0 || count != names.Count || count != initial.Count || count != parents.Count || statics < 0 || statics > count)
            throw DirectionalFailure("PAIRED_CONSUMER_UNSUPPORTED", "FE node/control/rest inventory is incomplete.");
        var refs = new PairedConsumerReference[count];
        for (var i = 0; i < count; i++)
        {
            var name = names[i].ToString(CultureInfo.InvariantCulture); var root = rootNames.GetValueOrDefault(name);
            if (!initial[i].IsArray || initial[i].Count != 8) throw DirectionalFailure("PAIRED_CONSUMER_UNSUPPORTED", "FE initial pose layout is uncharacterized.");
            foreach (var scalar in initial[i].Values) _ = ExperimentalFloat(scalar);
            _ = PairedSourceInt(parents[i]);
            refs[i] = new("fe_control", $"fe-{block:D6}", i, name, root?.Index ?? -1, root?.Name ?? "",
                root is null ? ContentHash.Compute([]) : MutationPlanJson.ComputeDirectionalFactsHash(root),
                MutationPlanJson.ComputeDirectionalFactsHash(new { Name = name, Token = PairedSourceUInt(hashes[i]), Rest = KvSemanticHasher.ComputeComplete(initial[i]), Parent = KvSemanticHasher.ComputeComplete(parents[i]) }),
                "preserve_unverified");
        }
        foreach (var field in fe.Children.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var name = field.Key;
            var category = name is "m_CtrlName" or "m_CtrlHash" ? "fe_controls"
                : name is "m_InitPose" or "m_SkelParents" or "m_nNodeCount" or "m_nStaticNodes" ? "fe_nodes"
                : name is "m_SphereRigids" or "m_TaperedCapsuleRigids" or "m_SDFRigids" or "m_BoxRigids" ? "fe_colliders" : "fe_rest_drivers";
            var references = category == "fe_controls" ? refs : category == "fe_nodes" ? refs.Select(r => r with { Kind = "fe_node" }).ToArray() : Array.Empty<PairedConsumerReference>();
            if (category == "fe_colliders")
            {
                if (!field.Value.IsArray) throw DirectionalFailure("PAIRED_CONSUMER_UNSUPPORTED", "Collider inventory must be an array.");
                var nodes = new HashSet<int>();
                foreach (var collider in field.Value.Values)
                {
                    if (!collider.IsCollection || !collider.TryGetValue("nNode", out var node))
                        throw DirectionalFailure("PAIRED_CONSUMER_UNSUPPORTED", "Collider driver reference is uncharacterized.");
                    var index = PairedSourceInt(node);
                    if ((uint)index >= count) throw DirectionalFailure("PAIRED_CONSUMER_UNSUPPORTED", "Collider driver is out of range.");
                    nodes.Add(index);
                }
                references = nodes.Order().Select(i => refs[i] with { Kind = "fe_node" }).ToArray();
            }
            add(category, $"fe-{block:D6}-{name.ToLowerInvariant()}", block, $"m_pFeModel.{name}", KvSemanticHasher.ComputeComplete(field.Value), references);
        }
    }
}
