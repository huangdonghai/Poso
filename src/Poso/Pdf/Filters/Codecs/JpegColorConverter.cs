using System;
using Poso.Graphics;

namespace Poso.Pdf.Filters.Codecs;

/// <summary>
/// Colour conversion for the 4-component (CMYK / YCCK) output of <see cref="JpegDecoder"/>.
///
/// The YCCK→CMYK step mirrors pdf.js's <c>_convertYcckToCmyk</c> (its constants fold in the Adobe CMYK
/// inversion that native codecs such as Skia omit), and the CMYK→RGB step mirrors pdf.js's
/// <c>DeviceCmykCS</c> polynomial fit to the US Web Coated (SWOP) profile. Both are validated against
/// MuPDF reference samples (CMYK samples match within JPEG IDCT rounding; RGB tracks MuPDF's own
/// ICC-based conversion to within a few units per channel).
/// </summary>
internal static class JpegColorConverter
{
    /// <summary>
    /// Converts a decoded 4-component JPEG into tightly packed BGRA (premultiplied, opaque) pixels.
    /// Returns null when the image does not have exactly four components.
    /// </summary>
    public static byte[]? ToBgra32(JpegDecoder.Result image)
    {
        byte[]? cmyk = ToCmyk(image);
        if (cmyk is null)
        {
            return null;
        }

        var bgra = new byte[image.Width * image.Height * 4];
        CmykToBgra32(cmyk, bgra);
        return bgra;
    }

    /// <summary>
    /// Converts interleaved CMYK samples (W*H*4 bytes) into tightly packed BGRA (premultiplied,
    /// opaque) pixels using the shared DeviceCMYK conversion. Shared by the managed JPEG path and
    /// by back-ends that decode Adobe JPEGs themselves, so both produce identical colours.
    /// </summary>
    internal static void CmykToBgra32(ReadOnlySpan<byte> cmyk, byte[] bgra)
    {
        const float Inv255 = 1f / 255f;
        int count = bgra.Length >> 2;
        for (int i = 0; i < count; i++)
        {
            PosoColor rgb = PosoColor.FromCmyk(
                cmyk[i * 4] * Inv255, cmyk[i * 4 + 1] * Inv255, cmyk[i * 4 + 2] * Inv255, cmyk[i * 4 + 3] * Inv255);
            int o = i * 4;
            bgra[o] = rgb.B;       // premultiplied with alpha 255 == straight colour
            bgra[o + 1] = rgb.G;
            bgra[o + 2] = rgb.R;
            bgra[o + 3] = 255;
        }
    }

    /// <summary>
    /// Returns the interleaved CMYK samples (W*H*4 bytes) after applying the YCCK→CMYK conversion when
    /// the source is YCCK (Adobe transform 2); for plain CMYK the decoded planes are returned as-is.
    /// Returns null when the image does not have exactly four components.
    /// </summary>
    public static byte[]? ToCmyk(JpegDecoder.Result image)
    {
        if (image.ComponentCount != 4)
        {
            return null;
        }

        int w = image.Width;
        int h = image.Height;
        byte[] c = image.Planes[0];
        byte[] m = image.Planes[1];
        byte[] y = image.Planes[2];
        byte[] k = image.Planes[3];
        int count = w * h;

        var cmyk = new byte[count * 4];

        // YCCK (Adobe transform == 2): the first three planes are Y, Cb, Cr and must be turned into
        // CMY (this also applies the inversion). transform 0 / absent: the planes are already CMYK.
        if (image.AdobeTransform == 2)
        {
            for (int i = 0; i < count; i++)
            {
                float Y = c[i], Cb = m[i], Cr = y[i];
                cmyk[i * 4] = Clamp(YcckC(Y, Cr));
                cmyk[i * 4 + 1] = Clamp(YcckM(Y, Cb, Cr));
                cmyk[i * 4 + 2] = Clamp(YcckY(Y, Cb));
                cmyk[i * 4 + 3] = k[i];
            }
            return cmyk;
        }

        for (int i = 0; i < count; i++)
        {
            cmyk[i * 4] = c[i];
            cmyk[i * 4 + 1] = m[i];
            cmyk[i * 4 + 2] = y[i];
            cmyk[i * 4 + 3] = k[i];
        }
        return cmyk;
    }

    private static float YcckC(float y, float cr) => 434.456f - y - 1.402f * cr;
    private static float YcckM(float y, float cb, float cr) => 119.541f - y + 0.344f * cb + 0.714f * cr;
    private static float YcckY(float y, float cb) => 481.816f - y - 1.772f * cb;

    private static byte Clamp(float v)
    {
        int r = (int)MathF.Round(v);
        return (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
    }
}
