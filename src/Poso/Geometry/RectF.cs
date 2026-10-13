namespace Poso.Geometry;

/// <summary>Rectangle (x/y is the top-left corner; width and height are non-negative). No axis direction is assumed.</summary>
public readonly record struct RectF(float X, float Y, float Width, float Height)
{
    public static readonly RectF Empty = default;

    public float Left => X;

    public float Top => Y;

    public float Right => X + Width;

    public float Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Construct from left/bottom/right/top (automatically sorted; compatible with box arrays that may be reversed in PDF files).</summary>
    public static RectF FromLbrb(float left, float bottom, float right, float top) =>
        new(Math.Min(left, right), Math.Min(bottom, top), Math.Abs(right - left), Math.Abs(top - bottom));

    public bool Contains(float x, float y) => x >= Left && x <= Right && y >= Top && y <= Bottom;

    public bool Contains(RectF other) =>
        other.Left >= Left && other.Right <= Right && other.Top >= Top && other.Bottom <= Bottom;

    public RectF Intersect(RectF other)
    {
        float left = Math.Max(Left, other.Left);
        float top = Math.Max(Top, other.Top);
        float right = Math.Min(Right, other.Right);
        float bottom = Math.Min(Bottom, other.Bottom);
        return right <= left || bottom <= top ? Empty : new RectF(left, top, right - left, bottom - top);
    }

    public RectF Offset(float dx, float dy) => new(X + dx, Y + dy, Width, Height);

    public RectF Scale(float sx, float sy) => new(X * sx, Y * sy, Width * sx, Height * sy);

    public override string ToString() => $"{X}, {Y}, {Width}, {Height}";
}
