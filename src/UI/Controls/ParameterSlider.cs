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
    readonly double minimumStep;
    bool syncing, editingNumber, dragging;
    double value, selectedStep, dragValue;
    public double Value => value;
    public bool IsInputValid => TryReadNumber(out _);
    internal bool HasPendingInput => editingNumber;
    public event Action<double>? Changed;

    public ParameterSlider(string label, double min, double max, double value, double reset = 0,
        bool showStepControls = false, double minimumStep = 0)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max) || min >= max) throw new ArgumentOutOfRangeException(nameof(max));
        if (!double.IsFinite(minimumStep) || minimumStep < 0) throw new ArgumentOutOfRangeException(nameof(minimumStep));
        this.minimumStep = minimumStep;
        Margin = new Thickness(0, 6, 0, 12);
        var row = new DockPanel();
        number = new TextBox { Width = 78, Padding = new Thickness(8, 5, 8, 5), MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, Margin = new Thickness(0) };
        AutomationProperties.SetName(number, label);
        DockPanel.SetDock(number, Dock.Right); row.Children.Add(number);
        var restore = Theme.Button("↺", () => SetValue(Math.Clamp(reset, min, max), true), label + " 초기화");
        restore.Width = 24; restore.MinHeight = 22; restore.Padding = new Thickness(0); restore.Margin = new Thickness(0);
        restore.Background = Brushes.Transparent; restore.BorderBrush = Brushes.Transparent;
        DockPanel.SetDock(restore, Dock.Right); row.Children.Add(restore); row.Children.Add(Theme.Label(label, 12)); Children.Add(row);
        bool precisionControls = showStepControls || minimumStep > 0;
        slider = precisionControls ? new PrecisionSlider(this) : new Slider();
        slider.Minimum = min; slider.Maximum = max; slider.Margin = new Thickness(4, 8, 4, 0);
        slider.SmallChange = precisionControls ? max - min <= 40 ? .01 : .1 : (max - min) / 200;
        slider.LargeChange = (max - min) / 20; slider.IsMoveToPointEnabled = true;
        if (TryFindResource("SpectrumSlider") is Style style) slider.Style = style;
        var ramp = new LinearGradientBrush { StartPoint = new Point(0, .5), EndPoint = new Point(1, .5) };
        string[] rampColors = label == "색조" ? ["#EE6981", "#EBCB78", "#87C78D", "#77CEDC", "#8C96E7", "#D180D7", "#EE6981"] : label == "채도" ? ["#858992", "#D982B2"] : label == "명도" || label.Contains("노출") || label.Contains("검정") || label.Contains("흰색") ? ["#17191E", "#F2F4F8"] : ["#4B596B", "#BDDAF8"];
        for (int i = 0; i < rampColors.Length; i++) ramp.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(rampColors[i]), i / (double)(rampColors.Length - 1)));
        slider.Background = ramp;
        if (precisionControls) slider.ToolTip = "방향키: 미세 조절 · 연속 이동에서 Shift+드래그/방향키: 1/10 속도";
        AutomationProperties.SetName(slider, label);
        Children.Add(slider); SetValue(value);
        slider.ValueChanged += (_, _) => { if (!syncing) ApplyValue(SnapUserValue(slider.Value), true); };
        if (precisionControls) slider.PreviewKeyDown += (_, e) => { if (AdjustByKey(e.Key, e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift))) e.Handled = true; };
        number.TextChanged += (_, _) => { if (!syncing) editingNumber = true; };
        number.LostKeyboardFocus += (_, _) => TryCommit();
        number.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { TryCommit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { SetValue(Value); e.Handled = true; }
        };
        if (showStepControls)
        {
            var steps = new ComboBox { MinHeight = 28, Width = 94, Margin = new Thickness(6, 0, 0, 0) };
            AutomationProperties.SetName(steps, label + " 이동 간격");
            steps.Items.Add(new ComboBoxItem { Content = minimumStep > 0 ? "기본" : "연속", Tag = 0d });
            foreach (double step in new[] { .01, .1, 1d, 5d }.Where(step => step <= max - min && step >= minimumStep))
                steps.Items.Add(new ComboBoxItem { Content = step.ToString("0.##", CultureInfo.InvariantCulture), Tag = step });
            steps.SelectedIndex = 0;
            steps.SelectionChanged += (_, _) =>
            {
                if (steps.SelectedItem is ComboBoxItem { Tag: double step })
                {
                    selectedStep = step; dragValue = Value;
                    slider.SmallChange = Math.Max(minimumStep, step > 0 ? step : max - min <= 40 ? .01 : .1);
                }
            };
            var stepRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 5, 2, 0) };
            var caption = Theme.Label("이동 간격", Theme.CaptionSize, Theme.Muted); caption.VerticalAlignment = VerticalAlignment.Center;
            stepRow.Children.Add(caption); stepRow.Children.Add(steps); Children.Add(stepRow);
        }
    }

    bool TryReadNumber(out double parsed)
    {
        if (!editingNumber) { parsed = Value; return true; }
        return double.TryParse(number.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
            && double.IsFinite(parsed) && parsed >= slider.Minimum && parsed <= slider.Maximum;
    }

    public bool TryCommit()
    {
        if (!TryReadNumber(out double parsed))
        {
            number.BorderBrush = Theme.Brush("#EE9292");
            number.ToolTip = $"{slider.Minimum} ~ {slider.Maximum} 사이의 숫자를 입력하세요.";
            return false;
        }
        SetValue(parsed, true); return true;
    }

    public void SetValue(double value, bool notify = false)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        // Model synchronization, direct typing and reset deliberately bypass the user's movement snap.
        ApplyValue(minimumStep > 0 ? Snap(value, minimumStep) : Math.Clamp(value, slider.Minimum, slider.Maximum), notify);
    }

    void ApplyValue(double next, bool notify)
    {
        bool changed = next != value;
        syncing = true;
        try
        {
            slider.Value = next; value = slider.Value;
            number.Text = value.ToString("0.########", CultureInfo.InvariantCulture);
            number.ClearValue(Control.BorderBrushProperty); number.ClearValue(ToolTipProperty);
            editingNumber = false;
        }
        finally { syncing = false; }
        if (notify && changed) Changed?.Invoke(value);
    }

    double Snap(double candidate, double step)
    {
        candidate = Math.Clamp(candidate, slider.Minimum, slider.Maximum);
        // Both endpoints remain reachable, including a lower bound which is not a multiple of the step.
        if (candidate <= slider.Minimum || candidate >= slider.Maximum) return candidate;
        return Math.Clamp(Math.Round(candidate / step, MidpointRounding.AwayFromZero) * step, slider.Minimum, slider.Maximum);
    }

    double SnapUserValue(double candidate)
    {
        double step = Math.Max(selectedStep, minimumStep);
        return step > 0 ? Snap(candidate, step) : Math.Clamp(candidate, slider.Minimum, slider.Maximum);
    }

    internal bool AdjustByKey(Key key, bool shift = false)
    {
        if (!IsEnabled || !slider.IsEnabled) return false;
        int direction = key switch { Key.Left or Key.Down => -1, Key.Right or Key.Up => 1, Key.PageDown => -10, Key.PageUp => 10, _ => 0 };
        if (key is Key.Home or Key.End) { ApplyValue(key == Key.Home ? slider.Minimum : slider.Maximum, true); return true; }
        if (direction == 0) return false;
        double step = Math.Max(selectedStep, minimumStep);
        if (step > 0)
        {
            double anchor = direction > 0 ? Math.Floor(Value / step + 1e-10) : Math.Ceiling(Value / step - 1e-10);
            ApplyValue(Snap((anchor + direction) * step, step), true);
        }
        else
        {
            double fine = slider.Maximum - slider.Minimum <= 40 ? .01 : .1;
            ApplyValue(Math.Clamp(Value + direction * fine * (shift ? .1 : 1), slider.Minimum, slider.Maximum), true);
        }
        return true;
    }

    internal void BeginUserDrag() { dragging = true; dragValue = Value; }
    internal void ApplyUserDragDelta(double delta, bool shift = false)
    {
        if (!dragging || !double.IsFinite(delta)) return;
        // Accumulate unsnapped positions: repeated small movements must eventually cross a step boundary.
        dragValue = Math.Clamp(dragValue + delta * (selectedStep == 0 && minimumStep == 0 && shift ? .1 : 1), slider.Minimum, slider.Maximum);
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
