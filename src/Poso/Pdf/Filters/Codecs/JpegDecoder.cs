using System;

namespace Poso.Pdf.Filters.Codecs;

/// <summary>
/// Baseline (sequential DCT) JPEG decoder. Decodes a JPEG into full-resolution per-component sample
/// planes and reports the Adobe APP14 <c>transform</c> code, so the caller can perform the colour
/// conversion itself (YCCK→CMYK→RGB). This exists because native codecs (Skia's <c>SKCodec</c>) mis-handle
/// Adobe CMYK/YCCK JPEGs (they omit the CMYK inversion), producing visibly wrong colours; SkiaSharp also
/// exposes no CMYK output so the raw samples cannot be recovered from it.
///
/// Scope: SOF0/SOF1 baseline sequential, single frame, 1-4 components, arbitrary (integer) sampling
/// factors, optional restart markers. Progressive (SOF2) and multi-scan files are rejected (null) so the
/// caller can fall back rather than render a partial image. Validated against MuPDF reference samples.
///
/// Entropy decoding uses a 10-bit lookahead table (peek 10 bits, one lookup resolves the symbol and its
/// length; DC magnitudes and short-AC magnitudes are pre-extended into the table) with the classic
/// mincode/maxcode walk as the slow path for long codes. The inverse DCT is the AA&amp;N (Arai-Agui-Nakajima)
/// floating-point algorithm — the butterfly structure and rotation constants follow libjpeg-turbo's
/// <c>jidctflt.c</c> (P&amp;M fig. 4-8), re-expressed for the dequantize-into-floats layout this decoder
/// uses; per the IJG license, this software is based in part on the work of the Independent JPEG Group.
/// </summary>
/// internal to the imaging pipeline but public for validation tests.
public static class JpegDecoder
{
    /// <summary>Decoded JPEG: full-resolution component planes (each <see cref="Width"/>×<see cref="Height"/> bytes) plus the Adobe transform code.</summary>
    public sealed class Result
    {
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int ComponentCount { get; init; }
        /// <summary>Adobe APP14 transform byte (0=none, 1=YCbCr, 2=YCCK), or -1 when no Adobe marker was present.</summary>
        public required int AdobeTransform { get; init; }
        /// <summary>One plane per component, upsampled to full resolution, row-major, length Width*Height.</summary>
        public required byte[][] Planes { get; init; }
    }

    private sealed class Component
    {
        public int Id;
        public int HSamp;
        public int VSamp;
        public int QTable;
        public int DcTable;
        public int AcTable;
        public int PlaneW;   // samples per row at this component's (sub-sampled) resolution
        public int PlaneH;
        public byte[] Plane = Array.Empty<byte>();      // IDCT output plane at PlaneW×PlaneH
        public float[] Multiplier = Array.Empty<float>(); // dequantize×aanscale×0.125, indexed by natural (row-major) position
    }

    private sealed class HuffmanTable
    {
        // Canonical decode tables (JPEG spec annex F / libjpeg jdhuff.c layout) — the slow path for
        // codes longer than the lookahead window.
        public readonly int[] MinCode = new int[18];
        public readonly int[] MaxCode = new int[18];
        public readonly int[] ValPtr = new int[18];
        public byte[] HuffVal = Array.Empty<byte>();
        public bool IsEmpty => HuffVal.Length == 0;

        // 10-bit lookahead: index by the next 10 stream bits (MSB-first, don't-care fill for shorter
        // codes). FastLength == 0 marks "no code of length <= 10 starts with this bit pattern".
        public const int FastBits = 10;
        public const int FastSize = 1 << FastBits;
        public readonly byte[] FastLength = new byte[FastSize];
        public readonly byte[] FastSymbol = new byte[FastSize];

        // DC-combined: one lookup yields the fully extended DC difference (code length + magnitude bits).
        // FastDcBits == 0 marks miss (a valid entry always consumes >= 1 bit).
        public readonly byte[] FastDcBits = new byte[FastSize];
        public readonly short[] FastDcValue = new short[FastSize];

        // AC-combined: one lookup yields run/size and (when the magnitude fits in the window) the
        // already-extended coefficient value. FastAcBits == 0 marks miss; FastAcSize == 0 means EOB (run
        // 0) or zero-run (run 15), which need no magnitude bits.
        public readonly byte[] FastAcBits = new byte[FastSize];
        public readonly byte[] FastAcRun = new byte[FastSize];
        public readonly byte[] FastAcSize = new byte[FastSize];
        public readonly short[] FastAcValue = new short[FastSize];

