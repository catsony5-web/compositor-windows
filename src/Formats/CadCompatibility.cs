using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CSMath;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;

namespace Compositor.Windows;

public static class CadCompatibility
{
    sealed record Mark(string Layer, Geometry Geometry, Color Color, bool Fill);
    public static CompatibilityResult Read(string path, CompatibilityOptions options, CancellationToken token = default)
    {
        CompatibilityImport.ValidateFile(path);
        if (options.CadLongEdge < 256 || options.CadLongEdge > 4096) throw new ArgumentOutOfRangeException(nameof(options), "도면의 긴 변은 256~4096px입니다.");
        var warnings = new HashSet<string>(); var unsupported = new Dictionary<string, int>(); int readerNotices = 0;
        void Notice(object sender, NotificationEventArgs e)
        {
            token.ThrowIfCancellationRequested();
            if (e.NotificationType != NotificationType.None) readerNotices++;
        }
        EncodingRegister();
        CadDocument cad = Path.GetExtension(path).Equals(".dwg", StringComparison.OrdinalIgnoreCase)
            ? DwgReader.Read(path, new DwgReaderConfiguration { Failsafe = false, KeepUnknownEntities = true }, Notice)
            : DxfReader.Read(path, new DxfReaderConfiguration { Failsafe = false, KeepUnknownEntities = true }, Notice);
        var marks = new List<Mark>(); int visited = 0; long pointCount = 0;
        void Unsupported(string type) => unsupported[type] = unsupported.GetValueOrDefault(type) + 1;
        Point P(XYZ p) { if (Math.Abs(p.Z) > .00001) warnings.Add("Z 좌표가 있는 객체는 XY 평면으로 투영했습니다. 3D 모델·입면의 정확한 표현은 지원하지 않습니다."); return new(p.X, p.Y); }
        void Add(string layer, Geometry geometry, Color color, bool fill, Matrix matrix)
        {
            if (geometry.IsEmpty()) return;
            geometry.Transform = new MatrixTransform(matrix); var bounds = geometry.Bounds;
            if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) || Math.Abs(bounds.X) > 1e15 || Math.Abs(bounds.Y) > 1e15)
                throw new InvalidDataException("도면에 유효하지 않거나 지나치게 큰 좌표가 있습니다.");
            geometry.Freeze(); marks.Add(new(layer, geometry, color, fill));
        }
        Geometry Poly(IReadOnlyList<Point> points, bool closed, IReadOnlyList<double>? bulges = null)
        {
            pointCount += points.Count;
            if (pointCount > 1_000_000) throw new InvalidDataException("도면의 점이 100만 개를 초과합니다. 필요한 영역만 별도 DWG/DXF로 저장해 주세요.");
            var geometry = new StreamGeometry(); if (points.Count < 2) return geometry;
            using var context = geometry.Open(); context.BeginFigure(points[0], closed, closed);
            int count = closed ? points.Count : points.Count - 1;
            for (int i = 0; i < count; i++)
            {
                var end = points[(i + 1) % points.Count]; double b = bulges == null ? 0 : bulges[i]; double chord = (end - points[i]).Length;
                if (!double.IsFinite(b) || !double.IsFinite(chord)) throw new InvalidDataException("유효하지 않은 도면 곡선입니다.");
                if (Math.Abs(b) < 1e-10 || chord < 1e-10) context.LineTo(end, true, false);
                else { double r = chord * (1 + b * b) / (4 * Math.Abs(b)); context.ArcTo(end, new Size(r, r), 0, Math.Abs(b) > 1, b > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, false); }
            }
            return geometry;
        }
        Geometry Curve(XYZ center, double radius, double start, double sweep)
        {
            if (!double.IsFinite(radius) || radius <= 0 || !double.IsFinite(sweep)) throw new InvalidDataException("유효하지 않은 도면 원호입니다.");
            int count = Math.Clamp((int)Math.Ceiling(Math.Abs(sweep) * 64), 8, 1024);
            return Poly(Enumerable.Range(0, count + 1).Select(i => new Point(center.X + radius * Math.Cos(start + sweep * i / count), center.Y + radius * Math.Sin(start + sweep * i / count))).ToArray(), false);
        }
        Geometry TextGeometry(string value, XYZ insert, double height, double rotation, double widthFactor = 1, TextAlignment alignment = TextAlignment.Left)
        {
            if (value.Length > 10000 || !double.IsFinite(height) || height <= 0 || !double.IsFinite(widthFactor) || widthFactor <= 0) throw new InvalidDataException("도면의 문자 크기 또는 길이가 지원 범위를 벗어났습니다.");
            warnings.Add("CAD 문자는 설치된 글꼴로 그립니다. SHX 글꼴·문자 정렬·복잡한 MTEXT 서식은 원본과 다를 수 있습니다.");
            var text = new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Malgun Gothic"), Math.Clamp(height, .001, 1e6), Brushes.Black, 1) { TextAlignment = alignment };
            var geometry = text.BuildGeometry(new Point(0, -text.Baseline)); var matrix = Matrix.Identity; matrix.Scale(widthFactor, -1); matrix.Rotate(rotation * 180 / Math.PI); matrix.Translate(insert.X, insert.Y);
            // Bake the text transform before the enclosing block transform is applied.
            var group = new GeometryGroup(); group.Children.Add(geometry); group.Transform = new MatrixTransform(matrix); return group.GetOutlinedPathGeometry();
        }
        void Visit(Entity entity, Matrix transform, string? inheritedLayer, ACadSharp.Color? inheritedColor, int depth)
        {
            token.ThrowIfCancellationRequested(); if (++visited > 200_000 || depth > 16) throw new InvalidDataException("도면 객체 또는 중첩 블록 한도를 초과합니다.");
            if (entity.IsInvisible || !entity.Layer.IsOn || entity.Layer.Flags.HasFlag(ACadSharp.Tables.LayerFlags.Frozen)) return;
            string layer = entity.Layer.Name == "0" && inheritedLayer != null ? inheritedLayer : entity.Layer.Name;
            var active = entity.Color.IsByBlock && inheritedColor.HasValue ? inheritedColor.Value : entity.GetActiveColor();
            if (entity.Color.IsByLayer && entity.Layer.Name == "0" && inheritedColor.HasValue) active = inheritedColor.Value;
            var color = Color.FromRgb(active.R, active.G, active.B); if (color.R > 235 && color.G > 235 && color.B > 235) color = Colors.Black;
            Geometry? geometry = null; bool fill = false;
            switch (entity)
            {
                case Insert block:
                    if (block.Block == null || !string.IsNullOrWhiteSpace(block.Block.BlockEntity.XRefPath)) { Unsupported("외부 참조 XREF"); return; }
                    if (block.Normal.Z != 1 || block.Normal.X != 0 || block.Normal.Y != 0) warnings.Add("일부 객체의 기울어진 좌표계는 XY 기준으로 표시했습니다.");
                    if ((long)block.RowCount * block.ColumnCount > 10000) throw new InvalidDataException("블록 배열이 너무 큽니다.");
                    for (int row = 0; row < Math.Max(1, (int)block.RowCount); row++) for (int column = 0; column < Math.Max(1, (int)block.ColumnCount); column++)
                    {
                        var basis = block.Block.BlockEntity.BasePoint; var local = Matrix.Identity;
                        local.Translate(-basis.X, -basis.Y); local.Scale(block.XScale, block.YScale); local.Translate(column * block.ColumnSpacing, row * block.RowSpacing); local.Rotate(block.Rotation * 180 / Math.PI); local.Translate(block.InsertPoint.X, block.InsertPoint.Y); local.Append(transform);
                        foreach (var child in block.Block.Entities) if (child is not AttributeDefinition) Visit(child, local, layer, active, depth + 1);
                    }
                    foreach (var attribute in block.Attributes) Visit(attribute, transform, layer, active, depth + 1);
                    return;
                case Dimension dimension:
                    if (dimension.Block == null) { Unsupported("치수 블록 없음"); return; }
                    foreach (var child in dimension.Block.Entities) Visit(child, transform, layer, active, depth + 1); return;
                case Line line: geometry = new LineGeometry(P(line.StartPoint), P(line.EndPoint)); break;
                case Arc arc: geometry = Curve(arc.Center, arc.Radius, arc.StartAngle, arc.Sweep); break;
                case Circle circle: geometry = new EllipseGeometry(P(circle.Center), circle.Radius, circle.Radius); break;
                case ACadSharp.Entities.Ellipse ellipse: geometry = Poly(ellipse.PolygonalVertexes(256).Select(P).ToArray(), ellipse.IsFullEllipse); break;
                case LwPolyline polyline: geometry = Poly(polyline.Vertices.Select(v => new Point(v.Location.X, v.Location.Y)).ToArray(), polyline.IsClosed, polyline.Vertices.Select(v => v.Bulge).ToArray()); break;
                case Polyline2D polyline: geometry = Poly(polyline.Vertices.Select(v => P(v.Location)).ToArray(), polyline.IsClosed, polyline.Vertices.Select(v => v.Bulge).ToArray()); break;
                case Polyline3D polyline: geometry = Poly(polyline.Vertices.Select(v => P(v.Location)).ToArray(), polyline.IsClosed); break;
                case Spline spline:
                    if (!spline.TryPolygonalVertexes(256, out var points)) { Unsupported("스플라인"); return; }
                    geometry = Poly(points.Select(P).ToArray(), spline.IsClosed); break;
                case Solid solid: geometry = Poly(new[] { P(solid.FirstCorner), P(solid.SecondCorner), P(solid.FourthCorner), P(solid.ThirdCorner) }, true); fill = true; break;
                case ACadSharp.Entities.Point point: geometry = new EllipseGeometry(P(point.Location), .1, .1); fill = true; break;
                case TextEntity text: geometry = TextGeometry(text.Value, text.InsertPoint, text.Height, text.Rotation, text.WidthFactor); fill = true; break;
                case MText text: geometry = TextGeometry(text.PlainText, text.InsertPoint, text.Height, text.Rotation); fill = true; break;
                case Hatch hatch:
                    // Render boundaries explicitly, rather than inventing unsupported pattern fills.
                    warnings.Add("해치는 경계선으로 가져왔습니다. 솔리드/패턴 채움은 재현하지 않습니다.");
                    foreach (var boundary in hatch.Paths) foreach (var edge in boundary.Edges)
                    { var child = edge.ToEntity(); child.Layer = entity.Layer; child.Color = active; Visit(child, transform, layer, active, depth + 1); }
                    return;
                default: Unsupported(entity.ObjectName); return;
            }
            Add(layer, geometry, color, fill, transform);
        }
        foreach (var entity in cad.Entities) Visit(entity, Matrix.Identity, null, null, 0);
        if (marks.Count == 0) throw new NotSupportedException("모델 공간에 표시할 수 있는 2D 객체가 없습니다. 도면을 PDF로 출력해 가져와 주세요.");
        Rect bounds = Rect.Empty; foreach (var mark in marks) bounds.Union(mark.Geometry.Bounds);
        double longest = Math.Max(bounds.Width, bounds.Height); if (longest <= 0 || !double.IsFinite(longest)) throw new InvalidDataException("도면 범위가 올바르지 않습니다.");
        double scale = (options.CadLongEdge - 40) / longest;
        int width = Math.Max(64, (int)Math.Ceiling(bounds.Width * scale) + 40), height = Math.Max(64, (int)Math.Ceiling(bounds.Height * scale) + 40);
        Raster.ValidateSize(width, height);
        var groups = options.SeparateLayers ? marks.GroupBy(m => m.Layer).Select(g => (Name: g.Key, Marks: g.ToArray())).ToArray() : [(Name: "CAD 모델 공간", Marks: marks.ToArray())];
        if (groups.Length + 1 > Document.MaxLayers || (long)width * height * 4 * (groups.Length + 1) > Document.MaxLayerBytes)
            throw new InvalidDataException("분리할 도면 레이어가 너무 큽니다. ‘CAD 레이어 분리’를 끄거나 긴 변 크기를 줄여 주세요.");
        var doc = new Document { Name = Path.GetFileNameWithoutExtension(path), Width = width, Height = height };
        doc.Add(new Layer { Name = "도면 배경", Pixels = Raster.Solid(width, height, Colors.White), Locked = true });
        var fit = new Matrix(scale, 0, 0, -scale, 20 - bounds.Left * scale, 20 + bounds.Bottom * scale);
        foreach (var group in groups)
        {
            token.ThrowIfCancellationRequested(); var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.PushTransform(new MatrixTransform(fit));
                foreach (var mark in group.Marks)
                { var brush = new SolidColorBrush(mark.Color); brush.Freeze(); drawing.DrawGeometry(mark.Fill ? brush : null, mark.Fill ? null : new Pen(brush, 1.2 / scale), mark.Geometry); }
                drawing.Pop();
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze();
            doc.Add(new Layer { Name = group.Name, Pixels = Raster.FromBitmap(bitmap) });
        }
        if (readerNotices != 0) warnings.Add($"도면을 읽는 과정에서 {readerNotices}건의 호환성 안내가 발생했습니다. 일부 객체나 부가 정보가 생략될 수 있으니 미리보기를 원본과 비교해 주세요.");
        if (unsupported.Count != 0) warnings.Add("표시하지 못한 객체: " + string.Join(", ", unsupported.OrderBy(p => p.Key).Take(15).Select(p => $"{p.Key} {p.Value}개")));
        warnings.Add("모델 공간의 2D 미리보기를 픽셀로 가져왔습니다. 도면 단위·실측 축척·CTB 선종류/선굵기·배치·외부 참조는 보존하지 않습니다. CAD 원본 수정용이 아닌 이미지 편집용입니다.");
        return new(doc, warnings.ToArray());
    }
    static void EncodingRegister() => System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
}
