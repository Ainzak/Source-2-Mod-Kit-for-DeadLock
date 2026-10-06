using System.Globalization;
using System.Security;
using System.Text;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Reporting;

public static class DirectionalSelectionPreviewRenderer
{
    private const int MaximumSvgBytes = 64 * 1024 * 1024;
    private const int PanelWidth = 298;
    private const int PanelHeight = 310;
    private sealed record Face(Point3 A, Point3 B, Point3 C, string Color, double Depth);

    public static EllipsoidPreviewArtifacts Render(DirectionalSelectionPreview preview)
    {
        if (preview.PreviewFingerprint != DirectionalSelectionPreviewBuilder.ComputeFingerprint(preview)) throw Invalid("Preview content identity drifted.");
        var lods = preview.Measurements.Select(m => m.Lod).ToArray();
        var all = preview.Buffers.SelectMany(b => b.Points.SelectMany(p => new[] { p.Original, p.Predicted })).ToArray();
        var field = preview.Transform.Field; var center = preview.Pivot.Point; var r = field.OuterRadii;
        // A common focus covers the field, moved vertices and one radius of nearby context;
        // the companion full view always retains the complete associated outline.
        Point3[] focus = [new(center.X - 2 * r.X, center.Y - 2 * r.Y, center.Z - 2 * r.Z),
            new(center.X + 2 * r.X, center.Y + 2 * r.Y, center.Z + 2 * r.Z),
            .. preview.Buffers.Where(b => b.Source.Selected).SelectMany(b => b.Points.Where(p => p.Original != p.Predicted).SelectMany(p => new[] { p.Original, p.Predicted }))];
        var height = 235 + lods.Length * 750;
        var svg = new StringBuilder(FormattableString.Invariant($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1800\" height=\"{height}\" viewBox=\"0 0 1800 {height}\"><title>Directional original and predicted surfaces</title><style>text{{font:14px Arial;fill:#233347}}</style><rect width=\"100%\" height=\"100%\" fill=\"#f5f7fa\"/>"));
        Text(svg, 20, 30, "DIRECTIONAL SURFACE COMPARISON / BIND-SPACE PREDICTION");
        Text(svg, 20, 55, $"Axis factors X {N(field.Scale.X)} / Y {N(field.Scale.Y)} / Z {N(field.Scale.Z)}; field radii {Vector(new(r.X, r.Y, r.Z))}; core {N(field.CoreFraction)}");
        Text(svg, 20, 80, "Orange = affected triangles; blue = all-pinned triangles; green = protected records; gray = excluded unchanged silhouettes.");
        Text(svg, 20, 105, "Matching orthographic cameras and scale across originals/predictions AND LODs. Full outline above; cropped focus below.");
        Text(svg, 20, 130, "Selected surfaces overlay excluded silhouettes; alternative authored views appear together. No physical occlusion or animation preview.");
        Text(svg, 20, 155, "Static surfaces do not prove joint/garment fit, intersections, culling, collision or live behavior. No automatic anatomy selection.");
        Text(svg, 20, 180, $"Input {preview.InputHash.Value[..16]} / Plan {preview.PlanFingerprint.Value[..16]} / Preview {preview.PreviewFingerprint.Value[..16]}");
        svg.Append("<defs>");
        foreach (var lod in lods)
            for (var projection = 0; projection < 3; projection++)
            {
                ContextSilhouette(svg, preview.Buffers.Where(b => b.Source.Lod == lod && !b.Source.Selected).ToArray(), lod, projection);
                for (var state = 0; state < 2; state++) Surface(svg, preview.Buffers.Where(b => b.Source.Lod == lod).ToArray(), lod, projection, state != 0);
            }
        svg.Append("</defs>");
        foreach (var (measure, row) in preview.Measurements.Select((m, i) => (m, i)))
        {
            var y = 205 + row * 750;
            Text(svg, 20, y + 20, $"LOD {measure.Lod}: changed {measure.ChangedCount}/{measure.SelectedCount}; pinned {measure.PinnedCount}; protected {measure.ProtectedCount}; excluded {measure.ExcludedCount}");
            Text(svg, 20, y + 43, $"Max displacement {N(measure.MaximumDisplacement)}; model diagonal {N(measure.ModelDiagonal)}; effect {N(measure.MaximumDisplacementOverModelDiagonal * 100)}% of model diagonal.");
            Text(svg, 20, y + 66, $"Changed-region spans XYZ: {Vector(measure.ChangedRegionSpanBefore)} -> {Vector(measure.ChangedRegionSpanAfter)}; complete-selection spans: {Vector(measure.SelectedSpanBefore)} -> {Vector(measure.SelectedSpanAfter)}");
            for (var projection = 0; projection < 3; projection++)
                for (var state = 0; state < 2; state++)
                {
                    var x = 10 + (projection * 2 + state) * PanelWidth;
                    Panel(svg, measure.Lod, projection, state != 0, x, y + 82, all, false);
                    Panel(svg, measure.Lod, projection, state != 0, x, y + 410, focus, true);
                }
        }
        svg.Append("</svg>\n"); CheckBudget(svg);
        var bytes = Encoding.UTF8.GetBytes(svg.ToString());
        if (bytes.Length > MaximumSvgBytes) throw Invalid("Surface artifact exceeds 64 MiB; no incomplete preview is published.");
        var hash = ContentHash.Compute(bytes);
        var summary = new
        {
            SchemaVersion = 1,
            Kind = "directional_surface_comparison",
            ProjectionProfile = DirectionalSelectionPreview.ProjectionProfile,
            ProofLevel = "bind_space_prediction",
            preview.InputHash,
            preview.PlanFingerprint,
            preview.TargetFingerprint,
            preview.PreviewFingerprint,
            AxisFactors = field.Scale,
            preview.Pivot,
            OuterRadii = field.OuterRadii,
            field.CoreFraction,
            Cameras = Enumerable.Range(0, 3).Select(p => Camera(p, all, focus)).ToArray(),
            Lods = preview.Measurements,
            Buffers = preview.Buffers.Select(b => new DirectionalPreviewBufferSummary(b.Source, b.MemberId, b.DrawCallIds, b.TriangleIndices.Count / 3,
                b.ProtectedIndices.Count, DirectionalContractValidator.VertexSetHash(b.ProtectedIndices))).ToArray(),
            ContactSheetHash = hash,
            Limitations = new[] { "Bind-space position/surface prediction; planned protection also covers packed frames but the picture shows positions only.",
                "Complete source topology and all records retained, with alternative authored views together; no claimed live bodygroup activation.",
                "Full-model panels retain the complete outline. Focus panels explicitly crop nearby context; no selected/excluded buffer is dropped from identity or measurements.",
                "Selected flat shaded surfaces are drawn over complete excluded silhouettes so alternatives cannot obscure the edit; this is not physical occlusion, a textured game render or an intersection test. Surface coordinates round to 0.001 model units.",
                "Model-relative effect is maximum stored vertex displacement / complete source AABB diagonal. Region spans use the SAME changed records before and after; neither is a perceptibility threshold.",
                "No proof of animated garment/joint fit, separation, spheres, proxies, collision, culling, LOD activation or runtime stability." }
        };
        var json = JsonDefaults.SerializeToUtf8(summary);
        return new(json, bytes, preview.PreviewFingerprint, ContentHash.Compute(json), hash);
    }