        public void Build(byte[] counts)
        {
            // counts[1..16] = number of codes of each length.
            int k = 0;
            int[] huffsize = new int[257];
            for (int len = 1; len <= 16; len++)
                for (int i = 0; i < counts[len]; i++)
                    huffsize[k++] = len;
            huffsize[k] = 0;

            int[] huffcode = new int[257];
            int code = 0;
            int codeSize = huffsize[0];
            k = 0;
            while (huffsize[k] != 0)
            {
                while (huffsize[k] == codeSize)
                {
                    huffcode[k] = code;
                    code++;
                    k++;
                }
                code <<= 1;
                codeSize++;
            }

            for (int len = 1; len <= 16; len++)
                ValPtr[len] = 0; // recomputed below

            int valPtr = 0;
            int idx = 0;
            for (int len = 1; len <= 16; len++)
            {
                if (idx >= k || huffsize[idx] != len)
                {
                    MinCode[len] = 0;
                    MaxCode[len] = -1; // no codes of this length -> decode loop fails
                }
                else
                {
                    MinCode[len] = huffcode[idx];
                    while (idx < k && huffsize[idx] == len)
                    {
                        idx++;
                    }
                    MaxCode[len] = huffcode[idx - 1];
                    ValPtr[len] = valPtr;
                    valPtr += counts[len];
                }
            }

            // Spread each code of length <= FastBits across its don't-care suffixes in the lookahead
            // window; combined tables additionally resolve the magnitude bits that follow the code.
            Array.Clear(FastLength);
            Array.Clear(FastDcBits);
            Array.Clear(FastAcBits);
            int n = huffsize.Length;
            for (int i = 0; i < n && huffsize[i] != 0; i++)
            {
                int len = huffsize[i];
                byte symbol = HuffVal[i];
                if (len > FastBits)
                {
                    continue; // slow path only
                }
                int shift = FastBits - len;
                int start = huffcode[i] << shift;
                int end = start + (1 << shift);
                for (int j = start; j < end; j++)
                {
                    FastLength[j] = (byte)len;
                    FastSymbol[j] = symbol;
                }

                // DC: value = symbol = magnitude bit count t; pre-extend when code+t fits the window.
                int t = symbol;
                if (t > 0 && len + t <= FastBits)
                {
                    int magShift = FastBits - len - t;
                    int magMask = (1 << t) - 1;
                    int threshold = 1 << (t - 1);
                    for (int j = start; j < end; j++)
                    {
                        int mag = (j >> magShift) & magMask;
                        FastDcBits[j] = (byte)(len + t);
                        FastDcValue[j] = (short)(mag < threshold ? mag - (1 << t) + 1 : mag);
                    }
                }

                // AC: symbol = run<<4 | size.
                int run = symbol >> 4;
                int size = symbol & 0x0F;
                if (size == 0)
                {
                    // EOB (run 0) or zero-run (run 15): no magnitude bits follow.
                    for (int j = start; j < end; j++)
                    {
                        FastAcBits[j] = (byte)len;
                        FastAcRun[j] = (byte)run;
                        FastAcSize[j] = 0;
                        FastAcValue[j] = 0;
                    }
                }
                else if (len + size <= FastBits)
                {
                    int magShift = FastBits - len - size;
                    int magMask = (1 << size) - 1;
                    int threshold = 1 << (size - 1);
                    for (int j = start; j < end; j++)
                    {
                        int mag = (j >> magShift) & magMask;
                        FastAcBits[j] = (byte)(len + size);
                        FastAcRun[j] = (byte)run;
                        FastAcSize[j] = (byte)size;
                        FastAcValue[j] = (short)(mag < threshold ? mag - (1 << size) + 1 : mag);
                    }
                }
            }
        }

        /// <summary>
        /// Slow path for codes longer than the lookahead window: walks the mincode/maxcode arrays over a
        /// 16-bit peek. Returns the symbol, or -1 when no code matches.
        /// </summary>
        public int DecodeSlow(BitReader reader)
        {
            int peeked = reader.PeekBits(16);
            int code = 0;
            for (int len = 1; len <= 16; len++)
            {
                code = (code << 1) | ((peeked >> (16 - len)) & 1);
                if (MaxCode[len] >= 0 && code >= MinCode[len] && code <= MaxCode[len])
                {
                    int valueIdx = ValPtr[len] + code - MinCode[len];
                    if (valueIdx >= HuffVal.Length)
                    {
                        return -1;
                    }
                    reader.ConsumeBits(len);
                    return HuffVal[valueIdx];
                }
            }
            return -1;
        }

        /// <summary>Decodes one DC difference (symbol + EXTEND). On failure sets <see cref="BitReader.HitError"/> and returns 0 — note that -1 is a legitimate difference value.</summary>
        public int DecodeDc(BitReader reader)
        {
            if (IsEmpty)
            {
                reader.HitError = true;
                return 0;
            }

            int peeked = reader.PeekBits(FastBits);
            int bits = FastDcBits[peeked];
            if (bits != 0)
            {
                reader.ConsumeBits(bits);
                return FastDcValue[peeked];
            }

            int len = FastLength[peeked];
            int t;
            if (len != 0)
            {
                reader.ConsumeBits(len);
                t = FastSymbol[peeked];
            }
            else
            {
                t = DecodeSlow(reader);
                if (t < 0)
                {
                    reader.HitError = true;
                    return 0;
                }
            }

            if (t == 0)
            {
                return 0;
            }
            return reader.ReceiveExtend(t);
        }
    }

