namespace S2ModKit.Adapters.Preview;

/// <summary>A separate mesh inside a part (see <see cref="PreviewSession.Pieces"/>): its index among parts and pieces, its part, its
/// number within the part (1 = largest) and its triangle count.</summary>
public sealed record PartPiece(int Index, int Part, int Number, int Triangles);
