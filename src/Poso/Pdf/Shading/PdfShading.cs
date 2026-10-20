using System.Numerics;
using Poso.Geometry;
using Poso.Graphics;
using Poso.Pdf.Functions;
using Poso.Pdf.Objects;

namespace Poso.Pdf.Shading;

/// <summary>
/// The colour space of a shading's function output, resolved to RGB conversion. Only the device
/// families are handled precisely; exotic spaces fall back to a sensible base.
/// </summary>
internal enum ShadingColorSpace
{
    Gray,
    Rgb,
    Cmyk,

    /// <summary>
    /// CIE-based spaces (ISO 32000-1 8.6.5.3/8.6.5.4): the values are not sRGB until the white point,
    /// gamma and matrix have been applied. Reading a linear CalRGB wash (Gamma [1 1 1]) as encoded sRGB
    /// darkens it badly - pdf.js issue17065.pdf.
    /// </summary>
    CalGray,
    CalRgb,
}

/// <summary>
/// A shading colour space as the rasteriser needs it: the base the pixels are built from, plus the
/// Separation/DeviceN tint transform that maps the function's components onto it. A shading may well be
/// defined in a two-component DeviceN space - Cyan and Yellow, say - whose output only becomes a colour
/// after that transform; reading the tints as base components instead turns a green wash near black.
/// </summary>
internal readonly record struct ShadingSpace(
    ShadingColorSpace Base,
    PdfFunction? Tint,
    int TintComponents,
    Poso.Pdf.Colors.CalColor? Cal = null);

/// <summary>
/// PDF shading dictionary (ISO 32000-1 12.5). <see cref="Type2"/> axial and <see cref="Type3"/>
/// radial gradients are rasterised in the core (device space) and blitted as an image, so the
/// back-end does not need native gradient support. Other shading types are recognised but not yet
/// rendered and are reported through the caller's diagnostics.
/// </summary>
public sealed class PdfShading
{
    private readonly PdfFunction? _function;
    private readonly float[] _domain;
    private readonly ShadingSpace _colorSpace;

    // Type 2 (axial): two endpoints in shading space.
    private readonly float[] _coords;

    // Type 3 (radial): centre0 x,y,r, centre1 x,y,r.
    private readonly bool _radial;

    // Type 1 (function): maps the function's two inputs into the space the CTM applies to.
    private Matrix3x2 _matrix = Matrix3x2.Identity;

    // Type 1's optional clipping rectangle, in the same space as its domain.
    private bool _hasBounds;
    private float _boundLeft;
    private float _boundBottom;
    private float _boundRight;
    private float _boundTop;

    private readonly bool[] _extend = new bool[2];

    private List<MeshTriangle>? _meshTriangles;

    private PdfShading(
        int type,
        PdfFunction? function,
        float[] domain,
        ShadingSpace colorSpace,
        float[] coords,
        bool radial)
    {
        Type = type;
        _function = function;
        _domain = domain;
        _colorSpace = colorSpace;
        _coords = coords;
        _radial = radial;
    }

    public int Type { get; }

    public bool IsSupported =>
        (_function is not null && (Type == 1 || Type == 2 || Type == 3)) || _meshTriangles is { Count: > 0 };

    public static PdfShading? Create(PdfDocument document, PdfObject? shadingObject)
    {
        PdfObject? resolved = document.Resolve(shadingObject);
        PdfDictionary? dictionary = resolved as PdfDictionary ?? (resolved as PdfStream)?.Dictionary;
        if (dictionary is null)
        {
            return null;
        }

        int type = (int)(dictionary.GetIntegerValue("ShadingType") ?? 0);

        ShadingSpace colorSpace = ResolveColorSpace(document, dictionary.Get("ColorSpace"));
        PdfFunction? function = PdfFunction.Create(document, dictionary.Get("Function"));
        if (function is null && type is 4 or 5 or 6 or 7)
        {
            // Real-world mesh shadings sometimes omit /Function, relying on the sampled components
            // (mapped through /Decode) as direct colourspace operands. PDFium and MuPDF both render
            // that as an identity mapping, so synthesise one rather than dropping the whole shading.
            int decodeCount = dictionary.GetArray("Decode")?.Count ?? 0;
            int components = decodeCount >= 6
                ? (decodeCount - 4) / 2
                : colorSpace.Base switch
                {
                    ShadingColorSpace.Cmyk => 4,
                    ShadingColorSpace.Rgb or ShadingColorSpace.CalRgb => 3,
                    _ => 1,
                };
            function = IdentityFunction.Create(components);
        }
        if (function is null)
        {
            return null; // function/axial/radial shadings cannot map colour without a function
        }

        if (type is 4 or 5 or 6 or 7)
        {
            if (resolved is not PdfStream meshStream)
            {
                return null; // mesh vertex data is the shading stream itself
            }

            List<MeshTriangle>? triangles = ParseMesh(meshStream, dictionary, colorSpace, function);
            return triangles is { Count: > 0 }
                ? new PdfShading(type, function, Array.Empty<float>(), colorSpace, Array.Empty<float>(), false) { _meshTriangles = triangles }
                : null;
        }

        if (type == 1)
        {
            // Function-based: the function takes the shading-space point itself, so it needs two inputs,
            // and /Matrix is what places that space relative to the CTM.
            if (function.InputCount != 2)
            {
                return null;
            }

            var functionShading = new PdfShading(
                1,
                function,
                ReadFloats(dictionary.GetArray("Domain"), new float[] { 0f, 1f, 0f, 1f }),
                colorSpace,
                Array.Empty<float>(),
                false);

            if (dictionary.GetArray("Matrix") is { Count: >= 6 } matrixArray)
            {
                functionShading._matrix = new Matrix3x2(
                    matrixArray.GetNumber(0)?.AsFloat() ?? 1f,
                    matrixArray.GetNumber(1)?.AsFloat() ?? 0f,
                    matrixArray.GetNumber(2)?.AsFloat() ?? 0f,
                    matrixArray.GetNumber(3)?.AsFloat() ?? 1f,
                    matrixArray.GetNumber(4)?.AsFloat() ?? 0f,
                    matrixArray.GetNumber(5)?.AsFloat() ?? 0f);
            }

            if (dictionary.GetArray("BBox") is { Count: >= 4 })
            {
                functionShading.ReadBounds(dictionary);
            }

            return functionShading;
        }

        if (type != 2 && type != 3)
        {
            return null; // other shading types are not yet rasterised
        }

        if (function.InputCount != 1)
        {
            return null;
        }

        float[] domain = ReadFloats(dictionary.GetArray("Domain"), new float[] { 0f, 1f });
        float[] coords = ReadFloats(dictionary.GetArray("Coords"), Array.Empty<float>());
        bool radial = type == 3;

        int expectedCount = radial ? 6 : 4;
        if (coords.Length < expectedCount)
        {
            return null;
        }

        var shading = new PdfShading(type, function, domain, colorSpace, coords, radial);

        // /BBox limits the painted region for axial and radial shadings too - it lives in the same space as
        // /Coords. Reading it only for type 1 left pdf.js issue8092.pdf painting its gradient across the
        // whole page instead of stopping at the declared box.
        shading.ReadBounds(dictionary);

        PdfArray? extend = dictionary.GetArray("Extend");
        if (extend is not null)
        {
            shading._extend[0] = IsTrue(extend, 0);
            shading._extend[1] = IsTrue(extend, 1);
        }

        return shading;
    }

