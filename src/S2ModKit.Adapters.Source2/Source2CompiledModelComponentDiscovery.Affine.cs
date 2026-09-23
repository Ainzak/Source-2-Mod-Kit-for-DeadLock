using System.Globalization;
using S2ModKit.Application;
using S2ModKit.Domain;

namespace S2ModKit.Adapters.Source2;

public sealed partial class Source2CompiledModelAdapter : IAffineComponentCapabilityAnalyzer
{
    Task<IReadOnlyList<ComponentCapabilityAnalysis>> IAffineComponentCapabilityAnalyzer.AnalyzeAffineAsync(
        ComponentCapabilityAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateCapabilityRequest(request.Input, request.Model);
        ValidateSelectionIdentities(request.Selections);
        if (request.Selections.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<ComponentCapabilityAnalysis>>([]);
        }

        using var parsed = Parse(request.Input, retainGeometryAnalysis: true);
        ValidateSnapshotAgreement(request.Model, parsed.Snapshot);
        var profile = CreateCapabilityProfile(request.Input, parsed);
        var results = request.Selections
            .OrderBy(selection => selection.SelectionId, StringComparer.Ordinal)
            .Select(selection =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return AssessAffineSelection(request, parsed, profile, selection);
            })
            .ToArray();
        return Task.FromResult<IReadOnlyList<ComponentCapabilityAnalysis>>(results);
    }

    private static ComponentCapabilityAnalysis AssessAffineSelection(
        ComponentCapabilityAnalysisRequest request,
        ParsedModel parsed,
        ComponentCapabilityProfile profile,
        ComponentCapabilitySelection selection)
    {
        var identityIssue = ValidateSelectionIdentity(selection);
        if (identityIssue is not null)
        {
            return AffineAssessment(selection.SelectionId, ComponentDiscoveryContract.Ambiguous, identityIssue);
        }

        var mapping = MapSelectionToMeshes(selection, profile);
        if (mapping.Issue is not null)
        {
            return AffineAssessment(selection.SelectionId, ComponentDiscoveryContract.Ambiguous, mapping.Issue);
        }

        var expectedLods = profile.PresentLods.Order().ToArray();
        if (mapping.SelectedMeshes.Count != expectedLods.Length
            || !mapping.SelectedMeshes.Select(item => item.Mesh.Lod).Order().SequenceEqual(expectedLods))
        {
            return AffineAssessment(selection.SelectionId, ComponentDiscoveryContract.Unsupported,
                Reason("TRANSFORM_GEOMETRY_LOD_COVERAGE_INCOMPLETE", "Affine selection requires exactly one selected mesh in every present LOD."));
        }

        var vertexCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var mapped in mapping.SelectedMeshes)
        {
            var geometry = mapped.Mesh.GeometryAnalysis;
            if (geometry is null)
            {
                return AffineAssessment(selection.SelectionId, ComponentDiscoveryContract.Unsupported,
                    Reason("AFFINE_LAYOUT_UNSUPPORTED", "The selected mesh has no characterized decoded geometry."));
            }

            var selectedIds = mapped.SelectedDrawCalls.Select(call => call.Id).ToHashSet(StringComparer.Ordinal);
            var count = geometry.DrawCalls
                .Where(call => selectedIds.Contains(call.Snapshot.DrawCallId))
                .SelectMany(call => call.VertexIndices.Select(vertex => (call.Snapshot.VertexBufferOrdinal, vertex)))
                .Distinct()
                .Count();
            if (count == 0)
            {
                return AffineAssessment(selection.SelectionId, ComponentDiscoveryContract.Unsupported,
                    Reason("AFFINE_LAYOUT_UNSUPPORTED", "The selected draw calls have no decoded affine vertices."));
            }

            vertexCounts.Add(mapped.Mesh.Lod.ToString(CultureInfo.InvariantCulture), count);
        }

        var operation = new TransformComponentOperation
        {
            Version = 4,
            Granularity = "draw_call_vertices",
            ExpectedMatchesByLod = expectedLods.ToDictionary(
                lod => lod.ToString(CultureInfo.InvariantCulture),
                lod => selection.SelectedDrawCalls.Count(call => call.Lod == lod),
                StringComparer.Ordinal),
            ExpectedVerticesByLod = vertexCounts,
            Transform = new ComponentTransform
            {
                Pivot = new TransformPivot { Kind = "selection_bounds_center", ReferenceLod = expectedLods[0] },
                Scale = new TransformVector3 { X = 1.1f, Y = 1.1f, Z = 1.1f },
                Rotation = new TransformRotation { Kind = "identity" },
                Frame = new TransformFrame { Kind = "model" },
            },
            Limits = new TransformLimits { MaximumVertexDisplacement = RecipeValidator.MaximumTransformDisplacement },
        };

        try
        {
            var planned = PlanAffineTransform(
                new TransformPlanningRequest(request.Input, request.Model, operation, selection.SelectedDrawCalls),
                parsed);
            var geometry = planned.AffineTransformTarget!.GeometryTargets
                .OrderBy(target => target.Lod)
                .Select(target => new ComponentGeometryLodFacts(
                    target.Lod,
                    target.SelectedVertexCount,
                    target.VertexSetHash,
                    true))
                .ToArray();
            return new ComponentCapabilityAnalysis(
                selection.SelectionId,
                TransformCapabilityOperation,
                4,
                ComponentDiscoveryContract.Available,
                [Reason("COMPONENT_CAPABILITY_AVAILABLE", "The selection satisfies a characterized affine geometry profile across every LOD.")],
                geometry);
        }
        catch (S2ModKitException exception)
        {
            return AffineAssessment(
                selection.SelectionId,
                exception.Error.Category == ErrorCategory.InputOrResolution
                    ? ComponentDiscoveryContract.Ambiguous
                    : ComponentDiscoveryContract.Unsupported,
                Reason(exception.Error.Code, exception.Error.Summary));
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidDataException
            or InvalidOperationException
            or OverflowException
            or IndexOutOfRangeException
            or KeyNotFoundException)
        {
            return AffineAssessment(selection.SelectionId, ComponentDiscoveryContract.Unsupported,
                Reason("AFFINE_LAYOUT_UNSUPPORTED", $"The selected geometry does not satisfy the characterized affine profile: {exception.Message}"));
        }
    }

    private static ComponentCapabilityAnalysis AffineAssessment(
        string selectionId,
        string availability,
        ComponentCapabilityReason reason) => new(
        selectionId,
        TransformCapabilityOperation,
        4,
        availability,
        [reason],
        []);
}
