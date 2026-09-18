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

public sealed partial class Source2CompiledModelAdapter : IModelInspector, IResourceDependencyReader, IModelRewriter, ITransformOperationPlanner
{
    private const int MaximumEmbeddedMeshCount = 4096;
    private const int MaximumDrawCallsPerMesh = 65536;
    private const int MaximumDirectDependencyCount = 4096;
    private readonly string? meshOptimizerPath;

    public Source2CompiledModelAdapter(string? meshOptimizerPath = null)
    {
        this.meshOptimizerPath = string.IsNullOrWhiteSpace(meshOptimizerPath)
            ? null
            : meshOptimizerPath;
        GeometryCodecCapability = MeshOptimizerCodecProbe.Probe(this.meshOptimizerPath);
    }

    public MeshOptimizerCodecCapability GeometryCodecCapability { get; }

    public string AdapterName => "source2_vrf";

    public string AdapterVersion
    {
        get
        {
            var version = typeof(Resource).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(Resource).Assembly.GetName().Version?.ToString()
                ?? "unknown";
            return $"2+vrf-{version}";
        }
    }

    public IReadOnlyDictionary<string, string> ComponentVersions
    {
        get
        {
            var versions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["s2modkit.adapter.source2"] = "2",
                ["nuget.valveresourceformat"] = GetAssemblyVersion(typeof(Resource).Assembly),
                ["nuget.valvekeyvalue"] = GetAssemblyVersion(typeof(KVObject).Assembly),
            };
            if (GeometryCodecCapability.Identity is { } identity)
            {
                versions["native.meshoptimizer"] = $"{identity.Version}+{identity.BinaryHash}";
            }

            return versions;
        }
    }

    public bool CanInspect(ArtifactContent artifact) =>
        artifact.Bytes.Length >= 16
        && artifact.LogicalPath.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase);

    public bool CanReadDependencies(ArtifactContent artifact) =>
        artifact.Bytes.Length >= 16
        && artifact.LogicalPath.EndsWith("_c", StringComparison.OrdinalIgnoreCase);

    private static string GetAssemblyVersion(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly.GetName().Version?.ToString()
        ?? "unknown";

    internal IMeshOptimizerCodec OpenGeometryCodec()
    {
        if (meshOptimizerPath is null || GeometryCodecCapability.Status != "ready")
        {
            throw Errors.Unsupported(
                "MESHOPTIMIZER_CAPABILITY_UNAVAILABLE",
                GeometryCodecCapability.Summary,
                "Configure a compatible native library explicitly and confirm it with s2mod doctor.");
        }

        var codec = NativeMeshOptimizerCodec.Open(meshOptimizerPath);
        if (GeometryCodecCapability.Identity is null || codec.Identity != GeometryCodecCapability.Identity)
        {
            codec.Dispose();
            throw Errors.Unsupported(
                "MESHOPTIMIZER_IDENTITY_DRIFT",
                "The configured meshoptimizer library changed after capability probing.",
                "Restart the command, run s2mod doctor, and regenerate any geometry plan against the current binary identity.");
        }

        return codec;
    }

    private static KVObject RequireArray(KVObject parent, string key, string path)
    {
        if (!parent.TryGetValue(key, out var value) || value is null || !value.IsArray)
        {
            throw Errors.Unsupported("KV_ARRAY_LAYOUT_UNSUPPORTED", $"Expected array '{path}.{key}' was not found.", "Use a model matching the supported embedded-mesh KV layout.");
        }

        return value;
    }

    private static KVObject RequireCollection(KVObject value, string path)
    {
        if (value is null || !value.IsCollection)
        {
            throw Errors.Unsupported("KV_COLLECTION_LAYOUT_UNSUPPORTED", $"Expected collection '{path}' was not found.", "Use a model matching the supported embedded-mesh KV layout.");
        }

        return value;
    }

    private static string RequireString(KVObject parent, string key, string path)
    {
        if (!parent.TryGetValue(key, out var value) || value is null || value.ValueType != KVValueType.String)
        {
            throw Errors.Unsupported("KV_STRING_LAYOUT_UNSUPPORTED", $"Expected string '{path}.{key}' was not found.", "Use a model matching the supported embedded-mesh KV layout.");
        }

        var result = value.ToString(CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(result))
        {
            throw Errors.Unsupported("KV_STRING_LAYOUT_UNSUPPORTED", $"String '{path}.{key}' is empty.", "Use an intact compiled model.");
        }

        return result;
    }

    private static int RequireInt32(KVObject parent, string key, string path)
    {
        var value = RequireScalar(parent, key, path);
        try
        {
            return checked((int)value.ToInt64(CultureInfo.InvariantCulture));
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
        {
            throw new S2ModKitException(
                new S2Error("KV_INTEGER_LAYOUT_UNSUPPORTED", "source2_adapter", $"Expected integer '{path}.{key}' is invalid.", "Use an intact compiled model matching the supported KV layout.", ErrorCategory.UnsupportedCapability),
                exception);
        }
    }

    private static long RequireInt64(KVObject parent, string key, string path)
    {
        var value = RequireScalar(parent, key, path);
        try
        {
            return value.ToInt64(CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
        {
            throw new S2ModKitException(
                new S2Error("KV_INTEGER_LAYOUT_UNSUPPORTED", "source2_adapter", $"Expected integer '{path}.{key}' is invalid.", "Use an intact compiled model matching the supported KV layout.", ErrorCategory.UnsupportedCapability),
                exception);
        }
    }

    private static ulong ReadUnsignedInteger(KVObject value, string path)
    {
        if (value is null || value.IsArray || value.IsCollection)
        {
            throw Errors.Unsupported("KV_INTEGER_LAYOUT_UNSUPPORTED", $"Expected integer '{path}' was not found.", "Use an intact compiled model matching the supported KV layout.");
        }

        try
        {
            return value.ToUInt64(CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
        {
            throw new S2ModKitException(
                new S2Error("KV_INTEGER_LAYOUT_UNSUPPORTED", "source2_adapter", $"Expected integer '{path}' is invalid.", "Use an intact compiled model matching the supported KV layout.", ErrorCategory.UnsupportedCapability),
                exception);
        }
    }

    private static KVObject RequireScalar(KVObject parent, string key, string path)
    {
        if (!parent.TryGetValue(key, out var value) || value is null || value.IsArray || value.IsCollection)
        {
            throw Errors.Unsupported("KV_INTEGER_LAYOUT_UNSUPPORTED", $"Expected integer '{path}.{key}' was not found.", "Use an intact compiled model matching the supported KV layout.");
        }

        return value;
    }


    private sealed class ParsedModel : IDisposable
    {
        public ParsedModel(
            MemoryStream stream,
            Resource resource,
            Source2ResourceEnvelope envelope,
            ModelSnapshot snapshot,
            Dictionary<int, ParsedMesh> meshesByOrdinal)
        {
            Stream = stream;
            Resource = resource;
            Envelope = envelope;
            Snapshot = snapshot;
            MeshesByOrdinal = meshesByOrdinal;
        }

        private MemoryStream Stream { get; }

        public Resource Resource { get; }

        public Source2ResourceEnvelope Envelope { get; }

        public ModelSnapshot Snapshot { get; }

        public Dictionary<int, ParsedMesh> MeshesByOrdinal { get; }

        public void Dispose()
        {
            Resource.Dispose();
            Stream.Dispose();
        }
    }
}
