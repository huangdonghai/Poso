using System.Collections.Generic;
using Poso.Pdf.Objects;
using Poso.Pdf.Parsing;

namespace Poso.Pdf.Functions;

/// <summary>
/// PDF function object (ISO 32000-1 7.12): maps an input vector onto an output vector. Used by
/// Separation/DeviceN tint transforms (and, later, by shadings). Types 0 (sampled), 2 (exponential),
/// 3 (stitching) and 4 (PostScript calculator) are supported.
/// </summary>
public abstract class PdfFunction
{
    protected readonly float[] Domain;
    protected readonly float[] Range;

    protected PdfFunction(float[] domain, float[] range)
    {
        Domain = domain;
        Range = range;
    }

    public int InputCount => Domain.Length / 2;
    public int OutputCount => Range.Length / 2;

    public abstract void EvaluateCore(ReadOnlySpan<float> input, Span<float> output);

    /// <summary>Clamps inputs to the domain, evaluates, and clamps outputs to the range.</summary>
    public void Evaluate(ReadOnlySpan<float> input, Span<float> output)
    {
        // The scratch buffers are sized by /Domain and /Range read from the file, so only small ones
        // may go on the stack; a corrupt function dictionary declaring a million outputs would overflow it.
        Span<float> clamped = input.Length <= 64 ? stackalloc float[input.Length] : new float[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            clamped[i] = i * 2 + 1 < Domain.Length
                ? Math.Clamp(input[i], Domain[i * 2], Domain[(i * 2) + 1])
                : input[i];
        }

        Span<float> raw = output.Length <= 64 ? stackalloc float[output.Length] : new float[output.Length];
        EvaluateCore(clamped, raw);

        for (int i = 0; i < output.Length; i++)
        {
            output[i] = i * 2 + 1 < Range.Length
                ? Math.Clamp(raw[i], Range[i * 2], Range[(i * 2) + 1])
                : raw[i];
        }
    }

    public float[] Evaluate(ReadOnlySpan<float> input)
    {
        var output = new float[OutputCount];
        Evaluate(input, output);
        return output;
    }

    public static PdfFunction? Create(PdfDocument document, PdfObject? functionObject)
    {
        PdfObject? resolved = document.Resolve(functionObject);

        switch (resolved)
        {
            case PdfStream stream when stream.Dictionary.GetIntegerValue("FunctionType") == 0:
                return SampledFunction.Create(stream);
            case PdfStream stream4 when stream4.Dictionary.GetIntegerValue("FunctionType") == 4:
                return PostScriptFunction.Create(stream4);
            case PdfDictionary dictionary:
                return dictionary.GetIntegerValue("FunctionType") switch
                {
                    2 => ExponentialFunction.Create(dictionary),
                    3 => StitchingFunction.Create(document, dictionary),
                    _ => null,
                };
            default:
                return null;
        }
    }

    protected static float[] ReadFloats(PdfArray? array, float[] fallback)
    {
        if (array is null || array.Count == 0)
        {
            return fallback;
        }

        var values = new float[array.Count];
        for (int i = 0; i < array.Count; i++)
        {
            values[i] = array.GetNumber(i)?.AsFloat() ?? 0f;
        }

        return values;
    }

    /// <summary>A [0,1] range pair per output component, used when the function omits /Range.</summary>
    protected static float[] UnitRange(int outputs)
    {
        var range = new float[outputs * 2];
        for (int i = 0; i < outputs; i++)
        {
            range[i * 2] = 0f;
            range[(i * 2) + 1] = 1f;
        }

        return range;
    }
}

/// <summary>Type 2: exponential interpolation <c>output = C0 + x^E * (C1 - C0)</c> per component.</summary>
internal sealed class ExponentialFunction : PdfFunction
{
    private readonly float[] _c0;
    private readonly float[] _c1;
    private readonly float _exponent;
    private readonly int _inputCount;

    private ExponentialFunction(float[] domain, float[] range, float[] c0, float[] c1, float exponent, int inputCount)
        : base(domain, range)
    {
        _c0 = c0;
        _c1 = c1;
        _exponent = exponent;
        _inputCount = inputCount;
    }

