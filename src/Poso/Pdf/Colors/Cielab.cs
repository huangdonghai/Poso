using System;

namespace Poso.Pdf.Colors;

/// <summary>
/// CIE L*a*b* to sRGB conversion (ISO 32000-1 8.6.5.5). The white point is given as normalized
/// XYZ (Y component expected as 1); the D65 default is { 0.9505, 1.0, 1.089 }.
/// </summary>
public static class Cielab
{
    private const float Epsilon = 0.008856f; // (6/29)^3
    private const float Kappa = 903.3f;      // (29/3)^3

    public static PosoColorF ToRgb(float l, float a, float b, ReadOnlySpan<float> whitePoint)
    {
        float xn = whitePoint.Length >= 3 ? whitePoint[0] : 0.9505f;
        float yn = whitePoint.Length >= 3 ? whitePoint[1] : 1f;
        float zn = whitePoint.Length >= 3 ? whitePoint[2] : 1.089f;

        float fy = (l + 16f) / 116f;
        float fx = fy + (a / 500f);
        float fz = fy - (b / 200f);

        float x = xn * InvertF(fx);
        float y = yn * InvertF(fy);
        float z = zn * InvertF(fz);

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
