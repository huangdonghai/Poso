using System;

namespace Poso.Pdf.Colors;

/// <summary>
/// An ICC tone reproduction curve (curveType 'curv' or parametricCurveType 'para'): maps a device value
/// in [0,1] to a linear response in [0,1]. Supports linear, gamma, sampled-table and the five ICC
/// parametric function types (parameters stored in ICC order g, a, b, c, d, e, f).
/// </summary>
internal sealed class Curve
{
    private enum Kind { Linear, Gamma, Table, Parametric }

    private readonly Kind _kind;
    private readonly float _gamma;
    private readonly ushort[]? _table;
    private readonly int _functionType;
    private readonly float[] _p; // g,a,b,c,d,e,f

    private Curve(Kind kind, float gamma, ushort[]? table, int functionType, float[] p)
    {
        _kind = kind;
        _gamma = gamma;
        _table = table;
        _functionType = functionType;
        _p = p;
    }

    public static Curve Linear() => new(Kind.Linear, 0, null, 0, Array.Empty<float>());

    public static Curve Gamma(float gamma) => new(Kind.Gamma, gamma, null, 0, Array.Empty<float>());

    public static Curve Table(ushort[] table) => new(Kind.Table, 0, table, 0, Array.Empty<float>());

    public static Curve Parametric(int functionType, float[] p) => new(Kind.Parametric, 0, null, functionType, p);

    public float Evaluate(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        switch (_kind)
        {
            case Kind.Linear:
                return x;
            case Kind.Gamma:
                return MathF.Pow(x, _gamma);
            case Kind.Table:
                return EvaluateTable(x);
            default:
                return EvaluateParametric(x);
        }
    }

    private float EvaluateTable(float x)
    {
        ushort[] t = _table!;
        if (t.Length == 1)
        {
            return t[0] / 65535f;
        }

        float scaled = x * (t.Length - 1);
        int i = (int)scaled;
        if (i >= t.Length - 1)
        {
            return t[^1] / 65535f;
        }

        float frac = scaled - i;
        float a = t[i] / 65535f;
        float b = t[i + 1] / 65535f;
        return a + (b - a) * frac;
    }

    private float EvaluateParametric(float x)
    {
        // Parameters in ICC order: [0]=g, [1]=a, [2]=b, [3]=c, [4]=d, [5]=e, [6]=f.
        float g = _p.Length > 0 ? _p[0] : 1f;
        float a = _p.Length > 1 ? _p[1] : 1f;
        float b = _p.Length > 2 ? _p[2] : 0f;
        float c = _p.Length > 3 ? _p[3] : 0f;
        float d = _p.Length > 4 ? _p[4] : 0f;
        float e = _p.Length > 5 ? _p[5] : 0f;
        float f = _p.Length > 6 ? _p[6] : 0f;

        static float Pow(float baseValue, float exponent) => baseValue <= 0f ? 0f : MathF.Pow(baseValue, exponent);

        switch (_functionType)
        {
            case 0:
                return MathF.Pow(x, g);
            case 1:
                return x >= -b / a ? Pow(a * x + b, g) : 0f;
            case 2:
                return x >= -b / a ? Pow(a * x + b, g) + e : e;
            case 3:
                return x >= -b / a ? Pow(a * x + b, g) : d * x;
            case 4:
                return x >= -b / a ? Pow(a * x + b, g) + e : f * x + e;
            default:
                return x;
        }
    }
}
