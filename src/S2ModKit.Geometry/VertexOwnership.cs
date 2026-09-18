using System.Collections.Immutable;

namespace S2ModKit.Geometry;

/// <summary>
/// One adapter-neutral draw-call index range: a stable string identifier, the
/// half-open index window [ <see cref="IndexStart"/>, <see cref="IndexStart"/> + <see cref="IndexCount"/> ),
/// and the signed base/applied vertex added to every decoded index. Structural
/// validation happens in <see cref="VertexOwnershipAnalyzer.Analyze"/>.
/// </summary>
public sealed record DrawCallRange(
    string Id,
    long IndexStart,
    long IndexCount,
    int BaseVertex);

/// <summary>
/// The canonical result of an exclusive vertex-ownership analysis. Both vertex
/// sequences are strictly ascending and distinct. <see cref="SelectedVertices"/>
/// is the union of vertices referenced by the selected draw calls;
/// <see cref="SharedWithNonSelectedVertices"/> is its intersection with the
/// vertices of every non-selected draw call over the same decoded buffer.
/// The sequences are immutable snapshots; they never alias caller inputs and
/// their derived counts cannot become inconsistent with their contents.
/// </summary>
public sealed class VertexOwnership
{
    internal VertexOwnership(
        int declaredVertexCount,
        long decodedIndexCount,
        int selectedDrawCallCount,
        int nonSelectedDrawCallCount,
        long selectedIndexCount,
        long nonSelectedIndexCount,
        ImmutableArray<int> selectedVertices,
        ImmutableArray<int> nonSelectedVertices,
        ImmutableArray<int> sharedWithNonSelectedVertices)
    {
        DeclaredVertexCount = declaredVertexCount;
        DecodedIndexCount = decodedIndexCount;
        SelectedDrawCallCount = selectedDrawCallCount;
        NonSelectedDrawCallCount = nonSelectedDrawCallCount;
        SelectedIndexCount = selectedIndexCount;
        NonSelectedIndexCount = nonSelectedIndexCount;
        SelectedVertices = selectedVertices;
        NonSelectedVertices = nonSelectedVertices;
        SharedWithNonSelectedVertices = sharedWithNonSelectedVertices;
    }

    public int DeclaredVertexCount { get; }

    public long DecodedIndexCount { get; }

    public int SelectedDrawCallCount { get; }

    public int NonSelectedDrawCallCount { get; }

    public long SelectedIndexCount { get; }

    public long NonSelectedIndexCount { get; }

    public int SelectedVertexCount => SelectedVertices.Length;

    public int SharedVertexCount => SharedWithNonSelectedVertices.Length;

    public ImmutableArray<int> SelectedVertices { get; }

    public ImmutableArray<int> NonSelectedVertices { get; }

    public ImmutableArray<int> SharedWithNonSelectedVertices { get; }
}