    private static void Surface(StringBuilder svg, DirectionalPreviewBuffer[] buffers, int lod, int projection, bool predicted)
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
                var color = ids.All(protect.Contains) ? "#58a782"
                    : ids.All(n => buffer.Points[n].Membership == "pinned") ? "#638caf" : "#e3a14a";
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

    private static void ContextSilhouette(StringBuilder svg, DirectionalPreviewBuffer[] buffers, int lod, int projection)
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

    private static void Panel(StringBuilder svg, int lod, int projection, bool predicted, int x, int y, Point3[] bounds, bool focus)
    {
        var (h, v, _) = Axes(projection);
        var minH = bounds.Min(p => Axis(p, h)); var maxH = bounds.Max(p => Axis(p, h));
        var minV = bounds.Min(p => Axis(p, v)); var maxV = bounds.Max(p => Axis(p, v));
        var scale = Math.Min(270 / Math.Max((double)maxH - minH, 1e-20), 265 / Math.Max((double)maxV - minV, 1e-20)) * .94;
        var cx = (double)minH + ((double)maxH - minH) / 2; var cy = (double)minV + ((double)maxV - minV) / 2;
        var id = $"clip-{lod}-{projection}-{(predicted ? 1 : 0)}-{(focus ? 1 : 0)}";
        svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x}\" y=\"{y}\" width=\"286\" height=\"{PanelHeight}\" fill=\"white\" stroke=\"#cbd3de\"/><clipPath id=\"{id}\"><rect x=\"{x + 5}\" y=\"{y + 30}\" width=\"276\" height=\"275\"/></clipPath>");
        Text(svg, x + 10, y + 20, $"{(predicted ? "PREDICTED" : "ORIGINAL")} {"XYZ"[h]}{"XYZ"[v]} / {(focus ? "FOCUS CROP" : "FULL OUTLINE")}");
        svg.Append(FormattableString.Invariant($"<g clip-path=\"url(#{id})\"><use href=\"#surface-{lod}-{projection}-{(predicted ? 1 : 0)}\" data-role=\"{(focus ? "focus-comparison" : "full-comparison")}\" data-camera=\"{projection}-{(focus ? 1 : 0)}\" transform=\"translate({x + 143} {y + 168}) scale({scale:R}) translate({-cx:R} {cy:R})\"/></g>"));
    }

    private static object Camera(int projection, Point3[] all, Point3[] focus)
    {
        var (h, v, d) = Axes(projection);
        object Extent(Point3[] values) => new
        {
            MinHorizontal = values.Min(p => Axis(p, h)),
            MaxHorizontal = values.Max(p => Axis(p, h)),
            MinVertical = values.Min(p => Axis(p, v)),
            MaxVertical = values.Max(p => Axis(p, v))
        };
        return new
        {
            HorizontalAxis = "XYZ"[h].ToString(),
            VerticalAxis = "XYZ"[v].ToString(),
            DepthAxis = "XYZ"[d].ToString(),
            Full = Extent(all),
            Focus = Extent(focus)
        };
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
    private static string Vector(Point3 p) => $"({N(p.X)}, {N(p.Y)}, {N(p.Z)})";
    private static void Text(StringBuilder svg, int x, int y, string text) => svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{x}\" y=\"{y}\">{SecurityElement.Escape(text)}</text>");
    private static void CheckBudget(StringBuilder svg) { if (svg.Length > MaximumSvgBytes) throw Invalid("Surface artifact exceeds the bounded SVG budget."); }
    private static S2ModKitException Invalid(string message) => Errors.Verification("DIRECTIONAL_PREVIEW_INVALID", message, "Regenerate a complete bounded preview; no partial artifact is published.");
}
