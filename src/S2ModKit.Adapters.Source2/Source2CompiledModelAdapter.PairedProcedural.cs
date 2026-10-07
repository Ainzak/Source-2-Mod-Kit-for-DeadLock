using System.Globalization;
using S2ModKit.Application;
using S2ModKit.Domain;
using ValveKeyValue;
using ValveResourceFormat.ResourceTypes;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter
{
    private static PlannedPairedProceduralInputs ResolvePairedProcedural(ParsedModel parsed, Model model, IReadOnlyList<DirectionalSourceBuffer> context)
    {
        var skeleton = ExperimentalCollection(model.Data, "m_modelSkeleton");
        var names = ExperimentalArray(skeleton, "m_boneName"); var parents = ExperimentalArray(skeleton, "m_nParent");
        var flags = ExperimentalArray(skeleton, "m_nFlag");
        var positions = ExperimentalArray(skeleton, "m_bonePosParent"); var rotations = ExperimentalArray(skeleton, "m_boneRotParent");
        var scales = ExperimentalArray(skeleton, "m_boneScaleParent"); var spheres = ExperimentalArray(skeleton, "m_boneSphere");
        if (names.Count == 0 || names.Count != model.Skeleton.Bones.Length
            || new[] { parents, flags, positions, rotations, scales, spheres }.Any(a => a.Count != names.Count))
            throw DirectionalFailure("PAIRED_PROCEDURAL_INCOMPLETE", "Complete root flag/hierarchy/bind/sphere arrays are required.");
        var roots = new PairedRootBone[names.Count];
        for (var i = 0; i < roots.Length; i++)
        {
            var name = names[i].ToString(CultureInfo.InvariantCulture);
            if (names[i].ValueType != KVValueType.String || name != model.Skeleton.Bones[i].Name
                || !positions[i].IsArray || positions[i].Count != 3 || !rotations[i].IsArray || rotations[i].Count != 4)
                throw DirectionalFailure("PAIRED_PROCEDURAL_INCOMPLETE", "Root name/bind representation differs from the decoded skeleton.");
            foreach (var scalar in positions[i].Values.Concat(rotations[i].Values).Append(scales[i])) _ = ExperimentalFloat(scalar);
            roots[i] = new(i, name, PairedSourceInt(parents[i]), PairedSourceUInt(flags[i]),
                model.Skeleton.Bones[i].IsProceduralCloth, MutationPlanJson.ComputeDirectionalFactsHash(new
                {
                    Position = KvSemanticHasher.ComputeComplete(positions[i]),
                    Rotation = KvSemanticHasher.ComputeComplete(rotations[i]),
                    Scale = BitConverter.SingleToUInt32Bits(ExperimentalFloat(scales[i]))
                }));
        }
        var skins = context.Select(c =>
        {
            if (c.Frame is null) throw DirectionalFailure("PAIRED_PROCEDURAL_INCOMPLETE", "All context needs characterized frame words.");
            var mesh = parsed.MeshesByOrdinal[c.Facts.MeshOrdinal];
            var descriptor = ExperimentalArray(mesh.Descriptor, "m_vertexBuffers")[c.Facts.VertexBufferOrdinal];
            var fields = ExperimentalArray(descriptor, "m_inputLayoutFields");
            KVObject? Field(string semantic) => fields.Values.SingleOrDefault(f => f["m_pSemanticName"].ToString(CultureInfo.InvariantCulture).Equals(semantic, StringComparison.OrdinalIgnoreCase));
            var index = Field("BLENDINDICES") ?? throw DirectionalFailure("PAIRED_PROCEDURAL_INCOMPLETE", "Complete cooked indices are required.");
            var weight = Field("BLENDWEIGHT");
            var format = PairedSourceUInt(index["m_Format"]);
            var contributors = roots.Select(r =>
            {
                var indices = c.RootContributors.GetValueOrDefault(r.Index) ?? [];
                return new PairedRootContributorSet(r.Index, indices, DirectionalContractValidator.VertexSetHash(indices));
            }).ToArray();
            return new PairedSkinningBufferFacts(c.Facts.Lod, c.Facts.MeshOrdinal, c.Facts.VertexBufferOrdinal, c.Position.Stride,
                c.Position.Offset, c.Frame.Offset, format, PairedSourceInt(index["m_nOffset"]),
                format switch { 30 => 4, 12 or 14 => 8, 4 => 16, _ => 0 }, weight is null ? 0 : PairedSourceUInt(weight["m_Format"]),
                weight is null ? -1 : PairedSourceInt(weight["m_nOffset"]),
                weight is null ? 0 : PairedSourceUInt(weight["m_Format"]) == 28 ? 4 : 8,
                PairedSourceInt(ExperimentalCollection(mesh.Block.Data, "m_skeleton")["m_nBoneWeightCount"]),
                c.Facts.SkinningHash, c.Facts.SkinningHash, c.Facts.RootRenderRemapHash, contributors, MutationPlanJson.ComputeDirectionalFactsHash(contributors));
        }).ToArray();
        var procedural = roots.Where(r => r.Procedural).Select(r =>
        {
            var sets = context.Select(c => DirectionalProtectedWords(c, c.RootContributors.GetValueOrDefault(r.Index) ?? [], "procedural")).ToArray();
            return new PairedProceduralBoneContributors(r.Index, r.Name, sets, DirectionalContractValidator.ContributorSetHash(sets));
        }).ToArray();
        var union = context.Select(c => DirectionalProtectedWords(c, roots.Where(r => r.Procedural)
            .SelectMany(r => c.RootContributors.GetValueOrDefault(r.Index) ?? []).Distinct().Order().ToArray(), "procedural union")).ToArray();
        var dataIndex = parsed.Resource.Blocks.Select((b, i) => (b, i)).Single(row => ReferenceEquals(row.b, model)).i;
        var families = ResolvePairedConsumers(parsed, model, roots, context, dataIndex);
        return new(dataIndex, ContentHash.Compute(parsed.Envelope.Blocks[dataIndex].Payload.Span), KvSemanticHasher.ComputeComplete(skeleton),
            roots, MutationPlanJson.ComputeDirectionalFactsHash(roots), skins, procedural, union, MutationPlanJson.ComputeDirectionalFactsHash(union),
            families, MutationPlanJson.ComputeDirectionalFactsHash(families));
    }
}
