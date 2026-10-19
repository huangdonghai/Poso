using System.Collections.Generic;

namespace Poso.Pdf.Filters.Codecs;

/// <summary>
/// A bilevel JBIG2 page bitmap: row-major, one bit per pixel packed MSB-first, 1 = black.
/// </summary>
public readonly record struct Jbig2Page(int Width, int Height, byte[] Packed)
{
    public bool IsBlack(int x, int y) => ((Packed[(y * ((Width + 7) / 8)) + (x / 8)] >> (7 - (x % 8))) & 1) == 1;
}

/// <summary>
/// JBIG2 (ITU-T T.88 / ISO 14492) page-stream decoder. Handles page-information, MMR and arithmetic
/// generic regions (templates 0-3, default/custom GBAT, TPGDON), arithmetic symbol dictionaries (type 0),
/// and arithmetic text regions (types 6/7) — all validated against MuPDF pixel-for-pixel.
/// Unsupported segments (Huffman, refinement, halftone, XOR/XNOR compositing) cause null return.
/// </summary>
public static class Jbig2Decoder
{
    private const int SymbolDictionary = 0;
    private const int IntermediateTextRegion = 4;
    private const int ImmediateTextRegion = 6;
    private const int ImmediateLosslessTextRegion = 7;
    private const int PatternDictionary = 16;
    private const int ImmediateHalftoneRegion = 22;
    private const int ImmediateLosslessHalftoneRegion = 23;
    private const int CodedGenericRegion = 36;
    private const int IntermediateGenericRegion = 37;
    private const int PageInformation = 48;
    private const int EndOfPage = 49;
    private const int EndOfStripe = 50;
    private const int ImmediateGenericRegion = 38;
    private const int ImmediateLosslessGenericRegion = 39;
    private const int IntermediateGenericRefinement = 40;
    private const int ImmediateGenericRefinement = 42;
    private const int ImmediateLosslessGenericRefinement = 43;
    private const int CodeTable = 53;
    private const int RegionInformationFieldLength = 17;

