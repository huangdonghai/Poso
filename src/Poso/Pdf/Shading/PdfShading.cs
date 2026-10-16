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
}

/// <summary>
/// A shading colour space as the rasteriser needs it: the base the pixels are built from, plus the
/// Separation/DeviceN tint transform that maps the function's components onto it. A shading may well be
/// defined in a two-component DeviceN space - Cyan and Yellow, say - whose output only becomes a colour
/// after that transform; reading the tints as base components instead turns a green wash near black.
/// </summary>
internal readonly record struct ShadingSpace(ShadingColorSpace Base, PdfFunction? Tint, int TintComponents);

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

    public bool IsSupported => (_function is not null && (Type == 2 || Type == 3)) || _meshTriangles is { Count: > 0 };

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
        if (function is null)
        {
            return null; // every shading type (including mesh) needs a function to map colour
        }

        if (type is 4 or 6 or 7)
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

        if (type != 2 && type != 3)
        {
            return null; // function / other shadings are not yet rasterised
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

        PdfArray? extend = dictionary.GetArray("Extend");
        if (extend is not null)
        {
            shading._extend[0] = IsTrue(extend, 0);
            shading._extend[1] = IsTrue(extend, 1);
        }

        return shading;
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

        if (!Matrix3x2.Invert(shadingToDevice, out Matrix3x2 deviceToShading))
        {
            return null;
        }

        var pixels = new byte[width * height * 4];
        Span<float> tValues = stackalloc float[] { 0f };
        Span<float> color = stackalloc float[4];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var device = new Vector2(deviceBounds.X + x + 0.5f, deviceBounds.Y + y + 0.5f);
                Vector2 s = Vector2.Transform(device, deviceToShading);

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
            {
                float k = v[3];
                r = ToByte(1f - Math.Min(1f, v[0] + k));
                g = ToByte(1f - Math.Min(1f, v[1] + k));
                b = ToByte(1f - Math.Min(1f, v[2] + k));
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
            ShadingColorSpace baseSpace = BaseSpaceOf(document.Resolve(array[2]));
            int tints = family.Value == "Separation"
                ? 1
                : Math.Max(1, (document.Resolve(array[1]) as PdfArray)?.Count ?? 1);
            return new ShadingSpace(baseSpace, PdfFunction.Create(document, array[3]), tints);
        }

        return new ShadingSpace(BaseSpaceOf(resolved), null, 0);
    }

    private static ShadingColorSpace BaseSpaceOf(PdfObject? colorSpaceObject)
    {
        string? name = colorSpaceObject switch
        {
            PdfName n => n.Value,
            PdfArray a when a.Count >= 1 && a[0] is PdfName family => family.Value,
            _ => null,
        };

        return name switch
        {
            "DeviceCMYK" or "CMYK" => ShadingColorSpace.Cmyk,
            "DeviceGray" or "G" or "CalGray" => ShadingColorSpace.Gray,
            "DeviceRGB" or "RGB" or "CalRGB" or "ICCBased" => ShadingColorSpace.Rgb,
            _ => ShadingColorSpace.Rgb,
        };
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

        MeshVertex ReadVertex()
        {
            int rx = reader.ReadBits(bitsPerCoordinate);
            int ry = reader.ReadBits(bitsPerCoordinate);
            float x = decode[0] + ((float)(rx / coordScale) * (decode[1] - decode[0]));
            float y = decode[2] + ((float)(ry / coordScale) * (decode[3] - decode[2]));
            var inputs = new float[k];
            for (int i = 0; i < k; i++)
            {
                int rc = reader.ReadBits(bitsPerComponent);
                inputs[i] = decode[4 + (2 * i)] + ((float)(rc / componentScale) * (decode[5 + (2 * i)] - decode[4 + (2 * i)]));
            }

            var outputs = new float[function.OutputCount];
            function.Evaluate(inputs, outputs);
            ColorToRgb(outputs, colorSpace, out byte r, out byte g, out byte b);
            return new MeshVertex(new Vector2(x, y), r, g, b);
        }

        if (type == 7)
        {
            var strip = new List<MeshVertex>();
            while (reader.RemainingBits >= bitsPerFlag + vertexBits)
            {
                int flag = bitsPerFlag > 0 ? reader.ReadBits(bitsPerFlag) : 1;
                if (flag != 0)
                {
                    if (reader.RemainingBits < 3 * vertexBits)
                    {
                        break;
                    }

                    strip.Clear();
                    strip.Add(ReadVertex());
                    strip.Add(ReadVertex());
                    strip.Add(ReadVertex());
                    triangles.Add(new MeshTriangle(strip[0], strip[1], strip[2]));
                }
                else
                {
                    if (strip.Count < 2 || reader.RemainingBits < vertexBits)
                    {
                        break;
                    }

                    strip.Add(ReadVertex());
                    int n = strip.Count;
                    triangles.Add(new MeshTriangle(strip[n - 3], strip[n - 2], strip[n - 1]));
                }
            }
        }
        else if (type == 4 || type == 6)
        {
            int per = type == 4 ? 16 : 4;
            var patch = new MeshVertex[per];
            bool started = false;
            while (reader.RemainingBits >= bitsPerFlag + vertexBits)
            {
                int flag = bitsPerFlag > 0 ? reader.ReadBits(bitsPerFlag) : 1;
                if (flag != 0 || !started)
                {
                    patch[0] = ReadVertex();
                }

                started = true;
                bool ok = true;
                for (int i = 1; i < per; i++)
                {
                    if (reader.RemainingBits < vertexBits)
                    {
                        ok = false;
                        break;
                    }

                    patch[i] = ReadVertex();
                }

                if (!ok)
                {
                    break;
                }

                EmitPatch(triangles, patch, tensor: type == 4);
            }
        }

        return triangles.Count > 0 ? triangles : null;
    }

    private static void EmitPatch(List<MeshTriangle> triangles, MeshVertex[] control, bool tensor)
    {
        const int Divisions = 8;
        var grid = new MeshVertex[Divisions + 1, Divisions + 1];
        for (int i = 0; i <= Divisions; i++)
        {
            for (int j = 0; j <= Divisions; j++)
            {
                float u = (float)i / Divisions;
                float v = (float)j / Divisions;
                grid[i, j] = tensor ? EvaluateTensorPatch(control, u, v) : EvaluateCoonsPatch(control, u, v);
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

    private static MeshVertex EvaluateTensorPatch(MeshVertex[] c, float u, float v)
    {
        float[] bu = BernsteinCubic(u);
        float[] bv = BernsteinCubic(v);
        float x = 0f, y = 0f, r = 0f, g = 0f, b = 0f;
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                float w = bu[i] * bv[j];
                MeshVertex p = c[(i * 4) + j];
                x += w * p.Position.X;
                y += w * p.Position.Y;
                r += w * p.R;
                g += w * p.G;
                b += w * p.B;
            }
        }

        return Vertex(x, y, r, g, b);
    }

    private static MeshVertex EvaluateCoonsPatch(MeshVertex[] c, float u, float v)
    {
        float x = (1 - u) * (1 - v) * c[0].Position.X + u * (1 - v) * c[1].Position.X + (1 - u) * v * c[2].Position.X + u * v * c[3].Position.X;
        float y = (1 - u) * (1 - v) * c[0].Position.Y + u * (1 - v) * c[1].Position.Y + (1 - u) * v * c[2].Position.Y + u * v * c[3].Position.Y;
        float r = (1 - u) * (1 - v) * c[0].R + u * (1 - v) * c[1].R + (1 - u) * v * c[2].R + u * v * c[3].R;
        float g = (1 - u) * (1 - v) * c[0].G + u * (1 - v) * c[1].G + (1 - u) * v * c[2].G + u * v * c[3].G;
        float b = (1 - u) * (1 - v) * c[0].B + u * (1 - v) * c[1].B + (1 - u) * v * c[2].B + u * v * c[3].B;
        return Vertex(x, y, r, g, b);
    }

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
