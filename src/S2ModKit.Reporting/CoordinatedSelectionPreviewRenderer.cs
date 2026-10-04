using System.Globalization;
using System.Security;
using System.Text;
using S2ModKit.Application;
using S2ModKit.Domain;
using S2ModKit.Geometry;

namespace S2ModKit.Reporting;

public static class CoordinatedSelectionPreviewRenderer
{
    public static EllipsoidPreviewArtifacts Render(CoordinatedSelectionPreview preview)
    {
        if (preview.PreviewFingerprint != CoordinatedSelectionPreviewBuilder.ComputeFingerprint(preview))
            throw Errors.Verification("COORDINATED_PREVIEW_INVALID", "Preview content identity drifted before rendering.", "Regenerate from the accepted plan.");
        var rowHeight = 365 + preview.Lods.Max(l => l.Buffers.Count) * 20;
        var height = 190 + rowHeight * preview.Lods.Count;
        var svg = new StringBuilder(FormattableString.Invariant($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1800\" height=\"{height}\" viewBox=\"0 0 1800 {height}\"><title>Coordinated common-field selection</title><style>text{{font:14px Arial;fill:#172b42}}</style><rect width=\"100%\" height=\"100%\" fill=\"#f5f7fa\"/>"));
        Text(svg, 20, 30, "COORDINATED COMMON-FIELD SELECTION / original and predicted bind-space geometry");
        Text(svg, 20, 55, $"Input {preview.InputHash.Value[..16]} / Plan {preview.PlanFingerprint.Value[..16]} / Preview {preview.PreviewFingerprint.Value[..16]}");
        Text(svg, 20, 80, Describe(preview.Transform.Field));
        Text(svg, 20, 105, "Orange = full; purple = transition; blue = pinned within selection; gray = excluded unchanged buffers.");
        Text(svg, 20, 130, "Exact mechanical coverage only. No inferred hair, eyeballs, clothing or attachments. Spheres, proxies, collision and live consumers remain unverified.");
        Text(svg, 20, 155, $"Zero boxes: {preview.ZeroBoneBoxPolicy.Kind}; paired zero spheres: {preview.ZeroRenderSpherePolicy.Kind}");
        foreach (var (lod, row) in preview.Lods.Select((l, i) => (l, i)))
        {
            var y = 175 + row * rowHeight;
            Text(svg, 20, y + 20, $"LOD {lod.Lod}: {lod.Buffers.Count} participating buffers; {lod.Context.Count} excluded context buffers / {lod.Context.Sum(c => c.Points.Count)} unchanged context points");
            foreach (var (buffer, index) in lod.Buffers.Select((b, i) => (b, i)))
                Text(svg, 20, y + 42 + index * 20, $"{buffer.Buffer.MemberId} / mesh {buffer.Buffer.MeshOrdinal}, buffer {buffer.Buffer.VertexBufferOrdinal}: {buffer.Buffer.VertexCount} points; full {buffer.Buffer.FullVertexCount}, transition {buffer.Buffer.TransitionVertexCount}, pinned {buffer.Buffer.PinnedVertexCount}; changed positions {buffer.Buffer.ChangedPositionCount}, frames {buffer.Buffer.ChangedFrameCount}");
            var focus = lod.Buffers.SelectMany(b => b.Points.SelectMany(p => new[] { p.Original, p.Predicted })).ToArray();
            for (var projection = 0; projection < 3; projection++)
                for (var predicted = 0; predicted < 2; predicted++)
                    Panel(svg, lod, focus, projection, predicted != 0, 10 + (projection * 2 + predicted) * 298, y + 48 + lod.Buffers.Count * 20);
        }
        svg.Append("</svg>\n");
        var bytes = Encoding.UTF8.GetBytes(svg.ToString()); var sheetHash = ContentHash.Compute(bytes);
        var summary = new
        {
            SchemaVersion = 1,
            Kind = "coordinated_selection",
            ProjectionProfile = "coordinated_orthographic_selection@1",
            ProofLevel = "bind_space_prediction",
            preview.InputHash,
            preview.PlanFingerprint,
            preview.TargetFingerprint,
            preview.PreviewFingerprint,
            preview.Transform,
            preview.Policy,
            preview.ZeroBoneBoxPolicy,
            preview.ZeroRenderSpherePolicy,
            Lods = preview.Lods.Select(l => new
            {
                l.Lod,
                Members = l.Buffers.Select(b => new { b.Buffer, b.DrawCallIds, TriangleCount = b.TriangleIndices.Count / 3 }).ToArray(),
                ExcludedBuffers = l.Context.Select(c => new
                {
                    c.MeshOrdinal,
                    c.VertexBufferOrdinal,
                    c.DrawCallIds,
                    c.MaterialPaths,
                    c.SourceLabel,
                    VertexCount = c.Points.Count,
                    PositionIdentity = ContentHash.Compute(JsonDefaults.SerializeToUtf8(c.Points))
                }).ToArray(),
            }).ToArray(),
            ContactSheetHash = sheetHash,
            Limitations = new[] { "All selected points and triangles retained; excluded buffers are pinned context, cropped only in focused pictures.",
                "Common field applies only to exact listed buffers. Labels do not imply anatomy, hair, eyeballs, clothing, attachments or procedural coordination.",
                "Preserved sphere/proxy/collision/zero fields have unverified containment and consumer behavior; bind-space previews do not prove garment fit, animation or live behavior." },
        };
        var json = JsonDefaults.SerializeToUtf8(summary);
        return new(json, bytes, preview.PreviewFingerprint, ContentHash.Compute(json), sheetHash);
    }

