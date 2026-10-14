using System;

namespace Poso.Pdf.Filters.Codecs;

/// <summary>
/// CCITT fax decoder (T.4 Group 3 1-D / 2-D and T.6 Group 4). A faithful C# port of the Xpdf-derived
/// reference implementation also used by pdf.js, so the Huffman tables are spec-exact. Decoded output
/// bytes pack pixels MSB-first; with the /BlackIs1 flag a 1 bit means black (matching the PDF filter
/// convention). Validated against MuPDF-generated reference vectors.
/// </summary>
/// internal to the imaging pipeline but public for validation tests.
public static class CcittFaxDecoder
{
    private const int Eol = -2;    private const int Eof = -1;

    private const int Pass = 0;
    private const int Horiz = 1;
    private const int Vert0 = 2;
    private const int VertR1 = 3;
    private const int VertL1 = 4;
    private const int VertR2 = 5;
    private const int VertL2 = 6;
    private const int VertR3 = 7;
    private const int VertL3 = 8;

    // Decoded output bit convention before /BlackIs1: 1 = white, 0 = black.
    // Returns packed rows (ceil(columns/8) bytes each), rows*ceil(columns/8) total. Bits beyond
    // the last column in a row are zero.
    public static byte[] Decode(byte[] data, int columns, int rows, int k, bool encodedByteAlign, bool blackIs1, bool endOfBlock, bool endOfLine)
    {
        var decoder = new State(data, columns, rows, k, encodedByteAlign, blackIs1, endOfBlock, endOfLine);
        int bytesPerRow = (columns + 7) / 8;
        var output = new byte[Math.Max(0, rows) * bytesPerRow];
        int produced = 0;
        int limit = output.Length;
        while (produced < limit)
        {
            int value = decoder.ReadNextChar();
            if (value < 0)
            {
                break; // stream exhausted / end of data
            }

            output[produced++] = (byte)value;
        }

        return output;
    }

    private sealed class State
    {
        private readonly byte[] _data;
        private int _sourcePos;

        private readonly int _columns;
        private readonly int _rows;
        private readonly int _encoding;
        private readonly bool _endOfLine;
        private readonly bool _byteAlign;
        private readonly bool _endOfBlock;
        private readonly bool _black;

        private readonly int[] _codingLine;
        private readonly int[] _refLine;
        private int _codingPos;
        private int _row;
        private bool _nextLine2D;
        private int _inputBits;
        private int _inputBuf;
        private int _outputBits;
        private bool _rowsDone;
        private bool _eof;
        private bool _err;

        public State(byte[] data, int columns, int rows, int k, bool byteAlign, bool blackIs1, bool endOfBlock, bool endOfLine)
        {
            _data = data;
            _columns = columns <= 0 ? 1728 : columns;
            _rows = rows;
            _encoding = k;
            _byteAlign = byteAlign;
            _endOfBlock = endOfBlock;
            _endOfLine = endOfLine;
            _black = blackIs1;

            _codingLine = new int[_columns + 1];
            _refLine = new int[_columns + 2];
            _codingLine[0] = _columns;
            _codingPos = 0;
            _row = 0;
            _nextLine2D = _encoding < 0;

            int code1;
            while ((code1 = LookBits(12)) == 0)
            {
                EatBits(1);
            }

            if (code1 == 1)
            {
                EatBits(12);
            }

            if (_encoding > 0)
            {
                _nextLine2D = LookBits(1) == 0;
                EatBits(1);
            }
        }

