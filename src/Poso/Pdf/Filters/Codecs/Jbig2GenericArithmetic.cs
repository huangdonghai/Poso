namespace Poso.Pdf.Filters.Codecs;

/// <summary>
/// JBIG2 arithmetic-coded generic region decoder (ITU-T T.88 §6.2.5), ported faithfully from the
/// reference implementation <c>jbig2_generic.c</c> (Artifex jbig2dec — the upstream of MuPDF and pdf.js).
/// Covers the four optimized template kernels (GBTEMPLATE 0..3) with their canonical, byte-parallel context
/// packing. Restricted to the common case of a default adaptive-template (GBAT) layout and typical
/// prediction OFF; anything else returns null so the caller degrades rather than mis-renders. Output is a
/// row-major, MSB-first bit-packed buffer where 1 = black, matching MuPDF's JBIG2 rendering.
/// </summary>
internal static class Jbig2GenericArithmetic
{
    private static readonly sbyte[] DefaultGbat0 = [+3, -1, -3, -1, +2, -2, -2, -2];
    private static readonly sbyte[] DefaultGbatOther = [+2, -1];
    private static readonly sbyte[] DefaultGbat1 = [+3, -1];

    /// <summary>
    /// Decodes an arithmetic generic region of <paramref name="width"/>×<paramref name="height"/> from the
    /// codestream slice. Returns null when the template / GBAT / typical-prediction combination is outside
    /// the supported (default-AT, no-TP) subset.
    /// </summary>
    public static byte[]? Decode(int template, bool tpDon, sbyte[] gbat, byte[] data, int start, int end, int width, int height)
    {
        if (template < 0 || template > 3)
        {
            return null;
        }

        if (width <= 0 || height <= 0)
        {
            return null;
        }

        int statsSize = template == 0 ? 1 << 16 : template == 1 ? 1 << 13 : 1 << 10;
        var mq = new Jbig2MqDecoder(data, start, end);
        byte[] stats = new byte[statsSize];
        int stride = (width + 7) / 8;
        byte[] image = new byte[stride * height];

        if (tpDon)
        {
            // Typical prediction: each row first decodes an LTP flag from a dedicated context; when set the
            // row is a copy of the previous row, otherwise pixels decode with the same context as the
            // GBAT-aware unoptimized kernel.
            if (!GbatInField(gbat, template == 0 ? 8 : 2))
            {
                return null;
            }

            switch (template)
            {
                case 0: DecodeTemplate0Tpgdon(mq, stats, image, gbat, width, height, stride); break;
                case 1: DecodeTemplate1Tpgdon(mq, stats, image, gbat, width, height, stride); break;
                case 2: DecodeTemplate2Tpgdon(mq, stats, image, gbat, width, height, stride); break;
                default: DecodeTemplate3Tpgdon(mq, stats, image, gbat, width, height, stride); break;
            }

            return image;
        }

        bool defaultGbat = MatchesDefaultGbat(template, gbat);
        switch (template)
        {
            case 0:
                if (defaultGbat) { DecodeTemplate0(mq, stats, image, width, height, stride); }
                else { if (!GbatInField(gbat, 8)) return null; DecodeTemplate0Unopt(mq, stats, image, gbat, width, height, stride); }
                break;
            case 1:
                if (defaultGbat) { DecodeTemplate1(mq, stats, image, width, height, stride); }
                else { if (!GbatInField(gbat, 2)) return null; DecodeTemplate1Unopt(mq, stats, image, gbat, width, height, stride); }
                break;
            case 2:
                if (defaultGbat) { DecodeTemplate2(mq, stats, image, width, height, stride); }
                else { if (!GbatInField(gbat, 2)) return null; DecodeTemplate2Unopt(mq, stats, image, gbat, width, height, stride); }
                break;
            case 3:
                if (defaultGbat) { DecodeTemplate3(mq, stats, image, width, height, stride); }
                else { if (!GbatInField(gbat, 2)) return null; DecodeTemplate3Unopt(mq, stats, image, gbat, width, height, stride); }
                break;
            default:
                return null;
        }

        return image;
    }

