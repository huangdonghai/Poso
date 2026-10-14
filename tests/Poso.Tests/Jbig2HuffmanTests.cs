using Poso.Pdf.Filters.Codecs;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// Deterministic self-check of the JBIG2 Huffman core against jbig2dec's own unit test
/// (jbig2_huffman.c): the stream { 0xe9, 0xcb, 0xf4, 0x00 } decoded with table indices
/// { B.4, B.2, B.2, B.1 } must yield 8, 5, OOB, 8. This pins the bit-reader + canonical-table
/// builder independently of any page vector before the region decoders rely on them.
/// </summary>
public class Jbig2HuffmanTests
{
    [Fact]
    public void Jbig2decSelfTest_Decodes8_5_Oob_8()
    {
        byte[] stream = [0xe9, 0xcb, 0xf4, 0x00];
        var hs = new Jbig2Huffman.State(stream, 0, stream.Length);

        int v0 = hs.Get(Jbig2Huffman.Standard(4), out int c0); // Table B.4
        int v1 = hs.Get(Jbig2Huffman.Standard(2), out int c1); // Table B.2
        int v2 = hs.Get(Jbig2Huffman.Standard(2), out int c2); // Table B.2 (OOB)
        int v3 = hs.Get(Jbig2Huffman.Standard(1), out int c3); // Table B.1

        Assert.Equal(0, c0);
        Assert.Equal(8, v0);
        Assert.Equal(0, c1);
        Assert.Equal(5, v1);
        Assert.Equal(1, c2);   // OOB
        Assert.Equal(0, c3);
        Assert.Equal(8, v3);
    }
}
