using System.IO;
using System.Text;
using System.Windows.Media;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;

namespace Compositor.Windows;

// PDF optional-content groups are rasterized separately, with their original paint
// order and default visibility. The native renderer still owns fonts, clipping,
// images and colour conversion; no Illustrator installation or Python is required.
internal static class PdfLayerImport
{
    sealed record Definition(string Key, string Name, bool Visible, bool Locked);
    sealed record Run(string Key, int Index);
    static PdfItem? Resolve(PdfItem? item) => item is PdfReference reference ? reference.Value : item;
    static PdfDictionary? Dictionary(PdfItem? item) => Resolve(item) as PdfDictionary;
    static string Key(PdfDictionary dictionary) => dictionary.Reference?.ObjectID.ToString() ?? throw new NotSupportedException("PDF 레이어 식별자가 없습니다.");
    static HashSet<string> Keys(PdfArray? array) => array == null ? [] : array.Elements.Select(Dictionary).Where(d => d != null).Select(d => Key(d!)).ToHashSet();

    static List<Definition> Definitions(PdfSharp.Pdf.PdfDocument pdf)
    {
        var optional = pdf.Internals.Catalog.Elements.GetDictionary("/OCProperties");
        var groups = optional?.Elements.GetArray("/OCGs");
        if (groups == null) return [];
        if (groups.Elements.Count > Document.MaxLayers - 1) throw new InvalidDataException("PDF 레이어가 너무 많습니다. 필요한 레이어만 남기거나 레이어 유지를 꺼 주세요.");
        var config = optional!.Elements.GetDictionary("/D");
        bool baseOn = config?.Elements.GetName("/BaseState") != "/OFF";
        var on = Keys(config?.Elements.GetArray("/ON")); var off = Keys(config?.Elements.GetArray("/OFF")); var locked = Keys(config?.Elements.GetArray("/Locked"));
        var definitions = new List<Definition>();
        foreach (var item in groups.Elements)
        {
            var group = Dictionary(item) ?? throw new InvalidDataException("PDF 레이어 정보가 잘못되었습니다.");
            string key = Key(group), name = group.Elements.GetString("/Name");
            bool visible = on.Contains(key) || baseOn && !off.Contains(key);
            // /AS can override the default configuration for the View event.
            if (config?.Elements.GetArray("/AS") is { } applications)
                foreach (var application in applications.Elements.Select(Dictionary).Where(d => d?.Elements.GetName("/Event") == "/View"))
                {
                    var affected = application!.Elements.GetArray("/OCGs");
                    if (affected != null && !Keys(affected).Contains(key)) continue;
                    string? state = group.Elements.GetDictionary("/Usage")?.Elements.GetDictionary("/View")?.Elements.GetName("/ViewState");
                    if (state == "/OFF") visible = false; else if (state == "/ON") visible = true;
                }
            definitions.Add(new(key, string.IsNullOrWhiteSpace(name) ? $"PDF 레이어 {definitions.Count + 1}" : name, visible, locked.Contains(key)));
        }
        // /Order is the layers panel order (top first), whereas our stack is bottom first.
        var displayOrder = new List<string>();
        void Order(PdfArray array, int depth)
        {
            if (depth > 16) throw new InvalidDataException("PDF 레이어 목록의 중첩 한도를 초과합니다.");
            foreach (var value in array.Elements)
                if (Resolve(value) is PdfArray nested) Order(nested, depth + 1);
                else if (Dictionary(value) is { } group && group.Elements.GetName("/Type") == "/OCG") displayOrder.Add(Key(group));
        }
        if (config?.Elements.GetArray("/Order") is { } order) Order(order, 0);
        displayOrder.Reverse();
        return displayOrder.Count == definitions.Count && displayOrder.Distinct().Count() == definitions.Count && displayOrder.All(key => definitions.Any(d => d.Key == key))
            ? displayOrder.Select(key => definitions.Single(d => d.Key == key)).ToList() : definitions;
    }

    internal static int Count(string path)
    {
        using var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        return pdf.Internals.Catalog.Elements.GetDictionary("/OCProperties")?.Elements.GetArray("/OCGs")?.Elements.Count ?? 0;
    }

