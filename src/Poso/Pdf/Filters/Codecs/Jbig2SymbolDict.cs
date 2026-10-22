using System.Collections.Generic;

namespace Poso.Pdf.Filters.Codecs;

/// <summary>
/// Represents a decoded JBIG2 symbol (a glyph bitmap) from a symbol dictionary segment.
/// The pixel data is row-major, MSB-first bit-packed, 1 = black.
///
/// A symbol may be <em>empty</em>: T.88 §6.5.8.2 lets a refinement/aggregate dictionary entry
/// carry no bitmap at all, and PDFium stores that as a null image which every consumer then
/// skips when placing instances. <see cref="IsEmpty"/> marks such a symbol; its
/// <see cref="Packed"/> is an empty array and its width/height are zero.
/// </summary>
internal readonly record struct Jbig2Symbol(int Width, int Height, byte[] Packed)
{
    /// <summary>A symbol with no bitmap: PDFium's null image.</summary>
    public static Jbig2Symbol Empty => new(0, 0, []);

    /// <summary>Whether this symbol carries no bitmap.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0 || Packed.Length == 0;

    public int Stride => (Width + 7) / 8;
}

/// <summary>
/// JBIG2 symbol dictionary segment decoder (ITU-T T.88 §6.4/§6.5, segment type 0) and text region
/// segment decoder (T.88 §6.3/§6.4, segment types 6/7), ported faithfully from the reference
/// implementation <c>jbig2_symbol_dict.c</c> / <c>jbig2_text.c</c> (Artifex jbig2dec).
///
/// Covers both coding modes and the refinement/aggregate constructions:
/// - Symbol dictionary: arithmetic and Huffman dictionaries; Huffman dictionaries may code a symbol as a
///   single refinement of another or as a text-region aggregate of several (§6.5.8.2).
/// - Text region: arithmetic and Huffman instance placement, with or without per-instance refinement.
///
/// Returns null for malformed or still-unsupported input, so the caller degrades gracefully.
/// </summary>
internal static class Jbig2SymbolDict
{
    // Reference-corner values as PDFium numbers them (JBig2_TrdProc.h, `enum JBig2Corner`).
    // Note that the spec's own numbering differs: T.88 lists the corners with the bottom edge
    // first, while PDFium (and jbig2dec) put BOTTOMLEFT at 0 and TOPLEFT at 1. The decoder uses
    // PDFium's numbering throughout, matching `REFCORNER = (flags >> 4) & 3`.
    private const int RefCornerBottomLeft = 0;
    private const int RefCornerTopLeft = 1;
    private const int RefCornerBottomRight = 2;
    private const int RefCornerTopRight = 3;

    /// <summary>
    /// Decodes an arithmetic symbol dictionary segment body (after the segment header).
    /// Returns the exported symbol array, or null if the segment uses unsupported features (Huffman/refinement).
    /// <paramref name="inStats"/> is the arithmetic context inherited from a previously-retained dictionary
    /// when SDREUSES (bit 8) is set; <paramref name="outStats"/> receives the final context on exit so the
    /// caller can register it for later SDREUSES consumers when SDRETAINS (bit 9) is set (mirrors PDFium's
    /// <c>m_bContextRetained</c> in JBig2_Context.cpp lines 768 and 812).
    /// </summary>
    public static Jbig2Symbol[]? DecodeDictionary(byte[] data, int bodyStart, int bodyEnd, Jbig2Symbol[]?[] referredDicts, Jbig2Huffman.Table?[] referredTables, byte[]? inStats, out byte[]? outStats)
    {
        outStats = null;
        if (bodyEnd - bodyStart < 10)
        {
            return null;
        }

        ushort flags = (ushort)((data[bodyStart] << 8) | data[bodyStart + 1]);

        bool sdHuffman = (flags & 0x0001) != 0;
        bool sdRefAgg = (flags & 0x0002) != 0;
        int sdTemplate = (flags >> 10) & 3;
        bool sdRTemplate = ((flags >> 12) & 1) == 1;

        // Huffman-coded symbol dictionary: deltas via Annex B tables; symbol bitmaps are a single MMR (or
        // uncompressed) "collective bitmap" per height class that is then sliced into per-symbol glyphs.
        if (sdHuffman)
        {
            return DecodeDictionaryHuffman(data, bodyStart, bodyEnd, flags, referredDicts, referredTables);
        }

        // 7.4.2.1.2: sdat_bytes (GBAT for symbol generic regions)
        int sdatBytes = sdTemplate == 0 ? 8 : 2;
        sbyte[] sdat = new sbyte[8];
        for (int i = 0; i < sdatBytes; i++)
        {
            sdat[i] = (sbyte)data[bodyStart + 2 + i];
        }
        int offset = 2 + sdatBytes;

        // 7.4.2.1.3: SDRA (refinement AT) — read if SDREFAGG && !SDRTEMPLATE
        sbyte[] sdrat = new sbyte[4];
        if (sdRefAgg && !sdRTemplate)
        {
            if (offset + 4 > bodyEnd - bodyStart)
            {
                return null;
            }
            for (int i = 0; i < 4; i++)
            {
                sdrat[i] = (sbyte)data[bodyStart + offset + i];
            }
            offset += 4;
        }

        // 7.4.2.1.4/5: SDNUMEXSYMS, SDNUMNEWSYMS
        if (offset + 8 > bodyEnd - bodyStart)
        {
            return null;
        }
        int sdNumExSyms = (int)ReadUInt32(data, bodyStart + offset);
        int sdNumNewSyms = (int)ReadUInt32(data, bodyStart + offset + 4);
        offset += 8;

        // Guard against absurd sizes
        if (sdNumNewSyms < 0 || sdNumNewSyms > 100000 || sdNumExSyms < 0 || sdNumExSyms > 100000)
        {
            return null;
        }

        // Gather input symbols from referred dictionaries
        Jbig2Symbol[]? sdInSyms = null;
        int sdNumInSyms = 0;
        if (referredDicts.Length > 0)
        {
            var allSyms = new List<Jbig2Symbol>();
            for (int i = 0; i < referredDicts.Length; i++)
            {
                if (referredDicts[i] is { } dict && dict.Length > 0)
                {
                    allSyms.AddRange(dict);
                }
            }
            if (allSyms.Count > 0)
            {
                sdInSyms = allSyms.ToArray();
                sdNumInSyms = sdInSyms.Length;
            }
        }

        // SBSYMCODELEN = ceil(log2(SDNUMINSYMS + SDNUMNEWSYMS))
        int sbSymCodeLen = 0;
        while ((1L << sbSymCodeLen) < (long)sdNumInSyms + sdNumNewSyms)
        {
            sbSymCodeLen++;
        }

        // Allocate generic region stats (shared across all symbols in this dictionary). If the caller
        // supplied stats from a previously-retained dictionary (SDREUSES=1), seed from those; PDFium
        // copies the last-referred segment's stats here (JBig2_Context.cpp line 771).
        int gbStatsSize = sdTemplate == 0 ? 1 << 16 : sdTemplate == 1 ? 1 << 13 : 1 << 10;
        byte[] gbStats = new byte[gbStatsSize];
        if (inStats is not null && inStats.Length == gbStatsSize)
        {
            Array.Copy(inStats, gbStats, gbStatsSize);
        }

        // Refinement stats + decoders (only used when SDREFAGG=1)
        int grStatsSize = sdRTemplate ? 1 << 10 : 1 << 13;
        byte[] grStats = sdRefAgg ? new byte[grStatsSize] : [];
        var iaai = sdRefAgg ? new Jbig2ArithmeticInteger() : null;
        var iardx = sdRefAgg ? new Jbig2ArithmeticInteger() : null;
        var iardy = sdRefAgg ? new Jbig2ArithmeticInteger() : null;
        var iaId = sdRefAgg ? new Jbig2ArithmeticIaid(sbSymCodeLen) : null;
        // Text-region-in-dict decoders for multi-instance aggregation (SDREFAGG, IAAI>1)
        var aggIadt = sdRefAgg ? new Jbig2ArithmeticInteger() : null;
        var aggIafs = sdRefAgg ? new Jbig2ArithmeticInteger() : null;
        var aggIads = sdRefAgg ? new Jbig2ArithmeticInteger() : null;
        var aggIait = sdRefAgg ? new Jbig2ArithmeticInteger() : null;
        // Within-aggregation per-instance refinement contexts (jbig2 runs the text region with SBREFINE=1;
        // IAID and IARDX/IARDY are the SAME tparams contexts reused from the single-refine path above).
        var aggIari = sdRefAgg ? new Jbig2ArithmeticInteger() : null;
        var aggIardw = sdRefAgg ? new Jbig2ArithmeticInteger() : null;
        var aggIardh = sdRefAgg ? new Jbig2ArithmeticInteger() : null;

        // Create the MQ decoder for the entire symbol dictionary body
        int bodyDataStart = bodyStart + offset;
        var mq = new Jbig2MqDecoder(data, bodyDataStart, bodyEnd);

        // Decode new symbols
        Jbig2Symbol[] sdNewSyms = new Jbig2Symbol[sdNumNewSyms];
        var iadh = new Jbig2ArithmeticInteger();
        var iadw = new Jbig2ArithmeticInteger();
        int hcHeight = 0;
        int nSymsDecoded = 0;
        int code;

        while (nSymsDecoded < sdNumNewSyms)
        {
            // 6.5.5(4a): IADH -> height class delta
            code = iadh.Decode(mq, out int hcdh);
            if (code != 0)
            {
                return null; // error or unexpected OOB in IADH
            }
            hcHeight += hcdh;
            if (hcHeight < 0 || hcHeight > 100000)
            {
                return null;
            }

            int symWidth = 0;

            // 6.5.5(4c): inner loop over symbols in this height class
            while (true)
            {
                // IADW → width delta
                code = iadw.Decode(mq, out int dw);
                if (code < 0)
                {
                    return null;
                }
                if (code > 0)
                {
                    break; // OOB signals end of height class
                }

                if (nSymsDecoded >= sdNumNewSyms)
                {
                    break; // guard against missing OOB
                }

                symWidth += dw;
                if (symWidth <= 0 || symWidth > 100000)
                {
                    return null;
                }

                // 6.5.8: decode one symbol
                if (!sdRefAgg)
                {
                    // Non-aggregate: generic region kernel
                    byte[]? symbolBits = DecodeSymbolGenericRegion(mq, gbStats, sdTemplate, sdat, symWidth, hcHeight);
                    if (symbolBits is null)
                    {
                        return null;
                    }
                    sdNewSyms[nSymsDecoded] = new Jbig2Symbol(symWidth, hcHeight, symbolBits);
                }
                else
                {
                    // 6.5.8.2: refinement/aggregate path
                    code = iaai!.Decode(mq, out int refAggNInst);
                    if (code != 0)
                    {
                        return null;
                    }
                    if (refAggNInst == 0)
                    {
                        // T.88 §6.5.8.2 with IAAI==0: the entry carries no bitmap. PDFium stores a
                        // null image for it (the BS = nullptr before the REFAGGNINST dispatch) and the
                        // entry is exported as an empty glyph.
                        sdNewSyms[nSymsDecoded] = Jbig2Symbol.Empty;
                    }
                    else if (refAggNInst > 1)
                    {
                        // Multi-instance aggregation: compose refAggNInst symbols as a text region (with
                        // per-instance refinement, mirroring jbig2 running the text region with SBREFINE=1).
                        byte[]? aggBits = DecodeAggregation(mq, aggIadt!, aggIafs!, aggIads!, aggIait!, iaId!,
                            aggIari!, aggIardw!, aggIardh!, iardx!, iardy!, grStats, sdRTemplate,
                            sdInSyms, sdNewSyms, nSymsDecoded, sdNumInSyms, sbSymCodeLen, symWidth, hcHeight, refAggNInst);
                        if (aggBits is null) { return null; }
                        sdNewSyms[nSymsDecoded] = new Jbig2Symbol(symWidth, hcHeight, aggBits);
                    }
                    else
                    {
                        // Single refinement: IAID + IARDX + IARDY
                        code = iaId!.Decode(mq, out int symbolId);
                        if (code != 0) { return null; }
                        code = iardx!.Decode(mq, out int rdx);
                        if (code != 0) { return null; }
                        code = iardy!.Decode(mq, out int rdy);
                        if (code != 0) { return null; }

                        // Look up reference symbol (from input + already-decoded new)
                        Jbig2Symbol refSym;
                        if (symbolId < sdNumInSyms)
                        {
                            refSym = sdInSyms![symbolId];
                        }
                        else if (symbolId - sdNumInSyms < nSymsDecoded)
                        {
                            refSym = sdNewSyms[symbolId - sdNumInSyms];
                        }
                        else
                        {
                            return null; // invalid reference
                        }

                        // PDFium refuses to refine a symbol that has no bitmap
                        // (`if (!sbsyms_idi) return nullptr` in CJBig2_SDDProc::DecodeArith).
                        if (refSym.IsEmpty)
                        {
                            return null;
                        }

                        byte[]? refBits = Jbig2Refinement.Decode(
                            sdRTemplate ? 1 : 0, false, sdrat, mq, grStats,
                            symWidth, hcHeight, refSym.Packed, refSym.Width, refSym.Height, rdx, rdy);
                        if (refBits is null) { return null; }
                        sdNewSyms[nSymsDecoded] = new Jbig2Symbol(symWidth, hcHeight, refBits);
                    }
                }
                nSymsDecoded++;
            }
        }

        // 6.5.10: IAEX -> export flags
        var iaex = new Jbig2ArithmeticInteger();
        Jbig2Symbol[] exported = new Jbig2Symbol[sdNumExSyms];
        int totalSyms = sdNumInSyms + sdNumNewSyms;
        int ei = 0; // index into the full symbol space (in + new)
        int ej = 0; // index into exported
        bool exFlag = false;
        int emptyRuns = 0;
        while (ei < totalSyms && ej < sdNumExSyms)
        {
            code = iaex.Decode(mq, out int exRunLength);
            if (code < 0)
            {
                break; // error → use what we have
            }
            if (code > 0)
            {
                break; // OOB → done
            }

            if (exRunLength <= 0 && ++emptyRuns >= 1000)
            {
                break;
            }
            if (exRunLength > 0)
            {
                emptyRuns = 0;
            }

            if (exRunLength > totalSyms - ei)
            {
                exRunLength = totalSyms - ei;
            }
            if (exFlag && ej + exRunLength > sdNumExSyms)
            {
                exRunLength = sdNumExSyms - ej;
            }

            for (int k = 0; k < exRunLength; k++)
            {
                if (exFlag)
                {
                    Jbig2Symbol sym = ei < sdNumInSyms ? sdInSyms![ei] : sdNewSyms[ei - sdNumInSyms];
                    exported[ej++] = sym;
                }
                ei++;
            }
            exFlag = !exFlag;
        }

        // PDFium clamps SDNUMEXSYMS to the actual export count when the IAEX run-lengths mark fewer
        // symbols than declared (JBig2_GeneralDecoder.cpp `if (J < SDNUMEXSYMS) pDict->SDNUMEXSYMS = J;`).
        // Mirror that so downstream text regions can proceed against the shortened dictionary rather
        // than bailing the whole decode to null.
        if (ej < exported.Length)
        {
            System.Array.Resize(ref exported, ej);
        }
        outStats = gbStats;
        return exported;
    }

