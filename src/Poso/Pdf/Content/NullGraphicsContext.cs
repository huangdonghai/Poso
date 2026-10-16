using System.Numerics;
using Poso.Geometry;
using Poso.Graphics;

namespace Poso.Pdf.Content;

/// <summary>
/// A no-op drawing context used for text-only passes (extraction, search): the interpreter
/// walks the content stream without rasterizing anything.
/// </summary>
internal sealed class NullGraphicsContext : IGraphicsContext
{
    public int Width => 1;

    public int Height => 1;

    public void Clear(PosoColor color)
    {
    }

    public void Save()
    {
    }

    public void Restore()
    {
    }

    public void SetMatrix(in Matrix3x2 matrix)
    {
    }

    public void ConcatMatrix(in Matrix3x2 matrix)
    {
    }

    public void ClipRect(in RectF rect)
    {
    }

    public void ClipPath(IPath path, bool evenOdd)
    {
    }

    public void FillPath(IPath path, PosoColor color, bool evenOdd)
    {
    }

    public void StrokePath(IPath path, in StrokeStyle style, PosoColor color)
    {
    }

    public void OpenTextClip()
    {
    }

    public void AddTextClipRun(IFont font, ReadOnlySpan<GlyphPosition> glyphs, in Matrix3x2 runMatrix)
    {
    }

    public void CloseTextClip()
    {
    }

    public void DrawGlyphs(IFont font, float fontSize, ReadOnlySpan<GlyphPosition> glyphs, PosoColor fillColor, PosoColor strokeColor, TextRenderMode mode, in StrokeStyle strokeStyle)
    {
    }

    public void DrawImage(IImage image, in RectF destinationRect, in RectF sourceRect, float alpha)
    {
    }

    public void DrawDeviceRaster(IImage image, in RectF destinationRect, float alpha)
    {
    }

    public IImage Snapshot() => throw new InvalidOperationException("The null context cannot produce a snapshot");

    public void Dispose()
    {
    }
}