    /// <summary>
    /// Decodes a generic region into a FRESH width×height bitmap using an EXTERNAL shared MQ decoder
    /// and stats array (no typical prediction). Used by the gray-scale bitplanes of halftone regions and
    /// the collective bitmap of pattern dictionaries, where multiple regions share one coding context.
    /// Returns null when the gbat is out of the causal field. 1-bit output is 1=black.
    /// </summary>
    public static byte[]? DecodeShared(int template, sbyte[] gbat, Jbig2MqDecoder mq, byte[] stats, int width, int height)
    {
        if (template < 0 || template > 3 || width <= 0 || height <= 0)
        {
            return null;
        }

        int stride = (width + 7) / 8;
        byte[] image = new byte[stride * height];
        bool defaultGbat = MatchesDefaultGbat(template, gbat);
        switch (template)
        {
            case 0:
                if (defaultGbat) { DecodeTemplate0(mq, stats, image, width, height, stride); }
                else { if (!GbatInField(gbat, 8)) return null; DecodeTemplate0Unopt(mq, stats, image, gbat, width, height, stride); }
                break;
            case 1:
                if (defaultGbat) { DecodeTemplate1(mq, stats, image, width, height, stride); }
                else { if (!GbatInField(gbat, 2)) return null; DecodeTemplate1Unopt(mq, stats, image, gbat, width, height, stride); }
                break;
            case 2:
                if (defaultGbat) { DecodeTemplate2(mq, stats, image, width, height, stride); }
                else { if (!GbatInField(gbat, 2)) return null; DecodeTemplate2Unopt(mq, stats, image, gbat, width, height, stride); }
                break;
            default:
                if (defaultGbat) { DecodeTemplate3(mq, stats, image, width, height, stride); }
                else { if (!GbatInField(gbat, 2)) return null; DecodeTemplate3Unopt(mq, stats, image, gbat, width, height, stride); }
                break;
        }
        return image;
    }

    /// <summary>
    /// Like <see cref="DecodeShared"/> but honors a per-pixel skip mask (§6.2.5.1 / halftone §6.6.5.1): where the
    /// <paramref name="skip"/> bitmap (same width×height, MSB-first) has a set bit the output pixel is forced to 0
    /// WITHOUT consuming arithmetic code, yet still participates in subsequent pixels' contexts. Uses the
    /// per-pixel unoptimized kernels with the supplied GBAT (which the halftone path sets to the template default).
    /// </summary>
    public static byte[]? DecodeSharedWithSkip(int template, sbyte[] gbat, Jbig2MqDecoder mq, byte[] stats, int width, int height, byte[] skip, int skipStride)
    {
        if (template < 0 || template > 3 || width <= 0 || height <= 0)
        {
            return null;
        }

        int stride = (width + 7) / 8;
        byte[] image = new byte[stride * height];
        switch (template)
        {
            case 0: DecodeTemplate0Unopt(mq, stats, image, gbat, width, height, stride, skip, skipStride); break;
            case 1: DecodeTemplate1Unopt(mq, stats, image, gbat, width, height, stride, skip, skipStride); break;
            case 2: DecodeTemplate2Unopt(mq, stats, image, gbat, width, height, stride, skip, skipStride); break;
            default: DecodeTemplate3Unopt(mq, stats, image, gbat, width, height, stride, skip, skipStride); break;
        }
        return image;
    }

    // pixel_outside_field (jbig2dec): adaptive template pixels must lie within the allowed causal field.
    internal static bool GbatInField(sbyte[] gbat, int count)
    {
        for (int i = 0; i + 1 < count; i += 2)
        {
            int x = gbat[i];
            int y = gbat[i + 1];
            if (y < -128 || y > 0 || x < -128 || (y < 0 && x > 127) || (y == 0 && x >= 0))
            {
                return false;
            }
        }

        return true;
    }

