namespace S2ModKit.Adapters.Source2;

internal static class MeshOptimizerCodecProbe
{
    public static MeshOptimizerCodecCapability Probe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new MeshOptimizerCodecCapability(
                "not_configured",
                "Set S2MODKIT_MESHOPTIMIZER_PATH to enable Source 2 geometry inspection and transforms.",
                null);
        }

        try
        {
            using var codec = NativeMeshOptimizerCodec.Open(path);
            return new MeshOptimizerCodecCapability(
                "ready",
                "The configured meshoptimizer library exposes the required Source 2 vertex/index profile.",
                codec.Identity);
        }
        catch (Exception exception) when (exception is ArgumentException
            or BadImageFormatException
            or DllNotFoundException
            or EntryPointNotFoundException
            or FileNotFoundException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            return new MeshOptimizerCodecCapability(
                "misconfigured",
                $"The configured meshoptimizer library is unavailable or incompatible: {exception.Message}",
                null);
        }
    }
}
