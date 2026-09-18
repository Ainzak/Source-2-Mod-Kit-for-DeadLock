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
    public Task<IReadOnlyList<ResourceDependency>> ReadDependenciesAsync(
        ArtifactContent artifact,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanReadDependencies(artifact))
        {
            throw Errors.Unsupported("DEPENDENCY_READER_CAPABILITY_UNAVAILABLE", $"The Source 2 dependency reader cannot inspect '{artifact.LogicalPath}'.", "Provide a compiled Source 2 resource whose logical path ends in _c.");
        }

        if (ContentHash.Compute(artifact.Bytes.Span) != artifact.ContentHash)
        {
            throw Errors.Input("INPUT_HASH_DRIFT", $"Resource '{artifact.LogicalPath}' no longer matches its supplied content hash.", "Reload the immutable resource before dependency discovery.");
        }

        using var resource = new Resource { FileName = artifact.LogicalPath };
        try
        {
            var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(artifact.Bytes.Span[..4]);
            if (declaredSize < 16 || declaredSize > artifact.Bytes.Length)
            {
                throw Errors.Unsupported(
                    "RESOURCE_DECLARED_PREFIX_INVALID",
                    $"Resource '{artifact.LogicalPath}' declares {declaredSize} bytes inside a {artifact.Bytes.Length}-byte artifact.",
                    "Use an intact compiled resource whose declared prefix fits its stored payload.");
            }

            var declaredContent = artifact.Bytes[..checked((int)declaredSize)];
            _ = ResourceEnvelopeReader.Read(declaredContent);
            using var stream = new MemoryStream(artifact.Bytes.ToArray(), writable: false);
            resource.Read(stream, verifyFileSize: declaredSize == artifact.Bytes.Length, leaveOpen: true);
            var externalReferences = resource.ExternalReferences?.ResourceRefInfoList;
            if (externalReferences is not null && externalReferences.Count > MaximumDirectDependencyCount)
            {
                throw Errors.Unsupported("RESOURCE_DEPENDENCY_COUNT_UNSUPPORTED", $"Resource '{artifact.LogicalPath}' declares {externalReferences.Count} direct dependencies; the supported limit is {MaximumDirectDependencyCount}.", "Use a bounded resource graph or add a reviewed higher-capacity profile.");
            }

            var references = (externalReferences ?? [])
                .Select(reference => new ResourceDependency(
                    ToCompiledLogicalPath(reference.Name),
                    reference.Id.ToString("x16", CultureInfo.InvariantCulture)))
                .OrderBy(reference => reference.LogicalPath, StringComparer.Ordinal)
                .ThenBy(reference => reference.ReferenceId, StringComparer.Ordinal)
                .ToArray();
            var collision = references.GroupBy(reference => reference.LogicalPath, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Select(reference => reference.ReferenceId).Distinct(StringComparer.Ordinal).Skip(1).Any());
            if (collision is not null)
            {
                throw Errors.Unsupported("RESOURCE_REFERENCE_ID_CONFLICT", $"Resource '{artifact.LogicalPath}' maps multiple reference IDs to '{collision.Key}'.", "Use an intact resource with an unambiguous RERL table.");
            }

            return Task.FromResult<IReadOnlyList<ResourceDependency>>(references
                .DistinctBy(reference => (reference.LogicalPath, reference.ReferenceId))
                .ToArray());
        }
        catch (S2ModKitException exception)
        {
            var context = new Dictionary<string, string>(exception.Error.Context ?? new Dictionary<string, string>(), StringComparer.Ordinal)
            {
                ["logicalPath"] = artifact.LogicalPath,
            };
            throw new S2ModKitException(
                exception.Error with
                {
                    Summary = $"Resource '{artifact.LogicalPath}': {exception.Error.Summary}",
                    Context = context,
                },
                exception);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException or NotSupportedException or ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new S2ModKitException(
                new S2Error("RESOURCE_DEPENDENCY_LAYOUT_UNSUPPORTED", "resource_dependencies", $"VRF could not read the external references of '{artifact.LogicalPath}'.", "Inspect the resource with a compatible Source 2 tool and add a reviewed RERL profile.", ErrorCategory.UnsupportedCapability),
                exception);
        }
    }

    private static string ToCompiledLogicalPath(string resourceName)
    {
        var normalized = StableIdentity.NormalizePath(resourceName);
        return normalized.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? normalized : $"{normalized}_c";
    }
}
