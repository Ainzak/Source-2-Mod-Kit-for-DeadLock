using System.Globalization;
using System.Security;
using System.Text;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Reporting;

/// <summary>Dependency-free static SVG projections; geometry is already source/plan qualified by Application.</summary>
public static class EllipsoidSelectionPreviewRenderer
{
    private const int Width = 1800;
    private const int HeaderHeight = 190;
    private const int OverviewHeight = 310;
    private const int RowHeight = 365;
    private const int MaximumSvgCharacters = 64 * 1024 * 1024;
    private static readonly string[] Colors = ["#d97706", "#b83280", "#336eab"];
    private static readonly string[] Memberships = ["core", "transition", "pinned"];
    private static readonly (int Horizontal, int Vertical, string Label)[] Projections = [(0, 1, "XY / view along Z"), (0, 2, "XZ / view along Y"), (1, 2, "YZ / view along X")];

    public static EllipsoidPreviewArtifacts Render(EllipsoidSelectionPreview preview)
    {
        if (preview.PreviewFingerprint != EllipsoidSelectionPreviewBuilder.ComputeFingerprint(preview))
            throw Errors.Verification("ELLIPSOID_PREVIEW_INVALID", "Preview geometry/content identity drifted before rendering.", "Regenerate the preview.");
        var svg = new StringBuilder();
        var height = HeaderHeight + OverviewHeight + preview.Lods.Count * RowHeight + 55;
        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{Width}\" height=\"{height}\" viewBox=\"0 0 {Width} {height}\" role=\"img\" aria-labelledby=\"title desc\">\n");
        svg.Append("<title id=\"title\">Planned ellipsoid selection: original and predicted bind-space geometry</title><desc id=\"desc\">All enclosing buffer points and indexed triangles in every LOD. Gray geometry is non-editable context. Static orthographic projections do not prove textures, animation, hair, clothing or live game behavior.</desc>\n");
        svg.Append("<style>text{font-family:Arial,Helvetica,sans-serif;fill:#172b42;font-size:14px}.heading{font-size:25px;font-weight:700}.small{font-size:12px}.row{font-size:16px;font-weight:700}</style><rect width=\"100%\" height=\"100%\" fill=\"#f5f7fa\"/>\n");
        Text(svg, 24, 36, preview.LocalTransform.Field.MirrorPlane is null ? "PLANNED ELLIPSOID SELECTION" : "PLANNED DISJOINT MIRRORED ELLIPSOIDS", "heading");
        Text(svg, 24, 61, preview.ResourcePath);
        Text(svg, 24, 84, $"Input {preview.InputHash.Value[..16]}   Plan {preview.PlanFingerprint.Value[..16]}   Preview {preview.PreviewFingerprint.Value[..16]}", "small");
        var field = preview.LocalTransform.Field;
        Text(svg, 24, 107, FormattableString.Invariant($"MODEL SPACE   Center ({field.Center.X:R}, {field.Center.Y:R}, {field.Center.Z:R})   Outer radii ({field.OuterRadii.X:R}, {field.OuterRadii.Y:R}, {field.OuterRadii.Z:R})   Core {field.CoreFraction:R}   Scale {preview.LocalTransform.UniformScale:R}"));
        if (field.MirrorPlane is { } plane) Text(svg, 24, 173, FormattableString.Invariant($"Explicit plane {plane.Axis}={plane.Coordinate:R}; base/reflected supports. Original asymmetry is retained."), "small");
        for (var index = 0; index < Memberships.Length; index++)
        {
            svg.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{32 + index * 220}\" cy=\"135\" r=\"5\" fill=\"{Colors[index]}\"/>");
            Text(svg, 46 + index * 220, 140, Memberships[index].ToUpperInvariant());
        }
        Text(svg, 710, 140, "GRAY = CONTEXT ONLY / NOT EDITABLE ANATOMY");
        Text(svg, 24, 164, "Source-derived colors remain the same in both columns. Dashed ellipses: projected outer support and core; + marks the center.", "small");
        var first = preview.Lods[0];
        Text(svg, 24, HeaderHeight + 16, $"LOD {first.Buffer.Lod} MODEL CONTEXT / ORIGINAL GEOMETRY / ORTHOGRAPHIC OVERVIEW", "row");
        for (var projection = 0; projection < Projections.Length; projection++)
            DrawPanel(svg, preview, first, projection, false, 12 + projection * 596, HeaderHeight + 27, 580, 272, overview: true);
        var focus = preview.Lods.SelectMany(l => l.Points.SelectMany(p => new[] { p.Original, p.Predicted })).ToArray();
        foreach (var (lod, row) in preview.Lods.Select((lod, index) => (lod, index)))
        {
            var y = HeaderHeight + OverviewHeight + row * RowHeight;
            var buffer = lod.Buffer;
            Text(svg, 20, y + 22, $"LOD {buffer.Lod}  MESH {buffer.MeshOrdinal}  BUFFER {buffer.VertexBufferOrdinal}  MDAT {buffer.ResourceBlockIndex} / MVTX {buffer.VertexResourceBlockIndex}  {buffer.VertexCount} points / {lod.TriangleIndices.Count / 3} triangles", "row");
            Text(svg, 20, y + 43, FormattableString.Invariant($"Core {buffer.CoreVertexCount}   Transition {buffer.TransitionVertexCount}   Pinned {buffer.PinnedVertexCount}   Changed positions {buffer.ChangedPositionCount}   Max displacement {buffer.MaximumDisplacement:R}   Gray context is clipped to the same focus window."), "small");
            for (var projection = 0; projection < Projections.Length; projection++)
                for (var predicted = 0; predicted < 2; predicted++)
                    DrawPanel(svg, preview, lod, projection, predicted != 0, 10 + (projection * 2 + predicted) * 298, y + 54, 286, 299, overview: false, focus);
        }
        Text(svg, 24, height - 27, "BIND-SPACE DIAGNOSTIC ONLY: no proof of texture, animation, hair, clothing fit, collision or live Deadlock behavior.");
        svg.Append("</svg>\n");
        var bytes = Encoding.UTF8.GetBytes(svg.ToString());
        var hash = ContentHash.Compute(bytes);
        var summary = new EllipsoidSelectionPreviewSummary(1, field.MirrorPlane is null ? "single_ellipsoid_selection" : "mirrored_ellipsoid_selection", EllipsoidSelectionPreview.ProjectionProfile, preview.ProofLevel,
            preview.InputHash, preview.PlanFingerprint, preview.TargetFingerprint, preview.PreviewFingerprint, preview.ResourcePath, preview.LocalTransform,
            preview.Lods.Select(l => new EllipsoidPreviewLodSummary(l.Buffer.Lod, l.Buffer.MeshOrdinal, l.Buffer.ResourceBlockIndex, l.Buffer.VertexBufferOrdinal,
                l.Buffer.VertexResourceBlockIndex, l.DrawCallIds, l.Buffer.VertexCount, l.TriangleIndices.Count / 3, l.Context.Count, l.Context.Sum(c => c.Points.Count),
                l.Buffer.CoreVertexCount, l.Buffer.TransitionVertexCount, l.Buffer.PinnedVertexCount, l.Buffer.ChangedPositionCount, l.Buffer.ChangedFrameCount,
                l.Buffer.MaskHash, l.Buffer.WeightHash, l.Buffer.InputPositionHash, l.Buffer.ExpectedPositionHash, l.Buffer.MaximumDisplacement)
            { MirroredMasks = l.Buffer.MirroredMasks }).ToArray(), hash,
            ["All enclosing points/topology retained; coincident records are not welded.", "Gray buffers are non-editable context; focused views crop context only.",
                "Orthographic bind-space prediction does not prove textures, animation, hair, clothing fit, collision or runtime."]);
        var json = JsonDefaults.SerializeToUtf8(summary);
        return new(json, bytes, preview.PreviewFingerprint, ContentHash.Compute(json), hash);
    }

    private static void DrawPanel(StringBuilder svg, EllipsoidSelectionPreview preview, EllipsoidSelectionPreviewLod lod,
        int projection, bool predicted, int x, int y, int width, int height, bool overview, Point3[]? focus = null)
    {
        var (horizontal, vertical, label) = Projections[projection];
        var candidates = overview ? lod.Context.SelectMany(c => c.Points).Concat(lod.Points.Select(p => p.Original)).ToArray() : focus!;
        double minX = candidates.Min(p => Component(p, horizontal)), maxX = candidates.Max(p => Component(p, horizontal));
        double minY = candidates.Min(p => Component(p, vertical)), maxY = candidates.Max(p => Component(p, vertical));
        var field = preview.LocalTransform.Field;
        var centers = field.MirrorPlane is null ? new[] { field.Center } : new[] { field.Center, EllipsoidFieldMath.Mirror(EllipsoidFieldMath.Create(preview.LocalTransform, lod.Buffer.MaximumDisplacement))!.ReflectedCenter };
        var cx = Component(field.Center, horizontal); var cy = Component(field.Center, vertical);
        var rx = Component(field.OuterRadii, horizontal); var ry = Component(field.OuterRadii, vertical);
        if (!overview) { foreach (var center in centers) { cx = Component(center, horizontal); cy = Component(center, vertical); minX = Math.Min(minX, cx - rx); maxX = Math.Max(maxX, cx + rx); minY = Math.Min(minY, cy - ry); maxY = Math.Max(maxY, cy + ry); } }
        var scale = Math.Min((width - 42d) / Math.Max(maxX - minX, 1e-20), (height - 62d) / Math.Max(maxY - minY, 1e-20)) * 0.94;
        var midX = minX + (maxX - minX) / 2; var midY = minY + (maxY - minY) / 2;
        double PX(double value) => x + width / 2d + (value - midX) * scale;
        double PY(double value) => y + 33 + (height - 53) / 2d - (value - midY) * scale;
        string XY(Point3 p) => $"{Number(PX(Component(p, horizontal)))},{Number(PY(Component(p, vertical)))}";
        var clip = $"clip-{lod.Buffer.Lod}-{projection}-{(overview ? "overview" : predicted ? "predicted" : "original")}";
        svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x}\" y=\"{y}\" width=\"{width}\" height=\"{height}\" rx=\"5\" fill=\"white\" stroke=\"#dbe1e8\"/><defs><clipPath id=\"{clip}\"><rect x=\"{x + 8}\" y=\"{y + 28}\" width=\"{width - 16}\" height=\"{height - 40}\"/></clipPath></defs>");
        Text(svg, x + 10, y + 20, (overview ? "CONTEXT " : predicted ? "PREDICTED " : "ORIGINAL ") + label, "small");
        svg.Append(CultureInfo.InvariantCulture, $"<g clip-path=\"url(#{clip})\">");
        svg.Append("<path data-role=\"context\" fill=\"none\" stroke=\"#c4cad2\" stroke-width=\"1\" stroke-linecap=\"round\" opacity=\"0.65\" d=\"");
        foreach (var p in lod.Context.SelectMany(c => c.Points))
        {
            if (!overview && (Component(p, horizontal) < minX || Component(p, horizontal) > maxX || Component(p, vertical) < minY || Component(p, vertical) > maxY)) continue;
            svg.Append(CultureInfo.InvariantCulture, $"M{XY(p)}h0.1");
        }
        svg.Append("\"/>");
        if (!overview)
        {
            svg.Append("<path data-role=\"enclosing-topology\" fill=\"none\" stroke=\"#74849b\" stroke-width=\"0.35\" opacity=\"0.32\" d=\"");
            for (var index = 0; index < lod.TriangleIndices.Count; index += 3)
            {
                var a = lod.Points[lod.TriangleIndices[index]]; var b = lod.Points[lod.TriangleIndices[index + 1]]; var c = lod.Points[lod.TriangleIndices[index + 2]];
                svg.Append(CultureInfo.InvariantCulture, $"M{XY(predicted ? a.Predicted : a.Original)}L{XY(predicted ? b.Predicted : b.Original)}L{XY(predicted ? c.Predicted : c.Original)}Z");
            }
            svg.Append("\"/>");
            foreach (var (center, side) in centers.Select((value, index) => (value, index)))
            {
                cx = Component(center, horizontal); cy = Component(center, vertical);
                foreach (var fraction in new[] { 1d, field.CoreFraction })
                    svg.Append(CultureInfo.InvariantCulture, $"<ellipse cx=\"{Number(PX(cx))}\" cy=\"{Number(PY(cy))}\" rx=\"{Number(rx * fraction * scale)}\" ry=\"{Number(ry * fraction * scale)}\" fill=\"none\" stroke=\"#d97706\" stroke-width=\"1\" stroke-dasharray=\"4 3\"/>");
                if (centers.Length > 1) Text(svg, (int)PX(cx), (int)PY(cy), side == 0 ? "base" : "reflected", "small");
                svg.Append(CultureInfo.InvariantCulture, $"<path d=\"M{Number(PX(cx) - 4)},{Number(PY(cy))}h8M{Number(PX(cx))},{Number(PY(cy) - 4)}v8\" stroke=\"#d97706\"/>");
            }
        }
        for (var membership = 0; membership < Memberships.Length; membership++)
        {
            svg.Append(CultureInfo.InvariantCulture, $"<path data-membership=\"{Memberships[membership]}\" data-count=\"{lod.Points.Count(p => p.Membership == Memberships[membership])}\" fill=\"none\" stroke=\"{Colors[membership]}\" stroke-width=\"{(overview ? "1.8" : "2.2")}\" stroke-linecap=\"round\" opacity=\"0.85\" d=\"");
            foreach (var p in lod.Points.Where(p => p.Membership == Memberships[membership])) svg.Append(CultureInfo.InvariantCulture, $"M{XY(predicted ? p.Predicted : p.Original)}h0.1");
            svg.Append("\"/>");
        }
        svg.Append("</g>");
        var axes = new[] { "X", "Y", "Z" };
        Text(svg, x + width - 52, y + height - 16, $"+{axes[horizontal]} →", "small");
        Text(svg, x + 12, y + 46, $"↑ +{axes[vertical]}", "small");
        if (svg.Length > MaximumSvgCharacters) throw Errors.Unsupported("ELLIPSOID_PREVIEW_LIMIT_EXCEEDED", "The complete static contact sheet exceeds 64 MiB.", "No partial contact sheet was published.");
    }

    private static void Text(StringBuilder svg, int x, int y, string text, string css = "") =>
        svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{x}\" y=\"{y}\" class=\"{css}\">{SecurityElement.Escape(text)}</text>\n");
    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static double Component(Point3 p, int axis) => axis switch { 0 => p.X, 1 => p.Y, _ => p.Z };
    private static double Component(TransformVector3 p, int axis) => axis switch { 0 => p.X, 1 => p.Y, _ => p.Z };
}
