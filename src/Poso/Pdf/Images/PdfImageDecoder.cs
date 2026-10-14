using System.Linq;
using Poso.Graphics;
using Poso.Pdf.Filters.Codecs;
using Poso.Pdf.Objects;

namespace Poso.Pdf.Images;

/// <summary>
/// Decodes PDF image XObjects (and inline images) into tightly packed BGRA (premultiplied) pixels.
/// Handles DeviceGray/RGB/CMYK/Indexed/ICCBased sample data, image masks, color-key masks and
/// soft masks; DCTDecode (JPEG) is delegated to the backend factory.
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

            return CreateBilevelImage(factory, page.Value);
        }

        if (hasCcitt)
        {
            IImage? ccitt = DecodeCcittImage(factory, stream, dictionary, width, height, diagnostics);
            if (ccitt is null)
            {
                diagnostics?.Report(PdfUnsupportedKind.ImageCodec, "CCITTFaxDecode could not be decoded");
            }

            return ccitt;
        }

        bool imageMask = dictionary.GetBooleanValue("ImageMask") ?? false;

        if (hasDct || hasJpx)
        {
            // PdfStream.Decode stops at the codec header and returns the encoded JPEG/JPEG2000 bytes.
            byte[] encoded = stream.Decode(diagnostics);

            // CMYK/YCCK (4-component) JPEGs are decoded here rather than delegated to the backend:
            // native codecs (Skia's SKCodec) omit the Adobe CMYK inversion and produce visibly wrong
            // colours, and SkiaSharp exposes no CMYK output to recover the raw samples from. 1/3-component
            // JPEGs and JPEG2000 are unaffected and stay on the backend path.
            if (hasDct && !hasJpx && JpegDecoder.PeekComponentCount(encoded) == 4)
            {
                JpegDecoder.Result? jpeg = JpegDecoder.Decode(encoded);
                byte[]? cmykPixels = jpeg is null ? null : JpegColorConverter.ToBgra32(jpeg);
                if (cmykPixels is not null)
                {
                    return factory.CreateImage(jpeg!.Width, jpeg.Height, PixelFormat.Bgra8888Premul, cmykPixels);
                }
                // Fall through to the backend if the self-decode could not complete.
            }

            IImage? raster = factory.DecodeImage(encoded);
            if (raster is null)
            {
                diagnostics?.Report(
                    PdfUnsupportedKind.ImageCodec,
                    $"The backend could not decode {string.Join(",", filters)}");
                return null;
            }

            using (raster)
            {
                byte[] jpegPixels = new byte[raster.Width * raster.Height * raster.BytesPerPixel];
                raster.CopyPixels(jpegPixels);
                return factory.CreateImage(raster.Width, raster.Height, PixelFormat.Bgra8888Premul, jpegPixels);
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

        // Soft mask (alpha)
        byte[]? alpha = null;
        if (document.Resolve(dictionary.Get("SMask")) is PdfStream softMaskStream)
        {
            PdfDictionary softMaskDictionary = softMaskStream.Dictionary;
            alpha = DecodeAlphaChannel(document, factory, softMaskStream, softMaskDictionary, width, height, diagnostics);
        }

        // Hard mask (stencil stream) or color-key mask (array)
        byte[]? stencil = null;
        float[]? colorKeyMin = null;
        float[]? colorKeyMax = null;

        switch (document.Resolve(dictionary.Get("Mask")))
        {
            case PdfStream maskStream when maskStream.Dictionary.GetNameValue("Subtype") != "Image" &&
                                              maskStream.Dictionary.GetBooleanValue("ImageMask") != true:
                // A stream mask is an image mask; handled below via stencil decoding
                stencil = DecodeStencil(document, factory, maskStream, maskStream.Dictionary, width, height, diagnostics);
                break;

            case PdfStream maskStream:
                stencil = DecodeStencil(document, factory, maskStream, maskStream.Dictionary, width, height, diagnostics);
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
                    (r, g, b) = space.Kind switch
                    {
                        ColorSpaceKind.DeviceGray => GrayToRgb(sampleValues[0]),
                        ColorSpaceKind.DeviceRgb => (ToByte(sampleValues[0]), ToByte(sampleValues[1]), ToByte(sampleValues[2])),
                        ColorSpaceKind.DeviceCmyk => CmykToRgb(sampleValues[0], sampleValues[1], sampleValues[2], sampleValues[3]),
                        ColorSpaceKind.Indexed => IndexedToRgb(space, (int)(sampleValues[0] * maxSample)),
                        _ => GrayToRgb(sampleValues[0]),
                    };

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
                    if (stencilIndex < stencil.Length && stencil[stencilIndex] == 0)
                    {
                        alphaByte = 0;
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

    /// <summary>Decodes a CCITT (Group 3/4) bilevel image XObject into a BGRA image.</summary>
    private static IImage CreateBilevelImage(IGraphicsFactory factory, Jbig2Page page)
    {
        int w = page.Width;
        int h = page.Height;
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                byte value = page.IsBlack(x, y) ? (byte)0 : (byte)255;
                int offset = ((y * w) + x) * 4;
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
                pixels[offset + 3] = 255;
            }
        }

        return factory.CreateImage(w, h, PixelFormat.Bgra8888Premul, pixels);
    }

    private static IImage? DecodeCcittImage(IGraphicsFactory factory, PdfStream stream, PdfDictionary dictionary, int width, int height, PdfDiagnostics? diagnostics)
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

        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                int bit = (bits[(y * bytesPerRow) + (x / 8)] >> (7 - (x % 8))) & 1;
                bool isBlack = blackIs1 ? bit == 1 : bit == 0;
                byte value = isBlack ? (byte)0 : (byte)255;
                int offset = ((y * columns) + x) * 4;
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

    private static (byte R, byte G, byte B) CmykToRgb(float c, float m, float y, float k)
    {
        float r = 1f - Math.Min(1f, c + k);
        float g = 1f - Math.Min(1f, m + k);
        float b = 1f - Math.Min(1f, y + k);
        return (ToByte(r), ToByte(g), ToByte(b));
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

        for (int y = 0; y < height; y++)
        {
            int bitOffset = 0;
            for (int x = 0; x < width; x++)
            {
                int sample = ReadBits(data, y * rowBytes, ref bitOffset, bitsPerComponent);
                alpha[(y * width) + x] = (byte)Math.Clamp((int)MathF.Round((float)sample / maxSample * 255f), 0, 255);
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
        var stencil = new byte[width * height];

        for (int y = 0; y < height; y++)
        {
            int bitOffset = 0;
            for (int x = 0; x < width; x++)
            {
                int sample = ReadBits(data, y * rowBytes, ref bitOffset, bitsPerComponent);
                stencil[(y * width) + x] = (byte)Math.Clamp((int)MathF.Round((float)sample / ((1 << bitsPerComponent) - 1) * 255f), 0, 255);
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
    }

    private sealed class ColorSpaceInfo
    {
        public ColorSpaceInfo(ColorSpaceKind kind, int components, byte[] lookup, int baseComponents)
        {
            Kind = kind;
            Components = components;
            Lookup = lookup;
            BaseComponents = baseComponents;
        }

        public ColorSpaceKind Kind { get; }

        public int Components { get; }

        public byte[] Lookup { get; }

        public int BaseComponents { get; }
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
                    _ => null,
                };

            case PdfArray array when array.Count >= 2 && array[0] is PdfName family:
                switch (family.Value)
                {
                    case "ICCBased":
                        PdfStream? profileStream = document.Resolve(array[1]) as PdfStream;
                        int n = Math.Max(1, (int)(profileStream?.Dictionary.GetIntegerValue("N") ?? 3));
                        return n switch
                        {
                            1 => new ColorSpaceInfo(ColorSpaceKind.DeviceGray, 1, Array.Empty<byte>(), 0),
                            3 => new ColorSpaceInfo(ColorSpaceKind.DeviceRgb, 3, Array.Empty<byte>(), 0),
                            4 => new ColorSpaceInfo(ColorSpaceKind.DeviceCmyk, 4, Array.Empty<byte>(), 0),
                            _ => null,
                        };

                    case "Indexed" or "I":
                        PdfObject? baseSpace = document.Resolve(array[1]);
                        ColorSpaceInfo? baseInfo = baseSpace is PdfName baseName ? ResolveColorSpace(document, baseName) : null;
                        int hival = array.GetNumber(2)?.AsInt() ?? 255;
                        PdfString? lookup = document.Resolve(array[3]) as PdfString;
                        byte[] lookupBytes = lookup?.Bytes ?? Array.Empty<byte>();
                        int baseComponents = baseInfo?.Components ?? 3;
                        return new ColorSpaceInfo(ColorSpaceKind.Indexed, 1, lookupBytes, baseComponents);

                    case "Separation" or "DeviceN":
                        // Approximate the first component as gray
                        return new ColorSpaceInfo(ColorSpaceKind.DeviceGray, 1, Array.Empty<byte>(), 0);

                    default:
                        return null;
                }

            default:
                // Missing color space: per the spec this means DeviceGray for non-RGB images
                return new ColorSpaceInfo(ColorSpaceKind.DeviceGray, 1, Array.Empty<byte>(), 0);
        }
    }
}
