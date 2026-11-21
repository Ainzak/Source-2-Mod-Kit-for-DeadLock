using S2ModKit.Domain;

namespace S2ModKit.Application;

/// <summary>One frame of a picture (RGBA, not premultiplied) and how long it shows (0 for a still picture).</summary>
public sealed record PictureFrame(PreviewImage Image, int DelayMs);

/// <summary>A picture or animation to place: its frames and a brightness factor (1 = as it is).</summary>
public sealed record PictureLayer(IReadOnlyList<PictureFrame> Frames, float Brightness = 1f);

/// <summary>How layers fill the canvas: <c>Whole</c> shows every layer entirely (stacked, centred, dark bars where needed);
/// <c>Fill</c> covers each layer's share of the canvas and crops what sticks out.</summary>
public enum PictureFit
{
    Whole,
    Fill,
}

/// <summary>A grid of equal cells in a texture: <see cref="Columns"/> x <see cref="Rows"/> cells of <see cref="CellWidth"/> x
/// <see cref="CellHeight"/> pixels, filled row by row from the top left.</summary>
public sealed record PictureGrid(int Columns, int Rows, int CellWidth, int CellHeight);

/// <summary>The composed frames of a picture at the canvas's shape, played at <see cref="FramesPerSecond"/> (one frame: a still).</summary>
public sealed record ComposedPicture(IReadOnlyList<PreviewImage> Frames, float FramesPerSecond, float LoopSeconds);

/// <summary>
/// Composes pictures for a surface in the game (a painting, a hero picture): layers stacked on one canvas of the surface's real
/// shape, animations put on one timeline, and the frames laid out in one texture. Everything here is plain pixel work: no game
/// files, no UI. Timeline: the longest animation sets the loop and the frame rate; each shorter animation plays a whole number of
/// times in that loop (sped up or slowed down a little, 0.7 s became 0.625 s in the first in-game test), so every layer starts
/// again with the loop and nothing jumps.
/// </summary>
public static class PictureComposer
{
    /// <summary>The colour behind and between layers (dark, as the painting test used).</summary>
    public static readonly byte[] Background = [18, 16, 14, 255];

    /// <summary>Composes <paramref name="layers"/> on a <paramref name="width"/> x <paramref name="height"/> canvas.
    /// <paramref name="speed"/> scales the playback (2 = twice as fast); at most <paramref name="maxFrames"/> frames (more are
    /// thinned out, keeping the loop's length).</summary>
    public static ComposedPicture Compose(IReadOnlyList<PictureLayer> layers, int width, int height, PictureFit fit, int maxFrames, float speed = 1f)
    {
        ArgumentNullException.ThrowIfNull(layers);
        if (layers.Count == 0 || layers.Any(layer => layer.Frames.Count == 0))
        {
            throw Errors.Input("PICTURE_NO_FRAMES", "There is no picture to place.", "Add a picture or an animation.");
        }

        if (width < 1 || height < 1 || maxFrames < 1 || !float.IsFinite(speed) || speed <= 0f)
        {
            throw Errors.Input("PICTURE_CANVAS", $"The canvas ({width}x{height}, {maxFrames} frames, speed {speed}) is not usable.", "Report the target.");
        }

        var durations = layers.Select(Duration).ToArray();
        var main = Array.IndexOf(durations, durations.Max());
        if (durations[main] <= 0)
        {
            return new ComposedPicture([Frame(layers, [.. layers.Select(layer => layer.Frames[0].Image)], width, height, fit)], 0f, 0f);
        }

        var loopMs = durations[main];
        var count = Math.Min(maxFrames, layers[main].Frames.Count);
        var loops = durations.Select(d => d <= 0 ? 0 : Math.Max(1, (int)Math.Round(loopMs / (double)d))).ToArray();
        var frames = new List<PreviewImage>(count);
        for (var k = 0; k < count; k++)
        {
            var at = loopMs * (k / (double)count);                          // in the main animation's own milliseconds
            var images = layers.Select((layer, i) => durations[i] <= 0 ? layer.Frames[0].Image
                : FrameAt(layer, at % (loopMs / (double)loops[i]) * (durations[i] / (loopMs / (double)loops[i])))).ToArray();
            frames.Add(Frame(layers, images, width, height, fit));
        }

        var loopSeconds = loopMs / 1000f / speed;
        return new ComposedPicture(frames, count / loopSeconds, loopSeconds);
    }