    public static PdfFunction? Create(PdfDictionary dictionary)
    {
        float[] c0 = ReadFloats(dictionary.GetArray("C0"), new[] { 0f });
        float[] c1 = ReadFloats(dictionary.GetArray("C1"), new[] { 1f });
        if (c0.Length != c1.Length)
        {
            return null;
        }

        float exponent = (float)(dictionary.GetNumberValue("E") ?? 1d);
        int inputCount = (int)(dictionary.GetIntegerValue("N") ?? 1) == 0 ? 0 : 1;
        float[] domain = ReadFloats(dictionary.GetArray("Domain"), inputCount == 0 ? Array.Empty<float>() : new float[] { 0f, 1f });
        float[] range = ReadFloats(dictionary.GetArray("Range"), UnitRange(c0.Length));

        return new ExponentialFunction(domain, range, c0, c1, exponent, inputCount);
    }

    public override void EvaluateCore(ReadOnlySpan<float> input, Span<float> output)
    {
        float x = _inputCount == 0 ? 0f : (input.Length > 0 ? input[0] : 0f);
        float factor = _exponent == 1f ? x : MathF.Pow(Math.Max(0f, x), _exponent);

        for (int i = 0; i < output.Length && i < _c0.Length; i++)
        {
            output[i] = _c0[i] + (factor * (_c1[i] - _c0[i]));
        }
    }
}

/// <summary>Type 3: stitching - the 1-D domain is split into ranges, each served by a sub-function.</summary>
internal sealed class StitchingFunction : PdfFunction
{
    private readonly PdfFunction[] _functions;
    private readonly float[] _encode;
    private readonly float[] _bounds;

    private StitchingFunction(float[] domain, float[] range, PdfFunction[] functions, float[] encode, float[] bounds)
        : base(domain, range)
    {
        _functions = functions;
        _encode = encode;
        _bounds = bounds;
    }

    public static PdfFunction? Create(PdfDocument document, PdfDictionary dictionary)
    {
        // The auxiliary arrays are read through the document so that an indirect reference
        // (/Functions 27 0 R rather than an inlined array) still resolves - real files such as
        // pdf.js issue7821.pdf store every stitching operand as an indirect object.
        PdfArray? functionArray = document.Resolve(dictionary.Get("Functions")) as PdfArray;
        if (functionArray is null || functionArray.Count == 0)
        {
            return null;
        }

        var functions = new List<PdfFunction>();
        foreach (PdfObject item in functionArray)
        {
            PdfFunction? function = Create(document, item);
            if (function is not null)
            {
                functions.Add(function);
            }
        }

        if (functions.Count == 0)
        {
            return null;
        }

        float[] domain = ReadFloats(document.Resolve(dictionary.Get("Domain")) as PdfArray, new float[] { 0f, 1f });
        // /Range is nominally required for a stitching function, but real files omit it and rely on the
        // sub-functions to fix the output width (MuPDF does the same). Defaulting to a single [0,1] range
        // would collapse a three-component RGB stitching function down to one output, painting only red.
        float[] range = ReadFloats(document.Resolve(dictionary.Get("Range")) as PdfArray, UnitRange(functions[0].OutputCount));
        float[] encode = ReadFloats(document.Resolve(dictionary.Get("Encode")) as PdfArray, DefaultEncode(functions.Count));
        float[] bounds = ReadFloats(document.Resolve(dictionary.Get("Bounds")) as PdfArray, Array.Empty<float>());

        return new StitchingFunction(domain, range, functions.ToArray(), encode, bounds);
    }

    private static float[] DefaultEncode(int count)
    {
        var encode = new float[count * 2];
        for (int i = 0; i < count; i++)
        {
            encode[i * 2] = 0f;
            encode[(i * 2) + 1] = 1f;
        }

        return encode;
    }