    /// <summary>Decodes the given JPEG bytes, or returns null when the file is outside the supported scope.</summary>
    public static Result? Decode(byte[] data)
    {
        if (data is null || data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            return null;
        }

        int adobeTransform = -1;
        int width = 0, height = 0;
        Component[] components = Array.Empty<Component>();
        int maxHsamp = 1, maxVsamp = 1;
        int restartInterval = 0;

        var qTables = new short[4][];   // zig-zag ordered dequantisation tables
        var dcTables = new HuffmanTable[4];
        var acTables = new HuffmanTable[4];
        for (int i = 0; i < 4; i++)
        {
            dcTables[i] = new HuffmanTable();
            acTables[i] = new HuffmanTable();
        }

        int sosScanHeaderOffset = -1;

        int pos = 2;
        try
        {
            while (pos < data.Length - 1)
            {
                if (data[pos] != 0xFF)
                {
                    pos++;
                    continue;
                }

                byte marker = data[pos + 1];
                pos += 2;

                if (marker == 0xFF)
                {
                    pos--; // fill byte
                    continue;
                }

                switch (marker)
                {
                    case 0xD8: // SOI
                        continue;
                    case 0xD9: // EOI
                        break;
                    case 0x00:
                        continue;
                    case 0x01: // TEM
                        continue;
                    case 0xC4: // DHT
                        if (!ReadDht(data, ref pos, dcTables, acTables))
                        {
                            return null;
                        }
                        continue;
                    case 0xDB: // DQT
                        if (!ReadDqt(data, ref pos, qTables))
                        {
                            return null;
                        }
                        continue;
                    case 0xDD: // DRI
                        int driLen = (data[pos] << 8) | data[pos + 1];
                        restartInterval = (data[pos + 2] << 8) | data[pos + 3];
                        pos += driLen;
                        continue;
                    case 0xEE: // APP14 - may carry the Adobe transform
                        adobeTransform = ReadApp14(data, pos);
                        int segLen14 = (data[pos] << 8) | data[pos + 1];
                        pos += segLen14;
                        continue;
                    case 0xC0: // SOF0 baseline
                    case 0xC1: // SOF1 extended sequential
                        if (!ReadSof(data, ref pos, out width, out height, out components, qTables,
                                ref maxHsamp, ref maxVsamp))
                        {
                            return null;
                        }
                        continue;
                    case 0xC2: // SOF2 progressive - not supported
                    case 0xC3: // SOF3 lossless
                    case 0xC5:
                    case 0xC6:
                    case 0xC7:
                    case 0xC9:
                    case 0xCA:
                    case 0xCB:
                    case 0xCD:
                    case 0xCE:
                    case 0xCF:
                        return null;
                    case 0xDA: // SOS
                        sosScanHeaderOffset = pos;
                        break;
                    default:
                        // Skip any other variable-length marker segment (APPn, COM, ...).
                        if (marker >= 0xE0 && marker != 0xEE)
                        {
                            int segLen = (data[pos] << 8) | data[pos + 1];
                            pos += segLen;
                        }
                        continue;
                }

                if (marker == 0xDA)
                {
                    break;
                }
            }
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }

        if (sosScanHeaderOffset < 0 || width <= 0 || height <= 0 || components.Length == 0)
        {
            return null;
        }

        // Parse the scan header (single full-frame baseline scan expected).
        if (!ReadSos(data, sosScanHeaderOffset, components, out int ss, out int se, out int ah, out int al))
        {
            return null;
        }

        // Ss, Se, Ah and Al describe a progressive scan's spectral band and successive approximation, and
        // this decoder only reads a whole block at a time from a sequential scan. A sequential scan is
        // supposed to declare 0..63, but encoders write 0 in Se too - pdf.js issue12841_reduced.pdf does,
        // and every lenient decoder reads its block anyway. Only a non-zero Ss or a non-zero Ah/Al says the
        // data really needs the progressive path, which is already refused at the SOF marker.
        if (ss != 0 || ah != 0 || al != 0)
        {
            return null;
        }

        // Validate Huffman tables are present for every component used.
        foreach (Component c in components)
        {
            if (qTables[c.QTable] is null)
            {
                return null;
            }
        }

        return DecodeScan(data, sosScanHeaderOffset, width, height, components, maxHsamp, maxVsamp,
            qTables, dcTables, acTables, restartInterval, adobeTransform);
    }

