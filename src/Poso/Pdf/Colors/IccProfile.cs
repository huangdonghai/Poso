using System;

namespace Poso.Pdf.Colors;

/// <summary>
/// A minimal ICC profile reader/transform for the matrix/TRC subset: <c>RGB</c> (3-component) and
/// <c>GRAY</c> (1-component) profiles whose PCS is <c>XYZ</c> and that carry colorant XYZ + tone
/// reproduction curves (no A2B0/B2A4/mft CLUT). Such a profile maps device values to the D50 PCS via
/// per-channel curves and a colorant matrix; this class then chromatically adapts D50&#8594;D65 (Bradford)
/// and encodes to sRGB. Anything else (CMYK, CLUT, Lab, unusual) returns <see langword="null"/> from
/// <see cref="TryCreate"/> so callers keep their existing per-channel approximation.
/// </summary>
/// <remarks>
/// Validated against lcms2 (the engine MuPDF uses) to within a couple of units per channel on real
/// corpus profiles. Not a full ICC engine: rendering intents other than the adapted colorimetric path,
/// CLUT interpolation and gamut mapping are out of scope.
/// </remarks>
internal sealed class IccProfile
{
    private readonly int _components;
    private readonly Curve[] _curves;
    private readonly float[,] _deviceToLinearSrgb; // 3x3: linear sRGB = M * deviceLinear (RGB)
    private readonly float[] _grayToLinearSrgb;    // 3: linear sRGB = v * grayLinear (GRAY)

    private IccProfile(int components, Curve[] curves, float[,] matrix, float[] grayVector)
    {
        _components = components;
        _curves = curves;
        _deviceToLinearSrgb = matrix;
        _grayToLinearSrgb = grayVector;
    }

    public int Components => _components;

    /// <summary>Parses a matrix/TRC RGB or Gray ICC profile; returns null for unsupported profiles.</summary>
    public static IccProfile? TryCreate(byte[] data)
    {
        if (data is null || data.Length < 132)
        {
            return null;
        }

        // Header: color space (16..19), PCS (20..23), 'acsp' magic (36..39).
        if (ReadTag(data, 36) != "acsp")
        {
            return null;
        }

        string colorSpace = ReadTag(data, 16);
        string pcs = ReadTag(data, 20);
        if (pcs != "XYZ ")
        {
            return null;
        }

        int components = colorSpace switch
        {
            "RGB " => 3,
            "GRAY" => 1,
            _ => 0,
        };
        if (components == 0)
        {
            return null; // CMYK / Lab / other
        }

        var tags = ReadTagTable(data);

        // Reject CLUT-based profiles (they need A2B0/B2A4 interpolation we do not implement).
        if (tags.ContainsKey("A2B0") || tags.ContainsKey("A2B1") || tags.ContainsKey("A2B2") ||
            tags.ContainsKey("B2A0") || tags.ContainsKey("B2A1") || tags.ContainsKey("B2A2") ||
            tags.ContainsKey("B2A4") || tags.ContainsKey("mft1") || tags.ContainsKey("mft2") || tags.ContainsKey("mft3"))
        {
            return null;
        }

        if (!tags.TryGetValue("wtpt", out (int Off, int Size) wtpt) || !TryReadXyz(data, wtpt.Off, wtpt.Size, out _))
        {
            return null;
        }

        if (components == 3)
        {
            if (!tags.TryGetValue("rXYZ", out (int Off, int Size) rx) || !TryReadXyz(data, rx.Off, rx.Size, out float[] rXyz) ||
                !tags.TryGetValue("gXYZ", out (int Off, int Size) gx) || !TryReadXyz(data, gx.Off, gx.Size, out float[] gXyz) ||
                !tags.TryGetValue("bXYZ", out (int Off, int Size) bx) || !TryReadXyz(data, bx.Off, bx.Size, out float[] bXyz))
            {
                return null;
            }

            if (!TryReadCurve(data, tags, "rTRC", out Curve? rr) || rr is null ||
                !TryReadCurve(data, tags, "gTRC", out Curve? gg) || gg is null ||
                !TryReadCurve(data, tags, "bTRC", out Curve? bb) || bb is null)
            {
                return null;
            }

            // Colorant matrix (device linear -> XYZ D50), columns rXYZ/gXYZ/bXYZ.
            float[,] colorants = new float[3, 3]
            {
                { rXyz[0], gXyz[0], bXyz[0] },
                { rXyz[1], gXyz[1], bXyz[1] },
                { rXyz[2], gXyz[2], bXyz[2] },
            };
            float[,] m = Multiply(XyzToLinearSrgbAdapted(), colorants);
            return new IccProfile(3, new[] { rr, gg, bb }, m, Array.Empty<float>());
        }
        else
        {
            if (!TryReadCurve(data, tags, "kTRC", out Curve? k) || k is null)
            {
                return null;
            }

            // Gray: the neutral axis is the D50 PCS white, so linear sRGB = (adapted matrix * D50) * linear.
            float[] v = Mul(XyzToLinearSrgbAdapted(), PcsD50White);
            return new IccProfile(1, new[] { k }, new float[3, 3], v);
        }
    }

