using Avalonia;
using Poso.Pdf;

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
    /// <summary>Rotated page size in points (1/72 inch).</summary>
    public static Size PageSize(PdfPage page, int rotationDegrees)
    {
        int rotation = Normalize(page.Rotation + rotationDegrees);
        bool swapped = rotation is 90 or 270;
        return new Size(
            swapped ? page.CropBox.Height : page.CropBox.Width,
            swapped ? page.CropBox.Width : page.CropBox.Height);
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
