using System.Numerics;
using Poso.Geometry;
using Poso.Graphics;

namespace Poso.PeachDrawing;

/// <summary>Conversions between Poso core value types and the PeachDrawing (PeachCore) API.</summary>
internal static class PeachConversions
{
    /// <summary>Poso RGBA color to PeachPaintColor (the struct keeps R/G/B/A channels; byte order verified against the surface).</summary>
    public static PeachCore.PaintColor ToPaintColor(this PosoColor color) =>
        PeachCore.PaintColor.FromArgb(color.A, color.R, color.G, color.B);

    public static PeachCore.Rect ToRect(this in RectF rect) =>
        new(rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>Axis-aligned rectangle transformed by <paramref name="matrix"/> mapped back to an AABB (Peach clipping takes rects in user space; the device AABB is rebuilt from the transformed corners).</summary>
    public static RectF TransformAabb(this in RectF rect, in Matrix3x2 matrix)
    {
        var p1 = Vector2.Transform(new Vector2(rect.Left, rect.Top), matrix);
        var p2 = Vector2.Transform(new Vector2(rect.Right, rect.Top), matrix);
        var p3 = Vector2.Transform(new Vector2(rect.Left, rect.Bottom), matrix);
        var p4 = Vector2.Transform(new Vector2(rect.Right, rect.Bottom), matrix);
        float minX = Math.Min(Math.Min(p1.X, p2.X), Math.Min(p3.X, p4.X));
        float minY = Math.Min(Math.Min(p1.Y, p2.Y), Math.Min(p3.Y, p4.Y));
        float maxX = Math.Max(Math.Max(p1.X, p2.X), Math.Max(p3.X, p4.X));
        float maxY = Math.Max(Math.Max(p1.Y, p2.Y), Math.Max(p3.Y, p4.Y));
        return new RectF(minX, minY, maxX - minX, maxY - minY);
    }

    public static PeachCore.FillMode ToFillMode(bool evenOdd) =>
        evenOdd ? PeachCore.FillMode.EvenOdd : PeachCore.FillMode.Nonzero;

    public static PeachCore.LineCap ToLineCap(LineCap cap) => cap switch
    {
        Graphics.LineCap.Round => PeachCore.LineCap.Round,
        Graphics.LineCap.Square => PeachCore.LineCap.Square,
        _ => PeachCore.LineCap.Butt,
    };

    public static PeachCore.LineJoin ToLineJoin(LineJoin join) => join switch
    {
        Graphics.LineJoin.Round => PeachCore.LineJoin.Round,
        Graphics.LineJoin.Bevel => PeachCore.LineJoin.Bevel,
        _ => PeachCore.LineJoin.Miter,
    };

    /// <summary>
    /// PDF 16 blend modes onto <c>PaintBlendMode</c>; the Peach enum is CSS-ordered (Normal=0, Multiply=1,
    /// Screen=2, Overlay=3, Darken=4, Lighten=5, ColorDodge=6, ColorBurn=7, HardLight=8, SoftLight=9,
    /// Difference=10, Exclusion=11, Hue=12, Saturation=13, Color=14, Luminosity=15) and maps 1:1.
    /// </summary>
    public static PeachCore.PaintBlendMode ToPaintBlendMode(this BlendMode mode) => mode switch
    {
        BlendMode.Multiply => PeachCore.PaintBlendMode.Multiply,
        BlendMode.Screen => PeachCore.PaintBlendMode.Screen,
        BlendMode.Overlay => PeachCore.PaintBlendMode.Overlay,
        BlendMode.Darken => PeachCore.PaintBlendMode.Darken,
        BlendMode.Lighten => PeachCore.PaintBlendMode.Lighten,
        BlendMode.ColorBurn => PeachCore.PaintBlendMode.ColorBurn,
        BlendMode.ColorDodge => PeachCore.PaintBlendMode.ColorDodge,
        BlendMode.HardLight => PeachCore.PaintBlendMode.HardLight,
        BlendMode.SoftLight => PeachCore.PaintBlendMode.SoftLight,
        BlendMode.Difference => PeachCore.PaintBlendMode.Difference,
        BlendMode.Exclusion => PeachCore.PaintBlendMode.Exclusion,
        BlendMode.Luminosity => PeachCore.PaintBlendMode.Luminosity,
        BlendMode.Hue => PeachCore.PaintBlendMode.Hue,
        BlendMode.Saturation => PeachCore.PaintBlendMode.Saturation,
        BlendMode.Color => PeachCore.PaintBlendMode.Color,
        _ => PeachCore.PaintBlendMode.Normal,
    };

    /// <summary>
    /// Copy a Poso segment-record path into a Peach <c>GraphicsPath</c>. Poso paths are y-down user-space
    /// records; Peach paths are user space too, so no coordinate change is applied here.
    /// </summary>
    public static PeachCore.GraphicsPath ToGraphicsPath(
        this IPath path,
        bool evenOdd,
        PeachCore.Canvas canvas,
        in Matrix3x2 transform)
    {
        PeachCore.GraphicsPath gp = canvas.GetGraphicsPath();
        gp.FillMode = ToFillMode(evenOdd);

        foreach (PathSegment segment in Enumerate(path))
        {
            switch (segment.Type)
            {
                case PathSegmentType.MoveTo:
                {
                    var p = Vector2.Transform(new Vector2(segment.X1, segment.Y1), transform);
                    gp.AddMove(p.X, p.Y);
                    break;
                }

                case PathSegmentType.LineTo:
                {
                    var p = Vector2.Transform(new Vector2(segment.X1, segment.Y1), transform);
                    gp.LineTo(p.X, p.Y);
                    break;
                }

                case PathSegmentType.CubicTo:
                {
                    var c1 = Vector2.Transform(new Vector2(segment.X1, segment.Y1), transform);
                    var c2 = Vector2.Transform(new Vector2(segment.X2, segment.Y2), transform);
                    var end = Vector2.Transform(new Vector2(segment.X3, segment.Y3), transform);
                    gp.AddBezierTo(c1.X, c1.Y, c2.X, c2.Y, end.X, end.Y);
                    break;
                }

                case PathSegmentType.Close:
                    // Peach closes the current contour with an explicit line back to its start; Poso's
                    // Close means the same, so an explicit close keeps fill and stroke behaviour identical.
                    gp.CloseFigure();
                    break;
            }
        }

        return gp;
    }

    private static IEnumerable<PathSegment> Enumerate(IPath path)
    {
        for (int i = 0; i < path.SegmentCount; i++)
        {
            yield return path.GetSegment(i);
        }
    }

    /// <summary>Dash arrays: PDF allows empty/zero entries; PeachCustom patterns must be positive-ish, so degenerate arrays disable dashing.</summary>
    public static void ApplyStrokeStyle(this PeachCore.Pen pen, in StrokeStyle style)
    {
        pen.Width = style.Width;
        pen.LineCap = ToLineCap(style.Cap);
        pen.LineJoin = ToLineJoin(style.Join);
        pen.MiterLimit = style.MiterLimit;

        if (style.DashArray is { Length: > 0 } dash)
        {
            var pattern = new double[dash.Length];
            bool usable = false;
            for (int i = 0; i < dash.Length; i++)
            {
                pattern[i] = dash.Span[i];
                if (dash.Span[i] > 0f)
                {
                    usable = true;
                }
            }

            if (usable)
            {
                pen.SetDashPattern(pattern, style.DashPhase);
                pen.DashStyle = PeachCore.DashStyle.Custom;
            }
            else
            {
                pen.DashStyle = PeachCore.DashStyle.Solid;
            }
        }
        else
        {
            pen.DashStyle = PeachCore.DashStyle.Solid;
        }
    }

    /// <summary>Premultiplied RGBA8 (Peach surface order) from a PosoColor, for direct pixel fills.</summary>
    public static (byte R, byte G, byte B, byte A) ToPremulRgba(this PosoColor color)
    {
        if (color.A == 255)
        {
            return (color.R, color.G, color.B, 255);
        }

        return (
            (byte)((color.R * color.A + 255) / 256),
            (byte)((color.G * color.A + 255) / 256),
            (byte)((color.B * color.A + 255) / 256),
            color.A);
    }
}
