using System.Numerics;
using Poso.Geometry;
using Poso.Graphics;
using SkiaSharp;

namespace Poso.Skia;

/// <summary>Conversions between the core graphics value types and Skia types.</summary>
internal static class SkiaConversions
{
    public static SKColor ToSKColor(PosoColor color) => new(color.R, color.G, color.B, color.A);

    public static SKRect ToSKRect(in RectF rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    public static SKMatrix ToSKMatrix(in Matrix3x2 matrix) => new()
    {
        ScaleX = matrix.M11,
        SkewX = matrix.M21,
        TransX = matrix.M31,
        SkewY = matrix.M12,
        ScaleY = matrix.M22,
        TransY = matrix.M32,
        Persp0 = 0,
        Persp1 = 0,
        Persp2 = 1,
    };

    /// <summary>Converts a core <see cref="IPath"/> segment sequence to an SKPath (the fill type is set by the caller).</summary>
    public static SKPath ToSKPath(IPath path, bool evenOdd)
    {
        var skPath = new SKPath
        {
            FillType = evenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding,
        };

        int count = path.SegmentCount;
        for (int i = 0; i < count; i++)
        {
            PathSegment segment = path.GetSegment(i);
            switch (segment.Type)
            {
                case PathSegmentType.MoveTo:
                    skPath.MoveTo(segment.X1, segment.Y1);
                    break;

                case PathSegmentType.LineTo:
                    skPath.LineTo(segment.X1, segment.Y1);
                    break;

                case PathSegmentType.CubicTo:
                    skPath.CubicTo(segment.X1, segment.Y1, segment.X2, segment.Y2, segment.X3, segment.Y3);
                    break;

                case PathSegmentType.Close:
                    skPath.Close();
                    break;
            }
        }

        return skPath;
    }

    public static void ApplyStrokeStyle(SKPaint paint, in StrokeStyle style)
    {
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = Math.Max(style.Width, 0.01f);
        paint.StrokeCap = style.Cap switch
        {
            LineCap.Butt => SKStrokeCap.Butt,
            LineCap.Round => SKStrokeCap.Round,
            _ => SKStrokeCap.Square,
        };
        paint.StrokeJoin = style.Join switch
        {
            LineJoin.Miter => SKStrokeJoin.Miter,
            LineJoin.Round => SKStrokeJoin.Round,
            _ => SKStrokeJoin.Bevel,
        };
        paint.StrokeMiter = Math.Max(style.MiterLimit, 1f);

        if (style.IsDashed && style.DashArray is { } dash)
        {
            paint.PathEffect = SKPathEffect.CreateDash(dash.ToArray(), style.DashPhase);
        }
        else
        {
            paint.PathEffect = null;
        }
    }
}
