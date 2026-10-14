using System.Collections.Generic;

namespace Poso.Pdf.Fonts;

/// <summary>A single Type 1 glyph converted to a Type 2 (CFF) charstring.</summary>
public sealed class Type1Glyph
{
    public required string Name { get; init; }

    /// <summary>The Type 2 charstring bytes (subroutines inlined, hints dropped, width folded into the first moveto).</summary>
    public required byte[] Charstring { get; init; }

    /// <summary>Glyph advance width in glyph-space units (from hsbw/sbw), 0 when the program omits it.</summary>
    public int Width { get; init; }
}

/// <summary>
/// Reader for a binary Type 1 font program (PDF <c>FontFile</c>). It decrypts the <c>eexec</c> block,
/// extracts the Private dictionary, subroutines and charstrings, and converts every Type 1 charstring
/// into a Type 2 (CFF) charstring so the result can be wrapped as CFF and handed to the graphics
/// back end. The algorithm mirrors pdf.js's <c>type1_parser.js</c> (validated against MuPDF).
/// </summary>
public sealed class Type1Program
{
    private const int EexecKey = 55665;
    private const int CharStringsKey = 4330;

    public float[] FontMatrix { get; }
    public float[] FontBBox { get; }

    /// <summary>The program's built-in encoding: code (0-255) to glyph name, or null when the header had none.</summary>
    public string?[]? BuiltInEncoding { get; }

    /// <summary>Glyphs in program order (each gets a GID after the synthetic .notdef).</summary>
    public IReadOnlyList<Type1Glyph> Glyphs { get; }

    private Type1Program(float[] fontMatrix, float[] fontBBox, string?[]? builtInEncoding, List<Type1Glyph> glyphs)
    {
        FontMatrix = fontMatrix;
        FontBBox = fontBBox;
        BuiltInEncoding = builtInEncoding;
        Glyphs = glyphs;
    }

    /// <summary>Parses a binary Type 1 program; returns false for CID-keyed, eexec-less or malformed data.</summary>
    public static bool TryCreate(byte[] data, out Type1Program? program)
    {
        program = null;
        if (data is not { Length: > 16 })
        {
            return false;
        }

        int start = 0;
        // Some generators embed the .pfb verbatim: strip the leading 6-byte binary record header.
        if (data[0] == 0x80 && data[1] == 0x01)
        {
            start = 6;
        }

        // Locate the "eexec" marker; everything before it is the cleartext header, after it the
        // eexec-encrypted program (Private dict + Subrs + CharStrings).
        int eexec = IndexOf(data, start, [0x65, 0x65, 0x78, 0x65, 0x63]); // "eexec"
        if (eexec < 0)
        {
            return false;
        }

        var header = new PsStream(Slice(data, start, eexec - start));
        float[] fontMatrix = [0.001f, 0f, 0f, 0.001f, 0f, 0f];
        float[] fontBBox = [0f, 0f, 0f, 0f];
        string?[]? builtIn = null;
        ReadHeader(header, fontMatrix, fontBBox, ref builtIn);

        int body = SkipToNonSpace(data, eexec + 5);
        // A second pfb binary record header (0x80 0x02) may sit between "eexec" and the ciphertext.
        if (body + 1 < data.Length && data[body] == 0x80 && data[body + 1] == 0x02)
        {
            body += 6;
        }

        byte[] decrypted = Decrypt(Slice(data, body, data.Length - body), EexecKey, 4);
        if (decrypted.Length == 0)
        {
            return false;
        }

        var glyphs = new List<Type1Glyph>();
        if (!ReadProgram(new PsStream(decrypted), glyphs))
        {
            return false;
        }

        program = new Type1Program(fontMatrix, fontBBox, builtIn, glyphs);
        return true;
    }

    // ---- Cleartext header: FontMatrix / FontBBox / Encoding ----

    private static void ReadHeader(PsStream s, float[] fontMatrix, float[] fontBBox, ref string?[]? builtIn)
    {
        string? token;
        while ((token = s.GetToken()) is not null)
        {
            if (token != "/")
            {
                continue;
            }

            token = s.GetToken();
            switch (token)
            {
                case "FontMatrix":
                    ReadArray(s, fontMatrix);
                    break;
                case "FontBBox":
                    ReadArray(s, fontBBox);
                    break;
                case "Encoding":
                    builtIn = ReadEncoding(s);
                    break;
            }
        }
    }

