using System.Globalization;
using System.Security;
using System.Text;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Reporting;

public static class PairedSelectionPreviewRenderer
{
    private const int MaximumSvgBytes = 64 * 1024 * 1024;
    private const int PanelWidth = 298;
    private const int PanelHeight = 310;
    private sealed record Face(Point3 A, Point3 B, Point3 C, string Color, double Depth);

    public static EllipsoidPreviewArtifacts Render(PairedSelectionPreview preview)
    {
        if (preview.PreviewFingerprint != PairedSelectionPreviewBuilder.ComputeFingerprint(preview)) throw Invalid("Preview identity differs.");
        var lods = preview.Regions.Select(r => r.Lod).Distinct().ToArray();
        var all = preview.Buffers.SelectMany(b => b.Points.SelectMany(p => new[] { p.Original, p.Predicted })).ToArray();
        var focuses = preview.Transform.Fields.Select(f =>
        {
            var c = f.Field.Pivot.Point!; var r = f.Field.OuterRadii;
            return new Point3[] { new(c.X - r.X, c.Y - r.Y, c.Z - r.Z), new(c.X + r.X, c.Y + r.Y, c.Z + r.Z) };
        }).ToArray();
        var focus = focuses.SelectMany(p => p).Concat(preview.Buffers.SelectMany(b => b.Points.Where(p => p.Original != p.Predicted).SelectMany(p => new[] { p.Original, p.Predicted }))).ToArray();
        var height = 220 + lods.Length * 1400;
        var svg = new StringBuilder(FormattableString.Invariant($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1800\" height=\"{height}\" viewBox=\"0 0 1800 {height}\"><title>Paired original and predicted region surfaces</title><style>text{{font:14px Arial;fill:#233347}}</style><rect width=\"100%\" height=\"100%\" fill=\"#f5f7fa\"/>"));
        Text(svg, 20, 30, "PAIRED SURFACE COMPARISON / BIND-SPACE PREDICTION");
        Text(svg, 20, 55, $"Orange = {preview.Transform.Fields[0].FieldId}; purple = {preview.Transform.Fields[1].FieldId}; blue = pinned; green = protected; gray = excluded.");
        Text(svg, 20, 80, "Matched cameras/scale across states and LODs. FRONT = YZ looking along X; SIDE = XZ looking along Y; TOP = XY.");
        Text(svg, 20, 105, "Full outline, common region, then each partner. Crops retain source topology; no texture, physical occlusion or live bodygroup inference.");
        Text(svg, 20, 130, "Serialized procedural inputs stay fixed; simulation, animated fit, self-intersection, culling and runtime remain untested.");
        Text(svg, 20, 155, "Dimension changes measure the same changed records before/after. Packed-frame counts are planned; these pictures predict positions.");
        Text(svg, 20, 180, $"Input {preview.InputHash.Value[..16]} / Plan {preview.PlanFingerprint.Value[..16]}");
        svg.Append("<defs>");
        foreach (var lod in lods)
            for (var projection = 0; projection < 3; projection++)
            {
                var buffers = preview.Buffers.Where(b => b.Source.Lod == lod).ToArray();
                ContextSilhouette(svg, buffers.Where(b => !b.Source.Selected).ToArray(), lod, projection);
                Surface(svg, buffers, lod, projection, false); Surface(svg, buffers, lod, projection, true);
            }
        svg.Append("</defs>");
        for (var li = 0; li < lods.Length; li++)
        {
            var lod = lods[li]; var y = 220 + li * 1400;
            var measures = preview.Regions.Where(r => r.Lod == lod).ToArray();
            Text(svg, 20, y + 20, $"LOD {lod}: " + string.Join(" / ", measures.Select(r => $"{r.FieldId}: {r.ChangedPositions} changed positions; X {Percent(r.XChangePercent)}, Y {Percent(r.YChangePercent)}, Z {Percent(r.ZChangePercent)}")));
            Text(svg, 20, y + 64, "Positive X reach from each authored pivot: " + string.Join(" / ", measures.Select(r => $"{r.FieldId} {N(r.PositiveReachBefore.X)} to {N(r.PositiveReachAfter.X)} ({Percent(r.PositiveReachBefore.X > 0 ? 100 * ((double)r.PositiveReachAfter.X / r.PositiveReachBefore.X - 1) : null)})")));
            Text(svg, 20, y + 43, $"Fixed protection {measures[0].ProtectedRecords}; fixed procedural {measures[0].ProceduralRecords}. FULL OUTLINE");
            for (var projection = 0; projection < 3; projection++)
                for (var state = 0; state < 2; state++) Panel(svg, lod, projection, state == 1, 10 + (projection * 2 + state) * PanelWidth, y + 75, all, false);
            Point3[][] rows = [focus, focuses[0], focuses[1]];
            for (var row = 0; row < rows.Length; row++)
            {
                var top = y + 415 + row * 330;
                Text(svg, 20, top - 8, row == 0 ? "COMMON REGION / SOURCE AND PREDICTED" : $"PARTNER {preview.Transform.Fields[row - 1].FieldId} / SOURCE AND PREDICTED");
                for (var projection = 0; projection < 3; projection++)
                    for (var state = 0; state < 2; state++) Panel(svg, lod, projection, state == 1, 10 + (projection * 2 + state) * PanelWidth, top, rows[row], true, row);
            }
        }
        svg.Append("</svg>\n"); CheckBudget(svg);
        var bytes = Encoding.UTF8.GetBytes(svg.ToString()); if (bytes.Length > MaximumSvgBytes) throw Invalid("Complete SVG exceeds 64 MiB.");
        var hash = ContentHash.Compute(bytes);
        var summary = new PairedPreviewSummary(1, "paired_surface_comparison", PairedSelectionPreview.ProjectionProfile, "bind_space_prediction",
            preview.InputHash, preview.PlanFingerprint, preview.TargetFingerprint, preview.PreviewFingerprint,
            preview.Transform.Fields, preview.Regions, hash,
            preview.Buffers.Select(b => new PairedPreviewBufferSummary(b.Source, b.MemberId, b.DrawCallIds, b.TriangleIndices.Count / 3,
                b.ProtectedIndices.Count, DirectionalContractValidator.VertexSetHash(b.ProtectedIndices), b.ProceduralIndices.Count,
                DirectionalContractValidator.VertexSetHash(b.ProceduralIndices))).ToArray(),
            ["Matching orthographic cameras and scales across all states/LODs; front YZ, side XZ, top XY in model axes.",
             "Full surfaces retain all triangle indices and source records. Named partner crops explicitly clip context.",
             "Selected flat shaded surfaces overlay excluded silhouettes; this is not physical occlusion or a textured render.",
             "Local spans and signed-axis reaches from explicit pivots use identical changed records; no volume or live perceptibility claim.",
             "Protected and procedural position words are checked; packed-frame counts and protection are planned facts.",
             "Animation, bodygroups, simulation, garment fit, self-intersection, culling and runtime remain untested."]);
        var json = PairedPreviewSummaryJson.Write(summary);
        return new(json, bytes, preview.PreviewFingerprint, ContentHash.Compute(json), hash);
    }
    private static string Percent(double? value) => value is { } v ? v.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "undefined";

