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

    /// <summary>
    /// Draw a PDF image. The destination rectangle is the image's unit square in user space; PDF puts the
    /// first sample row at v = 1 of that square, so the back end flips the bitmap within the destination and
    /// the CTM (rotation, mirroring) is left to place it.
    /// </summary>
    void DrawImage(IImage image, in RectF destinationRect, in RectF sourceRect, float alpha);

    /// <summary>
    /// Blit a raster the back end itself produced in device space - a shading or a tiling pattern cell - so
    /// its first row already belongs at the top of the destination. No image-space flip applies.
    /// </summary>
    void DrawDeviceRaster(IImage image, in RectF destinationRect, float alpha);

    /// <summary>
    /// Take a snapshot of the current canvas. After the call the context enters a "handed off" state:
    /// the returned <see cref="IImage"/> owns the pixel data, and disposing the context no longer frees that data.
    /// </summary>
    IImage Snapshot();

    /// <summary>
    /// Set the compositing blend mode for subsequent draws until the next call. Back-ends without
    /// support keep the current mode (this default implementation is a no-op).
    /// </summary>
    void SetBlendMode(BlendMode mode)
    {
    }

    /// <summary>
    /// Begin an isolated transparency group: subsequent draws composite on an off-surface and are
    /// blended back with <paramref name="alpha"/> and an optional luminosity <paramref name="softMask"/>
    /// when <see cref="PopLayer"/> runs. No-op in back-ends without support.
    /// </summary>
    void PushLayer(in RectF? bounds, byte alpha, BlendMode mode, IImage? softMask)
    {
    }

    /// <summary>Composite and pop the layer started by <see cref="PushLayer"/>. No-op without a matching push.</summary>
    void PopLayer()
    {
    }

    /// <summary>
    /// The current clip's bounding rectangle in device pixels. Used to size a rasterised shading
    /// fill. Back-ends without precise clip reporting return the whole canvas.
    /// </summary>
    RectF ClipBounds => new RectF(0f, 0f, Width, Height);

    /// <summary>
    /// How many <see cref="Save"/> calls have not been matched by a <see cref="Restore"/>. A content
    /// stream is allowed to leak one, and the interpreter has to un-leak it before popping a
    /// transparency group. Back-ends that do not track it report zero, which makes the balancing a
    /// no-op there.
    /// </summary>
    int SaveDepth => 0;
}
