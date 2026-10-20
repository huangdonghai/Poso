using System;

namespace Poso.Pdf.Colors;

/// <summary>
/// CIE L*a*b* to sRGB conversion (ISO 32000-1 8.6.5.5). The white point is given as normalized
/// XYZ (Y component expected as 1); PDF declares CIELab against the D50 standard illuminant, so that is
/// also the fallback when a dictionary leaves /WhitePoint out.
/// </summary>
public static class Cielab
{
    private const float Epsilon = 0.008856f; // (6/29)^3
    private const float Kappa = 903.3f;      // (29/3)^3

    private const float D65X = 0.9505f;
    private const float D65Z = 1.089f;

    /// <summary>The D50 reference white CIELab is defined against, and the ICC profile connection space.</summary>
    public static readonly float[] DefaultWhitePoint = [0.9642f, 1f, 0.8249f];

    /// <summary>
    /// Brings normalized XYZ measured against <paramref name="whitePoint"/> onto the D65 white that
    /// <see cref="XyzToRgb"/> expects. Every CIE-based PDF space declares its own white point, and scaling
    /// XYZ by it and then applying the sRGB primaries leaves a cast - a white point that is not D65 has to
    /// be brought together with it first. A diagonal von Kries scaling is what the reference
    /// implementations do about it, and it leaves a colour already measured against D65 unchanged.
    /// </summary>
    internal static void AdaptToD65(ref float x, ref float y, ref float z, ReadOnlySpan<float> whitePoint)
    {
        float xn = whitePoint.Length >= 3 ? whitePoint[0] : DefaultWhitePoint[0];
        float yn = whitePoint.Length >= 3 ? whitePoint[1] : DefaultWhitePoint[1];
        float zn = whitePoint.Length >= 3 ? whitePoint[2] : DefaultWhitePoint[2];

        if (xn > 0f && yn > 0f && zn > 0f)
        {
            x *= D65X / xn;
            y /= yn;
            z *= D65Z / zn;
        }
    }

    public static PosoColorF ToRgb(float l, float a, float b, ReadOnlySpan<float> whitePoint)
    {
        float xn = whitePoint.Length >= 3 ? whitePoint[0] : DefaultWhitePoint[0];
        float yn = whitePoint.Length >= 3 ? whitePoint[1] : DefaultWhitePoint[1];
        float zn = whitePoint.Length >= 3 ? whitePoint[2] : DefaultWhitePoint[2];

        float fy = (l + 16f) / 116f;
        float fx = fy + (a / 500f);
        float fz = fy - (b / 200f);

        float x = xn * InvertF(fx);
        float y = yn * InvertF(fy);
        float z = zn * InvertF(fz);

        // CIELab in a PDF is measured against the D50 standard illuminant (that is also the fallback above)
        // while sRGB is defined on D65, so the two whites have to be brought together before the primaries
        // are applied. Scaling XYZ by the file's white point and then using the D65 matrix left every Lab
        // colour with a yellow cast: pdf.js issue10339_reduced.pdf lost 68.6% of its pixels that way, its
        // pale blue reading (21,120,147) where MuPDF and PDFium both paint (0,121,169), and even the file's
        // own declared white came out (255,243,215) instead of (245,246,248).
        AdaptToD65(ref x, ref y, ref z, whitePoint);

        return XyzToRgb(x, y, z);
    }

    /// <summary>Converts normalized CIE XYZ (D65) to a linear 0..1 sRGB triple.</summary>
    internal static PosoColorF XyzToRgb(float x, float y, float z)
    {
        float r = (3.2406f * x) + (-1.5372f * y) + (-0.4986f * z);
        float g = (-0.9689f * x) + (1.8758f * y) + (0.0415f * z);
        float bl = (0.0557f * x) + (-0.204f * y) + (1.057f * z);
        return new PosoColorF(ToSrgb(r), ToSrgb(g), ToSrgb(bl));
    }

    // Inverse of the CIE f(t) component function: t^3 above the linear threshold, else the linear form.
    private static float InvertF(float t)
    {
        float t3 = t * t * t;
        return t3 > Epsilon ? t3 : (t - (16f / 116f)) * (1f / Kappa);
    }

    private static float ToSrgb(float linear)
    {
        float c = Math.Clamp(linear, 0f, 1f);
        float encoded = c <= 0.0031308f ? 12.92f * c : (1.055f * MathF.Pow(c, 1f / 2.4f)) - 0.055f;
        return Math.Clamp(encoded, 0f, 1f);
    }
}

/// <summary>Linear 0..1 RGB triple (float), distinct from the byte <c>PosoColor</c>.</summary>
public readonly record struct PosoColorF(float R, float G, float B)
{
    public static implicit operator Graphics.PosoColor(PosoColorF c) =>
        new((byte)Math.Round(Math.Clamp(c.R, 0f, 1f) * 255f),
            (byte)Math.Round(Math.Clamp(c.G, 0f, 1f) * 255f),
            (byte)Math.Round(Math.Clamp(c.B, 0f, 1f) * 255f));
}
