using System;
using System.Collections.Generic;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace KrakenEliteScreenManager.Services;

/// <summary>
/// Decodes an animated GIF into standalone 640x640 frames (crop-filled + rotated to
/// the panel mount), each with its display delay — for compositing the dashboard over
/// an animating GIF and streaming the result frame by frame.
/// </summary>
public static class GifFrames
{
    public readonly record struct Frame(Image<Rgba32> Image, int DelayMs);

    public static List<Frame> Load(byte[] gifBytes, int size, int rotationDegrees)
    {
        using var gif = Image.Load<Rgba32>(gifBytes);
        var frames = new List<Frame>(gif.Frames.Count);

        for (int i = 0; i < gif.Frames.Count; i++)
        {
            var frame = gif.Frames.CloneFrame(i); // fully-composited frame as its own image
            frame.Mutate(c =>
            {
                c.Resize(new ResizeOptions
                {
                    Size = new Size(size, size),
                    Mode = ResizeMode.Crop,
                    Position = AnchorPositionMode.Center,
                });
                if (rotationDegrees % 360 != 0) c.Rotate(rotationDegrees);
            });

            int cs = gif.Frames[i].Metadata.GetGifMetadata().FrameDelay; // centiseconds
            frames.Add(new Frame(frame, Math.Max(cs * 10, 20)));         // ≥20ms guard
        }

        // A single-frame (static) image still works — it just doesn't animate.
        if (frames.Count == 0) frames.Add(new Frame(gif.CloneAs<Rgba32>(), 100));
        return frames;
    }
}
