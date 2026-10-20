using System;
using Poso.Pdf.Objects;

namespace Poso.Pdf.Colors;

/// <summary>
/// CIE-based CalGray and CalRGB color spaces (ISO 32000-1 8.6.5.3 / 8.6.5.4) converted to sRGB.
/// Parameters come from the color space dictionary: /WhitePoint, /BlackPoint, /Gamma, /Matrix.
/// </summary>
public sealed class CalColor
{
    public float[] WhitePoint { get; init; } = { 0.9505f, 1f, 1.089f };

    public float[] BlackPoint { get; init; } = { 0f, 0f, 0f };

    public float[] Gamma { get; init; } = { 1f, 1f, 1f };

    /// <summary>3x3 row-major RGB-to-XYZ matrix; null means use the sRGB primaries matrix.</summary>
    public float[]? Matrix { get; init; }

    private static readonly float[] SrgbToXyz =
    {
        0.4124f, 0.3576f, 0.1805f,
        0.2126f, 0.7152f, 0.0722f,
        0.0193f, 0.1192f, 0.9505f,
    };

    /// <summary>
    /// Reads a CIE space's parameters out of its dictionary (ISO 32000-1 8.6.5.3/8.6.5.4): /WhitePoint,
    /// /BlackPoint, /Gamma (one number or three) and /Matrix. Whatever the file leaves out falls back to
    /// the defaults, which is what makes a bare `[/CalRGB << >>]` still render.
    /// </summary>
    public static CalColor FromDictionary(PdfDictionary? dictionary)
    {
        if (dictionary is null)
        {
            return new CalColor();
        }

        float[]? whitePoint = Floats(dictionary.GetArray("WhitePoint"));
        float[]? blackPoint = Floats(dictionary.GetArray("BlackPoint"));
        float[]? gamma = Floats(dictionary.GetArray("Gamma"));
        if (gamma is null && dictionary.Get("Gamma") is PdfNumber singleGamma)
        {
            float g = singleGamma.AsFloat();
            gamma = new[] { g, g, g };
        }

        float[]? matrix = Floats(dictionary.GetArray("Matrix"));
        return new CalColor
        {
            WhitePoint = whitePoint is { Length: >= 3 } ? whitePoint : new[] { 0.9505f, 1f, 1.089f },
            BlackPoint = blackPoint is { Length: >= 3 } ? blackPoint : new[] { 0f, 0f, 0f },
            Gamma = gamma is { Length: >= 3 } ? gamma : new[] { 1f, 1f, 1f },
            Matrix = matrix is { Length: >= 9 } ? matrix : null,
        };
    }

    private static float[]? Floats(PdfArray? array)
    {
        if (array is null || array.Count == 0)
        {
            return null;
        }

        var values = new float[array.Count];
        for (int i = 0; i < array.Count; i++)
        {
            values[i] = array.GetNumber(i)?.AsFloat() ?? 0f;
        }

        return values;
    }

    public PosoColorF ToRgb(ReadOnlySpan<float> values)
    {
        float[] matrix = Matrix ?? SrgbToXyz;
        float gamma = Gamma.Length > 0 ? Math.Max(0.1f, Gamma[0]) : 1f;

        if (values.Length == 1)
        {
            // CalGray: A = value^gamma, then luminance scaled between black and white point Y.
            float a = MathF.Pow(Math.Clamp(values[0], 0f, 1f), gamma);
            float wpY = Safe(WhitePoint, 1, 1f);
            float gy = (a * (wpY - Safe(BlackPoint, 1, 0f))) + Safe(BlackPoint, 1, 0f);
            float gx = wpY == 0f ? gy : gy * Safe(WhitePoint, 0, 0.9505f) / wpY;
            float gz = wpY == 0f ? gy : gy * Safe(WhitePoint, 2, 1.089f) / wpY;
            return Cielab.XyzToRgb(gx, gy, gz);
        }

        // CalRGB: per-component gamma, then RGB -> XYZ via Matrix normalized to the white point.
        Span<float> linear = stackalloc float[3];
        for (int i = 0; i < 3; i++)
        {
            float v = i < values.Length ? Math.Clamp(values[i], 0f, 1f) : 0f;
            float g = i < Gamma.Length ? Math.Max(0.1f, Gamma[i]) : 1f;
            linear[i] = MathF.Pow(v, g);
        }

        float x = matrix[0] * linear[0] + matrix[1] * linear[1] + matrix[2] * linear[2];
        float y = matrix[3] * linear[0] + matrix[4] * linear[1] + matrix[5] * linear[2];
        float z = matrix[6] * linear[0] + matrix[7] * linear[1] + matrix[8] * linear[2];

        // Normalize so that all-1 input maps to the white point (PDF 8.6.5.4).
        float wx = WhitePoint.Length > 0 ? WhitePoint[0] : 0.9505f;
        float wy = WhitePoint.Length > 1 ? WhitePoint[1] : 1f;
        float wz = WhitePoint.Length > 2 ? WhitePoint[2] : 1.089f;
        x *= wx / (matrix[0] + matrix[1] + matrix[2]);
        y *= wy / (matrix[3] + matrix[4] + matrix[5]);
        z *= wz / (matrix[6] + matrix[7] + matrix[8]);

        return Cielab.XyzToRgb(x, y, z);
    }

    private static float Safe(float[] array, int index, float fallback) =>
        index < array.Length ? array[index] : fallback;
}
