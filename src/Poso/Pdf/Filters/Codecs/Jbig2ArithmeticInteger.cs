namespace Poso.Pdf.Filters.Codecs;

/// <summary>
/// JBIG2 arithmetic integer and symbol-ID decoders (ITU-T T.88 Annex A.2 / A.3), ported faithfully from the
/// reference implementation <c>jbig2_arith_int.c</c> / <c>jbig2_arith_iaid.c</c> (Artifex jbig2dec). These are
/// the primitives used by symbol-dictionary and text regions to decode dimensions, deltas (IAD/IADL/IADS/IAIT)
/// and symbol indices with context-based MQ arithmetic coding. They operate on <see cref="Jbig2MqDecoder"/>.
/// </summary>
internal sealed class Jbig2ArithmeticInteger
{
    // IAx[] context array (Annex A.2). Indexed by a shifting PREV context (0..511).
    private readonly byte[] _iax = new byte[512];

    /// <summary>
    /// Decodes one arithmetic integer (A.2). Returns 0 on a normal value (via <paramref name="result"/>),
    /// 1 on an OOB (end-of-straw) return, or -1 on error. The sign bit S plus V==0 signals OOB.
    /// </summary>
    public int Decode(Jbig2MqDecoder mq, out int result)
    {
        result = 0;
        int prev = 1;

        int s = mq.Decode(ref _iax[prev]);
        if (s < 0)
        {
            return -1;
        }

        prev = (prev << 1) | s;

        int bit = mq.Decode(ref _iax[prev]);
        if (bit < 0)
        {
            return -1;
        }

        prev = (prev << 1) | bit;

        int nTail;
        int offset;
        if (bit != 0)
        {
            bit = mq.Decode(ref _iax[prev]);
            if (bit < 0)
            {
                return -1;
            }

            prev = (prev << 1) | bit;

            if (bit != 0)
            {
                bit = mq.Decode(ref _iax[prev]);
                if (bit < 0)
                {
                    return -1;
                }

                prev = (prev << 1) | bit;

                if (bit != 0)
                {
                    bit = mq.Decode(ref _iax[prev]);
                    if (bit < 0)
                    {
                        return -1;
                    }

                    prev = (prev << 1) | bit;

                    if (bit != 0)
                    {
                        bit = mq.Decode(ref _iax[prev]);
                        if (bit < 0)
                        {
                            return -1;
                        }

                        prev = (prev << 1) | bit;
                        nTail = bit != 0 ? 32 : 12;
                        offset = bit != 0 ? 4436 : 340;
                    }
                    else
                    {
                        nTail = 8;
                        offset = 84;
                    }
                }
                else
                {
                    nTail = 6;
                    offset = 20;
                }
            }
            else
            {
                nTail = 4;
                offset = 4;
            }
        }
        else
        {
            nTail = 2;
            offset = 0;
        }

        int v = 0;
        for (int i = 0; i < nTail; i++)
        {
            bit = mq.Decode(ref _iax[prev]);
            if (bit < 0)
            {
                return -1;
            }

            prev = ((prev << 1) & 511) | (prev & 256) | bit;
            v = (v << 1) | bit;
        }

        // offset >= 0 so V cannot underflow; clamp to avoid int overflow on the 32-bit tail.
        v = v > int.MaxValue - offset ? int.MaxValue : v + offset;
        v = s != 0 ? -v : v;
        result = v;
        return s != 0 && v == 0 ? 1 : 0;
    }
}

/// <summary>
/// JBIG2 IAID arithmetic symbol-index decoder (T.88 Annex A.3): decodes a <c>SBSYMCODELEN</c>-bit symbol
/// index via a binary tree of MQ contexts. Ported from <c>jbig2_arith_iaid.c</c>.
/// </summary>
internal sealed class Jbig2ArithmeticIaid
{
    private readonly byte[] _iaidx;
    private readonly int _codeLen;

    public Jbig2ArithmeticIaid(int symCodeLen)
    {
        if (symCodeLen > 31)
        {
            throw new System.OverflowException("JBIG2 IAID SBSYMCODELEN too large");
        }

        _codeLen = symCodeLen;
        _iaidx = new byte[1u << Math.Max(symCodeLen, 1)];
    }

    /// <summary>Decodes one symbol index (A.3). Returns 0 on success (via <paramref name="result"/>), -1 on error.</summary>
    public int Decode(Jbig2MqDecoder mq, out int result)
    {
        result = 0;
        int prev = 1;

        for (int i = 0; i < _codeLen; i++)
        {
            int d = mq.Decode(ref _iaidx[prev]);
            if (d < 0)
            {
                return -1;
            }

            prev = (prev << 1) | d;
        }

        prev -= 1 << _codeLen;
        result = prev;
        return 0;
    }
}
