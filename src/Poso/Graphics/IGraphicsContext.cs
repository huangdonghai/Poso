using System.Numerics;
using Poso.Geometry;

namespace Poso.Graphics;

/// <summary>
/// Drawing context: all drawing operations issued by the PDF content interpreter go through this interface.
/// The coordinate system is y-down (consistent with Skia/Avalonia); the PDF y-up to y-down flip is merged
/// into the initial transformation matrix by the interpreter.
/// The current transformation matrix (CTM) is maintained by the context; paths are passed in user space
/// coordinates and the CTM is applied when drawing.
/// </summary>
public interface IGraphicsContext : IDisposable
{
    int Width { get; }

    int Height { get; }

    /// <summary>Clear the entire canvas with the specified color.</summary>
    void Clear(PosoColor color);

    /// <summary>Save the graphics state (matrix, clipping, etc.).</summary>
    void Save();

    /// <summary>Restore the most recently saved graphics state (<see cref="Save"/>).</summary>
    void Restore();

    /// <summary>Set the current transformation matrix (replace).</summary>
    void SetMatrix(in Matrix3x2 matrix);

    /// <summary>Concatenate a transformation matrix (the new matrix is left-multiplied with the current matrix).</summary>
    void ConcatMatrix(in Matrix3x2 matrix);

    /// <summary>Restrict the clip region to the intersection of the rectangle with the existing clip.</summary>
    void ClipRect(in RectF rect);

    /// <summary>Restrict the clip region to the intersection of the path with the existing clip.</summary>
    void ClipPath(IPath path, bool evenOdd);

    /// <summary>Fill the path.</summary>
    void FillPath(IPath path, PosoColor color, bool evenOdd);

    /// <summary>Stroke the path.</summary>
    void StrokePath(IPath path, in StrokeStyle style, PosoColor color);

    /// <summary>
    /// Draw a sequence of positioned glyphs. Coordinates are in text space (the text matrix is already
    /// applied by the caller) and y is the baseline.
    /// <paramref name="strokeStyle"/> is used only by stroke-based rendering modes (Stroke/FillStroke/*Clip).
    /// </summary>
    void DrawGlyphs(IFont font, float fontSize, ReadOnlySpan<GlyphPosition> glyphs, PosoColor color, TextRenderMode mode, in StrokeStyle strokeStyle);

    /// <summary>Draw an image (the source rectangle is in image pixel coordinates; the destination rectangle is in user space coordinates).</summary>
    void DrawImage(IImage image, in RectF destinationRect, in RectF sourceRect, float alpha);

    /// <summary>
    /// Take a snapshot of the current canvas. After the call the context enters a "handed off" state:
    /// the returned <see cref="IImage"/> owns the pixel data, and disposing the context no longer frees that data.
    /// </summary>
    IImage Snapshot();
}
