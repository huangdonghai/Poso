namespace Poso.Graphics;

/// <summary>
/// PDF transparency blend mode (the /BM entry of an ExtGState dictionary, ISO 32000-1 11.3.3.4).
/// The separator and non-separable modes map onto the equivalent back-end compositing operators.
/// </summary>
public enum BlendMode
{
    Normal,
    Multiply,
    Screen,
    Overlay,
    Darken,
    Lighten,
    ColorBurn,
    ColorDodge,
    HardLight,
    SoftLight,
    Difference,
    Exclusion,
    Luminosity,
    Hue,
    Saturation,
    Color,
}
