using Poso.Geometry;

namespace Poso.Graphics;

/// <summary>Core implementation of <see cref="IPath"/>: segment sequence plus lazily computed bounds.</summary>
public sealed class Path : IPath
{
    private readonly List<PathSegment> _segments;
    private RectF _bounds;
    private bool _boundsDirty = true;

    public Path()
    {
        _segments = new List<PathSegment>();
    }

    public Path(int capacity)
    {
        _segments = new List<PathSegment>(capacity);
    }

    public bool IsEmpty => _segments.Count == 0;

    public int SegmentCount => _segments.Count;

    public PathSegment GetSegment(int index) => _segments[index];

    public RectF Bounds
    {
        get
        {
            if (_boundsDirty)
            {
                RecalculateBounds();
            }

            return _bounds;
        }
    }

    internal void Add(PathSegment segment)
    {
        _segments.Add(segment);
        _boundsDirty = true;
    }

    private void RecalculateBounds()
    {
        if (_segments.Count == 0)
        {
            _bounds = RectF.Empty;
            _boundsDirty = false;
            return;
        }

        float minX = float.MaxValue;
        float minY = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = float.MinValue;

        foreach (PathSegment segment in _segments)
        {
            switch (segment.Type)
            {
                case PathSegmentType.MoveTo:
                case PathSegmentType.LineTo:
                    Include(ref minX, ref minY, ref maxX, ref maxY, segment.X1, segment.Y1);
                    break;

                case PathSegmentType.CubicTo:
                    Include(ref minX, ref minY, ref maxX, ref maxY, segment.X1, segment.Y1);
                    Include(ref minX, ref minY, ref maxX, ref maxY, segment.X2, segment.Y2);
                    Include(ref minX, ref minY, ref maxX, ref maxY, segment.X3, segment.Y3);
                    break;

                case PathSegmentType.Close:
                    break;
            }
        }

        _bounds = new RectF(minX, minY, maxX - minX, maxY - minY);
        _boundsDirty = false;
    }

    private static void Include(ref float minX, ref float minY, ref float maxX, ref float maxY, float x, float y)
    {
        if (x < minX)
        {
            minX = x;
        }

        if (x > maxX)
        {
            maxX = x;
        }

        if (y < minY)
        {
            minY = y;
        }

        if (y > maxY)
        {
            maxY = y;
        }
    }
}

/// <summary>Core implementation of <see cref="IPathBuilder"/>.</summary>
public sealed class PathBuilder : IPathBuilder
{
    private readonly Path _path;

    public PathBuilder()
    {
        _path = new Path();
    }

    public PathBuilder(int capacity)
    {
        _path = new Path(capacity);
    }

    public bool IsEmpty => _path.IsEmpty;

    public void MoveTo(float x, float y) => _path.Add(PathSegment.Move(x, y));

    public void LineTo(float x, float y) => _path.Add(PathSegment.Line(x, y));

    public void CubicTo(float x1, float y1, float x2, float y2, float x3, float y3) =>
        _path.Add(PathSegment.Cubic(x1, y1, x2, y2, x3, y3));

    public void Close() => _path.Add(PathSegment.CloseSegment);

    public IPath Build() => _path;
}