    /// <summary>
    /// Decodes a Huffman-coded symbol dictionary (SDHUFF=1). Without refinement each height class carries
    /// one MMR/uncompressed collective bitmap that is sliced into per-symbol glyphs; with SDREFAGG a symbol
    /// is either a single arithmetic refinement of an earlier glyph or a text-region aggregate, laid out
    /// as PDFium does (fixed-width instance ids, RD*/RSIZE from Annex B). Mirrors PDFium's JBig2_SddProc.
    /// </summary>
    private static Jbig2Symbol[]? DecodeDictionaryHuffman(byte[] data, int bodyStart, int bodyEnd, ushort flags, Jbig2Symbol[]?[] referredDicts, Jbig2Huffman.Table?[] referredTables)
    {
        bool sdRefAgg = (flags & 0x0002) != 0;
        bool sdRTemplate = ((flags >> 12) & 1) == 1;

        // 7.4.2.1.6 table selection (custom tables consumed in referred-segment order via jbig2_find_table).
        int tableIndex = 0;
        Jbig2Huffman.Table? dh = SelectTable((flags >> 2) & 3, referredTables, ref tableIndex, 4, 5, oobTwo: false, customTwo: false);
        Jbig2Huffman.Table? dw = SelectTable((flags >> 4) & 3, referredTables, ref tableIndex, 2, 3, oobTwo: false, customTwo: false);
        if (dh is null || dw is null)
        {
            return null;
        }
        Jbig2Huffman.Table? bmsizeTbl = (flags & 0x0040) != 0 ? NextCustom(referredTables, ref tableIndex) : Jbig2Huffman.Standard(1);
        if (bmsizeTbl is null)
        {
            return null;
        }

        // 7.4.2.1.2: for Huffman there is no SDAT (GBAT) field; sdat_bytes = 0.
        int offset = 2;
        // 7.4.2.1.3: SDRAT (the refinement adaptive template) is present when SDREFAGG && !SDRTEMPLATE.
        sbyte[] sdrat = new sbyte[4];
        if (sdRefAgg && !sdRTemplate)
        {
            if (bodyStart + offset + 4 > bodyEnd)
            {
                return null;
            }
            for (int i = 0; i < 4; i++)
            {
                sdrat[i] = (sbyte)data[bodyStart + offset + i];
            }
            offset += 4;
        }

        // 7.4.2.1.6: the aggregation instance count is a Huffman table of its own (Table B.1 unless a
        // custom table is named by bit 7, consumed after the BMSIZE table).
        Jbig2Huffman.Table? aggInstTable = null;
        if (sdRefAgg)
        {
            aggInstTable = (flags & 0x0080) != 0 ? NextCustom(referredTables, ref tableIndex) : Jbig2Huffman.Standard(1);
            if (aggInstTable is null)
            {
                return null;
            }
        }

        if (offset + 8 > bodyEnd - bodyStart)
        {
            return null;
        }
        int sdNumExSyms = (int)ReadUInt32(data, bodyStart + offset);
        int sdNumNewSyms = (int)ReadUInt32(data, bodyStart + offset + 4);
        offset += 8;
        if (sdNumNewSyms < 0 || sdNumNewSyms > 100000 || sdNumExSyms < 0 || sdNumExSyms > 100000)
        {
            return null;
        }

        // Input symbols from referred dictionaries.
        Jbig2Symbol[]? sdInSyms = null;
        int sdNumInSyms = 0;
        if (referredDicts.Length > 0)
        {
            var allSyms = new List<Jbig2Symbol>();
            for (int i = 0; i < referredDicts.Length; i++)
            {
                if (referredDicts[i] is { } d && d.Length > 0)
                {
                    allSyms.AddRange(d);
                }
            }
            if (allSyms.Count > 0)
            {
                sdInSyms = allSyms.ToArray();
                sdNumInSyms = sdInSyms.Length;
            }
        }

        // SBSYMCODELEN = max(1, ceil(log2(SDNUMINSYMS + SDNUMNEWSYMS))); a single refinement names its
        // reference symbol with that many raw bits rather than through a table (PDFium's
        // JBig2_SddProc.cpp).
        int sbSymCodeLen = 0;
        while ((1L << sbSymCodeLen) < (long)sdNumInSyms + sdNumNewSyms)
        {
            sbSymCodeLen++;
        }
        if (sbSymCodeLen < 1)
        {
            sbSymCodeLen = 1;
        }

        // Arithmetic stats for the refinement bitmaps (one context set per dictionary, as in the
        // arithmetic path).
        int grStatsSize = sdRTemplate ? 1 << 10 : 1 << 13;
        byte[] grStats = sdRefAgg ? new byte[grStatsSize] : [];

        var hs = new Jbig2Huffman.State(data, bodyStart + offset, bodyEnd);

        Jbig2Symbol[] sdNewSyms = new Jbig2Symbol[sdNumNewSyms];
        int[] symWidths = new int[sdNumNewSyms];
        int hcHeight = 0;
        int nsymsDecoded = 0;

        while (nsymsDecoded < sdNumNewSyms)
        {
            // 6.5.6: HCDH via SDHUFFDH.
            int hcdh = hs.Get(dh, out int code);
            if (code != 0)
            {
                return null;
            }
            hcHeight += hcdh;
            if (hcHeight < 0 || hcHeight > 100000)
            {
                return null;
            }

            int symWidth = 0;
            int totWidth = 0;
            int hcFirstSym = nsymsDecoded;

            // 6.5.7: per-symbol width deltas via SDHUFFDW.
            while (true)
            {
                int dwv = hs.Get(dw, out code);
                if (code < 0)
                {
                    return null;
                }
                if (code > 0)
                {
                    break; // OOB → end of height class
                }
                if (nsymsDecoded >= sdNumNewSyms)
                {
                    break; // guard against missing OOB
                }
                symWidth += dwv;
                if (symWidth <= 0 || symWidth > 100000)
                {
                    return null;
                }

                if (!sdRefAgg)
                {
                    totWidth += symWidth;
                    if (totWidth <= 0 || totWidth > 10000000)
                    {
                        return null;
                    }
                    symWidths[nsymsDecoded] = symWidth;
                    nsymsDecoded++;
                }
                else
                {
                    // 6.5.8.2: refinement/aggregate symbol. Its data follows the width inside the
                    // Huffman stream, so it is decoded here rather than after the height class.
                    Jbig2Symbol? symbol = DecodeHuffmanRefinedSymbol(
                        hs, data, bodyStart, bodyEnd, aggInstTable!, sdrat, grStats, sdRTemplate,
                        sdInSyms, sdNewSyms, nsymsDecoded, sdNumInSyms, sbSymCodeLen, symWidth, hcHeight);
                    if (symbol is null)
                    {
                        return null;
                    }

                    sdNewSyms[nsymsDecoded] = symbol.Value;
                    nsymsDecoded++;
                }
            }

            if (sdRefAgg)
            {
                continue; // no collective bitmap on the refinement/aggregate path
            }

            // 6.5.5(4d) / 6.5.9: collective bitmap for this height class (MMR, or uncompressed when BMSIZE=0).
            int bmSize = hs.Get(bmsizeTbl, out code);
            if (code != 0 || bmSize < 0)
            {
                return null;
            }
            hs.SkipToByteBoundary();

            int cw = totWidth;
            int ch = hcHeight;
            if (cw <= 0 || ch <= 0)
            {
                return null;
            }
            int cstride = (cw >> 3) + ((cw & 7) != 0 ? 1 : 0);
            byte[] collective = new byte[cstride * ch];
            long consumed;
            int src = hs.Offset();
            if (bmSize == 0)
            {
                // Uncompressed: the collective bitmap is stored verbatim, MSB-first, 1 = black.
                if (src + (long)ch * cstride > bodyEnd)
                {
                    return null;
                }
                for (int j = 0; j < ch; j++)
                {
                    Buffer.BlockCopy(data, src + j * cstride, collective, j * cstride, cstride);
                }
                consumed = (long)ch * cstride;
            }
            else
            {
                if (src + bmSize > bodyEnd)
                {
                    return null;
                }
                byte[] mmr = new byte[bmSize];
                Buffer.BlockCopy(data, src, mmr, 0, (int)bmSize);
                // Group 4 MMR (no T.6 EOFB). MuPDF renders JBIG2 MMR collective bitmaps with the inverse
                // bit polarity of the uncompressed path (mirrors the generic-region MMR convention), so the
                // glyph bitmap is produced with 0 = black here (blackIs1:false).
                collective = CcittFaxDecoder.DecodeMmr(mmr, cw, ch, blackIs1: false);
                consumed = bmSize;
            }
            hs.Advance((int)consumed);

            // Slice the collective bitmap into per-symbol glyphs at cumulative horizontal offsets.
            int xoff = 0;
            for (int j = hcFirstSym; j < nsymsDecoded; j++)
            {
                int gw = symWidths[j];
                byte[] glyph = ExtractColumns(collective, cstride, gw, ch, xoff);
                sdNewSyms[j] = new Jbig2Symbol(gw, ch, glyph);
                xoff += gw;
            }
        }

        // 6.5.10: IAEX export run lengths via Table B.1.
        Jbig2Huffman.Table rsize = Jbig2Huffman.Standard(1);
        Jbig2Symbol[] exported = new Jbig2Symbol[sdNumExSyms];
        int totalSyms = sdNumInSyms + sdNumNewSyms;
        int ei = 0;
        int ej = 0;
        bool exFlag = false;
        int emptyRuns = 0;
        while (ei < totalSyms && ej < sdNumExSyms)
        {
            int exRunLength = hs.Get(rsize, out int code);
            if (code != 0)
            {
                break;
            }
            if (exRunLength <= 0 && ++emptyRuns >= 1000)
            {
                break;
            }
            if (exRunLength > 0)
            {
                emptyRuns = 0;
            }
            if (exRunLength > totalSyms - ei)
            {
                exRunLength = totalSyms - ei;
            }
            if (exFlag && ej + exRunLength > sdNumExSyms)
            {
                exRunLength = sdNumExSyms - ej;
            }
            for (int k = 0; k < exRunLength; k++)
            {
                if (exFlag)
                {
                    Jbig2Symbol sym = ei < sdNumInSyms ? sdInSyms![ei] : sdNewSyms[ei - sdNumInSyms];
                    exported[ej++] = sym;
                }
                ei++;
            }
            exFlag = !exFlag;
        }
        if (ej < sdNumExSyms)
        {
            // Same clamp as the arithmetic path: PDFium tolerates under-export by truncating SDNUMEXSYMS
            // to the actual count so downstream text regions see a shorter dictionary rather than failing.
            System.Array.Resize(ref exported, ej);
        }
        return exported;
    }

