namespace Poso.Geometry;

/// <summary>Two-dimensional point.</summary>
public readonly record struct PointF(float X, float Y)
{
    public override string ToString() => $"{X}, {Y}";
}