    private static int GetPixel(byte[] image, int width, int height, int stride, int x, int y) =>
        (uint)x < (uint)width && (uint)y < (uint)height
            ? (image[y * stride + (x >> 3)] >> (7 - (x & 7))) & 1
            : 0;

    internal static bool MatchesDefaultGbat(int template, sbyte[] gbat)
    {
        if (template == 0)
        {
            for (int i = 0; i < 8; i++)
            {
                if (gbat[i] != DefaultGbat0[i])
                {
                    return false;
                }
            }

            return true;
        }

        sbyte[] expected = template == 1 ? DefaultGbat1 : DefaultGbatOther;
        return gbat[0] == expected[0] && gbat[1] == expected[1];
    }

    internal static void DecodeTemplate0(Jbig2MqDecoder mq, byte[] stats, byte[] image, int gbWidth, int gbHeight, int stride)
    {
        int paddedWidth = (gbWidth + 7) & ~7;
        int line1 = -1;
        int line2 = -1;

        for (int y = 0; y < gbHeight; y++)
        {
            uint lineM1 = line1 >= 0 ? image[line1] : 0u;
            uint lineM2 = line2 >= 0 ? (uint)(image[line2] << 6) : 0u;
            uint context = (lineM1 & 0x7F0) | (lineM2 & 0xF800);
            int gbregLine = y * stride;

            for (int x = 0; x < paddedWidth; x += 8)
            {
                byte result = 0;
                int minorWidth = gbWidth - x > 8 ? 8 : gbWidth - x;

                if (line1 >= 0)
                {
                    lineM1 = (lineM1 << 8) | (x + 8 < gbWidth ? image[line1 + (x >> 3) + 1] : 0u);
                }

                if (line2 >= 0)
                {
                    lineM2 = (lineM2 << 8) | (x + 8 < gbWidth ? (uint)(image[line2 + (x >> 3) + 1] << 6) : 0u);
                }

                for (int xMinor = 0; xMinor < minorWidth; xMinor++)
                {
                    int bit = mq.Decode(ref stats[context]);
                    result |= (byte)(bit << (7 - xMinor));
                    context = ((context & 0x7BF7) << 1)
                        | (uint)bit
                        | ((lineM1 >> (7 - xMinor)) & 0x10)
                        | ((lineM2 >> (7 - xMinor)) & 0x800);
                }

                image[gbregLine + (x >> 3)] = result;
            }

            line2 = line1;
            line1 = gbregLine;
        }
    }

    internal static void DecodeTemplate1(Jbig2MqDecoder mq, byte[] stats, byte[] image, int gbWidth, int gbHeight, int stride)
    {
        int paddedWidth = (gbWidth + 7) & ~7;
        int line1 = -1;
        int line2 = -1;

        for (int y = 0; y < gbHeight; y++)
        {
            uint lineM1 = line1 >= 0 ? image[line1] : 0u;
            uint lineM2 = line2 >= 0 ? (uint)(image[line2] << 5) : 0u;
            uint context = ((lineM1 >> 1) & 0x1F8) | ((lineM2 >> 1) & 0x1E00);
            int gbregLine = y * stride;

            for (int x = 0; x < paddedWidth; x += 8)
            {
                byte result = 0;
                int minorWidth = gbWidth - x > 8 ? 8 : gbWidth - x;

                if (line1 >= 0)
                {
                    lineM1 = (lineM1 << 8) | (x + 8 < gbWidth ? image[line1 + (x >> 3) + 1] : 0u);
                }

                if (line2 >= 0)
                {
                    lineM2 = (lineM2 << 8) | (x + 8 < gbWidth ? (uint)(image[line2 + (x >> 3) + 1] << 5) : 0u);
                }

                for (int xMinor = 0; xMinor < minorWidth; xMinor++)
                {
                    int bit = mq.Decode(ref stats[context]);
                    result |= (byte)(bit << (7 - xMinor));
                    context = ((context & 0xEFB) << 1)
                        | (uint)bit
                        | ((lineM1 >> (8 - xMinor)) & 0x8)
                        | ((lineM2 >> (8 - xMinor)) & 0x200);
                }

                image[gbregLine + (x >> 3)] = result;
            }

            line2 = line1;
            line1 = gbregLine;
        }
    }