    internal static async Task<CompatibilityResult?> ReadAsync(string path, CompatibilityOptions options, CancellationToken token)
    {
        using var inspected = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        var definitions = Definitions(inspected); if (definitions.Count == 0) return null;
        if (options.Page < 1 || options.Page > inspected.PageCount) throw new ArgumentOutOfRangeException(nameof(options), $"페이지는 1~{inspected.PageCount} 범위입니다.");
        if (new FileInfo(path).Length > 512L * 1024 * 1024) throw new InvalidDataException("레이어 PDF는 512MB 이하로 가져와 주세요. 레이어 유지를 끄면 합성 이미지를 사용할 수 있습니다.");
        byte[] source = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        var warnings = new HashSet<string>(); var runs = new List<Run>();
        BuildVariant(inspected, options.Page - 1, null, definitions, runs, warnings, token);
        var used = runs.Select(r => r.Key).ToHashSet();
        int count = 1 + runs.Count + definitions.Count(d => !used.Contains(d.Key));
        if (count > Document.MaxLayers) throw new InvalidDataException("PDF의 그리기 순서를 보존하는 데 필요한 레이어가 128개를 초과합니다. 레이어 유지를 꺼 주세요.");
        var info = await PdfCompatibility.InspectPageAsync(path, options.Page, options.Dpi, token).ConfigureAwait(false);
        if ((long)info.Width * info.Height * 4 * count > Document.MaxLayerBytes)
            throw new InvalidDataException("PDF 레이어 이미지의 메모리 한도를 초과합니다. DPI를 낮춰 주세요.");
        var document = new Document { Name = Path.GetFileNameWithoutExtension(path), Width = info.Width, Height = info.Height, Dpi = options.Dpi };
        var paper = VectorShapes.Create(new ShapeSpec { Width = info.Width, Height = info.Height, FillArgb = 0xFFFFFFFF }); paper.Name = "PDF 용지"; paper.Locked = true; document.Add(paper);
        bool originalOrder = runs.All(r => r.Key.Length > 0) && definitions.Where(d => used.Contains(d.Key)).Select(d => d.Key).SequenceEqual(runs.Select(r => r.Key));
        var pendingEmpty = definitions.Where(d => !used.Contains(d.Key)).ToList();
        void AddEmpty(Definition definition) => document.Add(new Layer { Name = definition.Name, Pixels = new Raster(1, 1), Visible = definition.Visible, Locked = definition.Locked });
        if (!originalOrder) { foreach (var definition in pendingEmpty) AddEmpty(definition); pendingEmpty.Clear(); }
        var occurrences = new Dictionary<string, int>();
        foreach (var run in runs)
        {
            token.ThrowIfCancellationRequested();
            using var input = new MemoryStream(source, false);
            using var variant = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
            BuildVariant(variant, options.Page - 1, run.Index, definitions, [], warnings, token);
            using var encoded = new MemoryStream(); variant.Save(encoded, false); encoded.Position = 0;
            var vector = options.RetainVectors ? VectorContent.FromPdf(info.Width, info.Height, options.Page, encoded) : null;
            var pixels = await PdfCompatibility.RenderPageAsync(encoded, options.Page, info.Width, info.Height, true, token).ConfigureAwait(false);
            var definition = definitions.FirstOrDefault(d => d.Key == run.Key);
            if (originalOrder)
                foreach (var empty in pendingEmpty.Where(d => definitions.IndexOf(d) < definitions.IndexOf(definition!)).ToArray()) { AddEmpty(empty); pendingEmpty.Remove(empty); }
            int number = occurrences[run.Key] = occurrences.GetValueOrDefault(run.Key) + 1;
            string name = definition?.Name ?? "페이지 공통 내용";
            if (runs.Count(r => r.Key == run.Key) > 1) { name += $" · {number}"; warnings.Add("서로 교차해 그려진 레이어는 원래 겹침 순서를 유지하도록 여러 이미지 레이어로 나눴습니다."); }
            document.Add(new Layer { Name = name, Pixels = pixels, Visible = definition?.Visible ?? true, Locked = definition?.Locked ?? false, Vector = vector, Kind = vector == null ? LayerKind.Raster : LayerKind.Vector });
        }
        foreach (var empty in pendingEmpty) AddEmpty(empty);
        warnings.Add($"{inspected.PageCount}페이지 중 {options.Page}페이지 · PDF/AI 레이어 {definitions.Count}개의 이름과 표시 상태를 유지했습니다. " + (options.RetainVectors ? "원본 PDF 벡터를 포함하여 디자인 모드에서 확대 배율에 맞춰 그립니다. 포함된 사진은 원래 해상도를 유지합니다." : "각 레이어를 픽셀 이미지로 가져왔습니다."));
        document.Validate(); return new(document, warnings.ToArray());
    }

