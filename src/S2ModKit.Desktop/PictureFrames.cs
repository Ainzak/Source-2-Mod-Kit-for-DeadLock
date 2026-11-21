using System.Runtime.InteropServices;
using S2ModKit.Application;
using SkiaSharp;

namespace S2ModKit.Desktop;

/// <summary>
/// Reads a picture or an animation file (PNG, JPEG, WebP, BMP, GIF, animated WebP) into frames with SkiaSharp (shipped with Avalonia):
/// every frame of an animation, composed onto the frame it builds on (GIF frames are often only the changed part), with its delay.
/// </summary>
internal static class PictureFrames
{
    public const int MaxFrames = 1024;

    public static IReadOnlyList<PictureFrame> Read(string path)
    {
        using var codec = SKCodec.Create(path)
            ?? throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a picture or animation the app can read (png, jpg, webp, bmp, gif).");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var count = Math.Clamp(codec.FrameCount, 1, MaxFrames);
        var frames = new List<PictureFrame>(count);
        var pixels = new List<byte[]>(count);
        for (var i = 0; i < count; i++)
        {
            var required = codec.FrameCount > 1 ? codec.FrameInfo[i].RequiredFrame : -1;
            var bytes = required >= 0 && required < pixels.Count ? (byte[])pixels[required].Clone() : new byte[info.BytesSize];
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var result = codec.GetPixels(info, handle.AddrOfPinnedObject(), new SKCodecOptions(i, required));
                if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                {
                    throw new InvalidDataException($"'{Path.GetFileName(path)}' frame {i + 1} could not be read ({result}).");
                }
            }
            finally
            {
                handle.Free();
            }

            pixels.Add(bytes);
            frames.Add(new PictureFrame(new PreviewImage(info.Width, info.Height, bytes), codec.FrameCount > 1 ? codec.FrameInfo[i].Duration : 0));
        }

        return frames;
    }
}