    internal static void DecodeTemplate2(Jbig2MqDecoder mq, byte[] stats, byte[] image, int gbWidth, int gbHeight, int stride)
    {
        int paddedWidth = (gbWidth + 7) & ~7;
        int line1 = -1;
        int line2 = -1;

        for (int y = 0; y < gbHeight; y++)
        {
            uint lineM1 = line1 >= 0 ? image[line1] : 0u;
            uint lineM2 = line2 >= 0 ? (uint)(image[line2] << 4) : 0u;
            uint context = ((lineM1 >> 3) & 0x7C) | ((lineM2 >> 3) & 0x380);
            int gbregLine = y * stride;

            for (int x = 0; x < paddedWidth; x += 8)
            {
                byte result = 0;
                int minorWidth = gbWidth - x > 8 ? 8 : gbWidth - x;

                if (line1 >= 0)
                {
                    lineM1 = (lineM1 << 8) | (x + 8 < gbWidth ? image[line1 + (x >> 3) + 1] : 0u);
                }

                if (line2 >= 0)
                {
                    lineM2 = (lineM2 << 8) | (x + 8 < gbWidth ? (uint)(image[line2 + (x >> 3) + 1] << 4) : 0u);
                }

                for (int xMinor = 0; xMinor < minorWidth; xMinor++)
                {
                    int bit = mq.Decode(ref stats[context]);
                    result |= (byte)(bit << (7 - xMinor));
                    context = ((context & 0x1BD) << 1)
                        | (uint)bit
                        | ((lineM1 >> (10 - xMinor)) & 0x4)
                        | ((lineM2 >> (10 - xMinor)) & 0x80);
                }

                image[gbregLine + (x >> 3)] = result;
            }

            line2 = line1;
            line1 = gbregLine;
        }
    }

    internal static void DecodeTemplate3(Jbig2MqDecoder mq, byte[] stats, byte[] image, int gbWidth, int gbHeight, int stride)
    {
        int paddedWidth = (gbWidth + 7) & ~7;
        int line1 = -1;

        for (int y = 0; y < gbHeight; y++)
        {
            uint lineM1 = line1 >= 0 ? image[line1] : 0u;
            uint context = (lineM1 >> 1) & 0x3F0;
            int gbregLine = y * stride;

            for (int x = 0; x < paddedWidth; x += 8)
            {
                byte result = 0;
                int minorWidth = gbWidth - x > 8 ? 8 : gbWidth - x;

                if (line1 >= 0)
                {
                    lineM1 = (lineM1 << 8) | (x + 8 < gbWidth ? image[line1 + (x >> 3) + 1] : 0u);
                }

                for (int xMinor = 0; xMinor < minorWidth; xMinor++)
                {
                    int bit = mq.Decode(ref stats[context]);
                    result |= (byte)(bit << (7 - xMinor));
                    context = ((context & 0x1F7) << 1)
                        | (uint)bit
                        | ((lineM1 >> (8 - xMinor)) & 0x10);
                }

                image[gbregLine + (x >> 3)] = result;
            }

            line1 = gbregLine;
        }
    }

