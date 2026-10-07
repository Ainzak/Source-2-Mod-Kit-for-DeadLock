using System.Globalization;
using S2ModKit.Application;
using S2ModKit.Domain;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Utils;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    private static PairedConsumerFamily[] ResolvePairedConsumers(ParsedModel parsed, Model model,
        PairedRootBone[] roots, IReadOnlyList<DirectionalSourceBuffer> context, int dataIndex)
    {
        var rows = PairedContractValidator.ConsumerCategories.ToDictionary(c => c, _ => new List<PairedConsumerRecord>(), StringComparer.Ordinal);
        var names = roots.ToDictionary(r => r.Name, StringComparer.Ordinal);
        var tokens = roots.ToDictionary(r => StringToken.Get(r.Name)); // A collision is ambiguous and rejects.
        var attachments = new Dictionary<uint, (string Name, List<ContentHash> Hashes)>();
        PairedConsumerReference RootRef(string kind, string scope, int index, string name, ContentHash identity)
        {
            var root = names.GetValueOrDefault(name);
            return new(kind, scope, index, name, root?.Index ?? -1, root?.Name ?? "",
                root is null ? ContentHash.Compute([]) : MutationPlanJson.ComputeDirectionalFactsHash(root), identity, "preserve_unverified");
        }
        void Add(string category, string id, int block, string path, ContentHash hash, IEnumerable<PairedConsumerReference>? references = null)
        {
            var refs = (references ?? []).OrderBy(r => r.Kind, StringComparer.Ordinal).ThenBy(r => r.ScopeId, StringComparer.Ordinal).ThenBy(r => r.SourceIndex).ToArray();
            rows[category].Add(new(id, block, path, hash, hash, "preserve_unverified", refs, MutationPlanJson.ComputeDirectionalFactsHash(refs)));
        }
        Add("root_skeleton", "root", dataIndex, "$payload", ContentHash.Compute(parsed.Envelope.Blocks[dataIndex].Payload.Span),
            roots.Select(r => RootRef("root_bone", "root", r.Index, r.Name, MutationPlanJson.ComputeDirectionalFactsHash(r))));
        if (ExperimentalArray(model.Data, "m_boneFlexDrivers").Count != 0)
            throw DirectionalFailure("PAIRED_CONSUMER_UNSUPPORTED", "Unclassified bone flex drivers cannot be preserved by this profile.");
        foreach (var mesh in parsed.MeshesByOrdinal.Values.OrderBy(m => m.MeshOrdinal))
        {
            var scope = $"mesh-{mesh.MeshOrdinal:D6}"; var skeleton = ExperimentalCollection(mesh.Block.Data, "m_skeleton");
            var bones = ExperimentalArray(skeleton, "m_bones"); var remap = model.GetRemapTable(mesh.MeshOrdinal);
            if (remap is null || remap.Length < bones.Count || ExperimentalArray(mesh.Block.Data, "m_constraints").Count != 0)
                throw DirectionalFailure("PAIRED_CONSUMER_UNSUPPORTED", "Unclassified mesh constraints or incomplete render remaps reject.");
            for (var i = 0; i < bones.Count; i++)
            {
                var bone = bones[i]; var name = PairedSourceString(bone["m_boneName"]);
                if (name != roots[remap[i]].Name || bone.Children.Any(k => k.Key is not ("m_boneName" or "m_parentName" or "m_invBindPose" or "m_bbox" or "m_flSphereRadius")))
                    throw DirectionalFailure("PAIRED_CONSUMER_UNSUPPORTED", "Render/root names or bone field inventory is ambiguous.");
                foreach (var field in new[] { "m_boneName", "m_parentName", "m_invBindPose" })
                    Add("render_skeleton", $"render-{mesh.MeshOrdinal:D6}-{i:D6}-{field.ToLowerInvariant()}", mesh.BlockIndex,
                        $"m_skeleton.m_bones[{i}].{field}", KvSemanticHasher.ComputeComplete(bone[field]),
                        [RootRef("render_bone", scope, i, name, MutationPlanJson.ComputeDirectionalFactsHash(new
                        {
                            Name = KvSemanticHasher.ComputeComplete(bone["m_boneName"]), Parent = KvSemanticHasher.ComputeComplete(bone["m_parentName"]),
                            Bind = KvSemanticHasher.ComputeComplete(bone["m_invBindPose"])
                        }))]);
            }
            Add("render_skeleton", $"parents-{mesh.MeshOrdinal:D6}", mesh.BlockIndex, "m_skeleton.m_boneParents", KvSemanticHasher.ComputeComplete(skeleton["m_boneParents"]));
            Add("render_skeleton", $"hitboxes-{mesh.MeshOrdinal:D6}", mesh.BlockIndex, "m_hitboxsets", KvSemanticHasher.ComputeComplete(mesh.Block.Data["m_hitboxsets"]));
            Add("skin_remap_weights", $"skin-count-{mesh.MeshOrdinal:D6}", mesh.BlockIndex, "m_skeleton.m_nBoneWeightCount", KvSemanticHasher.ComputeComplete(skeleton["m_nBoneWeightCount"]));
            foreach (var c in context.Where(c => c.Facts.MeshOrdinal == mesh.MeshOrdinal))
                Add("skin_remap_weights", $"skin-words-{mesh.MeshOrdinal:D6}-{c.Facts.VertexBufferOrdinal:D6}", c.Facts.VertexResourceBlockIndex, "skinning_words", c.Facts.SkinningHash);
            var attached = ExperimentalArray(mesh.Block.Data, "m_attachments");
            for (var i = 0; i < attached.Count; i++)
            {
                var value = ExperimentalCollection(attached[i], "value"); var name = PairedSourceString(value["m_name"]);
                if (PairedSourceString(attached[i]["key"]) != name || string.IsNullOrWhiteSpace(name))
                    throw DirectionalFailure("PAIRED_CONSUMER_UNSUPPORTED", "Attachment key/name drift.");
                var influence = ExperimentalArray(value, "m_influenceNames"); var weights = ExperimentalArray(value, "m_influenceWeights");
                var rootTransforms = ExperimentalArray(value, "m_bInfluenceRootTransform"); var count = PairedSourceInt(value["m_nInfluences"]);
                if (count is < 1 or > 3 || influence.Count != 3 || weights.Count != 3 || rootTransforms.Count != 3)
                    throw DirectionalFailure("PAIRED_CONSUMER_UNSUPPORTED", "Attachment influence closure is incomplete.");
                var refs = new List<PairedConsumerReference>();
                for (var j = 0; j < 3; j++)
                {
                    var rootTransform = PairedSourceBool(rootTransforms[j]); var weight = ExperimentalFloat(weights[j]); var boneName = PairedSourceString(influence[j]);
                    if (weight < 0 || (j >= count && weight != 0)) throw DirectionalFailure("PAIRED_CONSUMER_UNSUPPORTED", "Undeclared attachment weight.");
                    if (weight == 0) continue;
                    if (!names.ContainsKey(boneName) && !(boneName.Length == 0 && rootTransform))
                        throw DirectionalFailure("PAIRED_CONSUMER_UNSUPPORTED", "Attachment influence lacks an exact root identity.");
                    var reference = RootRef("attachment", $"{scope}-attachment-{i:D6}", j, boneName, KvSemanticHasher.ComputeComplete(value));
                    refs.Add(reference with { SourceName = name });
                }
                var hash = KvSemanticHasher.ComputeComplete(value); var token = StringToken.Get(name);
                if (attachments.TryGetValue(token, out var previous) && previous.Name != name) throw DirectionalFailure("PAIRED_CONSUMER_UNSUPPORTED", "Attachment token collision.");
                if (!attachments.ContainsKey(token)) attachments.Add(token, (name, []));
                attachments[token].Hashes.Add(hash);
                Add("attachment", $"attachment-{mesh.MeshOrdinal:D6}-{i:D6}", mesh.BlockIndex, $"m_attachments[{i}]", KvSemanticHasher.ComputeComplete(attached[i]), refs);
            }
        }
        var keys = model.KeyValues;
        if (keys.TryGetValue("BoneConstraintList", out var constraints))
        {
            if (!constraints.IsArray) throw DirectionalFailure("PAIRED_CONSUMER_UNSUPPORTED", "Constraint list must be a complete array.");
            for (var i = 0; i < constraints.Count; i++)
            {
                var constraint = constraints[i]; var kind = PairedSourceString(constraint["_class"]);
                if (kind is not ("CAimConstraint" or "COrientConstraint" or "CPointConstraint" or "CTiltTwistConstraint"))
                    throw DirectionalFailure("PAIRED_CONSUMER_UNSUPPORTED", "Unknown model constraint family.");
                var refs = new List<PairedConsumerReference>();
                foreach (var role in new[] { "m_slaves", "m_targets" })
                {
                    var list = ExperimentalArray(constraint, role);
                    for (var j = 0; j < list.Count; j++)
                    {
                        var value = list[j]; var token = PairedSourceUInt(value["m_nBoneHash"]);
                        var isAttachment = value.TryGetValue("m_bIsAttachment", out var flag) && PairedSourceBool(flag);
                        var scope = $"constraint-{i:D6}-{role[2..]}";
                        if (isAttachment)
                        {
                            if (!attachments.TryGetValue(token, out var attachment)) throw DirectionalFailure("PAIRED_CONSUMER_UNSUPPORTED", "Constraint attachment token is unmapped.");
                            refs.Add(new("attachment", scope, j, attachment.Name, -1, "", ContentHash.Compute([]),
                                MutationPlanJson.ComputeDirectionalFactsHash(attachment.Hashes), "preserve_unverified"));
                        }
                        else
                        {
                            if (!tokens.TryGetValue(token, out var bone)) throw DirectionalFailure("PAIRED_CONSUMER_UNSUPPORTED", "Constraint bone token is unmapped.");
                            refs.Add(RootRef("root_bone", scope, bone.Index, bone.Name, MutationPlanJson.ComputeDirectionalFactsHash(bone)));
                        }
                    }
                }
                Add("model_constraint", $"constraint-{i:D6}", dataIndex, $"m_modelInfo.m_keyValueText.BoneConstraintList[{i}]", KvSemanticHasher.ComputeComplete(constraint), refs);
            }
        }
        foreach (var block in parsed.Envelope.Blocks.Where(b => b.Type == "PHYS"))
        {
            if (parsed.Resource.Blocks[block.Index] is not PhysAggregateData physics) throw DirectionalFailure("PAIRED_CONSUMER_UNSUPPORTED", "PHYS requires a decoded inventory.");
            var data = physics.Data;
            Add("physics", $"physics-{block.Index:D6}", block.Index, "$payload", ContentHash.Compute(block.Payload.Span));
            AddPairedFeConsumers(data, block.Index, roots, Add);
        }
        return PairedContractValidator.ConsumerCategories.Select(c => new PairedConsumerFamily(c, rows[c].Count != 0,
            rows[c].OrderBy(r => r.ConsumerId, StringComparer.Ordinal).ToArray())).ToArray();
    }
}