    private static string?[]? ReadEncoding(PsStream s)
    {
        string? arg = s.GetToken();
        if (arg is null)
        {
            return null;
        }

        // A named encoding (e.g. StandardEncoding) is handled by the PDF /Encoding; only a built-in
        // array here carries per-code glyph names we can reuse.
        if (!IsAllDigits(arg))
        {
            return null;
        }

        int size = int.Parse(arg);
        var table = new string?[256];
        s.GetToken(); // "array"
        while (s.GetToken() == "dup")
        {
            int index = s.ReadInt();
            s.GetToken(); // "/"
            string? glyph = s.GetToken();
            s.GetToken(); // "put"
            if (index >= 0 && index < 256 && glyph is not null)
            {
                table[index] = glyph;
            }
        }

        _ = size;
        return table;
    }

    // ---- eexec body: Private (lenIV) / Subrs / CharStrings ----

    private static bool ReadProgram(PsStream s, List<Type1Glyph> glyphs)
    {
        var subrs = new List<byte[]>();
        var charstrings = new List<(string name, byte[] encoded)>();
        int lenIV = 4;
        bool subrsParsed = false;
        bool charStringsParsed = false;

        string? token;
        while ((token = s.GetToken()) is not null)
        {
            if (token != "/")
            {
                continue;
            }

            token = s.GetToken();
            switch (token)
            {
                case "lenIV":
                    lenIV = (int)s.ReadNumber();
                    break;
                case "BlueValues" or "OtherBlues" or "FamilyBlues" or "FamilyOtherBlues"
                    or "StemSnapH" or "StemSnapV" or "StdHW" or "StdVW":
                    s.ReadNumberArray();
                    break;
                case "Subrs":
                    if (subrsParsed)
                    {
                        break;
                    }

                    subrsParsed = true;
                    s.ReadInt(); // num
                    s.GetToken(); // "array"
                    while (s.GetToken() == "dup")
                    {
                        int index = s.ReadInt();
                        int length = s.ReadInt();
                        s.GetToken(); // "RD" / "-|"
                        byte[] data = length > 0 ? s.GetBytes(length) : [];
                        s.GetByte();
                        string? t = s.GetToken(); // "NP" / "|"
                        if (t == "noaccess")
                        {
                            s.GetToken(); // "put"
                        }

                        while (subrs.Count <= index)
                        {
                            subrs.Add([]);
                        }

                        subrs[index] = Decrypt(data, CharStringsKey, lenIV);
                    }

                    break;
                case "CharStrings":
                    if (charStringsParsed)
                    {
                        break;
                    }

                    charStringsParsed = true;
                    s.GetToken(); // num
                    s.GetToken(); // "dict"
                    s.GetToken(); // "dup"
                    s.GetToken(); // "begin"
                    while (true)
                    {
                        token = s.GetToken();
                        if (token is null || token == "end")
                        {
                            break;
                        }

                        if (token != "/")
                        {
                            continue;
                        }

                        string? glyph = s.GetToken();
                        int length = s.ReadInt();
                        s.GetToken(); // "RD" / "-|"
                        byte[] data = length > 0 ? s.GetBytes(length) : [];
                        byte[] encoded = Decrypt(data, CharStringsKey, lenIV);
                        s.GetByte();
                        token = s.GetToken(); // "ND" / "|-"
                        if (token == "noaccess")
                        {
                            s.GetToken(); // "def"
                        }
                        else if (token == "/")
                        {
                            s.PrevChar();
                        }

                        if (glyph is not null)
                        {
                            charstrings.Add((glyph, encoded));
                        }
                    }

                    break;
            }
        }

        foreach ((string name, byte[] encoded) in charstrings)
        {
            var cs = new Type1CharString();
            bool error = cs.Convert(encoded, subrs);
            glyphs.Add(new Type1Glyph
            {
                Name = name,
                // FreeType ignores a glyph whose charstring errors; emit a bare endchar so the CFF stays valid.
                Charstring = error ? [14] : cs.Output.ToArray(),
                Width = (int)cs.Width,
            });
        }

        return charStringsParsed;
    }