    /// <summary>
    /// Selects one of the dictionary Delta-table slots: case 0/1 map to standard Annex B figures (given),
    /// case 3 pulls the next custom table in order, case 2 (reserved) yields null. Mirrors the two
    /// near-identical SDHUFFDH / SDHUFFDW switches in jbig2_symbol_dict.c.
    /// </summary>
    private static Jbig2Huffman.Table? SelectTable(int sel, Jbig2Huffman.Table?[] tables, ref int tableIndex, int std0, int std1, bool oobTwo, bool customTwo)
    {
        switch (sel)
        {
            case 0: return Jbig2Huffman.Standard(std0);
            case 1: return Jbig2Huffman.Standard(std1);
            case 3: return NextCustom(tables, ref tableIndex);
            default: return null; // 2 = reserved / invalid
        }
    }

    private static Jbig2Huffman.Table? NextCustom(Jbig2Huffman.Table?[] tables, ref int tableIndex)
    {
        if (tableIndex < 0 || tableIndex >= tables.Length || tables[tableIndex] is null)
        {
            return null;
        }
        tableIndex++;
        return tables[tableIndex - 1];
    }

    /// <summary>
    /// Extracts a <paramref name="width"/>-column-wide glyph from a packed MSB-first collective bitmap at a
    /// horizontal <paramref name="xoff"/>, producing a freshly-stride-packed bitmap of the same height.
    /// </summary>
    private static byte[] ExtractColumns(byte[] collective, int collectiveStride, int width, int height, int xoff)
    {
        int outStride = (width + 7) / 8;
        byte[] glyph = new byte[outStride * height];
        for (int y = 0; y < height; y++)
        {
            int srcRow = y * collectiveStride;
            int dstRow = y * outStride;
            for (int x = 0; x < width; x++)
            {
                int sx = xoff + x;
                bool black = ((collective[srcRow + (sx >> 3)] >> (7 - (sx & 7))) & 1) != 0;
                if (black)
                {
                    glyph[dstRow + (x >> 3)] |= (byte)(1 << (7 - (x & 7)));
                }
            }
        }
        return glyph;
    }

