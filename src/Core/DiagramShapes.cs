using System.Collections;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace Compositor.Windows;

// Diagram shapes for architecture boards (선 · 꺾은선 · 곡선, 라벨 · 지시선). They are ShapeSpec kinds:
// a line keeps its points in layer pixels, a callout keeps [anchor, elbow, label]. Stored by number
// in projects: append new values at the end of every enum.
public enum StrokeDash { Solid, Dotted, Dashed, DashDot }
public enum StrokeCap { Round, Square, Flat }
public enum LineMark { None, Arrow, OpenArrow, Dot, Ring, Bar }
public enum CalloutLeader { Elbow, Straight }

/// <summary>
/// An immutable list of points with value equality, so ShapeSpec records compare their geometry
/// (undo, unchanged-edit detection, scene hashing) instead of array references.
/// Stored in projects and automation replies as [[x, y], …].
/// </summary>
[JsonConverter(typeof(ShapePointsConverter))]
public sealed class ShapePoints : IReadOnlyList<Point>, IEquatable<ShapePoints>
{
    public const int MaxCount = 1024;
    // Points may sit outside the layer by the stroke and marks; a layer is at most 65,535 px.
    public const double MaxCoordinate = 200_000;
    readonly Point[] points;
    public ShapePoints(IEnumerable<Point> source) => points = source.ToArray();
    public int Count => points.Length;
    public Point this[int index] => points[index];
    public Point[] ToArray() => (Point[])points.Clone();
    public IEnumerator<Point> GetEnumerator() => ((IEnumerable<Point>)points).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => points.GetEnumerator();
    public bool Equals(ShapePoints? other) => other is not null && (ReferenceEquals(this, other) || points.AsSpan().SequenceEqual(other.points));
    public override bool Equals(object? obj) => obj is ShapePoints other && Equals(other);
    public override int GetHashCode() { var hash = new HashCode(); foreach (var p in points) hash.Add(p); return hash.ToHashCode(); }
    public static bool operator ==(ShapePoints? a, ShapePoints? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(ShapePoints? a, ShapePoints? b) => !(a == b);
    public override string ToString() => string.Join(" ", points.Select(p => p.X.ToString("0.##", CultureInfo.InvariantCulture) + "," + p.Y.ToString("0.##", CultureInfo.InvariantCulture)));

    public ShapePoints With(int index, Point value) { var copy = ToArray(); copy[index] = value; return new(copy); }
    public ShapePoints Insert(int index, Point value) { var list = points.ToList(); list.Insert(index, value); return new(list); }
    public ShapePoints RemoveAt(int index) { var list = points.ToList(); list.RemoveAt(index); return new(list); }
    public ShapePoints Offset(Vector delta) => new(points.Select(p => p + delta));
    public ShapePoints Reversed() => new(points.Reverse());
    public void Validate(int minimum, int maximum)
    {
        if (points.Length < minimum || points.Length > maximum) throw new InvalidDataException("선의 점 개수가 올바르지 않습니다.");
        foreach (var p in points)
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || Math.Abs(p.X) > MaxCoordinate || Math.Abs(p.Y) > MaxCoordinate)
                throw new InvalidDataException("선의 점 좌표가 올바르지 않습니다.");
    }
}

sealed class ShapePointsConverter : JsonConverter<ShapePoints>
{
    public override ShapePoints Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("점 목록은 배열이어야 합니다.");
        var points = new List<Point>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartArray || points.Count >= ShapePoints.MaxCount) throw new JsonException("점은 [x, y] 배열이어야 합니다.");
            reader.Read(); if (reader.TokenType != JsonTokenType.Number) throw new JsonException("점 좌표는 숫자여야 합니다.");
            double x = reader.GetDouble();
            reader.Read(); if (reader.TokenType != JsonTokenType.Number) throw new JsonException("점 좌표는 숫자여야 합니다.");
            double y = reader.GetDouble();
            reader.Read(); if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("점은 [x, y] 두 값이어야 합니다.");
            points.Add(new Point(x, y));
        }
        return new ShapePoints(points);
    }
    public override void Write(Utf8JsonWriter writer, ShapePoints value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var p in value) { writer.WriteStartArray(); writer.WriteNumberValue(p.X); writer.WriteNumberValue(p.Y); writer.WriteEndArray(); }
        writer.WriteEndArray();
    }
}

