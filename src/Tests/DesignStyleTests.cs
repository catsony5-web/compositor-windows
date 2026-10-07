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
    // Which layers differ between two documents built the same way: kind, name, placement, settings and pixels.
    static string Differences(Document a, Document b)
    {
        var notes = new List<string>();
        if (a.Layers.Count != b.Layers.Count) notes.Add($"layer count {a.Layers.Count} vs {b.Layers.Count}");
        for (int i = 0; i < Math.Min(a.Layers.Count, b.Layers.Count) && notes.Count < 6; i++)
        {
            Layer x = a.Layers[i], y = b.Layers[i]; var parts = new List<string>();
            if (x.Kind != y.Kind || x.Name != y.Name) parts.Add($"kind/name {x.Kind} {x.Name} vs {y.Kind} {y.Name}");
            if (x.X != y.X || x.Y != y.Y || x.Scale != y.Scale || x.Rotation != y.Rotation || x.Opacity != y.Opacity || x.Visible != y.Visible || x.Blend != y.Blend || x.Clipped != y.Clipped)
                parts.Add($"placement ({x.X},{x.Y},{x.Scale},{x.Opacity}) vs ({y.X},{y.Y},{y.Scale},{y.Opacity})");
            // Settings by value (their records hold arrays, which compare by reference).
            if (System.Text.Json.JsonSerializer.Serialize(x.Adjustment) != System.Text.Json.JsonSerializer.Serialize(y.Adjustment)) parts.Add("adjustment settings");
            if (System.Text.Json.JsonSerializer.Serialize(x.Text) != System.Text.Json.JsonSerializer.Serialize(y.Text)) parts.Add("text settings");
            if (x.Pixels.Width != y.Pixels.Width || x.Pixels.Height != y.Pixels.Height) parts.Add($"size {x.Pixels.Width}x{x.Pixels.Height} vs {y.Pixels.Width}x{y.Pixels.Height}");
            else if (!x.Pixels.Data.AsSpan().SequenceEqual(y.Pixels.Data))
            {
                int count = 0, worst = 0, first = -1;
                for (int k = 0; k < x.Pixels.Data.Length; k++) { int d = Math.Abs(x.Pixels.Data[k] - y.Pixels.Data[k]); if (d > 0) { count++; worst = Math.Max(worst, d); if (first < 0) first = k / 4; } }
                parts.Add($"pixels {count} bytes differ (worst {worst}, first pixel {first % Math.Max(1, x.Pixels.Width)},{first / Math.Max(1, x.Pixels.Width)})");
            }
            if ((x.Mask == null) != (y.Mask == null) || x.Mask != null && !x.Mask.AsSpan().SequenceEqual(y.Mask)) parts.Add("mask");
            if (parts.Count > 0) notes.Add($"#{i} {x.Kind} '{x.Name}': " + string.Join(", ", parts));
        }
        return notes.Count == 0 ? "no layer differs (render only)" : string.Join(" | ", notes);
    }

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
            var values = poster.Values(new Dictionary<string, double> { ["photo"] = 7.6, ["color"] = -2, ["title"] = .7, ["unknown"] = 3 });
            Check(values["photo"] == 2 && values["color"] == 0 && values["title"] == 1 && !values.ContainsKey("unknown"), "Values were not clamped to the parameters or kept an unknown key");
            var editorial = DesignStyles.Find(DesignStyles.TranslucentEditorial)!.Values(new Dictionary<string, double> { ["glow"] = 250 });
            Check(editorial["glow"] == 100 && editorial["blur"] == 60 && editorial["panel"] == 2, "Slider values were not clamped or defaults are missing");
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
                    // Re-applying gives the same pixels. The first apply above also realizes every font face the style
                    // uses: on a fresh machine (the CI runner) WPF rasterized a face's first glyphs slightly differently
                    // from later ones (Georgia Italic in the editorial title), so the two later applies are compared.
                    var again = sample.Snapshot(); DesignStyleEngine.Apply(again, new StyleRequest(style.Id), Services);
                    var third = sample.Snapshot(); DesignStyleEngine.Apply(third, new StyleRequest(style.Id), Services);
                    Check(Imaging.Render(again).Data.SequenceEqual(Imaging.Render(third).Data), "The style is not deterministic: " + Differences(again, third));
                    bool Effect(AdjustmentKind kind, Func<AdjustmentSpec, bool>? also = null, bool clipped = false) =>
                        members.Any(l => l.Kind == LayerKind.Adjustment && l.Adjustment!.Kind == kind && l.Clipped == clipped && (also == null || also(l.Adjustment)));
                    switch (style.Id)
                    {
                        case DesignStyles.ScreentonePlan:
                            Check(members.Count(l => l.Kind == LayerKind.Material) >= 3 && members.All(l => l.Kind != LayerKind.Material ||
                                l.Blend == BlendMode.Multiply && HatchPatterns.TryGet(l.Material!.Asset, out var screen) && HatchPatterns.IsScreentone(screen)), "Tone classes are not screentone layers");
                            Check(Effect(AdjustmentKind.Threshold) && Effect(AdjustmentKind.PaperTexture, s => s.Paper.Toner > 0 && s.Paper.Streaks > 0), "The line threshold or the photocopy texture is missing");
                            Check(Array.FindIndex(members, l => l.Adjustment?.Kind == AdjustmentKind.Threshold) < Array.FindIndex(members, l => l.Kind == LayerKind.Material), "The threshold would snap the screens instead of the line work"); break;
                        case DesignStyles.DarkSection:
                            int i = (40 * doc.Width + 40) * 4; Check(after.Data[i] < 80 && after.Data[i + 1] < 80, "The paper did not turn dark");
                            Check(members.Any(l => l.Kind == LayerKind.Material && HatchPatterns.TryGet(l.Material!.Asset, out var grid) && grid == HatchPattern.Grid && l.Blend == BlendMode.Screen), "The grid layer is missing");
                            Check(Effect(AdjustmentKind.PaperTexture, s => s.Paper.Grain > 0), "The print texture is missing"); break;
                        case DesignStyles.Cyanotype:
                            Check(Effect(AdjustmentKind.PaperTexture, s => s.Paper.Edges > 0 && s.Paper.Fibers > 0 && s.Paper.Tint > 0), "The paper with brushed edges is missing");
                            int c = ((int)(doc.Height * .3) * doc.Width + (int)(doc.Width * .2)) * 4; Check(after.Data[c] > after.Data[c + 2] + 20, "The result is not Prussian blue");
                            int corner = (2 * doc.Width + 2) * 4; Check(after.Data[corner + 2] > 200 && after.Data[corner + 1] > 200, "The brushed margin is not the paper color"); break;
                        case DesignStyles.NeoBrutalistPoster:
                            var texts = members.Where(l => l.Kind == LayerKind.Text).ToArray(); var subject = members.Single(l => l.Kind == LayerKind.Raster && l.Mask != null);
                            int title = Array.FindIndex(members, l => l.Name == "제목"), cut = Array.IndexOf(members, subject);
                            Check(texts.Length >= 5 && texts[0].Text!.Content.StartsWith(doc.Name.ToUpperInvariant().Split(' ')[0], StringComparison.Ordinal), "The title or the small text blocks are missing");
                            Check(title >= 0 && title + 1 == cut && members[cut + 1].Clipped && members[cut + 1].Kind == LayerKind.Adjustment, "The subject is not right in front of the title with its own print");
                            // One photo layer: the cut-out is its copy (피사체를 글자 앞으로), sharing the photo's pixels.
                            Check(ReferenceEquals(subject.Pixels, doc.Layers[0].Pixels) && subject.X == doc.Layers[0].X && subject.Y == doc.Layers[0].Y, "The cut-out is not a copy of the photo layer");
                            Check(Effect(AdjustmentKind.Halftone) && Effect(AdjustmentKind.Halftone, clipped: true) && Effect(AdjustmentKind.PaperTexture, s => s.Paper.Grain > 0), "The halftone print or its grain is missing");
                            Check(texts.Any(t => t.Text!.BoxWidth > 0 && t.Text.Alignment == TextAlignment.Justify), "No small text block is a justified paragraph box");
                            Check(texts.Any(t => t.Text!.Content.Contains("2026")), "The year placeholder is missing"); break;
                        case DesignStyles.TranslucentEditorial:
                            Check(members.Any(l => l.Kind == LayerKind.Raster && l.Mask != null) && members.Count(l => l.Kind == LayerKind.Shape) == 2 && members.Count(l => l.Kind == LayerKind.Text) >= 3,
                                "The frosted panel, its veil, edge or text is missing");
                            Check(Effect(AdjustmentKind.Glow) && members.Any(l => l.Kind == LayerKind.Text && l.Text!.BoxWidth > 0), "The glow or the paragraph box is missing"); break;
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
            HatchPattern Pattern(Layer fill) => HatchPatterns.TryGet(fill.Material!.Asset, out var pattern) ? pattern : throw new InvalidOperationException(fill.Name + " is not a library pattern");
            var poche = fills.Single(f => f.Name == "포셰");
            Check(poche.Material!.Boundary.Data.Count(ch => ch == 'M') > 3 && Pattern(poche) == HatchPattern.SolidBlack, "Poché is not one multi-contour black fill");
            // Light, medium and dark rooms get dot screens of rising ink, and they print that way.
            var screens = new[] { "망점 · 밝게", "망점 · 중간", "망점 · 어둡게" }.Select(name => fills.SingleOrDefault(f => f.Name == name)).OfType<Layer>().ToArray();
            Check(screens.Length >= 2 && screens.Select(f => HatchPatterns.Coverage(Pattern(f)) ?? 0).Zip(screens.Skip(1).Select(f => HatchPatterns.Coverage(Pattern(f)) ?? 0)).All(p => p.First < p.Second),
                "The dot screens do not rise in density from light to dark rooms");
            var look = Imaging.Render(doc);
            double Printed(ScreenTone tone)
            {
                // Mean darkness around the middle of the largest room of this tone (inside its inscribed circle).
                var room = map.Regions.Where(r => tones.TryGetValue(r.Label, out var t) && t == tone).OrderByDescending(r => r.Area).First();
                double cx = room.Center.X / map.Scale, cy = room.Center.Y / map.Scale, half = room.Radius * .45 / map.Scale, sum = 0; int count = 0;
                for (int y = (int)(cy - half); y < cy + half; y++) for (int x = (int)(cx - half); x < cx + half; x++) { int k = (y * look.Width + x) * 4; sum += 1 - look.Data[k + 1] / 255d; count++; }
                return sum / Math.Max(1, count);
            }
            var printed = new[] { ScreenTone.Light, ScreenTone.Medium, ScreenTone.Dark }.Where(tones.ContainsValue).Select(Printed).ToArray();
            Check(printed.Zip(printed.Skip(1)).All(p => p.First + .04 < p.Second), "Printed room tones do not follow the screens: " + string.Join(" / ", printed.Select(v => v.ToString("0.00"))));
            var gradient = fills.Where(f => f.Name.StartsWith("점묘 그라데이션", StringComparison.Ordinal) || f.Name.StartsWith("점 그라데이션", StringComparison.Ordinal)).ToArray();
            Check(gradient.Length >= 1 && gradient.All(f => HatchPatterns.IsGradient(Pattern(f)) && f.Material!.Gradient is { } g && g.Start > g.End + .5) && Pattern(gradient[0]) == HatchPattern.StippleGradient,
                "The largest room is not a photocopied stipple gradient from dense to light");
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