    /// <summary>
    /// Decodes an arithmetic text region segment body and composites symbol bitmaps into the page buffer.
    /// Returns true on success; false if the segment uses unsupported features or an error occurred.
    /// </summary>
    public static bool DecodeTextRegion(
        byte[] data, int bodyStart, int bodyEnd,
        Jbig2Symbol[]? referredSymbols,
        int regionWidth, int regionHeight, Jbig2Huffman.Table?[] referredTables, out byte[]? regionResult)
    {
        regionResult = null;

        if (referredSymbols is null)
        {
            return false;
        }
        // An empty symbol set is allowed: every instance ID is then out of range and nothing is composed
        // (the region renders blank), which faithfully matches MuPDF for legitimately-empty dictionaries.

        // Standard region info is 17 bytes (already parsed by caller); text region-specific fields start after that.
        // bodyStart here points to after the 17-byte region info.
        int textFlagsPos = bodyStart + 17; // region_info is 17 bytes at bodyStart
        // Actually wait - let me reconsider. The caller already parsed region_info.
        // The text region body starts with 2-byte text flags (after region info).
        // Let me reorganize: bodyStart is the segment body start; region_info is at bodyStart (17 bytes).
        // Text region flags are at bodyStart + 17.

        if (bodyEnd - textFlagsPos < 6) // 2 flags + 4 numInstances minimum
        {
            return false;
        }

        ushort tf = (ushort)((data[textFlagsPos] << 8) | data[textFlagsPos + 1]);
        int cursor = textFlagsPos + 2;

        bool sbHuffman = (tf & 0x0001) != 0;
        bool sbRefine = (tf & 0x0002) != 0;
        int logSbStrips = (tf >> 2) & 3;
        int sbStrips = 1 << logSbStrips;
        int refCorner = (tf >> 4) & 3; // jbig2dec: (flags & 0x0030) >> 4 — 2 bits only (bit 6 is TRANSPOSED)
        bool transposed = (tf & 0x0040) != 0;
        int sbCombOp = (tf >> 7) & 3;
        bool sbDefPixel = (tf & 0x0200) != 0;
        int sbDsOffset = (tf >> 10) & 0x1F;
        if (sbDsOffset > 15)
        {
            sbDsOffset -= 32; // signed 5-bit
        }
        bool sbRTemplate = (tf & 0x8000) != 0;

        // Arithmetic path supported (refinement handled inline). Huffman coding is decoded in a dedicated
        // routine sharing the same placement geometry.
        if (sbHuffman)
        {
            return DecodeTextRegionHuffman(data, bodyStart, bodyEnd, tf, referredSymbols, regionWidth, regionHeight, referredTables, out regionResult);
        }

        // 7.4.3.1.3: SBRA (4 bytes) — if SBREFINE && !SBRTEMPLATE
        sbyte[] sbrat = new sbyte[4];
        if (sbRefine && !sbRTemplate)
        {
            if (cursor + 4 > bodyEnd) { return false; }
            for (int i = 0; i < 4; i++) sbrat[i] = (sbyte)data[cursor + i];
            cursor += 4;
        }

        // 7.4.3.1.4: SBNUMINSTANCES
        if (cursor + 4 > bodyEnd)
        {
            return false;
        }
        int sbNumInstances = (int)ReadUInt32(data, cursor);
        cursor += 4;

        if (sbNumInstances < 0 || sbNumInstances > 1000000)
        {
            return false;
        }

        // SBSYMCODELEN = ceil(log2(SBNUMSYMS))
        int sbNumSyms = referredSymbols.Length;
        int sbSymCodeLen = 0;
        while ((1L << sbSymCodeLen) < sbNumSyms)
        {
            sbSymCodeLen++;
        }

        // Create text region image (regionWidth × regionHeight, default pixel from SBDEFPIXEL)
        int textStride = (regionWidth + 7) / 8;
        byte[] textImage = new byte[textStride * regionHeight];
        if (sbDefPixel)
        {
            Array.Fill(textImage, (byte)0xFF);
        }

        // Create arithmetic decoders
        var mq = new Jbig2MqDecoder(data, cursor, bodyEnd);
        var iadt = new Jbig2ArithmeticInteger();
        var iafs = new Jbig2ArithmeticInteger();
        var iads = new Jbig2ArithmeticInteger();
        var iait = new Jbig2ArithmeticInteger();
        var iaId = new Jbig2ArithmeticIaid(sbSymCodeLen);
        // Refinement decoders (only used when sbRefine)
        var iari = sbRefine ? new Jbig2ArithmeticInteger() : null;
        var iardw = sbRefine ? new Jbig2ArithmeticInteger() : null;
        var iardh = sbRefine ? new Jbig2ArithmeticInteger() : null;
        var iardx = sbRefine ? new Jbig2ArithmeticInteger() : null;
        var iardy = sbRefine ? new Jbig2ArithmeticInteger() : null;
        int grStatsSize = sbRTemplate ? 1 << 10 : 1 << 13;
        byte[] grStats = sbRefine ? new byte[grStatsSize] : [];

        // 6.4.6: decode initial STRIPT (= -IADT * SBSTRIPS)
        int code = iadt.Decode(mq, out int stripT);
        if (code != 0)
        {
            return false;
        }
        stripT = -stripT * sbStrips;
        
        int firstS = 0;
        int nInstances = 0;
        
        while (nInstances < sbNumInstances)
        {
            // 6.4.5(3b): DT -> strip delta
            code = iadt.Decode(mq, out int dt);
            if (code != 0)
            {
                return false;
            }
            stripT += dt * sbStrips;
        
            // 6.4.7: IAFS -> first S in this strip
            code = iafs.Decode(mq, out int dfs);
            if (code != 0)
            {
                return false;
            }
            firstS += dfs;
            int curs = firstS;
        
            // 6.4.5(3c) inner loop: decode symbols in this strip (do-while pattern from jbig2dec)
            while (true)
            {
                // (3c.iii) IAIT -> current T within strip
                int curt;
                if (sbStrips == 1)
                {
                    curt = 0;
                }
                else
                {
                    code = iait.Decode(mq, out curt);
                    if (code != 0)
                    {
                        return false;
                    }
                }
                int t = stripT + curt;

                // (3b.iv) IAID → symbol ID
                code = iaId.Decode(mq, out int id);
                if (code != 0)
                {
                    return false;
                }

                // (3c.v) look up symbol bitmap. The arithmetic text region treats an out-of-range id or
                // a null symbol as a decode failure, exactly like PDFium's CJBig2_TRDProc::DecodeArith
                // (`if (IDI >= SBNUMSYMS) return nullptr` / `if (!pIBI) return nullptr`) — a stricter
                // contract than the Huffman path, which skips an instance with no bitmap.
                if (id >= sbNumSyms || id < 0)
                {
                    return false;
                }

                Jbig2Symbol ib = referredSymbols[id];

                // 6.4.11: refinement indicator (IARI) and apply refinement. PDFium decodes IARI
                // whenever SBREFINE is set — even for an instance whose symbol is null — then fails
                // the region if the placed image would be null, so the bit position must not depend
                // on the symbol's emptiness.
                if (sbRefine)
                {
                    code = iari!.Decode(mq, out int ri);
                    if (code != 0) { return false; }
                    if (ri != 0)
                    {
                        code = iardw!.Decode(mq, out int rdw);
                        if (code != 0) { return false; }
                        code = iardh!.Decode(mq, out int rdh);
                        if (code != 0) { return false; }
                        code = iardx!.Decode(mq, out int rdx2);
                        if (code != 0) { return false; }
                        code = iardy!.Decode(mq, out int rdy2);
                        if (code != 0) { return false; }
                        if (ib.IsEmpty)
                        {
                            return false; // PDFium: `if (!IBOI) return nullptr`
                        }
                        int newW = ib.Width + rdw;
                        int newH = ib.Height + rdh;
                        if (newW <= 0 || newH <= 0 || newW > 100000 || newH > 100000) { return false; }
                        byte[]? refined = Jbig2Refinement.Decode(
                            sbRTemplate ? 1 : 0, false, sbrat, mq, grStats,
                            newW, newH, ib.Packed, ib.Width, ib.Height,
                            (rdw >> 1) + rdx2, (rdh >> 1) + rdy2);
                        if (refined is null) { return false; }
                        ib = new Jbig2Symbol(newW, newH, refined);
                    }
                }

                if (ib.IsEmpty)
                {
                    return false; // PDFium: `if (!pIBI) return nullptr`
                }

                // (3c.vi) adjust CURS for reference corner
                if (!transposed && (refCorner == RefCornerBottomRight || refCorner == RefCornerTopRight) && ib.Width > 0)
                {
                    curs += ib.Width - 1;
                }
                else if (transposed && (refCorner == RefCornerBottomLeft || refCorner == RefCornerBottomRight) && ib.Height > 0)
                {
                    curs += ib.Height - 1;
                }

                // (3c.vii) S = CURS
                int s = curs;

                // (3c.viii) compute x,y from corner + transposition
                int x, y;
                if (!transposed)
                {
                    switch (refCorner)
                    {
                        case RefCornerBottomLeft: x = s; y = ib.Height > 0 ? t - ib.Height + 1 : t + 1; break;
                        case RefCornerTopLeft: x = s; y = t; break;
                        case RefCornerBottomRight: x = ib.Width > 0 ? s - ib.Width + 1 : s + 1; y = ib.Height > 0 ? t - ib.Height + 1 : t + 1; break;
                        default: x = ib.Width > 0 ? s - ib.Width + 1 : s + 1; y = t; break; // TOPRIGHT
                    }
                }
                else
                {
                    // TRANSPOSED: symbol is placed upright; only the anchor offsets swap axes.
                    // x derives from T, y derives from S (jbig2dec 6.4.12).
                    switch (refCorner)
                    {
                        case RefCornerBottomLeft: x = t; y = ib.Height > 0 ? s - ib.Height + 1 : s + 1; break;
                        case RefCornerTopLeft: x = t; y = s; break;
                        case RefCornerBottomRight: x = ib.Width > 0 ? t - ib.Width + 1 : t + 1; y = ib.Height > 0 ? s - ib.Height + 1 : s + 1; break;
                        default: x = ib.Width > 0 ? t - ib.Width + 1 : t + 1; y = s; break; // TOPRIGHT
                    }
                }

                // (3c.ix) compose symbol into text image (upright; transposition only affects position)
                if (ib.Width > 0 && ib.Height > 0)
                {
                    ComposeSymbol(textImage, regionWidth, regionHeight, textStride, ib, x, y, sbCombOp);
                }

                // (3c.x) advance CURS
                if (ib.Width > 0)
                {
                    if (!transposed && (refCorner == RefCornerBottomLeft || refCorner == RefCornerTopLeft))
                    {
                        curs += ib.Width - 1;
                    }
                    else if (transposed && (refCorner == RefCornerTopLeft || refCorner == RefCornerTopRight))
                    {
                        curs += ib.Height - 1;
                    }
                }

                nInstances++;

                // (3c.xi) IADS → next symbol delta S (or OOB = end of strip)
                code = iads.Decode(mq, out int ids);
                if (code < 0)
                {
                    return false;
                }
                if (code > 0)
                {
                    break; // OOB → end of strip
                }
                curs += ids + sbDsOffset;

                if (nInstances >= sbNumInstances)
                {
                    break;
                }
            }
        }

        // Return the standalone region bitmap; the caller composites it onto the page (types 6/7)
        // or retains it as an intermediate result for a later refinement (type 4).
        regionResult = textImage;
        return true;
    }

