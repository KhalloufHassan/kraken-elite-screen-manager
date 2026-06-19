using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace KrakenEliteScreenManager.Services;

/// <summary>Frame helpers for the Dashboard render loop.</summary>
public static class LcdFrame
{
    /// <summary>Crop-fill to a square and apply rotation (clockwise degrees).</summary>
    public static void Fit(Image<Rgba32> img, int size, int rotationDegrees)
    {
        img.Mutate(c =>
        {
            c.Resize(new ResizeOptions
            {
                Size = new Size(size, size),
                Mode = ResizeMode.Crop,
                Position = AnchorPositionMode.Center,
            });
            if (rotationDegrees % 360 != 0) c.Rotate(rotationDegrees);
        });
    }

    /// <summary>Encode as a single-frame GIF (asset mode 0x01).</summary>
    public static byte[] ToGif(Image<Rgba32> img)
    {
        using var ms = new MemoryStream();
        img.SaveAsGif(ms, new GifEncoder());
        return ms.ToArray();
    }
}
