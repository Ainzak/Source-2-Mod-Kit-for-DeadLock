namespace S2ModKit.Geometry;

/// <summary>
/// Deterministic facts about one transform application over a point sequence.
/// <see cref="ChangedPointCount"/> counts points whose transformed float bit
/// patterns differ from the inputs on any component. <see cref="MaximumDisplacement"/>
/// is the largest Euclidean point displacement, computed in double precision and
/// rounded once to float; it is always finite.
/// </summary>
public sealed record TransformSummary(
    Bounds3 BeforeBounds,
    Bounds3 AfterBounds,
    int ChangedPointCount,
    float MaximumDisplacement);
