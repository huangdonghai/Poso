namespace Poso.Pdf.Filters.Codecs;

/// <summary>
/// JBIG2 MQ arithmetic decoder (ITU-T T.88 Annex C / T.82), ported faithfully from the reference
/// implementation <c>jbig2_arith.c</c> (Artifex jbig2dec — the upstream of both MuPDF and pdf.js). The
/// 47-state probability table and the bytestream (byte-in), init, renormalization and decode routines
/// mirror the C source line-for-line, including the XOR-encoded state-transition trick.
/// A context is one byte: bit 7 = the current MPS value, bits 0..6 = the state-table index.
/// </summary>
internal sealed class Jbig2MqDecoder
{
    // State-table data transcribed from jbig2dec's jbig2_arith_Qe[] (Qe, NMPS, NLPS, SWITCH).
    private static readonly ushort[] Qe =
    {
        0x5601, 0x3401, 0x1801, 0x0AC1, 0x0521, 0x0221, 0x5601, 0x5401, 0x4801, 0x3801,
        0x3001, 0x2401, 0x1C01, 0x1601, 0x5601, 0x5401, 0x5101, 0x4801, 0x3801, 0x3401,
        0x3001, 0x2801, 0x2401, 0x2201, 0x1C01, 0x1801, 0x1601, 0x1401, 0x1201, 0x1101,
        0x0AC1, 0x09C1, 0x08A1, 0x0521, 0x0441, 0x02A1, 0x0221, 0x0141, 0x0111, 0x0085,
        0x0049, 0x0025, 0x0015, 0x0009, 0x0005, 0x0001, 0x5601,
    };

    private static readonly byte[] Nmps =
    {
        1, 2, 3, 4, 5, 38, 7, 8, 9, 10, 11, 12, 13, 29, 15, 16, 17, 18, 19, 20,
        21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40,
        41, 42, 43, 44, 45, 45, 46,
    };

    private static readonly byte[] Nlps =
    {
        1, 6, 9, 12, 29, 33, 6, 14, 14, 14, 17, 18, 20, 21, 14, 14, 15, 16, 17, 18,
        19, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37,
        38, 39, 40, 41, 42, 43, 46,
    };

    private static readonly byte[] Switch =
    {
        1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0,
    };

    // Precomputed transition XOR masks (mirrors C macros: MPS=index^NMPS, LPS=index^NLPS^(SWITCH<<7)).
    private static readonly byte[] MpsXor = new byte[47];
    private static readonly byte[] LpsXor = new byte[47];

    static Jbig2MqDecoder()
    {
        for (int i = 0; i < 47; i++)
        {
            MpsXor[i] = (byte)(i ^ Nmps[i]);
            LpsXor[i] = (byte)((i ^ Nlps[i]) ^ (Switch[i] << 7));
        }
    }

    private readonly byte[] _data;
    private readonly int _end;
    private uint _c;
    private uint _a;
    private int _ct;
    private uint _nextWord;
    private int _nextWordBytes;
    private bool _error;
    private int _offset;

    public Jbig2MqDecoder(byte[] data, int start, int end)
    {
        _data = data;
        _offset = start;
        _end = end;

        (_nextWord, _nextWordBytes) = ReadNextWord();
        if (_nextWordBytes == 0)
        {
            _error = true;
            return;
        }

        _offset += _nextWordBytes;

        // Figure F.1
        _c = (~(uint)(_nextWord >> 8)) & 0xFF0000;

        // Figure E.20 (2)
        ByteIn();

        // Figure E.20 (3)
        _c <<= 7;
        _ct -= 7;
        _a = 0x8000;
    }

