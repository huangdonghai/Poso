using System.Numerics;
using Poso.Geometry;
using Poso.Graphics;
using SkiaSharp;

namespace Poso.Skia;

/// <summary>
/// Skia implementation of <see cref="IGraphicsContext"/>.
/// The default off-screen bitmap mode owns its SKBitmap; an external SKCanvas mode is also
/// supported (not owned, for future live rendering integration).
/// </summary>
public sealed class SkiaGraphicsContext : IGraphicsContext
{
    private readonly SKBitmap? _bitmap;
    private readonly SKCanvas _baseCanvas;
    private SKCanvas _canvas;
    private readonly bool _ownsCanvas;
    private readonly SKPaint _fillPaint;
    private readonly SKPaint _strokePaint;
    private readonly SKPaint _imagePaint;
    private readonly Stack<Layer> _layers = new();
    private SKPath? _textClip;
    private bool _textClipRequested;
    private readonly int _width;
    private readonly int _height;
    private bool _snapshotTaken;

    /// <summary>Creates an off-screen bitmap context (Bgra8888 premultiplied).</summary>
    public SkiaGraphicsContext(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException("The context size must be positive", nameof(width));
        }

        _width = width;
        _height = height;
        _bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        _baseCanvas = new SKCanvas(_bitmap);
        _canvas = _baseCanvas;
        _ownsCanvas = true;

