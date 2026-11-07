using System.Text.Json;
using S2ModKit.Adapters.Preview;
using S2ModKit.Application;
using S2ModKit.Infrastructure;

namespace S2ModKit.Desktop;

/// <summary>
/// Preview measurements: frame rate per second of live rendering in the window, the open time,
/// and two captures of the same pose (plain, then the largest part highlighted) to show that a part can be highlighted.
/// Writes &lt;prefix&gt;.json, &lt;prefix&gt;-plain.png and &lt;prefix&gt;-highlight.png, then asks the window to close.
/// </summary>
internal sealed class SelfTest(string prefix, PreviewControl preview, Action close)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private readonly List<double> fps = [];
    private readonly List<string> sizes = [];
    private readonly List<double> longest = [];
    private double previous, longestNow;
    private int frames;
    private double second = 1;
    private int stage;
    private int hiddenPart = -1;
    private int? pickedAtCentre, pickedAfterHide;
    private string? sampleInfo;
    private double openSeconds;
    private string? error;

    public void Opened(PreviewSession? session, TimeSpan elapsed, string? failure)
    {
        openSeconds = elapsed.TotalSeconds;
        error = failure;
        if (session is null)
        {
            Write(null, 0, 0);
        }
    }

    public void Step(PreviewSession session, PreviewView view, int width, int height, double now)
    {
        frames++;
        longestNow = Math.Max(longestNow, now - previous);
        previous = now;
        if (now >= second)
        {
            fps.Add(frames);
            longest.Add(Math.Round(longestNow * 1000));
            longestNow = 0;
            sizes.Add($"{width}x{height}");
            frames = 0;
            second += 1;
        }

        if (stage == 0 && now > 3)
        {
            preview.Playing = false;                               // same pose for both captures
            File.WriteAllBytes(prefix + "-plain.png", new PngPreviewImageEncoder().EncodePng(session.Capture(view, width, height)));
            var largest = session.Parts.Select((part, index) => (part.Indices, index)).Max().index;
            preview.Highlight = largest;
            stage = 1;
        }
        else if (stage == 1 && now > 4)
        {
            File.WriteAllBytes(prefix + "-highlight.png", new PngPreviewImageEncoder().EncodePng(session.Capture(view, width, height)));
            session.HighlightPart = -1;                                          // the control clears it only at the next frame: the captures below need it off
            pickedAtCentre = session.PickPart(width / 2f, height / 2f, width, height);   // the frame just drawn at this size
            hiddenPart = preview.Highlight;
            session.SetPartHidden(hiddenPart, true);
            File.WriteAllBytes(prefix + "-hidden.png", new PngPreviewImageEncoder().EncodePng(session.Capture(view, width, height)));
            pickedAfterHide = session.PickPart(width / 2f, height / 2f, width, height);
            session.SetPartHidden(hiddenPart, false);
            session.SetPartColor(hiddenPart, new S2ModKit.Application.ColorAdjustment(Colorize: new S2ModKit.Application.RgbColor(0.8f, 0.1f, 0.15f), Brightness: 1.4f));
            File.WriteAllBytes(prefix + "-recolor.png", new PngPreviewImageEncoder().EncodePng(session.Capture(view, width, height)));
            if (session.SamplePartColor(width * 0.54f, height * 0.40f, width, height) is { } sampled)   // clothes painted on the same mesh: limit to one colour
            {
                session.SetPartColorRange(sampled.Part, sampled.Color, 0.12f);
                sampleInfo = $"part {sampled.Part} colour {sampled.Color}; mask {(session.PartColorMask(sampled.Part) is { } m ? $"{m.Mask.Count(v => v > 0.99f)} full, {m.Mask.Count(v => v > 0 && v <= 0.99f)} partial of {m.Mask.Length}" : "none")}";
                session.SetPartColor(sampled.Part, new S2ModKit.Application.ColorAdjustment(Colorize: new S2ModKit.Application.RgbColor(0.8f, 0.1f, 0.15f), Brightness: 1.4f));
                File.WriteAllBytes(prefix + "-range.png", new PngPreviewImageEncoder().EncodePng(session.Capture(view, width, height)));
                session.SetPartColorRange(sampled.Part, null, 0);
                session.SetPartColor(sampled.Part, null);
            }

            session.SetPartColor(hiddenPart, null);
            preview.Highlight = -1;
            preview.Playing = true;
            preview.ForcedSize = (1920, 1080);                     // the pass criterion's size, whatever the window
            stage = 2;
        }
        else if (stage == 2 && now > 9)
        {
            stage = 3;
            Write(session, width, height);
        }
    }

    private void Write(PreviewSession? session, int width, int height)
    {
        var record = new
        {
            operation = "desktop_renderer_spike@1",
            gl = preview.GlInfo,
            rendersOnUiThread = preview.RendersOnUiThread,
            renderer = session is null ? null : PreviewSession.RendererName,
            openSeconds,
            error,
            width,
            height,
            framesPerSecond = fps,
            frameSizes = sizes,
            longestFrameMs = longest,
            parts = session?.Parts.Count,
            hiddenPart,
            pickedAtCentre,
            pickedAfterHide,
            sampleInfo,
            animations = session?.Animations.Count,
        };
        File.WriteAllText(prefix + ".json", JsonSerializer.Serialize(record, Indented));
        close();
    }
}
