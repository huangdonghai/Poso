using System.Numerics;
using Poso.Geometry;
using Poso.Graphics;

namespace Poso.PeachDrawing;

/// <summary>
/// PeachDrawing implementation of <see cref="IGraphicsContext"/>: a pure-managed software raster target
/// backed by <c>RasterCanvas</c> painting into a <c>RasterSurface</c> (premultiplied RGBA8).
///
/// The Poso CTM is maintained here and never left resident on Peach's transform stack: every geometry
/// carrying call pushes the CTM for the duration of one canvas operation and pops it right after
/// (clips bake their device-space polygon at push time, so they survive the pop). This mirrors the
/// interpreter's contract where <see cref="SetMatrix"/> may reset the CTM without a matching state pop.
/// </summary>
public sealed class PeachGraphicsContext : IGraphicsContext
{
    private readonly PeachRaster.RasterRenderContext _renderContext;
    private readonly PeachRaster.RasterCanvas _baseCanvas;
    private readonly PeachCore.RasterSurface _baseSurface;
    private readonly int _width;
    private readonly int _height;

    // Poso graphics state (mirrors what the interpreter drives through Save/Restore/SetMatrix/...).
    private Matrix3x2 _ctm = Matrix3x2.Identity;
    private BlendMode _blend = BlendMode.Normal;
    private int _clipDepth;
    private RectF _clipBounds;

    private readonly Stack<State> _states = new();
    private readonly Stack<Layer> _layers = new();
    private bool _disposed;

    /// <summary>Captured <see cref="IGraphicsContext"/> state for one Poso Save level.</summary>
    private readonly record struct State(
        Matrix3x2 Ctm,
        BlendMode Blend,
        int ClipDepth,
        RectF ClipBounds,
        PeachCore.Canvas Canvas,
        int LayerCount);

    /// <summary>An isolated off-surface begun by <see cref="PushLayer"/> (a PDF transparency group).</summary>
    private sealed class Layer
    {
        public required PeachRaster.RasterCanvas Canvas { get; init; }
        public required PeachCore.RasterSurface? Surface { get; init; }
        public required int X { get; init; }
        public required int Y { get; init; }
        public required byte Alpha { get; init; }
        public required BlendMode Mode { get; init; }
        public required IImage? SoftMask { get; init; }

        /// <summary>Parent canvas clip bookkeeping, reinstated when the layer composites away.</summary>
        public required int ParentClipDepth { get; init; }
        public required RectF ParentClipBounds { get; init; }
    }

    public PeachGraphicsContext(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException("The context size must be positive", nameof(width));
        }

