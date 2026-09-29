using System.IO;
using System.Windows;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.UniversalAccessibility;

namespace Compositor.Windows;

/// <summary>
/// One-page PDF (also used for PDF-compatible .ai files) that keeps lines, text, shapes and
/// photos in their vector or original-resolution form, drawing only parts with masks,
/// clipping, opacity, blend modes, perspective or adjustments as document-resolution images
/// of their visible result. With layers on, every top-level layer or group becomes a PDF
/// optional-content layer (OCG) that PDF viewers and design apps can switch on and off; a
/// wrapped drawing file contributes one PDF layer per drawing layer. Without layers the same
/// drawing forms a single flat page.
/// </summary>
internal static class PdfLayerExport
{
    // Readers (including this app's importer) accept at most 127 layers next to the paper.
    internal const int MaxLayers = Document.MaxLayers - 1;

    internal enum Role { Content, Adjusted, Clipped, Empty }

    internal sealed record Unit(Layer Source, Layer[] Ancestors, Layer[] Siblings, int Index, Role Role, Layer? ClipBase, bool Visible);

    /// <summary>What the export will contain; built without rendering so dialogs can explain it.</summary>
    internal sealed record Plan(IReadOnlyList<Unit> Units, bool Layered, bool Expanded, int VectorParts, int ImageParts, int HiddenLayers, bool OmitsHiddenChildren);

    static bool CleanLeaf(Layer layer) => layer.Kind is LayerKind.Raster or LayerKind.Text or LayerKind.Shape or LayerKind.Vector &&
        layer.Opacity == 1 && layer.Blend == BlendMode.Normal && layer.Mask == null && !layer.Clipped && layer.Warp == null;
    static bool CleanGroup(Layer layer, Dictionary<Guid, Layer[]> children) => layer.Kind == LayerKind.Group &&
        layer.Opacity == 1 && layer.Blend == BlendMode.Normal && layer.Mask == null && !layer.Clipped && layer.Warp == null &&
        !LayerExportRender.ChildrenOf(layer, children).Any(child => child.Kind == LayerKind.Adjustment);
    static bool Expandable(Layer root, Dictionary<Guid, Layer[]> children) => DrawingLayers.IsContainer(root) && root.Visible &&
        root.Opacity == 1 && root.Blend == BlendMode.Normal && !root.Clipped && CleanGroup(root, children) && LayerExportRender.ChildrenOf(root, children).Length > 0;

    public static Plan Build(Document document, bool layered)
    {
        document.Validate();
        var children = LayerExportRender.Children(document); var roots = LayerExportRender.Roots(document);
        List<Unit> Units(bool expand)
        {
            var units = new List<Unit>();
            void Add(Layer[] stack, int i, Layer[] ancestors)
            {
                var layer = stack[i]; var role = layer.Kind == LayerKind.Adjustment ? Role.Adjusted : Role.Content; Layer? basis = null;
                if (layer.Clipped)
                {
                    int b = i - 1; while (b >= 0 && stack[b].Clipped) b--;
                    if (b >= 0 && stack[b].Kind != LayerKind.Adjustment && layer.Kind != LayerKind.Adjustment) { role = Role.Clipped; basis = stack[b]; } else role = Role.Empty;
                }
                units.Add(new Unit(layer, ancestors, stack, i, role, basis, layer.Visible && role != Role.Empty && (basis?.Visible ?? true)));
            }
            for (int i = 0; i < roots.Length; i++)
            {
                var root = roots[i];
                // Only a standalone drawing file opens up; a clipping base or clipped layer stays whole.
                bool clippedAbove = i + 1 < roots.Length && roots[i + 1].Clipped;
                if (expand && !root.Clipped && !clippedAbove && Expandable(root, children))
                {
                    var kids = LayerExportRender.ChildrenOf(root, children);
                    for (int k = 0; k < kids.Length; k++) Add(kids, k, [root]);
                }
                else Add(roots, i, []);
            }
            return units;
        }
        var result = Units(layered);
        bool expanded = layered && result.Any(u => u.Ancestors.Length > 0);
        if (layered && result.Count > MaxLayers && expanded) { result = Units(false); expanded = false; }
        if (layered && result.Count > MaxLayers)
            throw new InvalidDataException($"PDF 레이어가 {result.Count:N0}개가 됩니다. PDF 레이어는 {MaxLayers}개까지 만들 수 있으니 레이어를 그룹으로 묶거나 ‘PDF · 한 장으로 합치기’를 선택해 주세요.");
        int vector = 0, image = 0; bool omitted = false;
        void Classify(Layer layer, bool root)
        {
            if (!root && !layer.Visible) { omitted = true; return; }
            if (layer.Opacity <= 0) return;
            if (CleanLeaf(layer)) { if (layer.Kind == LayerKind.Raster) image++; else vector++; return; }
            if (!CleanGroup(layer, children)) { image++; return; }
            var kids = LayerExportRender.ChildrenOf(layer, children);
            for (int i = 0; i < kids.Length; i++)
            {
                if (kids[i].Clipped) continue;
                int end = i + 1; while (end < kids.Length && kids[end].Clipped) end++;
                if (kids[i].Visible && kids[i].Opacity > 0 && kids[(i + 1)..end].Any(c => c.Visible && c.Opacity > 0)) image++; else Classify(kids[i], false);
                i = end - 1;
            }
        }
        foreach (var unit in result)
        {
            if (!layered && !unit.Visible) continue;
            if (unit.Role == Role.Content) Classify(unit.Source, layered); else if (unit.Role != Role.Empty) image++;
        }
        return new Plan(result, layered, expanded, vector, image, layered ? result.Count(u => !u.Visible) : 0, omitted);
    }

