using SixLabors.ImageSharp;
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

    /// <summary>Raw 24bpp pixel buffer in B,G,R order — the format asset mode 0x09 expects (verified on-device).</summary>
    public static byte[] ToBgr888(Image<Rgba32> img)
    {
        using var bgr = img.CloneAs<Bgr24>();
        var buf = new byte[bgr.Width * bgr.Height * 3];
        bgr.CopyPixelDataTo(buf);
        return buf;
    }
}
