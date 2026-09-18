using System.Collections.Immutable;
using System.Globalization;

namespace S2ModKit.Geometry;

/// <summary>
/// Adapter-neutral exclusive vertex-ownership analysis over one decoded index
/// buffer. The analyzer is a pure function of its arguments: it never mutates
/// caller collections, never hashes string identifiers with process-randomized
/// hash codes, and produces byte-identical output regardless of the order of
/// the selected-identifier input.
/// </summary>
public static class VertexOwnershipAnalyzer
{
    public static VertexOwnership Analyze(
        IReadOnlyList<uint> indices,
        int vertexCount,
        IReadOnlyList<DrawCallRange> drawCalls,
        IReadOnlyList<string> selectedDrawCallIds)
    {
        ArgumentNullException.ThrowIfNull(indices);
        ArgumentNullException.ThrowIfNull(drawCalls);
        ArgumentNullException.ThrowIfNull(selectedDrawCallIds);
        if (vertexCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(vertexCount),
                vertexCount,
                "The declared vertex count must not be negative.");
        }

        if (selectedDrawCallIds.Count == 0)
        {
            throw new ArgumentException(
                "At least one selected draw-call identifier is required.",
                nameof(selectedDrawCallIds));
        }

        var drawCallIdentifiers = ValidateAndSortIdentifiers(
            drawCalls.Select(drawCall => drawCall.Id),
            "Draw call",
            nameof(drawCalls));
        var selectedIdentifiers = ValidateAndSortIdentifiers(
            selectedDrawCallIds,
            "Selected draw call",
            nameof(selectedDrawCallIds));
        ValidateSelectedIdentifiers(
            drawCallIdentifiers,
            selectedIdentifiers,
            nameof(selectedDrawCallIds));
        ValidateRanges(indices, drawCalls);

        List<int> selectedVertices = [];
        List<int> nonSelectedVertices = [];
        var selectedDrawCallCount = 0;
        var nonSelectedDrawCallCount = 0;
        long selectedIndexCount = 0;
        long nonSelectedIndexCount = 0;
        foreach (var drawCall in drawCalls)
        {
            if (Array.BinarySearch(selectedIdentifiers, drawCall.Id, StringComparer.Ordinal) >= 0)
            {
                selectedDrawCallCount++;
                selectedIndexCount = checked(selectedIndexCount + drawCall.IndexCount);
                CollectVertices(indices, vertexCount, drawCall, selectedVertices);
            }
            else
            {
                nonSelectedDrawCallCount++;
                nonSelectedIndexCount = checked(nonSelectedIndexCount + drawCall.IndexCount);
                CollectVertices(indices, vertexCount, drawCall, nonSelectedVertices);
            }
        }

        var selected = VertexSetCanonicalizer.Canonicalize(selectedVertices, nameof(selectedVertices));
        var nonSelected = VertexSetCanonicalizer.Canonicalize(nonSelectedVertices, nameof(nonSelectedVertices));
        var shared = IntersectSorted(selected, nonSelected);
        return new VertexOwnership(
            vertexCount,
            indices.Count,
            selectedDrawCallCount,
            nonSelectedDrawCallCount,
            selectedIndexCount,
            nonSelectedIndexCount,
            selected,
            nonSelected,
            shared);
    }

    private static string[] ValidateAndSortIdentifiers(
        IEnumerable<string> identifiers,
        string description,
        string parameterName)
    {
        var canonical = identifiers.ToArray();
        for (var index = 0; index < canonical.Length; index++)
        {
            var identifier = canonical[index];
            if (string.IsNullOrWhiteSpace(identifier))
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"{description} {index} has an empty identifier."),
                    parameterName);
            }
        }

        Array.Sort(canonical, StringComparer.Ordinal);
        for (var index = 1; index < canonical.Length; index++)
        {
            if (string.Equals(canonical[index - 1], canonical[index], StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"{description} identifier '{canonical[index]}' is duplicated."),
                    parameterName);
            }
        }

        return canonical;
    }

    private static void ValidateSelectedIdentifiers(
        string[] drawCallIdentifiers,
        string[] selectedIdentifiers,
        string parameterName)
    {
        foreach (var identifier in selectedIdentifiers)
        {
            if (Array.BinarySearch(drawCallIdentifiers, identifier, StringComparer.Ordinal) < 0)
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"Selected draw call identifier '{identifier}' does not exist."),
                    parameterName);
            }
        }
    }

    private static void ValidateRanges(IReadOnlyList<uint> indices, IReadOnlyList<DrawCallRange> drawCalls)
    {
        for (var index = 0; index < drawCalls.Count; index++)
        {
            var drawCall = drawCalls[index];
            if (drawCall.IndexStart < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(drawCalls),
                    string.Create(CultureInfo.InvariantCulture, $"Draw call '{drawCall.Id}' has a negative index start."),
                    "Index starts must not be negative.");
            }

            if (drawCall.IndexCount < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(drawCalls),
                    string.Create(CultureInfo.InvariantCulture, $"Draw call '{drawCall.Id}' has a negative index count."),
                    "Index counts must not be negative.");
            }

            long rangeEnd;
            try
            {
                rangeEnd = checked(drawCall.IndexStart + drawCall.IndexCount);
            }
            catch (OverflowException exception)
            {
                throw new OverflowException(
                    string.Create(CultureInfo.InvariantCulture, $"Draw call '{drawCall.Id}' has an index range that overflows."),
                    exception);
            }

            if (rangeEnd > indices.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(drawCalls),
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Draw call '{drawCall.Id}' range [{drawCall.IndexStart}..{rangeEnd}) exceeds the {indices.Count}-entry index sequence."),
                    "Every draw-call range must lie inside the decoded index sequence.");
            }

            if (drawCall.BaseVertex < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(drawCalls),
                    string.Create(CultureInfo.InvariantCulture, $"Draw call '{drawCall.Id}' has a negative base vertex."),
                    "Base vertices must not be negative.");
            }
        }
    }

    private static void CollectVertices(
        IReadOnlyList<uint> indices,
        int vertexCount,
        DrawCallRange drawCall,
        List<int> vertices)
    {
        long position = drawCall.IndexStart;
        var end = drawCall.IndexStart + drawCall.IndexCount;
        while (position < end)
        {
            var vertex = checked((long)indices[(int)position] + drawCall.BaseVertex);
            if (vertex < 0 || vertex >= vertexCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(drawCall),
                    vertex,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Draw call '{drawCall.Id}' decodes vertex {vertex}, which is outside the declared range [0..{vertexCount})."));
            }

            vertices.Add((int)vertex);
            position++;
        }
    }

    private static ImmutableArray<int> IntersectSorted(ImmutableArray<int> left, ImmutableArray<int> right)
    {
        List<int> shared = [];
        int leftIndex = 0;
        int rightIndex = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            if (left[leftIndex] < right[rightIndex])
            {
                leftIndex++;
            }
            else if (left[leftIndex] > right[rightIndex])
            {
                rightIndex++;
            }
            else
            {
                shared.Add(left[leftIndex]);
                leftIndex++;
                rightIndex++;
            }
        }

        return [.. shared];
    }
}