        _fillPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        _strokePaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
        _imagePaint = new SKPaint { IsAntialias = true };
#pragma warning disable CS0618
        _imagePaint.FilterQuality = SKFilterQuality.Medium;
#pragma warning restore CS0618
    }

    /// <summary>Wraps an external canvas (does not own the bitmap; the caller manages its lifetime).</summary>
    public SkiaGraphicsContext(SKCanvas canvas)
    {
        _baseCanvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        _canvas = _baseCanvas;
        _width = (int)canvas.LocalClipBounds.Width;
        _height = (int)canvas.LocalClipBounds.Height;

        _fillPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        _strokePaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
        _imagePaint = new SKPaint { IsAntialias = true };
#pragma warning disable CS0618
        _imagePaint.FilterQuality = SKFilterQuality.Medium;
#pragma warning restore CS0618
    }

    public int Width => _width;

    public int Height => _height;

    public void Clear(PosoColor color) => _canvas.Clear(SkiaConversions.ToSKColor(color));

    public void Save() => _canvas.Save();

    public void Restore() => _canvas.Restore();

    /// <summary>
    /// Set the device-space matrix, keeping any masked layer's surface origin. Each such layer owns an
    /// off-surface whose pixel (0,0) is a device pixel other than (0,0), so a matrix that is absolute in
    /// device space has to be shifted by that origin - otherwise everything drawn after it lands a
    /// layer-origin away and misses the surface.
    /// </summary>
    public void SetMatrix(in Matrix3x2 matrix)
    {
        Matrix3x2 device = LayerOffset() is { } offset
            ? matrix * Matrix3x2.CreateTranslation(-offset.X, -offset.Y)
            : matrix;
        _canvas.SetMatrix(SkiaConversions.ToSKMatrix(device));
    }

    /// <summary>
    /// The summed origin of every masked layer currently open, or null when none is. Each nested layer
    /// takes its parent's total matrix before applying its own shift, so the offsets add up.
    /// </summary>
    private (int X, int Y)? LayerOffset()
    {
        int x = 0;
        int y = 0;
        bool any = false;
        foreach (Layer layer in _layers)
        {
            if (layer.Surface is not null)
            {
                x += layer.X;
                y += layer.Y;
                any = true;
            }
        }

        return any ? (x, y) : null;
    }

    public void ConcatMatrix(in Matrix3x2 matrix) => _canvas.Concat(SkiaConversions.ToSKMatrix(matrix));

    public void SetBlendMode(BlendMode mode)
    {
        SKBlendMode blend = MapBlendMode(mode);
        _fillPaint.BlendMode = blend;
        _strokePaint.BlendMode = blend;
        _imagePaint.BlendMode = blend;
    }

    public void PushLayer(in RectF? bounds, byte alpha, BlendMode mode, IImage? softMask)
    {
        if (softMask is not SkiaImage maskImage)
        {
            // An isolated group with no spatial mask is exactly what SaveLayer gives: the layer is
            // composited back once, with this paint's alpha and blend mode.
            SKPaint paint = new()
            {
                IsAntialias = true,
                Color = new SKColor(255, 255, 255, alpha),
                BlendMode = MapBlendMode(mode),
            };

            if (bounds is { } b && !b.IsEmpty)
            {
                _canvas.SaveLayer(SkiaConversions.ToSKRect(b), paint);
            }
            else
            {
                _canvas.SaveLayer(paint);
            }

            _layers.Push(new Layer(paint, null, null, 0, 0, 0, 0, alpha, mode, null));
            return;
        }

        // SaveLayer composites with the paint's alpha and blend mode but never consults the paint's
        // shader, so a soft mask has to be multiplied into the group's own pixels. That needs a surface
        // this context owns, on the same pixel pitch as the device, so compositing back is a 1:1 copy.
        SKRect clip = _canvas.DeviceClipBounds;
        int x = Math.Clamp((int)MathF.Floor(clip.Left), 0, _width);
        int y = Math.Clamp((int)MathF.Floor(clip.Top), 0, _height);
        int layerWidth = Math.Clamp((int)MathF.Ceiling(clip.Right), x, _width) - x;
        int layerHeight = Math.Clamp((int)MathF.Ceiling(clip.Bottom), y, _height) - y;

        // A layer whose clip fell outside the device still has to absorb its content, so it gets a
        // 1x1 surface and is dropped on pop.
        SKSurface surface = SKSurface.Create(new SKImageInfo(
            Math.Max(1, layerWidth), Math.Max(1, layerHeight), SKColorType.Bgra8888, SKAlphaType.Premul))!;
        SKCanvas layerCanvas = surface.Canvas;

        // The surface covers the device region starting at (x,y), so the parent matrix has to be shifted
        // by that origin *in device space*: translating after it, not before. Pre-multiplying the shift
        // instead moves the content by (x,y) scaled and flipped by the page matrix, which lands off a
        // surface this size and makes the whole group disappear.
        layerCanvas.SetMatrix(_canvas.TotalMatrix.PostConcat(SKMatrix.CreateTranslation(-x, -y)));

        _layers.Push(new Layer(null, surface, _canvas, x, y, layerWidth, layerHeight, alpha, mode, maskImage));
        _canvas = layerCanvas;
    }

    public void PopLayer()
    {
        if (_layers.Count == 0)
        {
            return;
        }

        Layer layer = _layers.Pop();
        if (layer.Surface is null)
        {
            _canvas.Restore();
            layer.Paint?.Dispose();
            return;
        }

        SKCanvas parent = layer.ParentCanvas!;
        _canvas = parent;

        using (layer.Surface)
        {
            int width = Math.Max(1, layer.Width);
            int height = Math.Max(1, layer.Height);

            if (layer.SoftMask is SkiaImage maskImage && layer.Alpha > 0)
            {
                // The mask arrives as BGRA whose channels already carry the mask coverage, so reading a
                // channel into alpha and compositing with DstIn multiplies the group's coverage away.
                using SKPaint maskPaint = new()
                {
                    BlendMode = SKBlendMode.DstIn,
                    ColorFilter = SKColorFilter.CreateColorMatrix(new float[]
                    {
                        0, 0, 0, 0, 0,
                        0, 0, 0, 0, 0,
                        0, 0, 0, 0, 0,
                        1, 0, 0, 0, 0,
                    }),
                };

                SKCanvas canvas = layer.Surface.Canvas;
                canvas.Save();
                canvas.SetMatrix(SKMatrix.Identity);
                using SKImage mask = SKImage.FromBitmap(maskImage.Bitmap);
                canvas.DrawImage(mask, new SKRect(0, 0, width, height), maskPaint);
                canvas.Restore();
            }

            if (layer.Alpha > 0)
            {
                using SKImage group = layer.Surface.Snapshot();
                using SKPaint composite = new()
                {
                    Color = new SKColor(255, 255, 255, layer.Alpha),
                    BlendMode = MapBlendMode(layer.Mode),
                };

                parent.Save();
                parent.SetMatrix(SKMatrix.Identity);
                parent.DrawImage(group, new SKRect(layer.X, layer.Y, layer.X + width, layer.Y + height), composite);
                parent.Restore();
            }
        }
    }

    /// <summary>
    /// One pushed transparency group. A layer without a <see cref="Surface"/> is Skia's own
    /// <c>SaveLayer</c>, restored by <c>Restore</c>; a masked one owns an off-surface whose pixels are
    /// composited back by hand.
    /// </summary>
    private sealed class Layer(
        SKPaint? paint,
        SKSurface? surface,
        SKCanvas? parentCanvas,
        int x,
        int y,
        int width,
        int height,
        byte alpha,
        BlendMode mode,
        IImage? softMask)
    {
        public SKPaint? Paint { get; } = paint;
        public SKSurface? Surface { get; } = surface;
        public SKCanvas? ParentCanvas { get; } = parentCanvas;
        public int X { get; } = x;
        public int Y { get; } = y;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public byte Alpha { get; } = alpha;
        public BlendMode Mode { get; } = mode;
        public IImage? SoftMask { get; } = softMask;
    }

    /// <summary>Skia's own save count, less the base level it always starts with.</summary>
    public int SaveDepth => _canvas.SaveCount - 1;

    public RectF ClipBounds
    {
        get
        {
            SKRect bounds = _canvas.DeviceClipBounds;

            // Inside a masked layer the canvas device space is the layer's own surface, whose pixel (0,0)
            // is a device pixel other than (0,0). The clip has to be reported in the parent device space,
            // the one SetMatrix and DrawDeviceRaster work in, otherwise anything sized from this box (a
            // shading or tiling raster, a soft mask) is placed one layer origin away from its pixels.
            if (LayerOffset() is { } offset)
            {
                bounds = new SKRect(
                    bounds.Left + offset.X,
                    bounds.Top + offset.Y,
                    bounds.Right + offset.X,
                    bounds.Bottom + offset.Y);
            }

            return new RectF(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        }
    }

    private static SKBlendMode MapBlendMode(BlendMode mode) => mode switch
    {
        BlendMode.Multiply => SKBlendMode.Multiply,
        BlendMode.Screen => SKBlendMode.Screen,
        BlendMode.Overlay => SKBlendMode.Overlay,
        BlendMode.Darken => SKBlendMode.Darken,
        BlendMode.Lighten => SKBlendMode.Lighten,
        BlendMode.ColorBurn => SKBlendMode.ColorBurn,
        BlendMode.ColorDodge => SKBlendMode.ColorDodge,
        BlendMode.HardLight => SKBlendMode.HardLight,
        BlendMode.SoftLight => SKBlendMode.SoftLight,
        BlendMode.Difference => SKBlendMode.Difference,
        BlendMode.Exclusion => SKBlendMode.Exclusion,
        BlendMode.Luminosity => SKBlendMode.Luminosity,
        BlendMode.Hue => SKBlendMode.Hue,
        BlendMode.Saturation => SKBlendMode.Saturation,
        BlendMode.Color => SKBlendMode.Color,
        _ => SKBlendMode.SrcOver,
    };

    public void ClipRect(in RectF rect)
    {
        if (rect.IsEmpty)
        {
            return;
        }

        _canvas.ClipRect(SkiaConversions.ToSKRect(rect));
    }

    public void ClipPath(IPath path, bool evenOdd)
    {
        if (path.IsEmpty)
        {
            return;
        }

        using SKPath skPath = SkiaConversions.ToSKPath(path, evenOdd);
        _canvas.ClipPath(skPath, antialias: true);
    }

    /// <summary>
    /// Clips to the outline a stroke of the path covers, so a pattern used as a stroke colour is
    /// confined to the stroke. Skia widens the path with the same paint state
    /// <see cref="StrokePath"/> strokes with - caps, joins, dashes and the same hairline clamp for a
    /// zero width - so the clip is the shape this back-end itself would paint.
    /// </summary>
    public void ClipStroke(IPath path, in StrokeStyle style)
    {
        if (path.IsEmpty)
        {
            return;
        }

        SkiaConversions.ApplyStrokeStyle(_strokePaint, style);
        using SKPath skPath = SkiaConversions.ToSKPath(path, evenOdd: false);
        using SKPath outline = new();
        if (_strokePaint.GetFillPath(skPath, outline) && !outline.IsEmpty)
        {
            _canvas.ClipPath(outline, antialias: true);
        }
    }

    public void FillPath(IPath path, PosoColor color, bool evenOdd)
    {
        if (path.IsEmpty)
        {
            return;
        }

        using SKPath skPath = SkiaConversions.ToSKPath(path, evenOdd);
        _fillPaint.Color = SkiaConversions.ToSKColor(color);
        _canvas.DrawPath(skPath, _fillPaint);
    }

    public void StrokePath(IPath path, in StrokeStyle style, PosoColor color)
    {
        if (path.IsEmpty)
        {
            return;
        }

        SkiaConversions.ApplyStrokeStyle(_strokePaint, style);
        _strokePaint.Color = SkiaConversions.ToSKColor(color);
        using SKPath skPath = SkiaConversions.ToSKPath(path, evenOdd: false);
        _canvas.DrawPath(skPath, _strokePaint);
    }

    public void DrawGlyphs(
        IFont font,
        float fontSize,
        ReadOnlySpan<GlyphPosition> glyphs,
        PosoColor fillColor,
        PosoColor strokeColor,
        TextRenderMode mode,
        in StrokeStyle strokeStyle)
    {
        if (glyphs.IsEmpty || font is not SkiaFont skiaFont || mode == TextRenderMode.Invisible)
        {
            return;
        }

        bool fill = mode is TextRenderMode.Fill or TextRenderMode.FillStroke or TextRenderMode.FillClip or TextRenderMode.FillStrokeClip;
        bool stroke = mode is TextRenderMode.Stroke or TextRenderMode.FillStroke or TextRenderMode.StrokeClip or TextRenderMode.FillStrokeClip;
        bool clip = mode is TextRenderMode.FillClip or TextRenderMode.StrokeClip or TextRenderMode.FillStrokeClip or TextRenderMode.Clip;

        if (!fill && !stroke && !clip)
        {
            return;
        }

        SKColor skColor = SkiaConversions.ToSKColor(fillColor);
        SKColor skStroke = SkiaConversions.ToSKColor(strokeColor);

        // The glyphs are filled from their outlines. A Skia text blob would rasterise them from the
        // glyph atlas instead, which is not the same picture: the atlas stores a coverage mask per
        // glyph size, and its anti-aliasing inks the letterforms a little lighter and a little
        // narrower than the analytic path fill - pdf.js endchar.pdf's "LE HOLD-UP PLAN" came out a pixel
        // thinner, and 141 corpus files moved closer to PDFium when the outlines were used. The
        // atlas path was no faster either: rendering the whole corpus took the same 3m40s either way.
        //
        // The outlines are supplied in em space under the caller's CTM (which already scales by the
        // font size and flips y for the page).
        foreach (GlyphPosition glyph in glyphs)
        {
            using SKPath? outline = skiaFont.GetGlyphOutlinePath(glyph.GlyphId);
            if (outline is null || outline.IsEmpty)
            {
                continue;
            }

            using var positioned = new SKPath();
            positioned.AddPath(outline, glyph.X, glyph.Y);
            positioned.FillType = outline.FillType;

            if (fill)
            {
                _fillPaint.Color = skColor;
                _canvas.DrawPath(positioned, _fillPaint);
            }

            if (stroke)
            {
                SkiaConversions.ApplyStrokeStyle(_strokePaint, strokeStyle);
                _strokePaint.Color = skStroke;
                _canvas.DrawPath(positioned, _strokePaint);
            }
        }
    }

    public void OpenTextClip()
    {
        _textClip?.Dispose();
        _textClip = new SKPath { FillType = SKPathFillType.Winding };
        _textClipRequested = false;
    }

    public void AddTextClipRun(IFont font, ReadOnlySpan<GlyphPosition> glyphs, in Matrix3x2 runMatrix)
    {
        if (_textClip is null || font is not SkiaFont skiaFont)
        {
            return;
        }

        // The text object is in a clipping render mode: it contributes to the clipping path whether or not
        // any outline of it survives.
        _textClipRequested = true;

        SKMatrix run = SkiaConversions.ToSKMatrix(runMatrix);
        foreach (GlyphPosition glyph in glyphs)
        {
            using SKPath? outline = skiaFont.GetGlyphOutlinePath(glyph.GlyphId);
            if (outline is null || outline.IsEmpty)
            {
                continue;
            }

            using var placed = new SKPath { FillType = outline.FillType };
            placed.AddPath(outline, glyph.X, glyph.Y, SKPathAddMode.Append);
            _textClip.AddPath(placed, run, SKPathAddMode.Append);
        }
    }

    public void AddTextClipPath(IPath path, in Matrix3x2 runMatrix)
    {
        if (_textClip is null || path.IsEmpty)
        {
            return;
        }

        _textClipRequested = true;
        using SKPath skPath = SkiaConversions.ToSKPath(path, evenOdd: false);
        _textClip.AddPath(skPath, SkiaConversions.ToSKMatrix(runMatrix), SKPathAddMode.Append);
    }

    public void CloseTextClip()
    {
        // An empty clip paints nothing: a Tr 4-7 text object whose outlines all dropped out (an unmapped
        // or broken font, as in pdf.js recursiveCompositGlyf.pdf) still replaces the clipping path, and
        // treating "no path" as "no clip" let the fill that followed cover the whole page.
        if (_textClipRequested && _textClip is { } path)
        {
            _canvas.ClipPath(path, SKClipOperation.Intersect, antialias: true);
        }

        _textClip?.Dispose();
        _textClip = null;
        _textClipRequested = false;
    }

    public void DrawImage(IImage image, in RectF destinationRect, in RectF sourceRect, float alpha)
    {
        if (image is not SkiaImage skiaImage || destinationRect.IsEmpty || sourceRect.IsEmpty)
        {
            return;
        }

        SetImageAlpha(alpha);
        var destination = new SKRect(destinationRect.Left, destinationRect.Top, destinationRect.Right, destinationRect.Bottom);
        var source = new SKRect(sourceRect.Left, sourceRect.Top, sourceRect.Right, sourceRect.Bottom);

        // PDF maps the first sample row onto v = 1 of the image's unit square, while Skia maps the source
        // rect's top edge onto the destination rect's top edge, so the image is drawn flipped within its own
        // destination. That belongs to the image space, not to the CTM: keying it to the matrix determinant
        // cancelled the page's y flip, which landed every image upside down and mirrored placements right side up.
#pragma warning disable CS0618 // SkiaSharp 3.119.4: the SKSamplingOptions overloads do not support source/destination rectangles, so the obsolete overload is used for now (filter quality via FilterQuality)
        _canvas.Save();
        _canvas.Translate(0f, destination.Top + destination.Bottom);
        _canvas.Scale(1f, -1f);
        _canvas.DrawBitmap(skiaImage.Bitmap, source, destination, _imagePaint);
        _canvas.Restore();
#pragma warning restore CS0618
    }

    public void DrawDeviceRaster(IImage image, in RectF destinationRect, float alpha)
    {
        if (image is not SkiaImage skiaImage || destinationRect.IsEmpty)
        {
            return;
        }

        SetImageAlpha(alpha);
        var destination = new SKRect(destinationRect.Left, destinationRect.Top, destinationRect.Right, destinationRect.Bottom);
        var source = new SKRect(0f, 0f, image.Width, image.Height);

#pragma warning disable CS0618 // see above: no SKSamplingOptions overload takes source and destination rectangles
        _canvas.DrawBitmap(skiaImage.Bitmap, source, destination, _imagePaint);
#pragma warning restore CS0618
    }

    private void SetImageAlpha(float alpha) => _imagePaint.Color =
        new SKColor(255, 255, 255, (byte)Math.Clamp((int)MathF.Round(alpha * 255f), 0, 255));

    /// <summary>Takes a snapshot of the current canvas: ownership of the pixel data passes to the returned image.</summary>
    public IImage Snapshot()
    {
        if (_bitmap is null)
        {
            throw new InvalidOperationException("Snapshot is not supported in external canvas mode; use the off-screen mode");
        }

        _snapshotTaken = true;
        return new SkiaImage(_bitmap);
    }

    public void Dispose()
    {
        if (!_snapshotTaken)
        {
            _bitmap?.Dispose();
        }

        if (_ownsCanvas)
        {
            _baseCanvas.Dispose();
        }

        while (_layers.TryPop(out Layer? layer))
        {
            layer?.Surface?.Dispose();
            layer?.Paint?.Dispose();
        }

        _fillPaint.Dispose();
        _strokePaint.Dispose();
        _imagePaint.Dispose();
    }
}