    public override void EvaluateCore(ReadOnlySpan<float> input, Span<float> output)
    {
        float x = input.Length > 0 ? input[0] : 0f;
        int k = _functions.Length;
        float dMin = Domain.Length >= 2 ? Domain[0] : 0f;
        float dMax = Domain.Length >= 2 ? Domain[1] : 1f;
        float span = dMax - dMin;
        int piece;
        float local;
        if (_bounds.Length == k - 1)
        {
            // /Bounds gives the domain breakpoints between sub-functions (PDF 32000-1 7.12.2.3);
            // pick the piece whose interval contains x and normalise within it.
            piece = 0;
            while (piece < k - 1 && x > _bounds[piece])
            {
                piece++;
            }

            float lower = piece == 0 ? dMin : _bounds[piece - 1];
            float upper = piece == k - 1 ? dMax : _bounds[piece];
            local = upper > lower ? (x - lower) / (upper - lower) : 0f;
        }
        else
        {
            float normalized = span > 0 ? (x - dMin) / span : 0f;
            piece = Math.Clamp((int)(normalized * k), 0, k - 1);
            float pieceStart = dMin + (span * piece / k);
            float pieceEnd = dMin + (span * (piece + 1) / k);
            local = pieceEnd > pieceStart ? (x - pieceStart) / (pieceEnd - pieceStart) : 0f;
        }

        int encBase = piece * 2;
        float eMin = encBase < _encode.Length ? _encode[encBase] : 0f;
        float eMax = encBase + 1 < _encode.Length ? _encode[encBase + 1] : 1f;
        float argument = eMin + (local * (eMax - eMin));

        PdfFunction function = _functions[piece];
        Span<float> sub = stackalloc float[function.OutputCount];
        function.Evaluate(stackalloc float[] { argument }, sub);

        for (int i = 0; i < output.Length && i < sub.Length; i++)
        {
            output[i] = sub[i];
        }
    }
}

/// <summary>Type 0: sampled values with nearest (order 0) or linear (order 1+) interpolation.</summary>
internal sealed class SampledFunction : PdfFunction
{
    private readonly byte[] _samples;
    private readonly int[] _size;
    private readonly int _bitsPerSample;
    private readonly int _order;
    private readonly float[] _encode;
    private readonly float[] _decode;

    private SampledFunction(PdfStream stream, float[] domain, float[] range, int[] size, int bitsPerSample, int order, float[] encode, float[] decode)
        : base(domain, range)
    {
        _samples = stream.Decode();
        _size = size;
        _bitsPerSample = bitsPerSample;
        _order = order;
        _encode = encode;
        _decode = decode;
    }

    public static PdfFunction? Create(PdfStream stream)
    {
        PdfDictionary dictionary = stream.Dictionary;
        int inputCount = dictionary.GetArray("Domain") is { } dm && dm.Count >= 2 ? dm.Count / 2 : 1;
        int outputCount = dictionary.GetArray("Range") is { } rg && rg.Count >= 2 ? rg.Count / 2 : 1;

        PdfArray? sizeArray = dictionary.GetArray("Size");
        if (sizeArray is null || sizeArray.Count < inputCount)
        {
            return null;
        }

        var size = new int[inputCount];
        for (int i = 0; i < inputCount; i++)
        {
            size[i] = Math.Max(1, sizeArray.GetNumber(i)?.AsInt() ?? 1);
        }

        int bitsPerSample = (int)(dictionary.GetIntegerValue("BitsPerSample") ?? 8);
        int order = (int)(dictionary.GetIntegerValue("Order") ?? 1);

        float[] domain = ReadFloats(dictionary.GetArray("Domain"), DefaultUnit(inputCount));
        float[] range = ReadFloats(dictionary.GetArray("Range"), DefaultUnit(outputCount));

        float[] encode = ReadFloats(dictionary.GetArray("Encode"), DefaultUnit(inputCount * 2));
        float[] decode = ReadFloats(dictionary.GetArray("Decode"), DefaultUnit(outputCount * 2));

        return new SampledFunction(stream, domain, range, size, bitsPerSample, order, encode, decode);
    }

    private static float[] DefaultUnit(int length)
    {
        var values = new float[length];
        for (int i = 0; i < length; i += 2)
        {
            values[i] = 0f;
            if (i + 1 < length)
            {
                values[i + 1] = 1f;
            }
        }

        return values;
    }