    private static void Surface(StringBuilder svg, PairedPreviewBuffer[] buffers, int lod, int projection, bool predicted)
    {
        var (h, v, depth) = Axes(projection); var faces = new List<Face>();
        foreach (var buffer in buffers.Where(b => b.Source.Selected))
        {
            var protect = buffer.ProtectedIndices.ToHashSet();
            for (var i = 0; i < buffer.TriangleIndices.Count; i += 3)
            {
                int[] ids = [buffer.TriangleIndices[i], buffer.TriangleIndices[i + 1], buffer.TriangleIndices[i + 2]];
                Point3 Get(int n) => predicted ? buffer.Points[n].Predicted : buffer.Points[n].Original;
                var a = Get(ids[0]); var b = Get(ids[1]); var c = Get(ids[2]);
                var active = ids.Select(n => buffer.Partners[n]).Where(n => n >= 0).Distinct().ToArray();
                var color = ids.All(protect.Contains) ? "#58a782" : active.Length == 0 ? "#638caf"
                    : active.Length > 1 ? "#db789b" : active[0] == 0 ? "#e3a14a" : "#aa8bd3";
                faces.Add(new(a, b, c, Shade(color, a, b, c), ((double)Axis(a, depth) + Axis(b, depth) + Axis(c, depth)) / 3));
            }
        }
        svg.Append(CultureInfo.InvariantCulture, $"<g id=\"surface-{lod}-{projection}-{(predicted ? 1 : 0)}\" data-role=\"complete-surfaces\">");
        svg.Append(CultureInfo.InvariantCulture, $"<use href=\"#context-{lod}-{projection}\"/>");
        foreach (var face in faces.OrderBy(f => f.Depth))
        {
            svg.Append(CultureInfo.InvariantCulture, $"<path fill=\"{face.Color}\" d=\"M{N(Axis(face.A, h))},{N(-Axis(face.A, v))}L{N(Axis(face.B, h))},{N(-Axis(face.B, v))}L{N(Axis(face.C, h))},{N(-Axis(face.C, v))}Z\"/>");
            if ((svg.Length & 8191) < 140) CheckBudget(svg);
        }
        // Includes protected and unindexed records, visible as a fine overlay in focus views.
        svg.Append("<path data-role=\"protected-records\" fill=\"none\" stroke=\"#138455\" stroke-width=\"0.12\" d=\"");
        foreach (var b in buffers)
            foreach (var i in b.ProtectedIndices)
            {
                var p = predicted ? b.Points[i].Predicted : b.Points[i].Original;
                svg.Append(CultureInfo.InvariantCulture, $"M{N(Axis(p, h))},{N(-Axis(p, v))}h0.025");
            }
        svg.Append("\"/></g>"); CheckBudget(svg);
    }