    /// <summary>
    /// One refinement/aggregate-coded symbol of a Huffman dictionary (§6.5.8.2). A single instance names
    /// a reference symbol with raw bits, then RDX/RDY (Table B.15) and the arithmetic-coded refinement
    /// bitmap's byte size (Table B.1); the bitmap itself is decoded with the refinement context and the
    /// Huffman stream is advanced past it. Multi-instance aggregates are a nested text region.
    ///
    /// Returns <see cref="Jbig2Symbol.Empty"/> for a dictionary entry that carries no bitmap at all
    /// (REFAGGNINST == 0): PDFium stores a null image for it and every later consumer skips it when
    /// placing instances. The entry still occupies a slot in the dictionary, so the stream stays in
    /// sync.
    /// </summary>
    private static Jbig2Symbol? DecodeHuffmanRefinedSymbol(
        Jbig2Huffman.State hs, byte[] data, int bodyStart, int bodyEnd,
        Jbig2Huffman.Table aggInstTable, sbyte[] sdrat, byte[] grStats, bool sdRTemplate,
        Jbig2Symbol[]? sdInSyms, Jbig2Symbol[] sdNewSyms, int nsymsDecoded, int sdNumInSyms,
        int sbSymCodeLen, int symWidth, int hcHeight)
    {
        int code;
        int refAggNInst = hs.Get(aggInstTable, out code);
        if (code != 0)
        {
            return null;
        }

        if (refAggNInst == 0)
        {
            // T.88 §6.5.8.2: a zero instance count means the dictionary entry has no bitmap of its
            // own. PDFium leaves the image null (`BS = nullptr` before the REFAGGNINST dispatch in
            // JBig2_SddProc.cpp) and stores that null as the symbol, so the entry is exported as an
            // empty glyph that is skipped at placement time.
            return Jbig2Symbol.Empty;
        }

        if (refAggNInst > 1)
        {
            // §6.5.8.2.2 (2): the aggregate is a text region over the input symbols plus the ones already
            // decoded, laid out into this symbol's box (Table 17). Unlike a text region segment, it has no
            // symbol-code table in the stream: PDFium gives every symbol a fixed-width id (the table is
            // the identity), and the instances follow the REFAGGNINST value directly.
            var symbols = new List<Jbig2Symbol>(sdNumInSyms + nsymsDecoded);
            if (sdInSyms is not null)
            {
                symbols.AddRange(sdInSyms);
            }
            for (int i = 0; i < nsymsDecoded; i++)
            {
                symbols.Add(sdNewSyms[i]);
            }

            if (!DecodeTextRegionBodyHuffman(
                    hs, data, bodyEnd, symbols.ToArray(), symWidth, hcHeight,
                    sbStrips: 1, refCorner: RefCornerTopLeft, transposed: false,
                    sbCombOp: 0, sbDefPixel: false, sbDsOffset: 0, sbNumInstances: refAggNInst,
                    fs: Jbig2Huffman.Standard(6), ds: Jbig2Huffman.Standard(8), dt: Jbig2Huffman.Standard(11),
                    rdw: Jbig2Huffman.Standard(15), rdh: Jbig2Huffman.Standard(15),
                    rdx: Jbig2Huffman.Standard(15), rdy: Jbig2Huffman.Standard(15),
                    rsize: Jbig2Huffman.Standard(1),
                    sbRefine: true, sbRTemplate: sdRTemplate, sbrat: sdrat,
                    fixedWidthSymbolIds: true, fixedSymbolIdBits: sbSymCodeLen, out byte[]? aggregate) || aggregate is null)
            {
                return null;
            }

            return new Jbig2Symbol(symWidth, hcHeight, aggregate);
        }

        int symbolId = hs.GetBits(sbSymCodeLen, out code);
        if (code != 0)
        {
            return null;
        }
        int rdx = hs.Get(Jbig2Huffman.Standard(15), out code);
        if (code != 0) { return null; }
        int rdy = hs.Get(Jbig2Huffman.Standard(15), out code);
        if (code != 0) { return null; }
        int bmSize = hs.Get(Jbig2Huffman.Standard(1), out code);
        if (code != 0 || bmSize < 0)
        {
            return null;
        }

        Jbig2Symbol refSym;
        if (symbolId < sdNumInSyms)
        {
            if (sdInSyms is null) { return null; }
            refSym = sdInSyms[symbolId];
        }
        else if (symbolId - sdNumInSyms < nsymsDecoded)
        {
            refSym = sdNewSyms[symbolId - sdNumInSyms];
        }
        else
        {
            return null;
        }

        // PDFium refuses to refine a symbol that has no bitmap (JBig2_SddProc.cpp's
        // `if (!sbsyms_idi) return nullptr`), which fails the whole dictionary segment.
        if (refSym.IsEmpty)
        {
            return null;
        }

        hs.SkipToByteBoundary();
        int start = hs.Offset();
        int stride = (symWidth + 7) / 8;
        if (bmSize == 0)
        {
            // A zero BMSIZE means the bitmap is stored uncompressed-sized: the caller skips its raw size.
            bmSize = hcHeight * stride;
        }
        if (start < 0 || start + bmSize > bodyEnd)
        {
            return null;
        }

        var mq = new Jbig2MqDecoder(data, start, start + bmSize);
        byte[]? bits = Jbig2Refinement.Decode(
            sdRTemplate ? 1 : 0, false, sdrat, mq, grStats,
            symWidth, hcHeight, refSym.Packed, refSym.Width, refSym.Height, rdx, rdy);
        if (bits is null)
        {
            return null;
        }

        hs.Advance(bmSize);
        return new Jbig2Symbol(symWidth, hcHeight, bits);
    }

