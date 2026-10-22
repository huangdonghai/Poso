using Poso.Graphics;

namespace Poso.Pdf.Fonts;

/// <summary>
/// Type 2 charstring interpreter (Adobe Technical Note 5177): turns a CFF glyph program into path
/// segments in em units, so a glyph can be painted with an ordinary fill instead of being handed to a
/// back end that may not be able to load the font.
/// </summary>
/// <remarks>
/// Hints are counted but never applied. They only matter here so that the mask bytes after
/// <c>hintmask</c>/<c>cntrmask</c> are stepped over rather than read as operands; an unhinted outline is
/// what the back end would have rasterised at page scale anyway.
/// </remarks>
internal static class Type2Charstring
{
    private const int MaxNesting = 10;

    /// <summary>
    /// Runs <paramref name="charString"/> and appends its contours to <paramref name="path"/>, applying
    /// <paramref name="fontMatrix"/> to move from the program's design space into em units.
    /// Returns false on a program this interpreter cannot follow.
    /// </summary>
    public static bool TryRun(
        byte[] charString,
        byte[] program,
        CffIndex globalSubrs,
        CffIndex localSubrs,
        ReadOnlySpan<float> fontMatrix,
        IPathBuilder path)
    {
        var interpreter = new Interpreter(program, globalSubrs, localSubrs, fontMatrix, path);
        return interpreter.Run(charString, 0);
    }

    /// <summary>The subroutine bias a count of subroutines implies (TN 5177 4.2).</summary>
    public static int BiasFor(int subrCount) => subrCount switch
    {
        < 1240 => 107,
        < 33900 => 1131,
        _ => 32768,
    };

    private sealed class Interpreter
    {
        private readonly byte[] _program;
        private readonly CffIndex _globalSubrs;
        private readonly CffIndex _localSubrs;
        private readonly IPathBuilder _path;

        // FontMatrix as [a b c d e f]: x' = a*x + c*y + e, y' = b*x + d*y + f.
        private readonly float _a, _b, _c, _d, _e, _f;

        private readonly List<float> _stack = new(48);
        private readonly float[] _transient = new float[32];

        private float _x, _y;
        private bool _open;
        private bool _widthSeen;
        private int _hints;

        public Interpreter(byte[] program, CffIndex globalSubrs, CffIndex localSubrs, ReadOnlySpan<float> fontMatrix, IPathBuilder path)
        {
            _program = program;
            _globalSubrs = globalSubrs;
            _localSubrs = localSubrs;
            _path = path;
            _a = fontMatrix.Length > 0 ? fontMatrix[0] : 0.001f;
            _b = fontMatrix.Length > 1 ? fontMatrix[1] : 0f;
            _c = fontMatrix.Length > 2 ? fontMatrix[2] : 0f;
            _d = fontMatrix.Length > 3 ? fontMatrix[3] : 0.001f;
            _e = fontMatrix.Length > 4 ? fontMatrix[4] : 0f;
            _f = fontMatrix.Length > 5 ? fontMatrix[5] : 0f;
        }

