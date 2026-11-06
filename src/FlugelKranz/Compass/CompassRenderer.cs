using System.Numerics;
using FlugelKranz.Core;
using SkiaSharp;

namespace FlugelKranz.Compass;

/// <summary>
/// Draws the gravity compass: a faint sphere with a thin outline (no highlights), a ring for the
/// real horizontal plane, a thick softly glowing gravity arrow and thin forward / right arrows.
/// Anything on the far half of the sphere (pointing away from the viewer) is dimmed.
/// The image is side-by-side stereo: each half is a perspective view from one eye. Every point
/// of the gizmo (unit sphere, quad frame) goes onto the quad plane along the ray from that eye
/// (<see cref="CompassMath.Project"/>), so the sphere, ring and arrows have real parallax and an
/// arrow pointing at or away from the viewer reads as nearer or farther. Arrow heads are cones
/// (base disc plus the triangle facing the viewer), so seen end-on they show as a disc.
/// One bitmap, canvas, paint and path are reused for every image.
/// </summary>
public sealed class CompassRenderer : ICompassRenderer, IDisposable
{
    public const int DefaultSize = 256;
    private const float ShaftLength = 0.86f;
    private const float RingRadius = 0.94f;
    private const int RingSegments = 96;
    private static readonly SKColor Forward = SKColor.Parse("#7fa8e8");
    private static readonly SKColor Right = SKColor.Parse("#e8907a");
    private static readonly SKColor Shell = new(0xe8, 0xf0, 0xf4);

