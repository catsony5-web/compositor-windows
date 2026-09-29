using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Compositor.Windows;

/// <summary>Before/after comparison in the adjustment (photo develop) dialog, driven without showing a window.</summary>
public static class BeforeAfterTests
{
    // Routed key events need a source; this never creates an HWND or touches real input.
    sealed class KeySource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    public static void Run(Action<string, Action> test)
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static AdjustmentSpec Develop(double exposure) => new() { Kind = AdjustmentKind.PhotoDevelop, PhotoDevelop = new() { Exposure = exposure, Contrast = 12 } };
        static Document Photo(int width = 12, int height = 8)
        {
            var pixels = new Raster(width, height);
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                pixels.Data[i] = (byte)(40 + x * 150 / width); pixels.Data[i + 1] = (byte)(60 + y * 120 / height); pixels.Data[i + 2] = (byte)(90 + (x + y) * 5 % 100); pixels.Data[i + 3] = 255;
            }
            var document = new Document { Width = width, Height = height }; document.Add(new Layer { Name = "사진", Pixels = pixels });
            return document;
        }
        // Async preview continuations must come back to this thread, as they do in the running app.
        static void WithDispatcher(Action body)
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { body(); } finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }
        static void Wait(AdjustmentDialog dialog) => Check(dialog.WaitForPreviews(TimeSpan.FromSeconds(30)), "Preview rendering did not finish");
        static byte[] Pixels(BitmapSource bitmap)
        {
            var converted = bitmap.Format == PixelFormats.Bgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            var data = new byte[converted.PixelWidth * converted.PixelHeight * 4]; converted.CopyPixels(data, converted.PixelWidth * 4, 0); return data;
        }
        static (byte[] Data, int Width) Snapshot(FrameworkElement element, int width, int height)
        {
            var size = new Size(width, height); element.Measure(size); element.Arrange(new Rect(size)); element.UpdateLayout();
            var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(element);
            return (Pixels(image), width);
        }
        static Color At((byte[] Data, int Width) shot, double x, double y)
        {
            int i = ((int)y * shot.Width + (int)x) * 4;
            return Color.FromArgb(shot.Data[i + 3], shot.Data[i + 2], shot.Data[i + 1], shot.Data[i]);
        }
        static bool Near(Color a, Color b, int tolerance = 3) => Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance && Math.Abs(a.A - b.A) <= tolerance;
        static BitmapSource Solid(Color color, int width = 40, int height = 20) => Raster.Solid(width, height, color).Bitmap();
        static BitmapSource Gradient(int width, int height)
        {
            var raster = new Raster(width, height);
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            { int i = (y * width + x) * 4; raster.Data[i] = (byte)(x * 255 / (width - 1)); raster.Data[i + 1] = (byte)(y * 255 / (height - 1)); raster.Data[i + 2] = (byte)((x * 7 + y * 13) % 256); raster.Data[i + 3] = 255; }
            return raster.Bitmap();
        }
        static Slider NamedSlider(DependencyObject root, string name) => Descendants<Slider>(root).Single(s => AutomationProperties.GetName(s) == name);
        static void Press(UIElement target, RoutedEvent routedEvent, Key key, KeySource source, out bool handled)
        {
            var e = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = routedEvent };
            target.RaiseEvent(e); handled = e.Handled;
        }

        test("develop compare renders before once while only after follows slider changes", () => WithDispatcher(() =>
        {
            var document = Photo(); var dialog = new AdjustmentDialog(null, document, Develop(.8));
            try
            {
                dialog.StartPreview(); Wait(dialog);
                Check(dialog.CompareModes.Selected == CompareMode.Result && dialog.AfterRenderCount == 1 && dialog.BeforeRenderCount == 0, "Default result view rendered the before image or missed the after image");
                dialog.SelectCompareMode(CompareMode.Split); Wait(dialog);
                Check(dialog.BeforeRenderCount == 1 && dialog.AfterRenderCount == 1, "Opening the split view re-rendered the adjusted image");
                Check(dialog.BeforePreview!.Data.SequenceEqual(Imaging.Render(document).Data), "Before image is not the document without this adjustment");
                var exposure = NamedSlider(dialog, "노출 EV");
                exposure.Value = 1.6; Wait(dialog); exposure.Value = -.7; Wait(dialog);
                Check(dialog.AfterRenderCount == 3 && dialog.BeforeRenderCount == 1, $"Slider changes rendered before {dialog.BeforeRenderCount}x / after {dialog.AfterRenderCount}x");
                var expected = Imaging.Render(AdjustmentDialog.PreviewDocument(document, null, dialog.Spec, true, null));
                Check(Math.Abs(dialog.Spec.PhotoDevelop.Exposure + .7) < 1e-9 && dialog.AfterPreview!.Data.SequenceEqual(expected.Data), "After image is not the adjusted preview for the current settings");
                Check(Pixels(dialog.CompareView.After!).SequenceEqual(expected.Data) && Pixels(dialog.CompareView.Before!).SequenceEqual(Imaging.Render(document).Data), "Stage shows different pixels than the rendered states");
                foreach (var mode in new[] { CompareMode.SideBySide, CompareMode.Toggle, CompareMode.Result, CompareMode.Split, CompareMode.Result })
                { dialog.SelectCompareMode(mode); Wait(dialog); }
                dialog.PreviewToggle.IsChecked = false; dialog.PreviewToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Wait(dialog);
                Check(dialog.CompareView.DisplaysBefore && dialog.BeforeRenderCount == 1 && dialog.AfterRenderCount == 3, "Switching views or the preview check box rendered again");
            }
            finally { dialog.Close(); }
        }));

        test("develop compare before hides a re-edited adjustment and keeps the selection for after", () => WithDispatcher(() =>
        {
            var document = Photo(); var layer = DocumentFeatures.CreateAdjustment(document, Develop(1)); document.Add(layer);
            var edit = new AdjustmentDialog(null, document, layer.Adjustment!, layer.Id);
            try
            {
                edit.StartPreview(); edit.SelectCompareMode(CompareMode.SideBySide); Wait(edit);
                var hidden = document.Snapshot(); hidden.Layers.Single(l => l.Id == layer.Id).Visible = false;
                Check(edit.BeforePreview!.Data.SequenceEqual(Imaging.Render(hidden).Data), "Re-edit before image still contains the edited adjustment");
                Check(edit.AfterPreview!.Data.SequenceEqual(Imaging.Render(document).Data) && !edit.BeforePreview.Data.SequenceEqual(edit.AfterPreview.Data), "Re-edit after image differs from the current adjustment");
                Check(layer.Visible && layer.Adjustment!.PhotoDevelop.Exposure == 1, "Comparison modified the source document");
            }
            finally { edit.Close(); }
            var plain = Photo(); var spec = Develop(1.2);
            var masked = new AdjustmentDialog(null, plain, spec, null, new Selection(new Rect(0, 0, 6, 8)));
            try
            {
                masked.StartPreview(); masked.SelectCompareMode(CompareMode.Split); Wait(masked);
                var before = masked.BeforePreview!.Data; var after = masked.AfterPreview!.Data; var source = Imaging.Render(plain).Data;
                Check(before.SequenceEqual(source), "Selection changed the before image");
                Check(after[4 * 2] > before[4 * 2] && after[(8 * 12 - 1) * 4] == before[(8 * 12 - 1) * 4], "After image ignored the selection mask");
            }
            finally { masked.Close(); }
        }));

        test("develop compare split draws before left of the divider and after right with aligned halves", () =>
        {
            var red = Color.FromRgb(210, 40, 40); var blue = Color.FromRgb(40, 70, 220);
            var view = new BeforeAfterView { Before = Solid(red), After = Solid(blue), Mode = CompareMode.Split };
            var rect = view.ImageRects(new Size(424, 224)).Single();
            Check(rect == new Rect(12, 12, 400, 200), "Split image rectangle is not the fitted stage area: " + rect);
            var shot = Snapshot(view, 424, 224);
            Check(Near(At(shot, 60, 180), red) && Near(At(shot, 180, 180), red), "Left of the divider is not the before image");
            Check(Near(At(shot, 244, 180), blue) && Near(At(shot, 380, 180), blue), "Right of the divider is not the after image");
            Check(Near(At(shot, 212, 60), ((SolidColorBrush)Theme.Text).Color, 24), "Divider line is not visible at the middle");
            Check(Near(At(shot, 212, 112), ((SolidColorBrush)Theme.Surface).Color, 40) || Near(At(shot, 205, 112), ((SolidColorBrush)Theme.Surface).Color, 40), "Divider handle is not drawn at the vertical center");
            view.Divider = .25; shot = Snapshot(view, 424, 224);
            Check(view.DividerX(rect) == 112 && Near(At(shot, 80, 180), red) && Near(At(shot, 150, 180), blue), "Moving the divider did not move the boundary");
            // Same content in both states: the split frame must match the single frame pixel for pixel away from the bar.
            var gradient = Gradient(40, 20);
            var split = new BeforeAfterView { Before = Gradient(40, 20), After = gradient, Mode = CompareMode.Split, Divider = .5 };
            var single = new BeforeAfterView { After = gradient, Mode = CompareMode.Result };
            var a = Snapshot(split, 424, 224); var b = Snapshot(single, 424, 224);
            for (int y = 150; y < 210; y += 3) for (int x = 14; x < 410; x += 3)
                if (Math.Abs(x - 212) > 4 && !Near(At(a, x, y), At(b, x, y), 1)) throw new Exception($"Before and after halves are misaligned at {x},{y}");
        });

        test("develop compare divider clamps to the image and follows arrow keys", () =>
        {
            var view = new BeforeAfterView { Before = Solid(Colors.Red), After = Solid(Colors.Blue), Mode = CompareMode.Split };
            view.Divider = -3; Check(view.Divider == 0, "Divider went below zero");
            view.Divider = 7; Check(view.Divider == 1, "Divider went above one");
            view.Divider = double.NaN; Check(view.Divider == 1, "NaN moved the divider");
            view.Divider = .5;
            Check(view.HandleKey(Key.Right, false) && view.Divider == .51, "Right arrow did not move 1%");
            view.HandleKey(Key.Left, false); view.HandleKey(Key.Left, false); Check(view.Divider == .49, "Left arrow did not move 1%");
            view.HandleKey(Key.Right, true); Check(view.Divider == .59, "Shift+arrow did not move 10%");
            view.HandleKey(Key.Home, false); view.HandleKey(Key.Left, false); Check(view.Divider == 0, "Home or clamping failed");
            view.HandleKey(Key.End, false); view.HandleKey(Key.Right, true); Check(view.Divider == 1, "End or clamping failed");
            var source = new KeySource { RootVisual = view }; view.Divider = .5;
            Press(view, Keyboard.KeyDownEvent, Key.Left, source, out bool handled);
            Check(handled && view.Divider is .49 or .4, "Routed arrow key was not consumed by the focused divider");
            Snapshot(view, 424, 224);
            view.MoveDividerTo(new Point(112, 50)); Check(Math.Abs(view.Divider - .25) < 1e-9, "Dragging did not follow the pointer");
            view.MoveDividerTo(new Point(-80, 50)); Check(view.Divider == 0, "Dragging left of the image did not clamp");
            view.MoveDividerTo(new Point(900, 50)); Check(view.Divider == 1, "Dragging right of the image did not clamp");
            view.Mode = CompareMode.Result;
            Check(!view.HandleKey(Key.Left, false) && view.Divider == 1, "Arrow keys were captured outside the split view");
        });

        test("develop compare side by side keeps one scale and stacks in tall preview areas", () =>
        {
            var red = Color.FromRgb(200, 30, 30); var blue = Color.FromRgb(30, 60, 200);
            var view = new BeforeAfterView { Before = Solid(red, 30, 20), After = Solid(blue, 30, 20), Mode = CompareMode.SideBySide };
            var wide = view.ImageRects(new Size(824, 424));
            Check(wide.Length == 2 && wide[0].Size == wide[1].Size && wide[1].X >= wide[0].Right + BeforeAfterView.Gap - 1 && Math.Abs(wide[0].Y - wide[1].Y) < 1, "Wide area did not place equal images left and right");
            var tall = view.ImageRects(new Size(424, 824));
            Check(tall.Length == 2 && tall[0].Size == tall[1].Size && tall[1].Y >= tall[0].Bottom + BeforeAfterView.Gap - 1 && Math.Abs(tall[0].X - tall[1].X) < 1, "Tall area did not stack equal images");
            Check(!BeforeAfterView.StackVertically(new Size(800, 400), 30, 20) && BeforeAfterView.StackVertically(new Size(400, 800), 30, 20), "Wide areas must go left and right, tall areas top and bottom");
            Check(BeforeAfterView.StackVertically(new Size(800, 800), 30, 20) && !BeforeAfterView.StackVertically(new Size(400, 800), 10, 40), "Orientation does not pick the larger image scale");
            var shot = Snapshot(view, 824, 424);
            Check(Near(At(shot, wide[0].X + wide[0].Width / 2, wide[0].Y + wide[0].Height * .75), red) && Near(At(shot, wide[1].X + wide[1].Width / 2, wide[1].Y + wide[1].Height * .75), blue), "Side-by-side cells show the wrong states");
            shot = Snapshot(view, 424, 824);
            Check(Near(At(shot, tall[0].X + tall[0].Width / 2, tall[0].Y + tall[0].Height * .75), red) && Near(At(shot, tall[1].X + tall[1].Width / 2, tall[1].Y + tall[1].Height * .75), blue), "Stacked cells show the wrong states");
        });

        test("develop compare toggle switches by click button key and hold", () => WithDispatcher(() =>
        {
            var dialog = new AdjustmentDialog(null, Photo(), Develop(.9));
            try
            {
                dialog.StartPreview(); dialog.SelectCompareMode(CompareMode.Toggle); Wait(dialog);
                var view = dialog.CompareView; var source = new KeySource { RootVisual = (Visual)dialog.Content };
                Check(dialog.HoldBeforeButton.Visibility == Visibility.Visible && dialog.PreviewToggle.IsEnabled && dialog.BeforeRenderCount == 1, "Toggle controls are not offered or before was not prepared");
                Check(!view.DisplaysBefore && view.StateLabel == BeforeAfterView.AfterText, "Toggle view does not start on the adjusted image");
                view.Click(); Check(view.DisplaysBefore && view.StateLabel == BeforeAfterView.BeforeText && dialog.PreviewToggle.IsChecked == false, "Clicking the image did not switch to before");
                dialog.PreviewToggle.IsChecked = true; dialog.PreviewToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check(!view.DisplaysBefore && view.StateLabel == BeforeAfterView.AfterText, "Result check box did not switch back to after");
                Check(view.HandleKey(Key.Space, false) && view.DisplaysBefore, "Space on the focused image did not switch"); view.Click();
                Press(dialog, Keyboard.PreviewKeyDownEvent, Key.Oem5, source, out bool down);
                Check(down && view.HoldBefore && view.DisplaysBefore && view.StateLabel == BeforeAfterView.BeforeText, "Holding \\ did not show before");
                Press(dialog, Keyboard.PreviewKeyUpEvent, Key.Oem5, source, out bool up);
                Check(up && !view.HoldBefore && !view.DisplaysBefore, "Releasing \\ did not return to after");
                var number = Descendants<TextBox>(dialog).First();
                Press(number, Keyboard.PreviewKeyDownEvent, Key.Oem5, source, out bool typed);
                Check(!typed && !view.HoldBefore, "\\ typed into a number field switched the preview");
                dialog.HoldBeforeButton.SimulateHold(true); Check(view.DisplaysBefore, "Hold button did not show before");
                dialog.HoldBeforeButton.SimulateHold(false); Check(!view.DisplaysBefore, "Releasing the hold button did not show after");
                dialog.SelectCompareMode(CompareMode.Split);
                Check(dialog.HoldBeforeButton.Visibility == Visibility.Collapsed && !dialog.PreviewToggle.IsEnabled, "Two-image view kept single-image controls active");
                Press(dialog, Keyboard.PreviewKeyDownEvent, Key.Oem5, source, out bool splitKey); view.Click();
                Check(!splitKey && !view.HoldBefore && dialog.PreviewToggle.IsChecked == true, "Hold key or click changed the split view");
                Check(dialog.BeforeRenderCount == 1, "Toggling rendered the before image again");
            }
            finally { dialog.Close(); }
        }));

        test("develop compare offers four views with result as default in every adjustment dialog", () => WithDispatcher(() =>
        {
            foreach (var kind in new[] { AdjustmentKind.PhotoDevelop, AdjustmentKind.Levels, AdjustmentKind.Curves })
            {
                var dialog = new AdjustmentDialog(null, Photo(), new AdjustmentSpec { Kind = kind });
                try
                {
                    var names = dialog.CompareModes.Buttons.Select(AutomationProperties.GetName).ToArray();
                    Check(names.SequenceEqual(["결과", "좌우 분할", "나란히", "전후 전환"]) && dialog.CompareModes.Buttons.All(b => b.ToolTip is string { Length: > 5 }), kind + " compare modes are missing names or tips");
                    Check(dialog.CompareModes.Selected == CompareMode.Result && dialog.CompareView.Mode == CompareMode.Result && dialog.PreviewToggle.IsEnabled && dialog.HoldBeforeButton.Visibility == Visibility.Collapsed, kind + " does not open on the single result view");
                    dialog.SelectCompareMode(CompareMode.SideBySide); Wait(dialog);
                    Check(!dialog.PreviewToggle.IsEnabled && dialog.CompareView.Before != null, kind + " side-by-side did not prepare the before image");
                    dialog.SelectCompareMode(CompareMode.Result); Check(dialog.PreviewToggle.IsEnabled, kind + " result view left the preview check box disabled");
                }
                finally { dialog.Close(); }
            }
            foreach (var (_, _, glyph, _) in CompareControls.Modes)
                foreach (var layer in glyph.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var bounds = Geometry.Parse(layer.TrimStart('~', '*')).Bounds;
                    Check(bounds.Left >= 2 && bounds.Top >= 2 && bounds.Right <= 22 && bounds.Bottom <= 22, "Compare glyph leaves the 24-unit grid: " + layer);
                }
            Check(CompareControls.IsHoldKey(Key.Oem5) && CompareControls.IsHoldKey(Key.OemBackslash) && !CompareControls.IsHoldKey(Key.Space), "Hold key mapping changed");
        }));

        test("develop compare keeps preview-size rasters for large photos", () => WithDispatcher(() =>
        {
            var small = Raster.Solid(40, 30, Colors.Red);
            Check(ReferenceEquals(PreviewScaling.Fit(small, 64), small), "Small image was copied");
            var wide = new Raster(5000, 40);
            for (int y = 0; y < 40; y++) for (int x = 0; x < 5000; x++)
            {
                int i = (y * 5000 + x) * 4; bool left = x < 2500, hidden = x % 2 == 1;
                wide.Data[i] = (byte)(left ? 0 : 255); wide.Data[i + 1] = (byte)(hidden ? 255 : 0); wide.Data[i + 2] = (byte)(left ? 255 : 0); wide.Data[i + 3] = (byte)(hidden ? 0 : 255);
            }
            var fitted = PreviewScaling.Fit(wide, 2560);
            Check(fitted.Width == 2560 && fitted.Height == 20, $"Fitted size is {fitted.Width}x{fitted.Height}");
            int l = (10 * 2560 + 10) * 4, r = (10 * 2560 + 2550) * 4;
            Check(fitted.Data[l + 2] == 255 && fitted.Data[l + 1] == 0 && fitted.Data[r] == 255 && fitted.Data[r + 1] == 0 && Math.Abs(fitted.Data[l + 3] - 128) <= 1, "Box filter bled hidden color or lost coverage");
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel(); bool stopped = false;
                try { PreviewScaling.Fit(wide, 2560, canceled.Token); } catch (OperationCanceledException) { stopped = true; }
                Check(stopped, "Canceled preview scaling completed");
            }
            var document = new Document { Width = 3000, Height = 12 }; document.Add(new Layer { Pixels = Raster.Solid(3000, 12, Color.FromRgb(120, 110, 100)) });
            var dialog = new AdjustmentDialog(null, document, Develop(.5));
            try
            {
                dialog.StartPreview(); dialog.SelectCompareMode(CompareMode.Split); Wait(dialog);
                Check(dialog.AfterPreview!.Width == AdjustmentDialog.PreviewMaxSide && dialog.BeforePreview!.Width == AdjustmentDialog.PreviewMaxSide, "Dialog kept full-resolution comparison images");
                Check(dialog.CompareView.Before!.PixelWidth == dialog.CompareView.After!.PixelWidth && dialog.CompareView.Before.PixelHeight == dialog.CompareView.After.PixelHeight, "Before and after preview sizes differ");
            }
            finally { dialog.Close(); }
        }));
    }

    static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is not DependencyObject dependency) continue;
            if (dependency is T match) yield return match;
            foreach (T nested in Descendants<T>(dependency)) yield return nested;
        }
    }
}
