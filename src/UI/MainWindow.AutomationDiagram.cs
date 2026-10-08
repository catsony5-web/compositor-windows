using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

// AI connection for diagram shapes: add_shape lines and curves, update_shape for every shape kind and
// add_callout (MainWindow.Automation.cs routes the commands here).
public sealed partial class MainWindow
{
    static StrokeDash AutomationDash(JsonObject args, StrokeDash fallback) => args.ContainsKey("dash") ? AString(args, "dash") switch
    {
        "dotted" => StrokeDash.Dotted, "dashed" => StrokeDash.Dashed, "dash_dot" => StrokeDash.DashDot, _ => StrokeDash.Solid
    } : fallback;
    static StrokeCap AutomationCap(JsonObject args, StrokeCap fallback) => args.ContainsKey("cap") ? AString(args, "cap") switch
    {
        "square" => StrokeCap.Square, "flat" => StrokeCap.Flat, _ => StrokeCap.Round
    } : fallback;
    static LineMark AutomationMark(JsonObject args, string name, LineMark fallback) => args.ContainsKey(name) ? AString(args, name) switch
    {
        "arrow" => LineMark.Arrow, "open_arrow" => LineMark.OpenArrow, "dot" => LineMark.Dot, "ring" => LineMark.Ring, "bar" => LineMark.Bar, _ => LineMark.None
    } : fallback;
    static ShapeSpec AutomationStrokeStyle(JsonObject args, ShapeSpec spec) =>
        spec with { Dash = AutomationDash(args, spec.Dash), DashScale = ANumber(args, "dashScale", spec.DashScale), Cap = AutomationCap(args, spec.Cap) };
    static double AutomationMarkSize(double strokeWidth) => Math.Min(ShapeSpec.MaxMarkSize, Math.Max(10, strokeWidth * 5));

    /// <summary>add_shape with shape=line or curve: points in parent pixels, offset by x/y.</summary>
    internal static ShapeSpec AutomationLine(JsonObject args)
    {
        var points = AutomationCatalog.MaterialPoints(args["points"]!.AsArray());
        double width = ANumber(args, "strokeWidth", 2); bool closed = ABool(args, "closed");
        var spec = new ShapeSpec
        {
            Kind = ShapeKind.Line, Points = new ShapePoints(points), Smooth = AString(args, "shape") == "curve", Closed = closed,
            StrokeEnabled = true, StrokeArgb = VectorShapes.Argb(AColor(args, "stroke", VectorShapes.Color(0xFF20344F))), StrokeWidth = width,
            FillEnabled = closed && args.ContainsKey("fill"), FillArgb = VectorShapes.Argb(AColor(args, "fill", VectorShapes.Color(0xFFBCD9FA))),
            StartMark = AutomationMark(args, "startMark", LineMark.None), EndMark = AutomationMark(args, "endMark", LineMark.None),
            MarkSize = ANumber(args, "markSize", AutomationMarkSize(width))
        };
        return AutomationStrokeStyle(args, spec);
    }

    /// <summary>add_callout: a leader from the target to the label point with its text.</summary>
    internal static ShapeSpec AutomationCallout(JsonObject args, Document document)
    {
        var anchor = new Point(ANumber(args, "anchorX"), ANumber(args, "anchorY"));
        var label = new Point(ANumber(args, "labelX"), ANumber(args, "labelY"));
        var leader = AString(args, "leader", "elbow") == "straight" ? CalloutLeader.Straight : CalloutLeader.Elbow;
        var elbow = args.ContainsKey("elbowX") || args.ContainsKey("elbowY") ? new Point(ANumber(args, "elbowX", anchor.X), ANumber(args, "elbowY", label.Y))
            : leader == CalloutLeader.Elbow ? DiagramEditing.DefaultElbow(anchor, label) : anchor + (label - anchor) / 2;
        var text = AutomationText(args, new TextSpec { Content = "", FontFamily = "Malgun Gothic", FontSize = Math.Clamp(Math.Round(Math.Min(document.Width, document.Height) / 40.0), 12, 160), ColorArgb = 0xFF20344F });
        double width = ANumber(args, "strokeWidth", 2);
        var spec = new ShapeSpec
        {
            Kind = ShapeKind.Callout, Leader = leader, Points = new ShapePoints([anchor, elbow, label]), Label = text,
            StrokeEnabled = true, StrokeArgb = VectorShapes.Argb(AColor(args, "stroke", VectorShapes.Color(text.ColorArgb))), StrokeWidth = width,
            StartMark = AutomationMark(args, "anchorMark", LineMark.Dot), EndMark = AutomationMark(args, "labelMark", LineMark.None),
            MarkSize = ANumber(args, "markSize", AutomationMarkSize(width)),
            FillEnabled = args.ContainsKey("fill"), FillArgb = VectorShapes.Argb(AColor(args, "fill", Colors.White)), CornerRadius = ANumber(args, "cornerRadius", 4)
        };
        return AutomationStrokeStyle(args, spec);
    }

    static readonly string[] LineOnlyArguments = ["points", "closed", "curve", "startMark", "endMark"];
    static readonly string[] CalloutOnlyArguments = ["anchorX", "anchorY", "elbowX", "elbowY", "labelX", "labelY", "leader", "anchorMark", "labelMark",
        "text", "fontFamily", "fontSize", "color", "bold", "italic", "alignment", "lineHeight", "tracking", "boxWidth", "outline", "outlineWidth", "outlineColor", "outlinePosition", "outlineOnly"];