    /// <summary>
    /// Decodes a Huffman-coded text region (SBHUFF=1). Non-refinement path only: instance deltas via the
    /// FS/DS/DT tables and symbol IDs via the runlength-constructed symbol ID table; placement geometry is
    /// identical to the arithmetic path. Mirrors jbig2_text.c's Huffman branch, validated against MuPDF.
    /// </summary>
    private static bool DecodeTextRegionHuffman(
        byte[] data, int bodyStart, int bodyEnd, ushort tf,
        Jbig2Symbol[] referredSymbols, int regionWidth, int regionHeight,
        Jbig2Huffman.Table?[] referredTables, out byte[]? regionResult)
    {
        regionResult = null;

        bool sbRefine = (tf & 0x0002) != 0;
        int logSbStrips = (tf >> 2) & 3;
        int sbStrips = 1 << logSbStrips;
        int refCorner = (tf >> 4) & 3;
        bool transposed = (tf & 0x0040) != 0;
        int sbCombOp = (tf >> 7) & 3;
        bool sbDefPixel = (tf & 0x0200) != 0;
        int sbDsOffset = (tf >> 10) & 0x1F;
        if (sbDsOffset > 15)
        {
            sbDsOffset -= 32;
        }
        bool sbRTemplate = (tf & 0x8000) != 0;

        // §7.4.3.1 header field order (matching PDFium, which decodes the pdf.js SBHUFF+SBREFINE
        // corpus): region-info (17) + SBTF (2) + SBHF (2 if SBHUFF) + SBRA (4 if SBREFINE && !SBRTEMPLATE)
        // + SBNUMINSTANCES (4). MuPDF skips SBRA on the Huffman path (and gets an all-black fallback
        // on these files); the previous Poso gate rejected SBREFINE outright. Read HF first, then SBRA.
        int minLen = 17 + 2 + 2 + 4;
        if (sbRefine && !sbRTemplate)
        {
            minLen += 4;
        }
        if (bodyEnd - bodyStart < minLen)
        {
            return false;
        }
        ushort hf = (ushort)((data[bodyStart + 19] << 8) | data[bodyStart + 20]);
        int cursor = bodyStart + 21;
        sbyte[] sbrat = new sbyte[4];
        if (sbRefine && !sbRTemplate)
        {
            for (int i = 0; i < 4; i++)
            {
                sbrat[i] = (sbyte)data[cursor + i];
            }
            cursor += 4;
        }
        // 7.4.3.1.5: SBNUMINSTANCES.
        int sbNumInstances = (int)ReadUInt32(data, cursor);
        cursor += 4;
        if (sbNumInstances < 0 || sbNumInstances > 1000000)
        {
            return false;
        }

        int tableIndex = 0;
        Jbig2Huffman.Table? fs = SelectTextTable(0, hf & 0x0003, referredTables, ref tableIndex);
        Jbig2Huffman.Table? ds = SelectTextTable(1, (hf >> 2) & 0x0003, referredTables, ref tableIndex);
        Jbig2Huffman.Table? dt = SelectTextTable(2, (hf >> 4) & 0x0003, referredTables, ref tableIndex);
        if (fs is null || ds is null || dt is null)
        {
            return false;
        }

        // §7.4.3.1.6: refinement tables, only when SBREFINE=1. RDW/H/X/Y select B.14 (small), B.15 (large),
        // or a custom table; RSIZE selects B.1 or a custom.
        Jbig2Huffman.Table? rdw = null, rdh = null, rdx = null, rdy = null, rsize = null;
        if (sbRefine)
        {
            rdw = SelectTable((hf >> 6) & 3, referredTables, ref tableIndex, 14, 15, oobTwo: false, customTwo: false);
            rdh = SelectTable((hf >> 8) & 3, referredTables, ref tableIndex, 14, 15, oobTwo: false, customTwo: false);
            rdx = SelectTable((hf >> 10) & 3, referredTables, ref tableIndex, 14, 15, oobTwo: false, customTwo: false);
            rdy = SelectTable((hf >> 12) & 3, referredTables, ref tableIndex, 14, 15, oobTwo: false, customTwo: false);
            int rsizeSel = (hf >> 14) & 1;
            rsize = rsizeSel == 0 ? Jbig2Huffman.Standard(1) : NextCustom(referredTables, ref tableIndex);
            if (rdw is null || rdh is null || rdx is null || rdy is null || rsize is null)
            {
                return false;
            }
        }

        var hs = new Jbig2Huffman.State(data, cursor, bodyEnd);
        return DecodeTextRegionBodyHuffman(
            hs, data, bodyEnd, referredSymbols, regionWidth, regionHeight,
            sbStrips, refCorner, transposed, sbCombOp, sbDefPixel, sbDsOffset, sbNumInstances,
            fs, ds, dt, rdw, rdh, rdx, rdy, rsize, sbRefine, sbRTemplate, sbrat,
            fixedWidthSymbolIds: false, fixedSymbolIdBits: 0, out regionResult);
    }

    /// <summary>
    /// The instance-placement core of a Huffman text region (T.88 6.4), shared by a text region segment
    /// and by a multi-instance aggregate symbol inside a symbol dictionary (6.5.8.2.2). The caller has
    /// already consumed the segment header and selected the tables; <paramref name="hs"/> continues at
    /// the current bit position, which is what lets the aggregate interleave with the dictionary stream.
    /// </summary>
    private static bool DecodeTextRegionBodyHuffman(
        Jbig2Huffman.State hs,
        byte[] data,
        int bodyEnd,
        Jbig2Symbol[] referredSymbols,
        int regionWidth,
        int regionHeight,
        int sbStrips,
        int refCorner,
        bool transposed,
        int sbCombOp,
        bool sbDefPixel,
        int sbDsOffset,
        int sbNumInstances,
        Jbig2Huffman.Table fs,
        Jbig2Huffman.Table ds,
        Jbig2Huffman.Table dt,
        Jbig2Huffman.Table? rdw,
        Jbig2Huffman.Table? rdh,
        Jbig2Huffman.Table? rdx,
        Jbig2Huffman.Table? rdy,
        Jbig2Huffman.Table? rsize,
        bool sbRefine,
        bool sbRTemplate,
        sbyte[] sbrat,
        bool fixedWidthSymbolIds,
        int fixedSymbolIdBits,
        out byte[]? regionResult)
    {
        regionResult = null;
        int logSbStrips = 0;
        while ((1 << logSbStrips) < sbStrips)
        {
            logSbStrips++;
        }

        int sbNumSyms = referredSymbols.Length;
        Jbig2Huffman.Table? symCodes = null;
        int symIdBits = fixedSymbolIdBits;
        if (fixedWidthSymbolIds)
        {
            // An aggregate inside a symbol dictionary numbers its instances with plain fixed-width
            // bits; the text region segment's run-coded table is not in the stream. PDFium builds the
            // identity table with codelen = max(1, CeilLog2(SDNUMINSYMS + SDNUMNEWSYMS)) — the width
            // of the dictionary's full declared symbol count, not the number currently available
            // (JBig2_SddProc.cpp, the REFAGGNINST > 1 branch). |fixedSymbolIdBits| carries that.
        }
        else
        {
            symCodes = BuildSymbolIdTable(hs, sbNumSyms);
            if (symCodes is null)
            {
                return false;
            }
        }

        // Arithmetic refinement stats persist across instances within this text region (§6.4.11).
        int grStatsSize = sbRTemplate ? 1 << 10 : 1 << 13;
        byte[] grStats = sbRefine ? new byte[grStatsSize] : [];

        int textStride = (regionWidth + 7) / 8;
        byte[] textImage = new byte[textStride * regionHeight];
        if (sbDefPixel)
        {
            Array.Fill(textImage, (byte)0xFF);
        }

        // 6.4.6: initial STRIPT via DT.
        int stripT = hs.Get(dt, out int code);
        if (code != 0)
        {
            return false;
        }
        stripT = -stripT * sbStrips;

        int firstS = 0;
        int nInstances = 0;
        while (nInstances < sbNumInstances)
        {
            int dtv = hs.Get(dt, out code);
            if (code != 0)
            {
                return false;
            }
            stripT += dtv * sbStrips;

            int dfs = hs.Get(fs, out code);
            if (code != 0)
            {
                return false;
            }
            firstS += dfs;
            int curs = firstS;

            while (true)
            {
                int curt;
                if (sbStrips == 1)
                {
                    curt = 0;
                }
                else
                {
                    curt = hs.GetBits(logSbStrips, out code);
                }
                int t = stripT + curt;

                int id = fixedWidthSymbolIds ? hs.GetBits(symIdBits, out code) : hs.Get(symCodes!, out code);
                if (code != 0)
                {
                    return false;
                }

                Jbig2Symbol ib = id >= sbNumSyms || id < 0 ? Jbig2Symbol.Empty : referredSymbols[id];

                // §6.4.11: SBREFINE interleave — one RI bit decides whether this instance is placed as-is
                // or refined. When refined: RDW/RDH/RDX/RDY/BMSIZE come from Huffman tables, then BMSIZE
                // bytes of arithmetic refinement codeword follow (decoded by Jbig2Refinement). Huffman
                // and arithmetic decoders share the underlying byte stream, so align to a byte boundary
                // before entering arithmetic and advance by BMSIZE afterwards.
                if (sbRefine)
                {
                    int ri = hs.GetBits(1, out code);
                    if (code != 0)
                    {
                        return false;
                    }
                    if (ri != 0)
                    {
                        int rdwVal = hs.Get(rdw!, out code);
                        if (code != 0) { return false; }
                        int rdhVal = hs.Get(rdh!, out code);
                        if (code != 0) { return false; }
                        int rdxVal = hs.Get(rdx!, out code);
                        if (code != 0) { return false; }
                        int rdyVal = hs.Get(rdy!, out code);
                        if (code != 0) { return false; }
                        int bmSize = hs.Get(rsize!, out code);
                        if (code != 0) { return false; }
                        hs.SkipToByteBoundary();

                        // PDFium refuses to refine an instance whose reference glyph is null
                        // (CJBig2_TRDProc::DecodeHuffman's `if (!IBOI) return nullptr`).
                        if (ib.IsEmpty)
                        {
                            return false;
                        }

                        int newW = ib.Width + rdwVal;
                        int newH = ib.Height + rdhVal;
                        if (newW <= 0 || newH <= 0 || newW > 100000 || newH > 100000)
                        {
                            return false;
                        }

                        // NB: PDFium's Huffman-refinement uses `>> 2` (arith path uses `>> 1`), which is
                        // why the pdf.js SBHUFF+SBREFINE corpus renders differently in Chrome vs a strict
                        // spec decoder. We mirror Chrome's behaviour because that is what our oracle
                        // (PDFium) produced.
                        int alignX = (rdwVal >> 2) + rdxVal;
                        int alignY = (rdhVal >> 2) + rdyVal;
                        int mqStart = hs.Offset();
                        int mqEnd = Math.Min(bodyEnd, mqStart + bmSize);
                        if (mqStart > mqEnd)
                        {
                            return false;
                        }
                        var mq = new Jbig2MqDecoder(data, mqStart, mqEnd);
                        byte[]? refined = Jbig2Refinement.Decode(
                            sbRTemplate ? 1 : 0, false, sbrat, mq, grStats,
                            newW, newH, ib.Packed, ib.Width, ib.Height, alignX, alignY);
                        if (refined is null)
                        {
                            return false;
                        }
                        ib = new Jbig2Symbol(newW, newH, refined);

                        hs.Advance(bmSize);
                    }
                }

                // A dictionary entry that carries no bitmap (PDFium's null image) is skipped: the
                // instance contributes nothing and does not count towards SBNUMINSTANCES, but the
                // strip's next coordinate delta is still read (PDFium's `continue` lands on the loop
                // head, where bFirst is false and IADS/IDS is decoded).
                bool place = !ib.IsEmpty;

                if (place && !transposed && (refCorner == RefCornerBottomRight || refCorner == RefCornerTopRight) && ib.Width > 0)
                {
                    curs += ib.Width - 1;
                }
                else if (place && transposed && (refCorner == RefCornerBottomLeft || refCorner == RefCornerBottomRight) && ib.Height > 0)
                {
                    curs += ib.Height - 1;
                }

                int s = curs;
                int x, y;
                if (!transposed)
                {
                    switch (refCorner)
                    {
                        case RefCornerBottomLeft: x = s; y = ib.Height > 0 ? t - ib.Height + 1 : t + 1; break;
                        case RefCornerTopLeft: x = s; y = t; break;
                        case RefCornerBottomRight: x = ib.Width > 0 ? s - ib.Width + 1 : s + 1; y = ib.Height > 0 ? t - ib.Height + 1 : t + 1; break;
                        default: x = ib.Width > 0 ? s - ib.Width + 1 : s + 1; y = t; break; // TOPRIGHT
                    }
                }
                else
                {
                    switch (refCorner)
                    {
                        case RefCornerBottomLeft: x = t; y = ib.Height > 0 ? s - ib.Height + 1 : s + 1; break;
                        case RefCornerTopLeft: x = t; y = s; break;
                        case RefCornerBottomRight: x = ib.Width > 0 ? t - ib.Width + 1 : t + 1; y = ib.Height > 0 ? s - ib.Height + 1 : s + 1; break;
                        default: x = ib.Width > 0 ? t - ib.Width + 1 : t + 1; y = s; break; // TOPRIGHT
                    }
                }

                if (place && ib.Width > 0 && ib.Height > 0)
                {
                    ComposeSymbol(textImage, regionWidth, regionHeight, textStride, ib, x, y, sbCombOp);
                }

                if (place && ib.Width > 0)
                {
                    if (!transposed && (refCorner == RefCornerBottomLeft || refCorner == RefCornerTopLeft))
                    {
                        curs += ib.Width - 1;
                    }
                    else if (transposed && (refCorner == RefCornerTopLeft || refCorner == RefCornerTopRight))
                    {
                        curs += ib.Height - 1;
                    }

                    nInstances++;
                }

                // The strip carries one more IDS after its last instance (PDFium reads it and expects
                // OOB); that trailing symbol is what closes the strip.
                int ids = hs.Get(ds, out code);
                if (code < 0)
                {
                    return false;
                }
                if (code > 0)
                {
                    break; // OOB → end of strip
                }
                curs += ids + sbDsOffset;

                if (nInstances >= sbNumInstances)
                {
                    break;
                }
            }
        }

        regionResult = textImage;
        return true;
    }

