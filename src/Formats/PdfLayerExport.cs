using System.IO;
using System.Windows;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.UniversalAccessibility;

namespace Compositor.Windows;

/// <summary>
/// One-page PDF (also used for PDF-compatible .ai files) that keeps lines, text, shapes and
/// photos in their vector or original-resolution form, drawing only parts with masks,
/// clipping, opacity, blend modes, perspective or adjustments as document-resolution images
/// of their visible result. With layers on, every top-level layer or group becomes a PDF
/// optional-content layer (OCG) that PDF viewers and design apps can switch on and off; a
/// wrapped drawing file contributes one PDF layer per drawing layer. Without layers the same
/// drawing forms a single flat page. A group whose children use blend modes, and a clipping stack
/// on a translucent or blended base, become isolated PDF transparency groups so they blend exactly
/// as on the canvas.
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
        !LayerExportRender.ChildrenOf(layer, children).Any(child => child.Kind == LayerKind.Adjustment || DesignRenderer.IsPassThrough(child));
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
                // A pass-through folder (a design style) changes what is below it, like an adjustment layer.
                var layer = stack[i]; var role = layer.Kind == LayerKind.Adjustment || DesignRenderer.IsPassThrough(layer) ? Role.Adjusted : Role.Content; Layer? basis = null;
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
        // Optional-content groups exist before drawing so isolated groups can refer to them too.
        var layerGroups = new List<PdfReference>();
        if (layered)
            foreach (var unit in plan.Units)
            {
                var group = new PdfDictionary(pdf);
                group.Elements["/Type"] = new PdfName("/OCG"); group.Elements["/Name"] = new PdfString(unit.Source.Name, PdfStringEncoding.Unicode);
                pdf.Internals.AddObject(group); layerGroups.Add(group.Reference!);
            }
        using (var pageGraphics = XGraphics.FromPdfPage(page))
        {
            // A soft mask that keeps everything (white luminosity over the page). Some PDF renderers,
            // including the Windows one this app imports with, only honour a group's isolation when
            // the group has to be composited on its own; this neutral mask makes them do so.
            PdfReference? neutralMask = null;
            PdfReference NeutralMask()
            {
                if (neutralMask != null) return neutralMask;
                double w = page.Width.Point, h = page.Height.Point; var culture = System.Globalization.CultureInfo.InvariantCulture;
                var mask = new PdfDictionary(pdf);
                mask.Elements["/Type"] = new PdfName("/XObject"); mask.Elements["/Subtype"] = new PdfName("/Form");
                var box = new PdfArray(pdf); foreach (double v in new[] { 0, 0, w, h }) box.Elements.Add(new PdfReal(v)); mask.Elements["/BBox"] = box;
                var group = new PdfDictionary(pdf); group.Elements["/S"] = new PdfName("/Transparency"); group.Elements["/CS"] = new PdfName("/DeviceRGB"); mask.Elements["/Group"] = group;
                mask.Elements["/Resources"] = new PdfDictionary(pdf);
                mask.CreateStream(System.Text.Encoding.ASCII.GetBytes(string.Format(culture, "1 1 1 rg 0 0 {0:0.####} {1:0.####} re f", w, h)));
                pdf.Internals.AddObject(mask); return neutralMask = mask.Reference!;
            }
            var pageSurface = new Surface(pageGraphics, NeutralMask);
            // Draws into a transparency group of its own, like the editor renders a group on a fresh
            // surface: blend modes inside only see the group's content, never the page below it. The
            // group is then placed with the given opacity and blend mode.
            void Isolated(Surface target, double opacity, BlendMode blend, Action<Surface> body)
            {
                var form = new XForm(pdf, page.Width, page.Height); Surface inner;
                using (var formGraphics = XGraphics.FromForm(form)) { inner = new Surface(formGraphics, NeutralMask); body(inner); }
                var formObject = FormObject(form);
                var isolation = new PdfDictionary(pdf);
                isolation.Elements["/Type"] = new PdfName("/Group"); isolation.Elements["/S"] = new PdfName("/Transparency");
                isolation.Elements["/CS"] = new PdfName("/DeviceRGB"); isolation.Elements["/I"] = new PdfBoolean(true);
                formObject.Elements["/Group"] = isolation;
                var resources = formObject.Elements.GetDictionary("/Resources");
                if (resources == null) { resources = new PdfDictionary(pdf); formObject.Elements["/Resources"] = resources; }
                inner.Finish(pdf, resources);
                target.Place(form, target.State(blend, opacity, isolate: true));
            }

            void Vector(Surface surface, Layer layer, Layer[] ancestors)
            {
                var graphics = surface.Graphics;
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
            void Image(Surface surface, Layer[] members, Layer[] ancestors, Int32Rect region, BlendMode blend, Layer? clipBase)
            {
                if (region.Width <= 0 || region.Height <= 0 || members.Length == 0) return;
                var raster = LayerExportRender.Render(document, ancestors, members, children, region, token);
                if (clipBase != null) LayerExportRender.MultiplyAlpha(raster, LayerExportRender.Render(document, ancestors, [LayerExportRender.Plain(clipBase, keepMask: true)], children, region, token), token);
                var local = LayerExportRender.Opaque(raster, token); if (local.Width == 0) return;
                using var encoded = new MemoryStream(); LayerExportRender.Crop(raster, local).WritePng(encoded); encoded.Position = 0;
                using var picture = XImage.FromStream(encoded);
                string? state = surface.State(blend, 1);
                if (state != null) surface.Raw($"q /{state} gs\n");
                var graphics = surface.Graphics; var saved = graphics.Save(); graphics.ScaleTransform(scale);
                graphics.DrawImage(picture, region.X + local.X, region.Y + local.Y, local.Width, local.Height);
                graphics.Restore(saved);
                if (state != null) surface.Raw("Q\n");
            }
            // Layer's own look (opacity and mask included) without its blend mode, which the PDF applies.
            static Layer Own(Layer layer) => LayerExportRender.Plain(layer, keepMask: true, keepOpacity: true);
            void Draw(Surface surface, Layer layer, Layer[] ancestors, bool root)
            {
                token.ThrowIfCancellationRequested();
                if (!root && !layer.Visible || layer.Opacity <= 0) return;
                if (CleanLeaf(layer)) { Vector(surface, layer, ancestors); return; }
                if (!CleanGroup(layer, children)) { Image(surface, [Own(layer)], ancestors, LayerExportRender.Footprint(document, layer, ancestors), layer.Blend, null); return; }
                var kids = LayerExportRender.ChildrenOf(layer, children); Layer[] next = [.. ancestors, layer];
                void Kids(Surface inside)
                {
                    for (int i = 0; i < kids.Length; i++)
                    {
                        var kid = kids[i]; if (kid.Clipped) continue;
                        int end = i + 1; while (end < kids.Length && kids[end].Clipped) end++;
                        var clips = kids[(i + 1)..end];
                        // A clipping stack is one image: the base with its clipped layers merged in.
                        if (kid.Visible && kid.Opacity > 0 && clips.Any(c => c.Visible && c.Opacity > 0))
                            Image(inside, [Own(kid), .. clips], next, LayerExportRender.Footprint(document, kid, next), kid.Blend, null);
                        else Draw(inside, kid, next, false);
                        i = end - 1;
                    }
                }
                if (BlendsInside(layer, children)) Isolated(surface, 1, BlendMode.Normal, Kids); else Kids(surface);
            }
            void Unit(Surface surface, int index, Layer? shownAs = null)
            {
                token.ThrowIfCancellationRequested(); var unit = plan.Units[index]; var layer = shownAs ?? unit.Source;
                if (layered) surface.BeginLayer(index, layerGroups[index]);
                if (layered || unit.Visible)
                    switch (unit.Role)
                    {
                        case Role.Content: Draw(surface, layer, unit.Ancestors, layered); break;
                        case Role.Clipped: Image(surface, [Own(layer)], unit.Ancestors, LayerExportRender.Footprint(document, unit.ClipBase!, unit.Ancestors), layer.Blend, unit.ClipBase); break;
                        case Role.Adjusted:
                            // The adjusted look of everything below it in the same stack.
                            var shown = layer.Snapshot(); shown.Visible = true; shown.Clipped = false;
                            Image(surface, [.. unit.Siblings.Take(unit.Index), shown], unit.Ancestors, LayerExportRender.StackArea(document, unit.Ancestors), BlendMode.Normal, null); break;
                    }
                if (layered) surface.Raw("EMC\n");
                progress?.Invoke(index + 1, plan.Units.Count);
            }
            // Units from..to share their ancestors. A clipping stack whose base is translucent or blended
            // is one transparency group placed with the base's opacity and blend mode, as the editor
            // merges the clipped layers into the base first; each part keeps its own PDF layer inside.
            void Units(Surface surface, int from, int to)
            {
                for (int i = from; i < to; i++)
                {
                    var unit = plan.Units[i];
                    int end = i + 1; while (end < to && plan.Units[end].Role == Role.Clipped && plan.Units[end].ClipBase == unit.Source) end++;
                    if (unit.Role == Role.Content && end > i + 1 && (unit.Source.Opacity < 1 || unit.Source.Blend != BlendMode.Normal))
                    {
                        var basis = unit.Source.Snapshot(); basis.Opacity = 1; basis.Blend = BlendMode.Normal;
                        int first = i, last = end;
                        Isolated(surface, unit.Source.Opacity, unit.Source.Blend, inside => { Unit(inside, first, basis); for (int k = first + 1; k < last; k++) Unit(inside, k); });
                        i = end - 1; continue;
                    }
                    Unit(surface, i);
                }
            }
            for (int index = 0; index < plan.Units.Count;)
            {
                // An opened drawing folder's layers are drawn inside the folder's own group when one of
                // them blends, so hatch materials multiply only with the drawing, as on the canvas.
                var owner = plan.Units[index].Ancestors.Length > 0 ? plan.Units[index].Ancestors[0] : null;
                int end = index + 1;
                while (end < plan.Units.Count && (plan.Units[end].Ancestors.Length > 0 ? plan.Units[end].Ancestors[0] : null) == owner) end++;
                int from = index, to = end;
                if (owner != null && BlendsInside(owner, children)) Isolated(pageSurface, 1, BlendMode.Normal, inside => Units(inside, from, to));
                else Units(pageSurface, from, to);
                index = end;
            }
            pageSurface.Finish(pdf, page.Resources);
        }
        if (layered && plan.Units.Count > 0)
        {
            var properties = new PdfDictionary(pdf); var all = new PdfArray(pdf); var order = new PdfArray(pdf);
            var on = new PdfArray(pdf); var off = new PdfArray(pdf); var locked = new PdfArray(pdf);
            for (int index = 0; index < plan.Units.Count; index++)
            {
                var unit = plan.Units[index]; var reference = layerGroups[index];
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

    // A child drawn with its own blend mode (a clipping stack's base included) must only see its
    // group's content; the editor renders every group on a fresh surface.
    static bool BlendsInside(Layer group, Dictionary<Guid, Layer[]> children) =>
        LayerExportRender.ChildrenOf(group, children).Any(child => !child.Clipped && child.Blend != BlendMode.Normal && child.Opacity > 0);

    // PDFsharp 6 keeps a form's PDF object internal; the isolation flag and the raw operators'
    // resources must be written onto it. The self-tests read the result back.
    static readonly System.Reflection.PropertyInfo FormProperty = typeof(XForm).GetProperty("PdfForm", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
        ?? throw new InvalidOperationException("PDF 그룹을 만들 수 없습니다.");
    static PdfDictionary FormObject(XForm form) => FormProperty.GetValue(form) as PdfDictionary ?? throw new InvalidOperationException("PDF 그룹을 만들 수 없습니다.");

    /// <summary>One content stream being drawn (the page or an isolated group) and the resources its raw operators use.</summary>
    sealed class Surface
    {
        readonly Dictionary<(BlendMode Blend, byte Opacity, bool Isolate), string> states = [];
        readonly Func<PdfReference> neutralMask;
        readonly Dictionary<int, PdfReference> layers = [];
        readonly System.Text.StringBuilder content;
        public XGraphics Graphics { get; }
        public Surface(XGraphics graphics, Func<PdfReference> neutralMask)
        {
            Graphics = graphics; this.neutralMask = neutralMask;
            // PDFsharp writes its set-up lazily. Flush it so the raw layer operators stay balanced.
            var first = graphics.Save(); graphics.Restore(first);
            content = graphics.Internals.ContentStringBuilder ?? throw new InvalidOperationException("PDF 내용을 만들 수 없습니다.");
        }
        public void Raw(string text) { PdfRendererExtensions.BeginGraphicMode(Graphics); content.Append(text); }
        /// <summary>The graphics state for a blend mode and opacity (and the neutral mask for an isolated group), or null when drawing plainly.</summary>
        public string? State(BlendMode blend, double opacity, bool isolate = false)
        {
            byte alpha = Imaging.Byte(opacity * 255);
            if (blend == BlendMode.Normal && alpha == 255 && !isolate) return null;
            if (!states.TryGetValue((blend, alpha, isolate), out var name))
                states[(blend, alpha, isolate)] = name = "MoruBlend" + (int)blend + (alpha == 255 ? "" : "o" + alpha) + (isolate ? "i" : "");
            return name;
        }
        /// <summary>
        /// Draws an isolated group with a blend state. PDFsharp resets the fill opacity with its own
        /// state right before painting, so the state is set again just before the group is painted.
        /// </summary>
        public void Place(XForm form, string? state)
        {
            if (state == null) { Graphics.DrawImage(form, 0, 0); return; }
            Raw("q\n"); int start = content.Length;
            Graphics.DrawImage(form, 0, 0);
            string drawn = content.ToString(start, content.Length - start);
            int paint = drawn.LastIndexOf(" Do Q", StringComparison.Ordinal), line = paint < 0 ? -1 : drawn.LastIndexOf("q ", paint, StringComparison.Ordinal);
            if (line < 0) throw new InvalidOperationException("PDF 그룹을 만들 수 없습니다.");
            content.Insert(start + line, $"/{state} gs\n");
            Raw("Q\n");
        }
        public void BeginLayer(int index, PdfReference group) { layers[index] = group; Raw($"/OC /MoruLayer{index} BDC\n"); }
        /// <summary>Adds the blend states and PDF layers used by the raw operators to the stream's resources.</summary>
        public void Finish(PdfDocument pdf, PdfDictionary resources)
        {
            PdfDictionary Entry(string key)
            {
                var dictionary = resources.Elements.GetDictionary(key);
                if (dictionary == null) { dictionary = new PdfDictionary(pdf); resources.Elements[key] = dictionary; }
                return dictionary;
            }
            if (states.Count > 0)
            {
                var dictionary = Entry("/ExtGState");
                foreach (var ((blend, alpha, isolate), name) in states)
                {
                    var state = new PdfDictionary(pdf); state.Elements["/Type"] = new PdfName("/ExtGState"); state.Elements["/BM"] = new PdfName("/" + blend);
                    if (alpha != 255) { state.Elements["/ca"] = new PdfReal(alpha / 255d); state.Elements["/CA"] = new PdfReal(alpha / 255d); }
                    if (isolate)
                    {
                        var mask = new PdfDictionary(pdf); mask.Elements["/Type"] = new PdfName("/Mask"); mask.Elements["/S"] = new PdfName("/Luminosity");
                        mask.Elements["/G"] = neutralMask(); state.Elements["/SMask"] = mask;
                    }
                    dictionary.Elements["/" + name] = state;
                }
            }
            if (layers.Count > 0)
            {
                var properties = Entry("/Properties");
                foreach (var (index, group) in layers) properties.Elements["/MoruLayer" + index] = group;
            }
        }
    }
}