    private static void ContextSilhouette(StringBuilder svg, PairedPreviewBuffer[] buffers, int lod, int projection)
    {
        var (h, v, _) = Axes(projection);
        svg.Append(CultureInfo.InvariantCulture, $"<path id=\"context-{lod}-{projection}\" data-role=\"excluded-silhouette\" fill=\"#bdc6d2\" d=\"");
        foreach (var buffer in buffers)
            for (var i = 0; i < buffer.TriangleIndices.Count; i += 3)
            {
                var a = buffer.Points[buffer.TriangleIndices[i]].Original;
                var b = buffer.Points[buffer.TriangleIndices[i + 1]].Original;
                var c = buffer.Points[buffer.TriangleIndices[i + 2]].Original;
                // Consistent projected winding makes overlapping alternatives a silhouette union,
                // rather than cancelling front/back faces in the SVG nonzero fill rule.
                if (((double)Axis(b, h) - Axis(a, h)) * ((double)Axis(c, v) - Axis(a, v))
                    - ((double)Axis(b, v) - Axis(a, v)) * ((double)Axis(c, h) - Axis(a, h)) < 0) (b, c) = (c, b);
                svg.Append(CultureInfo.InvariantCulture, $"M{N(Axis(a, h))},{N(-Axis(a, v))}L{N(Axis(b, h))},{N(-Axis(b, v))}L{N(Axis(c, h))},{N(-Axis(c, v))}Z");
            }
        svg.Append("\"/>"); CheckBudget(svg);
    }

    private static void Panel(StringBuilder svg, int lod, int projection, bool predicted, int x, int y, Point3[] bounds, bool focus, int focusOrdinal = 0)
    {
        var (h, v, _) = Axes(projection);
        var minH = bounds.Min(p => Axis(p, h)); var maxH = bounds.Max(p => Axis(p, h));
        var minV = bounds.Min(p => Axis(p, v)); var maxV = bounds.Max(p => Axis(p, v));
        var scale = Math.Min(270 / Math.Max((double)maxH - minH, 1e-20), 265 / Math.Max((double)maxV - minV, 1e-20)) * .94;
        var cx = (double)minH + ((double)maxH - minH) / 2; var cy = (double)minV + ((double)maxV - minV) / 2;
        var id = $"clip-{lod}-{projection}-{(predicted ? 1 : 0)}-{(focus ? 1 : 0)}-{focusOrdinal}";
        svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x}\" y=\"{y}\" width=\"286\" height=\"{PanelHeight}\" fill=\"white\" stroke=\"#cbd3de\"/><clipPath id=\"{id}\"><rect x=\"{x + 5}\" y=\"{y + 30}\" width=\"276\" height=\"275\"/></clipPath>");
        Text(svg, x + 10, y + 20, $"{(predicted ? "PREDICTED" : "ORIGINAL")} {(projection == 2 ? "FRONT" : projection == 1 ? "SIDE" : "TOP")} {"XYZ"[h]}{"XYZ"[v]} / {(focus ? "REGION CROP" : "FULL OUTLINE")}");
        svg.Append(FormattableString.Invariant($"<g clip-path=\"url(#{id})\"><use href=\"#surface-{lod}-{projection}-{(predicted ? 1 : 0)}\" data-role=\"{(focus ? "focus-comparison" : "full-comparison")}\" data-camera=\"{projection}-{(focus ? 1 : 0)}-{focusOrdinal}\" transform=\"translate({x + 143} {y + 168}) scale({scale:R}) translate({-cx:R} {cy:R})\"/></g>"));
    }

    private static string Shade(string color, Point3 a, Point3 b, Point3 c)
    {
        double ux = (double)b.X - a.X, uy = (double)b.Y - a.Y, uz = (double)b.Z - a.Z;
        double vx = (double)c.X - a.X, vy = (double)c.Y - a.Y, vz = (double)c.Z - a.Z;
        var nx = uy * vz - uz * vy; var ny = uz * vx - ux * vz; var nz = ux * vy - uy * vx;
        var length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        var light = length == 0 ? .7 : .58 + .42 * Math.Abs((nx * .3 + ny * .4 + nz * .8660254) / length);
        int Channel(int offset) => (int)Math.Round(int.Parse(color.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) * light);
        return $"#{Channel(1):x2}{Channel(3):x2}{Channel(5):x2}";
    }
    private static (int H, int V, int D) Axes(int projection) => projection switch { 0 => (0, 1, 2), 1 => (0, 2, 1), _ => (1, 2, 0) };
    private static float Axis(Point3 p, int axis) => axis switch { 0 => p.X, 1 => p.Y, _ => p.Z };
    private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static void Text(StringBuilder svg, int x, int y, string text) => svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{x}\" y=\"{y}\">{SecurityElement.Escape(text)}</text>");
    private static void CheckBudget(StringBuilder svg) { if (svg.Length > MaximumSvgBytes) throw Invalid("Surface artifact exceeds the bounded SVG budget."); }
    private static S2ModKitException Invalid(string message) => Errors.Verification("PAIRED_PREVIEW_INVALID", message, "Regenerate a complete bounded preview; no partial artifact is published.");
}
