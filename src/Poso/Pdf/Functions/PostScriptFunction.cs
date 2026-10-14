using System.Collections.Generic;
using Poso.Pdf.Objects;
using Poso.Pdf.Parsing;

namespace Poso.Pdf.Functions;

/// <summary>
/// Type 4 (PostScript calculator) function: evaluates a compact PostScript program over the operand
/// stack. Supports the arithmetic, logical, relational, stack and control operators that appear in
/// real tint transforms and shadings. Unrecognised operands abort evaluation without throwing.
/// </summary>
internal sealed class PostScriptFunction : PdfFunction
{
    private enum CellKind
    {
        Number,
        Boolean,
        Operator,
        Procedure,
    }

    private readonly struct Cell
    {
        public CellKind Kind { get; init; }
        public double Number { get; init; }
        public bool Bool { get; init; }
        public string? Operator { get; init; }
        public List<Cell>? Items { get; init; }
    }

    private readonly List<Cell> _program;

    private PostScriptFunction(float[] domain, float[] range, List<Cell> program)
        : base(domain, range)
    {
        _program = program;
    }

    public static PdfFunction Create(PdfStream stream)
    {
        PdfDictionary dictionary = stream.Dictionary;
        float[] domain = ReadFloats(dictionary.GetArray("Domain"), Array.Empty<float>());
        float[] range = ReadFloats(dictionary.GetArray("Range"), Array.Empty<float>());
        List<Cell> program = Parse(stream.Decode());
        return new PostScriptFunction(domain, range, program);
    }

    public override void EvaluateCore(ReadOnlySpan<float> input, Span<float> output)
    {
        var stack = new List<object>(_program.Count + 8);
        foreach (float value in input)
        {
            stack.Add((double)value);
        }

        Execute(_program, stack);

        // The final stack entries are the results; output index 0 is the deepest of the top n.
        int n = output.Length;
        int baseIndex = stack.Count - n;
        for (int i = 0; i < n; i++)
        {
            int at = baseIndex + i;
            if (at < 0 || at >= stack.Count)
            {
                continue;
            }

            object value = stack[at];
            output[i] = value is double number ? (float)number : (value is bool flag ? (flag ? 1f : 0f) : 0f);
        }
    }

    private static void Execute(List<Cell> cells, List<object> stack)
    {
        foreach (Cell cell in cells)
        {
            switch (cell.Kind)
            {
                case CellKind.Number:
                    stack.Add(cell.Number);
                    break;
                case CellKind.Boolean:
                    stack.Add(cell.Bool);
                    break;
                case CellKind.Procedure:
                    stack.Add(cell.Items!); // a code array sits on the stack until if/ifelse
                    break;
                case CellKind.Operator:
                    if (!ApplyOperator(cell.Operator!, stack))
                    {
                        return; // unknown operator: stop; whatever is on the stack is the result
                    }

                    break;
            }
        }
    }

    private static bool ApplyOperator(string op, List<object> stack)
    {
        switch (op)
        {
            case "add": return Binary(stack, (a, b) => a + b);
            case "sub": return Binary(stack, (a, b) => a - b);
            case "mul": return Binary(stack, (a, b) => a * b);
            case "div": return Binary(stack, (a, b) => b == 0 ? 0 : a / b);
            case "idiv": return Binary(stack, (a, b) => b == 0 ? 0 : Math.Truncate(a / b));
            case "mod": return Binary(stack, (a, b) => b == 0 ? 0 : a - (b * Math.Truncate(a / b)));
            case "max": return Binary(stack, (a, b) => Math.Max(a, b));
            case "min": return Binary(stack, (a, b) => Math.Min(a, b));
            case "neg": return Unary(stack, v => -v);
            case "abs": return Unary(stack, Math.Abs);
            case "ceiling": return Unary(stack, Math.Ceiling);
            case "floor": return Unary(stack, Math.Floor);
            case "round": return Unary(stack, Math.Round);
            case "truncate": return Unary(stack, Math.Truncate);
            case "sqrt": return Unary(stack, v => Math.Sqrt(Math.Max(0, v)));
            case "exp": return Unary(stack, Math.Exp);
            case "ln": return Unary(stack, v => Math.Log(Math.Max(double.Epsilon, v)));
            case "log": return Unary(stack, v => Math.Log10(Math.Max(double.Epsilon, v)));
            case "sin": return Unary(stack, Math.Sin);
            case "cos": return Unary(stack, Math.Cos);
            case "atan": return Unary(stack, Math.Atan);
            case "cvi": return Unary(stack, Math.Truncate);
            case "cvr": return Unary(stack, v => v);
            case "pi": stack.Add(Math.PI); return true;

            case "atan2":
            {
                if (stack.Count < 2)
                {
                    return true;
                }

                double x = AsNumber(Pop(stack));
                double y = AsNumber(Pop(stack));
                stack.Add(Math.Atan2(y, x));
                return true;
            }

            case "eq": return CompareBool(stack, (a, b) => a == b);
            case "ne": return CompareBool(stack, (a, b) => a != b);
            case "lt": return CompareBool(stack, (a, b) => a < b);
            case "le": return CompareBool(stack, (a, b) => a <= b);
            case "gt": return CompareBool(stack, (a, b) => a > b);
            case "ge": return CompareBool(stack, (a, b) => a >= b);

            case "and": return LogicalBool(stack, (a, b) => a & b);
            case "or": return LogicalBool(stack, (a, b) => a | b);
            case "xor": return LogicalBool(stack, (a, b) => a ^ b);
            case "not":
            {
                if (stack.Count == 0)
                {
                    return true;
                }

                object value = Pop(stack);
                stack.Add(value is bool b ? !b : AsNumber(value) == 0);
                return true;
            }

            case "dup":
            {
                if (stack.Count == 0)
                {
                    return true;
                }

                stack.Add(stack[^1]);
                return true;
            }

            case "exch":
            {
                if (stack.Count < 2)
                {
                    return true;
                }

                (stack[^1], stack[^2]) = (stack[^2], stack[^1]);
                return true;
            }

            case "pop":
            {
                if (stack.Count > 0)
                {
                    Pop(stack);
                }

                return true;
            }

            case "index":
            {
                if (stack.Count < 1)
                {
                    return true;
                }

                int j = (int)AsNumber(Pop(stack));
                int at = stack.Count - 1 - j;
                if (at >= 0 && at < stack.Count)
                {
                    stack.Add(stack[at]);
                }

                return true;
            }

            case "count":
                stack.Add(stack.Count);
                return true;

            case "clear":
                stack.Clear();
                return true;

            case "if":
            {
                if (stack.Count < 2)
                {
                    return true;
                }

                List<Cell>? code = AsProcedure(Pop(stack));
                bool condition = AsBool(Pop(stack));
                if (condition && code is not null)
                {
                    Execute(code, stack);
                }

                return true;
            }

            case "ifelse":
            {
                if (stack.Count < 3)
                {
                    return true;
                }

                List<Cell>? falseCode = AsProcedure(Pop(stack));
                List<Cell>? trueCode = AsProcedure(Pop(stack));
                bool condition = AsBool(Pop(stack));
                Execute(condition ? trueCode ?? new List<Cell>() : falseCode ?? new List<Cell>(), stack);
                return true;
            }

            default:
                return false;
        }
    }