    // Unoptimized template kernels (jbig2_decode_generic_templateN_unopt): honor arbitrary GBAT offsets.
    // Ported verbatim from jbig2dec, including the pd/ppd sliding windows over the two previous rows and the
    // adaptive pixels read directly from the image (out-of-field pixels read as 0).
    internal static void DecodeTemplate0Unopt(Jbig2MqDecoder mq, byte[] stats, byte[] image, sbyte[] gbat, int gbWidth, int gbHeight, int stride, byte[]? skip = null, int skipStride = 0)
    {
        for (int y = 0; y < gbHeight; y++)
        {
            uint outByte = 0;
            int outBits = 8;
            int d = y * stride;
            uint pd = 0, ppd = 0;
            int pline = -1, ppline = -1;
            if (y >= 1) { pline = (y - 1) * stride; pd = (uint)(image[pline] << 8); pline++; if (gbWidth > 8) { pd |= image[pline]; pline++; } }
            if (y >= 2) { ppline = (y - 2) * stride; ppd = (uint)(image[ppline] << 8); ppline++; if (gbWidth > 8) { ppd |= image[ppline]; ppline++; } }

            for (int x = 0; x < gbWidth; x++)
            {
                uint context = outByte & 0x000F;
                context |= (uint)GetPixel(image, gbWidth, gbHeight, stride, x + gbat[0], y + gbat[1]) << 4;
                context |= (pd >> 8) & 0x03E0;
                context |= (uint)GetPixel(image, gbWidth, gbHeight, stride, x + gbat[2], y + gbat[3]) << 10;
                context |= (uint)GetPixel(image, gbWidth, gbHeight, stride, x + gbat[4], y + gbat[5]) << 11;
                context |= (ppd >> 2) & 0x7000;
                context |= (uint)GetPixel(image, gbWidth, gbHeight, stride, x + gbat[6], y + gbat[7]) << 15;

                int bit;
                if (skip is not null && GetPixel(skip, gbWidth, gbHeight, skipStride, x, y) != 0)
                {
                    bit = 0; // §6.2.5.1: a skipped pixel is forced to 0 and not arithmetic-decoded
                }
                else
                {
                    bit = mq.Decode(ref stats[context]);
                }
                pd <<= 1;
                ppd <<= 1;
                outByte = (outByte << 1) | (uint)bit;
                outBits--;
                image[d] = (byte)(outByte << outBits);
                if (outBits == 0)
                {
                    outBits = 8;
                    d++;
                    if (x + 9 < gbWidth && pline >= 0) { pd |= image[pline]; pline++; if (ppline >= 0) { ppd |= image[ppline]; ppline++; } }
                }
            }

            if (outBits != 8) image[d] = (byte)(outByte << outBits);
        }
    }

    internal static void DecodeTemplate1Unopt(Jbig2MqDecoder mq, byte[] stats, byte[] image, sbyte[] gbat, int gbWidth, int gbHeight, int stride, byte[]? skip = null, int skipStride = 0)
    {
        for (int y = 0; y < gbHeight; y++)
        {
            uint outByte = 0;
            int outBits = 8;
            int d = y * stride;
            uint pd = 0, ppd = 0;
            int pline = -1, ppline = -1;
            if (y >= 1) { pline = (y - 1) * stride; pd = (uint)(image[pline] << 8); pline++; if (gbWidth > 8) { pd |= image[pline]; pline++; } }
            if (y >= 2) { ppline = (y - 2) * stride; ppd = (uint)(image[ppline] << 8); ppline++; if (gbWidth > 8) { ppd |= image[ppline]; ppline++; } }

            for (int x = 0; x < gbWidth; x++)
            {
                uint context = outByte & 0x0007;
                context |= (uint)GetPixel(image, gbWidth, gbHeight, stride, x + gbat[0], y + gbat[1]) << 3;
                context |= (pd >> 9) & 0x01F0;
                context |= (ppd >> 4) & 0x1E00;

                int bit;
                if (skip is not null && GetPixel(skip, gbWidth, gbHeight, skipStride, x, y) != 0)
                {
                    bit = 0;
                }
                else
                {
                    bit = mq.Decode(ref stats[context]);
                }
                pd <<= 1;
                ppd <<= 1;
                outByte = (outByte << 1) | (uint)bit;
                outBits--;
                image[d] = (byte)(outByte << outBits);
                if (outBits == 0)
                {
                    outBits = 8;
                    d++;
                    if (x + 9 < gbWidth && pline >= 0) { pd |= image[pline]; pline++; if (ppline >= 0) { ppd |= image[ppline]; ppline++; } }
                }
            }

            if (outBits != 8) image[d] = (byte)(outByte << outBits);
        }
    }

