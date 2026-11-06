using System.Numerics;
using FlugelKranz.Core;
using SkiaSharp;

namespace FlugelKranz.Compass;

/// <summary>
/// The navball look. Only the half of the sphere facing the viewer is drawn, so what is seen is
/// always the near side. It is shaded by the real frame: a faint sky tint above the real horizon,
/// a faint ground tint below it, and a bright thin line on the real horizontal great circle where
/// they meet, inside a thin faint outline (no reflections or highlights). Real down is a glowing
/// dot in the chosen colour at its pole when the pole is on the near half, or a dimmer hollow ring
/// at the spot it projects to when it is on the far half. A small glowing triangle just outside the
/// rim always points toward real down on the image, so the way down is clear even when the pole is
/// behind the ball. The geometry is in <see cref="NavballMath"/>.
/// </summary>
public sealed class NavballDesign : ICompassDesign
{
    /// <summary>rgba(120, 165, 220, 0.27)</summary>
    public static readonly SKColor Sky = new(120, 165, 220, 69);
    /// <summary>rgba(190, 140, 95, 0.37)</summary>
    public static readonly SKColor Ground = new(190, 140, 95, 94);
    private static readonly SKColor Shell = new(0xe8, 0xf0, 0xf4);
    private const int ArcSegments = 48;

    private readonly SKPaint paint = new() { IsAntialias = true, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    private readonly SKPath path = new();
    private SKMaskFilter? glowBlur, pointerBlur;
    private int blurSize;
    private float centre, radius;

    public void Draw(SKCanvas canvas, int size, CompassAxes axes, CompassGlow glow)
    {
        EnsureBlur(size);
        centre = size / 2f;
        radius = size * CompassMath.SphereFraction / 2;
        var color = CompassRenderer.GlowColor(glow);

        // Sky and ground halves of the near face, then the real horizon between them.
        paint.MaskFilter = null;
        paint.Style = SKPaintStyle.Fill;
        if (NavballMath.Horizon(axes) is { } horizon)
        {
            BuildRegion(horizon.Side, horizon.Near, horizon.SkyRim);
            paint.Color = Sky;
            canvas.DrawPath(path, paint);
            BuildRegion(horizon.Side, horizon.Near, -horizon.SkyRim);
            paint.Color = Ground;
            canvas.DrawPath(path, paint);

            path.Reset();
            for (int i = 0; i <= ArcSegments; i++)
            {
                float t = i * MathF.PI / ArcSegments;
                var point = Pixel(MathF.Cos(t) * horizon.Side + MathF.Sin(t) * horizon.Near);
                if (i == 0) path.MoveTo(point); else path.LineTo(point);
            }
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = size * 0.009f;
            paint.Color = Shell.WithAlpha(235);
            canvas.DrawPath(path, paint);
        }
        else
        {
            // Real up straight at or away from the viewer: the horizon is the rim itself.
            paint.Color = NavballMath.IsSky(Vector2.Zero, axes) ? Sky : Ground;
            canvas.DrawCircle(centre, centre, radius, paint);
        }

        paint.MaskFilter = null;
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = size * 0.006f;
        paint.Color = Shell.WithAlpha(90);
        canvas.DrawCircle(centre, centre, radius, paint);

        var pole = NavballMath.PoleInImage(axes);
        var polePixel = new SKPoint(centre + pole.X * radius, centre - pole.Y * radius);
        if (NavballMath.PoleOnFront(axes))
        {
            paint.MaskFilter = glowBlur;
            paint.Style = SKPaintStyle.Fill;
            paint.Color = color.WithAlpha(170);
            canvas.DrawCircle(polePixel, size * 0.05f, paint);
            paint.MaskFilter = null;
            paint.Color = Blend(color, SKColors.White, 0.3f).WithAlpha(250);
            canvas.DrawCircle(polePixel, size * 0.028f, paint);
        }
        else
        {
            paint.MaskFilter = glowBlur;
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = size * 0.018f;
            paint.Color = color.WithAlpha(55);
            canvas.DrawCircle(polePixel, size * 0.032f, paint);
            paint.MaskFilter = null;
            paint.StrokeWidth = size * 0.009f;
            paint.Color = color.WithAlpha(150);
            canvas.DrawCircle(polePixel, size * 0.032f, paint);
        }

        // Edge pointer: just outside the rim, pointing outward along the way to real down.
        var down = NavballMath.PointerDirection(axes);
        var along = new Vector2(down.X, -down.Y);
        var across = new Vector2(-along.Y, along.X);
        var middle = new Vector2(centre, centre);
        var tip = middle + along * (radius + size * 0.075f);
        var baseCentre = middle + along * (radius + size * 0.022f);
        var left = baseCentre + across * size * 0.032f;
        var right = baseCentre - across * size * 0.032f;
        path.Reset();
        path.MoveTo(tip.X, tip.Y);
        path.LineTo(left.X, left.Y);
        path.LineTo(right.X, right.Y);
        path.Close();
        paint.MaskFilter = pointerBlur;
        paint.Style = SKPaintStyle.StrokeAndFill;
        paint.StrokeWidth = size * 0.012f;
        paint.Color = color.WithAlpha(150);
        canvas.DrawPath(path, paint);
        paint.MaskFilter = null;
        paint.Style = SKPaintStyle.Fill;
        paint.Color = Blend(color, SKColors.White, 0.2f).WithAlpha(240);
        canvas.DrawPath(path, paint);
    }

    /// <summary>A sphere point (quad frame, sphere radii) seen straight on, in pixels.</summary>
    private SKPoint Pixel(Vector3 point) => new(centre + point.X * radius, centre - point.Y * radius);

    /// <summary>
    /// The part of the near face on one side of the horizon: the horizon's front half from
    /// <paramref name="side"/> through <paramref name="near"/> to -<paramref name="side"/>, back along
    /// the rim through <paramref name="rim"/>.
    /// </summary>
    private void BuildRegion(Vector3 side, Vector3 near, Vector3 rim)
    {
        path.Reset();
        for (int i = 0; i <= ArcSegments; i++)
        {
            float t = i * MathF.PI / ArcSegments;
            var point = Pixel(MathF.Cos(t) * side + MathF.Sin(t) * near);
            if (i == 0) path.MoveTo(point); else path.LineTo(point);
        }
        for (int i = 1; i <= ArcSegments; i++)
        {
            float t = i * MathF.PI / ArcSegments;
            path.LineTo(Pixel(-MathF.Cos(t) * side + MathF.Sin(t) * rim));
        }
        path.Close();
    }

    private void EnsureBlur(int size)
    {
        if (blurSize == size && glowBlur is not null) return;
        glowBlur?.Dispose();
        pointerBlur?.Dispose();
        glowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, size * 0.022f);
        pointerBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, size * 0.012f);
        blurSize = size;
    }

    private static SKColor Blend(SKColor a, SKColor b, float t) => new(
        (byte)(a.Red + (b.Red - a.Red) * t),
        (byte)(a.Green + (b.Green - a.Green) * t),
        (byte)(a.Blue + (b.Blue - a.Blue) * t),
        (byte)(a.Alpha + (b.Alpha - a.Alpha) * t));

    public void Dispose()
    {
        paint.Dispose();
        path.Dispose();
        glowBlur?.Dispose();
        pointerBlur?.Dispose();
    }
}
