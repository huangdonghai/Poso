using Poso.Geometry;

namespace Poso.Graphics;

/// <summary>Path segment type.</summary>
public enum PathSegmentType
{
    MoveTo,
    LineTo,
    CubicTo,
    Close,
}

/// <summary>
/// Path segment. MoveTo/LineTo use (X1,Y1); CubicTo uses (X1,Y1) as control point 1,
/// (X2,Y2) as control point 2, and (X3,Y3) as the end point.
/// </summary>
public readonly record struct PathSegment(PathSegmentType Type, float X1, float Y1, float X2, float Y2, float X3, float Y3)
{
    public static PathSegment Move(float x, float y) => new(PathSegmentType.MoveTo, x, y, 0, 0, 0, 0);

    public static PathSegment Line(float x, float y) => new(PathSegmentType.LineTo, x, y, 0, 0, 0, 0);

    public static PathSegment Cubic(float x1, float y1, float x2, float y2, float x3, float y3) =>
        new(PathSegmentType.CubicTo, x1, y1, x2, y2, x3, y3);

    public static readonly PathSegment CloseSegment = new(PathSegmentType.Close, 0, 0, 0, 0, 0, 0);
}

/// <summary>Record-style path (user space coordinates). Backends consume the segment sequence for rasterization.</summary>
public interface IPath
{
    bool IsEmpty { get; }

    int SegmentCount { get; }

    PathSegment GetSegment(int index);

    /// <summary>Control point bounds (transform not applied).</summary>
    RectF Bounds { get; }
}

/// <summary>Path builder.</summary>
public interface IPathBuilder
{
    void MoveTo(float x, float y);

    void LineTo(float x, float y);

    void CubicTo(float x1, float y1, float x2, float y2, float x3, float y3);

    void Close();

    /// <summary>Finish building and return the path (the builder can continue to be used).</summary>
    IPath Build();
}
