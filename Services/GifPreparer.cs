using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace KrakenEliteScreenManager.Services;

/// <summary>
/// Prepares a user GIF for the LCD: crop-fill to 640x640, rotate to the panel
/// mount, mark to loop forever. No overlay — pure GIF. Uploaded once, the
/// firmware loops it natively.
/// </summary>
public static class GifPreparer
{
    private const int Size = KrakenLcdDriver.Width;

    public static byte[] MakeLoop(byte[] gifBytes, int rotationDegrees)
    {
        using var img = Image.Load<Rgba32>(gifBytes);
        img.Mutate(c =>
        {
            c.Resize(new ResizeOptions
            {
                Size = new Size(Size, Size),
                Mode = ResizeMode.Crop,
                Position = AnchorPositionMode.Center,
            });
            if (rotationDegrees % 360 != 0) c.Rotate(rotationDegrees);
        });
        img.Metadata.GetGifMetadata().RepeatCount = 0; // loop forever

        using var ms = new MemoryStream();
        img.SaveAsGif(ms, new GifEncoder());
        return ms.ToArray();
    }
}
