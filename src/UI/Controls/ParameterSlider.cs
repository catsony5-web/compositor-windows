using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed class ParameterSlider : StackPanel
{
    readonly Slider slider;
    readonly TextBox number;
    readonly double minimumStep, min, max;
    // A logarithmic track puts a wide positive range (10–1000%) evenly under the pointer: equal
    // distances are equal ratios, so the middle of 10–1000 is 100.
    readonly bool logarithmic;
    bool syncing, editingNumber, dragging;
    double value, selectedStep, dragValue, reset;
    public double Value => value;
    public bool IsInputValid => TryReadNumber(out _);
    internal bool HasPendingInput => editingNumber;
    public event Action<double>? Changed;

    public ParameterSlider(string label, double min, double max, double value, double reset = 0,
        bool showStepControls = false, double minimumStep = 0, bool logarithmic = false)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max) || min >= max) throw new ArgumentOutOfRangeException(nameof(max));
        if (!double.IsFinite(minimumStep) || minimumStep < 0) throw new ArgumentOutOfRangeException(nameof(minimumStep));
        if (logarithmic && (min <= 0 || showStepControls || minimumStep > 0)) throw new ArgumentOutOfRangeException(nameof(logarithmic));
        this.minimumStep = minimumStep; this.min = min; this.max = max; this.logarithmic = logarithmic; this.reset = reset;
        Margin = new Thickness(0, 4, 0, 8);
        var row = new DockPanel();
        number = new TextBox { Width = 64, Padding = new Thickness(6, 3, 6, 3), MinHeight = 26, VerticalContentAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, Margin = new Thickness(0) };
        AutomationProperties.SetName(number, label);
        DockPanel.SetDock(number, Dock.Right); row.Children.Add(number);
        ComboBox? steps = null;
        if (showStepControls)
        {
            // The movement unit sits beside the value; its purpose is carried by the tooltip and accessible name.
            // MinWidth keeps the numeric steps aligned; a longer translated "기본"/"연속" widens the box instead of being cut.
            steps = new ComboBox { MinHeight = 26, MinWidth = 68, Padding = new Thickness(7, 2, 4, 2), FontSize = Theme.CaptionSize, Margin = new Thickness(4, 0, 4, 0), ToolTip = "이동 간격 · 슬라이더와 방향키가 움직이는 단위" };
            AutomationProperties.SetName(steps, label + " 이동 간격");
            steps.Items.Add(new ComboBoxItem { Content = minimumStep > 0 ? "기본" : "연속", Tag = 0d });
            foreach (double step in new[] { .01, .1, 1d, 5d }.Where(step => step <= max - min && step >= minimumStep))
                steps.Items.Add(new ComboBoxItem { Content = step.ToString("0.##", CultureInfo.InvariantCulture), Tag = step });
            steps.SelectedIndex = 0;
            DockPanel.SetDock(steps, Dock.Right); row.Children.Add(steps);
        }
        var restore = Theme.IconButton(Theme.Glyphs.Revert, () => SetValue(Math.Clamp(this.reset, min, max), true), label + " 초기화", 22, 13);
        restore.Margin = new Thickness(0);
        DockPanel.SetDock(restore, Dock.Right); row.Children.Add(restore);
        var caption = Theme.Label(label, Theme.BodySize, Theme.Muted); caption.Margin = new Thickness(1, 2, 2, 2); row.Children.Add(caption); Children.Add(row);
        bool precisionControls = showStepControls || minimumStep > 0;
        slider = precisionControls ? new PrecisionSlider(this) : new Slider();
        slider.Minimum = Track(min); slider.Maximum = Track(max); slider.Margin = new Thickness(2, 4, 2, 0);
        slider.SmallChange = precisionControls ? max - min <= 40 ? .01 : .1 : (slider.Maximum - slider.Minimum) / 200;
        slider.LargeChange = (slider.Maximum - slider.Minimum) / 20; slider.IsMoveToPointEnabled = true;
        if (TryFindResource("SpectrumSlider") is Style style) slider.Style = style;
        var ramp = new LinearGradientBrush { StartPoint = new Point(0, .5), EndPoint = new Point(1, .5) };
        string[] rampColors = label == "색조" ? ["#EE6981", "#EBCB78", "#87C78D", "#77CEDC", "#8C96E7", "#D180D7", "#EE6981"] : label == "채도" ? ["#858992", "#D982B2"] : label == "명도" || label.Contains("노출") || label.Contains("검정") || label.Contains("흰색") ? ["#17191E", "#F2F4F8"] : ["#4B596B", "#BDDAF8"];
        for (int i = 0; i < rampColors.Length; i++) ramp.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(rampColors[i]), i / (double)(rampColors.Length - 1)));
        slider.Background = ramp;
        if (precisionControls) slider.ToolTip = "방향키: 미세 조절 · 연속 이동에서 Shift+드래그/방향키: 1/10 속도";
        AutomationProperties.SetName(slider, label);
        Children.Add(slider); SetValue(value);
        slider.ValueChanged += (_, _) => { if (!syncing) ApplyValue(SnapUserValue(Real(slider.Value)), true); };
        if (precisionControls) slider.PreviewKeyDown += (_, e) => { if (AdjustByKey(e.Key, e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift))) e.Handled = true; };
        number.TextChanged += (_, _) => { if (!syncing) editingNumber = true; };
        number.LostKeyboardFocus += (_, _) => TryCommit();
        number.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { TryCommit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { SetValue(Value); e.Handled = true; }
        };
        if (steps != null)
        {
            steps.SelectionChanged += (_, _) =>
            {
                if (steps.SelectedItem is ComboBoxItem { Tag: double step })
                {
                    selectedStep = step; dragValue = Value;
                    slider.SmallChange = Math.Max(minimumStep, step > 0 ? step : max - min <= 40 ? .01 : .1);
                }
            };
        }
    }

    double Track(double real) => logarithmic ? Math.Log(real) : real;
    double Real(double track) => logarithmic ? Math.Exp(track) : track;
    // Where the thumb sits along the track, 0–1.
    internal double TrackPosition => (slider.Value - slider.Minimum) / (slider.Maximum - slider.Minimum);

    bool TryReadNumber(out double parsed)
    {
        if (!editingNumber) { parsed = Value; return true; }
        return double.TryParse(number.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
            && double.IsFinite(parsed) && parsed >= min && parsed <= max;
    }

    public bool TryCommit()
    {
        if (!TryReadNumber(out double parsed))
        {
            number.BorderBrush = Theme.Danger;
            number.ToolTip = $"{min} ~ {max} 사이의 숫자를 입력하세요.";
            return false;
        }
        SetValue(parsed, true); return true;
    }

    // The value the reset button restores, for a default measured after the control was built.
    internal double ResetValue { get => reset; set => reset = double.IsFinite(value) ? value : reset; }

    public void SetValue(double value, bool notify = false)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        // Model synchronization, direct typing and reset deliberately bypass the user's movement snap.
        ApplyValue(minimumStep > 0 ? Snap(value, minimumStep) : Math.Clamp(value, min, max), notify);
    }

    void ApplyValue(double next, bool notify)
    {
        bool changed = next != value;
        syncing = true;
        try
        {
            slider.Value = Track(next); value = logarithmic ? Math.Clamp(next, min, max) : slider.Value;
            number.Text = value.ToString("0.########", CultureInfo.InvariantCulture);
            number.ClearValue(Control.BorderBrushProperty); number.ClearValue(ToolTipProperty);
            editingNumber = false;
        }
        finally { syncing = false; }
        if (notify && changed) Changed?.Invoke(value);
    }

    double Snap(double candidate, double step)
    {
        candidate = Math.Clamp(candidate, min, max);
        // Both endpoints remain reachable, including a lower bound which is not a multiple of the step.
        if (candidate <= min || candidate >= max) return candidate;
        return Math.Clamp(Math.Round(candidate / step, MidpointRounding.AwayFromZero) * step, min, max);
    }

    double SnapUserValue(double candidate)
    {
        double step = Math.Max(selectedStep, minimumStep);
        if (step > 0) return Snap(candidate, step);
        // A dragged logarithmic value keeps three significant digits (137, 12.3), not the raw exponential.
        if (logarithmic && candidate > 0) candidate = Math.Round(candidate, Math.Clamp(2 - (int)Math.Floor(Math.Log10(candidate)), 0, 6));
        return Math.Clamp(candidate, min, max);
    }

    internal bool AdjustByKey(Key key, bool shift = false)
    {
        if (!IsEnabled || !slider.IsEnabled) return false;
        int direction = key switch { Key.Left or Key.Down => -1, Key.Right or Key.Up => 1, Key.PageDown => -10, Key.PageUp => 10, _ => 0 };
        if (key is Key.Home or Key.End) { ApplyValue(key == Key.Home ? min : max, true); return true; }
        if (direction == 0) return false;
        double step = Math.Max(selectedStep, minimumStep);
        if (step > 0)
        {
            double anchor = direction > 0 ? Math.Floor(Value / step + 1e-10) : Math.Ceiling(Value / step - 1e-10);
            ApplyValue(Snap((anchor + direction) * step, step), true);
        }
        else
        {
            double fine = max - min <= 40 ? .01 : .1;
            ApplyValue(Math.Clamp(Value + direction * fine * (shift ? .1 : 1), min, max), true);
        }
        return true;
    }

    internal void BeginUserDrag() { dragging = true; dragValue = Value; }
    internal void ApplyUserDragDelta(double delta, bool shift = false)
    {
        if (!dragging || !double.IsFinite(delta)) return;
        // Accumulate unsnapped positions: repeated small movements must eventually cross a step boundary.
        dragValue = Math.Clamp(dragValue + delta * (selectedStep == 0 && minimumStep == 0 && shift ? .1 : 1), min, max);
        ApplyValue(SnapUserValue(dragValue), true);
    }
    internal void EndUserDrag() => dragging = false;

    sealed class PrecisionSlider(ParameterSlider owner) : Slider
    {
        protected override void OnThumbDragStarted(DragStartedEventArgs e)
        {
            base.OnThumbDragStarted(e); owner.BeginUserDrag();
        }
        protected override void OnThumbDragDelta(DragDeltaEventArgs e)
        {
            if (GetTemplateChild("PART_Track") is Track track)
            {
                owner.ApplyUserDragDelta(track.ValueFromDistance(e.HorizontalChange, e.VerticalChange), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                e.Handled = true;
            }
            else base.OnThumbDragDelta(e);
        }
        protected override void OnThumbDragCompleted(DragCompletedEventArgs e)
        {
            owner.EndUserDrag(); base.OnThumbDragCompleted(e);
        }
    }
}