    internal static void DecodeTemplate2Unopt(Jbig2MqDecoder mq, byte[] stats, byte[] image, sbyte[] gbat, int gbWidth, int gbHeight, int stride, byte[]? skip = null, int skipStride = 0)
    {
        for (int y = 0; y < gbHeight; y++)
        {
            uint outByte = 0;
            int outBits = 8;
            int d = y * stride;
            uint pd = 0, ppd = 0;
            int pline = -1, ppline = -1;
            if (y > 0)
            {
                pline = (y - 1) * stride;
                pd = (uint)(image[pline] << 8); pline++;
                if (gbWidth > 8) { pd |= image[pline]; pline++; }
                if (y > 1)
                {
                    ppline = (y - 2) * stride;
                    ppd = (uint)(image[ppline] << 8); ppline++;
                    if (gbWidth > 8) { ppd |= image[ppline]; ppline++; }
                }
            }

            for (int x = 0; x < gbWidth; x++)
            {
                uint context = outByte & 0x0003;
                context |= (uint)GetPixel(image, gbWidth, gbHeight, stride, x + gbat[0], y + gbat[1]) << 2;
                context |= (pd >> 11) & 0x078;
                context |= (ppd >> 7) & 0x380;

                int bit;
                if (skip is not null && GetPixel(skip, gbWidth, gbHeight, skipStride, x, y) != 0)
                {
                    bit = 0;
                }
                else
                {
                    bit = mq.Decode(ref stats[context]);
                }
                pd <<= 1;
                ppd <<= 1;
                outByte = (outByte << 1) | (uint)bit;
                outBits--;
                image[d] = (byte)(outByte << outBits);
                if (outBits == 0)
                {
                    outBits = 8;
                    d++;
                    if (x + 9 < gbWidth && pline >= 0) { pd |= image[pline]; pline++; if (ppline >= 0) { ppd |= image[ppline]; ppline++; } }
                }
            }

            if (outBits != 8) image[d] = (byte)(outByte << outBits);
        }
    }

    internal static void DecodeTemplate3Unopt(Jbig2MqDecoder mq, byte[] stats, byte[] image, sbyte[] gbat, int gbWidth, int gbHeight, int stride, byte[]? skip = null, int skipStride = 0)
    {
        for (int y = 0; y < gbHeight; y++)
        {
            uint outByte = 0;
            int outBits = 8;
            int d = y * stride;
            uint pd = 0;
            int pline = -1;
            if (y > 0) { pline = (y - 1) * stride; pd = (uint)(image[pline] << 8); pline++; if (gbWidth > 8) { pd |= image[pline]; pline++; } }

            for (int x = 0; x < gbWidth; x++)
            {
                uint context = outByte & 0x000F;
                context |= (uint)GetPixel(image, gbWidth, gbHeight, stride, x + gbat[0], y + gbat[1]) << 4;
                context |= (pd >> 9) & 0x3E0;

                int bit;
                if (skip is not null && GetPixel(skip, gbWidth, gbHeight, skipStride, x, y) != 0)
                {
                    bit = 0;
                }
                else
                {
                    bit = mq.Decode(ref stats[context]);
                }
                pd <<= 1;
                outByte = (outByte << 1) | (uint)bit;
                outBits--;
                image[d] = (byte)(outByte << outBits);
                if (outBits == 0)
                {
                    outBits = 8;
                    d++;
                    if (x + 9 < gbWidth && pline >= 0) { pd |= image[pline]; pline++; }
                }
            }

            if (outBits != 8) image[d] = (byte)(outByte << outBits);
        }
    }

    // Typical-prediction kernels (jbig2_decode_generic_templateN_TPGDON). Per row an LTP flag is decoded from
    // a dedicated context; if it marks the row "typical" the previous row is copied, else pixels decode with
    // the same context as the GBAT-aware unoptimized kernel.
    private static void CopyPrevRow(byte[] image, int row, int stride)
    {
        int dst = row * stride;
        if (row == 0)
        {
            for (int i = 0; i < stride; i++)
            {
                image[dst + i] = 0;
            }
        }
        else
        {
            int src = (row - 1) * stride;
            for (int i = 0; i < stride; i++)
            {
                image[dst + i] = image[src + i];
            }
        }
    }