    /// <summary>How many frames <see cref="Compose"/> makes for these layers: 1 for still pictures, else the longest animation's
    /// frame count, at most <paramref name="maxFrames"/>.</summary>
    public static int FrameCount(IReadOnlyList<PictureLayer> layers, int maxFrames)
    {
        ArgumentNullException.ThrowIfNull(layers);
        var durations = layers.Select(Duration).ToArray();
        return durations.Length == 0 || durations.Max() <= 0 ? 1 : Math.Min(maxFrames, layers[Array.IndexOf(durations, durations.Max())].Frames.Count);
    }

    /// <summary>The grid for <paramref name="frames"/> cells in a <paramref name="textureWidth"/> x <paramref name="textureHeight"/>
    /// texture: columns and rows are powers of two (exact in the game's frame formula), as few cells as hold the frames (the largest
    /// cells), and among those the cell shape nearest the canvas's (<paramref name="canvasAspect"/> = width / height).</summary>
    public static PictureGrid Grid(int frames, int textureWidth, int textureHeight, float canvasAspect)
    {
        PictureGrid? best = null;
        var bestCells = int.MaxValue;
        var bestShape = double.MaxValue;
        for (var columns = 1; columns <= textureWidth; columns *= 2)
        {
            for (var rows = 1; rows <= textureHeight; rows *= 2)
            {
                var cells = columns * rows;
                if (cells < frames)
                {
                    continue;
                }

                var shape = Math.Abs(Math.Log(textureWidth / (double)columns / (textureHeight / (double)rows) / canvasAspect));
                if (cells < bestCells || (cells == bestCells && shape < bestShape))
                {
                    (best, bestCells, bestShape) = (new PictureGrid(columns, rows, textureWidth / columns, textureHeight / rows), cells, shape);
                }
            }
        }

        return best ?? throw Errors.Input("PICTURE_GRID", $"{frames} frames do not fit a {textureWidth}x{textureHeight} texture.", "Use a shorter animation.");
    }

    /// <summary>The frames in one texture: each frame squeezed into its cell (the game stretches the cell back to the surface's shape),
    /// unused cells dark.</summary>
    public static PreviewImage Atlas(IReadOnlyList<PreviewImage> frames, int textureWidth, int textureHeight, PictureGrid grid)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(grid);
        var atlas = Filled(textureWidth, textureHeight);
        for (var k = 0; k < frames.Count; k++)
        {
            var cell = Resize(frames[k], grid.CellWidth, grid.CellHeight);
            Paste(atlas, cell, k % grid.Columns * grid.CellWidth, k / grid.Columns * grid.CellHeight);
        }

