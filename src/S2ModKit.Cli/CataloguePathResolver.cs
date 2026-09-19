namespace S2ModKit.Cli;

internal static class CataloguePathResolver
{
    public static string? TryFindPackagedCatalogue(string applicationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);

        var candidate = Path.Combine(applicationDirectory, "catalogues", "deadlock-current.json");
        return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
    }
}