    private static void DecodeTemplate0Tpgdon(Jbig2MqDecoder mq, byte[] stats, byte[] image, sbyte[] gbat, int gbWidth, int gbHeight, int stride)
    {
        int ltp = 0;
        for (int y = 0; y < gbHeight; y++)
        {
            int bit = mq.Decode(ref stats[0x9B25]);
            ltp ^= bit;
            if (ltp != 0)
            {
                CopyPrevRow(image, y, stride);
                continue;
            }

            uint outByte = 0;
            int outBits = 8;
            int d = y * stride;
            uint pd = 0, ppd = 0;
            int pline = y > 0 ? (y - 1) * stride : -1;
            int ppline = y > 1 ? (y - 2) * stride : -1;
            if (y > 0)
            {
                pd = (uint)(image[pline] << 8); pline++;
                if (gbWidth > 8) { pd |= image[pline]; pline++; }
                if (y > 1) { ppd = (uint)(image[ppline] << 8); ppline++; if (gbWidth > 8) { ppd |= image[ppline]; ppline++; } }
            }

            for (int x = 0; x < gbWidth; x++)
            {
                uint context = outByte & 0x000F;
                context |= (pd >> 8) & 0x03E0;
                context |= (ppd >> 2) & 0x7000;
                context |= (uint)GetPixel(image, gbWidth, gbHeight, stride, x + gbat[0], y + gbat[1]) << 4;
                context |= (uint)GetPixel(image, gbWidth, gbHeight, stride, x + gbat[2], y + gbat[3]) << 10;
                context |= (uint)GetPixel(image, gbWidth, gbHeight, stride, x + gbat[4], y + gbat[5]) << 11;
                context |= (uint)GetPixel(image, gbWidth, gbHeight, stride, x + gbat[6], y + gbat[7]) << 15;

                bit = mq.Decode(ref stats[context]);
                pd <<= 1;
                ppd <<= 1;
                outByte = (outByte << 1) | (uint)bit;
                outBits--;
                image[d] = (byte)(outByte << outBits);
                if (outBits == 0)
                {
                    outBits = 8;
                    d++;
                    if (x + 9 < gbWidth && pline >= 0) { pd |= image[pline]; pline++; if (ppline >= 0) { ppd |= image[ppline]; ppline++; } }
                }
            }

            if (outBits != 8) image[d] = (byte)(outByte << outBits);
        }
    }

    private static void DecodeTemplate1Tpgdon(Jbig2MqDecoder mq, byte[] stats, byte[] image, sbyte[] gbat, int gbWidth, int gbHeight, int stride)
    {
        int ltp = 0;
        for (int y = 0; y < gbHeight; y++)
        {
            int bit = mq.Decode(ref stats[0x0795]);
            ltp ^= bit;
            if (ltp != 0)
            {
                CopyPrevRow(image, y, stride);
                continue;
            }

            uint outByte = 0;
            int outBits = 8;
            int d = y * stride;
            uint pd = 0, ppd = 0;
            int pline = y > 0 ? (y - 1) * stride : -1;
            int ppline = y > 1 ? (y - 2) * stride : -1;
            if (y > 0)
            {
                pd = (uint)(image[pline] << 8); pline++;
                if (gbWidth > 8) { pd |= image[pline]; pline++; }
                if (y > 1) { ppd = (uint)(image[ppline] << 8); ppline++; if (gbWidth > 8) { ppd |= image[ppline]; ppline++; } }
            }

            for (int x = 0; x < gbWidth; x++)
            {
                uint context = outByte & 0x0007;
                context |= (uint)GetPixel(image, gbWidth, gbHeight, stride, x + gbat[0], y + gbat[1]) << 3;
                context |= (pd >> 9) & 0x01F0;
                context |= (ppd >> 4) & 0x1E00;

                bit = mq.Decode(ref stats[context]);
                pd <<= 1;
                ppd <<= 1;
                outByte = (outByte << 1) | (uint)bit;
                outBits--;
                image[d] = (byte)(outByte << outBits);
                if (outBits == 0)
                {
                    outBits = 8;
                    d++;
                    if (x + 9 < gbWidth && pline >= 0) { pd |= image[pline]; pline++; if (ppline >= 0) { ppd |= image[ppline]; ppline++; } }
                }
            }

            if (outBits != 8) image[d] = (byte)(outByte << outBits);
        }
    }

