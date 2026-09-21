using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;

namespace Compositor.Windows;

public static class ParameterSliderTests
{
    public static void Run(Action<string, Action> test)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static void Near(double value, double expected) => Check(Math.Abs(value - expected) < 1e-9, $"Expected {expected}, got {value}");
        static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            {
                yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }
        static Slider Track(ParameterSlider control) => Descendants(control).OfType<Slider>().Single();
        static TextBox Input(ParameterSlider control) => Descendants(control).OfType<TextBox>().Single();
        static ComboBox Steps(ParameterSlider control) => Descendants(control).OfType<ComboBox>().Single();
        static void SelectStep(ParameterSlider control, double value) => Steps(control).SelectedItem = Steps(control).Items.OfType<ComboBoxItem>().Single(item => item.Tag is double step && step == value);

        test("parameter precision opt-in preserves compact legacy sliders and filters step options", () =>
        {
            var compact = new ParameterSlider("크기", 1, 2001, 42);
            Check(!Descendants(compact).OfType<ComboBox>().Any() && Track(compact).GetType() == typeof(Slider), "Compact brush slider acquired precision controls");
            Near(Track(compact).SmallChange, 10); Near(Track(compact).LargeChange, 100);
            var narrow = new ParameterSlider("작은 범위", 0, .5, .25, showStepControls: true);
            var values = Steps(narrow).Items.OfType<ComboBoxItem>().Select(item => (double)item.Tag).ToArray();
            Check(values.SequenceEqual(new[] { 0, .01, .1 }) && AutomationProperties.GetName(Steps(narrow)) == "작은 범위 이동 간격", "Step options exceed the range or lack an accessible label");
            narrow.Measure(new Size(248, double.PositiveInfinity)); narrow.Arrange(new Rect(0, 0, 248, narrow.DesiredSize.Height));
            Check(narrow.Children.OfType<FrameworkElement>().All(child => child.ActualWidth + child.Margin.Left + child.Margin.Right <= 248.1), "Step row overflows a narrow panel");
        });
        test("parameter movement mode preserves the current value and snaps to zero-anchored units", () =>
        {
            var control = new ParameterSlider("노출", -100, 100, 0, showStepControls: true);
            int notifications = 0; control.Changed += _ => notifications++;
            Track(control).Value = 3.789;
            Near(control.Value, 3.789); Check(notifications == 1, "Continuous movement lost precision or notified twice");
            SelectStep(control, 5);
            Near(control.Value, 3.789); Check(notifications == 1, "Changing movement mode edited the value");
            Track(control).Value = 7.7; Near(control.Value, 10);
            Track(control).Value = 9.1; Near(control.Value, 10);
            Check(notifications == 2, "Movement inside the same snapped cell emitted a duplicate notification");
            Track(control).Value = -7.7; Near(control.Value, -10);
            SelectStep(control, .1); Near(control.Value, -10);
            Track(control).Value = -.26; Near(control.Value, -.3);
            Check(notifications == 4, "Snapping published intermediate unsnapped values");
        });
        test("parameter snapping keeps nonzero lower bounds and both endpoints reachable", () =>
        {
            var control = new ParameterSlider("입력 흰색", 1, 255, 31, showStepControls: true);
            SelectStep(control, 5);
            Track(control).Value = 8.8; Near(control.Value, 10);
            Track(control).Value = 1; Near(control.Value, 1);
            control.AdjustByKey(Key.Right); Near(control.Value, 5);
            control.AdjustByKey(Key.Left); Near(control.Value, 1);
            Track(control).Value = 255; Near(control.Value, 255);
            control.AdjustByKey(Key.Left); Near(control.Value, 250);
            control.AdjustByKey(Key.End); Near(control.Value, 255);
            control.AdjustByKey(Key.Home); Near(control.Value, 1);
        });
        test("parameter direct typing model sync and reset bypass the movement step without duplicate commits", () =>
        {
            var control = new ParameterSlider("감마", 0, 10, 1, .25, showStepControls: true);
            int notifications = 0; control.Changed += _ => notifications++;
            SelectStep(control, 5);
            Input(control).Text = "3.141592653589793";
            Check(control.TryCommit() && control.Value == Math.PI && notifications == 1, "Typing quantized or truncated committed precision");
            Check(control.TryCommit() && control.Value == Math.PI && notifications == 1, "Recommitting formatted text rounded the stored value");
            control.SetValue(Math.E); Check(control.Value == Math.E && notifications == 1, "Model synchronization applied movement snap or notified");
            var reset = Descendants(control).OfType<Button>().Single(button => Equals(button.ToolTip, "감마 초기화"));
            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Near(control.Value, .25);
            Check(notifications == 2, "Reset did not publish its exact default once");
            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(notifications == 2, "Resetting an unchanged default notified again");
        });
        test("parameter invalid numeric input stays visible and cannot publish a value", () =>
        {
            var control = new ParameterSlider("색조", -180, 180, 12.3456789, showStepControls: true);
            int notifications = 0; control.Changed += _ => notifications++;
            foreach (string invalid in new[] { "", "--", "NaN", "Infinity", "181", "-181" })
            {
                Input(control).Text = invalid;
                Check(!control.IsInputValid && !control.TryCommit() && Input(control).Text == invalid && notifications == 0,
                    "Invalid numeric input was accepted, replaced or notified");
                Near(control.Value, 12.3456789);
            }
            Input(control).Text = "-0.125";
            Check(control.IsInputValid && control.TryCommit() && notifications == 1, "Valid replacement did not recover from validation error");
            Near(control.Value, -.125);
        });
        test("parameter arrow controls provide fine continuous changes and exact selected step changes", () =>
        {
            var small = new ParameterSlider("노출 EV", -5, 5, 0, showStepControls: true);
            Check(small.AdjustByKey(Key.Right), "Right key was not handled"); Near(small.Value, .01);
            small.AdjustByKey(Key.Up, true); Near(small.Value, .011);
            small.AdjustByKey(Key.Down, true); Near(small.Value, .01);
            Check(!small.AdjustByKey(Key.Tab), "Parameter control trapped keyboard navigation");
            var large = new ParameterSlider("채도", -100, 100, 0, showStepControls: true);
            large.AdjustByKey(Key.Left); Near(large.Value, -.1);
            large.AdjustByKey(Key.Right, true); Near(large.Value, -.09);
            SelectStep(large, 5); large.AdjustByKey(Key.Right, true); Near(large.Value, 0);
            large.AdjustByKey(Key.Right, true); Near(large.Value, 5);
            large.IsEnabled = false; Check(!large.AdjustByKey(Key.Right) && large.Value == 5, "Disabled parameter accepted keyboard changes");
        });
        test("parameter drag accumulates raw distance through snapped positions and supports fine shift motion", () =>
        {
            var control = new ParameterSlider("대비", 0, 100, 0, showStepControls: true);
            int notifications = 0; control.Changed += _ => notifications++;
            SelectStep(control, 5); control.BeginUserDrag();
            for (int i = 0; i < 30; i++) control.ApplyUserDragDelta(.2);
            Near(control.Value, 5); Check(notifications == 1, "Small drag increments stuck at the rounded thumb position");
            for (int i = 0; i < 10; i++) control.ApplyUserDragDelta(.2);
            Near(control.Value, 10); Check(notifications == 2, "Drag accumulator failed to reach the next unit");
            control.EndUserDrag(); control.ApplyUserDragDelta(20); Near(control.Value, 10);
            SelectStep(control, 0); control.SetValue(0); control.BeginUserDrag();
            for (int i = 0; i < 10; i++) control.ApplyUserDragDelta(.2, true);
            Near(control.Value, .2);
            control.ApplyUserDragDelta(.2, false); Near(control.Value, .4); control.EndUserDrag();
        });
        test("parameter routed thumb drag reaches precision accumulation and stops after completion", () =>
        {
            var control = new ParameterSlider("실제 드래그", 0, 100, 0, showStepControls: true);
            var slider = Track(control);
            // Use the production PART_Track/Thumb contract without requiring an Application or creating a window.
            slider.Template = (ControlTemplate)XamlReader.Parse("""
                <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="{x:Type Slider}">
                    <Grid Height="20">
                        <Track x:Name="PART_Track" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}"
                               Value="{Binding Value, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
                            <Track.Thumb><Thumb Width="12" Height="12" /></Track.Thumb>
                        </Track>
                    </Grid>
                </ControlTemplate>
                """);
            control.Measure(new Size(300, double.PositiveInfinity));
            control.Arrange(new Rect(0, 0, 300, control.DesiredSize.Height)); slider.ApplyTemplate(); control.UpdateLayout();
            var track = (System.Windows.Controls.Primitives.Track)slider.Template.FindName("PART_Track", slider);
            Check(track.Thumb != null && track.ActualWidth > 12, "Offscreen precision thumb did not lay out");
            double pixelDelta = .2 / track.ValueFromDistance(1, 0);
            Check(double.IsFinite(pixelDelta) && pixelDelta > 0, "Track did not provide a usable drag scale");
            SelectStep(control, 5); int notifications = 0; control.Changed += _ => notifications++;
            track.Thumb!.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            for (int i = 0; i < 30; i++)
            {
                var delta = new DragDeltaEventArgs(pixelDelta, 0) { RoutedEvent = Thumb.DragDeltaEvent };
                track.Thumb.RaiseEvent(delta);
                Check(delta.Handled, "Precision slider did not consume the routed thumb drag");
            }
            Near(control.Value, 5); Check(notifications == 1, "Routed small drags stuck at the rounded thumb position");
            track.Thumb.RaiseEvent(new DragCompletedEventArgs(pixelDelta * 30, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            track.Thumb.RaiseEvent(new DragDeltaEventArgs(pixelDelta * 100, 0) { RoutedEvent = Thumb.DragDeltaEvent });
            Near(control.Value, 5); Check(notifications == 1, "A completed routed drag continued to change the parameter");
            Check(!control.IsLoaded, "Offscreen test raised Loaded or created a presentation window");
        });
        test("parameter minimum unit keeps integer filters integral while excluding fractional movement choices", () =>
        {
            var control = new ParameterSlider("흐림 반경", 1, 64, 2.2, 3.4, showStepControls: true, minimumStep: 1);
            Near(control.Value, 2);
            Check(Steps(control).Items.OfType<ComboBoxItem>().Select(item => (double)item.Tag).SequenceEqual(new[] { 0d, 1, 5 }), "Integer parameter offered fractional movement steps");
            int notifications = 0; control.Changed += _ => notifications++;
            SelectStep(control, 5); Near(control.Value, 2);
            Input(control).Text = "3.8"; Check(control.TryCommit(), "Integer filter rejected a numeric value"); Near(control.Value, 4);
            Check(notifications == 1, "Integer commit notified more than once");
            control.SetValue(6.2); Near(control.Value, 6); Check(notifications == 1, "Integer model sync notified");
            Track(control).Value = 7.2; Near(control.Value, 5);
            SelectStep(control, 0); control.AdjustByKey(Key.Right, true); Near(control.Value, 6);
            var reset = Descendants(control).OfType<Button>().Single(button => Equals(button.ToolTip, "흐림 반경 초기화"));
            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Near(control.Value, 3);
        });
    }
}