    private static bool Binary(List<object> stack, Func<double, double, double> operation)
    {
        if (stack.Count < 2)
        {
            return true;
        }

        double second = AsNumber(Pop(stack));
        double first = AsNumber(Pop(stack));
        stack.Add(operation(first, second));
        return true;
    }

    private static bool Unary(List<object> stack, Func<double, double> operation)
    {
        if (stack.Count < 1)
        {
            return true;
        }

        stack.Add(operation(AsNumber(Pop(stack))));
        return true;
    }

    private static bool CompareBool(List<object> stack, Func<double, double, bool> operation)
    {
        if (stack.Count < 2)
        {
            return true;
        }

        double second = AsNumber(Pop(stack));
        double first = AsNumber(Pop(stack));
        stack.Add(operation(first, second));
        return true;
    }

    private static bool LogicalBool(List<object> stack, Func<bool, bool, bool> operation)
    {
        if (stack.Count < 2)
        {
            return true;
        }

        bool second = AsBool(Pop(stack));
        bool first = AsBool(Pop(stack));
        stack.Add(operation(first, second));
        return true;
    }

    private static object Pop(List<object> stack)
    {
        object value = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return value;
    }

    private static double AsNumber(object value) => value switch
    {
        double number => number,
        bool flag => flag ? 1 : 0,
        _ => 0d,
    };

    private static bool AsBool(object value) => value switch
    {
        bool flag => flag,
        double number => number != 0,
        _ => false,
    };

    private static List<Cell>? AsProcedure(object value) => value as List<Cell>;

    private static List<Cell> Parse(byte[] program)
    {
        var lexer = new PdfLexer(program);
        return ReadCells(lexer, false);
    }

    /// <summary>Reads cells until the stream ends (top level) or a closing bracket (inside a procedure).</summary>
    private static List<Cell> ReadCells(PdfLexer lexer, bool insideArray)
    {
        var cells = new List<Cell>();
        while (!lexer.AtEnd)
        {
            PdfToken token = lexer.NextToken();
            switch (token.Kind)
            {
                case PdfTokenKind.Integer:
                    cells.Add(new Cell { Kind = CellKind.Number, Number = token.IntegerValue });
                    break;
                case PdfTokenKind.Real:
                    cells.Add(new Cell { Kind = CellKind.Number, Number = token.RealValue });
                    break;
                case PdfTokenKind.ArrayOpen:
                {
                    List<Cell> nested = ReadCells(lexer, true);
                    cells.Add(new Cell { Kind = CellKind.Procedure, Items = nested });
                    break;
                }

                case PdfTokenKind.ArrayClose:
                    if (insideArray)
                    {
                        return cells;
                    }

                    break;

                case PdfTokenKind.BraceOpen:
                {
                    // PostScript procedure { ... } used by if/ifelse.
                    List<Cell> procedure = ReadCells(lexer, true);
                    cells.Add(new Cell { Kind = CellKind.Procedure, Items = procedure });
                    break;
                }

                case PdfTokenKind.BraceClose:
                    if (insideArray)
                    {
                        return cells;
                    }

                    break;

                case PdfTokenKind.Keyword:
                    if (lexer.Match(token, "true"u8))
                    {
                        cells.Add(new Cell { Kind = CellKind.Boolean, Bool = true });
                    }
                    else if (lexer.Match(token, "false"u8))
                    {
                        cells.Add(new Cell { Kind = CellKind.Boolean, Bool = false });
                    }
                    else
                    {
                        cells.Add(new Cell { Kind = CellKind.Operator, Operator = lexer.GetText(token) });
                    }

                    break;

                case PdfTokenKind.DictOpen:
                    SkipDictionary(lexer);
                    break;
            }
        }

        return cells;
    }

    private static void SkipDictionary(PdfLexer lexer)
    {
        int depth = 1;
        while (!lexer.AtEnd && depth > 0)
        {
            PdfToken token = lexer.NextToken();
            if (token.Kind == PdfTokenKind.DictOpen)
            {
                depth++;
            }
            else if (token.Kind == PdfTokenKind.DictClose)
            {
                depth--;
            }
        }
    }
}
