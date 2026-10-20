using System.Linq;
using Poso.Graphics;
using Poso.Pdf.Colors;
using Poso.Pdf.Filters.Codecs;
using Poso.Pdf.Functions;
using Poso.Pdf.Objects;

namespace Poso.Pdf.Images;

/// <summary>
/// Decodes PDF image XObjects (and inline images) into tightly packed BGRA (premultiplied) pixels.
/// Handles DeviceGray/RGB/CMYK/Indexed/ICCBased sample data, image masks, color-key masks and
/// soft masks; DCTDecode (JPEG) is delegated to the backend factory, while JPXDecode (JPEG 2000) is
/// decoded here because no backend can.
/// </summary>
public static class PdfImageDecoder
{
    /// <summary>
    /// Decodes an image stream. <paramref name="stencilColor"/> is the paint color used for
    /// 1-bit image masks (the current fill color).
    /// </summary>
    public static IImage? Decode(
        PdfDocument document,
        IGraphicsFactory factory,
        PdfStream stream,
        PdfDictionary dictionary,
        PosoColor stencilColor,
        PdfDiagnostics? diagnostics = null)
    {
        int width = (int)(dictionary.GetIntegerValue("Width") ?? 0);
        int height = (int)(dictionary.GetIntegerValue("Height") ?? 0);
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        List<string> filters = GetFilters(dictionary);
        bool hasDct = filters.Exists(f => f is "DCTDecode" or "DCT");
        bool hasJpx = filters.Exists(f => f is "JPXDecode");
        bool hasCcitt = filters.Exists(f => f is "CCITTFaxDecode" or "CCF");
        bool hasJbig2 = filters.Exists(f => f is "JBIG2Decode");

        if (hasJbig2)
        {
            // Page-information + generic regions (MMR and arithmetic, templates 0-3 with default or custom
            // GBAT and typical prediction) are decoded and validated against MuPDF. Symbol dictionaries / text
            // / halftone / refinement regions and XOR/XNOR composition are reported and degraded (null) so
            // callers skip the image rather than render a partial/incorrect bitmap.
            byte[] pageData = stream.Decode(diagnostics);
            byte[]? globals = GetJbig2Globals(document, dictionary, diagnostics);
            byte[] jbig2Data = globals is null
                ? pageData
                : Combine(globals, pageData);

            Jbig2Page? page = Jbig2Decoder.Decode(jbig2Data);
            if (page is null)
            {
                diagnostics?.Report(PdfUnsupportedKind.ImageCodec, "Unsupported JBIG2 features (symbol dictionaries / text / halftone / refinement regions are not decoded)");
                return null;
            }

            return CreateBilevelImage(factory, page.Value, dictionary, stencilColor);
        }

        if (hasCcitt)
        {
            IImage? ccitt = DecodeCcittImage(factory, stream, dictionary, width, height, stencilColor, diagnostics);
            if (ccitt is null)
            {
                diagnostics?.Report(PdfUnsupportedKind.ImageCodec, "CCITTFaxDecode could not be decoded");
            }

            return ccitt;
        }

        bool imageMask = dictionary.GetBooleanValue("ImageMask") ?? false;

        if (hasJpx)
        {
            // The PDF /ColorSpace, when present, names the image's colour; a JPX codestream may carry extra
            // components beyond it (issue19326: a 16-bit gray image with two components). Without an
            // explicit /ColorSpace the codestream's own space wins (ResolveColorSpace would default to
            // DeviceGray, which would turn an RGB JPX into a one-channel image).
            PdfObject? jpxSpaceObject = dictionary.Get("ColorSpace");
            int spaceComponents = jpxSpaceObject is not null
                ? ResolveColorSpace(document, jpxSpaceObject)?.Components ?? 0
                : 0;
            IImage? jpx = DecodeJpx(document, factory, stream, dictionary, width, height, spaceComponents, diagnostics);
            if (jpx is null)
            {
                diagnostics?.Report(PdfUnsupportedKind.ImageCodec, "JPXDecode image could not be decoded");
            }

            return jpx is null ? null : ApplyMasks(document, factory, dictionary, jpx, diagnostics);

        }

        if (hasDct)
        {
            // PdfStream.Decode stops at the codec header and returns the encoded JPEG bytes.
            byte[] encoded = stream.Decode(diagnostics);

            // Every return below hands back pixels the codec produced without ever seeing the dictionary.
            IImage? Masked(IImage? raster) => raster is null
                ? null
                : ApplyMasks(document, factory, dictionary, raster, diagnostics);

            // CMYK/YCCK (4-component) JPEGs are decoded here rather than delegated to the backend:
            // native codecs (Skia's SKCodec) omit the Adobe CMYK inversion and produce visibly wrong
            // colours, and SkiaSharp exposes no CMYK output to recover the raw samples from. Backends
            // that declare HandlesAdobeCmykJpeg get the first crack instead (faster, progressive-capable),
            // with the managed decoder kept as the fallback when the backend cannot produce pixels.
            // 1/3-component JPEGs are unaffected and stay on the backend path.
            bool cmykJpeg = JpegDecoder.PeekComponentCount(encoded) == 4;
            // A backend that decodes CMYK JPEGs itself never sees the PDF /Decode array, so it can only
            // be used when that array is the identity.
            IImage? backendCmyk = cmykJpeg && factory.HandlesAdobeCmykJpeg && IsIdentityDecode(dictionary)
                ? DecodeViaBackend(factory, encoded)
                : null;

            if (cmykJpeg && backendCmyk is null)
            {
                JpegDecoder.Result? jpeg = JpegDecoder.Decode(encoded);
                byte[]? cmyk = jpeg is null ? null : JpegColorConverter.ToCmyk(jpeg);
                if (cmyk is not null)
                {
                    MapCmykDecode(dictionary, cmyk);
                    var cmykPixels = new byte[cmyk.Length];
                    JpegColorConverter.CmykToBgra32(cmyk, cmykPixels);
                    return Masked(factory.CreateImage(jpeg!.Width, jpeg.Height, PixelFormat.Bgra8888Premul, cmykPixels));
                }
                // Fall through to the backend if the self-decode could not complete.
            }

            if (backendCmyk is not null)
            {
                return Masked(backendCmyk);
            }

            IImage? raster = factory.DecodeImage(encoded);
            if (raster is null)
            {
                // The backend has no decoder for this JPEG - PeachDrawing rejects SOF1 extended
                // sequential outright, which blanked a whole book cover - so fall back to the managed
                // decoder, which handles baseline and extended sequential.
                JpegDecoder.Result? managed = JpegDecoder.Decode(encoded);
                byte[]? managedPixels = managed is null ? null : JpegColorConverter.ToBgra32(managed);
                if (managedPixels is not null)
                {
                    return Masked(factory.CreateImage(managed!.Width, managed.Height, PixelFormat.Bgra8888Premul, managedPixels));
                }

                diagnostics?.Report(
                    PdfUnsupportedKind.ImageCodec,
                    $"The backend could not decode {string.Join(",", filters)}");
                return null;
            }

            using (raster)
            {
                return Masked(CopyBackendRaster(factory, raster));
            }
        }

        byte[] data = stream.Decode(diagnostics);

        // Color space resolution (skipped for image masks)
        ColorSpaceInfo? colorSpace = imageMask ? null : ResolveColorSpace(document, dictionary.Get("ColorSpace"));
        if (!imageMask && colorSpace is null)
        {
            // Unsupported color space: fall back to gray
            colorSpace = new ColorSpaceInfo(ColorSpaceKind.DeviceGray, 1, Array.Empty<byte>(), 0);
        }

        int components = colorSpace?.Components ?? 1;

        int bitsPerComponent = Math.Max(1, (int)(dictionary.GetIntegerValue("BitsPerComponent") ?? (imageMask ? 1 : 8)));
        int maxSample = (1 << bitsPerComponent) - 1;

        // /Decode array (defaults to [0 1] per component)
        float[] decodeMin = new float[components];
        float[] decodeMax = new float[components];
        PdfArray? decodeArray = dictionary.GetArray("Decode");
        for (int c = 0; c < components; c++)
        {
            decodeMin[c] = decodeArray is not null && decodeArray.Count > c * 2
                ? decodeArray.GetNumber(c * 2)?.AsFloat() ?? 0f
                : 0f;
            decodeMax[c] = decodeArray is not null && decodeArray.Count > (c * 2) + 1
                ? decodeArray.GetNumber((c * 2) + 1)?.AsFloat() ?? 1f
                : 1f;
        }

        // A present /Decode whose bounds exceed [0,1] is expressed in palette-index units (e.g. [255 0]
        // reverses an Indexed image), so the decoded value is the index itself; the default [0,1] must be
        // scaled back up by the sample range. pdf.js issue10339_reduced.pdf draws the same palette image
        // twice, once with Decode[255 0], and treating its decoded 0..255 value as a 0..1 fraction sent the
        // lookup far out of range and swapped the two halves' tones.
        bool indexedDecodeIsIndexUnits = decodeArray is not null &&
            (decodeMin[0] > 1f || decodeMax[0] > 1f);

        // Soft mask (alpha)
        byte[]? alpha = null;
        if (document.Resolve(dictionary.Get("SMask")) is PdfStream softMaskStream)
        {
            PdfDictionary softMaskDictionary = softMaskStream.Dictionary;
            alpha = DecodeAlphaChannel(document, factory, softMaskStream, softMaskDictionary, width, height, diagnostics);
        }

        // Hard mask (stencil stream) or color-key mask (array)
        byte[]? stencil = null;
        byte[]? fineStencil = null;
        int fineStencilWidth = 0, fineStencilHeight = 0;
        float[]? colorKeyMin = null;
        float[]? colorKeyMax = null;

        switch (document.Resolve(dictionary.Get("Mask")))
        {
            case PdfStream maskStream:
                if (maskStream.Dictionary.GetNameValue("Subtype") != "Image" &&
                    maskStream.Dictionary.GetBooleanValue("ImageMask") != true)
                {
                    break; // not an image mask
                }

                int maskWidth = (int)(maskStream.Dictionary.GetIntegerValue("Width") ?? width);
                int maskHeight = (int)(maskStream.Dictionary.GetIntegerValue("Height") ?? height);
                if (maskWidth > 0 && maskHeight > 0 && (maskWidth > width || maskHeight > height))
                {
                    // A mask finer than the image keeps its own resolution: upscale the raster to it and
                    // apply the stencil 1:1 (both references do). Collapsing pdf.js issue4246.pdf's
                    // 1000x800 stencil onto its 50x40 image first loses the mask's shape entirely.
                    fineStencil = DecodeStencil(document, factory, maskStream, maskStream.Dictionary, maskWidth, maskHeight, diagnostics);
                    fineStencilWidth = maskWidth;
                    fineStencilHeight = maskHeight;
                }
                else
                {
                    stencil = MaskCoverage(document, factory, maskStream, width, height, diagnostics);
                }

                break;

            case PdfArray maskArray when maskArray.Count >= 2:
                colorKeyMin = new float[maskArray.Count / 2];
                colorKeyMax = new float[maskArray.Count / 2];
                for (int i = 0; i + 1 < maskArray.Count; i += 2)
                {
                    colorKeyMin[i / 2] = maskArray.GetNumber(i)?.AsFloat() ?? 0f;
                    colorKeyMax[i / 2] = maskArray.GetNumber(i + 1)?.AsFloat() ?? 0f;
                }

                break;
        }

        int rowBits = width * components * bitsPerComponent;
        int rowBytes = (rowBits + 7) / 8;
        var output = new byte[width * height * 4];

        Span<float> sampleValues = stackalloc float[components];

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * rowBytes;
            int bitOffset = 0;

            for (int x = 0; x < width; x++)
            {
                int pixelIndex = (y * width) + x;
                byte alphaByte = 255;
                byte r, g, b;

                if (imageMask)
                {
                    // 1-bit stencil: sample 0 paints, 1 is transparent (unless /Decode inverts)
                    int bit = ReadBits(data, rowStart, ref bitOffset, 1);
                    bool inverted = decodeArray is not null &&
                                    decodeArray.Count >= 2 &&
                                    (decodeArray.GetNumber(0)?.AsFloat() ?? 0f) == 1f;
                    bool paint = inverted ? bit == 1 : bit == 0;
                    r = stencilColor.R;
                    g = stencilColor.G;
                    b = stencilColor.B;
                    alphaByte = paint ? stencilColor.A : (byte)0;
                }
                else
                {
                    for (int c = 0; c < components; c++)
                    {
                        int sample = ReadBits(data, rowStart, ref bitOffset, bitsPerComponent);
                        float decoded = maxSample == 0
                            ? 0f
                            : decodeMin[c] + ((float)sample / maxSample * (decodeMax[c] - decodeMin[c]));
                        sampleValues[c] = decoded;
                    }

                    ColorSpaceInfo space = colorSpace!;
                    if (space.Kind is ColorSpaceKind.Separation or ColorSpaceKind.DeviceN)
                    {
                        (r, g, b) = TintedToRgb(space, sampleValues);
                    }
                    else if (space.Icc is { } icc)
                    {
                        icc.ToRgb(sampleValues, out r, out g, out b);
                    }
                    else
                    {
                        (r, g, b) = space.Kind switch
                        {
                            ColorSpaceKind.DeviceGray => GrayToRgb(sampleValues[0]),
                            ColorSpaceKind.DeviceRgb => (ToByte(sampleValues[0]), ToByte(sampleValues[1]), ToByte(sampleValues[2])),
                            ColorSpaceKind.DeviceCmyk => CmykToRgb(sampleValues[0], sampleValues[1], sampleValues[2], sampleValues[3]),
                            ColorSpaceKind.Indexed => IndexedToRgb(space, indexedDecodeIsIndexUnits ? (int)MathF.Round(sampleValues[0]) : (int)(sampleValues[0] * maxSample)),
                            _ => GrayToRgb(sampleValues[0]),
                        };
                    }

                    if (colorKeyMin is not null && colorKeyMax is not null)
                    {
                        bool masked = true;
                        for (int c = 0; c < components && c < colorKeyMin.Length; c++)
                        {
                            float keyValue = sampleValues[c] * maxSample;
                            if (keyValue < colorKeyMin[c] || keyValue > colorKeyMax[c])
                            {
                                masked = false;
                                break;
                            }
                        }

                        if (masked)
                        {
                            alphaByte = 0;
                        }
                    }
                }

                if (stencil is not null)
                {
                    int stencilIndex = (y * width) + x;
                    if (stencilIndex < stencil.Length)
                    {
                        // The mask may have been scaled onto this grid, so it carries partial coverage
                        // rather than only 0/255.
                        alphaByte = (byte)((alphaByte * stencil[stencilIndex] + 127) / 255);
                    }
                }

                if (alpha is not null)
                {
                    int alphaIndex = (y * width) + x;
                    if (alphaIndex < alpha.Length)
                    {
                        alphaByte = alpha[alphaIndex];
                    }
                }

                int offset = pixelIndex * 4;
                output[offset] = Premultiply(b, alphaByte);
                output[offset + 1] = Premultiply(g, alphaByte);
                output[offset + 2] = Premultiply(r, alphaByte);
                output[offset + 3] = alphaByte;
            }
        }