    private static void Panel(StringBuilder svg, CoordinatedPreviewLod lod, Point3[] focus, int projection, bool predicted, int x, int y)
    {
        var h = projection == 2 ? 1 : 0; var v = projection == 0 ? 1 : 2;
        var minX = focus.Min(p => Axis(p, h)); var maxX = focus.Max(p => Axis(p, h));
        var minY = focus.Min(p => Axis(p, v)); var maxY = focus.Max(p => Axis(p, v));
        var scale = Math.Min(260 / Math.Max(maxX - minX, 1e-20), 245 / Math.Max(maxY - minY, 1e-20)) * 0.94;
        string XY(Point3 p) => FormattableString.Invariant($"{x + 143 + (Axis(p, h) - (minX + (maxX - minX) / 2)) * scale:0.###},{y + 161 - (Axis(p, v) - (minY + (maxY - minY) / 2)) * scale:0.###}");
        svg.Append(FormattableString.Invariant($"<rect x=\"{x}\" y=\"{y}\" width=\"286\" height=\"299\" fill=\"white\" stroke=\"#dbe1e8\"/>"));
        Text(svg, x + 10, y + 20, $"{(predicted ? "PREDICTED" : "ORIGINAL")} {"XYZ"[h]}{"XYZ"[v]} (+{"XYZ"[v]} up)");
        svg.Append("<path data-role=\"excluded-context\" fill=\"none\" stroke=\"#c4cad2\" opacity=\"0.65\" d=\"");
        foreach (var p in lod.Context.SelectMany(c => c.Points).Where(p => Axis(p, h) >= minX && Axis(p, h) <= maxX && Axis(p, v) >= minY && Axis(p, v) <= maxY)) svg.Append(CultureInfo.InvariantCulture, $"M{XY(p)}h0.1");
        svg.Append("\"/>");
        foreach (var buffer in lod.Buffers)
        {
            svg.Append(CultureInfo.InvariantCulture, $"<path data-member=\"{SecurityElement.Escape(buffer.Buffer.MemberId)}\" fill=\"none\" stroke=\"#74849b\" stroke-width=\"0.35\" opacity=\"0.3\" d=\"");
            for (var i = 0; i < buffer.TriangleIndices.Count; i += 3)
            {
                var a = buffer.Points[buffer.TriangleIndices[i]]; var b = buffer.Points[buffer.TriangleIndices[i + 1]]; var c = buffer.Points[buffer.TriangleIndices[i + 2]];
                svg.Append(CultureInfo.InvariantCulture, $"M{XY(predicted ? a.Predicted : a.Original)}L{XY(predicted ? b.Predicted : b.Original)}L{XY(predicted ? c.Predicted : c.Original)}Z");
            }
            svg.Append("\"/>");
            foreach (var (membership, color) in new[] { ("full", "#d97706"), ("transition", "#b83280"), ("pinned", "#336eab") })
            {
                svg.Append(CultureInfo.InvariantCulture, $"<path data-membership=\"{membership}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"2\" d=\"");
                foreach (var p in buffer.Points.Where(p => p.Membership == membership)) svg.Append(CultureInfo.InvariantCulture, $"M{XY(predicted ? p.Predicted : p.Original)}h0.1");
                svg.Append("\"/>");
            }
            if (svg.Length > 64 * 1024 * 1024) throw Errors.Unsupported("COORDINATED_PREVIEW_LIMIT_EXCEEDED", "Complete SVG exceeds its 64 MiB budget.", "No partial preview was published.");
        }
    }
    private static string Describe(CoordinatedField field) => field switch
    {
        CoordinatedEllipsoidField e => FormattableString.Invariant($"MODEL SPACE ellipsoid: center ({e.Intent.Field.Center.X:R},{e.Intent.Field.Center.Y:R},{e.Intent.Field.Center.Z:R}), radii ({e.Intent.Field.OuterRadii.X:R},{e.Intent.Field.OuterRadii.Y:R},{e.Intent.Field.OuterRadii.Z:R}), core {e.Intent.Field.CoreFraction:R}, scale {e.Intent.UniformScale:R}"),
        CoordinatedAxisRampField a => FormattableString.Invariant($"MODEL SPACE axis ramp {a.Axis}: pinned through {a.PinnedThrough:R}, full from {a.FullFrom:R}, pivot ({a.Pivot.X:R},{a.Pivot.Y:R},{a.Pivot.Z:R}), scale {a.UniformScale:R}"),
        CoordinatedTiltedRampField t => FormattableString.Invariant($"MODEL SPACE tilted ramp q={t.FirstSign}*{t.FirstAxis}+{t.SecondSign}*{t.SecondAxis}: pinned through {t.PinnedThrough:R}, full from {t.FullFrom:R}, pivot ({t.Pivot.X:R},{t.Pivot.Y:R},{t.Pivot.Z:R}), scale {t.UniformScale:R}"),
        _ => throw new ArgumentException("Unknown field."),
    };
    private static double Axis(Point3 p, int axis) => axis switch { 0 => p.X, 1 => p.Y, _ => p.Z };
    private static void Text(StringBuilder svg, int x, int y, string text) => svg.Append(FormattableString.Invariant($"<text x=\"{x}\" y=\"{y}\">{SecurityElement.Escape(text)}</text>\n"));
}
