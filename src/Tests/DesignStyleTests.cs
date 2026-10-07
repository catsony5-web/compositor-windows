using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Media;

namespace Compositor.Windows;

// 디자인 스타일 engine checks: the registry, pass-through folders, every recipe on a sample plan
// and photo (editable layers, originals untouched), region detection and tone classes, project
// round trips, reversible edits and cancellable previews.
public static class DesignStyleTests
{
    static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static readonly StyleServices Services = new() { SubjectMask = SyntheticPhoto.SubjectMask, Year = 2026 };

    // What must not change on the original layers (pixel buffers compared by reference: rasters are immutable).
    static (Guid Id, string Name, bool Visible, double X, double Y, double Opacity, BlendMode Blend, Guid? Parent, byte[] Pixels, byte[]? Mask)[] Originals(Document doc) =>
        doc.Layers.Where(l => !DesignStyles.StyledLayers(doc).Contains(l.Id)).Select(l => (l.Id, l.Name, l.Visible, l.X, l.Y, l.Opacity, l.Blend, l.ParentId, l.Pixels.Data, l.Mask)).ToArray();

    static Raster Ink(int width, int height, Action<DrawingContext> draw) => Imaging.Draw(width, height, dc =>
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height)); draw(dc);
    });

    public static void Run(Action<string, Action> test, string directory)
    {
        test("design style registry lists five styles with stable ids, Korean names and bounded parameters", () =>
        {
            Check(DesignStyles.All.Count == 5 && DesignStyles.All.Select(s => s.Id).Distinct().Count() == 5, "The registry does not hold five distinct styles");
            Check(DesignStyles.All.Select(s => s.Id).SequenceEqual([DesignStyles.ScreentonePlan, DesignStyles.DarkSection, DesignStyles.Cyanotype, DesignStyles.NeoBrutalistPoster, DesignStyles.TranslucentEditorial]), "Style ids or their order changed");
            string[] banned = ["Photoshop", "포토샵", "Illustrator", "AutoCAD", "Camera Raw", "Content-Aware", "Lightroom"];
            foreach (var style in DesignStyles.All)
            {
                Check(DesignStyles.IsValidKey(style.Id) && style.Version >= 1 && StyleKit.HasHangul(style.Name) && StyleKit.HasHangul(style.Description), $"{style.Id}: id, version, name or description is missing");
                Check(style.Parameters.Length is >= 1 and <= 3 && style.Parameters.Select(p => p.Key).Distinct().Count() == style.Parameters.Length, $"{style.Id}: needs 1–3 distinct parameters");
                Check(!banned.Any(word => (style.Name + style.Description + string.Concat(style.Parameters.Select(p => p.Name + p.Description))).Contains(word, StringComparison.OrdinalIgnoreCase)), $"{style.Id}: names a third-party product");
                foreach (var p in style.Parameters)
                {
                    Check(DesignStyles.IsValidKey(p.Key) && StyleKit.HasHangul(p.Name) && p.Min < p.Max && p.Default >= p.Min && p.Default <= p.Max, $"{style.Id}.{p.Key}: invalid range or name");
                    if (p.Kind == StyleParameterKind.Choice) Check(p.Choices.Length >= 2 && p.Choices.Select(c => c.Key).Distinct().Count() == p.Choices.Length && p.Default < p.Choices.Length, $"{style.Id}.{p.Key}: choices are incomplete");
                    if (p.Kind == StyleParameterKind.Toggle) Check(p.Min == 0 && p.Max == 1, $"{style.Id}.{p.Key}: a toggle is 0–1");
                }
                Check(DesignStyles.Find(style.Id) == style && DesignStyles.GroupName(style) == "스타일 · " + style.Name, $"{style.Id}: lookup or folder name differs");
            }
            var poster = DesignStyles.Find(DesignStyles.NeoBrutalistPoster)!;
            var values = poster.Values(new Dictionary<string, double> { ["contrast"] = 250, ["color"] = 7.6, ["unknown"] = 3 });
            Check(values["contrast"] == 100 && values["color"] == 3 && values["texture"] == 50 && !values.ContainsKey("unknown"), "Values were not clamped to the parameters or kept an unknown key");
            Check(DesignStyles.Find("missing") == null && !DesignStyles.IsValidKey("Bad Key"), "Unknown or malformed style ids were accepted");
        });

        test("pass-through folders let their adjustments reach the layers below; opacity, mask and blend behave", () =>
        {
            Document Make(bool pass, double opacity = 1, BlendMode blend = BlendMode.Normal, byte[]? mask = null)
            {
                var doc = new Document { Width = 8, Height = 4 };
                doc.Add(new Layer { Name = "바탕", Pixels = Raster.Solid(8, 4, Color.FromRgb(200, 40, 40)) });
                var folder = new Layer { Name = "통과", Kind = LayerKind.Group, PassThrough = pass, Opacity = opacity, Blend = blend, Pixels = new Raster(8, 4), Mask = mask }; doc.Add(folder);
                var invert = DocumentFeatures.CreateAdjustment(doc, new AdjustmentSpec { Kind = AdjustmentKind.GradientMap, DarkColor = 0xFFFFFFFF, LightColor = 0xFF000000 }); invert.ParentId = folder.Id; doc.Add(invert);
                return doc;
            }
            byte Red(Raster r, int x) => r.Data[x * 4 + 2];
            var isolated = Imaging.Render(Make(false)); var through = Imaging.Render(Make(true));
            Check(Red(isolated, 0) == 200, "An ordinary folder's adjustment changed the layer below it");
            Check(Red(through, 0) > 150 && through.Data[0] > 150 && Math.Abs(through.Data[0] - through.Data[2]) < 3, "A pass-through folder's gradient map did not reach the layer below");
            var half = Imaging.Render(Make(true, .5));
            Check(Math.Abs(Red(half, 0) - (Red(isolated, 0) + Red(through, 0)) / 2d) <= 2, "Folder opacity does not mix the pass-through result");
            var mask = new byte[32]; for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) mask[y * 8 + x] = 255;
            var masked = Imaging.Render(Make(true, mask: mask));
            Check(Red(masked, 0) == Red(through, 0) && Red(masked, 6) == 200, "The folder mask does not limit the pass-through result");
            Check(Red(Imaging.Render(Make(true, blend: BlendMode.Multiply)), 0) == 200, "A pass-through folder with its own blend mode is not isolated");
            var design = DesignRenderer.Render(Make(true), new Rect(0, 0, 8, 4), 8, 4);
            Check(Red(design, 0) == Red(through, 0), "The design view renders the pass-through folder differently from export");
            var history = new History(); var doc = Make(false); history.Reset(doc); var before = doc.Snapshot();
            doc.Layers.Single(l => l.Kind == LayerKind.Group).PassThrough = true; history.Commit("통과", before, doc);
            Check(history.CanUndo, "Turning a folder into pass-through was not recorded as a change");
            var scene = new SceneState(); long first = scene.Update(doc); doc.Layers.Single(l => l.Kind == LayerKind.Group).PassThrough = false;
            Check(scene.Update(doc) != first, "The scene cache did not notice a pass-through change");
            var raster = new Layer { Name = "그림", Pixels = new Raster(2, 2), Style = new StyleTag(DesignStyles.Cyanotype, 1, [], [], []) };
            bool rejected = false; try { Document.ValidateLayer(raster); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Style information on a non-folder layer was accepted");
        });

        var plan = SyntheticPlan.Create(); var photo = SyntheticPhoto.Create();
        foreach (var style in DesignStyles.All)
            foreach (var (sample, kind) in new[] { (plan, "plan"), (photo, "photo") })
            {
                if (style.Target == StyleTarget.Drawing && kind == "photo" || style.Target == StyleTarget.Photo && kind == "plan") continue;
                test($"design style {style.Id} on the {kind} adds an editable folder and leaves the originals untouched", () =>
                {
                    var doc = sample.Snapshot(); var originals = Originals(doc); var before = Imaging.Render(doc);
                    var outcome = DesignStyleEngine.Apply(doc, new StyleRequest(style.Id), Services);
                    var folder = doc.Layers.Single(l => l.Id == outcome.GroupId);
                    var members = doc.Layers.Where(l => l.ParentId == folder.Id).ToArray();
                    Check(folder.Kind == LayerKind.Group && folder.PassThrough && folder.Style?.StyleId == style.Id && folder.Style.Version == style.Version && folder.Name == DesignStyles.GroupName(style)
                        && folder.Category == LayerCategory.Photo && folder.ParentId == null, "The style folder is not a tagged pass-through folder at the root");
                    Check(doc.Layers.Last(l => l.ParentId == null).Id == folder.Id, "The style folder is not on top of the document");
                    Check(members.Length == outcome.LayerCount && members.Length >= 3 && members.Any(l => l.Kind == LayerKind.Adjustment), "The folder lacks editable adjustment layers");
                    Check(folder.Style!.Values.Select(v => v.Key).OrderBy(k => k).SequenceEqual(style.Parameters.Select(p => p.Key).OrderBy(k => k)), "The folder does not remember every parameter");
                    Check(Originals(doc).SequenceEqual(originals), "Original layers changed");
                    doc.Validate();
                    var after = Imaging.Render(doc);
                    Check(!after.Data.SequenceEqual(before.Data), "The style did not change how the document looks");
                    var again = sample.Snapshot(); DesignStyleEngine.Apply(again, new StyleRequest(style.Id), Services);
                    Check(Imaging.Render(again).Data.SequenceEqual(after.Data), "The style is not deterministic");
                    switch (style.Id)
                    {
                        case DesignStyles.ScreentonePlan:
                            Check(members.Count(l => l.Kind == LayerKind.Material) >= 3 && members.All(l => l.Kind != LayerKind.Material || l.Blend == BlendMode.Multiply && LinePatterns.IsPattern(l.Material!.Asset)),
                                "Tone classes are not pattern layers");
                            Check(members.Any(l => l.Kind == LayerKind.Raster && l.Blend == BlendMode.Multiply), "The photocopy texture layer is missing"); break;
                        case DesignStyles.DarkSection:
                            int i = (40 * doc.Width + 40) * 4; Check(after.Data[i] < 80 && after.Data[i + 1] < 80, "The paper did not turn dark");
                            Check(members.Any(l => l.Kind == LayerKind.Material && HatchPatterns.TryGet(l.Material!.Asset, out var grid) && grid == HatchPattern.Grid && l.Blend == BlendMode.Screen), "The grid layer is missing"); break;
                        case DesignStyles.Cyanotype:
                            Check(members.Any(l => l.Kind == LayerKind.Adjustment && l.Adjustment!.Kind == AdjustmentKind.Exposure && l.Mask != null), "The edge darkening is missing");
                            int c = ((int)(doc.Height * .3) * doc.Width + (int)(doc.Width * .2)) * 4; Check(after.Data[c] > after.Data[c + 2] + 20, "The result is not Prussian blue"); break;
                        case DesignStyles.NeoBrutalistPoster:
                            var texts = members.Where(l => l.Kind == LayerKind.Text).ToArray(); var subject = members.Single(l => l.Kind == LayerKind.Raster && l.Mask != null);
                            int title = Array.FindIndex(members, l => l.Name == "제목"), cut = Array.IndexOf(members, subject);
                            Check(texts.Length >= 5 && texts[0].Text!.Content.StartsWith(doc.Name.ToUpperInvariant().Split(' ')[0], StringComparison.Ordinal), "The title or the small text blocks are missing");
                            Check(title >= 0 && title < cut && members[cut + 1].Clipped && members[cut + 1].Kind == LayerKind.Adjustment, "The subject is not in front of the title with its own duotone");
                            Check(texts.Any(t => t.Text!.Content.Contains("2026")), "The year placeholder is missing"); break;
                        case DesignStyles.TranslucentEditorial:
                            Check(members.Any(l => l.Kind == LayerKind.Raster && l.Mask != null) && members.Count(l => l.Kind == LayerKind.Shape) == 2 && members.Count(l => l.Kind == LayerKind.Text) >= 3,
                                "The frosted panel, its veil, edge or text is missing"); break;
                    }
                });
            }

        test("design styles re-apply in place, keep the folder id and are removed with their edits", () =>
        {
            var doc = SyntheticDrawing.Create(groups: 4, objectsPerGroup: 60, width: 900, height: 640);
            var hatches = doc.Layers.Where(l => l.Kind == LayerKind.Material).Select(l => l.Id).ToArray();
            Check(hatches.Length > 0, "The sample has no hatch materials");
            var first = DesignStyleEngine.Apply(doc, new StyleRequest(DesignStyles.ScreentonePlan, new Dictionary<string, double> { ["strength"] = 30 }), Services);
            var tag = doc.Layers.Single(l => l.Id == first.GroupId).Style!;
            Check(hatches.All(id => !doc.Layers.Single(l => l.Id == id).Visible) && tag.Edits.Select(e => e.LayerId).OrderBy(g => g).SequenceEqual(hatches.OrderBy(g => g)), "The hatch fills were not hidden as recorded edits");
            var firstMembers = doc.Layers.Where(l => l.ParentId == first.GroupId).Select(l => l.Id).ToHashSet();
            var folder = doc.Layers.Single(l => l.Id == first.GroupId); folder.Opacity = .7; folder.Name = "내 스타일";
            int position = doc.Layers.IndexOf(folder);
            var second = DesignStyleEngine.Apply(doc, new StyleRequest(DesignStyles.ScreentonePlan, new Dictionary<string, double> { ["strength"] = 90 }, GroupId: first.GroupId), Services);
            var replaced = doc.Layers.Single(l => l.Id == second.GroupId);
            Check(second.GroupId == first.GroupId && doc.Layers.Count(DesignStyles.IsStyleGroup) == 1 && doc.Layers.IndexOf(replaced) == position, "Re-apply did not replace the folder in place");
            Check(replaced.Opacity == .7 && replaced.Name == "내 스타일" && replaced.Style!.Get("strength", 0) == 90 && replaced.Style.Get("texture", 0) == 45, "Re-apply lost the folder's opacity, its name or the new values");
            Check(!doc.Layers.Any(l => firstMembers.Contains(l.Id)) && hatches.All(id => !doc.Layers.Single(l => l.Id == id).Visible), "Old style layers remain or the hatches reappeared while styled");
            var other = DesignStyleEngine.Apply(doc, new StyleRequest(DesignStyles.DarkSection, GroupId: first.GroupId), Services);
            Check(doc.Layers.Single(l => l.Id == other.GroupId).Style!.StyleId == DesignStyles.DarkSection && hatches.All(id => doc.Layers.Single(l => l.Id == id).Visible), "Switching the folder to another style did not restore the hatches");
            DesignStyleEngine.Apply(doc, new StyleRequest(DesignStyles.ScreentonePlan, GroupId: first.GroupId), Services);
            var styled = doc.Snapshot();
            DesignStyleEngine.Remove(doc, first.GroupId);
            Check(!doc.Layers.Any(DesignStyles.IsStyleGroup) && hatches.All(id => doc.Layers.Single(l => l.Id == id).Visible), "Removing the style left its folder or kept the hatches hidden");
            DocumentFeatures.Remove(styled, first.GroupId);
            Check(hatches.All(id => styled.Layers.Single(l => l.Id == id).Visible), "Deleting the folder as a layer did not restore the hatches");
            bool locked = false; var lockedDoc = SyntheticPlan.Create(800, 540); var made = DesignStyleEngine.Apply(lockedDoc, new StyleRequest(DesignStyles.DarkSection), Services);
            lockedDoc.Layers.Single(l => l.Id == made.GroupId).Locked = true;
            try { DesignStyleEngine.Apply(lockedDoc, new StyleRequest(DesignStyles.DarkSection, GroupId: made.GroupId), Services); } catch (InvalidOperationException) { locked = true; }
            Check(locked, "A locked style folder was replaced");
            bool wrong = false; try { DesignStyleEngine.Apply(lockedDoc, new StyleRequest(DesignStyles.DarkSection, GroupId: lockedDoc.Layers[0].Id), Services); } catch (ArgumentException) { wrong = true; }
            Check(wrong, "A plain layer was accepted as a style folder");
        });

        test("design style targets limit what is read and where the folder goes", () =>
        {
            var doc = SyntheticPhoto.Create(400, 260);
            var extra = new Layer { Name = "위 사진", Pixels = Raster.Solid(80, 60, Colors.Orange), X = 300, Y = 10 }; doc.Add(extra);
            var bottom = doc.Layers[0];
            var outcome = DesignStyleEngine.Apply(doc, new StyleRequest(DesignStyles.Cyanotype, Targets: [bottom.Id]), Services);
            Check(doc.Layers.IndexOf(doc.Layers.Single(l => l.Id == outcome.GroupId)) == 1 && doc.Layers[^1].Id == extra.Id, "The folder is not right above its target");
            Check(doc.Layers.Single(l => l.Id == outcome.GroupId).Style!.Targets.SequenceEqual([bottom.Id]), "The folder does not remember its targets");
            var analysis = DesignStyleEngine.AnalysisDocument(doc, [bottom.Id]);
            Check(!analysis.Layers.Single(l => l.Id == extra.Id).Visible && !analysis.Layers.Single(l => l.Id == outcome.GroupId).Visible, "The analysis shows layers that are not targets or style folders");
            Check(DesignStyleEngine.ResolveTargets(doc, [outcome.GroupId]) == null, "A style folder was accepted as a target");
        });

        test("region detection finds the rooms of a simple plan and ignores the outside", () =>
        {
            var image = Ink(400, 300, dc =>
            {
                var pen = new Pen(Brushes.Black, 3);
                dc.DrawRectangle(null, pen, new Rect(40, 40, 320, 220));
                dc.DrawLine(pen, new Point(200, 40), new Point(200, 260)); dc.DrawLine(pen, new Point(200, 150), new Point(360, 150));
                dc.DrawRectangle(Brushes.White, pen, new Rect(100, 180, 14, 14));
            });
            var map = RegionDetection.Find(RegionDetection.Ink(image), 400, 300, 1);
            int Label(int x, int y) => map.Labels[y * 400 + x];
            Check(Label(10, 10) == -1 && Label(390, 290) == -1, "The outside became a region");
            Check(Label(40, 100) == 0 || Label(41, 100) == 0, "A wall line is not ink");
            int left = Label(120, 100), topRight = Label(280, 90), bottomRight = Label(280, 210), column = Label(107, 187);
            Check(new[] { left, topRight, bottomRight, column }.All(l => l > 0) && new[] { left, topRight, bottomRight, column }.Distinct().Count() == 4 && map.Regions.Count == 4, "Rooms and the column were not found as four regions");
            var neighbors = RegionDetection.Neighbors(map, 4);
            Check(neighbors.Contains((Math.Min(topRight, bottomRight), Math.Max(topRight, bottomRight))) && neighbors.Contains((Math.Min(left, topRight), Math.Max(left, topRight))), "Rooms sharing a wall are not neighbours");
            var tones = StyleRecipes.Classify(map, neighbors, .5);
            Check(tones[column] == ScreenTone.Poche && tones[left] != ScreenTone.Poche && tones[topRight] != tones[bottomRight], "The column is not poché or neighbouring rooms share a tone");
            var outline = RegionDetection.Outlines(map, [topRight], 1, .8);
            double area = outline.Sum(o => Math.Abs(RegionDetection.SignedArea(o)));
            var info = map.Regions.Single(r => r.Label == topRight);
            Check(outline.Count == 1 && area >= info.Area * .97 && area <= info.Area * 1.12, "The outline does not follow the room");
            var c = new StyleContext(SyntheticPlan.Create(), true, new Dictionary<string, double>(), Services, default);
            var planMap = StyleRecipes.LineRegions(c)!;
            var planTones = StyleRecipes.Classify(planMap, RegionDetection.Neighbors(planMap, 4), .55);
            Check(planTones.Count(p => p.Value != ScreenTone.Poche) >= 5 && planTones.ContainsValue(ScreenTone.Poche) && planTones.ContainsValue(ScreenTone.Gradient), "The sample plan's rooms, walls or gradient room were not found");
            Check(planMap.Regions.All(r => r.Bounds.X > 0 && r.Bounds.Y > 0 && r.Bounds.X + r.Bounds.Width < planMap.Width && r.Bounds.Y + r.Bounds.Height < planMap.Height), "A region of the plan touches the border");
        });

        test("screentone tone classes become one multi-contour pattern layer each", () =>
        {
            var doc = SyntheticPlan.Create();
            var outcome = DesignStyleEngine.Apply(doc, new StyleRequest(DesignStyles.ScreentonePlan), Services);
            var fills = doc.Layers.Where(l => l.ParentId == outcome.GroupId && l.Kind == LayerKind.Material).ToArray();
            var c = new StyleContext(DesignStyleEngine.AnalysisDocument(SyntheticPlan.Create(), null), true, new Dictionary<string, double>(), Services, default);
            var map = StyleRecipes.LineRegions(c)!; var tones = StyleRecipes.Classify(map, RegionDetection.Neighbors(map, Math.Max(3, (int)Math.Round(map.Width / 220d))), .55);
            int expected = tones.Values.Where(t => t != ScreenTone.Gradient).Distinct().Count() + tones.Values.Count(t => t == ScreenTone.Gradient);
            Check(fills.Length == expected, $"Expected {expected} tone layers, found {fills.Length}");
            Check(fills.Select(f => f.Name).Distinct().Count() == fills.Length && fills.Any(f => f.Name == "포셰") && fills.Any(f => f.Name.StartsWith("망점", StringComparison.Ordinal)), "Tone layers are not named by class");
            var poche = fills.Single(f => f.Name == "포셰");
            Check(poche.Material!.Boundary.Data.Count(ch => ch == 'M') > 3 && ReferenceEquals(poche.Material.Asset, StyleKit.Poche), "Poché is not one multi-contour region");
            Check(fills.Where(f => f.Name.StartsWith("망점", StringComparison.Ordinal)).All(f => ReferenceEquals(f.Material!.Asset, StyleKit.DotScreen) && f.Material.Angle == 45), "Dot screens do not share the screen pattern");
            var gradient = fills.Where(f => f.Name.StartsWith("점묘", StringComparison.Ordinal)).ToArray();
            Check(gradient.Length >= 1 && gradient.All(f => f.Mask != null && f.Mask.Any(v => v == 0) && f.Mask.Any(v => v == 255)), "The stipple gradient has no dithered mask");
            // The material panel can swap a tone class to another pattern like any material layer.
            var swapped = MaterialEditing.Swap(fills[0].Material!, HatchPatternRenderer.Create(HatchPattern.Dots));
            MaterialEditing.ValidateFill(swapped, fills[0].Pixels);
        });

        test("design style folders survive a project round trip and older readers keep their layers", () =>
        {
            var doc = SyntheticPhoto.Create(480, 300);
            var outcome = DesignStyleEngine.Apply(doc, new StyleRequest(DesignStyles.NeoBrutalistPoster, new Dictionary<string, double> { ["color"] = 1 }), Services);
            string path = Path.Combine(directory, "design-style-roundtrip.moruproj");
            ProjectStore.Save(doc, path); var read = ProjectStore.Load(path);
            var original = doc.Layers.Single(l => l.Id == outcome.GroupId); var loaded = read.Layers.Single(l => l.Id == outcome.GroupId);
            Check(loaded.PassThrough && loaded.Style is { } tag && tag.StyleId == DesignStyles.NeoBrutalistPoster && tag.Version == original.Style!.Version &&
                tag.Values.SequenceEqual(original.Style.Values) && tag.Targets.SequenceEqual(original.Style.Targets) && tag.Edits.SequenceEqual(original.Style.Edits), "The style tag changed in the project");
            Check(read.Layers.Select(l => (l.Id, l.Kind, l.ParentId, l.Clipped, l.Blend)).SequenceEqual(doc.Layers.Select(l => (l.Id, l.Kind, l.ParentId, l.Clipped, l.Blend))), "Style layers changed in the project");
            var a = Imaging.Render(doc); var b = Imaging.Render(read);
            Check(a.Data.Zip(b.Data).All(p => Math.Abs(p.First - p.Second) <= 2), "The loaded project renders differently");
            using var zip = ZipFile.OpenRead(path); using var reader = new StreamReader(zip.GetEntry("document.json")!.Open());
            string json = reader.ReadToEnd();
            Check(json.Split("\"Style\":").Length == 2 && json.Split("\"PassThrough\":").Length == 2, "Style fields are written for layers other than the folder");
        });

        test("design style folders keep their look in layered .psd and PDF exports", () =>
        {
            static double Difference(Raster a, Raster b)
            {
                int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height); double total = 0;
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                {
                    int i = (y * a.Width + x) * 4, j = (y * b.Width + x) * 4;
                    for (int c = 0; c < 3; c++) total += Math.Abs(a.Data[i + c] - b.Data[j + c]);
                }
                return total / (3d * w * h);
            }
            var doc = SyntheticPhoto.Create(320, 200, "export study");
            var outcome = DesignStyleEngine.Apply(doc, new StyleRequest(DesignStyles.Cyanotype), Services);
            doc.Layers.Single(l => l.Id == outcome.GroupId).Opacity = .8;
            var look = DesignRenderer.RenderOutput(doc);
            string psd = Path.Combine(directory, "design-style-layers.psd");
            ProjectStore.AtomicWrite(psd, s => PsdCompatibility.Write(doc, s, true));
            var back = PsdCompatibility.Read(psd, true).Document;
            Check(back.Layers.Any(l => l.Name == DesignStyles.GroupName(DesignStyles.Find(DesignStyles.Cyanotype)!)) && Difference(look, DesignRenderer.RenderOutput(back)) < 3, "The layered .psd lost the style's look");
            string pdf = Path.Combine(directory, "design-style-layers.pdf");
            ProjectStore.AtomicWrite(pdf, s => CompatibilityExport.Write(doc, CompatibilityExportFormat.PdfLayers, s));
            var flat = Task.Run(() => CompatibilityImport.ReadAsync(pdf, new(Dpi: 96, PreservePdfLayers: false))).GetAwaiter().GetResult().Document;
            Check(Difference(look, Imaging.Render(flat)) < 8, "The layered PDF lost the style's look");
        });

        test("the poster keeps a drawing whole under its title", () =>
        {
            var doc = SyntheticPlan.Create(1200, 800);
            var outcome = DesignStyleEngine.Apply(doc, new StyleRequest(DesignStyles.NeoBrutalistPoster), Services);
            var members = doc.Layers.Where(l => l.ParentId == outcome.GroupId).ToArray();
            Check(members.Any(l => l.Kind == LayerKind.Text && l.Name == "제목") && !members.Any(l => l.Kind == LayerKind.Raster && l.Mask != null),
                "A drawing was cut out in front of the poster title");
        });

        test("design style previews render miniatures and cancel cleanly", () =>
        {
            var preview = StylePreview.Create(SyntheticPlan.Create(), 320, Services);
            Check(preview.IsDrawing && Math.Max(preview.Proxy.Width, preview.Proxy.Height) == 320 && !preview.Proxy.Layers.Any(DesignStyles.IsStyleGroup), "The miniature is not a drawing of the requested size");
            var image = preview.Render(DesignStyles.DarkSection, null);
            Check(image.Width == preview.Proxy.Width && image.Height == preview.Proxy.Height, "The preview render has the wrong size");
            var styled = SyntheticPlan.Create(); DesignStyleEngine.Apply(styled, new StyleRequest(DesignStyles.DarkSection), Services);
            Check(!StylePreview.Create(styled, 200, Services).Proxy.Layers.Any(DesignStyles.IsStyleGroup), "The miniature kept an applied style folder");
            // The miniature reads its line work at the document's size, so the card shows the rooms the applied style will fill.
            string Tones(StyleContext context)
            {
                var map = context.Services.LineRegions(context)!;
                var tones = StyleRecipes.Classify(map, RegionDetection.Neighbors(map, Math.Max(3, (int)Math.Round(map.Width / 220d))), .55);
                return string.Join(" ", tones.Values.GroupBy(t => t).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}"));
            }
            var none = new Dictionary<string, double>();
            var whole = new StyleContext(DesignStyleEngine.AnalysisDocument(SyntheticPlan.Create(), null), true, none, Services, default);
            var mini = new StyleContext(DesignStyleEngine.AnalysisDocument(preview.Proxy, null), true, none, preview.Services, default);
            Check(Tones(mini) == Tones(whole), $"The miniature found other rooms ({Tones(mini)}) than the document ({Tones(whole)})");
            Check(ReferenceEquals(preview.Services.LineRegions(mini), preview.Services.LineRegions(mini)), "The miniature's rooms are found again for every render");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            bool stopped = false; try { preview.Render(DesignStyles.ScreentonePlan, null, cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped, "A cancelled preview render did not stop");
            stopped = false; try { DesignStyleEngine.Apply(SyntheticPlan.Create(600, 400), new StyleRequest(DesignStyles.ScreentonePlan), Services, cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped, "A cancelled apply did not stop");
        });
    }
}
