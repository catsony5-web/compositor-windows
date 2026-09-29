using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunShadowCommandTests(Action<string, Action> test)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static Document Board()
        {
            var document = new Document { Width = 200, Height = 160, Name = "그림자 명령" };
            document.Add(new Layer { Name = "배경", Pixels = Raster.Solid(200, 160, Colors.White) });
            document.Add(new Layer { Name = "사람", Pixels = Raster.Solid(20, 50, Colors.Black), X = 60, Y = 40 });
            return document;
        }
        MainWindow Open(Document document)
        {
            var window = new MainWindow(null) { headlessTesting = true };
            window.AddTab(document, null);
            var figure = window.doc.Layers.Single(l => l.Name == "사람");
            window.doc.ActiveId = figure.Id; window.selectedLayers.Clear(); window.selectedLayers.Add(figure.Id);
            return window;
        }
        static string[] Rows(MainWindow window) => window.properties.Children.OfType<Button>().Select(b => AutomationProperties.GetName(b)).ToArray();

        test("UI shadow: the command adds one multiply layer below the source as one undo step", () =>
        {
            var w = Open(Board());
            try
            {
                var figure = w.doc.Active!; int count = w.doc.Layers.Count;
                ShadowSpec? offered = null;
                w.shadowDialogHandler = dialog => { offered = dialog.Spec; dialog.StyleChoice.Select(ShadowStyle.Shape); return dialog.TryCommit(); };
                w.AddShadow();
                var shadow = w.doc.Active!;
                Check(offered is { Projection: ShadowProjection.Drop } && offered.Sources.SequenceEqual([figure.Id]), "The dialog did not start from the selected photo layer");
                Check(w.doc.Layers.Count == count + 1 && w.doc.Layers.IndexOf(shadow) + 1 == w.doc.Layers.IndexOf(figure), "The shadow is not directly below its source");
                Check(shadow.Blend == BlendMode.Multiply && shadow.Shadow is { Style: ShadowStyle.Shape } && shadow.Name.EndsWith("사람"), "The shadow layer lost its settings");
                Check(w.history.UndoLabel == "그림자 추가" && w.selectedLayers.SetEquals([shadow.Id]), "Creation was not one selected undo step");
                w.Undo(); Check(w.doc.Layers.Count == count && !w.history.CanUndo, "Undo did not remove the shadow in one step");
                w.Redo(); Check(w.doc.Layers.Any(l => l.Shadow != null), "Redo did not restore the shadow");
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("UI shadow: reopening the command on a shadow edits it and regeneration follows the source", () =>
        {
            var w = Open(Board());
            try
            {
                var figure = w.doc.Active!;
                var shadow = w.CreateShadow(ShadowSpec.Default(ShadowProjection.Drop) with { Angle = 0, Softness = 0, Sources = [figure.Id] })!;
                double x = shadow.X, y = shadow.Y; var original = shadow.Shadow;
                ShadowSpec? offered = null;
                w.shadowDialogHandler = dialog => { offered = dialog.Spec; dialog.ApplyPreset("정오"); return dialog.TryCommit(); };
                w.AddShadow();
                var edited = w.doc.Layers.Single(l => l.Id == shadow.Id);
                Check(offered == original && w.doc.Layers.Count(l => l.Shadow != null) == 1, "The command did not reopen the existing shadow");
                Check(edited.Shadow!.Angle == 90 && edited.Y > y && Math.Abs(edited.X - x) > 1, "Editing did not recast the shadow");
                Check(w.history.UndoLabel == "그림자 편집", "Editing was not its own undo step");
                w.doc.ActiveId = figure.Id; w.EditLayer("사람 이동", l => l.X += 30);
                w.doc.ActiveId = shadow.Id; double before = w.doc.Active!.X;
                w.RegenerateShadow();
                Check(Math.Abs(w.doc.Active!.X - before - 30) <= 1 && w.history.UndoLabel == "그림자 다시 만들기", "Regenerating did not follow the moved source");
                w.Undo(); Check(Math.Abs(w.doc.Layers.Single(l => l.Id == shadow.Id).X - before) < .001, "Undo did not restore the previous shadow");
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("UI shadow: menu, command search, ribbon and inspector offer the shadow commands", () =>
        {
            var w = Open(Board());
            try
            {
                var commands = w.BuildCommandRegistry();
                Check(commands.Any(c => c.Id == "menu:레이어/그림자 추가…" && c.IsAvailable()) && commands.Any(c => c.Id == "menu:레이어/그림자 다시 만들기"), "The shadow commands are missing from the menu and search");
                Check(RibbonGlyph("그림자 추가…") == Theme.Glyphs.Shadow && GroupTitle("레이어", new MenuItem { Header = "그림자 추가…" }) == "그림자", "The ribbon lacks the shadow icon or group");
                w.BuildProperties();
                Check(Rows(w).Contains("그림자 추가") && !Rows(w).Contains("그림자 편집"), "The inspector lacks the add action");
                w.doc.Active!.Locked = true; w.BuildProperties();
                Check(w.properties.Children.OfType<Button>().Single(b => AutomationProperties.GetName(b) == "그림자 추가").IsEnabled, "A locked source cannot cast a shadow");
                w.doc.Active!.Locked = false;
                var shadow = w.CreateShadow(ShadowSpec.Default(ShadowProjection.Drop) with { Sources = [w.doc.ActiveId] })!;
                w.BuildProperties();
                Check(Rows(w).Contains("그림자 편집") && Rows(w).Contains("원본에 맞춰 다시 만들기") && !Rows(w).Contains("그림자 추가"), "A shadow layer does not offer edit and regenerate");
                var group = DocumentFeatures.CreateGroup(w.doc, "잠금 그룹"); group.Locked = true; w.doc.Add(group);
                var inside = new Layer { Name = "안", Pixels = Raster.Solid(10, 10, Colors.Black), ParentId = group.Id }; w.doc.Add(inside);
                w.doc.ActiveId = inside.Id; w.selectedLayers.Clear(); w.selectedLayers.Add(inside.Id); int layers = w.doc.Layers.Count;
                w.shadowDialogHandler = _ => throw new InvalidOperationException("The dialog opened for a locked group");
                w.AddShadow();
                Check(w.doc.Layers.Count == layers && w.status.Text.Contains("잠금"), "A shadow was added inside a locked group");
            }
            finally { w.StopRenderingForShutdown(); }
        });

        test("UI shadow: the dialog previews both styles and all projections without touching the document", () =>
        {
            var document = Board(); var figure = document.Layers[1];
            var before = document.Snapshot();
            var dialog = new ShadowDialog(null, document, ShadowSpec.Default(ShadowProjection.Drop) with { Distance = 30, Softness = 0, Opacity = 1, Sources = [figure.Id] });
            try
            {
                dialog.RefreshPreviewNow();
                var drop = dialog.PreviewRaster!; int i = (70 * drop.Width + 95) * 4;
                Check(drop.Width == 200 && drop.Data[i] < 60 && dialog.Info.Contains("200 × 160"), "The preview does not show the shadow");
                dialog.StyleChoice.Select(ShadowStyle.Shape); dialog.ProjectionChoice.Select(ShadowProjection.Ground);
                Check(dialog.Spec.Style == ShadowStyle.Shape && dialog.Spec.Projection == ShadowProjection.Ground && dialog.Spec.Angle == ShadowSpec.Default(ShadowProjection.Ground).Angle, "The choices did not update the settings");
                dialog.RefreshPreviewNow();
                Check(!dialog.PreviewRaster!.Data.SequenceEqual(drop.Data), "The preview did not follow the projection");
                dialog.ProjectionChoice.Select(ShadowProjection.Plan); dialog.RefreshPreviewNow();
                Check(dialog.TryCommit() && dialog.Spec.Projection == ShadowProjection.Plan, "A valid setting was not accepted");
                Check(SameDocument(before, document) && document.Layers.Count == 2, "The preview changed the document");
            }
            finally { dialog.Close(); }
        });
    }
}
