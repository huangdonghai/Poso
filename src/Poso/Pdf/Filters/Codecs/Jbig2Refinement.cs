namespace Poso.Pdf.Filters.Codecs;

/// <summary>
/// JBIG2 generic refinement region decoder (ITU-T T.88 §6.2.6), ported from jbig2dec's
/// <c>jbig2_refinement.c</c>. Decodes a bitmap by arithmetic coding with context derived from
/// both previously-decoded pixels (coding template) and a reference bitmap (reference template).
/// Supports GRTEMPLATE 0 and 1 with arbitrary GRAT offsets (the unoptimized path from jbig2dec).
/// </summary>
internal static class Jbig2Refinement
{
    /// <summary>
    /// Decodes a refinement region producing a width×height bitmap (packed MSB-first, 1=black).
    /// Returns null on error or unsupported configuration.
    /// </summary>
    public static byte[]? Decode(
        int template, bool tpgron, sbyte[] grat,
        Jbig2MqDecoder mq, byte[] grStats,
        int width, int height,
        byte[]? refPacked, int refWidth, int refHeight, int dx, int dy)
    {
        if (width <= 0 || height <= 0 || width > 100000 || height > 100000)
        {
            return null;
        }

        int stride = (width + 7) / 8;
        byte[] image = new byte[stride * height];

        if (tpgron)
        {
            return DecodeTpgron(template, grat, mq, grStats, image, width, height, stride, refPacked, refWidth, refHeight, dx, dy);
        }

        if (template == 0)
        {
            DecodeTemplate0(grat, mq, grStats, image, width, height, stride, refPacked, refWidth, refHeight, dx, dy);
        }
        else
        {
            DecodeTemplate1(mq, grStats, image, width, height, stride, refPacked, refWidth, refHeight, dx, dy);
        }
        return image;
    }