    /// <summary>Decodes the supported subset, or returns null for unsupported/complex streams.</summary>
    public static Jbig2Page? Decode(byte[] data)
    {
        int position = 0;

        // Optional file header: 8-byte signature (0x97 'J' 'B' '2' CR LF 0x1A LF) + flags + page count.
        if (data.Length >= 8
            && data[0] == 0x97 && data[1] == 0x4A && data[2] == 0x42 && data[3] == 0x32
            && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
        {
            position = 8;
            byte fileFlags = data[position++];
            if ((fileFlags & 0x02) == 0 && position + 4 <= data.Length)
            {
                position += 4; // numberOfPages (ignored; PDF images are a single page)
            }
        }

        int pageWidth = 0;
        int pageHeight = 0;
        byte[]? page = null;
        int rowBytes = 0;
        bool pageStarted = false;
        bool defaultPixelBlack = false;
        bool pageGrowable = false;

        // Grow the (striped, initially-unknown-height) page buffer to hold at least `rows`, filling new rows
        // with the default pixel. Reassigns the captured `page`/`pageHeight` locals.
        void EnsurePageRows(int rows)
        {
            if (!pageGrowable || page is null || rows <= pageHeight)
            {
                return;
            }

            byte fill = defaultPixelBlack ? (byte)0xFF : (byte)0x00;
            byte[] grown = new byte[rowBytes * rows];
            for (int i = pageHeight * rowBytes; i < grown.Length; i++)
            {
                grown[i] = fill;
            }
            Buffer.BlockCopy(page, 0, grown, 0, page.Length);
            page = grown;
            pageHeight = rows;
        }

        // Mirrors jbig2_find_table: collect the type-53 Huffman code tables referenced by a segment, in
        // referred-segment order, so the dictionary / text region can index custom tables by position.
        static Jbig2Huffman.Table?[] CollectCodeTables(int[] referredSegments, Dictionary<int, Jbig2Huffman.Table> codeTables)
        {
            if (referredSegments.Length == 0)
            {
                return [];
            }
            var list = new List<Jbig2Huffman.Table?>();
            for (int i = 0; i < referredSegments.Length; i++)
            {
                if (codeTables.TryGetValue(referredSegments[i], out var t))
                {
                    list.Add(t);
                }
            }
            return list.ToArray();
        }

        // Segment registry: maps segment number → exported symbol dictionary
        var symbolRegistry = new Dictionary<int, Jbig2Symbol[]>();
        // Pattern dictionary registry: maps segment number → decoded pattern dictionary (for halftone)
        var patternRegistry = new Dictionary<int, Jbig2PatternDict>();
        // Intermediate region results: maps segment number → decoded region bitmap (for refinement)
        var regionResults = new Dictionary<int, (byte[] Bits, int Width, int Height)>();
        // Huffman code-table registry: maps segment number → custom table (segment type 53)
        var codeTables = new Dictionary<int, Jbig2Huffman.Table>();

        while (position + 11 <= data.Length)
        {
            if (!TryReadSegmentHeader(data, ref position, out int segmentNumber, out int segmentType, out int segmentLength, out int[] referredSegments, out int segmentRows))
            {
                return null;
            }

            if (segmentLength < 0 || position + segmentLength > data.Length)
            {
                return null; // unknown/overflowing segment length: cannot trust the stream
            }

            int bodyStart = position;
            int bodyEnd = position + segmentLength;

            switch (segmentType)
            {
                case PageInformation:
                {
                    if (segmentLength < 19)
                    {
                        return null;
                    }

                    pageWidth = (int)ReadUInt32(data, bodyStart);
                    uint rawHeight = ReadUInt32(data, bodyStart + 4);
                    byte pageFlags = data[bodyStart + 16];
                    // Per jbig2 (jbig2_image_clear(flags & 4)): the default-pixel value is bit 0x04.
                    defaultPixelBlack = (pageFlags & 0x04) != 0;
                    if (pageWidth <= 0 || pageWidth > 100000)
                    {
                        return null;
                    }

                    rowBytes = (pageWidth + 7) / 8;
                    if (rawHeight == 0xFFFFFFFF)
                    {
                        // Initially-unknown height (striped organization): grow the buffer on demand as
                        // regions composite at their absolute SBY (final height = highest row reached).
                        pageGrowable = true;
                        pageHeight = 0;
                        page = [];
                    }
                    else
                    {
                        if (rawHeight <= 0 || rawHeight > 100000)
                        {
                            return null;
                        }

                        pageGrowable = false;
                        pageHeight = (int)rawHeight;
                        page = new byte[rowBytes * pageHeight];
                        if (defaultPixelBlack)
                        {
                            Array.Fill(page, (byte)0xFF);
                        }
                    }

                    pageStarted = true;
                    break;
                }

                case SymbolDictionary:
                {
                    // Gather referred dictionaries
                    var referred = new Jbig2Symbol[]?[referredSegments.Length];
                    for (int i = 0; i < referredSegments.Length; i++)
                    {
                        symbolRegistry.TryGetValue(referredSegments[i], out referred[i]);
                    }

                    Jbig2Symbol[]? dict = Jbig2SymbolDict.DecodeDictionary(data, bodyStart, bodyEnd, referred, CollectCodeTables(referredSegments, codeTables));
                    if (dict is null)
                    {
                        return null;
                    }
                    symbolRegistry[segmentNumber] = dict;
                    break;
                }

                case CodeTable:
                {
                    // §7.4.2.1.5 / Annex B.2: a user-supplied Huffman code table. Parse and register by segment
                    // number so a later symbol dictionary / text region can reference it (jbig2_find_table).
                    Jbig2Huffman.Table? custom = Jbig2Huffman.BuildCustom(data, bodyStart, bodyEnd);
                    if (custom is null)
                    {
                        return null;
                    }
                    codeTables[segmentNumber] = custom;
                    break;
                }

                case ImmediateTextRegion:
                case ImmediateLosslessTextRegion:
                case IntermediateTextRegion:
                {
                    if (bodyEnd - bodyStart < RegionInformationFieldLength + 6)
                    {
                        return null;
                    }

                    // Parse region info
                    int regWidth = (int)ReadUInt32(data, bodyStart);
                    int regHeight = (int)ReadUInt32(data, bodyStart + 4);
                    int regX = (int)ReadUInt32(data, bodyStart + 8);
                    int regY = (int)ReadUInt32(data, bodyStart + 12);
                    int regCombop = data[bodyStart + 16] & 0x07;

                    // Concatenate all referred symbol dictionaries
                    Jbig2Symbol[]? allSyms = null;
                    if (referredSegments.Length > 0)
                    {
                        var list = new List<Jbig2Symbol>();
                        for (int i = 0; i < referredSegments.Length; i++)
                        {
                            if (symbolRegistry.TryGetValue(referredSegments[i], out var d))
                            {
                                list.AddRange(d);
                            }
                        }
                        if (list.Count > 0)
                        {
                            allSyms = list.ToArray();
                        }
                    }

                    if (allSyms is null)
                    {
                        // No symbol dictionaries resolved to any glyphs. Because a dictionary segment that
                        // fails to decode aborts the whole page earlier (see SymbolDictionary case), reaching
                        // here with an empty symbol set means the referenced dictionaries are legitimately
                        // empty — so every instance ID is out of range and the region renders blank. That is
                        // a safe, faithful render (MuPDF places nothing), not a mis-render.
                        allSyms = [];
                    }

                    if (!Jbig2SymbolDict.DecodeTextRegion(data, bodyStart, bodyEnd, allSyms,
                        regWidth, regHeight, CollectCodeTables(referredSegments, codeTables), out byte[]? regionBits) || regionBits is null)
                    {
                        return null;
                    }

                    if (segmentType == IntermediateTextRegion)
                    {
                        // Intermediate text region: retain the standalone bitmap as this segment's result so
                        // a later refinement can use it as a reference (not composited to the page here).
                        regionResults[segmentNumber] = (regionBits, regWidth, regHeight);
                    }
                    else
                    {
                        if (page is null || regCombop is not (0 or 1 or 2 or 3 or 4))
                        {
                            return null;
                        }
                        EnsurePageRows(regY + regHeight);
                        if (!Jbig2SymbolDict.CompositeRegionOntoPage(page, pageWidth, rowBytes, regionBits, regWidth, regHeight, regX, regY, regCombop))
                        {
                            return null;
                        }
                    }
                    break;
                }

                case ImmediateGenericRegion:
                case ImmediateLosslessGenericRegion:
                {
                    if (bodyEnd - bodyStart >= RegionInformationFieldLength + 1)
                    {
                        int regY = (int)ReadUInt32(data, bodyStart + 12);
                        int regH = segmentRows >= 0 ? segmentRows : (int)ReadUInt32(data, bodyStart + 4);
                        EnsurePageRows(regY + regH);
                    }

                    if (!TryDecodeGenericRegion(data, bodyStart, bodyEnd, page, pageWidth, rowBytes, segmentRows))
                    {
                        return null;
                    }

                    break;
                }

                case CodedGenericRegion:
                case IntermediateGenericRegion:
                {
                    // §7.4.3 / §7.4.4: same codeword as an immediate generic region, but the decoded bitmap
                    // is *retained* as this segment's result and referenced by SBREFNUM from a later
                    // refinement (types 40/42/43) instead of being composited onto the page here.
                    if (!DecodeGenericRegionCodeword(data, bodyStart, bodyEnd, segmentRows,
                        out byte[] regionBits, out int regW, out int regH, out _, out _, out int regCombop, out bool bitIsBlack))
                    {
                        return null;
                    }
                    if (regW <= 0 || regH <= 0)
                    {
                        return null;
                    }
                    // Reserved combination operator (5..7): the standalone bitmap is still safe to retain,
                    // but a downstream composite would need to reject it; leave that to the caller.

                    // Normalise the packed bits so 1 = black, matching what Jbig2Refinement.GetRefPixel
                    // reads from a reference bitmap. Arithmetic already emits 1 = black; MMR emits 0 =
                    // black (see CompositeRegion's convention), so invert for MMR.
                    byte[] packed = regionBits;
                    if (!bitIsBlack)
                    {
                        packed = new byte[regionBits.Length];
                        for (int i = 0; i < regionBits.Length; i++)
                        {
                            packed[i] = (byte)~regionBits[i];
                        }
                    }
                    _ = regCombop; // not composited here; retained region ignores this
                    regionResults[segmentNumber] = (packed, regW, regH);
                    break;
                }

                case PatternDictionary:
                {
                    Jbig2PatternDict? pdict = Jbig2Halftone.DecodeDictionary(data, bodyStart, bodyEnd);
                    if (pdict is null)
                    {
                        return null;
                    }
                    patternRegistry[segmentNumber] = pdict;
                    break;
                }

                case ImmediateHalftoneRegion:
                case ImmediateLosslessHalftoneRegion:
                {
                    if (page is null || referredSegments.Length == 0)
                    {
                        return null;
                    }
                    if (!patternRegistry.TryGetValue(referredSegments[0], out var hpats))
                    {
                        return null;
                    }
                    if (!Jbig2Halftone.DecodeRegion(data, bodyStart, bodyEnd, hpats, page, pageWidth, pageHeight, rowBytes))
                    {
                        return null;
                    }
                    break;
                }

                case IntermediateGenericRefinement:
                case ImmediateGenericRefinement:
                case ImmediateLosslessGenericRefinement:
                {
                    if (page is null || bodyEnd - bodyStart < 18)
                    {
                        return null;
                    }

                    int regW = (int)ReadUInt32(data, bodyStart);
                    int regH = (int)ReadUInt32(data, bodyStart + 4);
                    int regX = (int)ReadUInt32(data, bodyStart + 8);
                    int regY = (int)ReadUInt32(data, bodyStart + 12);
                    int regOp = data[bodyStart + 16] & 0x07;
                    byte segFlags = data[bodyStart + 17];
                    int grTemplate = segFlags & 1;
                    bool tpgron = (segFlags & 2) != 0;
                    int offset2 = 18;

                    // GRTEMPLATE 1 + TPGRON 1: PDFium (which is the sole oracle for the pdf.js corpus
                    // files that MuPDF fallbacks on) uses a different arithmetic-context bit layout from
                    // MuPDF/spec here (see core/fxcodec/jbig2/jbig2_grrd_proc.cpp DecodeTemplate1Unopt).
                    // Adopting PDFium's layout is a distinct work item; until then this specific
                    // combination degrades to null so the image is skipped rather than mis-rendered.
                    if (grTemplate == 1 && tpgron)
                    {
                        return null;
                    }

                    sbyte[] grat = new sbyte[4]; // default zeros
                    if (grTemplate == 0)
                    {
                        if (bodyStart + offset2 + 4 > bodyEnd) { return null; }
                        for (int i = 0; i < 4; i++) grat[i] = (sbyte)data[bodyStart + offset2 + i];
                        offset2 += 4;
                    }

                    // Find reference bitmap: the first referred segment with a retained region result
                    // (jbig2_region_find_referred), else fall back to the page buffer.
                    byte[]? refPacked = null; int refW = 0, refH = 0;
                    for (int i = 0; i < referredSegments.Length; i++)
                    {
                        if (regionResults.TryGetValue(referredSegments[i], out var rr))
                        {
                            refPacked = rr.Bits; refW = rr.Width; refH = rr.Height;
                            break;
                        }
                    }
                    if (refPacked is null)
                    {
                        // Use page buffer as reference
                        refPacked = page; refW = pageWidth; refH = pageHeight;
                    }

                    int statsSize = grTemplate != 0 ? 1 << 10 : 1 << 13;
                    byte[] grStats = new byte[statsSize];
                    int csStart = bodyStart + offset2;
                    var mq = new Jbig2MqDecoder(data, csStart, bodyEnd);

                    byte[]? refBits = Jbig2Refinement.Decode(grTemplate, tpgron, grat, mq, grStats, regW, regH, refPacked, refW, refH, 0, 0);
                    if (refBits is null) { return null; }

                    if (segmentType == IntermediateGenericRefinement)
                    {
                        regionResults[segmentNumber] = (refBits, regW, regH);
                    }
                    else
                    {
                        // Composite onto page
                        CompositeRegion(page, pageWidth, rowBytes, refBits, regW, regH, regX, regY, regOp, true);
                    }
                    break;
                }

                case EndOfStripe:
                    // §7.2.5/§7.4.9: end-of-stripe carries a running end-row count. In striped organization
                    // each region segment carries its own absolute SBY, so this is only an advisory marker;
                    // advancing/ignoring it does not change where regions composite (verified against MuPDF).
                    break;

                case 52: // profile
                case 62: // extension segment: implementation-specific; MuPDF/jbig2dec ignore for rendering
                    break;

                case EndOfPage:
                    return pageStarted ? new Jbig2Page(pageWidth, pageHeight, page!) : null;

                case 51: // end of file
                    return pageStarted ? new Jbig2Page(pageWidth, pageHeight, page!) : null;

                default:
                    return null; // symbol dictionary / text / halftone / refinement / tables: unsupported
            }

            position = bodyEnd;
        }

        return pageStarted ? new Jbig2Page(pageWidth, pageHeight, page!) : null;
    }

    private static bool TryDecodeGenericRegion(byte[] data, int bodyStart, int bodyEnd, byte[]? page, int pageWidth, int rowBytes, int heightOverride)
    {
        if (page is null)
        {
            return false;
        }

        if (!DecodeGenericRegionCodeword(data, bodyStart, bodyEnd, heightOverride, out byte[] regionBits, out int regionWidth, out int regionHeight, out int regionX, out int regionY, out int combinationOperator, out bool bitIsBlack))
        {
            return false;
        }

        if (combinationOperator is not (0 or 1 or 2 or 3 or 4))
        {
            // Reserved combination operator (5..7): degrade rather than guess a composition.
            return false;
        }

        return CompositeRegion(page, pageWidth, rowBytes, regionBits, regionWidth, regionHeight, regionX, regionY, combinationOperator, bitIsBlack);
    }

    /// <summary>
    /// Decodes a generic-region codeword (MMR or arithmetic, templates 0-3, default/custom GBAT, TPGDON)
    /// into a standalone packed bitmap, reporting the region rectangle and combination operator parsed from
    /// the segment's 17-byte region header. Shared by immediate (38/39) and coded/intermediate (36/37)
    /// generic regions. Returns false when the codeword cannot be decoded.
    /// </summary>
    private static bool DecodeGenericRegionCodeword(byte[] data, int bodyStart, int bodyEnd, int heightOverride, out byte[] regionBits, out int regionWidth, out int regionHeight, out int regionX, out int regionY, out int combinationOperator, out bool bitIsBlack)
    {
        regionBits = []; regionWidth = 0; regionHeight = 0; regionX = 0; regionY = 0; combinationOperator = 0; bitIsBlack = false;

        if (bodyEnd - bodyStart < RegionInformationFieldLength + 1)
        {
            return false;
        }

        regionWidth = (int)ReadUInt32(data, bodyStart);
        regionHeight = heightOverride >= 0 ? heightOverride : (int)ReadUInt32(data, bodyStart + 4);
        regionX = (int)ReadUInt32(data, bodyStart + 8);
        regionY = (int)ReadUInt32(data, bodyStart + 12);
        combinationOperator = data[bodyStart + 16] & 0x07;
        byte regionFlags = data[bodyStart + RegionInformationFieldLength];

        if (regionWidth <= 0 || regionHeight <= 0)
        {
            return false;
        }

        bool mmr = (regionFlags & 0x01) != 0;

        if (mmr)
        {
            int codestreamStart = bodyStart + RegionInformationFieldLength + 1;
            byte[] codestream = new byte[bodyEnd - codestreamStart];
            Array.Copy(data, codestreamStart, codestream, 0, codestream.Length);

            // T.6 / Group 4 MMR. JBIG2 generic-region MMR codestreams do not carry the T.6 EOFB terminator,
            // so decode by row count (endOfBlock=false). The 1-bit output uses 1=black under BlackIs1; JBIG2
            // paints the inverse of that convention relative to MuPDF's rendering (verified against MuPDF).
            regionBits = CcittFaxDecoder.Decode(codestream, regionWidth, regionHeight, k: -1, encodedByteAlign: false, blackIs1: true, endOfBlock: false, endOfLine: false);
            bitIsBlack = false;
            return true;
        }

        int template = (regionFlags >> 1) & 0x03;
        bool tpDon = (regionFlags & 0x08) != 0;
        int gbatBytes = (regionFlags & 0x06) != 0 ? 2 : 8;
        int csStart = bodyStart + RegionInformationFieldLength + 1 + gbatBytes;
        if (csStart > bodyEnd)
        {
            return false;
        }

        sbyte[] gbat = new sbyte[8];
        for (int i = 0; i < gbatBytes; i++)
        {
            gbat[i] = (sbyte)data[bodyStart + RegionInformationFieldLength + 1 + i];
        }

        byte[]? decoded = Jbig2GenericArithmetic.Decode(template, tpDon, gbat, data, csStart, bodyEnd, regionWidth, regionHeight);
        if (decoded is null)
        {
            return false;
        }

        regionBits = decoded;
        bitIsBlack = true;
        return true;
    }

    private static bool CompositeRegion(byte[] page, int pageWidth, int rowBytes, byte[] regionBits, int regionWidth, int regionHeight, int regionX, int regionY, int combinationOperator, bool bitIsBlack)
    {
        int regionRowBytes = (regionWidth + 7) / 8;
        int pageHeight = rowBytes == 0 ? 0 : page.Length / rowBytes;

        for (int y = 0; y < regionHeight; y++)
        {
            int pageRow = regionY + y;
            if (pageRow < 0 || pageRow >= pageHeight)
            {
                continue;
            }

            for (int x = 0; x < regionWidth; x++)
            {
                int pageColumn = regionX + x;
                if (pageColumn < 0 || pageColumn >= pageWidth)
                {
                    continue;
                }

                int sourceBit = (regionBits[(y * regionRowBytes) + (x / 8)] >> (7 - (x % 8))) & 1;
                bool black = bitIsBlack ? sourceBit == 1 : sourceBit == 0;

                int pageIndex = (pageRow * rowBytes) + (pageColumn / 8);
                int mask = 1 << (7 - (pageColumn % 8));
                bool current = (page[pageIndex] & mask) != 0;

                bool result = combinationOperator switch
                {
                    0 => current || black,          // OR
                    1 => current && black,          // AND
                    2 => current ^ black,            // XOR
                    3 => current == black,           // XNOR
                    _ => black,                       // 4 = REPLACE
                };

                page[pageIndex] = result
                    ? (byte)(page[pageIndex] | mask)
                    : (byte)(page[pageIndex] & ~mask);
            }
        }

        return true;
    }

    private static bool TryReadSegmentHeader(byte[] data, ref int position, out int segmentNumber, out int segmentType, out int segmentLength, out int[] referredSegments, out int segmentRows)
    {
        segmentNumber = 0;
        segmentType = 0;
        segmentLength = 0;
        segmentRows = -1;
        referredSegments = [];

        uint number = ReadUInt32(data, position);
        segmentNumber = (int)number;
        byte flags = data[position + 4];
        segmentType = flags & 0x3F;
        bool pageAssociationFieldSize = (flags & 0x40) != 0;
        int cursor = position + 5;

        byte referredFlags = data[cursor++];
        int referredToCount = referredFlags >> 5 & 7;
        if (referredFlags == 7)
        {
            // Extended count: 3 bytes follow the first byte's retain bits.
            if (cursor + 3 > data.Length)
            {
                return false;
            }

            referredToCount = (int)(ReadUInt32(data, cursor - 1) & 0x1FFFFFFF);
            cursor += 2; // skip remaining 2 of the 3-byte count (already read 1 at cursor-1)
            int retainBytes = (referredToCount + 7) / 8;
            cursor += retainBytes;
        }
        else if (referredFlags is 5 or 6)
        {
            return false; // reserved
        }

        int referredNumberSize = number <= 256 ? 1 : number <= 65536 ? 2 : 4;
        if (referredToCount > 0 && referredToCount <= 1000)
        {
            referredSegments = new int[referredToCount];
            for (int i = 0; i < referredToCount; i++)
            {
                if (cursor + referredNumberSize > data.Length)
                {
                    return false;
                }
                referredSegments[i] = referredNumberSize switch
                {
                    1 => data[cursor],
                    2 => (data[cursor] << 8) | data[cursor + 1],
                    _ => (int)ReadUInt32(data, cursor),
                };
                cursor += referredNumberSize;
            }
        }
        else if (referredToCount > 1000)
        {
            return false;
        }

        cursor += pageAssociationFieldSize ? 4 : 1;

        if (cursor + 4 > data.Length)
        {
            return false;
        }

        uint length = ReadUInt32(data, cursor);
        cursor += 4;

        if (length == 0xFFFFFFFF)
        {
            // §7.2.2/D.3: generic-region kinds 36 (coded link-on), 37 (intermediate) and 38 (immediate)
            // may carry an unknown segment length. Locate the codestream terminator (MMR: 00 00,
            // arithmetic: FF AC) then a 4-byte row count that supplies the region height (mirrors
            // jbig2.c). Other unknown-length types cannot be bounded here.
            if (segmentType is not (CodedGenericRegion or IntermediateGenericRegion or ImmediateGenericRegion))
            {
                return false;
            }

            int bodyStart = cursor;
            if (bodyStart + 18 > data.Length)
            {
                return false;
            }

            bool mmr = (data[bodyStart + 17] & 1) != 0;
            byte m0 = mmr ? (byte)0x00 : (byte)0xFF;
            byte m1 = mmr ? (byte)0x00 : (byte)0xAC;
            int scan = bodyStart + 18;
            while (scan + 1 < data.Length && !(data[scan] == m0 && data[scan + 1] == m1))
            {
                scan++;
            }
            if (scan + 1 >= data.Length || scan + 6 > data.Length)
            {
                return false; // terminator or row count not present
            }

            segmentRows = (int)ReadUInt32(data, scan + 2);
            segmentLength = (scan + 2 + 4) - bodyStart;
            position = bodyStart;
            return true;
        }

        segmentLength = (int)length;
        position = cursor;
        return true;
    }

    private static uint ReadUInt32(byte[] data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
}
