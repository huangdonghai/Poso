using System.Numerics;
using Avalonia;
using Poso.Geometry;
using Poso.Pdf;
using Vector = Avalonia.Vector;

namespace Poso.Avalonia.Rendering;

/// <summary>Zoom modes of <see cref="Controls.PdfView"/>.</summary>
public enum ZoomMode
{
    /// <summary>Manual zoom factor (see the Zoom property).</summary>
    Custom,

    /// <summary>Fit the whole page into the viewport.</summary>
    FitPage,

    /// <summary>Fit the page width into the viewport.</summary>
    FitWidth,
}

/// <summary>Page layout modes of <see cref="Controls.PdfView"/>.</summary>
public enum PageLayout
{
    /// <summary>Show a single page at a time.</summary>
    SinglePage,

    /// <summary>Show pages in a continuous vertical column.</summary>
    Continuous,
}

/// <summary>Fit-mode and anchor-zoom mathematics for the PDF viewer.</summary>
public static class ZoomMath
{
    /// <summary>Rotated page size in default user space units (1/72 inch, /UserUnit applied).</summary>
    public static Size PageSize(PdfPage page, int rotationDegrees)
    {
        int rotation = Normalize(page.Rotation + rotationDegrees);
        bool swapped = rotation is 90 or 270;
        float width = page.CropBox.Width * page.UserUnit;
        float height = page.CropBox.Height * page.UserUnit;
        return swapped ? new Size(height, width) : new Size(width, height);
    }

    /// <summary>
    /// Matrix mapping page user space - the unrotated, y-up coordinates reported by
    /// <see cref="PdfTextPage"/> and page boxes - to page-view device independent pixels (y-down).
    /// It mirrors the content interpreter's base matrix, including the crop-box offset, /UserUnit
    /// and the page's own /Rotate, so extracted rectangles land on the pixels they came from.
    /// </summary>
    public static Matrix3x2 PageToViewMatrix(PdfPage page, int rotationDegrees, double zoom)
    {
        int rotation = Normalize(page.Rotation + rotationDegrees);
        RectF crop = page.CropBox;
        float unit = page.UserUnit * (float)zoom;

        // Translation into the positive quadrant after the clockwise rotation
        float tx, ty;
        switch (rotation)
        {
            case 90:
                tx = 0;
                ty = crop.Width * unit;
                break;
            case 180:
                tx = crop.Width * unit;
                ty = crop.Height * unit;
                break;
            case 270:
                tx = crop.Height * unit;
                ty = 0;
                break;
            default:
                tx = 0;
                ty = 0;
                break;
        }

        float deviceHeight = rotation is 90 or 270 ? crop.Width * unit : crop.Height * unit;

        return Matrix3x2.CreateTranslation(-crop.X, -crop.Y) *
               Matrix3x2.CreateScale(unit, unit) *
               Matrix3x2.CreateRotation(-rotation * MathF.PI / 180f) *
               Matrix3x2.CreateTranslation(tx, ty) *
               Matrix3x2.CreateScale(1f, -1f) *
               Matrix3x2.CreateTranslation(0f, deviceHeight);
    }

    /// <summary>Maps a page-space rectangle into view space (the four corners, then their bounds).</summary>
    public static Rect ToViewRect(RectF rect, Matrix3x2 pageToView)
    {
        Vector2 p0 = Vector2.Transform(new Vector2(rect.Left, rect.Top), pageToView);
        Vector2 p1 = Vector2.Transform(new Vector2(rect.Right, rect.Top), pageToView);
        Vector2 p2 = Vector2.Transform(new Vector2(rect.Left, rect.Bottom), pageToView);
        Vector2 p3 = Vector2.Transform(new Vector2(rect.Right, rect.Bottom), pageToView);

        float left = MathF.Min(MathF.Min(p0.X, p1.X), MathF.Min(p2.X, p3.X));
        float top = MathF.Min(MathF.Min(p0.Y, p1.Y), MathF.Min(p2.Y, p3.Y));
        float right = MathF.Max(MathF.Max(p0.X, p1.X), MathF.Max(p2.X, p3.X));
        float bottom = MathF.Max(MathF.Max(p0.Y, p1.Y), MathF.Max(p2.Y, p3.Y));

        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    /// <summary>Maps a point in page-view space back to page user space (for hit-testing).</summary>
    public static Vector2 ToPagePoint(Point viewPoint, Matrix3x2 pageToView)
    {
        if (!Matrix3x2.Invert(pageToView, out Matrix3x2 inverse))
        {
            return Vector2.Zero;
        }

        return Vector2.Transform(new Vector2((float)viewPoint.X, (float)viewPoint.Y), inverse);
    }

    /// <summary>Computes the zoom factor for a fit mode; returns 1 for <see cref="ZoomMode.Custom"/>.</summary>
    public static double FitScale(PdfPage page, Size viewport, ZoomMode mode, int rotationDegrees)
    {
        if (mode == ZoomMode.Custom || viewport.Width <= 0 || viewport.Height <= 0)
        {
            return 1d;
        }

        Size size = PageSize(page, rotationDegrees);
        if (size.Width <= 0 || size.Height <= 0)
        {
            return 1d;
        }

        return mode switch
        {
            ZoomMode.FitWidth => viewport.Width / size.Width,
            ZoomMode.FitPage => Math.Min(viewport.Width / size.Width, viewport.Height / size.Height),
            _ => 1d,
        };
    }

    /// <summary>
    /// Computes the scroll offset that keeps the anchor point (in viewport coordinates)
    /// fixed when zooming from one factor to another.
    /// </summary>
    public static Vector AnchorOffset(Vector currentOffset, double oldZoom, double newZoom, Point anchor)
    {
        if (oldZoom <= 0 || newZoom <= 0)
        {
            return currentOffset;
        }

        double factor = newZoom / oldZoom;
        return new Vector(
            (currentOffset.X + anchor.X) * factor - anchor.X,
            (currentOffset.Y + anchor.Y) * factor - anchor.Y);
    }

    /// <summary>Clamps a zoom factor to a sane range.</summary>
    public static double ClampZoom(double zoom) => Math.Clamp(zoom, 0.05, 32d);

    /// <summary>Normalizes a rotation to 0/90/180/270.</summary>
    public static int Normalize(int rotation)
    {
        int normalized = ((rotation % 360) + 360) % 360;
        return normalized switch
        {
            0 or 90 or 180 or 270 => normalized,
            < 45 => 0,
            < 135 => 90,
            < 225 => 180,
            _ => 270,
        };
    }
}
