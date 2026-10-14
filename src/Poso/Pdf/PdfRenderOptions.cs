using Poso.Graphics;

namespace Poso.Pdf;

/// <summary>Page rendering options.</summary>
/// <param name="Scale">Zoom factor; 1.0 = 100% (72dpi baseline, 1 point = 1 pixel).</param>
/// <param name="RotationDegrees">Additional rotation applied on top of the page's own /Rotate.</param>
/// <param name="Background">Background color; null means transparent.</param>
/// <param name="Strict">When true, unsupported features throw instead of being skipped (used by tests).</param>
public readonly record struct PdfRenderOptions(float Scale, float RotationDegrees = 0f, PosoColor? Background = null, bool Strict = false)
{
    public static PdfRenderOptions Default { get; } = new(1f);
}