    /// <summary>Reads /BBox - the region the shading paints - normalising the corner order.</summary>
    private void ReadBounds(PdfDictionary dictionary)
    {
        if (dictionary.GetArray("BBox") is not { Count: >= 4 } boundsArray)
        {
            return;
        }

        _hasBounds = true;
        _boundLeft = MathF.Min(boundsArray.GetNumber(0)?.AsFloat() ?? 0f, boundsArray.GetNumber(2)?.AsFloat() ?? 0f);
        _boundBottom = MathF.Min(boundsArray.GetNumber(1)?.AsFloat() ?? 0f, boundsArray.GetNumber(3)?.AsFloat() ?? 0f);
        _boundRight = MathF.Max(boundsArray.GetNumber(0)?.AsFloat() ?? 0f, boundsArray.GetNumber(2)?.AsFloat() ?? 0f);
        _boundTop = MathF.Max(boundsArray.GetNumber(1)?.AsFloat() ?? 0f, boundsArray.GetNumber(3)?.AsFloat() ?? 0f);
    }

    /// <summary>
    /// Reads one /Extend entry. The specification writes these as booleans, which is what Illustrator
    /// emits, so a reader that only accepts numbers sees false and clips the gradient at its end circle
    /// instead of painting the extended colour out to the edge of the clip.
    /// </summary>
    private static bool IsTrue(PdfArray array, int index) =>
        index < array.Count && array[index] switch
        {
            PdfBoolean flag => flag.Value,
            PdfNumber number => number.AsFloat() != 0f,
            _ => false,
        };

    /// <summary>
    /// Rasterises the shading into a BGRA image covering <paramref name="deviceBounds"/>.
    /// <paramref name="shadingToDevice"/> maps a shading-space point to device pixels.
    /// </summary>
    public IImage? Rasterize(IGraphicsFactory factory, in RectF deviceBounds, in Matrix3x2 shadingToDevice)
    {
        if (_meshTriangles is { Count: > 0 } mesh)
        {
            return RasterizeMesh(factory, deviceBounds, shadingToDevice, mesh);
        }

        int width = Math.Max(1, (int)MathF.Ceiling(deviceBounds.Width));
        int height = Math.Max(1, (int)MathF.Ceiling(deviceBounds.Height));
        if (width > 8192 || height > 8192)
        {
            return null; // sanity limit for malformed clip regions
        }

        // A function-based shading's own /Matrix places its input space, and it applies before the
        // pattern matrix and CTM that the caller has already composed.
        if (!Matrix3x2.Invert(Type == 1 ? _matrix * shadingToDevice : shadingToDevice, out Matrix3x2 deviceToShading))
        {
            return null;
        }

        var pixels = new byte[width * height * 4];
        Span<float> tValues = stackalloc float[] { 0f };

        // Wide enough for what the function emits and for what the colour path reads out of it: a
        // stitching function that omits /Range reports one output while its sub-functions emit four, and
        // taking that report at face value truncates a CMYK wash to its first component. Evaluating into
        // a span wider than the function's outputs makes the evaluator read the leftovers still on its
        // stack as extra components instead. A DeviceN shading feeds the tint transform one input per
        // colourant - which can be more than the four a CMYK base needs - so the ceiling is the colourant
        // count, not four; clipping it to four starves a nine-colourant tint and paints the shading white.
        Span<float> color = stackalloc float[Math.Clamp(ColorSlotCount(), 1, 32)];

        if (Type == 1)
        {
            // /Domain bounds the function's inputs, which live before /Matrix; /BBox bounds the region
            // that gets painted, which lives after it. Comparing the same point against both is what
            // makes a shading that declares a /BBox come out empty.
            Matrix3x2.Invert(shadingToDevice, out Matrix3x2 deviceToPlaced);
            Span<float> position = stackalloc float[2];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var device = new Vector2(deviceBounds.X + x + 0.5f, deviceBounds.Y + y + 0.5f);
                    Vector2 s = Vector2.Transform(device, deviceToShading);
                    if (!AcceptsDomain(s))
                    {
                        continue;
                    }

                    if (_hasBounds)
                    {
                        Vector2 placed = Vector2.Transform(device, deviceToPlaced);
                        if (placed.X < _boundLeft || placed.X > _boundRight ||
                            placed.Y < _boundBottom || placed.Y > _boundTop)
                        {
                            continue;
                        }
                    }

                    position[0] = s.X;
                    position[1] = s.Y;
                    color.Clear();
                    _function!.Evaluate(position, color);
                    WritePixel(pixels, (y * width) + x, color);
                }
            }