    private static void DecodeTemplate0(
        sbyte[] grat, Jbig2MqDecoder mq, byte[] stats, byte[] image,
        int w, int h, int stride, byte[]? refPacked, int refW, int refH, int dx, int dy)
    {
        // Check adaptive pixel range (from jbig2dec pixel_outside_field/refpixel_outside_field)
        if ( grat[1] < -128 || grat[1] > 0 || grat[0] < -128 || (grat[1] < 0 && grat[0] > 127) || (grat[1] == 0 && grat[0] >= 0) ||
             grat[3] < -128 || grat[3] > 127 || grat[2] < -128 || grat[2] > 127)
        {
            return; // out of field, abort
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                uint ctx = 0;
                ctx |= (uint)GetImagePixel(image, w, h, stride, x - 1, y) << 0;
                ctx |= (uint)GetImagePixel(image, w, h, stride, x + 1, y - 1) << 1;
                ctx |= (uint)GetImagePixel(image, w, h, stride, x, y - 1) << 2;
                ctx |= (uint)GetImagePixel(image, w, h, stride, x + grat[0], y + grat[1]) << 3;
                ctx |= (uint)GetRefPixel(refPacked, refW, refH, x - dx + 1, y - dy + 1) << 4;
                ctx |= (uint)GetRefPixel(refPacked, refW, refH, x - dx, y - dy + 1) << 5;
                ctx |= (uint)GetRefPixel(refPacked, refW, refH, x - dx - 1, y - dy + 1) << 6;
                ctx |= (uint)GetRefPixel(refPacked, refW, refH, x - dx + 1, y - dy) << 7;
                ctx |= (uint)GetRefPixel(refPacked, refW, refH, x - dx, y - dy) << 8;
                ctx |= (uint)GetRefPixel(refPacked, refW, refH, x - dx - 1, y - dy) << 9;
                ctx |= (uint)GetRefPixel(refPacked, refW, refH, x - dx + 1, y - dy - 1) << 10;
                ctx |= (uint)GetRefPixel(refPacked, refW, refH, x - dx, y - dy - 1) << 11;
                ctx |= (uint)GetRefPixel(refPacked, refW, refH, x - dx + grat[2], y - dy + grat[3]) << 12;

                int bit = mq.Decode(ref stats[ctx]);
                if (bit < 0)
                {
                    return;
                }
                if (bit != 0)
                {
                    image[y * stride + (x >> 3)] |= (byte)(1 << (7 - (x & 7)));
                }
            }
        }
    }

    private static void DecodeTemplate1(
        Jbig2MqDecoder mq, byte[] stats, byte[] image,
        int w, int h, int stride, byte[]? refPacked, int refW, int refH, int dx, int dy)
    {
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                uint ctx = 0;
                ctx |= (uint)GetImagePixel(image, w, h, stride, x - 1, y) << 0;
                ctx |= (uint)GetImagePixel(image, w, h, stride, x + 1, y - 1) << 1;
                ctx |= (uint)GetImagePixel(image, w, h, stride, x, y - 1) << 2;
                ctx |= (uint)GetImagePixel(image, w, h, stride, x - 1, y - 1) << 3;
                ctx |= (uint)GetRefPixel(refPacked, refW, refH, x - dx + 1, y - dy + 1) << 4;
                ctx |= (uint)GetRefPixel(refPacked, refW, refH, x - dx, y - dy + 1) << 5;
                ctx |= (uint)GetRefPixel(refPacked, refW, refH, x - dx + 1, y - dy) << 6;
                ctx |= (uint)GetRefPixel(refPacked, refW, refH, x - dx, y - dy) << 7;
                ctx |= (uint)GetRefPixel(refPacked, refW, refH, x - dx - 1, y - dy) << 8;
                ctx |= (uint)GetRefPixel(refPacked, refW, refH, x - dx, y - dy - 1) << 9;

                int bit = mq.Decode(ref stats[ctx]);
                if (bit < 0)
                {
                    return;
                }
                if (bit != 0)
                {
                    image[y * stride + (x >> 3)] |= (byte)(1 << (7 - (x & 7)));
                }
            }
        }
    }

    private static byte[]? DecodeTpgron(
        int template, sbyte[] grat, Jbig2MqDecoder mq, byte[] stats, byte[] image,
        int w, int h, int stride, byte[]? refPacked, int refW, int refH, int dx, int dy)
    {
        // Port of jbig2_decode_refinement_TPGRON (jbig2dec). Per row:
        //   LTP ^= decode(startCtx). If LTP is 0, decode every pixel with the full mkctx; if LTP is 1,
        //   use the reference pixel directly when the 8-neighbourhood is uniform (typical), else fall
        //   back to decoding that pixel with mkctx.
        // The start_context slot and mkctx layout follow MuPDF's jbig2dec (spec-conformant). PDFium
        // uses a different bit layout for template 1 (image bits in the high positions, decoded bVal
        // in bit 6), and the two disagree for template 1 + TPGRON. Callers gate that combination to
        // null (see Jbig2Decoder's refinement case) until we adopt PDFium's exact T1 context.
        uint startCtx = template != 0 ? 0x40u : 0x100u;
        int ltp = 0;
        for (int y = 0; y < h; y++)
        {
            int flagBit = mq.Decode(ref stats[startCtx]);
            if (flagBit < 0)
            {
                return null;
            }
            ltp ^= flagBit;
            for (int x = 0; x < w; x++)
            {
                int bit;
                int iv = ltp != 0 ? ImplicitValue(refPacked, refW, refH, x - dx, y - dy) : -1;
                if (iv >= 0)
                {
                    bit = iv;
                }
                else
                {
                    uint ctx = BuildTpgronContext(template, grat, image, w, h, stride, refPacked, refW, refH, x, y, dx, dy);
                    bit = mq.Decode(ref stats[ctx]);
                    if (bit < 0)
                    {
                        return null;
                    }
                }
                int byteIdx = y * stride + (x >> 3);
                int mask = 1 << (7 - (x & 7));
                if (bit != 0)
                {
                    image[byteIdx] = (byte)(image[byteIdx] | mask);
                }
                else
                {
                    image[byteIdx] = (byte)(image[byteIdx] & ~mask);
                }
            }
        }
        return image;
    }

    private static int ImplicitValue(byte[]? refPacked, int refW, int refH, int i, int j)
    {
        // MuPDF jbig2dec's implicit_value(): returns ref[i,j] iff all 8 neighbours of (i,j) match, else -1.
        int m = GetRefPixel(refPacked, refW, refH, i, j);
        if (GetRefPixel(refPacked, refW, refH, i - 1, j - 1) != m) return -1;
        if (GetRefPixel(refPacked, refW, refH, i,     j - 1) != m) return -1;
        if (GetRefPixel(refPacked, refW, refH, i + 1, j - 1) != m) return -1;
        if (GetRefPixel(refPacked, refW, refH, i - 1, j)     != m) return -1;
        if (GetRefPixel(refPacked, refW, refH, i + 1, j)     != m) return -1;
        if (GetRefPixel(refPacked, refW, refH, i - 1, j + 1) != m) return -1;
        if (GetRefPixel(refPacked, refW, refH, i,     j + 1) != m) return -1;
        if (GetRefPixel(refPacked, refW, refH, i + 1, j + 1) != m) return -1;
        return m;
    }

    private static uint BuildTpgronContext(int template, sbyte[] grat, byte[] image, int w, int h, int stride,
        byte[]? refPacked, int refW, int refH, int x, int y, int dx, int dy)
    {
        uint ctx = 0;
        ctx |= (uint)GetImagePixel(image, w, h, stride, x - 1, y) << 0;
        ctx |= (uint)GetImagePixel(image, w, h, stride, x + 1, y - 1) << 1;
        ctx |= (uint)GetImagePixel(image, w, h, stride, x, y - 1) << 2;
        if (template == 0)
        {
            ctx |= (uint)GetImagePixel(image, w, h, stride, x + grat[0], y + grat[1]) << 3;
            ctx |= (uint)GetRefPixel(refPacked, refW, refH, x - dx + 1, y - dy + 1) << 4;
            ctx |= (uint)GetRefPixel(refPacked, refW, refH, x - dx, y - dy + 1) << 5;
            ctx |= (uint)GetRefPixel(refPacked, refW, refH, x - dx - 1, y - dy + 1) << 6;
            ctx |= (uint)GetRefPixel(refPacked, refW, refH, x - dx + 1, y - dy) << 7;
            ctx |= (uint)GetRefPixel(refPacked, refW, refH, x - dx, y - dy) << 8;
            ctx |= (uint)GetRefPixel(refPacked, refW, refH, x - dx - 1, y - dy) << 9;
            ctx |= (uint)GetRefPixel(refPacked, refW, refH, x - dx + 1, y - dy - 1) << 10;
            ctx |= (uint)GetRefPixel(refPacked, refW, refH, x - dx, y - dy - 1) << 11;
            ctx |= (uint)GetRefPixel(refPacked, refW, refH, x - dx + grat[2], y - dy + grat[3]) << 12;
        }
        else
        {
            ctx |= (uint)GetImagePixel(image, w, h, stride, x - 1, y - 1) << 3;
            ctx |= (uint)GetRefPixel(refPacked, refW, refH, x - dx + 1, y - dy + 1) << 4;
            ctx |= (uint)GetRefPixel(refPacked, refW, refH, x - dx, y - dy + 1) << 5;
            ctx |= (uint)GetRefPixel(refPacked, refW, refH, x - dx + 1, y - dy) << 6;
            ctx |= (uint)GetRefPixel(refPacked, refW, refH, x - dx, y - dy) << 7;
            ctx |= (uint)GetRefPixel(refPacked, refW, refH, x - dx - 1, y - dy) << 8;
            ctx |= (uint)GetRefPixel(refPacked, refW, refH, x - dx, y - dy - 1) << 9;
        }
        return ctx;
    }

    private static int GetImagePixel(byte[] image, int w, int h, int stride, int x, int y)
    {
        if ((uint)x < (uint)w && (uint)y < (uint)h)
        {
            return (image[y * stride + (x >> 3)] >> (7 - (x & 7))) & 1;
        }
        return 0;
    }

    private static int GetRefPixel(byte[]? packed, int w, int h, int x, int y)
    {
        if (packed is null || (uint)x >= (uint)w || (uint)y >= (uint)h)
        {
            return 0;
        }
        int stride = (w + 7) / 8;
        return (packed[y * stride + (x >> 3)] >> (7 - (x & 7))) & 1;
    }
}
