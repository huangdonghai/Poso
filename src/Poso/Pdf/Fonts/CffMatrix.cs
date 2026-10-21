namespace Poso.Pdf.Fonts;

/// <summary>
/// Resolves the design-space matrix a CFF DICT declares for its charstrings.
/// </summary>
/// <remarks>
/// A CID-keyed font dict names its scale as CharMatrix (12 7) where a name-keyed one uses FontMatrix
/// (7), and either position can be absent. Generators also routinely write an identity matrix as a
/// placeholder - pdf.js issue11713.pdf's font dict says [1 0 0 1 0 0] while its top DICT carries the
/// real 1/1000 - and an identity would put a glyph's 700-unit stem at 700 em, which no program means.
/// So an identity at either position carries no information and the search moves on, exactly as
/// FreeType's "matrix is plausible" guard ends up treating it.
/// </remarks>
internal static class CffMatrix
{
    /// <summary>The spec default: design units are thousandths of an em.</summary>
    public static readonly float[] Default = [0.001f, 0f, 0f, 0.001f, 0f, 0f];

    /// <summary>
    /// The matrix <paramref name="dict"/> declares, in [xx, xy, yx, yy, dx, dy] order, or null when it
    /// declares none worth honouring. A four-operand matrix keeps its translation at zero.
    /// </summary>
    public static float[]? Declared(CffDict dict)
    {
        float[]? charMatrix = dict.GetArray((12 << 8) | 7);
        if (charMatrix is { Length: >= 4 } && !IsIdentity(charMatrix))
        {
            return Normalise(charMatrix);
        }

        float[]? fontMatrix = dict.GetArray(7);
        if (fontMatrix is { Length: >= 4 } && !IsIdentity(fontMatrix))
        {
            return Normalise(fontMatrix);
        }

        return null;
    }

    private static bool IsIdentity(float[] matrix) =>
        matrix[0] == 1f && matrix[1] == 0f && matrix[2] == 0f && matrix[3] == 1f &&
        (matrix.Length < 5 || (matrix[4] == 0f && matrix[5] == 0f));

    private static float[] Normalise(float[] matrix) => matrix.Length >= 6
        ? [matrix[0], matrix[1], matrix[2], matrix[3], matrix[4], matrix[5]]
        : [matrix[0], matrix[1], matrix[2], matrix[3], 0f, 0f];
}