    /// <summary>Decodes one binary decision for the given context; updates the context in place.</summary>
    public int Decode(ref byte context)
    {
        int index = context & 0x7F;
        // Figure F.2
        _a -= Qe[index];
        if ((_c >> 16) < _a)
        {
            if ((_a & 0x8000) == 0)
            {
                // MPS_EXCHANGE, Figure E.16
                int d;
                if (_a < Qe[index])
                {
                    d = 1 - (context >> 7);
                    context ^= LpsXor[index];
                }
                else
                {
                    d = context >> 7;
                    context ^= MpsXor[index];
                }

                RenormD();
                return d;
            }
            else
            {
                return context >> 7;
            }
        }
        else
        {
            _c -= _a << 16;
            // LPS_EXCHANGE, Figure E.17
            int d;
            if (_a < Qe[index])
            {
                _a = Qe[index];
                d = context >> 7;
                context ^= MpsXor[index];
            }
            else
            {
                _a = Qe[index];
                d = 1 - (context >> 7);
                context ^= LpsXor[index];
            }

            RenormD();
            return d;
        }
    }

    // Figure F.3: read one byte of the arithmetic-coded stream into C, handling the FF marker convention.
    private void ByteIn()
    {
        if (_error || _nextWordBytes == 0)
        {
            _error = true;
            return;
        }

        byte b = (byte)((_nextWord >> 24) & 0xFF);
        if (b == 0xFF)
        {
            byte b1;
            if (_nextWordBytes <= 1)
            {
                (_nextWord, _nextWordBytes) = ReadNextWord();
                if (_nextWordBytes == 0)
                {
                    _nextWord = 0xFF900000;
                    _nextWordBytes = 2;
                    _c += 0xFF00;
                    _ct = 8;
                    return;
                }

                _offset += _nextWordBytes;
                b1 = (byte)((_nextWord >> 24) & 0xFF);
                if (b1 > 0x8F)
                {
                    _ct = 8;
                    _nextWord = 0xFF000000 | (_nextWord >> 8);
                    _nextWordBytes = 2;
                    _offset--;
                }
                else
                {
                    _c += 0xFE00 - ((uint)b1 << 9);
                    _ct = 7;
                }
            }
            else
            {
                b1 = (byte)((_nextWord >> 16) & 0xFF);
                if (b1 > 0x8F)
                {
                    _ct = 8;
                }
                else
                {
                    _nextWordBytes--;
                    _nextWord <<= 8;
                    _c += 0xFE00 - ((uint)b1 << 9);
                    _ct = 7;
                }
            }
        }
        else
        {
            _nextWord <<= 8;
            _nextWordBytes--;
            if (_nextWordBytes == 0)
            {
                (_nextWord, _nextWordBytes) = ReadNextWord();
                if (_nextWordBytes == 0)
                {
                    _nextWord = 0xFF900000;
                    _nextWordBytes = 2;
                    _c += 0xFF00;
                    _ct = 8;
                    return;
                }

                _offset += _nextWordBytes;
            }

            b = (byte)((_nextWord >> 24) & 0xFF);
            _c += 0xFF00 - ((uint)b << 8);
            _ct = 8;
        }
    }

    // Figure E.18: renormalize the interval, pulling more bytes when the counter is exhausted.
    private void RenormD()
    {
        do
        {
            if (_ct == 0)
            {
                ByteIn();
                if (_error)
                {
                    return;
                }
            }

            _a <<= 1;
            _c <<= 1;
            _ct--;
        }
        while ((_a & 0x8000) == 0);
    }

    private (uint Word, int Bytes) ReadNextWord()
    {
        int remaining = _end - _offset;
        if (remaining <= 0)
        {
            return (0, 0);
        }

        uint val = (uint)_data[_offset] << 24;
        int count = 1;
        if (remaining > 1)
        {
            val |= (uint)_data[_offset + 1] << 16;
            count++;
        }
        if (remaining > 2)
        {
            val |= (uint)_data[_offset + 2] << 8;
            count++;
        }
        if (remaining > 3)
        {
            val |= _data[_offset + 3];
            count++;
        }

        return (val, count);
    }
}