    /// <summary>Converts device components in [0,1] to sRGB bytes.</summary>
    public void ToRgb(ReadOnlySpan<float> components, out byte r, out byte g, out byte b)
    {
        if (_components == 1)
        {
            float lin = _curves[0].Evaluate(components[0]);
            r = EncodeSrgb(_grayToLinearSrgb[0] * lin);
            g = EncodeSrgb(_grayToLinearSrgb[1] * lin);
            b = EncodeSrgb(_grayToLinearSrgb[2] * lin);
            return;
        }

        Span<float> deviceLinear = stackalloc float[3];
        for (int i = 0; i < 3; i++)
        {
            deviceLinear[i] = _curves[i].Evaluate(i < components.Length ? components[i] : 0f);
        }

        r = EncodeSrgb(_deviceToLinearSrgb[0, 0] * deviceLinear[0] + _deviceToLinearSrgb[0, 1] * deviceLinear[1] + _deviceToLinearSrgb[0, 2] * deviceLinear[2]);
        g = EncodeSrgb(_deviceToLinearSrgb[1, 0] * deviceLinear[0] + _deviceToLinearSrgb[1, 1] * deviceLinear[1] + _deviceToLinearSrgb[1, 2] * deviceLinear[2]);
        b = EncodeSrgb(_deviceToLinearSrgb[2, 0] * deviceLinear[0] + _deviceToLinearSrgb[2, 1] * deviceLinear[1] + _deviceToLinearSrgb[2, 2] * deviceLinear[2]);
    }

    /// <summary>Converts 8-bit device components to sRGB bytes.</summary>
    public void ToRgb(ReadOnlySpan<byte> components, out byte r, out byte g, out byte b)
    {
        Span<float> norm = stackalloc float[_components];
        for (int i = 0; i < _components; i++)
        {
            norm[i] = components[i] / 255f;
        }

        ToRgb(norm, out r, out g, out b);
    }

    // ---- ICC parsing helpers ----

    private static string ReadTag(byte[] d, int offset) =>
        offset + 4 <= d.Length ? new string(new[] { (char)d[offset], (char)d[offset + 1], (char)d[offset + 2], (char)d[offset + 3] }) : string.Empty;

    private static Dictionary<string, (int Off, int Size)> ReadTagTable(byte[] d)
    {
        var map = new Dictionary<string, (int Off, int Size)>(StringComparer.Ordinal);
        int count = (int)ReadUInt32(d, 128);
        for (int i = 0; i < count; i++)
        {
            int entry = 132 + i * 12;
            if (entry + 12 > d.Length)
            {
                break;
            }

            string sig = ReadTag(d, entry);
            int off = (int)ReadUInt32(d, entry + 4);
            int size = (int)ReadUInt32(d, entry + 8);
            map[sig] = (off, size);
        }

        return map;
    }

    private static uint ReadUInt32(byte[] d, int o) =>
        o + 4 <= d.Length ? ((uint)d[o] << 24 | (uint)d[o + 1] << 16 | (uint)d[o + 2] << 8 | d[o + 3]) : 0u;