        public int ReadNextChar()
        {
            if (_eof)
            {
                return -1;
            }

            int[] refLine = _refLine;
            int[] codingLine = _codingLine;
            int columns = _columns;
            int refPos, blackPixels, bits, i;

            if (_outputBits == 0)
            {
                if (_rowsDone)
                {
                    _eof = true;
                }

                if (_eof)
                {
                    return -1;
                }

                _err = false;
                int code1, code2, code3;
                if (_nextLine2D)
                {
                    for (i = 0; codingLine[i] < columns; ++i)
                    {
                        refLine[i] = codingLine[i];
                    }

                    refLine[i++] = columns;
                    refLine[i] = columns;
                    codingLine[0] = 0;
                    _codingPos = 0;
                    refPos = 0;
                    blackPixels = 0;

                    while (codingLine[_codingPos] < columns)
                    {
                        code1 = GetTwoDimCode();
                        switch (code1)
                        {
                            case Pass:
                                AddPixels(refLine[refPos + 1], blackPixels);
                                if (refLine[refPos + 1] < columns)
                                {
                                    refPos += 2;
                                }

                                break;
                            case Horiz:
                                code1 = code2 = 0;
                                if (blackPixels != 0)
                                {
                                    do
                                    {
                                        code1 += code3 = GetBlackCode();
                                    }
                                    while (code3 >= 64);
                                    do
                                    {
                                        code2 += code3 = GetWhiteCode();
                                    }
                                    while (code3 >= 64);
                                }
                                else
                                {
                                    do
                                    {
                                        code1 += code3 = GetWhiteCode();
                                    }
                                    while (code3 >= 64);
                                    do
                                    {
                                        code2 += code3 = GetBlackCode();
                                    }
                                    while (code3 >= 64);
                                }

                                AddPixels(codingLine[_codingPos] + code1, blackPixels);
                                if (codingLine[_codingPos] < columns)
                                {
                                    AddPixels(codingLine[_codingPos] + code2, blackPixels ^ 1);
                                }

                                while (refLine[refPos] <= codingLine[_codingPos] && refLine[refPos] < columns)
                                {
                                    refPos += 2;
                                }

                                break;
                            case VertR3:
                            case VertR2:
                            case VertR1:
                            case Vert0:
                            {
                                int delta = code1 == VertR3 ? 3 : code1 == VertR2 ? 2 : code1 == VertR1 ? 1 : 0;
                                AddPixels(refLine[refPos] + delta, blackPixels);
                                blackPixels ^= 1;
                                if (codingLine[_codingPos] < columns)
                                {
                                    ++refPos;
                                    while (refLine[refPos] <= codingLine[_codingPos] && refLine[refPos] < columns)
                                    {
                                        refPos += 2;
                                    }
                                }

                                break;
                            }

                            case VertL3:
                            case VertL2:
                            case VertL1:
                            {
                                int delta = code1 == VertL3 ? 3 : code1 == VertL2 ? 2 : 1;
                                AddPixelsNeg(refLine[refPos] - delta, blackPixels);
                                blackPixels ^= 1;
                                if (codingLine[_codingPos] < columns)
                                {
                                    if (refPos > 0)
                                    {
                                        --refPos;
                                    }
                                    else
                                    {
                                        ++refPos;
                                    }

                                    while (refLine[refPos] <= codingLine[_codingPos] && refLine[refPos] < columns)
                                    {
                                        refPos += 2;
                                    }
                                }

                                break;
                            }

                            case Eof:
                                AddPixels(columns, 0);
                                _eof = true;
                                break;
                            default:
                                AddPixels(columns, 0);
                                _err = true;
                                break;
                        }
                    }
                }
                else
                {
                    codingLine[0] = 0;
                    _codingPos = 0;
                    blackPixels = 0;
                    while (codingLine[_codingPos] < columns)
                    {
                        code1 = 0;
                        if (blackPixels != 0)
                        {
                            do
                            {
                                code1 += code3 = GetBlackCode();
                            }
                            while (code3 >= 64);
                        }
                        else
                        {
                            do
                            {
                                code1 += code3 = GetWhiteCode();
                            }
                            while (code3 >= 64);
                        }

                        AddPixels(codingLine[_codingPos] + code1, blackPixels);
                        blackPixels ^= 1;
                    }
                }

                bool gotEol = false;

                if (_byteAlign)
                {
                    _inputBits &= ~7;
                }

                if (!_endOfBlock && _row == _rows - 1)
                {
                    _rowsDone = true;
                }
                else
                {
                    code1 = LookBits(12);
                    if (_endOfLine)
                    {
                        while (code1 != Eof && code1 != 1)
                        {
                            EatBits(1);
                            code1 = LookBits(12);
                        }
                    }
                    else
                    {
                        while (code1 == 0)
                        {
                            EatBits(1);
                            code1 = LookBits(12);
                        }
                    }

                    if (code1 == 1)
                    {
                        EatBits(12);
                        gotEol = true;
                    }
                    else if (code1 == Eof)
                    {
                        _eof = true;
                    }
                }

                if (!_eof && _encoding > 0 && !_rowsDone)
                {
                    _nextLine2D = LookBits(1) == 0;
                    EatBits(1);
                }

                if (_endOfBlock && gotEol && _byteAlign)
                {
                    code1 = LookBits(12);
                    if (code1 == 1)
                    {
                        EatBits(12);
                        if (_encoding > 0)
                        {
                            LookBits(1);
                            EatBits(1);
                        }

                        if (_encoding >= 0)
                        {
                            for (i = 0; i < 4; ++i)
                            {
                                code1 = LookBits(12);
                                EatBits(12);
                                if (_encoding > 0)
                                {
                                    LookBits(1);
                                    EatBits(1);
                                }
                            }
                        }

                        _eof = true;
                    }
                }
                else if (_err && _endOfLine)
                {
                    while (true)
                    {
                        code1 = LookBits(13);
                        if (code1 == Eof)
                        {
                            _eof = true;
                            return -1;
                        }

                        if (code1 >> 1 == 1)
                        {
                            break;
                        }

                        EatBits(1);
                    }

                    EatBits(12);
                    if (_encoding > 0)
                    {
                        EatBits(1);
                        _nextLine2D = (code1 & 1) == 0;
                    }
                }

                _outputBits = codingLine[0] > 0 ? codingLine[_codingPos = 0] : codingLine[_codingPos = 1];
                _row++;
            }

            int c;
            if (_outputBits >= 8)
            {
                c = (_codingPos & 1) != 0 ? 0 : 0xff;
                _outputBits -= 8;
                if (_outputBits == 0 && codingLine[_codingPos] < columns)
                {
                    _codingPos++;
                    _outputBits = codingLine[_codingPos] - codingLine[_codingPos - 1];
                }
            }
            else
            {
                bits = 8;
                c = 0;
                do
                {
                    if (_outputBits > bits)
                    {
                        c <<= bits;
                        if ((_codingPos & 1) == 0)
                        {
                            c |= 0xff >> (8 - bits);
                        }

                        _outputBits -= bits;
                        bits = 0;
                    }
                    else
                    {
                        c <<= _outputBits;
                        if ((_codingPos & 1) == 0)
                        {
                            c |= 0xff >> (8 - _outputBits);
                        }

                        bits -= _outputBits;
                        _outputBits = 0;
                        if (codingLine[_codingPos] < columns)
                        {
                            _codingPos++;
                            _outputBits = codingLine[_codingPos] - codingLine[_codingPos - 1];
                        }
                        else if (bits > 0)
                        {
                            c <<= bits;
                            bits = 0;
                        }
                    }
                }
                while (bits != 0);
            }

            // Base output is 1 = black. The PDF /BlackIs1 flag selects the decoded-array convention:
            // true -> 1 means black (keep), false -> 1 means white (invert).
            if (!_black)
            {
                c ^= 0xff;
            }

            return c;
        }

