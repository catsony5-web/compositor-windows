using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Blocks;
using ACadSharp.Tables;
using CSMath;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;

namespace Compositor.Windows;

public static class CadCompatibility
{
    public sealed record Space(string Key, string Name)
    {
        public override string ToString() => Name;
    }
    sealed record Mark(string Layer, Geometry Geometry, Color Color, bool Fill, Geometry? Clip, int ObjectId, string ObjectName);
    sealed record PaintObject(string Name, string Layer, Mark[] Marks);
    sealed record PixelArea(int Left, int Top, int Width, int Height);
    sealed record PaintGroup(string Name, PaintObject[] Objects);
    static CadDocument Load(string path, NotificationEventHandler? notice = null) => Path.GetExtension(path).Equals(".dwg", StringComparison.OrdinalIgnoreCase)
        ? DwgReader.Read(path, new DwgReaderConfiguration { Failsafe = false, KeepUnknownEntities = true }, notice)
        : DxfReader.Read(path, new DxfReaderConfiguration { Failsafe = false, KeepUnknownEntities = true }, notice);
    public static IReadOnlyList<Space> Inspect(string path)
    {
        CompatibilityImport.ValidateFile(path); EncodingRegister(); var cad = Load(path);
        return new[] { new Space("*Model_Space", "모델 공간") }.Concat(cad.BlockRecords
            .Where(b => b.Layout?.IsPaperSpace == true && b.Entities.Any(e => e is not Viewport || e is Viewport v && !v.RepresentsPaper))
            .OrderBy(b => b.Layout.TabOrder).Select(b => new Space(b.Name, b.Layout.Name))).ToArray();
    }
    public static CompatibilityResult Read(string path, CompatibilityOptions options, CancellationToken token = default)
    {
        CompatibilityImport.ValidateFile(path);
        if (options.CadLongEdge < 256 || options.CadLongEdge > 4096) throw new ArgumentOutOfRangeException(nameof(options), "도면의 긴 변은 256~4096px입니다.");
        var structure = options.CadStructure ?? (options.SeparateLayers ? CadImportStructure.Layers : CadImportStructure.Combined);
        if (!Enum.IsDefined(structure)) throw new ArgumentOutOfRangeException(nameof(options), "도면 가져오기 구조가 올바르지 않습니다.");
        bool separateObjects = structure == CadImportStructure.Objects;
        var warnings = new HashSet<string>(); var unsupported = new Dictionary<string, int>(); int readerNotices = 0;
        void Notice(object sender, NotificationEventArgs e)
        {
            token.ThrowIfCancellationRequested();
            if (e.NotificationType != NotificationType.None) readerNotices++;
        }
        EncodingRegister();
        CadDocument cad = Load(path, Notice);
        var references = new Dictionary<string, CadDocument>(StringComparer.OrdinalIgnoreCase) { [Path.GetFullPath(path)] = cad };
        var reading = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(path) };
        long referenceBytes = 0;
        Geometry? currentClip = null; HashSet<string>? frozenLayers = null;
        var marks = new List<Mark>(); int visited = 0, nextObjectId = 0; long pointCount = 0;
        var markedObjects = separateObjects ? new HashSet<int>() : null;
        void Unsupported(string type) => unsupported[type] = unsupported.GetValueOrDefault(type) + 1;
        Point P(XYZ p) { if (Math.Abs(p.Z) > .00001) warnings.Add("Z 좌표가 있는 객체는 XY 평면으로 투영했습니다. 3D 모델·입면의 정확한 표현은 지원하지 않습니다."); return new(p.X, p.Y); }
        void Add(string layer, Geometry geometry, Color color, bool fill, Matrix matrix, int objectId, string objectName)
        {
            if (geometry.IsEmpty()) return;
            geometry.Transform = new MatrixTransform(matrix); var bounds = geometry.Bounds;
            if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) || Math.Abs(bounds.X) > 1e15 || Math.Abs(bounds.Y) > 1e15)
                throw new InvalidDataException("도면에 유효하지 않거나 지나치게 큰 좌표가 있습니다.");
            if (currentClip != null && !bounds.IntersectsWith(currentClip.Bounds)) return;
            if (markedObjects != null && markedObjects.Add(objectId) && markedObjects.Count >= Document.MaxNodes)
                throw new InvalidDataException($"도면 객체 수가 가져오기 한도({Document.MaxNodes:N0}개, 그룹 포함)를 초과합니다. ‘레이어별’ 또는 ‘하나로’를 선택하거나 필요한 객체만 별도 도면으로 저장해 주세요.");
            geometry.Freeze(); marks.Add(new(layer, geometry, color, fill, currentClip, objectId, objectName));
        }
        Geometry Poly(IReadOnlyList<Point> points, bool closed, IReadOnlyList<double>? bulges = null)
        {
            pointCount += points.Count;
            if (pointCount > 8_000_000) throw new InvalidDataException("도면의 변환 점이 800만 개를 초과합니다. 필요한 배치나 영역을 선택해 주세요.");
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
        void Visit(Entity entity, Matrix transform, string? inheritedLayer, ACadSharp.Color? inheritedColor, int depth, string source, string prefix = "", int objectId = 0, string? objectName = null)
        {
            token.ThrowIfCancellationRequested(); if (++visited > 1_000_000 || depth > 16) throw new InvalidDataException("도면 객체 또는 중첩 블록 한도를 초과합니다.");
            if (entity.IsInvisible || !entity.Layer.IsOn || entity.Layer.Flags.HasFlag(ACadSharp.Tables.LayerFlags.Frozen)) return;
            if (entity is ACadSharp.Entities.Point && entity.Layer.Name.EndsWith("Defpoints", StringComparison.OrdinalIgnoreCase)) return;
            string layer = entity.Layer.Name == "0" && inheritedLayer != null ? inheritedLayer : prefix + entity.Layer.Name;
            if (frozenLayers?.Contains(layer) == true || frozenLayers?.Contains(entity.Layer.Name) == true) return;
            cad.Layers.TryGetValue(layer, out var hostLayer);
            if (hostLayer != null && (!hostLayer.IsOn || hostLayer.Flags.HasFlag(LayerFlags.Frozen))) return;
            var active = entity.Color.IsByBlock && inheritedColor.HasValue ? inheritedColor.Value : entity.GetActiveColor();
            if (entity.Color.IsByLayer && prefix.Length > 0 && hostLayer != null) active = hostLayer.Color;
            if (entity.Color.IsByLayer && entity.Layer.Name == "0" && inheritedLayer != null && inheritedColor.HasValue) active = inheritedColor.Value;
            var color = Color.FromRgb(active.R, active.G, active.B); if (color.R > 235 && color.G > 235 && color.B > 235) color = Colors.Black;
            bool atomicParent = objectId != 0;
            if (!atomicParent) { objectId = ++nextObjectId; objectName = ObjectLabel(entity) + " " + objectId.ToString(CultureInfo.InvariantCulture); }
            Geometry? geometry = null; bool fill = false;
            switch (entity)
            {
                case Insert block:
                    if (block.Block == null) { Unsupported("블록 정의 없음"); return; }
                    // Bound blocks and dimension arrowheads can retain XRefPath. Only the flags identify an actual reference.
                    bool isReference = (block.Block.BlockEntity.Flags & (BlockTypeFlags.XRef | BlockTypeFlags.XRefOverlay)) != 0;
                    if (prefix.Length > 0 && block.Block.BlockEntity.Flags.HasFlag(BlockTypeFlags.XRefOverlay)) return;
                    var children = block.Block.Entities.AsEnumerable(); string childSource = source, childPrefix = prefix;
                    string? referencePath = null;
                    var blockBase = block.Block.BlockEntity.BasePoint;
                    if (isReference)
                    {
                        string stored = block.Block.BlockEntity.XRefPath;
                        if (string.IsNullOrWhiteSpace(stored)) { Unsupported("외부참조 경로 없음: " + block.Block.Name); return; }
                        referencePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, stored));
                        if (referencePath.StartsWith(@"\\", StringComparison.Ordinal)) { Unsupported("네트워크 외부참조: " + block.Block.Name); return; }
                        if (!File.Exists(referencePath))
                        {
                            string local = Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileName(stored));
                            if (File.Exists(local)) referencePath = local;
                            else { Unsupported("외부참조 파일 없음: " + block.Block.Name); return; }
                        }
                        if (!reading.Add(referencePath)) { Unsupported("순환 외부참조: " + block.Block.Name); return; }
                        if (!references.TryGetValue(referencePath, out var external))
                        {
                            CompatibilityImport.ValidateFile(referencePath); referenceBytes += new FileInfo(referencePath).Length;
                            if (references.Count >= 32 || referenceBytes > 512L * 1024 * 1024) throw new InvalidDataException("외부참조의 개수 또는 파일 크기 한도를 초과합니다.");
                            external = Load(referencePath, Notice); references.Add(referencePath, external);
                        }
                        children = external.Entities; childSource = referencePath; childPrefix += block.Block.Name + "|"; blockBase = external.Header.ModelSpaceInsertionBase;
                    }
                    if (Math.Abs(block.Normal.X) > 1e-8 || Math.Abs(block.Normal.Y) > 1e-8) warnings.Add("기울어진 블록 좌표계는 XY 평면에 투영했습니다.");
                    if ((long)block.RowCount * block.ColumnCount > 10000) throw new InvalidDataException("블록 배열이 너무 큽니다.");
                    for (int row = 0; row < Math.Max(1, (int)block.RowCount); row++) for (int column = 0; column < Math.Max(1, (int)block.ColumnCount); column++)
                    {
                        var basis = blockBase; var local = Matrix.Identity;
                        local.Translate(-basis.X, -basis.Y); local.Scale(block.XScale, block.YScale); local.Translate(column * block.ColumnSpacing, row * block.RowSpacing); local.Rotate(block.Rotation * 180 / Math.PI); local.Translate(block.InsertPoint.X, block.InsertPoint.Y);
                        var axis = Matrix3.ArbitraryAxis(block.Normal); var ax = axis * XYZ.AxisX; var ay = axis * XYZ.AxisY; var az = axis * XYZ.AxisZ;
                        local.Append(new Matrix(ax.X, ax.Y, ay.X, ay.Y, az.X * block.InsertPoint.Z, az.Y * block.InsertPoint.Z)); local.Append(transform);
                        foreach (var child in children) if (child is not AttributeDefinition) Visit(child, local, isReference ? null : layer, active, depth + 1, childSource, childPrefix, atomicParent ? objectId : 0, atomicParent ? objectName : null);
                    }
                    if (referencePath != null) reading.Remove(referencePath);
                    foreach (var attribute in block.Attributes) Visit(attribute, transform, layer, active, depth + 1, source, prefix, atomicParent ? objectId : 0, atomicParent ? objectName : null);
                    return;
                case Dimension dimension:
                    if (dimension.Block == null) { Unsupported("치수 블록 없음"); return; }
                    foreach (var child in dimension.Block.Entities) Visit(child, transform, layer, active, depth + 1, source, prefix, objectId, objectName); return;
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
                    { var child = edge.ToEntity(); child.Layer = entity.Layer; child.Color = active; Visit(child, transform, layer, active, depth + 1, source, prefix, objectId, objectName); }
                    return;
                case Viewport: return;
                default: Unsupported(entity.ObjectName); return;
            }
            Add(layer, geometry, color, fill, transform, objectId, objectName!);
        }
        var spaces = cad.BlockRecords.Where(b => b.Layout?.IsPaperSpace == true && b.Entities.Any(e => e is not Viewport || e is Viewport v && !v.RepresentsPaper))
            .OrderBy(b => b.Layout.TabOrder).ToArray();
        var selected = options.CadLayout == null ? spaces.FirstOrDefault() : spaces.FirstOrDefault(b => b.Name == options.CadLayout || b.Layout.Name == options.CadLayout);
        if (options.CadLayout != null && options.CadLayout != "*Model_Space" && selected == null) throw new ArgumentException("선택한 CAD 배치를 찾을 수 없습니다.");
        string spaceName = selected?.Layout.Name ?? "모델 공간";
        if (selected != null)
        {
            foreach (var viewport in selected.Entities.OfType<Viewport>().Where(v => !v.RepresentsPaper && v.ActiveStatus != 0 && !v.IsInvisible))
            {
                if (viewport.Height <= 0 || viewport.Width <= 0 || viewport.ViewHeight <= 0) continue;
                if (Math.Abs(viewport.ViewDirection.X) > 1e-8 || Math.Abs(viewport.ViewDirection.Y) > 1e-8 || viewport.ViewDirection.Z <= 0)
                { Unsupported("기울어진 3D 뷰포트"); continue; }
                var rectangle = new Rect(viewport.Center.X - viewport.Width / 2, viewport.Center.Y - viewport.Height / 2, viewport.Width, viewport.Height);
                currentClip = new RectangleGeometry(rectangle);
                if (viewport.Boundary is LwPolyline boundary)
                    currentClip = Poly(boundary.Vertices.Select(v => new Point(v.Location.X, v.Location.Y)).ToArray(), true, boundary.Vertices.Select(v => v.Bulge).ToArray());
                else if (viewport.Boundary is Circle boundaryCircle) currentClip = new EllipseGeometry(P(boundaryCircle.Center), boundaryCircle.Radius, boundaryCircle.Radius);
                else if (viewport.Boundary != null) warnings.Add("일부 비정형 뷰포트는 사각 경계로 표시했습니다.");
                currentClip.Freeze(); frozenLayers = viewport.FrozenLayers.Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                double viewScale = viewport.Height / viewport.ViewHeight; var view = Matrix.Identity;
                view.Translate(-viewport.ViewTarget.X, -viewport.ViewTarget.Y); view.Rotate(-viewport.TwistAngle * 180 / Math.PI);
                view.Translate(-viewport.ViewCenter.X, -viewport.ViewCenter.Y); view.Scale(viewScale, viewScale); view.Translate(viewport.Center.X, viewport.Center.Y);
                foreach (var entity in cad.Entities) Visit(entity, view, null, null, 0, path);
            }
            currentClip = null; frozenLayers = null;
            foreach (var entity in selected.Entities) if (entity is not Viewport) Visit(entity, Matrix.Identity, null, null, 0, path);
        }
        else foreach (var entity in cad.Entities) Visit(entity, Matrix.Identity, null, null, 0, path);
        if (marks.Count == 0) throw new NotSupportedException($"‘{spaceName}’에서 표시할 도형을 찾지 못했습니다. " + (unsupported.Count == 0 ? "다른 배치나 모델 공간을 선택해 주세요." : string.Join(", ", unsupported.Keys.Take(5))));
        Rect bounds = Rect.Empty; foreach (var mark in marks) { var area = mark.Geometry.Bounds; if (mark.Clip != null) area.Intersect(mark.Clip.Bounds); bounds.Union(area); }
        double longest = Math.Max(bounds.Width, bounds.Height); if (longest <= 0 || !double.IsFinite(longest)) throw new InvalidDataException("도면 범위가 올바르지 않습니다.");
        double scale = (options.CadLongEdge - 40) / longest;
        int width = Math.Max(64, (int)Math.Ceiling(bounds.Width * scale) + 40), height = Math.Max(64, (int)Math.Ceiling(bounds.Height * scale) + 40);
        Raster.ValidateSize(width, height);
        var groups = new List<PaintGroup>();
        if (separateObjects)
        {
            // A source entity is the selection unit. Do not join merely touching LINEs,
            // or split a polyline into segments. Instance IDs also separate repeated blocks/viewports.
            var objects = marks.GroupBy(m => m.ObjectId).Select(g => new PaintObject(g.First().ObjectName, g.First().Layer, g.ToArray())).ToArray();
            var names = new Dictionary<string, int>();
            int start = 0;
            while (start < objects.Length)
            {
                token.ThrowIfCancellationRequested();
                int end = start + 1; string layer = objects[start].Layer;
                while (end < objects.Length && objects[end].Layer == layer) end++;
                int occurrence = names.GetValueOrDefault(layer) + 1; names[layer] = occurrence;
                groups.Add(new(occurrence == 1 ? layer : layer + " · " + occurrence, objects[start..end])); start = end;
            }
            if (names.Values.Any(n => n > 1)) warnings.Add("겹침 순서를 유지하기 위해 같은 CAD 레이어가 여러 그룹으로 나뉠 수 있습니다.");
        }
        else if (structure == CadImportStructure.Layers)
            groups.AddRange(marks.GroupBy(m => m.Layer).Select(g => new PaintGroup(g.Key, [new(g.Key, g.Key, g.ToArray())])));
        else groups.Add(new("CAD " + spaceName, [new("CAD " + spaceName, "", marks.ToArray())]));
        var fit = new Matrix(scale, 0, 0, -scale, 20 - bounds.Left * scale, 20 + bounds.Bottom * scale);
        PixelArea Area(IEnumerable<Mark> content)
        {
            token.ThrowIfCancellationRequested();
            Rect groupBounds = Rect.Empty;
            foreach (var mark in content) { var area = mark.Geometry.Bounds; if (mark.Clip != null) area.Intersect(mark.Clip.Bounds); groupBounds.Union(area); }
            var pixelsBounds = new MatrixTransform(fit).TransformBounds(groupBounds);
            int left = Math.Max(0, (int)Math.Floor(pixelsBounds.Left) - 2), top = Math.Max(0, (int)Math.Floor(pixelsBounds.Top) - 2);
            int pixelWidth = Math.Max(1, Math.Min(width, (int)Math.Ceiling(pixelsBounds.Right) + 2) - left);
            int pixelHeight = Math.Max(1, Math.Min(height, (int)Math.Ceiling(pixelsBounds.Bottom) + 2) - top);
            return new(left, top, pixelWidth, pixelHeight);
        }
        var plans = groups.Select(g => (Group: g, Items: g.Objects.Select(o => (Object: o, Area: Area(o.Marks))).ToArray())).ToArray();
        int objectCount = plans.Sum(p => p.Items.Length), nodeCount = 1 + objectCount + (separateObjects ? plans.Length : 0);
        int nodeLimit = separateObjects ? Document.MaxNodes : Document.MaxLayers;
        if (!options.RetainVectors && objectCount > Document.MaxLayers)
            throw new InvalidDataException($"픽셀 객체는 {Document.MaxLayers:N0}개까지 가져올 수 있습니다. 벡터 보존을 사용하거나 ‘레이어별’ 또는 ‘하나로’를 선택해 주세요.");
        if (nodeCount > nodeLimit)
            throw new InvalidDataException(separateObjects
                ? $"객체 {objectCount:N0}개와 그룹을 포함한 항목 {nodeCount:N0}개가 가져오기 한도({nodeLimit:N0}개)를 초과합니다. ‘레이어별’ 또는 ‘하나로’를 선택하거나 필요한 객체만 별도 도면으로 저장해 주세요."
                : "분리할 도면 레이어가 너무 많습니다. ‘하나로’를 선택하거나 필요한 레이어만 별도 도면으로 저장해 주세요.");
        long PixelBytes(PixelArea area) => (long)area.Width * area.Height * 4;
        long canvasBytes = (long)width * height * 4;
        // Group bounds share one immutable transparent canvas. Ordinary object
        // previews remain independently budgeted and are cropped to their own bounds.
        long totalBytes = canvasBytes * (separateObjects ? 2 : 1) + plans.Sum(p => p.Items.Sum(i => PixelBytes(i.Area)));
        if (totalBytes > Document.MaxLayerBytes)
            throw new InvalidDataException("도면 객체의 미리보기가 메모리 한도를 초과합니다. 긴 변 크기를 줄이거나 ‘레이어별’ 또는 ‘하나로’를 선택해 주세요.");
        var doc = new Document { Name = Path.GetFileNameWithoutExtension(path), Width = width, Height = height };
        var paper = VectorShapes.Create(new ShapeSpec { Width = width, Height = height, FillArgb = 0xFFFFFFFF }); paper.Name = "도면 배경"; paper.Locked = true; doc.Layers.Add(paper);
        long retainedVectorBytes = 0;
        Layer RenderObject(PaintObject content, PixelArea area)
        {
            token.ThrowIfCancellationRequested(); var visual = new DrawingVisual();
            int left = area.Left, top = area.Top, pixelWidth = area.Width, pixelHeight = area.Height;
            var localFit = fit; localFit.OffsetX -= left; localFit.OffsetY -= top;
            VectorContent? vector = null;
            if (options.RetainVectors)
            {
                Geometry LocalGeometry(Geometry source) { var g = source.CloneCurrentValue(); var m = g.Transform.Value; m.Append(localFit); g.Transform = new MatrixTransform(m); g.Freeze(); return g; }
                var clips = new Dictionary<Geometry, Geometry>();
                var primitives = content.Marks.Select(mark =>
                {
                    Geometry? clip = null;
                    if (mark.Clip != null && !clips.TryGetValue(mark.Clip, out clip)) clips[mark.Clip] = clip = LocalGeometry(mark.Clip);
                    return new VectorPrimitive(LocalGeometry(mark.Geometry), mark.Color, mark.Fill, 1.2, clip);
                });
                vector = VectorContent.FromPaths(pixelWidth, pixelHeight, primitives);
                retainedVectorBytes += vector.ByteLength;
                if (retainedVectorBytes > VectorContent.MaxDocumentBytes)
                    throw new InvalidDataException("도면의 벡터 원본이 512MiB를 초과합니다. 필요한 객체만 별도 도면으로 저장해 주세요.");
            }
            using (var drawing = visual.RenderOpen())
            {
                drawing.PushTransform(new MatrixTransform(localFit));
                Geometry? activeClip = null;
                var styles = new Dictionary<Color, (Brush Brush, Pen Pen)>();
                foreach (var mark in content.Marks)
                {
                    if (!ReferenceEquals(activeClip, mark.Clip)) { if (activeClip != null) drawing.Pop(); activeClip = mark.Clip; if (activeClip != null) drawing.PushClip(activeClip); }
                    if (!styles.TryGetValue(mark.Color, out var style)) { var brush = new SolidColorBrush(mark.Color); brush.Freeze(); var pen = new Pen(brush, 1.2 / scale); pen.Freeze(); style = (brush, pen); styles.Add(mark.Color, style); }
                    drawing.DrawGeometry(mark.Fill ? style.Brush : null, mark.Fill ? null : style.Pen, mark.Geometry);
                }
                if (activeClip != null) drawing.Pop();
                drawing.Pop();
            }
            var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze();
            return new Layer { Name = content.Name, Pixels = Raster.FromBitmap(bitmap), X = left, Y = top, Vector = vector, Kind = vector == null ? LayerKind.Raster : LayerKind.Vector };
        }
        var groupCanvas = separateObjects ? new Raster(width, height) : null;
        foreach (var plan in plans)
        {
            Layer? folder = null;
            if (separateObjects)
            {
                // Full document bounds allow an individual child to move anywhere on
                // the canvas without being clipped to its original CAD layer bounds.
                folder = new Layer { Name = plan.Group.Name, Kind = LayerKind.Group, Pixels = groupCanvas! };
                doc.Layers.Add(folder);
            }
            foreach (var item in plan.Items)
            {
                var layer = RenderObject(item.Object, item.Area);
                if (folder != null) layer.ParentId = folder.Id;
                doc.Layers.Add(layer);
            }
        }
        // Preflight and incremental source budgets above permit linear assembly;
        // repeated Document.Add validation would make large CAD drawings quadratic.
        doc.ActiveId = doc.Layers[^1].Id;
        token.ThrowIfCancellationRequested(); doc.Validate();
        if (readerNotices != 0) warnings.Add($"도면을 읽는 과정에서 {readerNotices}건의 호환성 안내가 발생했습니다. 일부 객체나 부가 정보가 생략될 수 있으니 미리보기를 원본과 비교해 주세요.");
        if (unsupported.Count != 0) warnings.Add("표시하지 못한 객체: " + string.Join(", ", unsupported.OrderBy(p => p.Key).Take(15).Select(p => $"{p.Key} {p.Value}개")));
        warnings.Add($"‘{spaceName}’을 가져왔습니다" + (references.Count > 1 ? $" · 외부참조 {references.Count - 1}개 읽음." : ".") + (options.RetainVectors ? " 벡터 경로를 보존하며 디자인 모드에서 확대 배율에 맞춰 그립니다." : "픽셀 이미지로 가져왔습니다.") + " 도면 단위·실측 축척·CTB 선종류/선굵기는 보존하지 않습니다.");
        return new(doc, warnings.ToArray());
    }
    static string ObjectLabel(Entity entity) => entity switch
    {
        Line => "선", LwPolyline p => p.IsClosed ? "닫힌 폴리라인" : "폴리라인",
        Polyline2D p => p.IsClosed ? "닫힌 폴리라인" : "폴리라인", Polyline3D => "3D 폴리라인",
        Arc => "호", Circle => "원", ACadSharp.Entities.Ellipse => "타원", Spline => "스플라인",
        Dimension => "치수", Hatch => "해치", TextEntity or MText => "문자", Solid => "면", ACadSharp.Entities.Point => "점",
        _ => entity.ObjectName
    };
    static void EncodingRegister() => System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
}