    private static int ReadInt32(byte[] d, int o) => unchecked((int)ReadUInt32(d, o));

    private static bool TryReadXyz(byte[] d, int tagOffset, int size, out float[] xyz)
    {
        xyz = Array.Empty<float>();
        if (ReadTag(d, tagOffset) != "XYZ ")
        {
            return false;
        }

        // Standard XYZType carries a count field (sig+reserved+count+XYZ = 24 bytes for one entry),
        // but many real profiles omit the count (bare sig+reserved+XYZ = 20 bytes). Pick the base by size.
        int baseOffset = size >= 24 ? tagOffset + 12 : tagOffset + 8;
        if (baseOffset + 12 > d.Length)
        {
            return false;
        }

        int x = ReadInt32(d, baseOffset);
        int y = ReadInt32(d, baseOffset + 4);
        int z = ReadInt32(d, baseOffset + 8);
        xyz = new[] { x / 65536f, y / 65536f, z / 65536f };
        return true;
    }

    private static bool TryReadCurve(byte[] d, Dictionary<string, (int Off, int Size)> tags, string sig, out Curve? curve)
    {
        curve = null;
        if (!tags.TryGetValue(sig, out (int Off, int Size) entry) || entry.Off + 8 > d.Length)
        {
            return false;
        }

        int off = entry.Off;
        string type = ReadTag(d, off);
        if (type == "curv")
        {
            int count = (int)ReadUInt32(d, off + 8);
            if (count == 0)
            {
                curve = Curve.Linear();
                return true;
            }

            if (count == 1)
            {
                // A single-entry curveType stores gamma as a u8Fixed8 number (2 bytes, value/256).
                float gamma = (d[off + 12] << 8 | d[off + 13]) / 256f;
                curve = Curve.Gamma(gamma);
                return true;
            }

            ushort[] table = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                int p = off + 12 + i * 2;
                if (p + 2 > d.Length)
                {
                    return false;
                }

                table[i] = (ushort)(d[p] << 8 | d[p + 1]);
            }

            curve = Curve.Table(table);
            return true;
        }

        if (type == "para")
        {
            int fn = (ushort)(d[off + 8] << 8 | d[off + 9]);
            float[] prm = new float[7];
            for (int i = 0; i < 7 && off + 12 + i * 4 + 4 <= d.Length; i++)
            {
                prm[i] = ReadInt32(d, off + 12 + i * 4) / 65536f;
            }

            curve = Curve.Parametric(fn, prm);
            return true;
        }

        return false;
    }

    // ---- Colorimetry ----

    /// <summary>
    /// The 3x3 matrix mapping XYZ (D50 PCS) to linear sRGB (D65 primaries): sRGB's matrix with a Bradford
    /// D50&#8594;D65 adaptation folded in (the canonical form lcms2 uses), so no separate adaptation step is needed.
    /// </summary>
    private static float[,] XyzToLinearSrgbAdapted() => new float[3, 3]
    {
        { 3.1338561f, -1.6168667f, -0.4906146f },
        { -0.9787684f, 1.9161415f, 0.0334540f },
        { 0.0719453f, -0.2272184f, 1.4250584f },
    };

    private static readonly float[] PcsD50White = { 0.9642f, 1.0f, 0.8249f };

    private static float[,] Multiply(float[,] a, float[,] b)
    {
        var r = new float[3, 3];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                r[i, j] = a[i, 0] * b[0, j] + a[i, 1] * b[1, j] + a[i, 2] * b[2, j];
            }
        }

        return r;
    }

    private static float[] Mul(float[,] m, float[] v) =>
        new[] { m[0, 0] * v[0] + m[0, 1] * v[1] + m[0,2] * v[2], m[1, 0] * v[0] + m[1, 1] * v[1] + m[1, 2] * v[2], m[2, 0] * v[0] + m[2, 1] * v[1] + m[2, 2] * v[2] };

    private static byte EncodeSrgb(float linear)
    {
        float c = linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(Math.Max(linear, 0f), 1f / 2.4f) - 0.055f;
        return (byte)Math.Clamp((int)MathF.Round(Math.Clamp(c, 0f, 1f) * 255f), 0, 255);
    }
}
