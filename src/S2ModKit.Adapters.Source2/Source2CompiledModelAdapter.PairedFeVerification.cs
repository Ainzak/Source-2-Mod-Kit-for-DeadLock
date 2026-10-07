using System.Globalization;
using S2ModKit.Application;
using S2ModKit.Domain;
using ValveKeyValue;
using ValveResourceFormat.Utils;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    private static void ReadPairedVerificationFeConsumers(KVObject physics, int block, IReadOnlyList<PairedRootBone> roots,
        Action<string, string, int, string, ContentHash, IEnumerable<PairedConsumerReference>?> add)
    {
        if (physics.Children.Any(k => !PairedPhysFields.Split(',').Contains(k.Key, StringComparer.Ordinal)))
            throw PairedDrift("Unknown PHYS input family.");
        var rootNames = roots.ToDictionary(r => r.Name, StringComparer.Ordinal);
        void TokenPairs(KVObject names, KVObject hashes)
        {
            if (!names.IsArray || !hashes.IsArray || names.Count != hashes.Count) throw PairedDrift("Incomplete authored token arrays.");
            var tokens = new HashSet<uint>();
            for (var i = 0; i < names.Count; i++)
                if (StringToken.Get(PairedSourceString(names[i])) != PairedSourceUInt(hashes[i]) || !tokens.Add(PairedSourceUInt(hashes[i])))
                    throw PairedDrift("Authored token/name identity drift.");
        }
        TokenPairs(ExperimentalArray(physics, "m_boneNames"), ExperimentalArray(physics, "m_bonesHash"));
        if (!physics.TryGetValue("m_pFeModel", out var fe) || fe.ValueType == KVValueType.Null) return;
        if (!fe.IsCollection || fe.Children.Any(k => !PairedFeFields.Split(',').Contains(k.Key, StringComparer.Ordinal)))
            throw PairedDrift("Unknown FE serialized-input family.");
        foreach (var unsupported in new[] { "m_JiggleBones", "m_MorphLayers", "m_MorphSetData" })
            if (fe.TryGetValue(unsupported, out var field) && (!field.IsArray || field.Count > 0))
                throw PairedDrift("Jiggle/morph consumers require separate characterization.");
        var names = ExperimentalArray(fe, "m_CtrlName"); var hashes = ExperimentalArray(fe, "m_CtrlHash");
        TokenPairs(names, hashes);
        var initial = ExperimentalArray(fe, "m_InitPose"); var parents = ExperimentalArray(fe, "m_SkelParents");
        var count = PairedSourceInt(fe["m_nNodeCount"]);
        var statics = PairedSourceInt(fe["m_nStaticNodes"]);
        if (count <= 0 || count != names.Count || count != initial.Count || count != parents.Count || statics < 0 || statics > count)
            throw PairedDrift("FE node/control/rest inventory is incomplete.");
        var refs = new PairedConsumerReference[count];
        for (var i = 0; i < count; i++)
        {
            var name = names[i].ToString(CultureInfo.InvariantCulture); var root = rootNames.GetValueOrDefault(name);
            if (!initial[i].IsArray || initial[i].Count != 8) throw PairedDrift("FE initial pose layout is uncharacterized.");
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
                if (!field.Value.IsArray) throw PairedDrift("Collider inventory must be an array.");
                var nodes = new HashSet<int>();
                foreach (var collider in field.Value.Values)
                {
                    if (!collider.IsCollection || !collider.TryGetValue("nNode", out var node))
                        throw PairedDrift("Collider driver reference is uncharacterized.");
                    var index = PairedSourceInt(node);
                    if ((uint)index >= count) throw PairedDrift("Collider driver is out of range.");
                    nodes.Add(index);
                }
                references = nodes.Order().Select(i => refs[i] with { Kind = "fe_node" }).ToArray();
            }
            add(category, $"fe-{block:D6}-{name.ToLowerInvariant()}", block, $"m_pFeModel.{name}", KvSemanticHasher.ComputeComplete(field.Value), references);
        }
    }
}
