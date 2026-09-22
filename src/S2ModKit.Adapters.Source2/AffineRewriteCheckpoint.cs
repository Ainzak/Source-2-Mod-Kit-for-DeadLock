namespace S2ModKit.Adapters.Source2;

internal enum AffineRewriteCheckpoint
{
    PositionsTransformed,
    PackedFramesTransformed,
    BoundsUpdated,
    VertexBufferEncoded,
    MetadataSerialized,
    EnvelopeRebuilt,
}