    private readonly SKBitmap bitmap;
    private readonly SKCanvas canvas;
    private readonly SKPaint paint = new() { IsAntialias = true, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    private readonly SKMaskFilter glowBlur;
    private readonly SKPath path = new() { FillType = SKPathFillType.Winding };
    private readonly SKPath headBase = new() { FillType = SKPathFillType.Winding };
    private readonly SKPath headSide = new() { FillType = SKPathFillType.Winding };
    private readonly SKPath ringPath = new();
    private readonly SKPoint[] silhouette = new SKPoint[72];
    private readonly SKPoint[] disc = new SKPoint[20];
    private readonly SKPoint[] triangle = new SKPoint[3];
    private readonly (Vector3 Direction, SKColor Color, float Width, bool Glow)[] arrows = new (Vector3, SKColor, float, bool)[3];
    private readonly object gate = new();

    // The eye being drawn (quad frame, sphere radii) and the midpoint of both eyes.
    private Vector3 eye, cyclops;
    private readonly float centre, radius;

    public CompassRenderer(int size = DefaultSize)
    {
        Size = size;
        centre = size / 2f;
        radius = size * CompassMath.SphereFraction / 2;
        bitmap = new SKBitmap(new SKImageInfo(2 * size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        canvas = new SKCanvas(bitmap);
        glowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, size * 0.022f);
    }

    /// <summary>One eye's half; the texture is twice as wide.</summary>
    public int Size { get; }

    public static SKColor GlowColor(CompassGlow glow) => glow switch
    {
        CompassGlow.Lime => SKColor.Parse("#a6f06a"),
        CompassGlow.Amber => SKColor.Parse("#ffc46b"),
        _ => SKColor.Parse("#5fe8c4")
    };

    public void Render(CompassAxes axes, CompassGlow glow, CompassEyes eyes, Span<byte> rgba)
    {
        if (rgba.Length < 2 * Size * Size * 4)
            throw new ArgumentException("Buffer is smaller than the texture.", nameof(rgba));
        lock (gate)
        {
            Draw(axes, glow, eyes);
            bitmap.GetPixelSpan().CopyTo(rgba);
        }
    }

    /// <summary>Encodes one stereo image as PNG (for checking the look without SteamVR).</summary>
    public byte[] RenderPng(CompassAxes axes, CompassGlow glow, CompassEyes eyes)
    {
        lock (gate)
        {
            Draw(axes, glow, eyes);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
    }

    private void Draw(CompassAxes axes, CompassGlow glow, CompassEyes eyes)
    {
        canvas.Clear(SKColors.Transparent);
        cyclops = (eyes.Left + eyes.Right) / 2;
        arrows[0] = (axes.Forward, Forward, Size * 0.012f, false);
        arrows[1] = (axes.Right, Right, Size * 0.012f, false);
        arrows[2] = (axes.Down, GlowColor(glow), Size * 0.03f, true);
        // Far arrows first, so near ones and the outline sit on top (same order for both eyes).
        for (int i = 1; i < arrows.Length; i++)
            for (int j = i; j > 0 && arrows[j].Direction.Z < arrows[j - 1].Direction.Z; j--)
                (arrows[j], arrows[j - 1]) = (arrows[j - 1], arrows[j]);
        DrawEye(axes, glow, eyes.Left, 0);
        DrawEye(axes, glow, eyes.Right, Size);
        canvas.Flush();
    }

    private void DrawEye(CompassAxes axes, CompassGlow glow, Vector3 eyePosition, float left)
    {
        eye = eyePosition;
        canvas.Save();
        canvas.ClipRect(new SKRect(left, 0, left + Size, Size));
        canvas.Translate(left, 0);

        // Faint fill and thin outline only: no reflections, rim light or specular spot.
        BuildSilhouette();
        paint.MaskFilter = null;
        paint.Style = SKPaintStyle.Fill;
        paint.Color = Shell.WithAlpha(30);
        canvas.DrawPath(path, paint);

        DrawRing(axes, glow, farHalf: true);
        foreach (var arrow in arrows)
            if (arrow.Direction.Z < 0) DrawArrow(arrow.Direction, arrow.Color, arrow.Width, arrow.Glow);

        BuildSilhouette();
        paint.MaskFilter = null;
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = Size * 0.007f;
        paint.Color = Shell.WithAlpha(120);
        canvas.DrawPath(path, paint);

        DrawRing(axes, glow, farHalf: false);
        foreach (var arrow in arrows)
            if (arrow.Direction.Z >= 0) DrawArrow(arrow.Direction, arrow.Color, arrow.Width, arrow.Glow);

        // Small hub where the three axes meet (the centre lies on the quad: no disparity).
        paint.MaskFilter = null;
        paint.Style = SKPaintStyle.Fill;
        paint.Color = Shell.WithAlpha(150);
        canvas.DrawCircle(Pixel(Vector3.Zero), Size * 0.012f, paint);
        canvas.Restore();
    }

    /// <summary>A point of the gizmo (quad frame, sphere radii) on this eye's half, in pixels.</summary>
    private SKPoint Pixel(Vector3 point)
    {
        var q = CompassMath.Project(point, eye);
        return new(centre + q.X * radius, centre - q.Y * radius);
    }

    /// <summary>The sphere's outline as this eye sees it: the circle where the cone of sight touches the sphere.</summary>
    private void BuildSilhouette()
    {
        path.Reset();
        var seen = eye with { Z = MathF.Max(eye.Z, CompassMath.MinimumEyeDistance) };
        float distance = seen.Length();
        var toward = seen / distance;
        float lift = 1 / distance, ring = MathF.Sqrt(1 - lift * lift);
        var u = Vector3.Normalize(Vector3.Cross(toward, MathF.Abs(toward.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var v = Vector3.Cross(toward, u);
        for (int i = 0; i < silhouette.Length; i++)
        {
            float a = i * MathF.Tau / silhouette.Length;
            silhouette[i] = Pixel(toward * lift + (MathF.Cos(a) * u + MathF.Sin(a) * v) * ring);
        }
        AddPolygon(path, silhouette);
    }

    /// <summary>Visibility of something at depth z (+1 toward the viewer, -1 away).</summary>
    private static float Depth(float z, float far) => far + (1 - far) * Math.Clamp((z + 0.15f) / 0.3f, 0, 1);

    private void DrawRing(CompassAxes axes, CompassGlow glow, bool farHalf)
    {
        paint.MaskFilter = null;
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = Size * 0.006f;
        var tint = Blend(Shell, GlowColor(glow), 0.35f);
        // Runs of segments with the same alpha go out as one polyline: far fewer draw calls than
        // one line per segment, and no darker beads where round caps overlapped.
        ringPath.Reset();
        int runAlpha = -1;
        for (int i = 0; i < RingSegments; i++)
        {
            float a0 = i * MathF.Tau / RingSegments, a1 = (i + 1) * MathF.Tau / RingSegments;
            var p0 = (MathF.Cos(a0) * axes.Right + MathF.Sin(a0) * axes.Forward) * RingRadius;
            var p1 = (MathF.Cos(a1) * axes.Right + MathF.Sin(a1) * axes.Forward) * RingRadius;
            float z = (p0.Z + p1.Z) / 2 / RingRadius;
            int alpha = farHalf != z < 0 ? -1 : (int)(110 * Depth(z, 0.3f));
            if (alpha != runAlpha)
            {
                FlushRing(tint, runAlpha);
                runAlpha = alpha;
                if (alpha >= 0) ringPath.MoveTo(Pixel(p0));
            }
            if (alpha >= 0) ringPath.LineTo(Pixel(p1));
        }
        FlushRing(tint, runAlpha);
    }

    private void FlushRing(SKColor tint, int alpha)
    {
        if (alpha >= 0 && !ringPath.IsEmpty)
        {
            paint.Color = tint.WithAlpha((byte)alpha);
            canvas.DrawPath(ringPath, paint);
        }
        ringPath.Reset();
    }

    private void DrawArrow(Vector3 direction, SKColor color, float width, bool glow)
    {
        var start = Pixel(Vector3.Zero);
        var tip = Pixel(direction * ShaftLength);
        // Gravity stays readable when it points away (looking down at the hand); the thin ones dim more.
        float visibility = Depth(direction.Z, glow ? 0.6f : 0.3f);
        bool baseShows = BuildHead(direction, width);

        if (glow)
        {
            // Soft phosphorescent halo: a blurred, wider copy under the stroke.
            paint.MaskFilter = glowBlur;
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = width * 2.6f;
            paint.Color = color.WithAlpha((byte)(150 * visibility));
            canvas.DrawLine(start, tip, paint);
            paint.Style = SKPaintStyle.StrokeAndFill;
            canvas.DrawPath(path, paint);
        }

        paint.MaskFilter = null;
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = width;
        var core = glow ? Blend(color, SKColors.White, 0.25f) : color;
        paint.Color = core.WithAlpha((byte)(235 * visibility));
        canvas.DrawLine(start, tip, paint);
        paint.Style = SKPaintStyle.Fill;
        if (!baseShows)
        {
            canvas.DrawPath(path, paint);
            return;
        }
        // Pointing away: the cone's flat base faces the viewer, in front of its side. A darker base
        // over the bright side reads as the back of the arrow head even in one eye.
        canvas.DrawPath(headSide, paint);
        paint.Color = Blend(core, SKColors.Black, 0.45f).WithAlpha((byte)(235 * visibility));
        canvas.DrawPath(headBase, paint);
    }

    /// <summary>
    /// The arrow head as a cone in 3D: its base disc (<see cref="headBase"/>) and the triangle from the
    /// apex to the base edge facing the viewer (<see cref="headSide"/>), both projected for this eye, and
    /// their union in <see cref="path"/>. Sideways it is the usual triangle; pointing at or away from the
    /// viewer it becomes a disc. Returns true when the base faces the viewer (the arrow points away).
    /// </summary>
    private bool BuildHead(Vector3 direction, float width)
    {
        path.Reset();
        headBase.Reset();
        headSide.Reset();
        float headLength = MathF.Max(width * 2.2f, radius * 0.2f) / radius;
        float headWidth = MathF.Max(width * 1.6f, radius * 0.09f) / radius;
        var apex = direction * (ShaftLength + headLength * 0.35f);
        var basePoint = apex - direction * headLength;
        var a = Vector3.Normalize(Vector3.Cross(direction, MathF.Abs(direction.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var b = Vector3.Cross(direction, a);
        for (int i = 0; i < disc.Length; i++)
        {
            float angle = i * MathF.Tau / disc.Length;
            disc[i] = Pixel(basePoint + (MathF.Cos(angle) * a + MathF.Sin(angle) * b) * headWidth);
        }
        AddPolygon(path, disc);
        AddPolygon(headBase, disc);
        // Both eyes use the same 3D triangle (facing the midpoint between them), so the pair fuses.
        var toViewer = cyclops - basePoint;
        var side = Vector3.Cross(direction, toViewer);
        if (side.LengthSquared() > 1e-8f)
        {
            side = Vector3.Normalize(side) * headWidth;
            triangle[0] = Pixel(apex);
            triangle[1] = Pixel(basePoint + side);
            triangle[2] = Pixel(basePoint - side);
            AddPolygon(path, triangle);
            AddPolygon(headSide, triangle);
        }
        return Vector3.Dot(direction, toViewer) < 0;
    }

    /// <summary>Adds a closed polygon, always wound the same way, so overlapping parts fill as one shape.</summary>
    private void AddPolygon(SKPath target, SKPoint[] polygon)
    {
        float area = 0;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            area += polygon[j].X * polygon[i].Y - polygon[i].X * polygon[j].Y;
        if (area >= 0)
        {
            target.MoveTo(polygon[0]);
            for (int i = 1; i < polygon.Length; i++) target.LineTo(polygon[i]);
        }
        else
        {
            target.MoveTo(polygon[^1]);
            for (int i = polygon.Length - 2; i >= 0; i--) target.LineTo(polygon[i]);
        }
        target.Close();
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
            path.Dispose();
            headBase.Dispose();
            headSide.Dispose();
            ringPath.Dispose();
            glowBlur.Dispose();
            canvas.Dispose();
            bitmap.Dispose();
        }
    }
}