    /// <summary>
    /// Text-region slot selector mirroring jbig2_text.c's switches: FS→B.6/B.7/custom, DS→B.8/B.9/B.10/custom,
    /// DT→B.11/B.12/B.13/custom (case value 2 is invalid for FS; reserved handling left to callers).
    /// </summary>
    private static Jbig2Huffman.Table? SelectTextTable(int kind, int sel, Jbig2Huffman.Table?[] tables, ref int tableIndex)
    {
        if (sel == 3)
        {
            return NextCustom(tables, ref tableIndex);
        }
        return kind switch
        {
            0 when sel is 0 or 1 => Jbig2Huffman.Standard(6 + sel),                 // FS: B.6 / B.7
            1 when sel is 0 or 1 or 2 => Jbig2Huffman.Standard(8 + sel),            // DS: B.8 / B.9 / B.10
            2 when sel is 0 or 1 or 2 => Jbig2Huffman.Standard(11 + sel),           // DT: B.11 / B.12 / B.13
            _ => null,
        };
    }

    /// <summary>
    /// Constructs the text-region symbol ID Huffman table (§7.4.3.1.7): reads 35 4-bit run-code lengths,
    /// builds the run-code table, run-length decodes the per-symbol code lengths, aligns to a byte boundary,
    /// then builds the symbol ID table. Returns null on malformed input.
    /// </summary>
    private static Jbig2Huffman.Table? BuildSymbolIdTable(Jbig2Huffman.State hs, int sbNumSyms)
    {
        if (sbNumSyms <= 0)
        {
            return null;
        }

        Jbig2Huffman.Line[] runLines = new Jbig2Huffman.Line[35];
        for (int i = 0; i < 35; i++)
        {
            int len = hs.GetBits(4, out int code);
            if (code != 0)
            {
                return null;
            }
            runLines[i] = new Jbig2Huffman.Line(len, 0, i);
        }
        Jbig2Huffman.Table runCodes = Jbig2Huffman.Build(runLines, false);

        Jbig2Huffman.Line[] symLines = new Jbig2Huffman.Line[sbNumSyms];
        int index = 0;
        while (index < sbNumSyms)
        {
            int c = hs.Get(runCodes, out int code);
            if (code != 0 || c < 0 || c >= 35)
            {
                return null;
            }
            int len;
            int range;
            if (c < 32)
            {
                len = c;
                range = 1;
            }
            else
            {
                if (c == 32)
                {
                    if (index < 1)
                    {
                        return null; // run length with no antecedent
                    }
                    len = symLines[index - 1].Preflen;
                    range = hs.GetBits(2, out code) + 3;
                }
                else
                {
                    len = 0;
                    range = c == 33 ? hs.GetBits(3, out code) + 3 : hs.GetBits(7, out code) + 11;
                }
                if (code != 0)
                {
                    return null;
                }
            }
            if (index + range > sbNumSyms)
            {
                range = sbNumSyms - index;
            }
            for (int r = 0; r < range; r++)
            {
                symLines[index + r] = new Jbig2Huffman.Line(len, 0, index + r);
            }
            index += range;
        }
        if (index < sbNumSyms)
        {
            return null;
        }

        hs.SkipToByteBoundary();
        return Jbig2Huffman.Build(symLines, false);
    }

    /// <summary>
    /// Decodes a single symbol bitmap via the generic region arithmetic kernel, reusing the shared MQ decoder and stats.
    /// </summary>
    private static byte[]? DecodeSymbolGenericRegion(Jbig2MqDecoder mq, byte[] stats, int template, sbyte[] gbat, int width, int height)
    {
        int stride = (width + 7) / 8;
        byte[] image = new byte[stride * height];

        // Symbol dictionary generic regions use TPGDON=0 and MMR=0.
        // Reuse the same kernel logic as Jbig2GenericArithmetic but with external MQ + stats.
        bool defaultGbat = Jbig2GenericArithmetic.MatchesDefaultGbat(template, gbat);

        switch (template)
        {
            case 0:
                if (defaultGbat)
                {
                    Jbig2GenericArithmetic.DecodeTemplate0(mq, stats, image, width, height, stride);
                }
                else
                {
                    if (!Jbig2GenericArithmetic.GbatInField(gbat, 8))
                    {
                        return null;
                    }
                    Jbig2GenericArithmetic.DecodeTemplate0Unopt(mq, stats, image, gbat, width, height, stride);
                }
                break;
            case 1:
                if (defaultGbat)
                {
                    Jbig2GenericArithmetic.DecodeTemplate1(mq, stats, image, width, height, stride);
                }
                else
                {
                    if (!Jbig2GenericArithmetic.GbatInField(gbat, 2))
                    {
                        return null;
                    }
                    Jbig2GenericArithmetic.DecodeTemplate1Unopt(mq, stats, image, gbat, width, height, stride);
                }
                break;
            case 2:
                if (defaultGbat)
                {
                    Jbig2GenericArithmetic.DecodeTemplate2(mq, stats, image, width, height, stride);
                }
                else
                {
                    if (!Jbig2GenericArithmetic.GbatInField(gbat, 2))
                    {
                        return null;
                    }
                    Jbig2GenericArithmetic.DecodeTemplate2Unopt(mq, stats, image, gbat, width, height, stride);
                }
                break;
            case 3:
                if (defaultGbat)
                {
                    Jbig2GenericArithmetic.DecodeTemplate3(mq, stats, image, width, height, stride);
                }
                else
                {
                    if (!Jbig2GenericArithmetic.GbatInField(gbat, 2))
                    {
                        return null;
                    }
                    Jbig2GenericArithmetic.DecodeTemplate3Unopt(mq, stats, image, gbat, width, height, stride);
                }
                break;
            default:
                return null;
        }
        return image;
    }