        public bool Run(byte[] code, int depth)
        {
            if (depth > MaxNesting)
            {
                return false;
            }

            int i = 0;
            while (i < code.Length)
            {
                byte b = code[i++];
                // 28 (short integer) and 255 (16.16 fixed) begin operands but are below the operator
                // range; 29 and 30 are the operators callgsubr/vhcurveto in charstrings, unlike DICTs.
                if (b >= 32 || b == 28 || b == 255)
                {
                    // Read first, then keep: the operand's bytes have to be consumed either way, or a
                    // full stack would leave the cursor standing on them.
                    float operand = ReadOperand(code, ref i, b);
                    if (_stack.Count < 48)
                    {
                        _stack.Add(operand);
                    }

                    continue;
                }

                switch (b)
                {
                    case 1:  // hstem
                    case 3:  // vstem
                    case 18: // hstemhm
                    case 23: // vstemhm
                        Stem();
                        break;

                    case 19: // hintmask
                    case 20: // cntrmask
                        if (!Mask(code, ref i))
                        {
                            return false;
                        }

                        break;

                    case 21:
                        if (!MoveTo(2, horizontal: false, vertical: false))
                        {
                            return false;
                        }

                        break;

                    case 22:
                        if (!MoveTo(1, horizontal: true, vertical: false))
                        {
                            return false;
                        }

                        break;

                    case 4:
                        if (!MoveTo(1, horizontal: false, vertical: true))
                        {
                            return false;
                        }

                        break;

                    case 5:
                        LineTo();
                        break;

                    case 6:
                    case 7:
                        AlternatingLineTo(horizontal: b == 6);
                        break;

                    case 8:
                        CurveTo();
                        break;

                    case 24:
                        CurveThenLine();
                        break;

                    case 25:
                        LineThenCurve();
                        break;

                    case 26:
                        VerticalCurves();
                        break;

                    case 27:
                        HorizontalCurves();
                        break;

                    case 30:
                        AlternatingCurves(horizontalFirst: false);
                        break;

                    case 31:
                        AlternatingCurves(horizontalFirst: true);
                        break;

                    case 10:
                        if (!CallSubroutine(depth, global: false))
                        {
                            return false;
                        }

                        break;

                    case 29:
                        if (!CallSubroutine(depth, global: true))
                        {
                            return false;
                        }

                        break;

                    case 11: // return
                        return true;

                    case 14: // endchar; the seac form is expanded by the caller
                        _stack.Clear();
                        return true;

                    case 12:
                        if (!Escape(code, ref i))
                        {
                            return false;
                        }

                        break;

                    default: // reserved operator: drop its operands rather than read them as a path
                        _stack.Clear();
                        break;
                }
            }

            return true;
        }

        private static float ReadOperand(byte[] code, ref int i, byte b)
        {
            if (b == 28)
            {
                short value = i + 1 < code.Length
                    ? (short)((code[i] << 8) | code[i + 1])
                    : (short)0;
                i += 2;
                return value;
            }

            if (b == 255)
            {
                // 16.16 fixed
                if (i + 4 > code.Length)
                {
                    i = code.Length;
                    return 0f;
                }

                int bits = (code[i] << 24) | (code[i + 1] << 16) | (code[i + 2] << 8) | code[i + 3];
                i += 4;
                return bits / 65536f;
            }

            if (b <= 246)
            {
                return b - 139;
            }

            if (b <= 250)
            {
                return i < code.Length ? ((b - 247) * 256) + code[i++] + 108 : 0f;
            }

            // 251-254
            return i < code.Length ? -((b - 251) * 256) - code[i++] - 108 : 0f;
        }

        // ---- width and hints ----

        /// <summary>
        /// Consumes the optional advance width: the first stack-clearing operator carries it as one
        /// operand more than the operator itself takes (TN 5177 3.1).
        /// </summary>
        private void DropWidthIfPresent(int expected)
        {
            if (_widthSeen)
            {
                return;
            }

            _widthSeen = true;
            if (_stack.Count > expected)
            {
                _stack.RemoveAt(0);
            }
        }

        private void Stem()
        {
            if (!_widthSeen && (_stack.Count & 1) == 1)
            {
                _stack.RemoveAt(0);
            }

            _widthSeen = true;
            _hints += _stack.Count / 2;
            _stack.Clear();
        }

        private bool Mask(byte[] code, ref int i)
        {
            // A hintmask can be preceded by the arguments of an implicit vstemhm.
            if (_stack.Count > 0)
            {
                Stem();
            }

            _widthSeen = true;
            i += (_hints + 7) / 8;
            return i <= code.Length;
        }

        // ---- path operators ----

        /// <summary>rmoveto (both axes), hmoveto (x only) and vmoveto (y only).</summary>
        private bool MoveTo(int operands, bool horizontal, bool vertical)
        {
            DropWidthIfPresent(operands);
            if (_stack.Count < operands)
            {
                _stack.Clear();
                return true;
            }

            float dx = horizontal ? _stack[^1] : 0f;
            float dy = vertical ? _stack[^1] : 0f;
            if (!horizontal && !vertical)
            {
                dx = _stack[^2];
                dy = _stack[^1];
            }

            Move(_x + dx, _y + dy);
            _stack.Clear();
            return true;
        }

        private void LineTo()
        {
            for (int i = 0; i + 1 < _stack.Count; i += 2)
            {
                Line(_x + _stack[i], _y + _stack[i + 1]);
            }

            _stack.Clear();
        }

