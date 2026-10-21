using System.Text;

namespace Poso.Pdf.Fonts;

/// <summary>
/// The byte layout and Unicode meaning of a predefined composite CMap (<c>/UniGB-UCS2-H</c>,
/// <c>/90ms-RKSJ-H</c>, <c>/GBK-EUC-H</c>, ...). Adobe's CMap resources, which also define the
/// code-to-CID tables, are not reproduced here: what a non-embedded CID font needs is the character
/// each code stands for, because its glyphs have to come from a substitute face. Codes stay in their
/// encoded form, which is also what /W and /CIDToGIDMap are keyed by in the subsets that carry them.
/// </summary>
internal sealed class PredefinedCMap
{
    // An explicit static constructor (not a field initializer: a type without one may run its field
    // initializers after a static method has already been entered).
    static PredefinedCMap()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private readonly Encoding? _encoding;
    private readonly int _codePage;

    private PredefinedCMap(Encoding? encoding, int codePage)
    {
        _encoding = encoding;
        _codePage = codePage;
    }

    /// <summary>The predefined encoding, or null for Identity and anything not recognised.</summary>
    public static PredefinedCMap? ForName(string name, string? ordering)
    {
        if (name.StartsWith("Identity", StringComparison.Ordinal))
        {
            return null;
        }

        // The Uni* CMaps come in a Unicode layout (two-byte code units that are the Unicode value) and
        // legacy layouts (GBK-EUC, RKSJ, B5, ...). Only the former needs no character set table.
        if (name.StartsWith("Uni", StringComparison.Ordinal))
        {
            return name.Contains("UTF16", StringComparison.Ordinal) || name.Contains("UCS2", StringComparison.Ordinal)
                ? new PredefinedCMap(null, 0)
                : null;
        }

        int codePage = CodePageFor(name, ordering);
        Encoding? encoding = codePage == 0 ? null : EncodingFor(codePage);
        return encoding is null ? null : new PredefinedCMap(encoding, codePage);
    }

    /// <summary>Reads one code at <paramref name="index"/>; returns the bytes consumed, or 0 at the end.</summary>
    public int Decode(ReadOnlySpan<byte> bytes, int index, out int code)
    {
        code = 0;
        if (index >= bytes.Length)
        {
            return 0;
        }

        int length = _encoding is null || IsLeadByte(bytes[index]) ? 2 : 1;
        if (index + length > bytes.Length)
        {
            // A truncated two-byte unit (an odd byte at the end of the string) has no character.
            return _encoding is null ? 0 : 1;
        }

        code = length == 2 ? (bytes[index] << 8) | bytes[index + 1] : bytes[index];
        return length;
    }

    /// <summary>The Unicode value a code stands for; 0 when the character set cannot name it.</summary>
    public uint ToUnicode(int code)
    {
        if (code < 0)
        {
            return 0;
        }

        if (_encoding is null)
        {
            return code <= 0xFFFF ? (uint)code : 0;
        }

        Span<byte> bytes = stackalloc byte[2];
        int length;
        if (code <= 0xFF)
        {
            bytes[0] = (byte)code;
            length = 1;
        }
        else
        {
            bytes[0] = (byte)(code >> 8);
            bytes[1] = (byte)code;
            length = 2;
        }

        string text = _encoding.GetString(bytes[..length]);
        if (text.Length == 0)
        {
            return 0;
        }

        if (text.Length == 2 && char.IsHighSurrogate(text[0]) && char.IsLowSurrogate(text[1]))
        {
            return (uint)char.ConvertToUtf32(text[0], text[1]);
        }

        return text[0];
    }

    private bool IsLeadByte(byte first) => _codePage switch
    {
        // Shift-JIS: 0x81-0x9F and 0xE0-0xEF begin a two-byte character.
        932 => (first >= 0x81 && first <= 0x9F) || (first >= 0xE0 && first <= 0xEF),

        // EUC-JP: SS2 (0x8E) and the 0xA1-0xFE plane.
        51932 => first == 0x8E || (first >= 0xA1 && first <= 0xFE),

        // GBK, UHC (superset of EUC-KR) and Big5 all use 0x81-0xFE as a lead byte.
        _ => first >= 0x81 && first <= 0xFE,
    };

    private static int CodePageFor(string name, string? ordering)
    {
        if (name.Contains("RKSJ", StringComparison.Ordinal))
        {
            return 932;
        }

        if (name.Contains("GBK", StringComparison.Ordinal) ||
            name.StartsWith("GB-EUC", StringComparison.Ordinal) ||
            name.StartsWith("GBpc", StringComparison.Ordinal) ||
            name.StartsWith("GBT", StringComparison.Ordinal))
        {
            return 936;
        }

        if (name.Contains("B5", StringComparison.Ordinal) ||
            name.StartsWith("ETen", StringComparison.Ordinal) ||
            name.StartsWith("HKscs", StringComparison.Ordinal))
        {
            return 950;
        }

        if (name.StartsWith("KSC", StringComparison.Ordinal))
        {
            return 949;
        }

        // A bare EUC-H/EUC-V only says which plane layout is used; the ordering names the character set.
        if (name.StartsWith("EUC", StringComparison.Ordinal))
        {
            return ordering switch
            {
                "Japan1" => 51932,
                "GB1" => 936,
                "Korea1" => 949,
                "CNS1" => 950,
                _ => 0,
            };
        }

        return 0;
    }

    private static Encoding? EncodingFor(int codePage)
    {
        try
        {
            return Encoding.GetEncoding(codePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

}