        _width = width;
        _height = height;
        _renderContext = PeachGraphicsFactory.RenderContext;
        _baseCanvas = _renderContext.CreateCanvas(width, height, dpi: 72);
        _baseSurface = _baseCanvas.Surface;
        _clipBounds = new RectF(0f, 0f, width, height);
    }

    public int Width => _width;

    public int Height => _height;

    public RectF ClipBounds => _clipBounds;

    /// <summary>
    /// Device pixels per user unit for the current CTM, read off the determinant so a rotation or a
    /// mirrored (flipped) axis still measures a positive scale; 0 when the matrix is degenerate.
    /// </summary>
    private float DeviceScale
    {
        get
        {
            float determinant = (_ctm.M11 * _ctm.M22) - (_ctm.M12 * _ctm.M21);
            return MathF.Sqrt(MathF.Abs(determinant));
        }
    }

    /// <summary>How many Save calls have not yet been matched by a Restore.</summary>
    public int SaveDepth => _states.Count;

    private PeachCore.GraphicsPath? _textClip;
    private RectF _textClipBounds;
    private bool _textClipAny;

    private PeachCore.Canvas Target => _layers.Count > 0 ? _layers.Peek().Canvas : _baseCanvas;

    // ---- state -------------------------------------------------------------------------------------------

    /// <summary>Overwrites the whole base surface with the colour (premultiplied RGBA byte order).</summary>
    public void Clear(PosoColor color)
    {
        (byte r, byte g, byte b, byte a) = color.ToPremulRgba();
        FillSurface(_baseSurface, r, g, b, a);
        _clipBounds = new RectF(0f, 0f, _width, _height);
    }

    public void Save()
    {
        _states.Push(new State(_ctm, _blend, _clipDepth, _clipBounds, Target, _layers.Count));
    }

    public void Restore()
    {
        if (_states.Count == 0)
        {
            return;
        }

        State state = _states.Pop();

        // Only pop clip entries when the clip stack belongs to the same canvas generation; clips set
        // inside a layer that has since been composited away are gone with that layer's canvas.
        if (state.Canvas == Target && state.LayerCount == _layers.Count && state.ClipDepth < _clipDepth)
        {
            for (int pop = _clipDepth; pop > state.ClipDepth; pop--)
            {
                Target.PopClip();
            }
        }

        _ctm = state.Ctm;
        _blend = state.Blend;
        _clipDepth = Math.Min(state.ClipDepth, _clipDepth);
        _clipBounds = state.ClipBounds;
    }

    public void SetMatrix(in Matrix3x2 matrix) => _ctm = matrix;

    /// <summary>PDF <c>cm</c> semantics: the new matrix is applied first, i.e. left-multiplied.</summary>
    public void ConcatMatrix(in Matrix3x2 matrix) => _ctm = matrix * _ctm;

    public void SetBlendMode(BlendMode mode) => _blend = mode;

    // ---- clipping ------------------------------------------------------------------------------------------

    public void ClipRect(in RectF rect)
    {
        if (rect.IsEmpty)
        {
            // An empty clip hides everything downstream; express it as a zero-area device bound.
            _clipBounds = RectF.Empty;
            using DrawScope emptyScope = BeginDraw();
            Target.PushClip(new PeachCore.Rect(0, 0, 0, 0));
            _clipDepth++;
            return;
        }

        using DrawScope scope = BeginDraw();
        Target.PushClip(rect.ToRect());
        _clipDepth++;
        _clipBounds = _clipBounds.Intersect(rect.TransformAabb(_ctm));
    }

    public void ClipPath(IPath path, bool evenOdd)
    {
        if (path.IsEmpty)
        {
            return;
        }

        using DrawScope scope = BeginDraw();
        using PeachCore.GraphicsPath gp = path.ToGraphicsPath(evenOdd, Target, Matrix3x2.Identity);
        Target.PushClip(gp);
        _clipDepth++;
        _clipBounds = _clipBounds.Intersect(path.Bounds.TransformAabb(_ctm));
    }

    /// <summary>
    /// Clips to the outline a stroke of the path covers, so a pattern used as a stroke colour is
    /// confined to the stroke. <c>PathStroker</c> widens the path by the pen's geometry - caps, joins,
    /// miter limit and dashes - so the clip is the shape <see cref="StrokePath"/> paints. A zero-width
    /// pen is a one-device-pixel hairline on the canvas, which has no outline of its own, so the
    /// outline is built at that width instead.
    /// </summary>
    public void ClipStroke(IPath path, in StrokeStyle style)
    {
        if (path.IsEmpty)
        {
            return;
        }

        float scale = DeviceScale;
        if (scale <= 0f)
        {
            return;
        }

        StrokeStyle outlineStyle = style.Width > 0f ? style : style with { Width = 1f / scale };

        using DrawScope scope = BeginDraw();
        using PeachCore.GraphicsPath gp = path.ToGraphicsPath(evenOdd: false, Target, Matrix3x2.Identity);
        PeachCore.Pen pen = Target.GetPen(PosoColor.Black.ToPaintColor());
        pen.ApplyStrokeStyle(outlineStyle);

        // The canvas owns the path once it is pushed as a clip: it lives until the clip level pops.
        using PeachCore.GraphicsPath widened = Target.GetGraphicsPath();
        PeachRaster.PathStroker.Stroke(gp, pen, widened, 0.25f / scale);
        Target.PushClip(widened);
        _clipDepth++;

        // ClipBounds only sizes rasters, so it may overestimate; a miter can reach miterLimit * width / 2
        // past the corner, which is the widest the outline ever gets.
        float pad = outlineStyle.Width * 0.5f * MathF.Max(1f, outlineStyle.Join == LineJoin.Miter ? outlineStyle.MiterLimit : 1f);
        RectF bounds = path.Bounds;
        var strokeBounds = new RectF(bounds.X - pad, bounds.Y - pad, bounds.Width + (2f * pad), bounds.Height + (2f * pad));
        _clipBounds = _clipBounds.Intersect(strokeBounds.TransformAabb(_ctm));
    }

    // ---- drawing ------------------------------------------------------------------------------------------

    public void FillPath(IPath path, PosoColor color, bool evenOdd)
    {
        if (path.IsEmpty)
        {
            return;
        }

        using DrawScope scope = BeginDraw();
        using PeachCore.GraphicsPath gp = path.ToGraphicsPath(evenOdd, Target, Matrix3x2.Identity);
        Target.DrawPath(Target.GetSolidBrush(color.ToPaintColor()), gp);
    }

    public void StrokePath(IPath path, in StrokeStyle style, PosoColor color)
    {
        if (path.IsEmpty)
        {
            return;
        }

        using DrawScope scope = BeginDraw();
        using PeachCore.GraphicsPath gp = path.ToGraphicsPath(evenOdd: false, Target, Matrix3x2.Identity);
        PeachCore.Pen pen = Target.GetPen(color.ToPaintColor());
        pen.ApplyStrokeStyle(style);
        Target.DrawPath(pen, gp);
    }

    /// <summary>
    /// Draws positioned glyphs by merging their outline contours (design units, y-up) at each glyph's
    /// text-space origin into one nonzero-fill path under the current CTM: the same geometry the Skia
    /// back-end's path mode produces, so every text render mode behaves identically across back-ends.
    /// </summary>
    public void DrawGlyphs(
        IFont font,
        float fontSize,
        ReadOnlySpan<GlyphPosition> glyphs,
        PosoColor fillColor,
        PosoColor strokeColor,
        TextRenderMode mode,
        in StrokeStyle strokeStyle)
    {
        if (glyphs.IsEmpty || font is not PeachFont peachFont || mode == TextRenderMode.Invisible)
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

        int unitsPerEm = peachFont.UnitsPerEm;
        double scale = 1d / unitsPerEm;

        using DrawScope scope = BeginDraw();

        if (fill)
        {
            using PeachCore.GraphicsPath fillPath = BuildGlyphPath(peachFont, glyphs, scale);
            Target.DrawPath(Target.GetSolidBrush(fillColor.ToPaintColor()), fillPath);
        }

        if (stroke)
        {
            PeachCore.Pen pen = Target.GetPen(strokeColor.ToPaintColor());
            pen.ApplyStrokeStyle(strokeStyle);
            using PeachCore.GraphicsPath strokePath = BuildGlyphPath(peachFont, glyphs, scale);
            Target.DrawPath(pen, strokePath);
        }
    }

    // A text object in a clipping render mode contributes to the clipping path even when none of its
    // outlines survive; only an accumulator no clip mode touched leaves the page's clip alone.
    private bool _textClipRequested;

    public void OpenTextClip()
    {
        _textClip?.Dispose();
        _textClip = Target.GetGraphicsPath();
        _textClip.FillMode = PeachCore.FillMode.Nonzero;
        _textClipBounds = default;
        _textClipAny = false;
        _textClipRequested = false;
    }

    public void AddTextClipRun(IFont font, ReadOnlySpan<GlyphPosition> glyphs, in Matrix3x2 runMatrix)
    {
        if (_textClip is null || font is not PeachFont peachFont)
        {
            return;
        }

        _textClipRequested = true;

        double scale = 1d / peachFont.UnitsPerEm;
        foreach (GlyphPosition glyph in glyphs)
        {
            if (!peachFont.TryGetOutline(glyph.GlyphId, out PeachOutlines.GlyphOutline? outline) || outline is null || outline.IsEmpty)
            {
                AddSeac(_textClip, peachFont, glyph, scale, runMatrix);
                continue;
            }

            AddOutline(_textClip, outline, glyph.X, glyph.Y, scale, runMatrix);
            RectF bounds = GlyphOutlineBounds(outline, glyph.X, glyph.Y, scale).TransformAabb(runMatrix);
            _textClipBounds = _textClipAny
                ? RectF.FromLbrb(
                    Math.Min(_textClipBounds.Left, bounds.Left), Math.Min(_textClipBounds.Bottom, bounds.Bottom),
                    Math.Max(_textClipBounds.Right, bounds.Right), Math.Max(_textClipBounds.Top, bounds.Top))
                : bounds;
            _textClipAny = true;
        }
    }

    public void AddTextClipPath(IPath path, in Matrix3x2 runMatrix)
    {
        if (_textClip is null || path.IsEmpty)
        {
            return;
        }

        _textClipRequested = true;

        // The run is given in text space and placed by its own matrix, matching AddTextClipRun: the
        // accumulator holds user-space coordinates, so the placement happens on the way in.
        using PeachCore.GraphicsPath placed = path.ToGraphicsPath(evenOdd: false, Target, runMatrix);
        _textClip.AddPath(placed);

        RectF bounds = path.Bounds.TransformAabb(runMatrix);
        _textClipBounds = _textClipAny
            ? RectF.FromLbrb(
                Math.Min(_textClipBounds.Left, bounds.Left), Math.Min(_textClipBounds.Bottom, bounds.Bottom),
                Math.Max(_textClipBounds.Right, bounds.Right), Math.Max(_textClipBounds.Top, bounds.Top))
            : bounds;
        _textClipAny = true;
    }

    public void CloseTextClip()
    {
        PeachCore.GraphicsPath? path = _textClip;
        _textClip = null;

        if (path is not null && (_textClipAny || _textClipRequested))
        {
            // The canvas owns the path from here: it lives and dies with the clip level it was pushed at.
            // BeginDraw puts the canvas in the current CTM, which is what makes the path's user-space
            // coordinates mean what they say - PushClip outside the scope would use a stale transform.
            using DrawScope scope = BeginDraw();
            _clipDepth++;
            Target.PushClip(path);
            // A clip with no surviving outline has nothing in it, so the clip bounds collapse with it.
            _clipBounds = _textClipAny
                ? _clipBounds.Intersect(_textClipBounds.TransformAabb(_ctm))
                : RectF.Empty;
        }
        else
        {
            path?.Dispose();
        }

        _textClipAny = false;
        _textClipRequested = false;
    }

    public void DrawImage(IImage image, in RectF destinationRect, in RectF sourceRect, float alpha)
    {
        if (image is not PeachImage peachImage || destinationRect.IsEmpty || sourceRect.IsEmpty)
        {
            return;
        }

        // Same flip as the Skia back-end: PDF maps the first sample row onto v = 1 of the image's unit
        // square, so the image is drawn flipped within its own destination. The flip belongs to the image
        // space rather than to the CTM, so it applies regardless of what the determinant says.
        Blit(peachImage, destinationRect, sourceRect, alpha,
            Matrix3x2.CreateScale(1f, -1f) *
            Matrix3x2.CreateTranslation(0f, destinationRect.Top + destinationRect.Bottom) *
            _ctm);
    }

    public void DrawDeviceRaster(IImage image, in RectF destinationRect, float alpha)
    {
        if (image is not PeachImage peachImage || destinationRect.IsEmpty)
        {
            return;
        }

        Blit(peachImage, destinationRect, new RectF(0f, 0f, image.Width, image.Height), alpha, _ctm);
    }

    private void Blit(PeachImage peachImage, in RectF destinationRect, in RectF sourceRect, float alpha, in Matrix3x2 transform)
    {
        PeachCore.Image canvasImage = peachImage.AsCanvasImage();
        PeachCore.Rect dest = destinationRect.ToRect();
        PeachCore.Rect src = sourceRect.ToRect();

        using DrawScope scope = BeginDraw(transform);
        double opacity = Math.Clamp(alpha, 0f, 1f);
        bool fullSource = sourceRect.Width >= peachImage.Width - 0.5f && sourceRect.Height >= peachImage.Height - 0.5f
            && sourceRect.X <= 0.5f && sourceRect.Y <= 0.5f;
        if (fullSource)
        {
            Target.DrawImageWithOpacity(canvasImage, dest, opacity, _blend.ToPaintBlendMode());
        }
        else if (opacity >= 1d)
        {
            Target.DrawImage(canvasImage, dest, src);
        }
        else
        {
            // No src-rect + opacity overload in the Peach API: crop into a fresh tight image and fade that.
            using PeachImage cropped = CropToTightImage(peachImage, sourceRect);
            Target.DrawImageWithOpacity(cropped.AsCanvasImage(), dest, opacity, _blend.ToPaintBlendMode());
        }
    }

    /// <summary>Copies a sub-rectangle of a <see cref="PeachImage"/> into a new tightly packed image.</summary>
    private static PeachImage CropToTightImage(PeachImage source, in RectF sourceRect)
    {
        int x = Math.Max(0, (int)sourceRect.X);
        int y = Math.Max(0, (int)sourceRect.Y);
        int w = Math.Min(source.Width - x, Math.Max(1, (int)Math.Round(sourceRect.Width)));
        int h = Math.Min(source.Height - y, Math.Max(1, (int)Math.Round(sourceRect.Height)));

        byte[] all = new byte[source.Width * source.Height * 4];
        source.CopyPixels(all);
        var pixels = new byte[w * h * 4];
        for (int row = 0; row < h; row++)
        {
            int from = ((y + row) * source.Width + x) * 4;
            System.Buffer.BlockCopy(all, from, pixels, row * w * 4, w * 4);
        }

        return new PeachImage(w, h, pixels, premultiplied: true);
    }

    // ---- transparency groups -------------------------------------------------------------------------------

    public void PushLayer(in RectF? bounds, byte alpha, BlendMode mode, IImage? softMask)
    {
        RectF device = bounds is { } b && !b.IsEmpty
            ? b.TransformAabb(_ctm)
            : _clipBounds;
        device = device.Intersect(new RectF(0f, 0f, _width, _height));

        int x = Math.Max(0, (int)MathF.Floor(device.Left));
        int y = Math.Max(0, (int)MathF.Floor(device.Top));
        int w = Math.Max(0, Math.Min(_width - x, (int)MathF.Ceiling(device.Right) - x));
        int h = Math.Max(0, Math.Min(_height - y, (int)MathF.Ceiling(device.Bottom) - y));

        Layer layer;
        if (w <= 0 || h <= 0)
        {
            // Degenerate layer: content draws into a 1x1 discard surface and is dropped on pop.
            var discard = new PeachCore.RasterSurface(1, 1, 0, 0, 1d, 1d);
            layer = new Layer
            {
                Canvas = new PeachRaster.RasterCanvas(_renderContext, discard, pixelsPerPoint: 1),
                Surface = discard,
                X = 0,
                Y = 0,
                Alpha = 0,
                Mode = mode,
                SoftMask = softMask,
                ParentClipDepth = _clipDepth,
                ParentClipBounds = _clipBounds,
            };
            _layers.Push(layer);
            _clipDepth = 0;
            _clipBounds = new RectF(0f, 0f, 1, 1);
            return;
        }

        // Grid-aligned off-surface on the same pixel pitch: compositing back is an exact 1:1 copy.
        var surface = new PeachCore.RasterSurface(w, h, x, y, 1d, 1d);
        layer = new Layer
        {
            Canvas = new PeachRaster.RasterCanvas(_renderContext, surface, pixelsPerPoint: 1),
            Surface = surface,
            X = x,
            Y = y,
            Alpha = alpha,
            Mode = mode,
            SoftMask = softMask,
            ParentClipDepth = _clipDepth,
            ParentClipBounds = _clipBounds,
        };
        _layers.Push(layer);

        // Clip bookkeeping restarts on the layer's canvas - but the canvas keeps the parent's device
        // matrix, because the surface carries its own origin, so the box it restarts with is the layer's
        // rectangle *in device coordinates*. Starting it at (0,0) instead makes every later clip intersect
        // a device-space bound with a surface-space one, which shrinks the reported clip bounds by the
        // layer's offset - and anything sized from them (a soft mask, a shading fill) lands misaligned.
        _clipDepth = 0;
        _clipBounds = new RectF(x, y, w, h);
    }

    public void PopLayer()
    {
        if (_layers.Count == 0)
        {
            return;
        }

        Layer layer = _layers.Pop();
        PeachCore.Canvas parent = Target;

        using (layer.Surface)
        {
            if (layer.Surface is { } surface && parent is not null && layer.Alpha > 0)
            {
                if (layer.SoftMask is { } mask)
                {
                    ApplySoftMask(surface, mask);
                }

                var dest = new PeachCore.Rect(layer.X, layer.Y, surface.Width, surface.Height);
                using SurfaceImage image = new(surface);
                // The isolated group composites once, with the group alpha and blend mode.
                parent.DrawImageWithOpacity(image, dest, layer.Alpha / 255d, layer.Mode.ToPaintBlendMode());
            }
        }

        _clipDepth = layer.ParentClipDepth;
        _clipBounds = layer.ParentClipBounds;
    }

    /// <summary>
    /// Multiplies the layer's alpha by the mask's luminosity (the mask arrives as BGRA whose channels
    /// already carry the DeviceGray luminance), sampling it scaled to the surface. PDF SMask G default
    /// is luminance; the core bakes the gray value into RGB with alpha 255.
    /// </summary>
    private static void ApplySoftMask(PeachCore.RasterSurface surface, IImage mask)
    {
        int mw = mask.Width;
        int mh = mask.Height;
        if (mw <= 0 || mh <= 0)
        {
            return;
        }

        byte[] maskPixels = new byte[mw * mh * 4];
        mask.CopyPixels(maskPixels);

        int sw = surface.Width;
        int sh = surface.Height;
        for (int y = 0; y < sh; y++)
        {
            Span<byte> row = surface.Row(y);
            int my = (int)((long)y * mh / sh);
            for (int x = 0; x < sw; x++)
            {
                int mx = (int)((long)x * mw / sw);
                byte lum = maskPixels[(my * mw + mx) * 4]; // red channel carries the luminance
                int p = x * 4;
                row[p] = (byte)((row[p] * lum + 255) / 256);
                row[p + 1] = (byte)((row[p + 1] * lum + 255) / 256);
                row[p + 2] = (byte)((row[p + 2] * lum + 255) / 256);
                row[p + 3] = (byte)((row[p + 3] * lum + 255) / 256);
            }
        }
    }

    public IImage Snapshot()
    {
        int count = _width * _height;
        var bgra = new byte[count * 4];
        for (int y = 0; y < _height; y++)
        {
            ReadOnlySpan<byte> row = _baseSurface.Row(y);
            int o = y * _width * 4;
            for (int x = 0; x < _width; x++)
            {
                int p = o + x * 4;
                bgra[p] = row[x * 4 + 2];     // B
                bgra[p + 1] = row[x * 4 + 1]; // G
                bgra[p + 2] = row[x * 4];     // R
                bgra[p + 3] = row[x * 4 + 3]; // A
            }
        }

        return new PeachImage(_width, _height, bgra, premultiplied: true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        while (_layers.Count > 0)
        {
            _layers.Pop().Surface?.Dispose();
        }

        _baseSurface.Dispose();
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Pushes the given user-to-device matrix and the current blend mode onto the target canvas;
    /// disposing the scope pops them again. All geometry in the wrapped call is passed in Poso user
    /// space (y-up PDF space under the interpreter's initial flip), matching the Skia back-end.
    /// </summary>
    private DrawScope BeginDraw() => BeginDraw(_ctm);

    private DrawScope BeginDraw(Matrix3x2 transform)
    {
        PeachCore.Canvas canvas = Target;
        bool pushedTransform = transform != Matrix3x2.Identity;
        if (pushedTransform)
        {
            canvas.PushTransform(transform);
        }

        PeachCore.PaintBlendMode blend = _blend.ToPaintBlendMode();
        bool pushedBlend = blend != PeachCore.PaintBlendMode.Normal;
        if (pushedBlend)
        {
            canvas.PushBlendMode(blend);
        }

        return new DrawScope(canvas, pushedTransform, pushedBlend);
    }

    private readonly struct DrawScope(PeachCore.Canvas canvas, bool pushedTransform, bool pushedBlend) : IDisposable
    {
        public void Dispose()
        {
            if (pushedBlend)
            {
                canvas.PopBlendMode();
            }

            if (pushedTransform)
            {
                canvas.PopTransform();
            }
        }
    }

    /// <summary>
    /// Builds one merged path for a glyph run: each outline's design-unit contours (y-up) translated to
    /// the glyph's text-space origin and divided by units-per-em, so the CTM's font-size scale applies once.
    /// </summary>
    private PeachCore.GraphicsPath BuildGlyphPath(PeachFont font, ReadOnlySpan<GlyphPosition> glyphs, double designToEm)
    {
        PeachCore.GraphicsPath gp = Target.GetGraphicsPath();
        gp.FillMode = PeachCore.FillMode.Nonzero;

        foreach (GlyphPosition glyph in glyphs)
        {
            if (!font.TryGetOutline(glyph.GlyphId, out PeachOutlines.GlyphOutline? outline) || outline is null || outline.IsEmpty)
            {
                AddSeac(gp, font, glyph, designToEm);
                continue;
            }

            AddOutline(gp, outline, glyph.X, glyph.Y, designToEm);
        }

        return gp;
    }

    /// <summary>
    /// A seac composite has no outline of its own, so it is drawn as its base plus its accent, the accent
    /// shifted by the charstring's own offset. PeachDrawing does not expand these composites itself, which
    /// left every accented glyph in a Type 1C font blank on this back end.
    /// </summary>
    private static void AddSeac(PeachCore.GraphicsPath gp, PeachFont font, in GlyphPosition glyph, double scale)
    {
        if (!font.TryGetSeacParts(glyph.GlyphId, out PeachOutlines.GlyphOutline? baseOutline,
                out PeachOutlines.GlyphOutline? accentOutline, out float accentX, out float accentY) ||
            baseOutline is null)
        {
            return;
        }

        AddOutline(gp, baseOutline, glyph.X, glyph.Y, scale);
        if (accentOutline is not null && !accentOutline.IsEmpty)
        {
            // The offset is in design units, the same space the outline points are in.
            float dx = (float)(accentX * scale);
            float dy = (float)(accentY * scale);
            AddOutline(gp, accentOutline, glyph.X + dx, glyph.Y + dy, scale);
        }
    }

    private static void AddSeac(PeachCore.GraphicsPath gp, PeachFont font, in GlyphPosition glyph, double scale, in Matrix3x2 transform)
    {
        if (!font.TryGetSeacParts(glyph.GlyphId, out PeachOutlines.GlyphOutline? baseOutline,
                out PeachOutlines.GlyphOutline? accentOutline, out float accentX, out float accentY) ||
            baseOutline is null)
        {
            return;
        }

        AddOutline(gp, baseOutline, glyph.X, glyph.Y, scale, transform);
        if (accentOutline is not null && !accentOutline.IsEmpty)
        {
            float dx = (float)(accentX * scale);
            float dy = (float)(accentY * scale);
            AddOutline(gp, accentOutline, glyph.X + dx, glyph.Y + dy, scale, transform);
        }
    }

    private static void AddOutline(
        PeachCore.GraphicsPath gp,
        PeachOutlines.GlyphOutline outline,
        float originX,
        float originY,
        double scale) => AddOutline(gp, outline, originX, originY, scale, Matrix3x2.Identity);

    /// <summary>Append the outline with an extra transform, so runs sharing one clip can be placed in a
    /// common space instead of relying on the CTM at the moment each was drawn.</summary>
    private static void AddOutline(
        PeachCore.GraphicsPath gp,
        PeachOutlines.GlyphOutline outline,
        float originX,
        float originY,
        double scale,
        in Matrix3x2 transform)
    {
        foreach (PeachOutlines.OutlineContour contour in outline.Contours)
        {
            (float sx, float sy) = EmToPoint(contour.Start.X, contour.Start.Y, originX, originY, scale, transform);
            gp.AddMove(sx, sy);
            foreach (PeachOutlines.OutlineSegment segment in contour.Segments)
            {
                (float ex, float ey) = EmToPoint(segment.End.X, segment.End.Y, originX, originY, scale, transform);
                if (segment.IsCubic)
                {
                    (float c1x, float c1y) = EmToPoint(segment.Control1.X, segment.Control1.Y, originX, originY, scale, transform);
                    (float c2x, float c2y) = EmToPoint(segment.Control2.X, segment.Control2.Y, originX, originY, scale, transform);
                    gp.AddBezierTo(c1x, c1y, c2x, c2y, ex, ey);
                }
                else
                {
                    gp.LineTo(ex, ey);
                }
            }

            gp.CloseFigure();
        }
    }

    private static (float X, float Y) EmToPoint(
        double x, double y, float originX, float originY, double scale, in Matrix3x2 transform)
    {
        var em = new Vector2((float)(originX + x * scale), (float)(originY + y * scale));
        Vector2 user = Vector2.Transform(em, transform);
        return (user.X, user.Y);
    }

    /// <summary>Control-point bounds of one outline in em space, before any matrix.</summary>
    private static RectF GlyphOutlineBounds(PeachOutlines.GlyphOutline outline, float originX, float originY, double scale)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        void Consider(double px, double py)
        {
            float x = (float)(originX + px * scale);
            float y = (float)(originY + py * scale);
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        foreach (PeachOutlines.OutlineContour contour in outline.Contours)
        {
            Consider(contour.Start.X, contour.Start.Y);
            foreach (PeachOutlines.OutlineSegment segment in contour.Segments)
            {
                if (segment.IsCubic)
                {
                    Consider(segment.Control1.X, segment.Control1.Y);
                    Consider(segment.Control2.X, segment.Control2.Y);
                }

                Consider(segment.End.X, segment.End.Y);
            }
        }

        return RectF.FromLbrb(minX, minY, maxX, maxY);
    }

    /// <summary>Rough user-space AABB of a glyph run (control-point bounds; ClipBounds only).</summary>
    private static RectF GlyphRunUserBounds(PeachFont font, ReadOnlySpan<GlyphPosition> glyphs, double scale)
    {
        float minX = float.MaxValue;
        float minY = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = float.MinValue;
        bool any = false;

        foreach (GlyphPosition glyph in glyphs)
        {
            if (!font.TryGetOutline(glyph.GlyphId, out PeachOutlines.GlyphOutline? outline) || outline is null || outline.IsEmpty)
            {
                continue;
            }

            any = true;
            foreach (PeachOutlines.OutlineContour contour in outline.Contours)
            {
                Accumulate(glyph, contour.Start.X, contour.Start.Y);
                foreach (PeachOutlines.OutlineSegment segment in contour.Segments)
                {
                    if (segment.IsCubic)
                    {
                        Accumulate(glyph, segment.Control1.X, segment.Control1.Y);
                        Accumulate(glyph, segment.Control2.X, segment.Control2.Y);
                    }

                    Accumulate(glyph, segment.End.X, segment.End.Y);
                }
            }
        }

        void Accumulate(GlyphPosition g, double x, double y)
        {
            float ux = (float)(g.X + x * scale);
            float uy = (float)(g.Y + y * scale);
            if (ux < minX) minX = ux;
            if (ux > maxX) maxX = ux;
            if (uy < minY) minY = uy;
            if (uy > maxY) maxY = uy;
        }

        return any ? new RectF(minX, minY, maxX - minX, maxY - minY) : RectF.Empty;
    }

    private static void FillSurface(PeachCore.RasterSurface surface, byte r, byte g, byte b, byte a)
    {
        for (int y = 0; y < surface.Height; y++)
        {
            Span<byte> row = surface.Row(y);
            for (int x = 0; x < row.Length; x += 4)
            {
                row[x] = r;
                row[x + 1] = g;
                row[x + 2] = b;
                row[x + 3] = a;
            }
        }
    }

    /// <summary>
    /// A PeachDrawing <see cref="PeachCore.Image"/> view over a <c>RasterSurface</c>'s premultiplied
    /// RGBA pixels without an intermediate copy; used to composite finished transparency groups back.
    /// </summary>
    private sealed class SurfaceImage : PeachCore.Image
    {
        private readonly PeachCore.RasterSurface _surface;

        public SurfaceImage(PeachCore.RasterSurface surface)
        {
            _surface = surface;
        }

        public override double Width => _surface.Width;

        public override double Height => _surface.Height;

        public override bool Interpolate { get; set; }

        public override PeachCore.PixelBuffer? GetPixels() =>
            new PeachCore.PixelBuffer(_surface.Width, _surface.Height, _surface.PixelMemory);

        public override void Dispose()
        {
            // The surface belongs to the Layer; PopLayer disposes it separately.
        }
    }
}
