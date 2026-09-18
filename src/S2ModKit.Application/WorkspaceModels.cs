using System.Text.Json;
using S2ModKit.Domain;

namespace S2ModKit.Application;

public sealed record ProjectArtifactManifest
{
    public string LogicalPath { get; init; } = string.Empty;

    public ContentHash ContentHash { get; init; }

    public long Size { get; init; }

    public string ObjectRelativePath { get; init; } = string.Empty;

    public string SourcePath { get; init; } = string.Empty;

    public string ProvenanceKind { get; init; } = "owned";

    public string SourceKind { get; init; } = "directory";

    public string CatalogIdentity { get; init; } = string.Empty;

    public string VerificationMode { get; init; } = "selected_entry_sha256";
}

public sealed record ProjectDependencyEdge
{
    public string FromLogicalPath { get; init; } = string.Empty;

    public string ToLogicalPath { get; init; } = string.Empty;

    public string ReferenceId { get; init; } = string.Empty;
}

public sealed record ProjectManifest
{
    public int SchemaVersion { get; init; } = 2;

    public string ProjectId { get; init; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; init; }

    public required ProjectArtifactManifest Input { get; init; }

    public bool DependencyGraphComplete { get; init; }

    public IReadOnlyList<ProjectArtifactManifest> Dependencies { get; init; } = [];

    public IReadOnlyList<ProjectDependencyEdge> DependencyEdges { get; init; } = [];

    public IReadOnlyList<string> ResourceRoots { get; init; } = [];

    public IReadOnlyList<string> RuntimeResourceRoots { get; init; } = [];

    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}