    public static void Write(Document document, Stream output, bool layered, Action<int, int>? progress = null, CancellationToken token = default)
    {
        var plan = Build(document, layered); var children = LayerExportRender.Children(document);
        double scale = 72d / document.Dpi;
        using var pdf = new PdfDocument();
        pdf.Info.Title = document.Name; pdf.Info.Creator = "Morupixel";
        var page = pdf.AddPage(); page.Width = XUnit.FromPoint(document.Width * scale); page.Height = XUnit.FromPoint(document.Height * scale);
        var blendStates = new Dictionary<BlendMode, string>();
        using (var graphics = XGraphics.FromPdfPage(page))
        {
            // PDFsharp writes its page set-up lazily. Flush it so the raw layer operators stay balanced.
            var first = graphics.Save(); graphics.Restore(first);
            var content = graphics.Internals.ContentStringBuilder ?? throw new InvalidOperationException("PDF 내용을 만들 수 없습니다.");
            void Raw(string text) { PdfRendererExtensions.BeginGraphicMode(graphics); content.Append(text); }

            void Vector(Layer layer, Layer[] ancestors)
            {
                var state = graphics.Save(); graphics.ScaleTransform(scale);
                graphics.IntersectClip(new XRect(0, 0, document.Width, document.Height));
                foreach (var ancestor in ancestors)
                {
                    VectorPdfExport.Transform(graphics, ancestor.Matrix);
                    if (!DrawingLayers.IsContainer(ancestor)) graphics.IntersectClip(new XRect(0, 0, ancestor.Pixels.Width, ancestor.Pixels.Height));
                }
                VectorPdfExport.Transform(graphics, layer.Matrix); graphics.IntersectClip(new XRect(0, 0, layer.Pixels.Width, layer.Pixels.Height));
                VectorPdfExport.DrawContent(graphics, layer, token);
                graphics.Restore(state);
            }
            void Image(Layer[] members, Layer[] ancestors, Int32Rect region, BlendMode blend, Layer? clipBase)
            {
                if (region.Width <= 0 || region.Height <= 0 || members.Length == 0) return;
                var raster = LayerExportRender.Render(document, ancestors, members, children, region, token);
                if (clipBase != null) LayerExportRender.MultiplyAlpha(raster, LayerExportRender.Render(document, ancestors, [LayerExportRender.Plain(clipBase, keepMask: true)], children, region, token), token);
                var local = LayerExportRender.Opaque(raster, token); if (local.Width == 0) return;
                using var encoded = new MemoryStream(); LayerExportRender.Crop(raster, local).WritePng(encoded); encoded.Position = 0;
                using var picture = XImage.FromStream(encoded);
                string? state = null;
                if (blend != BlendMode.Normal)
                {
                    if (!blendStates.TryGetValue(blend, out state)) blendStates[blend] = state = "MoruBlend" + (int)blend;
                    Raw($"q /{state} gs\n");
                }
                var saved = graphics.Save(); graphics.ScaleTransform(scale);
                graphics.DrawImage(picture, region.X + local.X, region.Y + local.Y, local.Width, local.Height);
                graphics.Restore(saved);
                if (state != null) Raw("Q\n");
            }
            // Layer's own look (opacity and mask included) without its blend mode, which the PDF applies.
            static Layer Own(Layer layer) => LayerExportRender.Plain(layer, keepMask: true, keepOpacity: true);
            void Draw(Layer layer, Layer[] ancestors, bool root)
            {
                token.ThrowIfCancellationRequested();
                if (!root && !layer.Visible || layer.Opacity <= 0) return;
                if (CleanLeaf(layer)) { Vector(layer, ancestors); return; }
                if (!CleanGroup(layer, children)) { Image([Own(layer)], ancestors, LayerExportRender.Footprint(document, layer, ancestors), layer.Blend, null); return; }
                var kids = LayerExportRender.ChildrenOf(layer, children); Layer[] next = [.. ancestors, layer];
                for (int i = 0; i < kids.Length; i++)
                {
                    var kid = kids[i]; if (kid.Clipped) continue;
                    int end = i + 1; while (end < kids.Length && kids[end].Clipped) end++;
                    var clips = kids[(i + 1)..end];
                    // A clipping stack is one image: the base with its clipped layers merged in.
                    if (kid.Visible && kid.Opacity > 0 && clips.Any(c => c.Visible && c.Opacity > 0))
                        Image([Own(kid), .. clips], next, LayerExportRender.Footprint(document, kid, next), kid.Blend, null);
                    else Draw(kid, next, false);
                    i = end - 1;
                }
            }
            for (int index = 0; index < plan.Units.Count; index++)
            {
                token.ThrowIfCancellationRequested(); var unit = plan.Units[index]; var layer = unit.Source;
                if (layered) Raw($"/OC /MoruLayer{index} BDC\n");
                if (layered || unit.Visible)
                    switch (unit.Role)
                    {
                        case Role.Content: Draw(layer, unit.Ancestors, layered); break;
                        case Role.Clipped: Image([Own(layer)], unit.Ancestors, LayerExportRender.Footprint(document, unit.ClipBase!, unit.Ancestors), layer.Blend, unit.ClipBase); break;
                        case Role.Adjusted:
                            // The adjusted look of everything below it in the same stack.
                            var shown = layer.Snapshot(); shown.Visible = true; shown.Clipped = false;
                            Image([.. unit.Siblings.Take(unit.Index), shown], unit.Ancestors, LayerExportRender.StackArea(document, unit.Ancestors), BlendMode.Normal, null); break;
                    }
                if (layered) Raw("EMC\n");
                progress?.Invoke(index + 1, plan.Units.Count);
            }
        }
        if (blendStates.Count > 0)
        {
            var states = page.Resources.Elements.GetDictionary("/ExtGState");
            if (states == null) { states = new PdfDictionary(pdf); page.Resources.Elements["/ExtGState"] = states; }
            foreach (var (blend, name) in blendStates)
            {
                var state = new PdfDictionary(pdf); state.Elements["/Type"] = new PdfName("/ExtGState"); state.Elements["/BM"] = new PdfName("/" + blend);
                states.Elements["/" + name] = state;
            }
        }
        if (layered && plan.Units.Count > 0)
        {
            var properties = new PdfDictionary(pdf); var all = new PdfArray(pdf); var order = new PdfArray(pdf);
            var on = new PdfArray(pdf); var off = new PdfArray(pdf); var locked = new PdfArray(pdf);
            for (int index = 0; index < plan.Units.Count; index++)
            {
                var unit = plan.Units[index]; var group = new PdfDictionary(pdf);
                group.Elements["/Type"] = new PdfName("/OCG"); group.Elements["/Name"] = new PdfString(unit.Source.Name, PdfStringEncoding.Unicode);
                pdf.Internals.AddObject(group); var reference = group.Reference!;
                properties.Elements["/MoruLayer" + index] = reference; all.Elements.Add(reference);
                (unit.Visible ? on : off).Elements.Add(reference); if (unit.Source.Locked) locked.Elements.Add(reference);
            }
            // The layers panel lists the front-most layer first.
            for (int index = plan.Units.Count - 1; index >= 0; index--) order.Elements.Add(all.Elements[index]);
            page.Resources.Elements["/Properties"] = properties;
            var configuration = new PdfDictionary(pdf);
            configuration.Elements["/Name"] = new PdfString(document.Name, PdfStringEncoding.Unicode);
            configuration.Elements["/BaseState"] = new PdfName("/ON"); configuration.Elements["/Order"] = order;
            configuration.Elements["/ON"] = on; configuration.Elements["/OFF"] = off;
            if (locked.Elements.Count > 0) configuration.Elements["/Locked"] = locked;
            var optional = new PdfDictionary(pdf); optional.Elements["/OCGs"] = all; optional.Elements["/D"] = configuration;
            pdf.Internals.Catalog.Elements["/OCProperties"] = optional;
            pdf.PageMode = PdfPageMode.UseOC;
            if (pdf.Version < 15) pdf.Version = 15;
        }
        token.ThrowIfCancellationRequested();
        pdf.Save(output, false);
    }
}
