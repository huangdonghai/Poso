namespace Poso.Pdf.Filters.Codecs;

/// <summary>
/// A JBIG2 pattern dictionary (ITU-T T.88 §6.7, segment type 16): a set of HPW×HPH gray-level patterns
/// decoded from one collective bitmap. Reused as the <see cref="Jbig2Symbol"/> holder for each pattern.
/// </summary>
internal sealed class Jbig2PatternDict
{
    public required int Hpw;
    public required int Hph;
    public required Jbig2Symbol[] Patterns;
}

/// <summary>
/// JBIG2 pattern-dictionary (type 16) and halftone-region (types 22/23) decoders, ported from jbig2dec's
/// <c>jbig2_halftone.c</c>. The pattern dictionary's collective bitmap and the halftone region's gray-scale
/// bitplanes are decoded via the already-validated arithmetic generic-region kernel (Annex C.5). Patterns
/// are then composited onto the region by gray-level index. Restricted to the arithmetic (non-MMR), no-skip
/// path; anything else returns null so the caller degrades rather than mis-renders.
/// </summary>
internal static class Jbig2Halftone
{
    /// <summary>Decodes a pattern dictionary segment body. Returns null for MMR/unsupported.</summary>
    public static Jbig2PatternDict? DecodeDictionary(byte[] data, int bodyStart, int bodyEnd)
    {
        if (bodyEnd - bodyStart < 7)
        {
            return null;
        }

        byte flags = data[bodyStart];
        bool hdMmr = (flags & 1) != 0;
        int hdTemplate = (flags >> 1) & 3;
        int hdpw = data[bodyStart + 1];
        int hdph = data[bodyStart + 2];
        uint grayMax = ReadUInt32(data, bodyStart + 3);

        int n = (int)grayMax + 1;
        if (hdpw <= 0 || hdph <= 0 || n <= 0 || n > 65536 || (long)hdpw * n > 100000)
        {
            return null;
        }

        int collectiveWidth = hdpw * n;
        int stride = (collectiveWidth + 7) / 8;
        int bodyData = bodyStart + 7;
        byte[]? collective;
        if (hdMmr)
        {
            // §7.4.4.2 HDHDAT (MMR): a single Group 4 codeword for the collective bitmap (HDPW*n) × HDPH.
            // PDFium decodes via _FaxG4Decode then explicitly inverts every output byte
            // (JBig2_GeneralDecoder.cpp line 3731: `(*pImage)->m_pData[i] = ~(*pImage)->m_pData[i];`),
            // meaning its internal convention is "fax raw has 1=black, invert to get MuPDF's 0=black".
            // CcittFax's blackIs1:true preserves the fax raw convention (1=black); we must additionally
            // invert so the collective bitmap uses MuPDF's 0=black convention that the shared slicing
            // loop below expects when it treats `bit != 0` as "pattern pixel is black".
            byte[] raw = CcittFaxDecoder.Decode(data, bodyData, bodyEnd - bodyData, collectiveWidth, hdph,
                k: -1, encodedByteAlign: false, blackIs1: true, endOfBlock: false, endOfLine: false, out _, out _);
            collective = new byte[raw.Length];
            for (int i = 0; i < raw.Length; i++)
            {
                collective[i] = (byte)~raw[i];
            }
        }
        else
        {
            sbyte[] gbat =
            [
                (sbyte)(-hdpw), 0, -3, -1, 2, -2, -2, -2,
            ];
            int gbStatsSize = hdTemplate == 0 ? 1 << 16 : hdTemplate == 1 ? 1 << 13 : 1 << 10;
            byte[] gbStats = new byte[gbStatsSize];
            var mq = new Jbig2MqDecoder(data, bodyData, bodyEnd);
            collective = Jbig2GenericArithmetic.DecodeShared(hdTemplate, gbat, mq, gbStats, collectiveWidth, hdph);
        }
        if (collective is null)
        {
            return null;
        }

        // Split the collective bitmap into n patterns of hdpw×hdph each (REPLACE-clipped, per 6.7.5(4)).
        Jbig2Symbol[] patterns = new Jbig2Symbol[n];
        int patStride = (hdpw + 7) / 8;
        for (int i = 0; i < n; i++)
        {
            byte[] pat = new byte[patStride * hdph];
            int originX = i * hdpw;
            for (int y = 0; y < hdph; y++)
            {
                for (int x = 0; x < hdpw; x++)
                {
                    int sx = originX + x;
                    int bit = (collective[y * stride + (sx >> 3)] >> (7 - (sx & 7))) & 1;
                    if (bit != 0)
                    {
                        pat[y * patStride + (x >> 3)] |= (byte)(1 << (7 - (x & 7)));
                    }
                }
            }
            patterns[i] = new Jbig2Symbol(hdpw, hdph, pat);
        }

        return new Jbig2PatternDict { Hpw = hdpw, Hph = hdph, Patterns = patterns };
    }