    public override void EvaluateCore(ReadOnlySpan<float> input, Span<float> output)
    {
        int m = _size.Length;
        int n = OutputCount;

        // Normalized coordinate per input axis, clamped so corner indices stay in range.
        Span<float> position = m <= 64 ? stackalloc float[m] : new float[m];
        for (int axis = 0; axis < m; axis++)
        {
            float value = axis < input.Length ? input[axis] : 0f;
            float dMin = axis * 2 < Domain.Length ? Domain[axis * 2] : 0f;
            float dMax = axis * 2 + 1 < Domain.Length ? Domain[(axis * 2) + 1] : 1f;
            float fraction = dMax > dMin ? (value - dMin) / (dMax - dMin) : 0f;

            float eMin = axis * 2 < _encode.Length ? _encode[axis * 2] : 0f;
            float eMax = axis * 2 + 1 < _encode.Length ? _encode[(axis * 2) + 1] : 1f;
            position[axis] = eMin + (fraction * (eMax - eMin));
        }

        if (_order == 0)
        {
            int nearestIndex = 0;
            int nearestStride = 1;
            for (int axis = 0; axis < m; axis++)
            {
                int chosen = Math.Clamp((int)MathF.Round(position[axis]), 0, _size[axis] - 1);
                nearestIndex += chosen * nearestStride;
                nearestStride *= _size[axis];
            }

            FillFromSample(nearestIndex, output);
            return;
        }

        // Multilinear interpolation over the 2^m surrounding grid points.
        output.Clear();
        int corners = 1 << m;
        for (int corner = 0; corner < corners; corner++)
        {
            float weight = 1f;
            int sampleIndex = 0;
            int stride = 1;

            for (int axis = 0; axis < m; axis++)
            {
                int maxIndex = _size[axis] - 1;
                int lower = (int)MathF.Floor(position[axis]);
                lower = Math.Clamp(lower, 0, maxIndex);
                int upper = Math.Min(lower + 1, maxIndex);
                float frac = Math.Clamp(position[axis] - lower, 0f, 1f);

                bool useUpper = (corner & (1 << axis)) != 0;
                int chosen = useUpper ? upper : lower;
                weight *= useUpper ? frac : 1f - frac;

                sampleIndex += chosen * stride;
                stride *= _size[axis];
            }

            if (weight <= 0f)
            {
                continue;
            }

            AccumulateFromSample(sampleIndex, weight, output);
        }

        // Map each normalized [0,1] output through its Decode range.
        for (int o = 0; o < n && o * 2 + 1 < _decode.Length; o++)
        {
            float dMin = _decode[o * 2];
            float dMax = _decode[(o * 2) + 1];
            output[o] = dMin + (output[o] * (dMax - dMin));
        }
    }

    private void FillFromSample(int sampleIndex, Span<float> output)
    {
        output.Clear();
        AccumulateFromSample(sampleIndex, 1f, output);
        for (int o = 0; o < output.Length && o * 2 + 1 < _decode.Length; o++)
        {
            float dMin = _decode[o * 2];
            float dMax = _decode[(o * 2) + 1];
            output[o] = dMin + (output[o] * (dMax - dMin));
        }
    }

    private void AccumulateFromSample(int sampleIndex, float weight, Span<float> output)
    {
        int n = OutputCount;
        int maxSample = (1 << _bitsPerSample) - 1;
        for (int o = 0; o < n; o++)
        {
            int bitOffset = ((sampleIndex * n) + o) * _bitsPerSample;
            int raw = ReadBits(bitOffset, _bitsPerSample);
            float normalized = maxSample == 0 ? 0f : raw / (float)maxSample;
            output[o] += weight * normalized;
        }
    }

    private int ReadBits(int bitOffset, int bits)
    {
        int value = 0;
        for (int i = 0; i < bits; i++)
        {
            int absolute = bitOffset + i;
            int byteIndex = absolute / 8;
            int bit = byteIndex < _samples.Length ? (byte)((_samples[byteIndex] >> (7 - (absolute % 8))) & 1) : 0;
            value = (value << 1) | bit;
        }

        return value;
    }
}

/// <summary>
/// A pass-through function (identity) used in place of a missing <c>/Function</c>. Some real-world
/// mesh shadings omit <c>/Function</c> and rely on the sampled components (already mapped through
/// <c>/Decode</c>) as direct colourspace operands; PDFium and MuPDF both treat that as identity.
/// One component per colour input, each with a [0,1] domain and range so <see cref="PdfFunction.Evaluate"/>
/// clamps to the same bounds the sampled values already sit in.
/// </summary>
internal sealed class IdentityFunction : PdfFunction
{
    private IdentityFunction(float[] domain, float[] range)
        : base(domain, range)
    {
    }

    public static PdfFunction Create(int components)
    {
        components = Math.Max(1, components);
        var bounds = new float[2 * components];
        for (int i = 0; i < bounds.Length; i += 2)
        {
            bounds[i] = 0f;
            bounds[i + 1] = 1f;
        }

        return new IdentityFunction((float[])bounds.Clone(), bounds);
    }

    public override void EvaluateCore(ReadOnlySpan<float> input, Span<float> output)
    {
        int n = Math.Min(input.Length, output.Length);
        input[..n].CopyTo(output[..n]);
    }
}