    // ---- Type 1 charstring -> Type 2 charstring (mirrors pdf.js Type1CharString.convert) ----

    private sealed class Type1CharString
    {
        private readonly List<byte> output = [];
        private readonly List<double> stack = [];

        public double Width { get; private set; }

        private double lsb;
        private bool flexing;

        public List<byte> Output => output;

        public bool Convert(byte[] encoded, List<byte[]> subrs)
        {
            int count = encoded.Length;
            bool error = false;
            for (int i = 0; i < count; i++)
            {
                int value = encoded[i];
                if (value < 32)
                {
                    if (value == 12)
                    {
                        i++;
                        value = (value << 8) + encoded[i];
                    }

                    switch (value)
                    {
                        case 1: // hstem (hint)
                        case 3: // vstem (hint)
                        case (12 << 8) + 1: // vstem3
                        case (12 << 8) + 2: // hstem3
                            stack.Clear();
                            break;
                        case 4: // vmoveto
                            if (flexing)
                            {
                                if (stack.Count < 1)
                                {
                                    error = true;
                                    break;
                                }

                                double dy = Pop();
                                stack.Add(0);
                                stack.Add(dy);
                                break;
                            }

                            error = Execute(1, [4]);
                            break;
                        case 5: // rlineto
                            error = Execute(2, [5]);
                            break;
                        case 6: // hlineto
                            error = Execute(1, [6]);
                            break;
                        case 7: // vlineto
                            error = Execute(1, [7]);
                            break;
                        case 8: // rrcurveto
                            error = Execute(6, [8]);
                            break;
                        case 9: // closepath
                            stack.Clear();
                            break;
                        case 10: // callsubr
                            if (stack.Count < 1)
                            {
                                error = true;
                                break;
                            }

                            int sn = (int)Pop();
                            if (sn < 0 || sn >= subrs.Count)
                            {
                                error = true;
                                break;
                            }

                            error = Convert(subrs[sn], subrs);
                            break;
                        case 11: // return
                            return error;
                        case 13: // hsbw
                            if (stack.Count < 2)
                            {
                                error = true;
                                break;
                            }

                            double wx = Pop();
                            double sbx = Pop();
                            lsb = sbx;
                            Width = wx;
                            stack.Add(wx);
                            stack.Add(sbx);
                            error = Execute(2, [22]); // hmoveto
                            break;
                        case 14: // endchar
                            output.Add(14);
                            break;
                        case 21: // rmoveto
                            if (flexing)
                            {
                                break;
                            }

                            error = Execute(2, [21]);
                            break;
                        case 22: // hmoveto
                            if (flexing)
                            {
                                stack.Add(0);
                                break;
                            }

                            error = Execute(1, [22]);
                            break;
                        case 30: // vhcurveto
                            error = Execute(4, [30]);
                            break;
                        case 31: // hvcurveto
                            error = Execute(4, [31]);
                            break;
                        case (12 << 8) + 0: // dotsection
                            stack.Clear();
                            break;
                        case (12 << 8) + 6: // seac
                            error = Execute(4, [14]);
                            break;
                        case (12 << 8) + 7: // sbw
                            if (stack.Count < 4)
                            {
                                error = true;
                                break;
                            }

                            Pop(); // wy
                            double swx = Pop();
                            double sby = Pop();
                            double sbx2 = Pop();
                            lsb = sbx2;
                            Width = swx;
                            stack.Add(swx);
                            stack.Add(sbx2);
                            stack.Add(sby);
                            error = Execute(3, [21]); // rmoveto
                            break;
                        case (12 << 8) + 12: // div
                            if (stack.Count < 2)
                            {
                                error = true;
                                break;
                            }

                            double n2 = Pop();
                            double n1 = Pop();
                            stack.Add(n2 == 0 ? 0 : n1 / n2);
                            break;
                        case (12 << 8) + 16: // callothersubr
                            if (stack.Count < 2)
                            {
                                error = true;
                                break;
                            }

                            int subrNumber = (int)Pop();
                            int numArgs = (int)Pop();
                            if (subrNumber == 0 && numArgs == 3)
                            {
                                error = EmitFlex();
                                flexing = false;
                            }
                            else if (subrNumber == 1 && numArgs == 0)
                            {
                                flexing = true;
                            }

                            break;
                        case (12 << 8) + 17: // pop
                            break;
                        case (12 << 8) + 33: // setcurrentpoint
                            stack.Clear();
                            break;
                    }

                    if (error)
                    {
                        break;
                    }

                    continue;
                }
                else if (value <= 246)
                {
                    value -= 139;
                }
                else if (value <= 250)
                {
                    i++;
                    value = ((value - 247) * 256) + encoded[i] + 108;
                }
                else if (value <= 254)
                {
                    i++;
                    value = -((value - 251) * 256) - encoded[i] - 108;
                }
                else
                {
                    value = (encoded[++i] << 24) | (encoded[++i] << 16) | (encoded[++i] << 8) | encoded[++i];
                }

                stack.Add(value);
            }

            return error;
        }

