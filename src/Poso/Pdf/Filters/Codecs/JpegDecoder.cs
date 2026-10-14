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
        public short[] Blocks = Array.Empty<short>();   // dequantised+IDCT plane at PlaneW×PlaneH
    }

    private sealed class HuffmanTable
    {
        // Canonical decode tables (JPEG spec annex F / libjpeg jdhuff.c layout).
        public readonly int[] MinCode = new int[18];
        public readonly int[] MaxCode = new int[18];
        public readonly int[] ValPtr = new int[18];
        public byte[] HuffVal = Array.Empty<byte>();
        public bool IsEmpty => HuffVal.Length == 0;

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

        if (ss != 0 || se != 63 || ah != 0 || al != 0)
        {
            return null; // spectral selection / successive approximation / multi-scan not supported
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
            c.Blocks = new short[c.PlaneW * c.PlaneH];
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

        var reader = new BitReader(data, bitPos);
        var block = new short[64];
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
                            DecodeBlock(reader, c, qTables[c.QTable], dcTables[c.DcTable], acTables[c.AcTable],
                                dcPred, ci, block, c.Blocks, blockX, blockY);
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

    private static void DecodeBlock(BitReader reader, Component c, short[] qTable, HuffmanTable dcTable,
        HuffmanTable acTable, int[] dcPred, int compIndex, short[] block, short[] plane, int blockX, int blockY)
    {
        Array.Clear(block);

        // DC coefficient.
        int t = reader.DecodeHuffman(dcTable);
        if (t < 0)
        {
            reader.HitError = true;
            return;
        }
        int diff = t == 0 ? 0 : reader.ReceiveExtend(t);
        dcPred[compIndex] += diff;
        block[0] = (short)(dcPred[compIndex] * qTable[0]);

        // AC coefficients in zig-zag order.
        int k = 1;
        while (k < 64)
        {
            int rs = reader.DecodeHuffman(acTable);
            if (rs < 0)
            {
                reader.HitError = true;
                return;
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
            block[ZigZag[k]] = (short)(reader.ReceiveExtend(s) * qTable[k]);
            k++;
        }

        // Inverse DCT into the component plane.
        Idct(block, plane, blockY * 8 * c.PlaneW + blockX * 8, c.PlaneW);
    }

    // Separable float inverse DCT (row pass then column pass), level-shifted and clamped to 0..255.
    private static void Idct(short[] coef, short[] dest, int offset, int stride)
    {
        Span<float> tmp = stackalloc float[64];
        // Column pass: transform each column (input rows are coefficient rows).
        for (int x = 0; x < 8; x++)
        {
            float s0 = coef[x], s1 = coef[8 + x], s2 = coef[16 + x], s3 = coef[24 + x];
            float s4 = coef[32 + x], s5 = coef[40 + x], s6 = coef[48 + x], s7 = coef[56 + x];

            for (int y = 0; y < 8; y++)
            {
                float sum = 0f;
                sum += CosMatrix[y, 0] * s0;
                sum += CosMatrix[y, 1] * s1;
                sum += CosMatrix[y, 2] * s2;
                sum += CosMatrix[y, 3] * s3;
                sum += CosMatrix[y, 4] * s4;
                sum += CosMatrix[y, 5] * s5;
                sum += CosMatrix[y, 6] * s6;
                sum += CosMatrix[y, 7] * s7;
                tmp[y * 8 + x] = sum;
            }
        }

        // Row pass.
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                float sum = 0f;
                for (int u = 0; u < 8; u++)
                {
                    sum += CosMatrix[x, u] * tmp[y * 8 + u];
                }
                int value = (int)MathF.Round(sum * 0.25f) + 128;
                dest[offset + y * stride + x] = (short)(value < 0 ? 0 : value > 255 ? 255 : value);
            }
        }
    }

    // Upsample a component plane to full resolution. A component at the maximum sampling factor maps
    // 1:1; a sub-sampled component is box-upsampled (each source sample covers maxSamp/samp output cells).
    private static byte[] Upsample(Component c, int width, int height, int maxHsamp, int maxVsamp)
    {
        var output = new byte[width * height];
        byte[] src = new byte[c.PlaneW * c.PlaneH];
        for (int i = 0; i < src.Length; i++)
        {
            int v = c.Blocks[i];
            src[i] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
        }

        if (c.HSamp == maxHsamp && c.VSamp == maxVsamp)
        {
            for (int y = 0; y < height; y++)
            {
                int srow = y * c.PlaneW;
                int drow = y * width;
                for (int x = 0; x < width; x++)
                {
                    output[drow + x] = src[srow + x];
                }
            }
            return output;
        }

        for (int y = 0; y < height; y++)
        {
            int sy = (y * c.VSamp) / maxVsamp;
            if (sy >= c.PlaneH) sy = c.PlaneH - 1;
            int srow = sy * c.PlaneW;
            int drow = y * width;
            for (int x = 0; x < width; x++)
            {
                int sx = (x * c.HSamp) / maxHsamp;
                if (sx >= c.PlaneW) sx = c.PlaneW - 1;
                output[drow + x] = src[srow + sx];
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
        public bool HitError;

        public BitReader(byte[] data, int pos)
        {
            _data = data;
            _pos = pos;
        }

        // Pulls the next data byte, transparently handling 0xFF byte-stuffing and fill bytes.
        private int NextByte()
        {
            while (_pos < _data.Length)
            {
                byte b = _data[_pos++];
                if (b != 0xFF)
                {
                    return b;
                }
                if (_pos >= _data.Length)
                {
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
                // A real marker (0xFF D0..D7 restart, or EOI etc.) - do not consume it here.
                HitError = true;
                return 0;
            }
            HitError = true;
            return 0;
        }

        private void EnsureBits(int n)
        {
            while (_bitsLeft < n)
            {
                int b = NextByte();
                _bitBuf = (_bitBuf << 8) | (uint)b;
                _bitsLeft += 8;
            }
        }

        // Reads n bits (consuming them), MSB-first.
        public int GetBits(int n)
        {
            if (n == 0)
            {
                return 0;
            }
            EnsureBits(n);
            int v = (int)((_bitBuf >> (_bitsLeft - n)) & ((1u << n) - 1));
            _bitsLeft -= n;
            return v;
        }

        // Decodes one Huffman symbol, returning its value (or -1 on error).
        public int DecodeHuffman(HuffmanTable table)
        {
            if (table.IsEmpty)
            {
                HitError = true;
                return -1;
            }
            int code = 0;
            for (int len = 1; len <= 16; len++)
            {
                code = (code << 1) | GetBits(1);
                if (HitError)
                {
                    return -1;
                }
                if (table.MaxCode[len] >= 0 && code >= table.MinCode[len] && code <= table.MaxCode[len])
                {
                    int idx = table.ValPtr[len] + code - table.MinCode[len];
                    return idx >= 0 && idx < table.HuffVal.Length ? table.HuffVal[idx] : -1;
                }
            }
            return -1;
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

    private static readonly float[,] CosMatrix = BuildCosMatrix();

    private static float[,] BuildCosMatrix()
    {
        const double Pi = Math.PI;
        var m = new float[8, 8];
        for (int n = 0; n < 8; n++)
        {
            for (int u = 0; u < 8; u++)
            {
                double cu = u == 0 ? 1.0 / Math.Sqrt(2) : 1.0;
                m[n, u] = (float)(cu * Math.Cos((2 * n + 1) * u * Pi / 16.0));
            }
        }
        return m;
    }
}