    /// <summary>
    /// Decodes a halftone region segment body and composites the resulting region bitmap onto the page.
    /// Returns false on unsupported features (MMR/skip) or error.
    /// </summary>
    public static bool DecodeRegion(
        byte[] data, int bodyStart, int bodyEnd, Jbig2PatternDict? patterns,
        byte[] page, int pageWidth, int pageHeight, int rowBytes)
    {
        if (page is null)
        {
            return false;
        }
        if (!DecodeRegionStandalone(data, bodyStart, bodyEnd, patterns, out byte[]? region, out int regW, out int regH, out int regX, out int regY, out int regOp))
        {
            return false;
        }
        return Jbig2SymbolDict.CompositeRegionOntoPage(page, pageWidth, rowBytes, region!, regW, regH, regX, regY, regOp);
    }

    /// <summary>
    /// Decodes a halftone region into a standalone bitmap without compositing. Used by immediate
    /// variants (via <see cref="DecodeRegion"/>) and by intermediate halftone regions (segment type 20)
    /// whose result is retained as a refinement reference. Returns false on unsupported features (MMR)
    /// or decoding error.
    /// </summary>
    public static bool DecodeRegionStandalone(
        byte[] data, int bodyStart, int bodyEnd, Jbig2PatternDict? patterns,
        out byte[]? region, out int regW, out int regH, out int regX, out int regY, out int regOp)
    {
        region = null; regW = 0; regH = 0; regX = 0; regY = 0; regOp = 0;
        if (patterns is null || bodyEnd - bodyStart < 38)
        {
            return false;
        }

        // region_info (17 bytes) at bodyStart
        regW = (int)ReadUInt32(data, bodyStart);
        regH = (int)ReadUInt32(data, bodyStart + 4);
        regX = (int)ReadUInt32(data, bodyStart + 8);
        regY = (int)ReadUInt32(data, bodyStart + 12);
        regOp = data[bodyStart + 16] & 0x07;
        int cursor = bodyStart + 17;

        byte flags = data[cursor];
        bool hMmr = (flags & 1) != 0;
        int hTemplate = (flags >> 1) & 3;
        bool hEnableSkip = (flags & 8) != 0;
        int hCombop = (flags >> 4) & 7;
        bool hDefPixel = (flags & 0x80) != 0;
        cursor += 1;

        // §7.4.5.2 HMMR=1: each of the GSBPP gray-scale bitplanes is a Group 4 codeword packed
        // back-to-back without per-plane length prefixes. Cursor management mirrors PDFium's
        // GSIDProc::decode_MMR (JBig2_GeneralDecoder.cpp line 3302/3316): align bitpos to byte,
        // then offset(3) to skip the EOFB tail padding. MuPDF uses explicit EOFB detection
        // (jbig2_mmr.c line 1280) but arrives at the same final cursor position on well-formed
        // streams, so both oracles render identically.
        if (cursor + 20 > bodyEnd)
        {
            return false;
        }
        int hgw = (int)ReadUInt32(data, cursor);
        int hgh = (int)ReadUInt32(data, cursor + 4);
        int hgx = (int)ReadUInt32(data, cursor + 8);
        int hgy = (int)ReadUInt32(data, cursor + 12);
        cursor += 16;
        int hrx = (data[cursor] << 8) | data[cursor + 1];
        int hry = (data[cursor + 2] << 8) | data[cursor + 3];
        cursor += 4;

        if (hgw <= 0 || hgh <= 0 || hgw > 100000 || hgh > 100000)
        {
            return false;
        }

        // HBPP = smallest number such that 2^HBPP >= n_patterns (6.6.5 point 3).
        int numPats = patterns.Patterns.Length;
        int hbpp = 0;
        while ((1U << hbpp) < (uint)numPats)
        {
            hbpp++;
        }
        if (hbpp > 16 || hbpp == 0)
        {
            return false;
        }

        // Decode gray-scale bitplanes (Annex C.5): shared MQ + stats, high plane first, cumulative XOR.
        sbyte[] gbatArith =
        [
            (sbyte)(hTemplate <= 1 ? 3 : 2), -1, -3, -1, 2, -2, -2, -2,
        ];
        int gbStatsSize = hTemplate == 0 ? 1 << 16 : hTemplate == 1 ? 1 << 13 : 1 << 10;
        byte[] gbStats = new byte[gbStatsSize];
        var mqArith = new Jbig2MqDecoder(data, cursor, bodyEnd);
        int gsStride = (hgw + 7) / 8;

        // §6.6.5.1: when skip is enabled, HSKIP is COMPUTED geometrically — grid cells whose pattern falls
        // entirely off the region canvas are marked to skip (their gray pixel is forced to 0 during plane decode).
        byte[]? skip = null;
        int skipStride = 0;
        if (hEnableSkip)
        {
            skipStride = (hgw + 7) / 8;
            skip = new byte[skipStride * hgh];
            int hpw = patterns.Hpw;
            int hph = patterns.Hph;
            for (int mg = 0; mg < hgh; mg++)
            {
                for (int ng = 0; ng < hgw; ng++)
                {
                    long x = ((long)hgx + (long)mg * hry + (long)ng * hrx) >> 8;
                    long y = ((long)hgy + (long)mg * hrx - (long)ng * hry) >> 8;
                    bool isSkip = x + hpw <= 0 || x >= regW || y + hph <= 0 || y >= regH;
                    if (isSkip)
                    {
                        skip[mg * skipStride + (ng >> 3)] |= (byte)(1 << (7 - (ng & 7)));
                    }
                }
            }
        }

        byte[][] planes = new byte[hbpp][];
        int mmrCursor = cursor;
        for (int j = hbpp - 1; j >= 0; j--)
        {
            byte[]? plane;
            if (hMmr)
            {
                int remaining = bodyEnd - mmrCursor;
                if (remaining <= 0)
                {
                    return false;
                }
                byte[] raw = CcittFaxDecoder.Decode(data, mmrCursor, remaining, hgw, hgh, k: -1,
                    encodedByteAlign: false, blackIs1: true, endOfBlock: false, endOfLine: false, out _, out int bitsConsumed);
                // Apply PDFium's byte-inversion (line 3731) so gray-bit 1 in the plane corresponds
                // to the black pixel in the MMR stream (which under fax raw is encoded as bit=1).
                plane = new byte[raw.Length];
                for (int i = 0; i < raw.Length; i++)
                {
                    plane[i] = (byte)~raw[i];
                }
                // align to next byte boundary then skip the 3-byte EOFB tail padding, exactly as
                // PDFium's `pStream->alignByte(); pStream->offset(3);` sequence does.
                mmrCursor += (bitsConsumed + 7) / 8 + 3;
            }
            else
            {
                plane = skip is not null
                    ? Jbig2GenericArithmetic.DecodeSharedWithSkip(hTemplate, gbatArith, mqArith, gbStats, hgw, hgh, skip, skipStride)
                    : Jbig2GenericArithmetic.DecodeShared(hTemplate, gbatArith, mqArith, gbStats, hgw, hgh);
            }
            if (plane is null)
            {
                return false;
            }
            if (j < hbpp - 1)
            {
                // C.5 step 3(b): GSPLANES[j] ^= GSPLANES[j+1]
                byte[] higher = planes[j + 1];
                for (int i = 0; i < gsStride * hgh; i++)
                {
                    plane[i] ^= higher[i];
                }
            }
            planes[j] = plane;
        }

        // Build the region image, fill with HDEFPIXEL (jbig2dec/mupdf do a byte-fill of the 0/1 value).
        int regStride = (regW + 7) / 8;
        byte[] RegionOut = new byte[regStride * regH];
        if (hDefPixel)
        {
            Array.Fill(RegionOut, (byte)0x01);
        }

        // For each grid cell, read the gray value and compose the corresponding pattern.
        for (int mg = 0; mg < hgh; mg++)
        {
            for (int ng = 0; ng < hgw; ng++)
            {
                long x = ((long)hgx + (long)mg * hry + (long)ng * hrx) >> 8;
                long y = ((long)hgy + (long)mg * hrx - (long)ng * hry) >> 8;

                // GSVALS[ng][mg] = sum over planes of bit << j.
                int gray = 0;
                for (int j = 0; j < hbpp; j++)
                {
                    gray |= ((planes[j][mg * gsStride + (ng >> 3)] >> (7 - (ng & 7))) & 1) << j;
                }
                if (gray >= numPats)
                {
                    gray = numPats - 1;
                }

                ComposeSymbol(RegionOut, regW, regH, regStride, patterns.Patterns[gray], (int)x, (int)y, hCombop);
            }
        }

        // Composite the region onto the page with the region's own combination operator.
        region = RegionOut;
        return true;
    }

    private static void ComposeSymbol(byte[] image, int imgW, int imgH, int imgStride, Jbig2Symbol sym, int x, int y, int combOp)
    {
        int symStride = sym.Stride;
        for (int sy = 0; sy < sym.Height; sy++)
        {
            int iy = y + sy;
            if (iy < 0 || iy >= imgH)
            {
                continue;
            }
            for (int sx = 0; sx < sym.Width; sx++)
            {
                int ix = x + sx;
                if (ix < 0 || ix >= imgW)
                {
                    continue;
                }
                bool symBlack = ((sym.Packed[sy * symStride + (sx >> 3)] >> (7 - (sx & 7))) & 1) != 0;
                int idx = iy * imgStride + (ix >> 3);
                int mask = 1 << (7 - (ix & 7));
                bool cur = (image[idx] & mask) != 0;
                bool result = combOp switch
                {
                    0 => cur || symBlack,
                    1 => cur && symBlack,
                    2 => cur ^ symBlack,
                    3 => cur == symBlack,
                    _ => symBlack,
                };
                image[idx] = result ? (byte)(image[idx] | mask) : (byte)(image[idx] & ~mask);
            }
        }
    }

    private static uint ReadUInt32(byte[] data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
}
