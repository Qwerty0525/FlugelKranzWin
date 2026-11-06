using System.Numerics;
using FlugelKranz.Core;
using SkiaSharp;

namespace FlugelKranz.Compass;

/// <summary>
/// Draws the gravity compass: a faint sphere with a thin outline (no highlights), a ring for the
/// real horizontal plane, a thick softly glowing gravity arrow and thin forward / right arrows.
/// Anything on the far half of the sphere (pointing away from the viewer) is dimmed.
/// </summary>
public sealed class CompassRenderer : ICompassRenderer, IDisposable
{
    public const int DefaultSize = 256;
    private static readonly SKColor Forward = SKColor.Parse("#7fa8e8");
    private static readonly SKColor Right = SKColor.Parse("#e8907a");
    private static readonly SKColor Shell = new(0xe8, 0xf0, 0xf4);

    private readonly SKBitmap bitmap;
    private readonly SKCanvas canvas;
    private readonly SKPaint paint = new() { IsAntialias = true, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    private readonly SKMaskFilter glowBlur;
    private readonly object gate = new();

    public CompassRenderer(int size = DefaultSize)
    {
        Size = size;
        bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        canvas = new SKCanvas(bitmap);
        glowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, size * 0.022f);
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
        float centre = Size / 2f;
        float radius = Size * CompassMath.SphereFraction / 2;
        canvas.Clear(SKColors.Transparent);

        // Faint fill and thin outline only: no reflections, rim light or specular spot.
        paint.MaskFilter = null;
        paint.Style = SKPaintStyle.Fill;
        paint.Color = Shell.WithAlpha(30);
        canvas.DrawCircle(centre, centre, radius, paint);

        DrawRing(axes, centre, radius, glow, farHalf: true);
        var arrows = new (Vector3 Direction, SKColor Color, float Width, bool Glow)[]
        {
            (axes.Forward, Forward, Size * 0.012f, false),
            (axes.Right, Right, Size * 0.012f, false),
            (axes.Down, GlowColor(glow), Size * 0.03f, true)
        };
        // Far arrows first, so near ones and the outline sit on top.
        foreach (var arrow in arrows.OrderBy(a => a.Direction.Z))
            if (arrow.Direction.Z < 0) DrawArrow(arrow.Direction, arrow.Color, arrow.Width, arrow.Glow, centre, radius);

        paint.MaskFilter = null;
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = Size * 0.007f;
        paint.Color = Shell.WithAlpha(120);
        canvas.DrawCircle(centre, centre, radius, paint);

        DrawRing(axes, centre, radius, glow, farHalf: false);
        foreach (var arrow in arrows.OrderBy(a => a.Direction.Z))
            if (arrow.Direction.Z >= 0) DrawArrow(arrow.Direction, arrow.Color, arrow.Width, arrow.Glow, centre, radius);

        // Small hub where the three axes meet.
        paint.MaskFilter = null;
        paint.Style = SKPaintStyle.Fill;
        paint.Color = Shell.WithAlpha(150);
        canvas.DrawCircle(centre, centre, Size * 0.012f, paint);
        canvas.Flush();
    }

    /// <summary>Visibility of something at depth z (+1 toward the viewer, -1 away).</summary>
    private static float Depth(float z, float far) => far + (1 - far) * Math.Clamp((z + 0.15f) / 0.3f, 0, 1);

    private void DrawRing(CompassAxes axes, float centre, float radius, CompassGlow glow, bool farHalf)
    {
        const int segments = 96;
        float r = radius * 0.94f;
        paint.MaskFilter = null;
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = Size * 0.006f;
        var tint = Blend(Shell, GlowColor(glow), 0.35f);
        for (int i = 0; i < segments; i++)
        {
            float a0 = i * MathF.Tau / segments, a1 = (i + 1) * MathF.Tau / segments;
            var p0 = MathF.Cos(a0) * axes.Right + MathF.Sin(a0) * axes.Forward;
            var p1 = MathF.Cos(a1) * axes.Right + MathF.Sin(a1) * axes.Forward;
            float z = (p0.Z + p1.Z) / 2;
            if (farHalf != z < 0) continue;
            paint.Color = tint.WithAlpha((byte)(110 * Depth(z, 0.3f)));
            canvas.DrawLine(centre + p0.X * r, centre - p0.Y * r, centre + p1.X * r, centre - p1.Y * r, paint);
        }
    }

    private void DrawArrow(Vector3 direction, SKColor color, float width, bool glow, float centre, float radius)
    {
        float length = radius * 0.86f;
        var tip = new SKPoint(centre + direction.X * length, centre - direction.Y * length);
        var start = new SKPoint(centre, centre);
        float projected = MathF.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
        // Gravity stays readable when it points away (looking down at the hand); the thin ones dim more.
        float visibility = Depth(direction.Z, glow ? 0.6f : 0.3f);
        var head = HeadPath(start, tip, projected, width, radius);

        if (glow)
        {
            // Soft phosphorescent halo: a blurred, wider copy under the stroke.
            paint.MaskFilter = glowBlur;
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = width * 2.6f;
            paint.Color = color.WithAlpha((byte)(150 * visibility));
            canvas.DrawLine(start, tip, paint);
            if (head is not null)
            {
                paint.Style = SKPaintStyle.StrokeAndFill;
                canvas.DrawPath(head, paint);
            }
        }

        paint.MaskFilter = null;
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = width;
        var core = glow ? Blend(color, SKColors.White, 0.25f) : color;
        paint.Color = core.WithAlpha((byte)(235 * visibility));
        canvas.DrawLine(start, tip, paint);
        if (head is not null)
        {
            paint.Style = SKPaintStyle.Fill;
            canvas.DrawPath(head, paint);
        }
        else
        {
            // Pointing almost straight at or away from the viewer: a dot instead of a head.
            paint.Style = SKPaintStyle.Fill;
            canvas.DrawCircle(tip, width * 1.2f, paint);
        }
        head?.Dispose();
    }

    private SKPath? HeadPath(SKPoint start, SKPoint tip, float projected, float width, float radius)
    {
        var along = new Vector2(tip.X - start.X, tip.Y - start.Y);
        if (projected < 0.18f || along.LengthSquared() < 1) return null;
        along = Vector2.Normalize(along);
        var side = new Vector2(-along.Y, along.X);
        // Foreshortened arrows get shorter heads.
        float headLength = MathF.Max(width * 2.2f, radius * 0.2f) * MathF.Min(1, projected * 1.2f);
        float headWidth = MathF.Max(width * 1.6f, radius * 0.09f);
        var tipV = new Vector2(tip.X, tip.Y) + along * headLength * 0.35f;
        var baseV = tipV - along * headLength;
        var path = new SKPath();
        path.MoveTo(tipV.X, tipV.Y);
        path.LineTo(baseV.X + side.X * headWidth, baseV.Y + side.Y * headWidth);
        path.LineTo(baseV.X - side.X * headWidth, baseV.Y - side.Y * headWidth);
        path.Close();
        return path;
    }

    private static SKColor Blend(SKColor a, SKColor b, float t) => new(
        (byte)(a.Red + (b.Red - a.Red) * t),
        (byte)(a.Green + (b.Green - a.Green) * t),
        (byte)(a.Blue + (b.Blue - a.Blue) * t),
        (byte)(a.Alpha + (b.Alpha - a.Alpha) * t));

    public void Dispose()
    {
        lock (gate)
        {
            paint.Dispose();
            glowBlur.Dispose();
            canvas.Dispose();
            bitmap.Dispose();
        }
    }
}