        private void AddPixels(int a1, int blackPixels)
        {
            int[] codingLine = _codingLine;
            int codingPos = _codingPos;
            if (a1 > codingLine[codingPos])
            {
                if (a1 > _columns)
                {
                    _err = true;
                    a1 = _columns;
                }

                if (((codingPos & 1) ^ blackPixels) != 0)
                {
                    ++codingPos;
                }

                codingLine[codingPos] = a1;
            }

            _codingPos = codingPos;
        }

        private void AddPixelsNeg(int a1, int blackPixels)
        {
            int[] codingLine = _codingLine;
            int codingPos = _codingPos;
            if (a1 > codingLine[codingPos])
            {
                if (a1 > _columns)
                {
                    _err = true;
                    a1 = _columns;
                }

                if (((codingPos & 1) ^ blackPixels) != 0)
                {
                    ++codingPos;
                }

                codingLine[codingPos] = a1;
            }
            else if (a1 < codingLine[codingPos])
            {
                if (a1 < 0)
                {
                    _err = true;
                    a1 = 0;
                }

                while (codingPos > 0 && a1 < codingLine[codingPos - 1])
                {
                    --codingPos;
                }

                codingLine[codingPos] = a1;
            }

            _codingPos = codingPos;
        }

        private (bool found, int code, bool valid) FindTableCode(int start, int end, int[][] table, int limit)
        {
            for (int i = start; i <= end; ++i)
            {
                int code = LookBits(i);
                if (code == Eof)
                {
                    return (true, 1, false);
                }

                if (i < end)
                {
                    code <<= end - i;
                }

                if (limit == 0 || code >= limit)
                {
                    int[] entry = table[code - limit];
                    if (entry[0] == i)
                    {
                        EatBits(i);
                        return (true, entry[1], true);
                    }
                }
            }

            return (false, 0, false);
        }