        private void AlternatingLineTo(bool horizontal)
        {
            for (int i = 0; i < _stack.Count; i++)
            {
                if (horizontal)
                {
                    Line(_x + _stack[i], _y);
                }
                else
                {
                    Line(_x, _y + _stack[i]);
                }

                horizontal = !horizontal;
            }

            _stack.Clear();
        }

        private void CurveTo()
        {
            for (int i = 0; i + 5 < _stack.Count; i += 6)
            {
                RelativeCurve(_stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], _stack[i + 4], _stack[i + 5]);
            }

            _stack.Clear();
        }

        /// <summary>rcurveline: curves while eight or more operands remain, then a final line.</summary>
        private void CurveThenLine()
        {
            int i = 0;
            while (_stack.Count - i >= 8)
            {
                RelativeCurve(_stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], _stack[i + 4], _stack[i + 5]);
                i += 6;
            }

            if (_stack.Count - i >= 2)
            {
                Line(_x + _stack[i], _y + _stack[i + 1]);
            }

            _stack.Clear();
        }

        /// <summary>rlinecurve: lines while eight or more operands remain, then a final curve.</summary>
        private void LineThenCurve()
        {
            int i = 0;
            while (_stack.Count - i >= 8)
            {
                Line(_x + _stack[i], _y + _stack[i + 1]);
                i += 2;
            }

            if (_stack.Count - i >= 6)
            {
                RelativeCurve(_stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], _stack[i + 4], _stack[i + 5]);
            }

            _stack.Clear();
        }

        /// <summary>vvcurveto: an optional leading dx1, then (dy1 dx2 dy2 dy3) groups.</summary>
        private void VerticalCurves()
        {
            int i = 0;
            float dx1 = 0f;
            if ((_stack.Count & 1) == 1)
            {
                dx1 = _stack[0];
                i = 1;
            }

            while (_stack.Count - i >= 4)
            {
                float dy1 = _stack[i];
                float dx2 = _stack[i + 1];
                float dy2 = _stack[i + 2];
                float dy3 = _stack[i + 3];
                Curve(
                    _x + dx1, _y + dy1,
                    _x + dx1 + dx2, _y + dy1 + dy2,
                    _x + dx1 + dx2, _y + dy1 + dy2 + dy3);
                i += 4;
                dx1 = 0f;
            }

            _stack.Clear();
        }

        /// <summary>hhcurveto: an optional leading dy1, then (dx1 dx2 dy2 dx3) groups.</summary>
        private void HorizontalCurves()
        {
            int i = 0;
            float dy1 = 0f;
            if ((_stack.Count & 1) == 1)
            {
                dy1 = _stack[0];
                i = 1;
            }

            while (_stack.Count - i >= 4)
            {
                float dx1 = _stack[i];
                float dx2 = _stack[i + 1];
                float dy2 = _stack[i + 2];
                float dx3 = _stack[i + 3];
                Curve(
                    _x + dx1, _y + dy1,
                    _x + dx1 + dx2, _y + dy1 + dy2,
                    _x + dx1 + dx2 + dx3, _y + dy1 + dy2);
                i += 4;
                dy1 = 0f;
            }

            _stack.Clear();
        }

        /// <summary>
        /// hvcurveto (horizontal first) and vhcurveto (vertical first): groups of four operands that
        /// alternate the axis the curve leaves on. The axis the curve does <em>not</em> leave on carries
        /// the third operand, and a fifth operand on the last group gives that same axis its remaining
        /// coordinate: for hvcurveto the deltas are (dx1 0) (dx2 dy2) (0 dy3) with an optional trailing
        /// dx4, and vhcurveto mirrors them onto the other axis.
        /// </summary>
        private void AlternatingCurves(bool horizontalFirst)
        {
            int i = 0;
            bool horizontal = horizontalFirst;
            while (_stack.Count - i >= 4)
            {
                bool extra = _stack.Count - i == 5;
                float dx1, dy1, dx2, dy2, dx3, dy3;
                if (horizontal)
                {
                    dx1 = _stack[i];
                    dy1 = 0f;
                    dx2 = _stack[i + 1];
                    dy2 = _stack[i + 2];
                    dx3 = extra ? _stack[i + 4] : 0f;
                    dy3 = _stack[i + 3];
                }
                else
                {
                    dx1 = 0f;
                    dy1 = _stack[i];
                    dx2 = _stack[i + 1];
                    dy2 = _stack[i + 2];
                    dx3 = _stack[i + 3];
                    dy3 = extra ? _stack[i + 4] : 0f;
                }

                RelativeCurve(dx1, dy1, dx2, dy2, dx3, dy3);
                i += extra ? 5 : 4;
                horizontal = !horizontal;
            }

            _stack.Clear();
        }

        private bool CallSubroutine(int depth, bool global)
        {
            if (_stack.Count == 0)
            {
                return false;
            }

            CffIndex index = global ? _globalSubrs : _localSubrs;
            int bias = BiasFor(index.Count);
            int operand = (int)MathF.Round(_stack[^1]);
            _stack.RemoveAt(_stack.Count - 1);

            int subr = operand + bias;
            if (subr < 0 || subr >= index.Count)
            {
                return false;
            }

            return Run(index.ReadItem(_program, subr), depth + 1);
        }

        private bool Escape(byte[] code, ref int i)
        {
            if (i >= code.Length)
            {
                return false;
            }

            byte op = code[i++];
            switch (op)
            {
                case 3: // and
                    Push(Pop() != 0f && Pop() != 0f ? 1f : 0f);
                    break;

                case 4: // or
                    Push(Pop() != 0f || Pop() != 0f ? 1f : 0f);
                    break;

                case 5: // not
                    Push(Pop() == 0f ? 1f : 0f);
                    break;

                case 9: // abs
                    Push(MathF.Abs(Pop()));
                    break;

                case 10: // add
                {
                    float b = Pop();
                    Push(Pop() + b);
                    break;
                }

                case 11: // sub
                {
                    float b = Pop();
                    Push(Pop() - b);
                    break;
                }

                case 12: // div
                {
                    float b = Pop();
                    float a = Pop();
                    Push(b == 0f ? 0f : a / b);
                    break;
                }

                case 14: // neg
                    Push(-Pop());
                    break;

                case 15: // eq
                {
                    float b = Pop();
                    Push(Pop() == b ? 1f : 0f);
                    break;
                }

                case 18: // drop
                    Pop();
                    break;

                case 20: // put
                {
                    int index = (int)MathF.Round(Pop());
                    float value = Pop();
                    if (index >= 0 && index < _transient.Length)
                    {
                        _transient[index] = value;
                    }

                    break;
                }

                case 21: // get
                {
                    int index = (int)MathF.Round(Pop());
                    Push(index >= 0 && index < _transient.Length ? _transient[index] : 0f);
                    break;
                }

                case 22: // ifelse
                {
                    float v2 = Pop();
                    float v1 = Pop();
                    float s2 = Pop();
                    float s1 = Pop();
                    Push(v1 <= v2 ? s1 : s2);
                    break;
                }

                case 23: // random; a fixed value keeps a page reproducible across renderers
                    Push(0.5f);
                    break;

                case 24: // mul
                {
                    float b = Pop();
                    Push(Pop() * b);
                    break;
                }

                case 26: // sqrt
                {
                    float value = Pop();
                    Push(value > 0f ? MathF.Sqrt(value) : 0f);
                    break;
                }

                case 27: // dup
                {
                    float value = Pop();
                    Push(value);
                    Push(value);
                    break;
                }

                case 28: // exch
                {
                    float b = Pop();
                    float a = Pop();
                    Push(b);
                    Push(a);
                    break;
                }

                case 29: // index
                {
                    int index = (int)MathF.Round(Pop());
                    if (index < 0 || index >= _stack.Count)
                    {
                        Push(0f);
                    }
                    else
                    {
                        Push(_stack[^(index + 1)]);
                    }

                    break;
                }

                case 30: // roll
                {
                    int j = (int)MathF.Round(Pop());
                    int n = (int)MathF.Round(Pop());
                    Roll(n, j);
                    break;
                }

                case 34: // hflex
                    HFlex();
                    break;

                case 35: // flex
                    Flex();
                    break;

                case 36: // hflex1
                    HFlex1();
                    break;

                case 37: // flex1
                    Flex1();
                    break;

                default: // reserved escape operator: drop its operands
                    _stack.Clear();
                    break;
            }

            return true;
        }

        private void Roll(int n, int j)
        {
            if (n <= 0 || n > _stack.Count)
            {
                return;
            }

            j %= n;
            if (j < 0)
            {
                j += n;
            }

            if (j == 0)
            {
                return;
            }

            int start = _stack.Count - n;
            float[] slice = _stack.GetRange(start, n).ToArray();
            for (int k = 0; k < n; k++)
            {
                _stack[start + k] = slice[(k + n - j) % n];
            }
        }

        /// <summary>hflex: (dx1 0) (dx2 dy2) (dx3 0) then (dx4 0) (dx5 -dy2) (dx6 0).</summary>
        private void HFlex()
        {
            if (_stack.Count < 7)
            {
                _stack.Clear();
                return;
            }

            float dx1 = _stack[0], dx2 = _stack[1], dy2 = _stack[2], dx3 = _stack[3];
            float dx4 = _stack[4], dx5 = _stack[5], dx6 = _stack[6];
            RelativeCurve(dx1, 0f, dx2, dy2, dx3, 0f);
            RelativeCurve(dx4, 0f, dx5, -dy2, dx6, 0f);
            _stack.Clear();
        }

        /// <summary>flex: two explicit curves and a flex depth that an unhinted outline can ignore.</summary>
        private void Flex()
        {
            if (_stack.Count < 13)
            {
                _stack.Clear();
                return;
            }

            RelativeCurve(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], _stack[5]);
            RelativeCurve(_stack[6], _stack[7], _stack[8], _stack[9], _stack[10], _stack[11]);
            _stack.Clear();
        }

        /// <summary>hflex1: (dx1 dy1) (dx2 dy2) (dx3 0) then (dx4 0) (dx5 dy5) (dx6 dy6).</summary>
        private void HFlex1()
        {
            if (_stack.Count < 9)
            {
                _stack.Clear();
                return;
            }

            float dy1 = _stack[1];
            float dy2 = _stack[3];
            float dy5 = _stack[7];
            RelativeCurve(_stack[0], dy1, _stack[2], dy2, _stack[4], 0f);
            RelativeCurve(_stack[5], 0f, _stack[6], dy5, _stack[8], -(dy1 + dy2 + dy5));
            _stack.Clear();
        }

        /// <summary>flex1: the last curve closes the outline back along the axis that moved less.</summary>
        private void Flex1()
        {
            if (_stack.Count < 11)
            {
                _stack.Clear();
                return;
            }

            float dx = _stack[0] + _stack[2] + _stack[4] + _stack[6] + _stack[8];
            float dy = _stack[1] + _stack[3] + _stack[5] + _stack[7] + _stack[9];
            RelativeCurve(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], _stack[5]);

            float dx6 = MathF.Abs(dx) > MathF.Abs(dy) ? _stack[10] : -dx;
            float dy6 = MathF.Abs(dx) > MathF.Abs(dy) ? -dy : _stack[10];
            RelativeCurve(_stack[6], _stack[7], _stack[8], _stack[9], dx6, dy6);
            _stack.Clear();
        }

        // ---- stack ----

        private void Push(float value)
        {
            if (_stack.Count < 48)
            {
                _stack.Add(value);
            }
        }

        private float Pop() => _stack.Count > 0 ? Take() : 0f;

        private float Take()
        {
            float value = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            return value;
        }

        // ---- emitting ----

        private void RelativeCurve(float dx1, float dy1, float dx2, float dy2, float dx3, float dy3)
        {
            float x1 = _x + dx1;
            float y1 = _y + dy1;
            float x2 = x1 + dx2;
            float y2 = y1 + dy2;
            Curve(x1, y1, x2, y2, x2 + dx3, y2 + dy3);
        }

        private void Move(float x, float y)
        {
            _x = x;
            _y = y;
            _open = true;
            _path.MoveTo(TransformX(x, y), TransformY(x, y));
        }

        private void Line(float x, float y)
        {
            if (!_open)
            {
                Move(_x, _y);
            }

            _x = x;
            _y = y;
            _path.LineTo(TransformX(x, y), TransformY(x, y));
        }

        private void Curve(float x1, float y1, float x2, float y2, float x3, float y3)
        {
            if (!_open)
            {
                Move(_x, _y);
            }

            _path.CubicTo(
                TransformX(x1, y1), TransformY(x1, y1),
                TransformX(x2, y2), TransformY(x2, y2),
                TransformX(x3, y3), TransformY(x3, y3));
            _x = x3;
            _y = y3;
        }

        private float TransformX(float x, float y) => (_a * x) + (_c * y) + _e;

        private float TransformY(float x, float y) => (_b * x) + (_d * y) + _f;
    }
}