    private static void ComposeSymbol(byte[] image, int imgWidth, int imgHeight, int imgStride, Jbig2Symbol sym, int x, int y, int combOp)
    {
        int symStride = sym.Stride;
        for (int sy = 0; sy < sym.Height; sy++)
        {
            int iy = y + sy;
            if (iy < 0 || iy >= imgHeight)
            {
                continue;
            }
            for (int sx = 0; sx < sym.Width; sx++)
            {
                int ix = x + sx;
                if (ix < 0 || ix >= imgWidth)
                {
                    continue;
                }
                bool symBlack = ((sym.Packed[sy * symStride + (sx >> 3)] >> (7 - (sx & 7))) & 1) != 0;
                int imgIdx = iy * imgStride + (ix >> 3);
                int mask = 1 << (7 - (ix & 7));
                bool curBlack = (image[imgIdx] & mask) != 0;

                bool result = combOp switch
                {
                    0 => curBlack || symBlack,    // OR
                    1 => curBlack && symBlack,    // AND
                    2 => curBlack ^ symBlack,     // XOR
                    3 => curBlack == symBlack,    // XNOR
                    _ => symBlack,                 // 4 = REPLACE
                };
                image[imgIdx] = result ? (byte)(image[imgIdx] | mask) : (byte)(image[imgIdx] & ~mask);
            }
        }
    }

    internal static bool CompositeRegionOntoPage(byte[] page, int pageWidth, int rowBytes, byte[] regionBits, int regionWidth, int regionHeight, int regionX, int regionY, int combop)
    {
        int regionStride = (regionWidth + 7) / 8;
        int pageHeight = page.Length / rowBytes;

        for (int y = 0; y < regionHeight; y++)
        {
            int pageRow = regionY + y;
            if (pageRow < 0 || pageRow >= pageHeight)
            {
                continue;
            }
            for (int x = 0; x < regionWidth; x++)
            {
                int pageCol = regionX + x;
                if (pageCol < 0 || pageCol >= pageWidth)
                {
                    continue;
                }
                bool black = ((regionBits[y * regionStride + (x >> 3)] >> (7 - (x & 7))) & 1) != 0;
                int pageIndex = pageRow * rowBytes + (pageCol >> 3);
                int mask = 1 << (7 - (pageCol & 7));
                bool current = (page[pageIndex] & mask) != 0;
                bool result = combop switch
                {
                    0 => current || black,
                    1 => current && black,
                    2 => current ^ black,
                    3 => current == black,
                    _ => black,
                };
                page[pageIndex] = result ? (byte)(page[pageIndex] | mask) : (byte)(page[pageIndex] & ~mask);
            }
        }
        return true;
    }

    /// <summary>
    /// Decodes a multi-instance aggregation (text region within symbol dictionary, SDREFAGG with IAAI>1).
    /// Composes multiple symbol instances into a width×height bitmap using simplified text-region params.
    /// </summary>
    private static byte[]? DecodeAggregation(
        Jbig2MqDecoder mq,
        Jbig2ArithmeticInteger iadt, Jbig2ArithmeticInteger iafs,
        Jbig2ArithmeticInteger iads, Jbig2ArithmeticInteger iait,
        Jbig2ArithmeticIaid iaId,
        Jbig2ArithmeticInteger iari, Jbig2ArithmeticInteger iardw, Jbig2ArithmeticInteger iardh,
        Jbig2ArithmeticInteger iardx, Jbig2ArithmeticInteger iardy,
        byte[] grStats, bool sbrTemplate,
        Jbig2Symbol[]? inSyms, Jbig2Symbol[] newSyms, int nSymsDecoded,
        int numInSyms, int symCodeLen, int width, int height, int numInstances)
    {
        // Compose all available symbols (input + decoded-so-far)
        int totalAvail = numInSyms + nSymsDecoded;
        if (totalAvail == 0) { return null; }

        // jbig2 runs the text region with: SBSTRIPS=1, REFCORNER=TOPLEFT, combop=OR, no transpose,
        // SBDSOFFSET=0, SBREFINE=1, SBRTEMPLATE=SDRTEMPLATE and a zeroed SBRA (grat) — tparams.sbrat is
        // never populated in the symbol-dictionary caller, so it stays all-zero.
        const int strips = 1;
        sbyte[] grat = new sbyte[4];

        int stride = (width + 7) / 8;
        byte[] image = new byte[stride * height];

        // 6.4.6: initial STRIPT
        int code = iadt.Decode(mq, out int stripT);
        if (code != 0) { return null; }
        stripT = -stripT * strips;

        int firstS = 0;
        int nPlaced = 0;

        while (nPlaced < numInstances)
        {
            code = iadt.Decode(mq, out int dt);
            if (code != 0) { break; }
            stripT += dt * strips;

            code = iafs.Decode(mq, out int dfs);
            if (code != 0) { break; }
            firstS += dfs;
            int curs = firstS;

            while (true)
            {
                // strips==1 → IAIT skipped (CURT=0)
                int t = stripT;

                code = iaId.Decode(mq, out int id);
                if (code != 0) { return nPlaced > 0 ? image : null; }

                // Look up symbol. PDFium's DecodeArith fails the whole aggregate on an
                // out-of-range id or an instance that resolves to no bitmap, so the empty
                // dictionary entries of the refinement/aggregate path cannot be referenced.
                Jbig2Symbol ib;
                if (id < numInSyms && inSyms != null && id < inSyms.Length)
                {
                    ib = inSyms[id];
                }
                else if (id >= numInSyms && (id - numInSyms) < nSymsDecoded)
                {
                    ib = newSyms[id - numInSyms];
                }
                else
                {
                    return null;
                }

                // SBREFINE (jbig2 tparams.SBREFINE=1): IARI, and when set RDW/RDH/RDX/RDY + arithmetic
                // refinement against the reference glyph (grat zeroed, template from SDRTEMPLATE).
                code = iari.Decode(mq, out int ri);
                if (code != 0) { return nPlaced > 0 ? image : null; }
                if (ri != 0)
                {
                    // RDW/RDH/RDX/RDY are consumed whenever RI is set, even if the reference glyph is
                    // missing (jbig2 reads them before the `if (IB)` guard), so the stream stays in sync.
                    code = iardw.Decode(mq, out int rdw);
                    if (code != 0) { return null; }
                    code = iardh.Decode(mq, out int rdh);
                    if (code != 0) { return null; }
                    code = iardx.Decode(mq, out int rdx);
                    if (code != 0) { return null; }
                    code = iardy.Decode(mq, out int rdy);
                    if (code != 0) { return null; }
                    if (ib.IsEmpty)
                    {
                        return null; // PDFium: `if (!IBOI) return nullptr`
                    }
                    int newW = ib.Width + rdw;
                    int newH = ib.Height + rdh;
                    if (newW <= 0 || newH <= 0 || newW > 100000 || newH > 100000) { return null; }
                    byte[]? refined = Jbig2Refinement.Decode(
                        sbrTemplate ? 1 : 0, false, grat, mq, grStats,
                        newW, newH, ib.Packed, ib.Width, ib.Height,
                        (rdw >> 1) + rdx, (rdh >> 1) + rdy);
                    if (refined is null) { return null; }
                    ib = new Jbig2Symbol(newW, newH, refined);
                }
                if (ib.IsEmpty)
                {
                    return null; // PDFium: `if (!pIBI) return nullptr`
                }

                // TOPLEFT: x=curs, y=t
                int x = curs;
                int y = t;
                if (ib.Width > 0 && ib.Height > 0)
                {
                    ComposeSymbol(image, width, height, stride, ib, x, y, 0 /* OR */);
                }

                // Advance curs
                if (ib.Width > 0) { curs += ib.Width - 1; }
                nPlaced++;

                code = iads.Decode(mq, out int ids);
                if (code > 0) { break; } // OOB = end of strip
                if (code < 0) { return nPlaced > 0 ? image : null; }
                curs += ids;
            }
        }
        return nPlaced > 0 ? image : null;
    }

    private static uint ReadUInt32(byte[] data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
}