        if (fineStencil is not null)
        {
            // Nearest-upscale the premultiplied raster to the mask grid and fold the stencil in 1:1.
            var merged = new byte[fineStencilWidth * fineStencilHeight * 4];
            for (int y = 0; y < fineStencilHeight; y++)
            {
                int sy = Math.Min((int)((long)y * height / fineStencilHeight), height - 1);
                for (int x = 0; x < fineStencilWidth; x++)
                {
                    int sx = Math.Min((int)((long)x * width / fineStencilWidth), width - 1);
                    int src = ((sy * width) + sx) * 4;
                    byte coverage = fineStencil[(y * fineStencilWidth) + x];
                    int dst = ((y * fineStencilWidth) + x) * 4;
                    merged[dst] = Premultiply(output[src], coverage);
                    merged[dst + 1] = Premultiply(output[src + 1], coverage);
                    merged[dst + 2] = Premultiply(output[src + 2], coverage);
                    merged[dst + 3] = Premultiply(output[src + 3], coverage);
                }
            }

            return factory.CreateImage(fineStencilWidth, fineStencilHeight, PixelFormat.Bgra8888Premul, merged);
        }

        return factory.CreateImage(width, height, PixelFormat.Bgra8888Premul, output);
    }

    /// <summary>Decodes an inline image (BI/ID/EI) using its dictionary and raw data.</summary>
    public static IImage? DecodeInline(
        PdfDocument document,
        IGraphicsFactory factory,
        PdfDictionary dictionary,
        byte[] rawData,
        PosoColor stencilColor,
        PdfDiagnostics? diagnostics = null)
    {
        var stream = new PdfStream(dictionary, rawData);
        return Decode(document, factory, stream, dictionary, stencilColor, diagnostics);
    }

    /// <summary>Decodes a JBIG2 bilevel image XObject into a BGRA image.</summary>
    private static IImage CreateBilevelImage(IGraphicsFactory factory, Jbig2Page page, PdfDictionary dictionary, PosoColor stencilColor)
    {
        int w = page.Width;
        int h = page.Height;
        var pixels = new byte[w * h * 4];
        bool mask = dictionary.GetBooleanValue("ImageMask") ?? false;
        bool paintWhenSet = MaskPaintsSetBit(dictionary);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int offset = ((y * w) + x) * 4;
                if (mask)
                {
                    byte ink = page.IsBlack(x, y) == paintWhenSet ? stencilColor.A : (byte)0;
                    pixels[offset] = Premultiply(stencilColor.B, ink);
                    pixels[offset + 1] = Premultiply(stencilColor.G, ink);
                    pixels[offset + 2] = Premultiply(stencilColor.R, ink);
                    pixels[offset + 3] = ink;
                    continue;
                }

                byte value = page.IsBlack(x, y) ? (byte)0 : (byte)255;
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
                pixels[offset + 3] = 255;
            }
        }

        return factory.CreateImage(w, h, PixelFormat.Bgra8888Premul, pixels);
    }

    /// <summary>
    /// Whether a 1-bit stencil mask paints where the filter's decoded bit is set. The reference renderer
    /// paints the set bit whichever way a filter's own black/white flag reads (the flag's inversion and the
    /// mask sample inversion cancel out - checked with controlled Group 4 codestreams both ways), and
    /// /Decode [1 0] swaps the two values again.
    /// </summary>
    private static bool MaskPaintsSetBit(PdfDictionary dictionary) =>
        (dictionary.GetArray("Decode")?.GetNumber(0)?.AsFloat() ?? 0f) != 1f;

    private static IImage? DecodeCcittImage(IGraphicsFactory factory, PdfStream stream, PdfDictionary dictionary, int width, int height, PosoColor stencilColor, PdfDiagnostics? diagnostics)
    {
        PdfDictionary? parms = GetCcittParms(dictionary);
        int columns = (int)(parms?.GetIntegerValue("Columns") ?? width);
        int rows = (int)(parms?.GetIntegerValue("Rows") ?? height);
        int k = (int)(parms?.GetIntegerValue("K") ?? 0);
        bool byteAlign = parms?.GetBooleanValue("EncodedByteAlign") ?? false;
        bool blackIs1 = parms?.GetBooleanValue("BlackIs1") ?? false;
        bool endOfBlock = parms?.GetBooleanValue("EndOfBlock") ?? true;
        bool endOfLine = parms?.GetBooleanValue("EndOfLine") ?? false;
        if (columns <= 0 || rows <= 0)
        {
            return null;
        }

        byte[] codestream = stream.Decode(diagnostics);
        byte[] bits = CcittFaxDecoder.Decode(codestream, columns, rows, k, byteAlign, blackIs1, endOfBlock, endOfLine);
        int bytesPerRow = (columns + 7) / 8;
        var pixels = new byte[columns * rows * 4];
        bool mask = dictionary.GetBooleanValue("ImageMask") ?? false;
        // For a stencil mask the ink is the filter's set bit; see MaskPaintsSetBit for how that is decided.
        bool paintWhenSet = !mask || MaskPaintsSetBit(dictionary);
        byte maskR = stencilColor.R, maskG = stencilColor.G, maskB = stencilColor.B, maskA = stencilColor.A;

        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                int bit = (bits[(y * bytesPerRow) + (x / 8)] >> (7 - (x % 8))) & 1;
                int offset = ((y * columns) + x) * 4;
                if (mask)
                {
                    byte ink = (bit == 1) == paintWhenSet ? maskA : (byte)0;
                    pixels[offset] = Premultiply(maskB, ink);
                    pixels[offset + 1] = Premultiply(maskG, ink);
                    pixels[offset + 2] = Premultiply(maskR, ink);
                    pixels[offset + 3] = ink;
                    continue;
                }

                // CcittFaxDecoder has already folded /BlackIs1 into its output, so a set bit means black for
                // either flag value (its true-flag convention is pinned by the decoder's MuPDF bit tests, and a
                // real default-flag scan - issue5747.pdf - needs the same). Re-deriving black from the flag here
                // inverted every non-mask CCITT grayscale image whose /BlackIs1 is the default false: a scanned
                // page painted as its own negative (91% ink against MuPDF's 18%).
                bool isBlack = bit == 1;
                byte value = isBlack ? (byte)0 : (byte)255;
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
                pixels[offset + 3] = 255;
            }
        }

        return factory.CreateImage(columns, rows, PixelFormat.Bgra8888Premul, pixels);
    }

    private static PdfDictionary? GetCcittParms(PdfDictionary dictionary) => dictionary.Get("DecodeParms") switch
    {
        PdfDictionary d => d,
        PdfArray a => a.OfType<PdfDictionary>().FirstOrDefault(),
        _ => null,
    };

    // Resolves the /JBIG2Globals shared-data stream (if any) from the image's DecodeParms and returns its
    // decoded bytes; JBIG2 page streams reference global segments (page-info, symbol dictionaries) out-of-band.
    private static byte[]? GetJbig2Globals(PdfDocument document, PdfDictionary dictionary, PdfDiagnostics? diagnostics)
    {
        PdfObject? parms = document.Resolve(dictionary.Get("DecodeParms")) ?? document.Resolve(dictionary.Get("DP"));
        PdfDictionary? candidate = parms switch
        {
            PdfDictionary d when d.Get("JBIG2Globals") is not null => d,
            PdfArray a => a.OfType<PdfDictionary>().FirstOrDefault(x => x.Get("JBIG2Globals") is not null),
            _ => null,
        };

        if (candidate is null)
        {
            return null;
        }

        return document.Resolve(candidate.Get("JBIG2Globals")) is PdfStream globalsStream
            ? globalsStream.Decode(diagnostics)
            : null;
    }

    // Runs a capable back-end's own decode for a CMYK JPEG and re-wraps the raster into the
    // pipeline's standard Bgra8888Premul image; null when the back-end cannot decode it.
    /// <summary>
    /// Decodes a JPEG 2000 (JPXDecode) image with the managed CoreJ2K codec. Neither graphics back end
    /// can: SkiaSharp's native build omits OpenJPEG and PeachDrawing has no codec at all, so without this
    /// a page whose only content is a JPX image - a book cover, typically - renders blank. CoreJ2K has
    /// already applied the codestream's own palette and colour transform by the time it hands back
    /// components, so what is left is gray or RGB samples.
    /// </summary>
    private static IImage? DecodeJpx(
        PdfDocument document,
        IGraphicsFactory factory,
        PdfStream stream,
        PdfDictionary dictionary,
        int width,
        int height,
        int spaceComponents,
        PdfDiagnostics? diagnostics)
    {
        if (dictionary.GetBooleanValue("ImageMask") ?? false)
        {
            diagnostics?.Report(PdfUnsupportedKind.ImageCodec, "JPXDecode used as an image mask is not supported");
            return null;
        }

        byte[] encoded = stream.Decode(diagnostics);

        CoreJ2K.Util.InterleavedImage image;
        try
        {
            image = CoreJ2K.J2kImage.FromStream(new MemoryStream(encoded));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // A damaged or unusual codestream costs its image, never the page around it.
            diagnostics?.Report(PdfUnsupportedKind.ImageCodec, $"JPXDecode failed: {ex.Message}");
            return null;
        }

        using (image)
        {
            int components = image.NumberOfComponents;

            // /SMaskInData (PDF 32000-1:2008 11.4.4) is the only explicit statement that the image carries
            // alpha in its own channels; without it, four components are CMYK, which is reported rather
            // than guessed at - rendering CMYK samples as RGB is silently wrong. A value of 2 adds that
            // the colour samples are premultiplied by that alpha, which a surface that multiplies again
            // would darken twice.
            int smaskInData = (int)(dictionary.GetIntegerValue("SMaskInData") ?? 0);
            bool hasAlpha = smaskInData >= 1;
            bool premultiplied = smaskInData == 2;
            int colorChannels = hasAlpha ? components - 1 : components;

            // The colour space in the PDF dictionary decides how many components are colour. When the
            // codestream carries more (a two-component gray image, pdf.js issue19326.pdf), only the leading
            // ones are used and the rest are dropped - MuPDF and PDFium read the dictionary's space first.
            if (!hasAlpha && components > spaceComponents && spaceComponents is 1 or 3)
            {
                colorChannels = spaceComponents;
            }

            if (colorChannels is not (1 or 3) || components > 4)
            {
                diagnostics?.Report(
                    PdfUnsupportedKind.ImageCodec,
                    $"JPXDecode with {components} components (alpha: {hasAlpha}) is not supported");
                return null;
            }

            int[] red = image.GetComponent(0);
            int[] green = colorChannels == 3 ? image.GetComponent(1) : red;
            int[] blue = colorChannels == 3 ? image.GetComponent(2) : red;
            int[] alpha = hasAlpha ? image.GetComponent(components - 1) : Array.Empty<int>();
            int redDepth = image.GetBitDepth(0);
            int greenDepth = colorChannels == 3 ? image.GetBitDepth(1) : redDepth;
            int blueDepth = colorChannels == 3 ? image.GetBitDepth(2) : redDepth;
            int alphaDepth = alpha.Length > 0 ? image.GetBitDepth(components - 1) : 8;
            byte[] matte = premultiplied ? MatteSamples(document, dictionary, colorChannels) : [];

            var pixels = new byte[width * height * 4];
            int samples = Math.Min(red.Length, green.Length);
            samples = Math.Min(samples, blue.Length);
            for (int i = 0; i < samples; i++)
            {
                byte r = ToByte(red[i], redDepth);
                byte g = ToByte(green[i], greenDepth);
                byte b = ToByte(blue[i], blueDepth);
                byte a = i < alpha.Length ? ToByte(alpha[i], alphaDepth) : (byte)0xFF;

                if (premultiplied)
                {
                    (r, g, b) = UndoPreblend(r, g, b, a, matte);
                }

                // BGRA, premultiplied: the surfaces are opaque unless the codestream said otherwise.
                int offset = i * 4;
                pixels[offset] = (byte)(b * a / 255);
                pixels[offset + 1] = (byte)(g * a / 255);
                pixels[offset + 2] = (byte)(r * a / 255);
                pixels[offset + 3] = a;
            }

            return factory.CreateImage(width, height, PixelFormat.Bgra8888Premul, pixels);
        }
    }

    /// <summary>Rebases a decoded sample of <paramref name="bitDepth"/> bits to a byte.</summary>
    private static byte ToByte(int sample, int bitDepth)
    {
        if (bitDepth >= 8)
        {
            return (byte)Math.Clamp(sample >> (bitDepth - 8), 0, 255);
        }

        // Scale the whole range up, so 1-bit becomes 0 or 255 and 4-bit becomes 0..255 in steps of 17.
        int max = (1 << bitDepth) - 1;
        return (byte)(((sample * 255) + (max / 2)) / max);
    }

    /// <summary>
    /// Reverses the premultiplication a /SMaskInData 2 codestream stores, un-compositing the /Matte colour
    /// as it goes: C = (C_stored - M) / alpha + M is 8.9.5.4's matte arithmetic, and pdf.js applies exactly
    /// that before it hands the samples on. A sample the mask makes invisible carries no colour, and a
    /// premultiplied surface cannot represent one, so it stays black.
    /// </summary>
    private static (byte R, byte G, byte B) UndoPreblend(byte r, byte g, byte b, byte a, byte[] matte)
    {
        if (a == 0)
        {
            return (0, 0, 0);
        }

        float k = 255f / a;
        return (
            Clamped(((r - matte[0]) * k) + matte[0]),
            Clamped(((g - matte[1]) * k) + matte[1]),
            Clamped(((b - matte[2]) * k) + matte[2]));
    }

    /// <summary>
    /// /Matte as 0..255 samples in the image's own colour space, black when the file carries none. One
    /// component is a gray image's matte; a matte that does not describe this image's colours is ignored
    /// rather than guessed at.
    /// </summary>
    private static byte[] MatteSamples(PdfDocument document, PdfDictionary dictionary, int colorChannels)
    {
        if (document.Resolve(dictionary.Get("Matte")) is not PdfArray matte)
        {
            return [0, 0, 0];
        }

        if (colorChannels == 1 && matte.Count >= 1)
        {
            byte gray = ToByte(document.GetArrayNumber(matte, 0)?.AsFloat() ?? 0f);
            return [gray, gray, gray];
        }

        if (colorChannels == 3 && matte.Count >= 3)
        {
            return
            [
                ToByte(document.GetArrayNumber(matte, 0)?.AsFloat() ?? 0f),
                ToByte(document.GetArrayNumber(matte, 1)?.AsFloat() ?? 0f),
                ToByte(document.GetArrayNumber(matte, 2)?.AsFloat() ?? 0f),
            ];
        }

        return [0, 0, 0];
    }

    private static byte Clamped(float value) => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);

    /// <summary>True when the image has no /Decode, or one that maps every component onto itself.</summary>
    private static bool IsIdentityDecode(PdfDictionary dictionary)
    {
        PdfArray? decode = dictionary.GetArray("Decode");
        if (decode is null || decode.Count == 0)
        {
            return true;
        }

        for (int i = 0; i + 1 < decode.Count; i += 2)
        {
            if (decode.GetNumber(i)?.AsFloat() != 0f || decode.GetNumber(i + 1)?.AsFloat() != 1f)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Maps interleaved 8-bit CMYK samples through the image's /Decode array, in place. Producers of
    /// Adobe CMYK JPEGs write [1 0 1 0 1 0 1 0] because the file stores the complements of the
    /// colourants; without this the cover of a book came out sepia where every viewer shows blue-grey.
    /// </summary>
    private static void MapCmykDecode(PdfDictionary dictionary, byte[] cmyk)
    {
        PdfArray? decode = dictionary.GetArray("Decode");
        if (decode is null || decode.Count < 8)
        {
            return;
        }

        bool identity = true;
        for (int c = 0; c < 4; c++)
        {
            if (decode.GetNumber(c * 2)?.AsFloat() != 0f || decode.GetNumber((c * 2) + 1)?.AsFloat() != 1f)
            {
                identity = false;
            }
        }

        if (identity)
        {
            return;
        }

        Span<float> min = stackalloc float[4];
        Span<float> max = stackalloc float[4];
        for (int c = 0; c < 4; c++)
        {
            min[c] = decode.GetNumber(c * 2)?.AsFloat() ?? 0f;
            max[c] = decode.GetNumber((c * 2) + 1)?.AsFloat() ?? 1f;
        }

        for (int i = 0; i + 3 < cmyk.Length; i += 4)
        {
            for (int c = 0; c < 4; c++)
            {
                float mapped = min[c] + (cmyk[i + c] / 255f * (max[c] - min[c]));
                cmyk[i + c] = (byte)Math.Clamp((int)MathF.Round(Math.Clamp(mapped, 0f, 1f) * 255f), 0, 255);
            }
        }
    }

    private static IImage? DecodeViaBackend(IGraphicsFactory factory, byte[] encoded)
    {
        IImage? raster = factory.DecodeImage(encoded);
        if (raster is null)
        {
            return null;
        }

        using (raster)
        {
            return CopyBackendRaster(factory, raster);
        }
    }

    private static IImage CopyBackendRaster(IGraphicsFactory factory, IImage raster)
    {
        byte[] pixels = new byte[raster.Width * raster.Height * raster.BytesPerPixel];
        raster.CopyPixels(pixels);
        return factory.CreateImage(raster.Width, raster.Height, PixelFormat.Bgra8888Premul, pixels);
    }

    private static byte[] Combine(byte[] first, byte[] second)
    {
        byte[] result = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, result, 0, first.Length);
        Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
        return result;
    }

    private static byte Premultiply(byte channel, byte alpha) =>
        (byte)((channel * alpha + 127) / 255);

    private static byte ToByte(float value) =>
        (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);

    private static (byte R, byte G, byte B) GrayToRgb(float gray)
    {
        byte value = ToByte(gray);
        return (value, value, value);
    }

    /// <summary>
    /// Runs a Separation/DeviceN image's colorant samples through the space's tint transform and converts
    /// the base space that comes out. A space with no tint transform falls back to the first tint as a
    /// grey density, which is what the separation of a single black plate means anyway.
    /// </summary>
    private static (byte R, byte G, byte B) TintedToRgb(ColorSpaceInfo space, ReadOnlySpan<float> tints)
    {
        Span<float> baseValues = stackalloc float[Math.Clamp(Math.Max(space.BaseComponents, space.Tint?.OutputCount ?? 0), 1, 4)];
        if (space.Tint is { } tint)
        {
            tint.Evaluate(tints, baseValues);
        }
        else
        {
            for (int i = 0; i < baseValues.Length && i < tints.Length; i++)
            {
                baseValues[i] = tints[i];
            }
        }

        return space.BaseKind switch
        {
            ColorSpaceKind.DeviceCmyk => CmykToRgb(baseValues[0], baseValues[1], baseValues[2], baseValues[3]),
            ColorSpaceKind.DeviceGray => GrayToRgb(baseValues[0]),
            _ => (ToByte(baseValues[0]), ToByte(baseValues[1]), ToByte(baseValues[2])),
        };
    }

    /// <summary>
    /// Runs a raw CMYK sample through the shared DeviceCMYK conversion. The one-minus-black shortcut is
    /// not that: MuPDF converts through a coated-paper profile, which is several times brighter in the
    /// dark tones a photograph is mostly made of, so the shortcut left every CMYK photo underexposed.
    /// </summary>
    private static (byte R, byte G, byte B) CmykToRgb(float c, float m, float y, float k)
    {
        PosoColor rgb = PosoColor.FromCmyk(c, m, y, k);
        return (rgb.R, rgb.G, rgb.B);
    }

    /// <summary>
    /// Reads an Indexed colour space's colour table. PDF 32000-1:2008 8.7.2 allows it as a string or as a
    /// stream, and generators switch to the stream form once the table is large; reading only the string
    /// left the table empty, which paints every index - including the white background - black.
    /// </summary>
    internal static byte[] ReadIndexedPalette(PdfObject? paletteObject) => paletteObject switch
    {
        PdfString text => text.Bytes,
        PdfStream stream => stream.Decode(),
        _ => Array.Empty<byte>(),
    };

    private static float[]? ReadLabWhite(PdfDictionary? lab)
    {
        if (lab?.GetArray("WhitePoint") is { Count: >= 3 } white)
        {
            return new[]
            {
                white.GetNumber(0)?.AsFloat() ?? 0.9505f,
                white.GetNumber(1)?.AsFloat() ?? 1f,
                white.GetNumber(2)?.AsFloat() ?? 1.089f,
            };
        }

        return null;
    }

    private static float[]? ReadLabRange(PdfDictionary? lab)
    {
        if (lab?.GetArray("Range") is { Count: >= 4 } range)
        {
            return new[]
            {
                range.GetNumber(0)?.AsFloat() ?? -128f,
                range.GetNumber(1)?.AsFloat() ?? 127f,
                range.GetNumber(2)?.AsFloat() ?? -128f,
                range.GetNumber(3)?.AsFloat() ?? 127f,
            };
        }

        return null;
    }

    /// <summary>
    /// Converts one Indexed palette entry over a Lab base: the three bytes are L* (0..100) and a*/b* over
    /// the /Range. Treating them as RGB (the generic three-component path) turned pdf.js
    /// issue10339_reduced.pdf's blue Lab gradient into brown - every channel read from the wrong component.
    /// </summary>
    private static (byte R, byte G, byte B) LabEntryToRgb(byte[] lookup, int offset, ColorSpaceInfo lab)
    {
        float l = lookup[offset] / 255f * 100f;
        float[] range = lab.LabRange ?? new[] { -128f, 127f, -128f, 127f };
        float a = range[0] + (lookup[offset + 1] / 255f * (range[1] - range[0]));
        float b = range[2] + (lookup[offset + 2] / 255f * (range[3] - range[2]));
        PosoColor colour = Poso.Pdf.Colors.Cielab.ToRgb(l, a, b, lab.LabWhite ?? Array.Empty<float>());
        return (colour.R, colour.G, colour.B);
    }

    private static (byte R, byte G, byte B) IndexedToRgb(ColorSpaceInfo space, int index)
    {
        byte[] lookup = space.Lookup;
        int baseComponents = space.BaseComponents;
        if (lookup.Length < baseComponents)
        {
            // Truncated or missing palette: render the sample as black instead of crashing
            return (0, 0, 0);
        }

        int maxIndex = (lookup.Length / baseComponents) - 1;
        int clamped = Math.Clamp(index, 0, Math.Max(maxIndex, 0));
        int offset = clamped * baseComponents;

        // A palette over a Lab base holds L*, a*, b* triplets rather than RGB.
        if (space.Base is { Kind: ColorSpaceKind.Lab } labBase)
        {
            return LabEntryToRgb(lookup, offset, labBase);
        }

        // A palette over a Separation or DeviceN holds one tint per colorant, and the entry is only a
        // colour once the space's tint transform has run over it.
        if (space.Base is { Kind: ColorSpaceKind.Separation or ColorSpaceKind.DeviceN } tinted)
        {
            Span<float> tints = stackalloc float[Math.Clamp(baseComponents, 1, 8)];
            for (int i = 0; i < tints.Length; i++)
            {
                tints[i] = lookup[offset + i] / 255f;
            }

            return TintedToRgb(tinted, tints);
        }

        return baseComponents switch
        {
            1 => GrayToRgb(lookup[offset] / 255f),
            3 => (lookup[offset], lookup[offset + 1], lookup[offset + 2]),
            4 => CmykToRgb(lookup[offset] / 255f, lookup[offset + 1] / 255f, lookup[offset + 2] / 255f, lookup[offset + 3] / 255f),
            _ => (0, 0, 0),
        };
    }

    /// <summary>Reads a big-endian bit field of the given width from packed sample data.</summary>
    private static int ReadBits(byte[] data, int rowStart, ref int bitOffset, int bits)
    {
        int value = 0;
        for (int i = 0; i < bits; i++)
        {
            int byteIndex = rowStart + ((bitOffset + i) / 8);
            int bitIndex = 7 - ((bitOffset + i) % 8);
            int bit = byteIndex < data.Length ? ((data[byteIndex] >> bitIndex) & 1) : 0;
            value = (value << 1) | bit;
        }

        bitOffset += bits;
        return value;
    }

    /// <summary>
    /// Fold a soft or stencil mask into a raster the back end produced. The DCT and JPX branches hand the
    /// codec's own pixels straight back, and a codec never looks at the image dictionary, so a masked
    /// JPEG or JPEG 2000 painted its full rectangle: a JPX cover plate with a JBIG2 stencil over it
    /// buried the page underneath itself.
    /// </summary>
    private static IImage ApplyMasks(
        PdfDocument document,
        IGraphicsFactory factory,
        PdfDictionary dictionary,
        IImage raster,
        PdfDiagnostics? diagnostics)
    {
        byte[]? alpha = MaskCoverage(document, factory, dictionary.Get("SMask"), raster.Width, raster.Height, diagnostics);
        byte[]? stencil = MaskCoverage(document, factory, dictionary.Get("Mask"), raster.Width, raster.Height, diagnostics);
        if (alpha is null && stencil is null)
        {
            return raster;
        }

        var pixels = new byte[raster.Width * raster.Height * 4];
        raster.CopyPixels(pixels);
        for (int p = 0, i = 0; i < pixels.Length; i += 4, p++)
        {
            byte coverage = stencil is not null ? stencil[p] : (byte)255;
            if (alpha is not null)
            {
                coverage = (byte)((coverage * alpha[p] + 127) / 255);
            }

            if (coverage == 255)
            {
                continue;
            }

            // The raster arrives premultiplied, so every channel - alpha included - scales together.
            pixels[i] = (byte)((pixels[i] * coverage + 127) / 255);
            pixels[i + 1] = (byte)((pixels[i + 1] * coverage + 127) / 255);
            pixels[i + 2] = (byte)((pixels[i + 2] * coverage + 127) / 255);
            pixels[i + 3] = (byte)((pixels[i + 3] * coverage + 127) / 255);
        }

        return factory.CreateImage(raster.Width, raster.Height, PixelFormat.Bgra8888Premul, pixels);
    }

    /// <summary>
    /// A mask's coverage at the image's own pixel grid, or null when the reference is not a mask stream.
    /// A mask may be a different resolution from the image it masks (the spec maps both onto the same
    /// unit square), so it is read at its own size and then sampled across.
    /// </summary>
    private static byte[]? MaskCoverage(
        PdfDocument document,
        IGraphicsFactory factory,
        PdfObject? reference,
        int width,
        int height,
        PdfDiagnostics? diagnostics)
    {
        if (document.Resolve(reference) is not PdfStream mask)
        {
            return null;
        }

        int maskWidth = (int)(mask.Dictionary.GetIntegerValue("Width") ?? width);
        int maskHeight = (int)(mask.Dictionary.GetIntegerValue("Height") ?? height);
        if (maskWidth <= 0 || maskHeight <= 0)
        {
            return null;
        }

        bool stencil = mask.Dictionary.GetBooleanValue("ImageMask") ?? false;
        byte[]? coverage = CodecEncodedMask(mask)
            ? DecodedImageCoverage(document, factory, mask, maskWidth, maskHeight)
            : stencil
                ? DecodeStencil(document, factory, mask, mask.Dictionary, maskWidth, maskHeight, diagnostics)
                : DecodeAlphaChannel(document, factory, mask, mask.Dictionary, maskWidth, maskHeight, diagnostics);
        if (coverage is null)
        {
            return null;
        }

        if (maskWidth == width && maskHeight == height)
        {
            return coverage;
        }

        // A mask of a different size is mapped onto the same unit square; a box average keeps the covered
        // fraction right. Nearest sampling aliases badly when the mask is much larger than the image: pdf.js
        // issue4246.pdf's 1000x800 stencil over 50x40 pixels hit 30% of the set bits where the true average
        // is 15%.
        var scaled = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            int sourceY0 = (int)((long)y * maskHeight / height);
            int sourceY1 = Math.Max(sourceY0 + 1, (int)((long)(y + 1) * maskHeight / height));
            sourceY1 = Math.Min(sourceY1, maskHeight);
            for (int x = 0; x < width; x++)
            {
                int sourceX0 = (int)((long)x * maskWidth / width);
                int sourceX1 = Math.Max(sourceX0 + 1, (int)((long)(x + 1) * maskWidth / width));
                sourceX1 = Math.Min(sourceX1, maskWidth);
                int sum = 0, count = 0;
                for (int sy = sourceY0; sy < sourceY1; sy++)
                {
                    int row = sy * maskWidth;
                    for (int sx = sourceX0; sx < sourceX1; sx++)
                    {
                        sum += coverage[row + sx];
                        count++;
                    }
                }

                scaled[(y * width) + x] = count > 0 ? (byte)((sum + (count / 2)) / count) : (byte)0;
            }
        }

        return scaled;
    }

    /// <summary>
    /// True when a mask carries a filter whose bytes are not plain samples: the JPEG path hands back the
    /// codestream itself and JPX is only decoded by <see cref="Decode"/>, so reading either as grayscale
    /// samples produces noise for the coverage.
    /// </summary>
    private static bool CodecEncodedMask(PdfStream mask)
    {
        List<string> filters = GetFilters(mask.Dictionary);
        return filters.Exists(f => f is "DCTDecode" or "DCT" or "JPXDecode");
    }

    /// <summary>
    /// A mask's coverage read out of an image the full pipeline decoded. Every colour space lands in the
    /// same three channels, so the red one is the luminance whatever the mask was authored in.
    /// </summary>
    private static byte[]? DecodedImageCoverage(
        PdfDocument document, IGraphicsFactory factory, PdfStream mask, int width, int height)
    {
        using IImage? image = Decode(document, factory, mask, mask.Dictionary, PosoColor.Black);
        if (image is null)
        {
            return null;
        }

        var pixels = new byte[image.Width * image.Height * 4];
        image.CopyPixels(pixels);
        var coverage = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            int sourceY = Math.Min((int)((long)y * image.Height / height), image.Height - 1);
            for (int x = 0; x < width; x++)
            {
                int sourceX = Math.Min((int)((long)x * image.Width / width), image.Width - 1);
                coverage[(y * width) + x] = pixels[((sourceY * image.Width) + sourceX) * 4 + 2];
            }
        }

        return coverage;
    }

    private static byte[]? DecodeAlphaChannel(
        PdfDocument document,
        IGraphicsFactory factory,
        PdfStream stream,
        PdfDictionary dictionary,
        int width,
        int height,
        PdfDiagnostics? diagnostics)
    {
        // Soft masks are grayscale images (possibly with their own filters)
        byte[] data = stream.Decode(diagnostics);
        int bitsPerComponent = Math.Max(1, (int)(dictionary.GetIntegerValue("BitsPerComponent") ?? 8));
        int maxSample = (1 << bitsPerComponent) - 1;
        int rowBytes = (width * bitsPerComponent + 7) / 8;
        var alpha = new byte[width * height];

        // A soft mask can invert its samples with /Decode [1 0] - a common way to encode an already
        // complemented coverage channel. Ignoring the array read raw samples as alpha and flipped the mask:
        // pdf.js issue1905.pdf's DCT/Flate images carry Decode[1 0] masks, and once the form-alpha leak was
        // fixed their visible chart bars turned transparent (white) instead of opaque.
        float decodeMin = 0f;
        float decodeMax = 1f;
        if (dictionary.GetArray("Decode") is { Count: >= 2 } decode)
        {
            decodeMin = decode.GetNumber(0)?.AsFloat() ?? 0f;
            decodeMax = decode.GetNumber(1)?.AsFloat() ?? 1f;
        }

        for (int y = 0; y < height; y++)
        {
            int bitOffset = 0;
            for (int x = 0; x < width; x++)
            {
                int sample = ReadBits(data, y * rowBytes, ref bitOffset, bitsPerComponent);
                float value = decodeMin + ((float)sample / maxSample * (decodeMax - decodeMin));
                alpha[(y * width) + x] = (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
            }
        }

        return alpha;
    }

    private static byte[]? DecodeStencil(
        PdfDocument document,
        IGraphicsFactory factory,
        PdfStream stream,
        PdfDictionary dictionary,
        int width,
        int height,
        PdfDiagnostics? diagnostics)
    {
        byte[] data = stream.Decode(diagnostics);
        int bitsPerComponent = Math.Max(1, (int)(dictionary.GetIntegerValue("BitsPerComponent") ?? 1));
        int rowBytes = (width * bitsPerComponent + 7) / 8;
        int maxSample = (1 << bitsPerComponent) - 1;
        var stencil = new byte[width * height];

        for (int y = 0; y < height; y++)
        {
            int bitOffset = 0;
            for (int x = 0; x < width; x++)
            {
                // A set sample paints. MuPDF and PDFium both ignore an ImageMask's /Decode [1 0] here -
                // pdf.js issue4246.pdf's stencil declares it and both references still paint its set bits.
                int sample = ReadBits(data, y * rowBytes, ref bitOffset, bitsPerComponent);
                stencil[(y * width) + x] = (byte)Math.Clamp((int)MathF.Round((float)sample / maxSample * 255f), 0, 255);
            }
        }

        return stencil;
    }

    private static List<string> GetFilters(PdfDictionary dictionary)
    {
        var filters = new List<string>();
        switch (dictionary.Get("Filter"))
        {
            case PdfName name:
                filters.Add(name.Value);
                break;

            case PdfArray array:
                foreach (PdfObject item in array)
                {
                    if (item is PdfName itemName)
                    {
                        filters.Add(itemName.Value);
                    }
                }

                break;
        }

        return filters;
    }

    private enum ColorSpaceKind
    {
        DeviceGray,
        DeviceRgb,
        DeviceCmyk,
        Indexed,
        Separation,
        DeviceN,
        Lab,
    }

    private sealed class ColorSpaceInfo
    {
        public ColorSpaceInfo(
            ColorSpaceKind kind,
            int components,
            byte[] lookup,
            int baseComponents,
            IccProfile? icc = null,
            PdfFunction? tint = null,
            ColorSpaceKind baseKind = ColorSpaceKind.DeviceGray,
            ColorSpaceInfo? baseSpace = null,
            float[]? labWhite = null,
            float[]? labRange = null)
        {
            Kind = kind;
            Components = components;
            Lookup = lookup;
            BaseComponents = baseComponents;
            Icc = icc;
            Tint = tint;
            BaseKind = baseKind;
            Base = baseSpace;
            LabWhite = labWhite;
            LabRange = labRange;
        }

        public ColorSpaceKind Kind { get; }

        public int Components { get; }

        public byte[] Lookup { get; }

        public int BaseComponents { get; }

        /// <summary>Lab white point (normalized XYZ) for a Lab space, or null for the D65 default.</summary>
        public float[]? LabWhite { get; }

        /// <summary>Lab /Range [aMin aMax bMin bMax] for a Lab space, or null for the -128..127 default.</summary>
        public float[]? LabRange { get; }

        /// <summary>Embedded ICC profile to apply, when the space is ICCBased and the profile is a supported matrix/TRC type; null otherwise.</summary>
        public IccProfile? Icc { get; }

        /// <summary>Separation/DeviceN tint transform: maps the colorant samples onto the base space.</summary>
        public PdfFunction? Tint { get; }

        /// <summary>What a Separation/DeviceN's base space resolves to, once the tint transform ran.</summary>
        public ColorSpaceKind BaseKind { get; }

        /// <summary>An Indexed space's base space, kept when it needs more than a component count to read -
        /// a DeviceN palette stores one tint per colorant and only becomes a colour through its transform.</summary>
        public ColorSpaceInfo? Base { get; }
    }

    private static ColorSpaceInfo? ResolveColorSpace(PdfDocument document, PdfObject? colorSpaceObject)
    {
        PdfObject? resolved = document.Resolve(colorSpaceObject);

        switch (resolved)
        {
            case PdfName name:
                return name.Value switch
                {
                    "DeviceGray" or "G" or "CalGray" => new ColorSpaceInfo(ColorSpaceKind.DeviceGray, 1, Array.Empty<byte>(), 0),
                    "DeviceRGB" or "RGB" or "CalRGB" => new ColorSpaceInfo(ColorSpaceKind.DeviceRgb, 3, Array.Empty<byte>(), 0),
                    "DeviceCMYK" or "CMYK" => new ColorSpaceInfo(ColorSpaceKind.DeviceCmyk, 4, Array.Empty<byte>(), 0),
                    "Lab" => new ColorSpaceInfo(ColorSpaceKind.Lab, 3, Array.Empty<byte>(), 3),
                    _ => null,
                };

            case PdfArray array when array.Count >= 2 && array[0] is PdfName family:
                switch (family.Value)
                {
                    case "ICCBased":
                        PdfStream? profileStream = document.Resolve(array[1]) as PdfStream;
                        int n = Math.Max(1, (int)(profileStream?.Dictionary.GetIntegerValue("N") ?? 3));
                        // Apply the embedded ICC profile for matrix/TRC RGB/Gray; other profile types
                        // (CLUT, CMYK) fall back to the per-channel device approximation by component count.
                        IccProfile? icc = profileStream is not null ? IccProfile.TryCreate(profileStream.Decode()) : null;
                        return n switch
                        {
                            1 => new ColorSpaceInfo(ColorSpaceKind.DeviceGray, 1, Array.Empty<byte>(), 0, icc),
                            3 => new ColorSpaceInfo(ColorSpaceKind.DeviceRgb, 3, Array.Empty<byte>(), 0, icc),
                            4 => new ColorSpaceInfo(ColorSpaceKind.DeviceCmyk, 4, Array.Empty<byte>(), 0, icc),
                            _ => null,
                        };

                    case "Indexed" or "I":
                        // The base space may itself be an array (an ICCBased palette is common), so it is
                        // resolved rather than assumed to be three-component RGB.
                        ColorSpaceInfo? baseInfo = ResolveColorSpace(document, document.Resolve(array[1]));
                        int hival = array.GetNumber(2)?.AsInt() ?? 255;
                        byte[] lookupBytes = ReadIndexedPalette(document.Resolve(array[3]));
                        int baseComponents = baseInfo?.Components ?? 3;
                        return new ColorSpaceInfo(ColorSpaceKind.Indexed, 1, lookupBytes, baseComponents, baseSpace: baseInfo);

                    case "Separation":
                    {
                        // One colorant sample per pixel; the tint transform says what colour it makes.
                        (ColorSpaceKind baseKind, int baseWidth) = DescribeBaseSpace(document, array.Count >= 3 ? array[2] : null);
                        PdfFunction? tint = array.Count >= 4 ? PdfFunction.Create(document, array[3]) : null;
                        return new ColorSpaceInfo(ColorSpaceKind.Separation, 1, Array.Empty<byte>(), baseWidth, null, tint, baseKind);
                    }

                    case "DeviceN":
                    {
                        // One sample per colorant, then the tint transform onto the base space. Reading
                        // these as base components instead steps through the raster one third of a pixel
                        // at a time, which scrambles the image as well as colouring it wrong.
                        int colorants = Math.Clamp((document.Resolve(array[1]) as PdfArray)?.Count ?? 1, 1, 8);
                        (ColorSpaceKind baseKind, int baseWidth) = DescribeBaseSpace(document, array.Count >= 3 ? array[2] : null);
                        PdfFunction? tint = array.Count >= 4 ? PdfFunction.Create(document, array[3]) : null;
                        return new ColorSpaceInfo(ColorSpaceKind.DeviceN, colorants, Array.Empty<byte>(), baseWidth, null, tint, baseKind);
                    }

                    case "Lab":
                    {
                        // [/Lab << /WhitePoint .. /Range [aMin aMax bMin bMax] >>]. The palette or image
                        // components are L* (0..100) and a*/b* over the /Range; Cielab turns them into sRGB.
                        PdfDictionary? lab = array.Count >= 2 ? document.Resolve(array[1]) as PdfDictionary : null;
                        float[]? white = ReadLabWhite(lab);
                        float[]? range = ReadLabRange(lab);
                        return new ColorSpaceInfo(ColorSpaceKind.Lab, 3, Array.Empty<byte>(), 3, labWhite: white, labRange: range);
                    }

                    default:
                        return null;
                }

            default:
                // Missing color space: per the spec this means DeviceGray for non-RGB images
                return new ColorSpaceInfo(ColorSpaceKind.DeviceGray, 1, Array.Empty<byte>(), 0);
        }
    }

    /// <summary>The kind and width of the base space a Separation or DeviceN array declares.</summary>
    private static (ColorSpaceKind Kind, int Components) DescribeBaseSpace(PdfDocument document, PdfObject? baseSpaceObject)
    {
        PdfObject? resolved = document.Resolve(baseSpaceObject);
        if (resolved is PdfName name)
        {
            return name.Value switch
            {
                "DeviceGray" or "G" or "CalGray" => (ColorSpaceKind.DeviceGray, 1),
                "DeviceCMYK" or "CMYK" => (ColorSpaceKind.DeviceCmyk, 4),
                _ => (ColorSpaceKind.DeviceRgb, 3),
            };
        }

        if (resolved is PdfArray { Count: >= 2 } array && array[0] is PdfName family && family.Value == "ICCBased")
        {
            int n = Math.Clamp((int)((document.Resolve(array[1]) as PdfStream)?.Dictionary.GetIntegerValue("N") ?? 3), 1, 4);
            return n switch
            {
                1 => (ColorSpaceKind.DeviceGray, 1),
                4 => (ColorSpaceKind.DeviceCmyk, 4),
                _ => (ColorSpaceKind.DeviceRgb, 3),
            };
        }

        return (ColorSpaceKind.DeviceRgb, 3);
    }
}
