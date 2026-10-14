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
    private readonly SKCanvas _canvas;
    private readonly bool _ownsCanvas;
    private readonly SKPaint _fillPaint;
    private readonly SKPaint _strokePaint;
    private readonly SKPaint _imagePaint;
    private readonly Stack<SKPaint> _layerPaints = new();
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
        _canvas = new SKCanvas(_bitmap);
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
        _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
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

    public void SetMatrix(in Matrix3x2 matrix) => _canvas.SetMatrix(SkiaConversions.ToSKMatrix(matrix));

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
        // Isolated transparency group: content composites on an off-surface that is blended back with
        // the given alpha and mode. The luminosity soft mask is honoured when supplied.
        SKPaint paint = new()
        {
            IsAntialias = true,
            Color = new SKColor(255, 255, 255, alpha),
            BlendMode = MapBlendMode(mode),
        };

        if (softMask is SkiaImage maskImage)
        {
            SKShader? maskShader = SKShader.CreateBitmap(
                maskImage.Bitmap,
                SKShaderTileMode.Clamp,
                SKShaderTileMode.Clamp);
            paint.Shader = maskShader?.WithColorFilter(
                SKColorFilter.CreateColorMatrix(new float[]
                {
                    0, 0, 0, 0, 0,
                    0, 0, 0, 0, 0,
                    0, 0, 0, 0, 0,
                    1, 0, 0, 0, 0, // take the mask's red (luminosity) channel as alpha
                }));
        }

        if (bounds is { } b && !b.IsEmpty)
        {
            _canvas.SaveLayer(SkiaConversions.ToSKRect(b), paint);
        }
        else
        {
            _canvas.SaveLayer(paint);
        }

        _layerPaints.Push(paint);
    }

    public void PopLayer()
    {
        _canvas.Restore();
        if (_layerPaints.Count > 0)
        {
            _layerPaints.Pop().Dispose();
        }
    }

    public RectF ClipBounds
    {
        get
        {
            SKRect bounds = _canvas.DeviceClipBounds;
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
        PosoColor color,
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

        SKColor skColor = SkiaConversions.ToSKColor(color);

        // Fill-only runs (the overwhelming majority of text) go through a Skia text blob so the
        // rasterised glyphs are reused from its glyph atlas instead of re-filling an outline path per
        // glyph. Outlines are supplied in em space under the caller's CTM (which already scales by the
        // font size and flips y for the page); Skia draws glyphs y-down, so a local Scale(1,-1) with
        // the pen Y negated reproduces the outline path's orientation exactly. Vertical-writing runs
        // (per-glyph Y != 0) keep the outline path: the mirrored-matrix atlas renders CJK glyphs with
        // slightly different ink than the path fill, and those runs are rare enough not to matter.
        bool baselineAligned = true;
        foreach (GlyphPosition g in glyphs)
        {
            if (g.Y != 0f)
            {
                baselineAligned = false;
                break;
            }
        }

        if (fill && !stroke && !clip && baselineAligned)
        {
            int n = glyphs.Length;
            ushort[] ids = new ushort[n];
            SKPoint[] positions = new SKPoint[n];
            for (int i = 0; i < n; i++)
            {
                ids[i] = glyphs[i].GlyphId;
                positions[i] = new SKPoint(glyphs[i].X, -glyphs[i].Y);
            }

            SKFont runFont = skiaFont.Font;
            runFont.Hinting = SKFontHinting.None;
            runFont.Edging = SKFontEdging.Antialias;
            _fillPaint.Color = skColor;

            using var builder = new SKTextBlobBuilder();
            builder.AddPositionedRun(ids, runFont, positions);
            using SKTextBlob? blob = builder.Build();
            if (blob is not null)
            {
                _canvas.Save();
                _canvas.Scale(1f, -1f);
                _canvas.DrawText(blob, 0f, 0f, _fillPaint);
                _canvas.Restore();
            }

            return;
        }

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

            if (clip)
            {
                _canvas.ClipPath(positioned, antialias: true);
            }

            if (fill)
            {
                _fillPaint.Color = skColor;
                _canvas.DrawPath(positioned, _fillPaint);
            }

            if (stroke)
            {
                // Stroke text width: the graphics state line width, with a floor of 2% of the font size
                var textStroke = new StrokeStyle(
                    Math.Max(strokeStyle.Width, fontSize * 0.02f),
                    strokeStyle.Cap,
                    strokeStyle.Join,
                    strokeStyle.MiterLimit,
                    strokeStyle.DashArray,
                    strokeStyle.DashPhase);
                SkiaConversions.ApplyStrokeStyle(_strokePaint, textStroke);
                _strokePaint.Color = skColor;
                _canvas.DrawPath(positioned, _strokePaint);
            }
        }
    }

    public void DrawImage(IImage image, in RectF destinationRect, in RectF sourceRect, float alpha)
    {
        if (image is not SkiaImage skiaImage || destinationRect.IsEmpty || sourceRect.IsEmpty)
        {
            return;
        }

        _imagePaint.Color = new SKColor(255, 255, 255, (byte)Math.Clamp((int)MathF.Round(alpha * 255f), 0, 255));

        var destination = new SKRect(destinationRect.Left, destinationRect.Top, destinationRect.Right, destinationRect.Bottom);
        var source = new SKRect(sourceRect.Left, sourceRect.Top, sourceRect.Right, sourceRect.Bottom);

        // Skia maps source-rect corners to destination-rect corners in the destination's own
        // frame; when the CTM flips an axis (pages always flip y), the image would come out
        // upside down. Compensate by flipping the canvas within the destination rectangle.
        SKMatrix total = _canvas.TotalMatrix;
        bool flipped = ((total.ScaleX * total.ScaleY) - (total.SkewX * total.SkewY)) < 0;

#pragma warning disable CS0618 // SkiaSharp 3.119.4: the SKSamplingOptions overloads do not support source/destination rectangles, so the obsolete overload is used for now (filter quality via FilterQuality)
        if (flipped)
        {
            _canvas.Save();
            _canvas.Translate(0f, destination.Top + destination.Bottom);
            _canvas.Scale(1f, -1f);
            _canvas.DrawBitmap(skiaImage.Bitmap, source, destination, _imagePaint);
            _canvas.Restore();
        }
        else
        {
            _canvas.DrawBitmap(skiaImage.Bitmap, source, destination, _imagePaint);
        }
#pragma warning restore CS0618
    }

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
            _canvas.Dispose();
        }

        _fillPaint.Dispose();
        _strokePaint.Dispose();
        _imagePaint.Dispose();
    }
}
