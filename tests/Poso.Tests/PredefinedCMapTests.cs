using Poso.Pdf.Fonts;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// The predefined composite CMaps a non-embedded CJK font names. Their CID tables are not reproduced,
/// but the byte layout and the character each code stands for are enough to draw the text in a system
/// face, which is what the references do.
/// </summary>
public class PredefinedCMapTests
{
    [Fact]
    public void ShiftJis_MixesOneAndTwoByteCodes()
    {
        PredefinedCMap? map = PredefinedCMap.ForName("90ms-RKSJ-H", "Japan1");
        Assert.NotNull(map);

        // "(57)" is four one-byte codes, then two-byte kanji.
        byte[] bytes = { 0x28, 0x35, 0x37, 0x29, 0x81, 0x79, 0x97, 0x76 };
        var codes = new System.Collections.Generic.List<int>();
        int position = 0;
        while (position < bytes.Length)
        {
            int used = map!.Decode(bytes, position, out int code);
            if (used == 0)
            {
                position++;
                continue;
            }

            codes.Add(code);
            position += used;
        }

        Assert.Equal(new[] { 0x28, 0x35, 0x37, 0x29, 0x8179, 0x9776 }, codes);
        Assert.Equal((uint)'(', map.ToUnicode(0x28));
        Assert.Equal((uint)'5', map.ToUnicode(0x35));
        Assert.Equal(0x8981u, map.ToUnicode(0x9776)); // 要
    }

    [Fact]
    public void Utf16CMap_CodeIsTheCharacter()
    {
        PredefinedCMap? map = PredefinedCMap.ForName("UniGB-UTF16-H", "GB1");
        Assert.NotNull(map);
        Assert.Equal(0x76EEu, map!.ToUnicode(0x76EE)); // 目
    }
}