/// <summary>Point edits of line and callout shapes, in layer pixels (VectorShapes.Update re-fits the layer afterwards).</summary>
public static class DiagramEditing
{
    public const int Anchor = 0, Elbow = 1, LabelPoint = 2;

    /// <summary>Moves one point. A bent leader keeps its shoulder level: the label end and the elbow share their height.</summary>
    public static ShapeSpec MovePoint(ShapeSpec spec, int index, Point to)
    {
        var points = spec.Points ?? throw new InvalidOperationException("점이 있는 도형이 아닙니다.");
        if (index < 0 || index >= points.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var moved = points.With(index, to);
        if (spec.Kind == ShapeKind.Callout && spec.Leader == CalloutLeader.Elbow)
        {
            if (index == LabelPoint) moved = moved.With(Elbow, new Point(points[Elbow].X, to.Y));
            else if (index == Elbow) moved = moved.With(LabelPoint, new Point(points[LabelPoint].X, to.Y));
        }
        return spec with { Points = moved };
    }

    /// <summary>Moves the whole label (its leader end) by a delta; a bent leader's shoulder follows it.</summary>
    public static ShapeSpec MoveLabel(ShapeSpec spec, Vector delta)
    {
        var points = spec.Points!; return MovePoint(spec, LabelPoint, points[LabelPoint] + delta);
    }

    public static ShapeSpec InsertPoint(ShapeSpec spec, int index, Point at)
    {
        if (spec.Kind != ShapeKind.Line) throw new InvalidOperationException("선에만 점을 추가할 수 있습니다.");
        var points = spec.Points!;
        if (points.Count >= ShapePoints.MaxCount) throw new InvalidOperationException($"선의 점은 최대 {ShapePoints.MaxCount:N0}개입니다.");
        return spec with { Points = points.Insert(Math.Clamp(index, 0, points.Count), at) };
    }

    public static ShapeSpec DeletePoint(ShapeSpec spec, int index)
    {
        if (spec.Kind != ShapeKind.Line) throw new InvalidOperationException("지시선의 점은 지울 수 없습니다. 꺾임을 없애려면 곧은 지시선을 고르세요.");
        var points = spec.Points!;
        int minimum = spec.Closed ? 3 : 2;
        if (points.Count <= minimum) throw new InvalidOperationException(spec.Closed ? "닫힌 선에는 점이 3개 이상 있어야 합니다." : "선에는 점이 2개 이상 있어야 합니다.");
        return spec with { Points = points.RemoveAt(index) };
    }

    /// <summary>Draws the line the other way: the start and end marks change places on the canvas.</summary>
    public static ShapeSpec Reverse(ShapeSpec spec) => spec.Kind != ShapeKind.Line ? spec
        : spec with { Points = spec.Points!.Reversed(), StartMark = spec.EndMark, EndMark = spec.StartMark };

    /// <summary>A point from `from` toward `to` whose angle is a multiple of 15° (Shift while drawing or editing).</summary>
    public static Point SnapAngle(Point from, Point to, double stepDegrees = 15)
    {
        var delta = to - from; double length = delta.Length;
        if (length < 1e-9) return to;
        double step = stepDegrees * Math.PI / 180, angle = Math.Round(Math.Atan2(delta.Y, delta.X) / step) * step;
        return new Point(from.X + Math.Cos(angle) * length, from.Y + Math.Sin(angle) * length);
    }

    /// <summary>The usual bend for a new callout: up or down from the anchor, then level to the label.</summary>
    public static Point DefaultElbow(Point anchor, Point label) => new(anchor.X, label.Y);
}
