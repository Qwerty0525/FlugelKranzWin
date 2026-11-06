using FlugelKranz.Core;
using SkiaSharp;

namespace FlugelKranz.Compass;

/// <summary>
/// One compass look: draws the whole image for one view onto a cleared, transparent square canvas.
/// <see cref="CompassRenderer"/> owns the bitmap, the copy into the texture buffer and PNG export,
/// so a new design only implements this.
/// </summary>
public interface ICompassDesign : IDisposable
{
    /// <param name="canvas">Cleared to transparent, <paramref name="size"/> × <paramref name="size"/> pixels.</param>
    /// <param name="size">Canvas width and height in pixels.</param>
    /// <param name="axes">The real axes in the quad's frame (+X right, +Y up, +Z toward the viewer).</param>
    /// <param name="glow">The colour chosen in the settings.</param>
    void Draw(SKCanvas canvas, int size, CompassAxes axes, CompassGlow glow);
}

/// <summary>
/// Renders the compass with a swappable <see cref="ICompassDesign"/> (the navball by default) into
/// one reused straight-alpha RGBA bitmap, which <c>CompassOverlay</c> copies into its GPU texture.
/// </summary>
public sealed class CompassRenderer : ICompassRenderer, IDisposable
{
    public const int DefaultSize = 256;

    private readonly SKBitmap bitmap;
    private readonly SKCanvas canvas;
    private readonly ICompassDesign design;
    private readonly object gate = new();

    public CompassRenderer(int size = DefaultSize, ICompassDesign? design = null)
    {
        Size = size;
        this.design = design ?? new NavballDesign();
        bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        canvas = new SKCanvas(bitmap);
    }

    public int Size { get; }

    public static SKColor GlowColor(CompassGlow glow) => glow switch
    {
        CompassGlow.Lime => SKColor.Parse("#a6f06a"),
        CompassGlow.Amber => SKColor.Parse("#ffc46b"),
        _ => SKColor.Parse("#5fe8c4")
    };

    public void Render(CompassAxes axes, CompassGlow glow, Span<byte> rgba)
    {
        if (rgba.Length < Size * Size * 4)
            throw new ArgumentException("Buffer is smaller than the texture.", nameof(rgba));
        lock (gate)
        {
            Draw(axes, glow);
            bitmap.GetPixelSpan().CopyTo(rgba);
        }
    }

    /// <summary>Encodes one image as PNG (for checking the look without SteamVR).</summary>
    public byte[] RenderPng(CompassAxes axes, CompassGlow glow)
    {
        lock (gate)
        {
            Draw(axes, glow);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
    }

    private void Draw(CompassAxes axes, CompassGlow glow)
    {
        canvas.Clear(SKColors.Transparent);
        design.Draw(canvas, Size, axes, glow);
        canvas.Flush();
    }

    public void Dispose()
    {
        lock (gate)
        {
            design.Dispose();
            canvas.Dispose();
            bitmap.Dispose();
        }
    }
}