    static PdfDictionary CopyDictionary(PdfDictionary source, PdfSharp.Pdf.PdfDocument pdf)
    {
        var copy = new PdfDictionary(pdf);
        foreach (var pair in source.Elements) copy.Elements[pair.Key] = pair.Value;
        return copy;
    }
    static COperator Operator(string name, params CObject[] operands)
    {
        var op = OpCodes.OperatorFromName(name); foreach (var operand in operands) op.Operands.Add(operand); return op;
    }
    static COperator TextMode(int mode) => Operator("Tr", new CInteger { Value = mode });

    static void BuildVariant(PdfSharp.Pdf.PdfDocument pdf, int pageIndex, int? target, List<Definition> definitions, List<Run> runs, HashSet<string> warnings, CancellationToken token)
    {
        var known = definitions.Select(d => d.Key).ToHashSet();
        string Owner(PdfItem? item, string inherited)
        {
            var optional = Dictionary(item); if (optional == null) return inherited;
            if (optional.Elements.GetName("/Type") == "/OCG")
            {
                string key = Key(optional); if (!known.Contains(key)) throw new InvalidDataException("정의되지 않은 PDF 레이어입니다.");
                if (inherited.Length != 0 && inherited != key) throw new NotSupportedException("중첩 가시성 조건을 가진 PDF 레이어입니다. 레이어 유지를 끄면 원본 합성 이미지를 가져올 수 있습니다.");
                return key;
            }
            if (optional.Elements.GetName("/Type") == "/OCMD")
            {
                if (optional.Elements.ContainsKey("/VE")) throw new NotSupportedException("조건식으로 연결된 PDF 레이어는 합성 이미지로 가져와 주세요.");
                var members = Resolve(optional.Elements["/OCGs"]);
                if (members is PdfDictionary single) return Owner(single, inherited);
                if (members is PdfArray array && array.Elements.Count == 1 && optional.Elements.GetName("/P") is not ("/AnyOff" or "/AllOff")) return Owner(array.Elements[0], inherited);
                throw new NotSupportedException("여러 가시성 조건이 연결된 PDF 레이어는 합성 이미지로 가져와 주세요.");
            }
            return inherited;
        }
        int operations = 0, runIndex = -1, formIndex = 0; string? lastOwner = null;
        bool Paint(string owner)
        {
            if (lastOwner != owner) { lastOwner = owner; runs.Add(new(owner, ++runIndex)); }
            return target == runIndex;
        }
        var activeForms = new HashSet<PdfDictionary>();
        (CSequence Content, PdfDictionary Resources) Filter(CSequence source, PdfDictionary sourceResources, string inherited, int depth, int inheritedTextMode = 0)
        {
            if (depth > 16) throw new InvalidDataException("PDF의 중첩 그리기 깊이 한도를 초과합니다.");
            var resources = CopyDictionary(sourceResources, pdf);
            var xobjects = sourceResources.Elements.GetDictionary("/XObject");
            var outputXobjects = xobjects == null ? new PdfDictionary(pdf) : CopyDictionary(xobjects, pdf);
            resources.Elements["/XObject"] = outputXobjects;
            var marked = new Stack<string>(); string owner = inherited;
            var textStates = new Stack<int>(); int textMode = inheritedTextMode;
            var output = new CSequence();
            foreach (var item in source)
            {
                if ((++operations & 1023) == 0) token.ThrowIfCancellationRequested();
                if (operations > 2_000_000) throw new InvalidDataException("PDF의 그리기 명령 한도를 초과합니다.");
                if (item is not COperator op) throw new InvalidDataException("PDF 그리기 명령을 해석하지 못했습니다.");
                switch (op.Name)
                {
                    case "BDC":
                    case "BMC":
                        marked.Push(owner);
                        if (op.Name == "BDC" && op.Operands.Count == 2 && op.Operands[0] is CName tag && tag.Name == "/OC")
                        {
                            if (op.Operands[1] is not CName property) throw new NotSupportedException("인라인 PDF 레이어 조건은 합성 이미지로 가져와 주세요.");
                            var properties = sourceResources.Elements.GetDictionary("/Properties");
                            if (properties == null || !properties.Elements.ContainsKey(property.Name)) throw new InvalidDataException("PDF 레이어 속성을 찾을 수 없습니다.");
                            owner = Owner(properties.Elements[property.Name], owner);
                            output.Add(Operator("BMC", new CName("/Span")));
                        }
                        else output.Add(op);
                        continue;
                    case "EMC":
                        if (marked.Count == 0) throw new InvalidDataException("PDF 레이어 경계가 잘못되었습니다.");
                        owner = marked.Pop(); output.Add(op); continue;
                    case "q": textStates.Push(textMode); output.Add(op); continue;
                    case "Q": textMode = textStates.Count > 0 ? textStates.Pop() : 0; output.Add(op); continue;
                    case "Tr": textMode = ((CInteger)op.Operands[0]).Value; output.Add(op); continue;
                    case "Do":
                    {
                        string name = ((CName)op.Operands[0]).Name;
                        var resource = xobjects == null ? null : Dictionary(xobjects.Elements[name]);
                        if (resource == null) throw new InvalidDataException("PDF 이미지/폼 리소스를 찾을 수 없습니다.");
                        string resourceOwner = Owner(resource.Elements["/OC"], owner);
                        if (resource.Elements.GetName("/Subtype") == "/Form")
                        {
                            if (!activeForms.Add(resource)) throw new InvalidDataException("PDF 폼이 순환 참조합니다.");
                            var filtered = Filter(ContentReader.ReadContent(resource.Stream.UnfilteredValue), resource.Elements.GetDictionary("/Resources") ?? sourceResources, resourceOwner, depth + 1, textMode);
                            activeForms.Remove(resource);
                            var copy = CopyDictionary(resource, pdf); copy.Elements.Remove("/OC"); copy.Elements.Remove("/Filter"); copy.Elements.Remove("/DecodeParms"); copy.Elements.Remove("/Length");
                            copy.Elements["/Resources"] = filtered.Resources; copy.CreateStream(filtered.Content.ToContent()); pdf.Internals.AddObject(copy);
                            string alias = "/MoruLayerForm" + ++formIndex; outputXobjects.Elements[alias] = copy.Reference!; output.Add(Operator("Do", new CName(alias)));
                        }
                        else if (Paint(resourceOwner))
                        {
                            if (resource.Elements.ContainsKey("/OC"))
                            {
                                var copy = CopyDictionary(resource, pdf); copy.Elements.Remove("/OC"); copy.CreateStream(resource.Stream.Value); pdf.Internals.AddObject(copy);
                                string alias = "/MoruLayerImage" + ++formIndex; outputXobjects.Elements[alias] = copy.Reference!; output.Add(Operator("Do", new CName(alias)));
                            }
                            else output.Add(op);
                        }
                        continue;
                    }
                    case "S": case "s": case "f": case "F": case "f*": case "B": case "B*": case "b": case "b*":
                        output.Add(Paint(owner) ? op : Operator("n")); continue;
                    case "Tj": case "TJ": case "'": case "\"":
                        if (Paint(owner)) output.Add(op);
                        else { output.Add(TextMode(textMode >= 4 ? 7 : 3)); output.Add(op); output.Add(TextMode(textMode)); }
                        continue;
                    case "sh": if (Paint(owner)) output.Add(op); continue;
                    case "BI": case "ID": case "EI":
                        throw new NotSupportedException("인라인 이미지가 있는 PDF는 레이어 유지를 끄고 가져와 주세요.");
                    case "gs":
                        var state = sourceResources.Elements.GetDictionary("/ExtGState")?.Elements.GetDictionary(((CName)op.Operands[0]).Name);
                        string? blend = state?.Elements.GetName("/BM");
                        if (!string.IsNullOrEmpty(blend) && blend is not ("/Normal" or "/Compatible")) warnings.Add("PDF의 레이어 사이 혼합 효과는 픽셀 분리 후 원본과 다를 수 있습니다.");
                        output.Add(op); continue;
                    default: output.Add(op); continue;
                }
            }
            if (marked.Count != 0) throw new InvalidDataException("닫히지 않은 PDF 레이어 경계입니다.");
            return (output, resources);
        }
        var page = pdf.Pages[pageIndex];
        var result = Filter(ContentReader.ReadContent(page), page.Elements.GetDictionary("/Resources") ?? new PdfDictionary(pdf), "", 0);
        page.Contents.ReplaceContent(result.Content); page.Elements["/Resources"] = result.Resources;
        if (page.Elements.GetArray("/Annots") is { } annotations)
        {
            var kept = new PdfArray(pdf);
            foreach (var annotation in annotations.Elements)
            {
                var dictionary = Dictionary(annotation); if (dictionary == null) continue;
                if (Paint(Owner(dictionary.Elements["/OC"], ""))) { dictionary.Elements.Remove("/OC"); kept.Elements.Add(annotation); }
            }
            page.Elements["/Annots"] = kept;
        }
        // All selected content is explicit now; native optional-content defaults
        // must not hide imported layers that were originally switched off.
        pdf.Internals.Catalog.Elements.Remove("/OCProperties");
    }
}