        private int GetTwoDimCode()
        {
            if (_endOfBlock)
            {
                int code = LookBits(7);
                if (code != Eof)
                {
                    int[] entry = CcittTables.TwoDim[code];
                    if (entry[0] > 0)
                    {
                        EatBits(entry[0]);
                        return entry[1];
                    }
                }
            }
            else
            {
                (bool found, int code, bool valid) = FindTableCode(1, 7, CcittTables.TwoDim, 0);
                if (found && valid)
                {
                    return code;
                }
            }

            return Eof;
        }

        private int GetWhiteCode()
        {
            if (_endOfBlock)
            {
                int code = LookBits(12);
                if (code == Eof)
                {
                    return 1;
                }

                int[] entry = code >> 5 == 0 ? CcittTables.White1[code] : CcittTables.White2[code >> 3];
                if (entry[0] > 0)
                {
                    EatBits(entry[0]);
                    return entry[1];
                }
            }
            else
            {
                (bool found, int code, bool valid) = FindTableCode(1, 9, CcittTables.White2, 0);
                if (found)
                {
                    return code;
                }

                (found, code, valid) = FindTableCode(11, 12, CcittTables.White1, 0);
                if (found)
                {
                    return code;
                }
            }

            EatBits(1);
            return 1;
        }

        private int GetBlackCode()
        {
            if (_endOfBlock)
            {
                int code = LookBits(13);
                if (code == Eof)
                {
                    return 1;
                }

                int[] entry;
                if (code >> 7 == 0)
                {
                    entry = CcittTables.Black1[code];
                }
                else if (code >> 9 == 0)
                {
                    entry = CcittTables.Black2[(code >> 1) - 64];
                }
                else
                {
                    entry = CcittTables.Black3[code >> 7];
                }

                if (entry[0] > 0)
                {
                    EatBits(entry[0]);
                    return entry[1];
                }
            }
            else
            {
                (bool found, int code, bool valid) = FindTableCode(2, 6, CcittTables.Black3, 0);
                if (found)
                {
                    return code;
                }

                (found, code, valid) = FindTableCode(7, 12, CcittTables.Black2, 64);
                if (found)
                {
                    return code;
                }

                (found, code, valid) = FindTableCode(10, 13, CcittTables.Black1, 0);
                if (found)
                {
                    return code;
                }
            }

            EatBits(1);
            return 1;
        }

        private int LookBits(int n)
        {
            while (_inputBits < n)
            {
                int c;
                if (_sourcePos >= _data.Length)
                {
                    c = -1;
                }
                else
                {
                    c = _data[_sourcePos++];
                }

                if (c == -1)
                {
                    if (_inputBits == 0)
                    {
                        return Eof;
                    }

                    return (_inputBuf << (n - _inputBits)) & (0xffff >> (16 - n));
                }

                _inputBuf = (_inputBuf << 8) | c;
                _inputBits += 8;
            }

            return (_inputBuf >> (_inputBits - n)) & (0xffff >> (16 - n));
        }

        private void EatBits(int n)
        {
            _inputBits -= n;
            if (_inputBits < 0)
            {
                _inputBits = 0;
            }
        }
    }
}
