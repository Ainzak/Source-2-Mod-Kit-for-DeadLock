using System.Collections.Immutable;

namespace S2ModKit.Geometry;

/// <summary>An opaque decoded vertex identity; ordinals are local to one resource.</summary>
public readonly record struct CullingVertexId(int MeshOrdinal, int BufferOrdinal, int VertexIndex);

/// <summary>
/// One field's complete direct contributors and contributing child fields. Null means unavailable,
/// not empty. Edges describe contributor membership, not a coordinate conversion or numeric rule.
/// </summary>
public sealed record CullingContributorField(
    string Id,
    IReadOnlyList<CullingVertexId>? DirectContributors,
    IReadOnlyList<string>? ContributingFields);

/// <summary>All contributors of an affected field, partitioned by whether their position changes.</summary>
public sealed record CullingFieldClosure(
    string FieldId,
    ImmutableArray<CullingVertexId> ChangedContributors,
    ImmutableArray<CullingVertexId> UnchangedContributors);

/// <summary>
/// Pure structural closure over an adapter-supplied complete dependency graph. In particular,
/// a root field includes unchanged contributors from other buffers, meshes and LODs through
/// its child fields. This does not establish coordinate spaces, remap validity, exclusive
/// ownership, inventory completeness against a resource, or permission to mutate a field.
/// Those proofs remain the adapter's responsibility. No persisted contract is introduced.
/// </summary>
public static class CullingContributorClosure
{
    public static ImmutableArray<CullingFieldClosure> Resolve(
        IReadOnlyList<CullingContributorField> fields,
        IReadOnlyList<CullingVertexId> changedVertices)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(changedVertices);
        var changed = new HashSet<CullingVertexId>();
        foreach (var vertex in changedVertices)
        {
            ValidateVertex(vertex);
            if (!changed.Add(vertex))
            {
                throw new ArgumentException("Duplicate changed vertex identity.", nameof(changedVertices));
            }
        }

        // Snapshot and validate every node, including unrelated nodes. A missing collection
        // cannot be silently treated as an empty set to prove a field unaffected.
        var nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (field is null || string.IsNullOrWhiteSpace(field.Id))
            {
                throw new ArgumentException("A field identifier is required.", nameof(fields));
            }

            if (field.DirectContributors is null || field.ContributingFields is null)
            {
                throw new ArgumentException($"Contributors unavailable for '{field.Id}'.", nameof(fields));
            }

            var contributors = new HashSet<CullingVertexId>();
            foreach (var vertex in field.DirectContributors)
            {
                ValidateVertex(vertex);
                contributors.Add(vertex);
            }

            var children = field.ContributingFields.ToArray();
            var uniqueChildren = new HashSet<string>(StringComparer.Ordinal);
            foreach (var child in children)
            {
                if (string.IsNullOrWhiteSpace(child) || !uniqueChildren.Add(child))
                {
                    throw new ArgumentException($"Invalid or duplicate dependency for '{field.Id}'.", nameof(fields));
                }
            }

            if (!nodes.TryAdd(field.Id, new Node(contributors, children)))
            {
                throw new ArgumentException($"Duplicate field '{field.Id}'.", nameof(fields));
            }
        }

        foreach (var (id, node) in nodes)
        {
            foreach (var child in node.Children)
            {
                if (!nodes.TryGetValue(child, out var dependency))
                {
                    throw new ArgumentException($"Unknown dependency '{child}' for '{id}'.", nameof(fields));
                }

                dependency.Parents.Add(id);
            }
        }

        // Iterative child-first evaluation avoids recursion limits. All ordering is ordinal;
        // hash-table/input order never determines serialized result order.
        var ready = new SortedSet<string>(nodes.Where(pair => pair.Value.Remaining == 0)
            .Select(pair => pair.Key), StringComparer.Ordinal);
        var seen = new HashSet<CullingVertexId>();
        var affected = new SortedDictionary<string, CullingFieldClosure>(StringComparer.Ordinal);
        var visited = 0;
        while (ready.Count != 0)
        {
            var id = ready.Min!;
            ready.Remove(id);
            var node = nodes[id];
            seen.UnionWith(node.Contributors);
            if (node.Contributors.Overlaps(changed))
            {
                affected.Add(id, new CullingFieldClosure(id,
                    Canonical(node.Contributors.Where(changed.Contains)),
                    Canonical(node.Contributors.Where(vertex => !changed.Contains(vertex)))));
            }

            foreach (var parentId in node.Parents)
            {
                var parent = nodes[parentId];
                parent.Contributors.UnionWith(node.Contributors);
                if (--parent.Remaining == 0)
                {
                    ready.Add(parentId);
                }
            }

            visited++;
        }

        if (visited != nodes.Count)
        {
            throw new ArgumentException("Cyclic culling contributor dependencies.", nameof(fields));
        }

        if (!changed.IsSubsetOf(seen))
        {
            throw new ArgumentException("Changed vertex absent from the contributor inventory.", nameof(changedVertices));
        }

        return [.. affected.Values];
    }

    private static ImmutableArray<CullingVertexId> Canonical(IEnumerable<CullingVertexId> vertices) =>
        [.. vertices.OrderBy(vertex => vertex.MeshOrdinal)
            .ThenBy(vertex => vertex.BufferOrdinal).ThenBy(vertex => vertex.VertexIndex)];

    private static void ValidateVertex(CullingVertexId vertex)
    {
        if (vertex.MeshOrdinal < 0 || vertex.BufferOrdinal < 0 || vertex.VertexIndex < 0)
        {
            throw new ArgumentException("Vertex identity ordinals must be nonnegative.", nameof(vertex));
        }
    }

    private sealed class Node(HashSet<CullingVertexId> contributors, string[] children)
    {
        public HashSet<CullingVertexId> Contributors { get; } = contributors;
        public string[] Children { get; } = children;
        public List<string> Parents { get; } = [];
        public int Remaining { get; set; } = children.Length;
    }
}