        // flex (callothersubr 0 3): fold the 17-operand flex sequence into a single Type 2 flex (13 args).
        private bool EmitFlex()
        {
            if (stack.Count < 17)
            {
                return true;
            }

            int baseIndex = stack.Count - 17;
            double[] a = new double[17];
            for (int i = 0; i < 17; i++)
            {
                a[i] = stack[baseIndex + i];
            }

            stack.RemoveRange(baseIndex, 17);
            stack.Add(a[2] + a[0]);
            stack.Add(a[3] + a[1]);
            stack.Add(a[4]);
            stack.Add(a[5]);
            stack.Add(a[6]);
            stack.Add(a[7]);
            stack.Add(a[8]);
            stack.Add(a[9]);
            stack.Add(a[10]);
            stack.Add(a[11]);
            stack.Add(a[12]);
            stack.Add(a[13]);
            stack.Add(a[14]);
            bool err = Execute(13, [12, 35], keepStack: true);
            stack.Add(a[15]);
            stack.Add(a[16]);
            return err;
        }

        private double Pop()
        {
            double v = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            return v;
        }

        private bool Execute(int howMany, byte[] command, bool keepStack = false)
        {
            int stackLength = stack.Count;
            if (howMany > stackLength)
            {
                return true;
            }

            int start = stackLength - howMany;
            for (int i = start; i < stackLength; i++)
            {
                EmitNumber(stack[i]);
            }

            output.AddRange(command);
            if (keepStack)
            {
                stack.RemoveRange(start, howMany);
            }
            else
            {
                stack.Clear();
            }

            return false;
        }

        private void EmitNumber(double value)
        {
            if (value == System.Math.Floor(value) && value >= short.MinValue && value <= short.MaxValue)
            {
                int v = (int)value;
                output.Add(28);
                output.Add((byte)((v >> 8) & 0xff));
                output.Add((byte)(v & 0xff));
            }
            else
            {
                int fixedValue = (int)(65536 * value);
                output.Add(255);
                output.Add((byte)((fixedValue >> 24) & 0xff));
                output.Add((byte)((fixedValue >> 16) & 0xff));
                output.Add((byte)((fixedValue >> 8) & 0xff));
                output.Add((byte)(fixedValue & 0xff));
            }
        }
    }

    // ---- byte helpers ----

    private static byte[] Slice(byte[] data, int start, int length)
    {
        if (start < 0)
        {
            start = 0;
        }

        length = System.Math.Min(length, data.Length - start);
        if (length <= 0)
        {
            return [];
        }

        var slice = new byte[length];
        System.Array.Copy(data, start, slice, 0, length);
        return slice;
    }