    /// <summary>update_shape: the requested changes on a shape layer's spec, in its own pixels (points arrive in parent pixels).</summary>
    internal static ShapeSpec AutomationShapeUpdate(Layer layer, ShapeSpec current, JsonObject args)
    {
        bool line = current.Kind == ShapeKind.Line, callout = current.Kind == ShapeKind.Callout, box = !current.HasPoints;
        if (!line && LineOnlyArguments.Any(args.ContainsKey))
            throw new AutomationFault("invalid_arguments", "points, closed, curve, startMark and endMark apply to lines and curves.");
        if (!callout && CalloutOnlyArguments.Any(args.ContainsKey))
            throw new AutomationFault("invalid_arguments", "Target, bend and label points, leader, marks and label text apply to callouts.");
        if (!box && (args.ContainsKey("width") || args.ContainsKey("height")))
            throw new AutomationFault("invalid_arguments", "Lines and callouts size themselves from their points; width and height apply to rectangles and ellipses.");
        if (box && args.ContainsKey("markSize")) throw new AutomationFault("invalid_arguments", "markSize applies to lines, curves and callouts.");
        var spec = current with
        {
            FillArgb = args.ContainsKey("fill") ? VectorShapes.Argb(AColor(args, "fill", Colors.Transparent)) : current.FillArgb,
            FillEnabled = args.ContainsKey("fillEnabled") ? ABool(args, "fillEnabled") : args.ContainsKey("fill") || current.FillEnabled,
            StrokeArgb = args.ContainsKey("stroke") ? VectorShapes.Argb(AColor(args, "stroke", Colors.Transparent)) : current.StrokeArgb,
            StrokeEnabled = args.ContainsKey("strokeEnabled") ? ABool(args, "strokeEnabled") : args.ContainsKey("stroke") || current.StrokeEnabled,
            StrokeWidth = ANumber(args, "strokeWidth", current.StrokeWidth), CornerRadius = ANumber(args, "cornerRadius", current.CornerRadius),
            Width = box ? (int)ANumber(args, "width", current.Width) : current.Width, Height = box ? (int)ANumber(args, "height", current.Height) : current.Height,
            MarkSize = ANumber(args, "markSize", current.MarkSize)
        };
        spec = AutomationStrokeStyle(args, spec);
        if (line)
        {
            if (args["points"] is JsonArray points) spec = spec with { Points = new ShapePoints(AutomationCatalog.MaterialPoints(points).Select(layer.Local)) };
            spec = spec with
            {
                Smooth = ABool(args, "curve", spec.Smooth), Closed = ABool(args, "closed", spec.Closed),
                StartMark = AutomationMark(args, "startMark", spec.StartMark), EndMark = AutomationMark(args, "endMark", spec.EndMark)
            };
            if (spec.Closed && spec.Points!.Count < 3) throw new AutomationFault("invalid_arguments", "A closed line needs at least 3 points.");
        }
        if (callout)
        {
            // Only the given points move; the others keep their exact stored values.
            var local = spec.Points!.ToArray(); var parent = local.Select(layer.Document).ToArray();
            Point Given(string x, string y, Point fallback) => new(ANumber(args, x, fallback.X), ANumber(args, y, fallback.Y));
            bool anchorGiven = args.ContainsKey("anchorX") || args.ContainsKey("anchorY"), labelGiven = args.ContainsKey("labelX") || args.ContainsKey("labelY");
            bool elbowGiven = args.ContainsKey("elbowX") || args.ContainsKey("elbowY");
            var leader = args.ContainsKey("leader") ? AString(args, "leader") == "straight" ? CalloutLeader.Straight : CalloutLeader.Elbow : spec.Leader;
            var anchor = anchorGiven ? Given("anchorX", "anchorY", parent[0]) : parent[0];
            var label = labelGiven ? Given("labelX", "labelY", parent[2]) : parent[2];
            if (anchorGiven) local[DiagramEditing.Anchor] = layer.Local(anchor);
            if (labelGiven) local[DiagramEditing.LabelPoint] = layer.Local(label);
            // Without a given bend, a bent leader stays level with its label (or starts that way when it becomes bent).
            if (elbowGiven) local[DiagramEditing.Elbow] = layer.Local(Given("elbowX", "elbowY", parent[1]));
            else if (leader == CalloutLeader.Elbow && spec.Leader != CalloutLeader.Elbow) local[DiagramEditing.Elbow] = layer.Local(DiagramEditing.DefaultElbow(anchor, label));
            else if (leader == CalloutLeader.Elbow && labelGiven) local[DiagramEditing.Elbow] = layer.Local(new Point(parent[1].X, label.Y));
            spec = spec with
            {
                Leader = leader, Points = new ShapePoints(local),
                StartMark = AutomationMark(args, "anchorMark", spec.StartMark), EndMark = AutomationMark(args, "labelMark", spec.EndMark),
                Label = CalloutOnlyArguments.Skip(9).Any(args.ContainsKey) ? AutomationText(args, spec.Label!) : spec.Label
            };
        }
        return spec;
    }
}