        return atlas;
    }

    /// <summary>Resamples an image to <paramref name="width"/> x <paramref name="height"/>: bilinear, averaged over the source pixels
    /// one output pixel covers when it shrinks.</summary>
    public static PreviewImage Resize(PreviewImage source, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Width == width && source.Height == height)
        {
            return source;
        }

        var output = new byte[width * height * 4];
        double sx = source.Width / (double)width, sy = source.Height / (double)height;
        int nx = Math.Max(1, (int)Math.Ceiling(sx)), ny = Math.Max(1, (int)Math.Ceiling(sy));
        Parallel.For(0, height, y =>
        {
            Span<double> sum = stackalloc double[4];
            for (var x = 0; x < width; x++)
            {
                sum.Clear();
                for (var j = 0; j < ny; j++)
                {
                    for (var i = 0; i < nx; i++)
                    {
                        Sample(source, ((x + ((i + 0.5) / nx)) * sx) - 0.5, ((y + ((j + 0.5) / ny)) * sy) - 0.5, sum);
                    }
                }

                var at = ((y * width) + x) * 4;
                for (var c = 0; c < 4; c++)
                {
                    output[at + c] = (byte)Math.Clamp(Math.Round(sum[c] / (nx * ny)), 0, 255);
                }
            }
        });
        return new PreviewImage(width, height, output);
    }

    private static int Duration(PictureLayer layer) => layer.Frames.Count < 2 ? 0 : layer.Frames.Sum(frame => Delay(frame.DelayMs));

    /// <summary>A frame delay as browsers play it: 0 to 10 ms counts as 100 ms.</summary>
    private static int Delay(int ms) => ms <= 10 ? 100 : ms;

    private static PreviewImage FrameAt(PictureLayer layer, double ms)
    {
        var elapsed = 0;
        foreach (var frame in layer.Frames)
        {
            elapsed += Delay(frame.DelayMs);
            if (ms < elapsed)
            {
                return frame.Image;
            }
        }

        return layer.Frames[^1].Image;
    }

    private static PreviewImage Frame(IReadOnlyList<PictureLayer> layers, PreviewImage[] images, int width, int height, PictureFit fit)
    {
        var canvas = Filled(width, height);
        var gap = layers.Count > 1 ? Math.Max(2, height / 50) : 0;
        if (fit == PictureFit.Fill)
        {
            var share = (height - (gap * (layers.Count - 1))) / layers.Count;
            for (var i = 0; i < layers.Count; i++)
            {
                var image = images[i];
                var scale = Math.Max(width / (double)image.Width, share / (double)image.Height);
                var scaled = Brighten(Resize(image, Math.Max(1, (int)Math.Round(image.Width * scale)), Math.Max(1, (int)Math.Round(image.Height * scale))), layers[i].Brightness);
                Paste(canvas, Crop(scaled, (scaled.Width - width) / 2, (scaled.Height - share) / 2, width, share), 0, i * (share + gap));
            }

            return canvas;
        }

        var heights = images.Select(image => image.Height * width / (double)image.Width).ToArray();
        var total = heights.Sum() + (gap * (layers.Count - 1));
        var shrink = Math.Min(1.0, height / total);
        var y = (height - (total * shrink)) / 2;
        for (var i = 0; i < layers.Count; i++)
        {
            var w = Math.Max(1, (int)Math.Round(width * shrink));
            var h = Math.Max(1, (int)Math.Round(heights[i] * shrink));
            Paste(canvas, Brighten(Resize(images[i], w, h), layers[i].Brightness), (width - w) / 2, (int)Math.Round(y));
            y += (heights[i] + gap) * shrink;
        }

        return canvas;
    }

    private static PreviewImage Filled(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            Background.CopyTo(pixels, i);
        }

        return new PreviewImage(width, height, pixels);
    }

    private static PreviewImage Brighten(PreviewImage image, float factor)
    {
        if (Math.Abs(factor - 1f) < 0.001f)
        {
            return image;
        }

        var pixels = (byte[])image.Rgba.Clone();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                pixels[i + c] = (byte)Math.Clamp(Math.Round(pixels[i + c] * factor), 0, 255);
            }
        }

        return image with { Rgba = pixels };
    }

    private static PreviewImage Crop(PreviewImage image, int left, int top, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var sy = Math.Clamp(top + y, 0, image.Height - 1);
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Clamp(left + x, 0, image.Width - 1);
                Array.Copy(image.Rgba, ((sy * image.Width) + sx) * 4, pixels, ((y * width) + x) * 4, 4);
            }
        }

        return new PreviewImage(width, height, pixels);
    }

    /// <summary>Draws <paramref name="image"/> over <paramref name="canvas"/> at (left, top), blended by its alpha; the canvas stays opaque.</summary>
    private static void Paste(PreviewImage canvas, PreviewImage image, int left, int top)
    {
        for (var y = 0; y < image.Height; y++)
        {
            var cy = top + y;
            if (cy < 0 || cy >= canvas.Height)
            {
                continue;
            }

            for (var x = 0; x < image.Width; x++)
            {
                var cx = left + x;
                if (cx < 0 || cx >= canvas.Width)
                {
                    continue;
                }

                var s = ((y * image.Width) + x) * 4;
                var d = ((cy * canvas.Width) + cx) * 4;
                var a = image.Rgba[s + 3] / 255.0;
                for (var c = 0; c < 3; c++)
                {
                    canvas.Rgba[d + c] = (byte)Math.Round((image.Rgba[s + c] * a) + (canvas.Rgba[d + c] * (1 - a)));
                }

                canvas.Rgba[d + 3] = 255;
            }
        }
    }

    private static void Sample(PreviewImage image, double x, double y, Span<double> sum)
    {
        x = Math.Clamp(x, 0, image.Width - 1);
        y = Math.Clamp(y, 0, image.Height - 1);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, image.Width - 1), y1 = Math.Min(y0 + 1, image.Height - 1);
        double fx = x - x0, fy = y - y0;
        for (var c = 0; c < 4; c++)
        {
            var top = (image.Rgba[(((y0 * image.Width) + x0) * 4) + c] * (1 - fx)) + (image.Rgba[(((y0 * image.Width) + x1) * 4) + c] * fx);
            var bottom = (image.Rgba[(((y1 * image.Width) + x0) * 4) + c] * (1 - fx)) + (image.Rgba[(((y1 * image.Width) + x1) * 4) + c] * fx);
            sum[c] += (top * (1 - fy)) + (bottom * fy);
        }
    }
}