    private static int IndexOf(byte[] data, int start, byte[] pattern)
    {
        for (int i = start; i <= data.Length - pattern.Length; i++)
        {
            bool hit = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] != pattern[j])
                {
                    hit = false;
                    break;
                }
            }

            if (hit)
            {
                return i;
            }
        }

        return -1;
    }

    private static int SkipToNonSpace(byte[] data, int from)
    {
        int i = from;
        while (i < data.Length && (data[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0))
        {
            i++;
        }

        return i;
    }

    private static void ReadArray(PsStream s, float[] target)
    {
        float[] arr = s.ReadNumberArray();
        for (int i = 0; i < target.Length && i < arr.Length; i++)
        {
            target[i] = arr[i];
        }
    }

    private static bool IsAllDigits(string s)
    {
        if (s.Length == 0)
        {
            return false;
        }

        foreach (char c in s)
        {
            if (c is not (>= '0' and <= '9'))
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] Decrypt(byte[] data, int key, int discard)
    {
        const int c1 = 52845;
        const int c2 = 22719;
        if (discard >= data.Length)
        {
            return [];
        }

        int r = key;
        for (int i = 0; i < discard; i++)
        {
            r = (int)((((long)data[i] + r) * c1 + c2) & 0xffff);
        }

        int count = data.Length - discard;
        var decrypted = new byte[count];
        for (int i = discard, j = 0; j < count; i++, j++)
        {
            byte value = data[i];
            decrypted[j] = (byte)(value ^ (byte)(r >> 8));
            r = (int)((((long)value + r) * c1 + c2) & 0xffff);
        }

        return decrypted;
    }

    /// <summary>A minimal PostScript tokenizer over a byte buffer, mirroring pdf.js's stream reading.</summary>
    private sealed class PsStream
    {
        private readonly byte[] data;
        private int pos;
        private int currentChar;

        public PsStream(byte[] data)
        {
            this.data = data;
            pos = 0;
            currentChar = data.Length > 0 ? data[0] : -1;
            if (data.Length > 0)
            {
                pos = 1;
            }
        }

        public int GetByte() => pos < data.Length ? data[pos++] : -1;

        public byte[] GetBytes(int n)
        {
            int len = System.Math.Min(n, data.Length - pos);
            if (len < 0)
            {
                len = 0;
            }

            var bytes = new byte[len];
            System.Array.Copy(data, pos, bytes, 0, len);
            pos += len;
            currentChar = pos < data.Length ? data[pos] : -1;
            return bytes;
        }

        public void PrevChar()
        {
            pos = System.Math.Max(0, pos - 2);
            currentChar = pos < data.Length ? data[pos] : -1;
        }

        private int NextChar()
        {
            currentChar = GetByte();
            return currentChar;
        }

        public string? GetToken()
        {
            bool comment = false;
            int ch = currentChar;
            while (true)
            {
                if (ch == -1)
                {
                    return null;
                }

                if (comment)
                {
                    if (ch == 0x0a || ch == 0x0d)
                    {
                        comment = false;
                    }
                }
                else if (ch == 0x25) // '%'
                {
                    comment = true;
                }
                else if (!IsWhiteSpace(ch))
                {
                    break;
                }

                ch = NextChar();
            }

            if (IsSpecial(ch))
            {
                NextChar();
                return ((char)ch).ToString();
            }

            var sb = new System.Text.StringBuilder();
            do
            {
                sb.Append((char)ch);
                ch = NextChar();
            }
            while (ch >= 0 && !IsWhiteSpace(ch) && !IsSpecial(ch));

            return sb.ToString();
        }

        public int ReadInt()
        {
            string? token = GetToken();
            return ParseInt(token);
        }

        public double ReadNumber()
        {
            string? token = GetToken();
            return double.TryParse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0;
        }

        public float[] ReadNumberArray()
        {
            GetToken(); // '[' or '{'
            var list = new List<float>();
            while (true)
            {
                string? token = GetToken();
                if (token is null || token == "]" || token == "}")
                {
                    break;
                }

                list.Add(float.TryParse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0f);
            }

            return list.ToArray();
        }

        private static int ParseInt(string? token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return 0;
            }

            // PostScript integers may be written in decimal; tolerate a leading radix form minimally.
            return int.TryParse(token, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int v) ? v : 0;
        }

        private static bool IsWhiteSpace(int c) =>
            c is 0x00 or 0x09 or 0x0a or 0x0c or 0x0d or 0x20;

        private static bool IsSpecial(int c) =>
            c is 0x2f /*/*/ or 0x5b /*[*/ or 0x5d /*]*/ or 0x7b /*{*/ or 0x7b /*}*/ or 0x28 /*(*/ or 0x29 /*)*/;
    }
}