            return factory.CreateImage(width, height, PixelFormat.Bgra8888Premul, pixels);
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var device = new Vector2(deviceBounds.X + x + 0.5f, deviceBounds.Y + y + 0.5f);
                Vector2 s = Vector2.Transform(device, deviceToShading);

                // /BBox limits the painted region for every function-based shading, not just type 1: an
                // axial or radial shading with a /BBox must stop at it (pdf.js issue8092.pdf fills the whole
                // page with the pattern while its shading declares a smaller box). Types 2 and 3 carry no
                // /Matrix, so the shading space is the placed space here.
                if (_hasBounds && (s.X < _boundLeft || s.X > _boundRight ||
                                   s.Y < _boundBottom || s.Y > _boundTop))
                {
                    continue;
                }

                if (!TryParameter(s, out float t))
                {
                    continue; // transparent (outside the domain and not extended)
                }

                tValues[0] = t;
                color.Clear();
                _function!.Evaluate(tValues, color);
                WritePixel(pixels, (y * width) + x, color);
            }
        }

        return factory.CreateImage(width, height, PixelFormat.Bgra8888Premul, pixels);
    }

    /// <summary>
    /// How many components the shading's colour path works with: the tint transform's input count when
    /// there is one, otherwise the base space's width - and never fewer than the function claims to
    /// return.
    /// </summary>
    private int ColorSlotCount()
    {
        int needed = _colorSpace.Tint is not null ?
            _colorSpace.TintComponents :
            _colorSpace.Base switch
            {
                ShadingColorSpace.Gray or ShadingColorSpace.CalGray => 1,
                ShadingColorSpace.Rgb or ShadingColorSpace.CalRgb => 3,
                _ => 4,
            };

        return Math.Max(needed, _function?.OutputCount ?? 0);
    }

    /// <summary>
    /// Whether a shading-space point is inside the function's /Domain. Outside it nothing is drawn: a
    /// function-based shading has no /Extend, and the axial and radial paths stop at their endpoints
    /// unless the flag says otherwise.
    /// </summary>
    private bool AcceptsDomain(Vector2 s) =>
        _domain.Length < 4 ||
        (s.X >= _domain[0] && s.X <= _domain[1] && s.Y >= _domain[2] && s.Y <= _domain[3]);

    private bool TryParameter(Vector2 s, out float t)
    {
        if (!_radial)
        {
            return TryAxialParameter(s, out t);
        }

        return TryRadialParameter(s, out t);
    }

    private bool TryAxialParameter(Vector2 s, out float t)
    {
        float x0 = _coords[0];
        float y0 = _coords[1];
        float x1 = _coords[2];
        float y1 = _coords[3];
        float vx = x1 - x0;
        float vy = y1 - y0;
        float lengthSquared = (vx * vx) + (vy * vy);
        float parameter = lengthSquared <= 0f ? 0f : (((s.X - x0) * vx) + ((s.Y - y0) * vy)) / lengthSquared;
        return AcceptParameter(parameter, out t);
    }

    private bool TryRadialParameter(Vector2 s, out float t)
    {
        float x0 = _coords[0];
        float y0 = _coords[1];
        float r0 = _coords[2];
        float x1 = _coords[3];
        float y1 = _coords[4];
        float r1 = _coords[5];

        float fx = x1 - x0;
        float fy = y1 - y0;
        float fd = (fx * fx) + (fy * fy);
        float dr = r1 - r0;
        float dx0 = s.X - x0;
        float dy0 = s.Y - y0;

        float a = fd - (dr * dr);
        float b = -((dx0 * fx) + (dy0 * fy) + (r0 * dr));
        float c = (dx0 * dx0) + (dy0 * dy0) - (r0 * r0);

        // Concentric / cone-closed case (a == 0): the radius is linear in t.
        if (MathF.Abs(a) < 1e-6f)
        {
            float linear = MathF.Abs(b) < 1e-6f ? 0f : c / b;
            return AcceptParameter(linear, out t);
        }

        float determinant = (b * b) - (a * c);
        if (determinant < 0f)
        {
            t = 0f;
            return false;
        }

        float root = MathF.Sqrt(determinant);
        float first = (-b - root) / a;
        float second = (-b + root) / a;

        // Choose the solution whose interpolated radius is non-negative; prefer the smaller t.
        float chosen = (r0 + (first * dr)) >= 0f ? first : second;
        return AcceptParameter(chosen, out t);
    }

    private bool AcceptParameter(float rawT, out float t)
    {
        float domainMin = _domain.Length >= 2 ? _domain[0] : 0f;
        float domainMax = _domain.Length >= 2 ? _domain[1] : 1f;

        if (rawT < domainMin)
        {
            if (!_extend[0])
            {
                t = domainMin;
                return false;
            }

            t = domainMin;
            return true;
        }

        if (rawT > domainMax)
        {
            if (!_extend[1])
            {
                t = domainMax;
                return false;
            }

            t = domainMax;
            return true;
        }

        t = rawT;
        return true;
    }

    private void WritePixel(byte[] pixels, int index, ReadOnlySpan<float> color)
    {
        ColorToRgb(color, _colorSpace, out byte r, out byte g, out byte b);
        int offset = index * 4;
        pixels[offset] = b; // BGRA, premultiplied (opaque)
        pixels[offset + 1] = g;
        pixels[offset + 2] = r;
        pixels[offset + 3] = 255;
    }

    private static void ColorToRgb(ReadOnlySpan<float> color, ShadingSpace colorSpace, out byte r, out byte g, out byte b)
    {
        // Four slots either way: a CMYK base needs all of them, and a tint transform's output is the
        // only thing that can be shorter.
        Span<float> v = stackalloc float[4];
        if (colorSpace.Tint is { } tint)
        {
            tint.Evaluate(color[..Math.Min(colorSpace.TintComponents, color.Length)], v);
        }
        else
        {
            for (int i = 0; i < v.Length && i < color.Length; i++)
            {
                v[i] = color[i];
            }
        }

        switch (colorSpace.Base)
        {
            case ShadingColorSpace.Gray:
                r = g = b = ToByte(v[0]);
                break;
            case ShadingColorSpace.Cmyk:
                // The same conversion the element colours use: the textbook 1-(c+k) formula leaves the
                // green channel at full strength for a cyan/yellow DeviceN wash, which is nowhere near
                // any viewer's SWOP-based default conversion.
                PosoColor converted = PosoColor.FromCmyk(v[0], v[1], v[2], v[3]);
                r = converted.R;
                g = converted.G;
                b = converted.B;
                break;
            case ShadingColorSpace.CalGray when colorSpace.Cal is { } calGray:
                {
                    Poso.Pdf.Colors.PosoColorF gray = calGray.ToRgb(v[..1]);
                    r = ToByte(gray.R);
                    g = ToByte(gray.G);
                    b = ToByte(gray.B);
                    break;
                }

            case ShadingColorSpace.CalRgb when colorSpace.Cal is { } calRgb:
                {
                    Poso.Pdf.Colors.PosoColorF rgb = calRgb.ToRgb(v[..3]);
                    r = ToByte(rgb.R);
                    g = ToByte(rgb.G);
                    b = ToByte(rgb.B);
                    break;
                }

            default: // RGB
                r = ToByte(v[0]);
                g = ToByte(v[1]);
                b = ToByte(v[2]);
                break;
        }
    }

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f), 0, 255);

    private static ShadingSpace ResolveColorSpace(PdfDocument document, PdfObject? colorSpaceObject)
    {
        PdfObject? resolved = document.Resolve(colorSpaceObject);
        if (resolved is PdfArray { Count: >= 4 } array
            && array[0] is PdfName family
            && family.Value is "Separation" or "DeviceN")
        {
            // [space, tint transform]: the function emits one component per separation name and only the
            // transform maps them onto the base space, which is itself a name or an ICCBased array.
            (ShadingColorSpace baseSpace, Poso.Pdf.Colors.CalColor? baseCal) = BaseSpaceOf(document.Resolve(array[2]));
            int tints = family.Value == "Separation"
                ? 1
                : Math.Max(1, (document.Resolve(array[1]) as PdfArray)?.Count ?? 1);
            return new ShadingSpace(baseSpace, PdfFunction.Create(document, array[3]), tints, baseCal);
        }

        (ShadingColorSpace plainBase, Poso.Pdf.Colors.CalColor? plainCal) = BaseSpaceOf(resolved);
        return new ShadingSpace(plainBase, null, 0, plainCal);
    }

    private static (ShadingColorSpace Base, Poso.Pdf.Colors.CalColor? Cal) BaseSpaceOf(PdfObject? colorSpaceObject)
    {
        string? name = colorSpaceObject switch
        {
            PdfName n => n.Value,
            PdfArray a when a.Count >= 1 && a[0] is PdfName family => family.Value,
            _ => null,
        };

        if (name is "CalGray" or "CalRGB")
        {
            // The parameters live in the dictionary that follows the family name.
            Poso.Pdf.Colors.CalColor cal = Poso.Pdf.Colors.CalColor.FromDictionary(
                colorSpaceObject is PdfArray { Count: >= 2 } cieArray ? cieArray[1] as PdfDictionary : null);
            return (name == "CalGray" ? ShadingColorSpace.CalGray : ShadingColorSpace.CalRgb, cal);
        }

        return (name switch
        {
            "DeviceCMYK" or "CMYK" => ShadingColorSpace.Cmyk,
            "DeviceGray" or "G" => ShadingColorSpace.Gray,
            _ => ShadingColorSpace.Rgb,
        }, null);
    }

    private static float[] ReadFloats(PdfArray? array, float[] fallback)
    {
        if (array is null || array.Count == 0)
        {
            return fallback;
        }

        var values = new float[array.Count];
        for (int i = 0; i < array.Count; i++)
        {
            values[i] = array.GetNumber(i)?.AsFloat() ?? 0f;
        }

        return values;
    }

    // ---- Mesh shadings (Type 4 tensor patches, Type 6 Coons, Type 7 lattice triangles) ----

    private readonly struct MeshVertex
    {
        public MeshVertex(Vector2 position, byte r, byte g, byte b)
        {
            Position = position;
            R = r;
            G = g;
            B = b;
        }

        public Vector2 Position { get; }
        public byte R { get; }
        public byte G { get; }
        public byte B { get; }
    }

    private readonly struct MeshTriangle
    {
        public MeshTriangle(MeshVertex a, MeshVertex b, MeshVertex c)
        {
            A = a;
            B = b;
            C = c;
        }

        public MeshVertex A { get; }
        public MeshVertex B { get; }
        public MeshVertex C { get; }
    }

    private sealed class BitReader
    {
        private readonly byte[] _data;
        private int _bit;

        public BitReader(byte[] data)
        {
            _data = data;
        }

        public int RemainingBits => (_data.Length * 8) - _bit;

        public int ReadBits(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                int byteIndex = _bit >> 3;
                int bit = byteIndex < _data.Length ? (_data[byteIndex] >> (7 - (_bit & 7))) & 1 : 0;
                value = (value << 1) | bit;
                _bit++;
            }

            return value;
        }

        /// <summary>
        /// Reads up to 32 bits without sign trouble. A 32-bit coordinate read through the int-returning
        /// ReadBits overflows into the sign bit and comes back negative: pdf.js personwithdog.pdf declares
        /// BitsPerCoordinate 32, and every mesh point then decoded far outside the page, leaving the page
        /// blank.
        /// </summary>
        public long ReadBitsWide(int count)
        {
            long value = 0;
            for (int i = 0; i < count; i++)
            {
                int byteIndex = _bit >> 3;
                int bit = byteIndex < _data.Length ? (_data[byteIndex] >> (7 - (_bit & 7))) & 1 : 0;
                value = (value << 1) | (long)bit;
                _bit++;
            }

            return value;
        }
    }

    private static List<MeshTriangle>? ParseMesh(PdfStream stream, PdfDictionary dictionary, ShadingSpace colorSpace, PdfFunction function)
    {
        byte[] data = stream.Decode(null);
        if (data.Length == 0)
        {
            return null;
        }

        int type = (int)(dictionary.GetIntegerValue("ShadingType") ?? 0);
        int bitsPerCoordinate = (int)(dictionary.GetIntegerValue("BitsPerCoordinate") ?? 1);
        int bitsPerFlag = Math.Max(0, (int)(dictionary.GetIntegerValue("BitsPerFlag") ?? 0));
        int bitsPerComponent = (int)(dictionary.GetIntegerValue("BitsPerComponent") ?? 1);
        if (bitsPerCoordinate <= 0 || bitsPerComponent <= 0)
        {
            return null;
        }

        int k = Math.Max(1, function.InputCount);
        float[] decode = ReadFloats(dictionary.GetArray("Decode"), Array.Empty<float>());
        if (decode.Length < 4 + (2 * k))
        {
            decode = new float[4 + (2 * k)];
            for (int i = 0; i < decode.Length; i += 2)
            {
                decode[i] = 0f;
                decode[i + 1] = 1f;
            }
        }

        double coordScale = Math.Pow(2, bitsPerCoordinate) - 1;
        double componentScale = Math.Pow(2, bitsPerComponent) - 1;
        var reader = new BitReader(data);
        var triangles = new List<MeshTriangle>();
        int vertexBits = (2 * bitsPerCoordinate) + (k * bitsPerComponent);

        Vector2 ReadPoint()
        {
            long rx = reader.ReadBitsWide(bitsPerCoordinate);
            long ry = reader.ReadBitsWide(bitsPerCoordinate);
            return new Vector2(
                decode[0] + ((float)(rx / coordScale) * (decode[1] - decode[0])),
                decode[2] + ((float)(ry / coordScale) * (decode[3] - decode[2])));
        }

        (byte R, byte G, byte B) ReadColour()
        {
            var inputs = new float[k];
            for (int i = 0; i < k; i++)
            {
                int rc = reader.ReadBits(bitsPerComponent);
                inputs[i] = decode[4 + (2 * i)] + ((float)(rc / componentScale) * (decode[5 + (2 * i)] - decode[4 + (2 * i)]));
            }

            var outputs = new float[function.OutputCount];
            function.Evaluate(inputs, outputs);
            ColorToRgb(outputs, colorSpace, out byte r, out byte g, out byte b);
            return (r, g, b);
        }

        MeshVertex ReadVertex()
        {
            // Point first, then its colour: that is the order types 4 and 6 write their vertices in.
            Vector2 point = ReadPoint();
            (byte R, byte G, byte B) colour = ReadColour();
            return new MeshVertex(point, colour.R, colour.G, colour.B);
        }

        if (type == 7)
        {
            // A tensor patch specifies 16 control points but only four colours, one at each corner, and a
            // continuation patch repeats part of the previous one: the first four points and the first two
            // colours come from the patch before, the rest follow in the stream. The walk order is the
            // boundary-then-interior order of 32000-1 Table 76.
            int pointBits = 2 * bitsPerCoordinate;
            int colourBits = k * bitsPerComponent;
            Vector2[]? previousPoints = null;
            (byte R, byte G, byte B)[]? previousColours = null;

            while (reader.RemainingBits >= bitsPerFlag)
            {
                int flag = bitsPerFlag > 0 ? reader.ReadBits(bitsPerFlag) : 0;
                if (flag is not (0 or 1 or 2 or 3) || (flag != 0 && previousPoints is null))
                {
                    break;
                }

                int firstPoint = flag == 0 ? 0 : 4;
                int firstColour = flag == 0 ? 0 : 2;
                if (reader.RemainingBits < ((16 - firstPoint) * pointBits) + ((4 - firstColour) * colourBits))
                {
                    break;
                }

                Vector2[] points = new Vector2[16];
                (byte R, byte G, byte B)[] colours = new (byte R, byte G, byte B)[4];
                for (int i = firstPoint; i < 16; i++)
                {
                    points[i] = ReadPoint();
                }

                for (int i = firstColour; i < 4; i++)
                {
                    colours[i] = ReadColour();
                }

                if (flag != 0 && previousPoints is { } sharedPoints && previousColours is { } sharedColours)
                {
                    // The shared edge runs along the twelve boundary points, so it wraps at 12 rather
                    // than at the 16 that includes the interior poles.
                    for (int i = 0; i < 4; i++)
                    {
                        points[i] = sharedPoints[((3 * flag) + i) % 12];
                    }

                    colours[0] = sharedColours[flag];
                    colours[1] = sharedColours[(flag + 1) % 4];
                }

                EmitType7Patch(triangles, points, colours);

                previousPoints = points;
                previousColours = colours;
            }
        }
        else if (type == 5)
        {
            // Lattice-form Gouraud-shaded triangle mesh (PDF 32000-1 12.6.4.2): no flags; vertices arrive
            // in rows of /VerticesPerRow and each adjacent pair of rows is stitched into a band of quads,
            // split as two Gouraud triangles. Mirrors MuPDF's fz_process_shade_type5 (which reads a full
            // row every time and splits quad (ref[i],ref[i+1],buf[i+1],buf[i]) into (v0,v1,v3)+(v3,v2,v1))
            // so the raster matches its edge walking and stays deterministic.
            int verticesPerRow = (int)(dictionary.GetIntegerValue("VerticesPerRow") ?? 0);
            if (verticesPerRow < 2)
            {
                return null;
            }

            MeshVertex[]? previousRow = null;
            while (reader.RemainingBits >= (verticesPerRow * vertexBits))
            {
                var row = new MeshVertex[verticesPerRow];
                for (int i = 0; i < verticesPerRow; i++)
                {
                    row[i] = ReadVertex();
                }

                if (previousRow is { } refRow)
                {
                    for (int i = 0; i < verticesPerRow - 1; i++)
                    {
                        MeshVertex v0 = refRow[i], v1 = refRow[i + 1], v2 = row[i + 1], v3 = row[i];
                        triangles.Add(new MeshTriangle(v0, v1, v3));
                        triangles.Add(new MeshTriangle(v3, v2, v1));
                    }
                }

                previousRow = row;
            }
        }
        else if (type == 4)
        {
            // Free-form Gouraud-shaded triangle mesh (PDF 32000-1 12.6.4.2): every vertex carries an edge
            // flag then its position and colour. Flag 0 starts a new triangle from three fresh vertices;
            // flags 1 and 2 reuse the previous triangle's shared edge (two or one carried vertices) so the
            // strip walks the mesh. Mirrors MuPDF's fz_process_shade_type4, including "any other flag means
            // start a new triangle". The previous (wrong) reader treated type 4 as a 16-point tensor patch.
            MeshVertex va = default, vb = default, vc = default, vd = default;
            bool first = true;
            int step = bitsPerFlag + vertexBits;
            while (reader.RemainingBits >= step)
            {
                int flag = bitsPerFlag > 0 ? reader.ReadBits(bitsPerFlag) : 0;
                vd = ReadVertex();
                if (first)
                {
                    flag = 0; // the very first vertex must begin a triangle
                    first = false;
                }

                switch (flag)
                {
                    case 1: // Vb, Vc, Vd
                        va = vb; vb = vc; vc = vd;
                        triangles.Add(new MeshTriangle(va, vb, vc));
                        break;
                    case 2: // Va, Vc, Vd
                        vb = vc; vc = vd;
                        triangles.Add(new MeshTriangle(va, vb, vc));
                        break;
                    default: // 0 (and reserved): three new vertices
                        va = vd;
                        if (reader.RemainingBits < step)
                        {
                            break; // truncated triangle - drop it
                        }

                        if (bitsPerFlag > 0)
                        {
                            reader.ReadBits(bitsPerFlag);
                        }

                        vb = ReadVertex();
                        if (reader.RemainingBits < step)
                        {
                            break;
                        }

                        if (bitsPerFlag > 0)
                        {
                            reader.ReadBits(bitsPerFlag);
                        }

                        vc = ReadVertex();
                        triangles.Add(new MeshTriangle(va, vb, vc));
                        break;
                }
            }
        }
        else if (type == 6)
        {
            // Constant-colour tensor-product patch mesh (12.6.4.3): each patch carries 12 boundary control
            // points and four corner colours - unlike type 7, which supplies all 16 points; the four interior
            // poles are computed from the boundary. A continuation flag inherits the shared edge's four
            // points and two seam colours from the previous patch. Mirrors MuPDF's fz_process_shade_type6.
            int pointBits = 2 * bitsPerCoordinate;
            int colourBits = k * bitsPerComponent;
            Vector2[]? previousPoints = null;
            (byte R, byte G, byte B)[]? previousColours = null;

            while (reader.RemainingBits >= bitsPerFlag)
            {
                int flag = bitsPerFlag > 0 ? reader.ReadBits(bitsPerFlag) : 0;
                if (flag is not (0 or 1 or 2 or 3) || (flag != 0 && previousPoints is null))
                {
                    break;
                }

                int firstPoint = flag == 0 ? 0 : 4;
                int firstColour = flag == 0 ? 0 : 2;
                if (reader.RemainingBits < ((12 - firstPoint) * pointBits) + ((4 - firstColour) * colourBits))
                {
                    break;
                }

                Vector2[] points = new Vector2[12];
                (byte R, byte G, byte B)[] colours = new (byte R, byte G, byte B)[4];
                for (int i = firstPoint; i < 12; i++)
                {
                    points[i] = ReadPoint();
                }

                for (int i = firstColour; i < 4; i++)
                {
                    colours[i] = ReadColour();
                }

                if (flag != 0 && previousPoints is { } pp && previousColours is { } pc)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        points[i] = pp[((3 * flag) + i) % 12];
                    }

                    colours[0] = pc[flag];
                    colours[1] = pc[(flag + 1) % 4];
                }

                EmitType6Patch(triangles, points, colours);
                previousPoints = points;
                previousColours = colours;
            }
        }

        return triangles.Count > 0 ? triangles : null;
    }

    /// <summary>
    /// Flattens one type 7 (tensor-product) patch. Its geometry is the cubic tensor surface of the 16
    /// control points, but its colour is bilinear over the four corner colours the patch carries - unlike a
    /// type 4 patch, where every control point brings its own colour and the surface blends them.
    /// </summary>
    private static void EmitType7Patch(
        List<MeshTriangle> triangles,
        Vector2[] points,
        (byte R, byte G, byte B)[] corners)
    {
        // Table 76 walks the patch boundary and then names the four interior poles; the grid the surface
        // is evaluated on is control[row, column], so the two orders differ by this permutation.
        ReadOnlySpan<int> streamOrder = [0, 1, 2, 3, 11, 12, 13, 4, 10, 15, 14, 5, 9, 8, 7, 6];
        var control = new Vector2[16];
        for (int index = 0; index < 16; index++)
        {
            control[index] = points[streamOrder[index]];
        }

        EmitTensorGrid(triangles, control, corners);
    }

    /// <summary>
    /// Flattens one type 6 (constant-colour tensor-product) patch. Its boundary is twelve control points; the
    /// four interior poles are computed from them so the surface is a full cubic tensor patch, then painted
    /// with bilinear interpolation of the four corner colours exactly like type 7. The point -> pole layout
    /// and the interior formulas mirror MuPDF's make_tensor_patch(type 6).
    /// </summary>
    private static void EmitType6Patch(
        List<MeshTriangle> triangles,
        Vector2[] v,
        (byte R, byte G, byte B)[] corners)
    {
        Vector2 i11 = ComputeTensorInterior(v[0], v[1], v[11], v[3], v[9], v[8], v[4], v[6]);
        Vector2 i12 = ComputeTensorInterior(v[3], v[2], v[4], v[0], v[6], v[7], v[11], v[9]);
        Vector2 i21 = ComputeTensorInterior(v[9], v[8], v[10], v[6], v[0], v[1], v[5], v[3]);
        Vector2 i22 = ComputeTensorInterior(v[6], v[7], v[5], v[9], v[3], v[2], v[10], v[0]);

        var control = new Vector2[16];
        control[0] = v[0];
        control[1] = v[1];
        control[2] = v[2];
        control[3] = v[3];
        control[4] = v[11];
        control[5] = i11;
        control[6] = i12;
        control[7] = v[4];
        control[8] = v[10];
        control[9] = i21;
        control[10] = i22;
        control[11] = v[5];
        control[12] = v[9];
        control[13] = v[8];
        control[14] = v[7];
        control[15] = v[6];

        EmitTensorGrid(triangles, control, corners);
    }

    /// <summary>Collapses the four interior poles of a type 6 patch from its boundary (PDF 1.7 p.330).</summary>
    private static Vector2 ComputeTensorInterior(
        Vector2 a, Vector2 b, Vector2 c, Vector2 d, Vector2 e, Vector2 f, Vector2 g, Vector2 h) =>
        new(
            ((-4f * a.X) + (6f * (b.X + c.X)) - (2f * (d.X + e.X)) + (3f * (f.X + g.X)) - h.X) / 9f,
            ((-4f * a.Y) + (6f * (b.Y + c.Y)) - (2f * (d.Y + e.Y)) + (3f * (f.Y + g.Y)) - h.Y) / 9f);

    /// <summary>
    /// Flattens a tensor-product patch given its 16 control poles in row-major [row * 4 + column] order: the
    /// geometry is the cubic Bernstein surface, the colour bilinear over the four corner colours. Shared by
    /// type 6 (computed interior) and type 7 (streamed interior).
    /// </summary>
    private static void EmitTensorGrid(
        List<MeshTriangle> triangles,
        Vector2[] control,
        (byte R, byte G, byte B)[] corners)
    {
        const int Divisions = 8;
        var grid = new MeshVertex[Divisions + 1, Divisions + 1];
        for (int i = 0; i <= Divisions; i++)
        {
            for (int j = 0; j <= Divisions; j++)
            {
                float u = (float)i / Divisions;
                float v = (float)j / Divisions;
                float[] bu = BernsteinCubic(u);
                float[] bv = BernsteinCubic(v);
                float x = 0f, y = 0f;
                for (int a = 0; a < 4; a++)
                {
                    for (int b = 0; b < 4; b++)
                    {
                        float w = bu[a] * bv[b];
                        x += w * control[(a * 4) + b].X;
                        y += w * control[(a * 4) + b].Y;
                    }
                }

                grid[i, j] = Vertex(
                    x,
                    y,
                    Bilinear(corners[0].R, corners[1].R, corners[2].R, corners[3].R, u, v),
                    Bilinear(corners[0].G, corners[1].G, corners[2].G, corners[3].G, u, v),
                    Bilinear(corners[0].B, corners[1].B, corners[2].B, corners[3].B, u, v));
            }
        }

        for (int i = 0; i < Divisions; i++)
        {
            for (int j = 0; j < Divisions; j++)
            {
                MeshVertex p00 = grid[i, j], p10 = grid[i + 1, j], p01 = grid[i, j + 1], p11 = grid[i + 1, j + 1];
                triangles.Add(new MeshTriangle(p00, p10, p11));
                triangles.Add(new MeshTriangle(p00, p11, p01));
            }
        }
    }

    private static float Bilinear(byte c00, byte c01, byte c11, byte c10, float u, float v) =>
        ((1 - u) * (1 - v) * c00) + ((1 - u) * v * c01) + (u * v * c11) + (u * (1 - v) * c10);

    private static MeshVertex Vertex(float x, float y, float r, float g, float b) =>
        new(new Vector2(x, y), ClampByte(r), ClampByte(g), ClampByte(b));

    private static byte ClampByte(float value) => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);

    private static float[] BernsteinCubic(float t)
    {
        float mt = 1f - t;
        return new[] { mt * mt * mt, 3 * t * mt * mt, 3 * t * t * mt, t * t * t };
    }

    private IImage? RasterizeMesh(IGraphicsFactory factory, in RectF deviceBounds, in Matrix3x2 shadingToDevice, List<MeshTriangle> mesh)
    {
        int width = Math.Max(1, (int)MathF.Ceiling(deviceBounds.Width));
        int height = Math.Max(1, (int)MathF.Ceiling(deviceBounds.Height));
        if (width > 8192 || height > 8192)
        {
            return null;
        }

        var pixels = new byte[width * height * 4];
        foreach (MeshTriangle triangle in mesh)
        {
            Vector2 a = Vector2.Transform(triangle.A.Position, shadingToDevice) - new Vector2(deviceBounds.X, deviceBounds.Y);
            Vector2 b = Vector2.Transform(triangle.B.Position, shadingToDevice) - new Vector2(deviceBounds.X, deviceBounds.Y);
            Vector2 c = Vector2.Transform(triangle.C.Position, shadingToDevice) - new Vector2(deviceBounds.X, deviceBounds.Y);
            FillTriangle(pixels, width, height, a, b, c, triangle);
        }

        return factory.CreateImage(width, height, PixelFormat.Bgra8888Premul, pixels);
    }

    private static void FillTriangle(byte[] pixels, int width, int height, Vector2 a, Vector2 b, Vector2 c, MeshTriangle triangle)
    {
        float minX = MathF.Max(0, MathF.Min(MathF.Min(a.X, b.X), c.X));
        float maxX = MathF.Min(width - 1, MathF.Max(MathF.Max(a.X, b.X), c.X));
        float minY = MathF.Max(0, MathF.Min(MathF.Min(a.Y, b.Y), c.Y));
        float maxY = MathF.Min(height - 1, MathF.Max(MathF.Max(a.Y, b.Y), c.Y));
        float area = ((b.Y - c.Y) * (a.X - c.X)) + ((c.X - b.X) * (a.Y - c.Y));
        if (MathF.Abs(area) < 1e-6f)
        {
            return;
        }

        for (int y = (int)MathF.Floor(minY); y <= (int)MathF.Ceiling(maxY); y++)
        {
            for (int x = (int)MathF.Floor(minX); x <= (int)MathF.Ceiling(maxX); x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                float w0 = (((b.Y - c.Y) * (px - c.X)) + ((c.X - b.X) * (py - c.Y))) / area;
                float w1 = (((c.Y - a.Y) * (px - c.X)) + ((a.X - c.X) * (py - c.Y))) / area;
                float w2 = 1f - w0 - w1;
                if (w0 < -0.001f || w1 < -0.001f || w2 < -0.001f)
                {
                    continue;
                }

                float r = (w0 * triangle.A.R) + (w1 * triangle.B.R) + (w2 * triangle.C.R);
                float g = (w0 * triangle.A.G) + (w1 * triangle.B.G) + (w2 * triangle.C.G);
                float bl = (w0 * triangle.A.B) + (w1 * triangle.B.B) + (w2 * triangle.C.B);
                int offset = ((y * width) + x) * 4;
                pixels[offset] = ClampByte(bl);
                pixels[offset + 1] = ClampByte(g);
                pixels[offset + 2] = ClampByte(r);
                pixels[offset + 3] = 255;
            }
        }
    }
}