    private static void DecodeTemplate2Tpgdon(Jbig2MqDecoder mq, byte[] stats, byte[] image, sbyte[] gbat, int gbWidth, int gbHeight, int stride)
    {
        int ltp = 0;
        for (int y = 0; y < gbHeight; y++)
        {
            int bit = mq.Decode(ref stats[0x00E5]);
            ltp ^= bit;
            if (ltp != 0)
            {
                CopyPrevRow(image, y, stride);
                continue;
            }

            uint outByte = 0;
            int outBits = 8;
            int d = y * stride;
            uint pd = 0, ppd = 0;
            int pline = y > 0 ? (y - 1) * stride : -1;
            int ppline = y > 1 ? (y - 2) * stride : -1;
            if (y > 0)
            {
                pd = (uint)(image[pline] << 8); pline++;
                if (gbWidth > 8) { pd |= image[pline]; pline++; }
                if (y > 1) { ppd = (uint)(image[ppline] << 8); ppline++; if (gbWidth > 8) { ppd |= image[ppline]; ppline++; } }
            }

            for (int x = 0; x < gbWidth; x++)
            {
                uint context = outByte & 0x0003;
                context |= (uint)GetPixel(image, gbWidth, gbHeight, stride, x + gbat[0], y + gbat[1]) << 2;
                context |= (pd >> 11) & 0x078;
                context |= (ppd >> 7) & 0x380;

                bit = mq.Decode(ref stats[context]);
                pd <<= 1;
                ppd <<= 1;
                outByte = (outByte << 1) | (uint)bit;
                outBits--;
                image[d] = (byte)(outByte << outBits);
                if (outBits == 0)
                {
                    outBits = 8;
                    d++;
                    if (x + 9 < gbWidth && pline >= 0) { pd |= image[pline]; pline++; if (ppline >= 0) { ppd |= image[ppline]; ppline++; } }
                }
            }

            if (outBits != 8) image[d] = (byte)(outByte << outBits);
        }
    }

    private static void DecodeTemplate3Tpgdon(Jbig2MqDecoder mq, byte[] stats, byte[] image, sbyte[] gbat, int gbWidth, int gbHeight, int stride)
    {
        int ltp = 0;
        for (int y = 0; y < gbHeight; y++)
        {
            int bit = mq.Decode(ref stats[0x0195]);
            ltp ^= bit;
            if (ltp != 0)
            {
                CopyPrevRow(image, y, stride);
                continue;
            }

            uint outByte = 0;
            int outBits = 8;
            int d = y * stride;
            uint pd = 0;
            int pline = y > 0 ? (y - 1) * stride : -1;
            if (y > 0)
            {
                pd = (uint)(image[pline] << 8); pline++;
                if (gbWidth > 8) { pd |= image[pline]; pline++; }
            }

            for (int x = 0; x < gbWidth; x++)
            {
                uint context = outByte & 0x000F;
                context |= (uint)GetPixel(image, gbWidth, gbHeight, stride, x + gbat[0], y + gbat[1]) << 4;
                context |= (pd >> 9) & 0x3E0;

                bit = mq.Decode(ref stats[context]);
                pd <<= 1;
                outByte = (outByte << 1) | (uint)bit;
                outBits--;
                image[d] = (byte)(outByte << outBits);
                if (outBits == 0)
                {
                    outBits = 8;
                    d++;
                    if (x + 9 < gbWidth && pline >= 0) { pd |= image[pline]; pline++; }
                }
            }

            if (outBits != 8) image[d] = (byte)(outByte << outBits);
        }
    }
}
