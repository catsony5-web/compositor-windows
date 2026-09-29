using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CSMath;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunUserProfileTests(Action<string, Action> test, string directory)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            {
                yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        static string[] Names(DependencyObject panel) => Descendants(panel).OfType<Button>().Select(AutomationProperties.GetName).ToArray();
        // Section titles and every action name in panel order.
        static string[] Signature(StackPanel panel)
        {
            var result = new List<string>();
            foreach (UIElement child in panel.Children)
            {
                if (child is SectionHeader header) { result.Add("# " + header.Key); continue; }
                if (child is Button button) result.Add(AutomationProperties.GetName(button));
                result.AddRange(Descendants(child).OfType<Button>().Select(AutomationProperties.GetName));
            }
            return result.ToArray();
        }
        static Button[] Toolbar(MainWindow window) => window.workspaceTools!.Children.OfType<UniformGrid>().SelectMany(grid => grid.Children.OfType<Button>()).ToArray();
        string root = Path.Combine(directory, "user-profiles"); Directory.CreateDirectory(root);

        test("사용 목적 기본 keeps the right panel, tool rail and tab captions exactly as before", () =>
        {
            var w = new MainWindow(null) { headlessTesting = true };
            try
            {
                var basic = UserProfiles.Default;
                Check(ReferenceEquals(w.CurrentUserProfile, basic) && !w.userProfileChosen && w.CaptureLayout().Profile == null, "A new window did not start with the default purpose unchosen");
                Check(basic.TabCaption == null && basic.PaneCaption == null && basic.PhotoSections == null && basic.DesignSections == null && basic.ToolGroups == null
                    && basic.DesignWorkspace == null && basic.QuickSizes == null && !basic.QuickDrawingImport, "The default purpose overrides a choice");
                w.AddTab(NewDocumentDialog.CreateDocument("기본", "64", "64", 0), null);
                string[] Expected(bool design) { var panel = new StackPanel(); if (design) w.BuildDesignActions(panel); else w.BuildPhotoActions(panel); return Signature(panel); }
                void Unchanged(string when)
                {
                    bool design = w.designWorkspace;
                    Check(Signature(w.studioContents[0]).SequenceEqual(Expected(design)), $"The first tab changed {when}: " + string.Join(", ", Signature(w.studioContents[0])));
                    Check(Toolbar(w).SequenceEqual(WorkspaceToolGroups(design).SelectMany(g => g.Tools).Select(t => w.toolButtons[t])), $"The tool rail changed {when}");
                    Check((string)w.studioTabs[0].Content == (design ? "디자인" : "보정") && w.studioPanes[0].Caption == (design ? "디자인" : "사진 보정"), $"The first tab caption changed {when}");
                }
                foreach (bool design in new[] { false, true, false }) { w.SetWorkspaceMode(design); Unchanged(design ? "in design mode" : "in photo mode"); }
                w.SetUserProfile(UserProfiles.ArchitectureId); w.SetUserProfile(UserProfiles.DefaultId);
                Check(w.designWorkspace && ReferenceEquals(w.CurrentUserProfile, basic), "Returning to 기본 changed the chosen mode");
                Unchanged("after returning from 건축학과");
                w.SetWorkspaceMode(false); Unchanged("after returning to photo mode");
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("사용 목적 건축학과 puts line cleanup and retouch first and keeps every panel action", () =>
        {
            var w = new MainWindow(null) { headlessTesting = true };
            try
            {
                w.AddTab(NewDocumentDialog.CreateDocument("보드", "64", "64", 1), null);
                string[] Defaults(bool design) { var panel = new StackPanel(); if (design) w.BuildDesignActions(panel); else w.BuildPhotoActions(panel); return Names(panel); }
                w.SetUserProfile(UserProfiles.ArchitectureId);
                Check(w.designWorkspace && w.userProfileChosen && w.studioPage == 0, "건축학과 did not open its first tab in design mode");
                foreach (bool design in new[] { true, false })
                {
                    w.SetWorkspaceMode(design);
                    var keys = w.studioContents[0].Children.OfType<SectionHeader>().Select(h => h.Key).ToArray();
                    Check(keys.Take(3).SequenceEqual([UserProfiles.LineCleanup, UserProfiles.Retouch, "배치와 그룹"]), "Drawing work is not first: " + string.Join(", ", keys));
                    int lead = Array.IndexOf(keys, UserProfiles.PhotoLead);
                    Check(lead > 2 && Array.IndexOf(keys, "조정 레이어") == lead + 1, "Photo adjustments do not follow the drawing and layer work: " + string.Join(", ", keys));
                    Check(keys.Distinct().Count() == keys.Length && w.studioContents[0].Children[0] is SectionHeader, "A section repeats or rows precede the first section");
                    var names = Names(w.studioContents[0]);
                    Check(names.Distinct().Count() == names.Length, "Action names repeat: " + string.Join(", ", names.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key)));
                    var missing = Defaults(design).Except(names).ToArray();
                    Check(missing.Length == 0, "Default actions became unreachable: " + string.Join(", ", missing));
                    Check(new[] { "선 정리 다시 적용", "도면 가져오기", "도면 레이어 보기", "치수·문자 표시 전환", "해치 재질 표시 전환", "흐림 브러시", "복구 브러시", "복제 도장" }.All(names.Contains), "Line cleanup or retouch actions are missing");
                    var buttons = Descendants(w.studioContents[0]).OfType<Button>().ToArray();
                    Check(buttons.All(b => b.Content is not string && b.ToolTip is string { Length: > 0 }), "An action is text-only or lacks a tooltip");
                    Check((string)w.studioTabs[0].Content == "도면" && w.studioPanes[0].Caption == "도면 작업", "The first tab is not labeled for drawing work");
                }
                Check(w.BuildCommandRegistry().Single(c => c.Id == "panel:page0").Title == "도면 작업 패널 열기", "The panel command did not follow the purpose");
                var retouch = Descendants(w.studioContents[0]).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "흐림 브러시");
                Click(retouch); Check(w.tool == Tool.BlurBrush, "The retouch action did not choose the blur brush");
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("사용 목적 건축학과 tool rail lists every tool once with selection and retouch first", () =>
        {
            var w = new MainWindow(null) { headlessTesting = true };
            try
            {
                w.SetUserProfile(UserProfiles.ArchitectureId);
                foreach (bool design in new[] { true, false })
                {
                    w.SetWorkspaceMode(design);
                    var bar = Toolbar(w);
                    Check(bar.Length == w.toolButtons.Count && bar.Distinct().Count() == bar.Length, "Tools are missing or repeated");
                    Check(bar.Take(3).SequenceEqual(new[] { Tool.Move, Tool.MagicWand, Tool.PolygonLasso }.Select(t => w.toolButtons[t])), "Drawing selection tools are not first");
                    int Index(Tool t) => Array.IndexOf(bar, w.toolButtons[t]);
                    Check(Index(Tool.Heal) < Index(Tool.Text) && Index(Tool.CloneStamp) < Index(Tool.Text) && Index(Tool.BlurBrush) < Index(Tool.Text), "Retouch tools do not precede board tools");
                    Check(bar.SequenceEqual(w.photoToolOrder.Select(t => w.toolButtons[t])), "The rail does not match its recorded order");
                    Check(w.workspaceTools!.Children.OfType<UniformGrid>().Select(AutomationProperties.GetName).SequenceEqual(["선택 도구", "리터치 도구", "보드 도구", "색상 도구", "보기 도구"]), "Tool groups are not named");
                }
                w.userProfile = UserProfiles.Architecture with { ToolGroups = [("선택", [Tool.Move, Tool.Move, Tool.Hand])] };
                w.BuildWorkspaceTools();
                var partial = Toolbar(w);
                Check(partial.Length == w.toolButtons.Count && partial.Distinct().Count() == partial.Length && partial[0] == w.toolButtons[Tool.Move], "An incomplete tool table lost or repeated tools");
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("사용 목적 persists with the workspace layout, keeps the saved mode and drops unknown values", () =>
        {
            string store = Path.Combine(root, "workspace-" + Guid.NewGuid().ToString("N") + ".json");
            var target = new MainWindow(null) { headlessTesting = true };
            var fresh = new MainWindow(null) { headlessTesting = true };
            try
            {
                WorkspaceLayoutStore.Save(new WorkspaceLayout { Profile = UserProfiles.ArchitectureId, DesignWorkspace = false, StudioPage = 2 }, store);
                var loaded = WorkspaceLayoutStore.Load(store)!;
                Check(loaded.Profile == UserProfiles.ArchitectureId && !loaded.DesignWorkspace, "The purpose was not stored");
                foreach (var bad in new[] { "unknown", "ARCHITECTURE", "", null })
                {
                    WorkspaceLayoutStore.Save(new WorkspaceLayout { Profile = bad }, store);
                    Check(WorkspaceLayoutStore.Load(store)!.Profile == null, $"An unknown purpose '{bad}' was kept");
                }
                target.ApplyPaneLayout(loaded);
                Check(ReferenceEquals(target.CurrentUserProfile, UserProfiles.Architecture) && target.userProfileChosen, "The purpose was not restored");
                Check(!target.designWorkspace && target.studioPage == 2, "Restoring the purpose overrode the saved mode or tab");
                Check(target.profileMenuItems[UserProfiles.ArchitectureId].IsChecked && !target.profileMenuItems[UserProfiles.DefaultId].IsChecked, "The View menu does not show the restored purpose");
                Check(target.startProfileChoice!.Selected == UserProfiles.ArchitectureId && target.startProfileHint!.Visibility == Visibility.Collapsed, "The start screen does not show the restored purpose");
                Check(WorkspaceLayoutStore.Sanitize(target.CaptureLayout())!.Profile == UserProfiles.ArchitectureId, "The restored purpose was not captured again");
                fresh.ApplyPaneLayout(new WorkspaceLayout { Profile = "unknown" });
                Check(ReferenceEquals(fresh.CurrentUserProfile, UserProfiles.Default) && !fresh.userProfileChosen && fresh.CaptureLayout().Profile == null, "An unknown purpose changed the window");
                fresh.SetUserProfile(UserProfiles.DefaultId);
                Check(fresh.CaptureLayout().Profile == UserProfiles.DefaultId, "Choosing 기본 was not remembered");
            }
            finally { target.StopRenderingForShutdown(); fresh.StopRenderingForShutdown(); if (File.Exists(store)) File.Delete(store); }
        });

        test("사용 목적 is chosen from the View menu, the ribbon and the command palette", () =>
        {
            var w = new MainWindow(null) { headlessTesting = true };
            try
            {
                var registry = w.BuildCommandRegistry();
                var architecture = registry.Single(c => c.Id == "menu:보기/사용 목적/건축학과");
                var basic = registry.Single(c => c.Id == "menu:보기/사용 목적/기본");
                Check(architecture.Category == "보기 › 사용 목적" && architecture.IsAvailable() && basic.IsAvailable(), "Purpose commands are not listed or need a document");
                Check(CommandPalette.Filter(registry, "건축", []).First().Id == architecture.Id && CommandPalette.Filter(registry, "사용목적", []).Any(c => c.Id == basic.Id), "The palette does not find the purposes");
                w.RunCommand(architecture);
                Check(ReferenceEquals(w.CurrentUserProfile, UserProfiles.Architecture) && w.designWorkspace, "The palette did not choose 건축학과");
                Check(w.profileMenuItems[UserProfiles.ArchitectureId].IsChecked && !w.profileMenuItems[UserProfiles.DefaultId].IsChecked, "The menu checks do not follow the purpose");
                w.RunCommand(w.BuildCommandRegistry().Single(c => c.Id == architecture.Id));
                Check(w.profileMenuItems[UserProfiles.ArchitectureId].IsChecked && ReferenceEquals(w.CurrentUserProfile, UserProfiles.Architecture), "Choosing the current purpose again unchecked it");
                w.SetRibbonMode(true); w.SelectRibbonTab("보기");
                var group = w.RibbonGroups("보기").Single(g => g.Title == "사용 목적");
                Check(group.Items.Select(i => i.Header).SequenceEqual(UserProfiles.All.Select(p => (object)p.Name)), "The ribbon lacks the purpose group");
                Check(RibbonGlyph("건축학과") == Theme.Glyphs.Plan && RibbonGlyph("기본") == Theme.Glyphs.Image, "Purpose buttons lack their icons");
                Click(Descendants(w.ribbonBody!).OfType<Button>().First(b => AutomationProperties.GetName(b) == "기본"));
                Check(ReferenceEquals(w.CurrentUserProfile, UserProfiles.Default) && w.profileMenuItems[UserProfiles.DefaultId].IsChecked, "The ribbon button did not choose 기본");
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("start screen offers 사용 목적 inline and 건축학과 quick starts A-series boards and drawing import", () =>
        {
            var w = new MainWindow(null) { headlessTesting = true };
            var other = new MainWindow(null) { headlessTesting = true };
            try
            {
                w.UpdateDocumentAvailability();
                var choice = w.startProfileChoice!;
                Check(Descendants(w.emptyWorkspace!).Contains(choice) && choice.Buttons.Select(AutomationProperties.GetName).SequenceEqual(UserProfiles.All.Select(p => p.Name)), "The start screen lacks the purpose choice");
                Check(w.startProfileHint!.Visibility == Visibility.Visible && w.startProfileSummary!.Text == UserProfiles.Default.Summary, "The first-run hint or summary is missing");
                string[] Quick() => Descendants(w.emptyWorkspace!).OfType<Button>().Select(AutomationProperties.GetName).Where(n => n?.StartsWith("빠른 시작: ") == true).ToArray();
                Button QuickButton(string label) => Descendants(w.emptyWorkspace!).OfType<Button>().Single(b => AutomationProperties.GetName(b)?.StartsWith("빠른 시작: " + label) == true);
                Check(Quick().Length == QuickSizes.Length && QuickSizes.All(s => QuickButton(s.Label) != null), "Default quick starts changed");
                Click(choice.Buttons[1]);
                Check(ReferenceEquals(w.CurrentUserProfile, UserProfiles.Architecture) && w.userProfileChosen && w.designWorkspace, "The start screen did not choose 건축학과");
                Check(w.startProfileHint!.Visibility == Visibility.Collapsed && w.startProfileSummary!.Text == UserProfiles.Architecture.Summary, "The hint stayed or the summary did not follow");
                Check(Quick().Length == 4 && new[] { "A1 가로", "A2 가로", "A3 가로", "도면 가져오기" }.All(label => QuickButton(label) != null), "건축학과 quick starts are missing: " + string.Join(", ", Quick()));
                Check(UserProfiles.Architecture.QuickSizes!.All(s => s.Millimeters && double.Parse(s.Dpi) is >= 150 and <= 300 && double.Parse(s.Width) > double.Parse(s.Height)), "Board sizes are not landscape A-series at 150–300 DPI");
                Check(QuickButton("A3 가로").ToolTip is string tip && tip.Contains("300 DPI") && !tip.Contains('\n'), "A board tooltip lacks its resolution");
                Click(QuickButton("A1 가로"));
                Check(w.HasDocument && w.doc.Dpi == 150 && Math.Abs(w.doc.Width - 4967) <= 1 && Math.Abs(w.doc.Height - 3508) <= 1 && !w.history.Dirty(w.doc), $"A1 did not open a clean 150 DPI board: {w.doc.Width} × {w.doc.Height}");
                w.CloseTab();
                Click(choice.Buttons[0]);
                Check(ReferenceEquals(w.CurrentUserProfile, UserProfiles.Default) && Quick().Length == QuickSizes.Length, "Returning to 기본 did not restore its quick starts");
                other.UpdateDocumentAvailability();
                Click(other.startProfileChoice!.Buttons[0]);
                Check(other.userProfileChosen && other.startProfileHint!.Visibility == Visibility.Collapsed && ReferenceEquals(other.CurrentUserProfile, UserProfiles.Default), "Picking the preselected 기본 did not count as a choice");
            }
            finally { w.StopRenderingForShutdown(); other.StopRenderingForShutdown(); }
        });

        // A plan named like an opening drawing: its folder name must not become a layer role.
        string Plan(string name, bool hatch)
        {
            var cad = new CadDocument();
            ACadSharp.Tables.Layer Layer(string layer) { var created = new ACadSharp.Tables.Layer(layer); cad.Layers.Add(created); return created; }
            var wall = Layer("A-WALL"); var furniture = Layer("A-FURN"); var dims = Layer("A-ANNO-DIMS"); var hatches = Layer("A-HATCH");
            cad.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(200, 0, 0), Layer = wall });
            cad.Entities.Add(new LwPolyline(new XY[] { new(0, 0), new(0, 120), new(200, 120) }) { Layer = wall });
            cad.Entities.Add(new Line { StartPoint = new XYZ(20, 60, 0), EndPoint = new XYZ(180, 60, 0), Layer = furniture });
            cad.Entities.Add(new Line { StartPoint = new XYZ(0, 150, 0), EndPoint = new XYZ(200, 150, 0), Layer = dims });
            cad.Entities.Add(new TextEntity { Value = "2000", InsertPoint = new XYZ(90, 156, 0), Height = 12, Layer = dims });
            cad.Entities.Add(new Line { StartPoint = new XYZ(0, 180, 0), EndPoint = new XYZ(200, 180, 0) });
            if (hatch)
            {
                var fill = new Hatch { Pattern = new HatchPattern("AR-CONC"), Layer = hatches };
                var path = new Hatch.BoundaryPath(); path.Edges.Add(new Hatch.BoundaryPath.Polyline(new[] { new XYZ(20, 20, 0), new XYZ(180, 20, 0), new XYZ(180, 50, 0), new XYZ(20, 50, 0) }, true));
                fill.Paths.Add(path); cad.Entities.Add(fill);
            }
            string file = Path.Combine(root, name); DxfWriter.Write(file, cad); return file;
        }
        Document Import(string file, CadImportStructure structure, CadCleanup? cleanup) => CompatibilityImport.ReadAsync(file, new(CadLongEdge: 600, CadLayout: "*Model_Space", RetainVectors: true,
            CadStructure: structure, SeparateLayers: true, GroupDrawingObjects: true, Cleanup: cleanup)).GetAwaiter().GetResult().Document;
        static byte[] Payload(Layer layer) { using var output = new MemoryStream(); layer.Vector!.Write(output); return output.ToArray(); }
        // Vector layers of one CAD layer, in document order, for either import structure.
        static Layer[] Lines(Document doc, string cadLayer) => doc.Layers.Where(l => l.Kind == LayerKind.Vector
            && (l.Name == cadLayer || doc.Layers.FirstOrDefault(p => p.Id == l.ParentId)?.SourceLayerName == cadLayer)).ToArray();
        static bool Same(Document a, Document b, string cadLayer) { var x = Lines(a, cadLayer); var y = Lines(b, cadLayer); return x.Length > 0 && x.Length == y.Length && x.Zip(y).All(p => Payload(p.First).SequenceEqual(Payload(p.Second))); }
        // The command works in the background; run it to the end on this dispatcher.
        static void Reapply(MainWindow w) => WaitOnDispatcher(w.ReapplyLineCleanupAsync);

        test("선 정리 다시 적용 restyles an imported plan by layer role like import cleanup, in one undo", () =>
        {
            string file = Plan("창호 평면.dxf", false);
            foreach (var structure in new[] { CadImportStructure.Layers, CadImportStructure.Objects })
            {
                var plain = Import(file, structure, null);
                var cleaned = Import(file, structure, new CadCleanup(Hatches: HatchTreatment.Keep));
                var w = new MainWindow(null) { headlessTesting = true };
                try
                {
                    w.AddTab(plain.Snapshot(), null);
                    Check(!Same(w.doc, cleaned, "A-WALL"), $"{structure}: the plain import was already cleaned");
                    w.selectedLayers.Clear(); w.doc.ActiveId = w.doc.Layers.First(l => l.Kind == LayerKind.Raster || l.Kind == LayerKind.Shape).Id;
                    Reapply(w);
                    foreach (var layer in new[] { "A-WALL", "A-FURN", "A-ANNO-DIMS" })
                        Check(Same(w.doc, cleaned, layer), $"{structure}: {layer} does not match the import cleanup");
                    Check(Same(w.doc, plain, "0"), $"{structure}: a layer without a role was restyled");
                    var wall = Lines(w.doc, "A-WALL")[0]; var furniture = Lines(w.doc, "A-FURN")[0];
                    Check(wall.Pixels.Data.Where((_, i) => i % 4 == 3).Count(a => a > 40) > furniture.Pixels.Data.Where((_, i) => i % 4 == 3).Count(a => a > 40), $"{structure}: the preview pixels were not redrawn");
                    w.Undo(); Check(Same(w.doc, plain, "A-WALL") && Same(w.doc, plain, "A-FURN") && !w.history.CanUndo, $"{structure}: line cleanup was not one undo step");
                    w.Redo(); Reapply(w); w.Undo();
                    Check(Same(w.doc, plain, "A-WALL") && !w.history.CanUndo, $"{structure}: re-applying a cleaned drawing added a step");
                    // Scope: the selected CAD layer only; locked layers stay as they are.
                    var target = Lines(w.doc, "A-FURN")[0];
                    var scope = structure == CadImportStructure.Objects ? w.doc.Layers.Single(l => l.Id == target.ParentId) : target;
                    w.selectedLayers.Clear(); w.selectedLayers.Add(scope.Id); w.doc.ActiveId = scope.Id;
                    Reapply(w);
                    Check(Same(w.doc, cleaned, "A-FURN") && Same(w.doc, plain, "A-WALL"), $"{structure}: the selection did not limit line cleanup");
                    w.Undo();
                    foreach (var layer in Lines(w.doc, "A-WALL")) layer.Locked = true;
                    w.selectedLayers.Clear(); w.doc.ActiveId = w.doc.Layers[0].Id;
                    Reapply(w);
                    Check(Same(w.doc, plain, "A-WALL") && Same(w.doc, cleaned, "A-FURN"), $"{structure}: a locked layer was restyled");
                    foreach (var tab in w.tabs) tab.History.MarkSaved(tab.Document);
                }
                finally { w.StopRenderingForShutdown(); }
            }
        });

        test("치수·문자 and hatch material toggles hide and show drawing layers in one undo each", () =>
        {
            string file = Plan("창호 평면 표시.dxf", true);
            var w = new MainWindow(null) { headlessTesting = true };
            var photo = new MainWindow(null) { headlessTesting = true };
            try
            {
                w.AddTab(Import(file, CadImportStructure.Objects, new CadCleanup()), null);
                var dims = DrawingLineCleanup.RoleLayers(w.doc, DrawingRole.Annotation);
                Check(dims.Length == 1 && w.doc.Layers.Single(l => l.Id == dims[0]) is { Kind: LayerKind.Group, SourceLayerName: "A-ANNO-DIMS" }, "The dimension folder was not found once: " + dims.Length);
                Check(DrawingLineCleanup.RoleLayers(w.doc, DrawingRole.Opening).Length == 0, "The drawing folder name was read as a layer role");
                Layer Get(Guid id) => w.doc.Layers.Single(l => l.Id == id);
                w.ToggleAnnotationLayers();
                Check(!Get(dims[0]).Visible && Lines(w.doc, "A-WALL").All(l => l.Visible), "Dimensions were not hidden alone");
                w.ToggleAnnotationLayers(); Check(Get(dims[0]).Visible, "Dimensions were not shown again");
                w.Undo(); Check(!Get(dims[0]).Visible, "Showing dimensions was not one undo step");
                var materials = w.doc.Layers.Where(l => l.Kind == LayerKind.Material).Select(l => l.Id).ToArray();
                Check(materials.Length > 0, "The cleanup import made no hatch material");
                w.ToggleMaterialLayers(); Check(materials.All(id => !Get(id).Visible), "Hatch materials were not hidden");
                w.Undo(); Check(materials.All(id => Get(id).Visible), "Hiding hatch materials was not one undo step");
                foreach (var tab in w.tabs) tab.History.MarkSaved(tab.Document);
                photo.AddTab(NewDocumentDialog.CreateDocument("사진", "16", "16", 1), null);
                photo.ToggleAnnotationLayers(); photo.ToggleMaterialLayers();
                Check(!photo.history.CanUndo && photo.status.Text.Contains("없습니다"), "Toggles without drawing layers changed history or stayed silent");
            }
            finally { w.StopRenderingForShutdown(); photo.StopRenderingForShutdown(); }
        });

        // A CAD layer as the panels show it: its folder (object imports) or its one vector layer (layer imports).
        static Layer CadNode(Document doc, string cadLayer) => doc.Layers.First(l => l.Kind == LayerKind.Group && l.SourceLayerName == cadLayer
            || l.Kind == LayerKind.Vector && l.Name == cadLayer && doc.Layers.FirstOrDefault(p => p.Id == l.ParentId)?.SourceLayerName == null);
        static void Settle(MainWindow w) { foreach (var tab in w.tabs) tab.History.MarkSaved(tab.Document); }

        test("선 정리 다시 적용 after placing a drawing on a board restyles the whole drawing, not only the last placed layer", () =>
        {
            string file = Plan("배치 평면.dxf", false);
            foreach (var structure in new[] { CadImportStructure.Layers, CadImportStructure.Objects })
            {
                var plain = Import(file, structure, null);
                var cleaned = Import(file, structure, new CadCleanup(Hatches: HatchTreatment.Keep));
                var w = new MainWindow(null) { headlessTesting = true };
                try
                {
                    w.AddTab(NewDocumentDialog.CreateDocument("보드", "900", "700", 1), null);
                    // What 도면 가져오기 does on an open board: one step, the selection left as it was.
                    var candidate = w.doc.Snapshot();
                    CompatibilityImport.Place(candidate, plain.Snapshot(), false, w.doc.Width, w.doc.Height);
                    w.Edit("이미지 가져오기", () => { w.doc = candidate; });
                    Check(!w.selectedLayers.Contains(w.doc.ActiveId), $"{structure}: placing the drawing selected its last layer explicitly");
                    Reapply(w);
                    foreach (var layer in new[] { "A-WALL", "A-FURN", "A-ANNO-DIMS" })
                        Check(Same(w.doc, cleaned, layer), $"{structure}: {layer} was not cleaned; only the implicit active layer was the scope");
                    Settle(w);
                }
                finally { w.StopRenderingForShutdown(); }
            }
        });

        test("선 정리 다시 적용 after switching tabs still restyles the whole placed drawing", () =>
        {
            string file = Plan("탭 전환 평면.dxf", false);
            var plain = Import(file, CadImportStructure.Objects, null);
            var cleaned = Import(file, CadImportStructure.Objects, new CadCleanup(Hatches: HatchTreatment.Keep));
            var w = new MainWindow(null) { headlessTesting = true };
            try
            {
                w.AddTab(NewDocumentDialog.CreateDocument("보드", "900", "700", 1), null);
                var candidate = w.doc.Snapshot();
                CompatibilityImport.Place(candidate, plain.Snapshot(), false, w.doc.Width, w.doc.Height);
                w.Edit("이미지 가져오기", () => { w.doc = candidate; });
                w.AddTab(NewDocumentDialog.CreateDocument("다른 보드", "100", "100", 1), null);
                w.SwitchTab(0);
                // Returning to the tab restores its layer selection; the last placed object stays implicit.
                Check(!w.selectedLayers.Contains(w.doc.ActiveId), "A tab switch turned the active layer into an explicit selection");
                Reapply(w);
                foreach (var layer in new[] { "A-WALL", "A-FURN", "A-ANNO-DIMS" })
                    Check(Same(w.doc, cleaned, layer), $"{layer} was not cleaned after a tab switch; only the restored active layer was the scope");
                Settle(w);
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("해치 재질 표시 전환 hides import hatch materials only, not materials applied to selections", () =>
        {
            string file = Plan("재질 평면 표시.dxf", true);
            var w = new MainWindow(null) { headlessTesting = true };
            var photo = new MainWindow(null) { headlessTesting = true };
            try
            {
                w.AddTab(Import(file, CadImportStructure.Objects, new CadCleanup()), null);
                var hatches = w.doc.Layers.Where(l => l.Kind == LayerKind.Material).ToArray();
                Check(hatches.Length > 0, "The cleanup import made no hatch material");
                w.selection = new Selection(new Rect(30, 30, 60, 60)); w.Refresh(false);
                var mine = w.ApplySelectionMaterial(MaterialPresets.Create(MaterialKind.Wood), DrawingCleanup.MaterialName(MaterialKind.Wood));
                Check(mine is { Kind: LayerKind.Material, Visible: true }, "The selection material was not made");
                w.ToggleMaterialLayers();
                Check(hatches.All(h => !w.doc.Layers.Single(l => l.Id == h.Id).Visible), "Import hatch materials were not hidden");
                Check(w.doc.Layers.Single(l => l.Id == mine!.Id).Visible, "The material the user applied to a selection was hidden with the hatches");
                Check(w.status.Text.Contains(hatches.Length.ToString("N0")) && !w.status.Text.Contains((hatches.Length + 1).ToString("N0")), "The status counted the selection material: " + w.status.Text);
                w.ToggleMaterialLayers();
                Check(hatches.All(h => w.doc.Layers.Single(l => l.Id == h.Id).Visible), "Hatch materials were not shown again");
                Settle(w);
                photo.AddTab(NewDocumentDialog.CreateDocument("사진", "64", "64", 1), null);
                photo.selection = new Selection(new Rect(8, 8, 32, 32)); photo.Refresh(false);
                var own = photo.ApplySelectionMaterial(MaterialPresets.Create(MaterialKind.Tile), DrawingCleanup.MaterialName(MaterialKind.Tile));
                Check(own != null, "The photo selection material was not made");
                photo.ToggleMaterialLayers();
                Check(photo.doc.Layers.Single(l => l.Id == own!.Id).Visible && photo.history.UndoLabel == "선택 영역 재질" && photo.status.Text.Contains("없습니다"),
                    "A document without import hatches toggled the user's material");
                Settle(photo);
            }
            finally { w.StopRenderingForShutdown(); photo.StopRenderingForShutdown(); }
        });

        test("선 정리 다시 적용 and 치수·문자 표시 전환 follow the role choices remembered from the import dialog", () =>
        {
            string file = Plan("역할 지정 평면.dxf", false);
            // Remembered choices are case-insensitive, as ImportSettings keeps them.
            var roles = new Dictionary<string, DrawingRole>(StringComparer.OrdinalIgnoreCase)
                { ["a-wall"] = DrawingRole.Furniture, ["A-FURN"] = DrawingRole.Structure, ["0"] = DrawingRole.Annotation };
            foreach (var structure in new[] { CadImportStructure.Layers, CadImportStructure.Objects })
            {
                var plain = Import(file, structure, null);
                var chosen = Import(file, structure, new CadCleanup(Hatches: HatchTreatment.Keep, Roles: roles));
                var w = new MainWindow(null) { headlessTesting = true };
                try
                {
                    w.SaveImportSettings(new ImportSettings { CadRoles = roles });
                    w.AddTab(plain.Snapshot(), null);
                    w.selectedLayers.Clear(); w.doc.ActiveId = w.doc.Layers.First(l => l.Kind == LayerKind.Raster || l.Kind == LayerKind.Shape).Id;
                    Reapply(w);
                    foreach (var layer in new[] { "A-WALL", "A-FURN", "0", "A-ANNO-DIMS" })
                        Check(Same(w.doc, chosen, layer), $"{structure}: {layer} does not match the import with the user's role choices");
                    w.ToggleAnnotationLayers();
                    Check(!CadNode(w.doc, "0").Visible && !CadNode(w.doc, "A-ANNO-DIMS").Visible && CadNode(w.doc, "A-WALL").Visible,
                        $"{structure}: the 치수·문자 toggle ignored the remembered role of layer 0");
                    Settle(w);
                }
                finally { w.StopRenderingForShutdown(); }
            }
        });

        test("선 정리 다시 적용 finds the CAD layer of objects grouped inside its folder", () =>
        {
            string file = Plan("그룹 평면.dxf", false);
            var plain = Import(file, CadImportStructure.Objects, null);
            var cleaned = Import(file, CadImportStructure.Objects, new CadCleanup(Hatches: HatchTreatment.Keep));
            var w = new MainWindow(null) { headlessTesting = true };
            try
            {
                w.AddTab(plain.Snapshot(), null);
                var walls = Lines(w.doc, "A-WALL").Select(l => l.Id).ToArray();
                Check(walls.Length == 2, "The plan has no two wall objects to group");
                w.selectedLayers.Clear(); foreach (var id in walls) w.selectedLayers.Add(id); w.doc.ActiveId = walls[0];
                w.GroupSelected();
                var group = w.doc.Layers.Single(l => l.Id == w.doc.Layers.Single(x => x.Id == walls[0]).ParentId);
                Check(group.SourceLayerName == null && w.doc.Layers.Single(l => l.Id == group.ParentId).SourceLayerName == "A-WALL", "The walls were not grouped inside their CAD-layer folder");
                w.selectedLayers.Clear(); w.doc.ActiveId = w.doc.Layers.First(l => l.Kind == LayerKind.Raster || l.Kind == LayerKind.Shape).Id;
                Reapply(w);
                var expected = Lines(cleaned, "A-WALL");
                Check(walls.Zip(expected).All(p => Payload(w.doc.Layers.Single(l => l.Id == p.First)).SequenceEqual(Payload(p.Second))), "Grouped wall objects were not restyled as walls");
                Check(w.status.Text.Contains("역할을 알 수 없는 레이어 1개"), "Grouped walls were counted as unknown: " + w.status.Text);
                Settle(w);
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("선 정리 다시 적용 leaves text and hatches on a layer without a role alone, like import cleanup", () =>
        {
            var cad = new CadDocument();
            var wall = new ACadSharp.Tables.Layer("A-WALL"); cad.Layers.Add(wall);
            cad.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(200, 0, 0), Layer = wall });
            cad.Entities.Add(new TextEntity { Value = "거실", InsertPoint = new XYZ(60, 40, 0), Height = 14 });
            var fill = new Hatch { Pattern = new HatchPattern("ANSI31") };
            var boundary = new Hatch.BoundaryPath(); boundary.Edges.Add(new Hatch.BoundaryPath.Polyline(new[] { new XYZ(20, 70, 0), new XYZ(180, 70, 0), new XYZ(180, 110, 0), new XYZ(20, 110, 0) }, true));
            fill.Paths.Add(boundary); cad.Entities.Add(fill);
            string file = Path.Combine(root, "이름 없는 층 평면.dxf"); DxfWriter.Write(file, cad);
            var plain = Import(file, CadImportStructure.Objects, null);
            var objects = Lines(plain, "0");
            Check(objects.Any(l => l.Name.StartsWith("문자", StringComparison.Ordinal)) && objects.Any(l => l.Name.StartsWith("해치", StringComparison.Ordinal)), "The plan has no text and hatch objects on layer 0: " + string.Join(", ", objects.Select(l => l.Name)));
            var w = new MainWindow(null) { headlessTesting = true };
            try
            {
                w.AddTab(plain.Snapshot(), null);
                w.selectedLayers.Clear(); w.doc.ActiveId = w.doc.Layers.First(l => l.Kind == LayerKind.Raster || l.Kind == LayerKind.Shape).Id;
                Reapply(w);
                Check(w.history.CanUndo, "The wall was not restyled");
                Check(Same(w.doc, plain, "0"), "Objects on layer 0 were restyled by their generated names");
                Settle(w);
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("선 정리 다시 적용 runs off the UI thread, Esc cancels it without a step, and a changed document is left alone", () =>
        {
            string file = Plan("백그라운드 평면.dxf", false);
            var plain = Import(file, CadImportStructure.Objects, null);
            var w = new MainWindow(null) { headlessTesting = true };
            try
            {
                w.AddTab(plain.Snapshot(), null);
                bool pending = false;
                // Esc cancels the running job (Shortcuts: jobCts).
                bool applied = WaitOnDispatcher(() => { var task = w.ReapplyLineCleanupAsync(); pending = !task.IsCompleted && w.jobCts != null; w.jobCts?.Cancel(); return task; });
                Check(pending, "Line cleanup finished before returning to the window: it ran on the UI thread");
                Check(!applied && !w.history.CanUndo && Same(w.doc, plain, "A-WALL") && w.jobCts == null && w.status.Text.Contains("취소"), "Cancelling line cleanup still changed the drawing");
                // An edit made while it runs wins; the stale result is dropped.
                applied = WaitOnDispatcher(() => { var task = w.ReapplyLineCleanupAsync(); w.Edit("레이어 이름", () => w.doc.Layers[^1].Name = "바뀐 이름"); return task; });
                Check(!applied && w.history.UndoLabel == "레이어 이름" && Same(w.doc, plain, "A-WALL"), "A result computed for an older document was applied");
                w.Undo();
                Check(WaitOnDispatcher(w.ReapplyLineCleanupAsync) && w.history.UndoLabel == "선 정리 다시 적용" && !Same(w.doc, plain, "A-WALL") && w.jobCts == null, "Line cleanup did not finish as one step");
                Settle(w);
            }
            finally { w.StopRenderingForShutdown(); }
        });
    }
}