    /// <summary>
    /// Scans the marker segments up to the start-of-frame and returns the component count (without
    /// decoding entropy data). Returns -1 when no SOF is found. Used to cheaply decide whether a JPEG
    /// is CMYK (4-component) and should use the self-decoded path rather than the native codec.
    /// </summary>
    public static int PeekComponentCount(byte[] data)
    {
        if (data is null || data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            return -1;
        }
        int pos = 2;
        try
        {
            while (pos < data.Length - 1)
            {
                if (data[pos] != 0xFF)
                {
                    pos++;
                    continue;
                }
                byte marker = data[pos + 1];
                pos += 2;
                if (marker == 0xFF)
                {
                    pos--;
                    continue;
                }
                if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
                {
                    // SOF layout from here: length(2) precision(1) height(2) width(2) ncomp(1).
                    return data[pos + 7];
                }
                if (marker == 0xDA) // SOS: entropy data follows, no SOF found
                {
                    return -1;
                }
                if (marker is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7)
                {
                    continue;
                }
                int segLen = (data[pos] << 8) | data[pos + 1];
                pos += segLen;
            }
        }
        catch (IndexOutOfRangeException)
        {
            return -1;
        }
        return -1;
    }

    /// <summary>
    /// Scans the marker segments up to the start-of-scan and returns the Adobe APP14 transform code
    /// (0=none, 1=YCbCr, 2=YCCK), or -1 when no Adobe marker is present. Used by back-ends that
    /// decode 4-component JPEGs themselves to tell YCCK apart from plain CMYK.
    /// </summary>
    internal static int PeekAdobeTransform(byte[] data)
    {
        if (data is null || data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            return -1;
        }
        int pos = 2;
        try
        {
            while (pos < data.Length - 1)
            {
                if (data[pos] != 0xFF)
                {
                    pos++;
                    continue;
                }
                byte marker = data[pos + 1];
                pos += 2;
                if (marker == 0xFF)
                {
                    pos--;
                    continue;
                }
                if (marker == 0xEE)
                {
                    return ReadApp14(data, pos);
                }
                if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC) || marker == 0xDA)
                {
                    return -1; // headers are over and no Adobe marker was seen
                }
                if (marker is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7)
                {
                    continue;
                }
                int segLen = (data[pos] << 8) | data[pos + 1];
                pos += segLen;
            }
        }
        catch (IndexOutOfRangeException)
        {
            return -1;
        }
        return -1;
    }

    private static int ReadApp14(byte[] data, int pos)
    {
        // pos points at the 2-byte segment length. Layout: len(2) "Adobe\0" ver(2) flags(2)(2) transform(1).
        int segLen = (data[pos] << 8) | data[pos + 1];
        int body = pos + 2;
        if (body + 12 > data.Length)
        {
            return -1;
        }
        if (data[body] == 0x41 && data[body + 1] == 0x64 && data[body + 2] == 0x6F &&
            data[body + 3] == 0x62 && data[body + 4] == 0x65) // "Adobe"
        {
            return data[body + 11];
        }
        _ = segLen;
        return -1;
    }

    private static bool ReadDqt(byte[] data, ref int pos, short[][] qTables)
    {
        int length = (data[pos] << 8) | data[pos + 1];
        pos += 2;
        int end = pos + length - 2;
        while (pos < end)
        {
            int pqTq = data[pos++];
            int precision = pqTq >> 4;   // 0 = 8-bit, 1 = 16-bit
            int tableId = pqTq & 0x0F;
            if (tableId > 3)
            {
                return false;
            }
            var table = new short[64];
            for (int i = 0; i < 64; i++)
            {
                if (precision == 0)
                {
                    table[ZigZag[i]] = (short)data[pos++];
                }
                else
                {
                    table[ZigZag[i]] = (short)((data[pos] << 8) | data[pos + 1]);
                    pos += 2;
                }
            }
            qTables[tableId] = table;
        }
        pos = end;
        return true;
    }

    private static bool ReadDht(byte[] data, ref int pos, HuffmanTable[] dcTables, HuffmanTable[] acTables)
    {
        int length = (data[pos] << 8) | data[pos + 1];
        pos += 2;
        int end = pos + length - 2;
        while (pos < end)
        {
            int tcTh = data[pos++];
            int tc = tcTh >> 4;   // 0 = DC, 1 = AC
            int th = tcTh & 0x0F;
            if (th > 3)
            {
                return false;
            }
            var counts = new byte[17];
            int total = 0;
            for (int i = 1; i <= 16; i++)
            {
                counts[i] = data[pos++];
                total += counts[i];
            }
            var huffVal = new byte[total];
            Array.Copy(data, pos, huffVal, 0, total);
            pos += total;

            HuffmanTable table = tc == 0 ? dcTables[th] : acTables[th];
            table.HuffVal = huffVal;
            table.Build(counts);
        }
        pos = end;
        return true;
    }

    private static bool ReadSof(byte[] data, ref int pos, out int width, out int height,
        out Component[] components, short[][] qTables, ref int maxHsamp, ref int maxVsamp)
    {
        width = height = 0;
        components = Array.Empty<Component>();
        int length = (data[pos] << 8) | data[pos + 1];
        pos += 2;
        int precision = data[pos++];
        height = (data[pos] << 8) | data[pos + 1];
        pos += 2;
        width = (data[pos] << 8) | data[pos + 1];
        pos += 2;
        int nComp = data[pos++];
        if (nComp < 1 || nComp > 4 || precision != 8)
        {
            return false;
        }

        components = new Component[nComp];
        for (int i = 0; i < nComp; i++)
        {
            var c = new Component
            {
                Id = data[pos],
                HSamp = data[pos + 1] >> 4,
                VSamp = data[pos + 1] & 0x0F,
                QTable = data[pos + 2],
            };
            if (c.HSamp < 1 || c.HSamp > 4 || c.VSamp < 1 || c.VSamp > 4)
            {
                return false;
            }
            maxHsamp = Math.Max(maxHsamp, c.HSamp);
            maxVsamp = Math.Max(maxVsamp, c.VSamp);
            components[i] = c;
            pos += 3;
        }

        // Component plane dimensions (rounded up to whole MCU blocks of that component).
        int blocksPerLine = (width + 8 * maxHsamp - 1) / (8 * maxHsamp);
        int blocksPerColumn = (height + 8 * maxVsamp - 1) / (8 * maxVsamp);
        foreach (Component c in components)
        {
            c.PlaneW = blocksPerLine * 8 * c.HSamp;
            c.PlaneH = blocksPerColumn * 8 * c.VSamp;
            c.Plane = new byte[c.PlaneW * c.PlaneH];
        }
        _ = length;
        return true;
    }

    private static bool ReadSos(byte[] data, int pos, Component[] components, out int ss, out int se, out int ah, out int al)
    {
        ss = se = ah = al = 0;
        int length = (data[pos] << 8) | data[pos + 1];
        pos += 2;
        int n = data[pos++];
        if (n != components.Length)
        {
            return false;
        }
        for (int i = 0; i < n; i++)
        {
            int cid = data[pos];
            int tdta = data[pos + 1];
            pos += 2;
            bool found = false;
            foreach (Component c in components)
            {
                if (c.Id == cid)
                {
                    c.DcTable = tdta >> 4;
                    c.AcTable = tdta & 0x0F;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                return false;
            }
        }
        ss = data[pos++];
        se = data[pos++];
        int aha = data[pos];
        ah = aha >> 4;
        al = aha & 0x0F;
        _ = length;
        return true;
    }

    private static Result? DecodeScan(byte[] data, int sosOffset, int width, int height,
        Component[] components, int maxHsamp, int maxVsamp, short[][] qTables,
        HuffmanTable[] dcTables, HuffmanTable[] acTables, int restartInterval, int adobeTransform)
    {
        // SOS segment length tells where entropy-coded data begins.
        int sosLen = (data[sosOffset] << 8) | data[sosOffset + 1];
        int bitPos = sosOffset + sosLen; // first byte of the entropy stream

        int mcusPerLine = (width + 8 * maxHsamp - 1) / (8 * maxHsamp);
        int mcusPerColumn = (height + 8 * maxVsamp - 1) / (8 * maxVsamp);

        // Per-component IDCT multiplier table: quantization value × AA&N scale factor (row × column)
        // × 1/8 descale, so DecodeBlock writes pre-scaled float coefficients straight into the block.
        foreach (Component c in components)
        {
            short[] qTable = qTables[c.QTable];
            var multiplier = new float[64];
            for (int i = 0; i < 64; i++)
            {
                multiplier[i] = qTable[i] * AanScale[i >> 3] * AanScale[i & 7] * 0.125f;
            }
            c.Multiplier = multiplier;
        }

        var reader = new BitReader(data, bitPos);
        var block = new float[64];
        var dcPred = new int[components.Length];
        int mcusInRestart = restartInterval > 0 ? restartInterval : int.MaxValue;
        int mcuCountDown = mcusInRestart;

        for (int mcuRow = 0; mcuRow < mcusPerColumn; mcuRow++)
        {
            for (int mcuCol = 0; mcuCol < mcusPerLine; mcuCol++)
            {
                if (mcuCountDown == 0)
                {
                    reader.ResetToNextRestartMarker();
                    Array.Clear(dcPred);
                    mcuCountDown = mcusInRestart;
                }

                for (int ci = 0; ci < components.Length; ci++)
                {
                    Component c = components[ci];
                    for (int by = 0; by < c.VSamp; by++)
                    {
                        for (int bx = 0; bx < c.HSamp; bx++)
                        {
                            int blockX = mcuCol * c.HSamp + bx;   // in 8x8 blocks of this component
                            int blockY = mcuRow * c.VSamp + by;
                            DecodeBlock(reader, c, dcTables[c.DcTable], acTables[c.AcTable],
                                dcPred, ci, block, c.Plane, blockX, blockY);
                        }
                    }
                }

                mcuCountDown--;
            }
        }

        if (reader.HitError)
        {
            return null;
        }

        var planes = new byte[components.Length][];
        for (int ci = 0; ci < components.Length; ci++)
        {
            planes[ci] = Upsample(components[ci], width, height, maxHsamp, maxVsamp);
        }

        return new Result
        {
            Width = width,
            Height = height,
            ComponentCount = components.Length,
            AdobeTransform = adobeTransform,
            Planes = planes,
        };
    }

    private static void DecodeBlock(BitReader reader, Component c, HuffmanTable dcTable,
        HuffmanTable acTable, int[] dcPred, int compIndex, float[] block, byte[] plane, int blockX, int blockY)
    {
        Array.Clear(block);
        float[] multiplier = c.Multiplier;

        // DC coefficient (combined lookup resolves symbol + magnitude in one step when it fits).
        int diff = dcTable.DecodeDc(reader);
        if (reader.HitError)
        {
            return;
        }
        dcPred[compIndex] += diff;
        float dc = dcPred[compIndex] * multiplier[0];
        block[0] = dc;

        // AC coefficients in zig-zag order.
        int k = 1;
        while (k < 64)
        {
            int peeked = reader.PeekBits(HuffmanTable.FastBits);
            int bits = acTable.FastAcBits[peeked];
            if (bits != 0)
            {
                // Combined entry: run/size (+ magnitude) resolved by the table.
                reader.ConsumeBits(bits);
                int run = acTable.FastAcRun[peeked];
                int size = acTable.FastAcSize[peeked];
                if (size == 0)
                {
                    if (run != 15)
                    {
                        break; // EOB
                    }
                    k += 16;
                    continue;
                }
                k += run;
                if (k > 63)
                {
                    reader.HitError = true;
                    return;
                }
                block[ZigZag[k]] = acTable.FastAcValue[peeked] * multiplier[ZigZag[k]];
                k++;
                continue;
            }

            // Symbol-only fast path, then the mincode/maxcode walk for long codes.
            int len = acTable.FastLength[peeked];
            int rs;
            if (len != 0)
            {
                reader.ConsumeBits(len);
                rs = acTable.FastSymbol[peeked];
            }
            else
            {
                rs = acTable.DecodeSlow(reader);
                if (rs < 0)
                {
                    reader.HitError = true;
                    return;
                }
            }

            int r = rs >> 4;
            int s = rs & 0x0F;
            if (s == 0)
            {
                if (r != 15)
                {
                    break; // EOB
                }
                k += 16;
                continue;
            }
            k += r;
            if (k > 63)
            {
                reader.HitError = true;
                return;
            }
            int natural = ZigZag[k];
            block[natural] = reader.ReceiveExtend(s) * multiplier[natural];
            k++;
        }

        // Inverse DCT into the component plane. An EOB right after the DC coefficient means every AC
        // term is zero: the block is flat and the 2-D IDCT of a DC-only block is constant
        // (DC value + 128, rounded) — fill it directly instead of transforming.
        if (k == 1)
        {
            int value = (int)(dc + 128.5f);
            byte flat = (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
            int planeOffset = blockY * 8 * c.PlaneW + blockX * 8;
            for (int row = 0; row < 8; row++)
            {
                int rowOffset = planeOffset + row * c.PlaneW;
                plane[rowOffset] = flat;
                plane[rowOffset + 1] = flat;
                plane[rowOffset + 2] = flat;
                plane[rowOffset + 3] = flat;
                plane[rowOffset + 4] = flat;
                plane[rowOffset + 5] = flat;
                plane[rowOffset + 6] = flat;
                plane[rowOffset + 7] = flat;
            }
            return;
        }

        Idct(block, plane, blockY * 8 * c.PlaneW + blockX * 8, c.PlaneW);
    }

    // ---- AA&N floating-point inverse DCT ----
    //
    // Butterfly structure and rotation constants follow libjpeg-turbo's jidctflt.c (AA&N scaled DCT,
    // Pennebaker & Mitchell fig. 4-8): ~5 multiplies per 1-D pass instead of the 64 of a direct matrix
    // product. The AA&N scale factors and the 1/8 descale are folded into the per-component multiplier
    // table (see DecodeScan), so the transform itself works on pre-scaled float coefficients. This
    // software is based in part on the work of the Independent JPEG Group.

    private static readonly float[] AanScale =
    {
        1.0f, 1.387039845f, 1.306562965f, 1.175875602f,
        1.0f, 0.785694958f, 0.541196100f, 0.275899379f,
    };

    private const float Sqrt2 = 1.414213562f;        // 2*c4
    private const float TwoC2 = 1.847759065f;        // 2*c2
    private const float TwoC2MinusC6 = 1.082392200f; // 2*(c2-c6)
    private const float TwoC2PlusC6 = 2.613125930f;  // 2*(c2+c6)

    // Separable 1-D AA&N IDCT (column pass into workspace, row pass out), level-shifted (+128, rounded)
    // and clamped to 0..255.
    private static void Idct(float[] coef, byte[] dest, int offset, int stride)
    {
        Span<float> wsp = stackalloc float[64];

        // Pass 1: transform each column of coefficients into the workspace.
        for (int x = 0; x < 8; x++)
        {
            float p0 = coef[x], p1 = coef[8 + x], p2 = coef[16 + x], p3 = coef[24 + x];
            float p4 = coef[32 + x], p5 = coef[40 + x], p6 = coef[48 + x], p7 = coef[56 + x];

            float t0, t1, t2, t3, t4, t5, t6, t7;
            if (p1 == 0 && p2 == 0 && p3 == 0 && p4 == 0 && p5 == 0 && p6 == 0 && p7 == 0)
            {
                // AC terms all zero: every output of this 1-D pass equals the first coefficient.
                wsp[x] = p0;
                wsp[8 + x] = p0;
                wsp[16 + x] = p0;
                wsp[24 + x] = p0;
                wsp[32 + x] = p0;
                wsp[40 + x] = p0;
                wsp[48 + x] = p0;
                wsp[56 + x] = p0;
                continue;
            }

            // Even part (phases 3 and 5-3, 2).
            float t10 = p0 + p4;
            float t11 = p0 - p4;
            float t13 = p2 + p6;
            float t12 = (p2 - p6) * Sqrt2 - t13;
            t0 = t10 + t13;
            t3 = t10 - t13;
            t1 = t11 + t12;
            t2 = t11 - t12;

            // Odd part (phases 5-6 and 2): pairs rows (1,7) and (3,5).
            float z13 = p5 + p3;
            float z10 = p5 - p3;
            float z11 = p1 + p7;
            float z12 = p1 - p7;
            t7 = z11 + z13;
            float t11b = (z11 - z13) * Sqrt2;
            float z5 = (z10 + z12) * TwoC2;
            float t10b = z5 - z12 * TwoC2MinusC6;
            float t12b = z5 - z10 * TwoC2PlusC6;
            t6 = t12b - t7;
            t5 = t11b - t6;
            t4 = t10b - t5;

            wsp[x] = t0 + t7;
            wsp[56 + x] = t0 - t7;
            wsp[8 + x] = t1 + t6;
            wsp[48 + x] = t1 - t6;
            wsp[16 + x] = t2 + t5;
            wsp[40 + x] = t2 - t5;
            wsp[24 + x] = t3 + t4;
            wsp[32 + x] = t3 - t4;
        }

        // Pass 2: transform each row of the workspace into the destination plane. The level shift and
        // the float->int rounding bias (+0.5) ride on the DC term, like jidctflt's output stage.
        for (int y = 0; y < 8; y++)
        {
            int wOffset = y * 8;
            float p0 = wsp[wOffset] + 128.5f;
            float p1 = wsp[wOffset + 1], p2 = wsp[wOffset + 2], p3 = wsp[wOffset + 3];
            float p4 = wsp[wOffset + 4], p5 = wsp[wOffset + 5], p6 = wsp[wOffset + 6], p7 = wsp[wOffset + 7];

            float t10 = p0 + p4;
            float t11 = p0 - p4;
            float t13 = p2 + p6;
            float t12 = (p2 - p6) * Sqrt2 - t13;
            float t0 = t10 + t13;
            float t3 = t10 - t13;
            float t1 = t11 + t12;
            float t2 = t11 - t12;

            float z13 = p5 + p3;
            float z10 = p5 - p3;
            float z11 = p1 + p7;
            float z12 = p1 - p7;
            float t7 = z11 + z13;
            float t11b = (z11 - z13) * Sqrt2;
            float z5 = (z10 + z12) * TwoC2;
            float t10b = z5 - z12 * TwoC2MinusC6;
            float t12b = z5 - z10 * TwoC2PlusC6;
            float t6 = t12b - t7;
            float t5 = t11b - t6;
            float t4 = t10b - t5;

            int outOffset = offset + y * stride;
            Store(dest, outOffset, t0 + t7);
            Store(dest, outOffset + 7, t0 - t7);
            Store(dest, outOffset + 1, t1 + t6);
            Store(dest, outOffset + 6, t1 - t6);
            Store(dest, outOffset + 2, t2 + t5);
            Store(dest, outOffset + 5, t2 - t5);
            Store(dest, outOffset + 3, t3 + t4);
            Store(dest, outOffset + 4, t3 - t4);
        }
    }

    private static void Store(byte[] dest, int index, float value)
    {
        int iv = (int)value;
        dest[index] = (byte)(iv < 0 ? 0 : iv > 255 ? 255 : iv);
    }

    // Upsample a component plane to full resolution. A component at the maximum sampling factor maps
    // 1:1 (row copies); a sub-sampled component is box-upsampled (each source sample covers
    // maxSamp/samp output cells), with the source-column mapping precomputed once.
    private static byte[] Upsample(Component c, int width, int height, int maxHsamp, int maxVsamp)
    {
        var output = new byte[width * height];
        byte[] src = c.Plane;

        if (c.HSamp == maxHsamp && c.VSamp == maxVsamp)
        {
            for (int y = 0; y < height; y++)
            {
                src.AsSpan(y * c.PlaneW, width).CopyTo(output.AsSpan(y * width, width));
            }
            return output;
        }

        var columnMap = new int[width];
        for (int x = 0; x < width; x++)
        {
            int sx = (x * c.HSamp) / maxHsamp;
            columnMap[x] = sx >= c.PlaneW ? c.PlaneW - 1 : sx;
        }

        for (int y = 0; y < height; y++)
        {
            int sy = (y * c.VSamp) / maxVsamp;
            if (sy >= c.PlaneH)
            {
                sy = c.PlaneH - 1;
            }
            int srow = sy * c.PlaneW;
            int drow = y * width;
            for (int x = 0; x < width; x++)
            {
                output[drow + x] = src[srow + columnMap[x]];
            }
        }
        return output;
    }

    // ---- entropy bit reader (handles byte stuffing and restart markers) ----

    private sealed class BitReader
    {
        private readonly byte[] _data;
        private int _pos;
        private uint _bitBuf;   // valid bits are the low _bitsLeft bits (right-aligned), read MSB-first
        private int _bitsLeft;
        private int _phantomBits;   // buffered bits that were never really in the stream (past its end)
        private bool _noMoreData;
        public bool HitError;

        public BitReader(byte[] data, int pos)
        {
            _data = data;
            _pos = pos;
        }

        // Pulls the next data byte, transparently handling 0xFF byte-stuffing and fill bytes. A real
        // marker (or the end of the data) yields -1 without consuming it: the lookahead may peek past
        // the last entropy byte, so running out is only an error once those bits are actually consumed.
        private int NextByte()
        {
            if (_noMoreData)
            {
                return -1;
            }
            while (_pos < _data.Length)
            {
                byte b = _data[_pos++];
                if (b != 0xFF)
                {
                    return b;
                }
                if (_pos >= _data.Length)
                {
                    _pos--;
                    break;
                }
                byte next = _data[_pos];
                if (next == 0x00)
                {
                    _pos++;         // stuffed 0xFF00 -> data byte 0xFF
                    return 0xFF;
                }
                if (next == 0xFF)
                {
                    continue;       // fill byte, skip
                }
                _pos--;             // rewind onto the 0xFF so restart-marker scanning still sees it
                break;
            }
            _noMoreData = true;
            return -1;
        }

        private void EnsureBits(int n)
        {
            while (_bitsLeft < n)
            {
                int b = NextByte();
                if (b < 0)
                {
                    _phantomBits += 8;   // pad past the end of the entropy stream
                    b = 0;
                }
                _bitBuf = (_bitBuf << 8) | (uint)b;
                _bitsLeft += 8;
            }
        }

        // Returns the next n bits without consuming them (n <= 16), MSB-first.
        public int PeekBits(int n)
        {
            EnsureBits(n);
            return (int)((_bitBuf >> (_bitsLeft - n)) & ((1u << n) - 1));
        }

        public void ConsumeBits(int n)
        {
            _bitsLeft -= n;
            if (_bitsLeft < _phantomBits)
            {
                // The consumed window reaches into bits that were never in the stream.
                HitError = true;
            }
        }

        // Reads n bits (consuming them), MSB-first.
        public int GetBits(int n)
        {
            if (n == 0)
            {
                return 0;
            }
            int v = PeekBits(n);
            ConsumeBits(n);
            return v;
        }

        // Reads an s-bit two's-complement value (JPEG EXTEND).
        public int ReceiveExtend(int s)
        {
            if (s == 0)
            {
                return 0;
            }
            int v = GetBits(s);
            return v < (1 << (s - 1)) ? v - (1 << s) + 1 : v;
        }

        // Aligns to the next restart marker (RSTn) and steps past it.
        public void ResetToNextRestartMarker()
        {
            _bitsLeft = 0;
            _bitBuf = 0;
            _phantomBits = 0;
            _noMoreData = false;
            while (_pos + 1 < _data.Length)
            {
                if (_data[_pos] == 0xFF && _data[_pos + 1] >= 0xD0 && _data[_pos + 1] <= 0xD7)
                {
                    _pos += 2;
                    return;
                }
                _pos++;
            }
            HitError = true;
        }
    }

    private static readonly int[] ZigZag =
    {
        0, 1, 8, 16, 9, 2, 3, 10,
        17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
    };
}
