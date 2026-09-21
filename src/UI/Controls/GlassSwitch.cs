using System.Globalization;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Compositor.Windows;

/// <summary>Keyboard-accessible segmented control with two persistent labels.</summary>
public sealed class GlassSwitch : ToggleButton
{
    public string LeftLabel { get; }
    public string RightLabel { get; }
    bool? requestedSelection;
    public GlassSwitch(string left, string right, double width)
    {
        LeftLabel = left; RightLabel = right; Width = width; Height = 34;
        Cursor = Cursors.Hand; VerticalAlignment = VerticalAlignment.Center;
        Template = new System.Windows.Controls.ControlTemplate(typeof(GlassSwitch));
        Checked += (_, _) => InvalidateVisual(); Unchecked += (_, _) => InvalidateVisual();
        IsKeyboardFocusWithinChanged += (_, _) => InvalidateVisual();
        System.Windows.Automation.AutomationProperties.SetName(this, left + " / " + right);
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        // The clicked label is an explicit destination. Clicking the already
        // selected half must not unexpectedly switch to the other workspace.
        requestedSelection = e.GetPosition(this).X >= ActualWidth / 2;
        try { base.OnMouseLeftButtonUp(e); }
        finally { requestedSelection = null; }
    }
    protected override void OnToggle()
    {
        if (requestedSelection is { } selected) SetCurrentValue(IsCheckedProperty, selected);
        else base.OnToggle();
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right)
        {
            requestedSelection = e.Key == Key.Right;
            try { OnClick(); }
            finally { requestedSelection = null; }
            e.Handled = true;
        }
        else base.OnKeyDown(e);
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;
        dc.DrawRoundedRectangle(Theme.Header, new Pen(Theme.Line, 1), new Rect(.5, .5, w - 1, h - 1), 6, 6);
        double thumbWidth = (w - 6) / 2, x = IsChecked == true ? w / 2 : 3;
        dc.DrawRoundedRectangle(Theme.Selected, null, new Rect(x, 3, thumbWidth, h - 6), 4, 4);
        DrawLabel(LeftLabel, w / 4, IsChecked != true); DrawLabel(RightLabel, w * .75, IsChecked == true);
        if (IsKeyboardFocusWithin) dc.DrawRoundedRectangle(null, new Pen(Theme.Accent, 1.5), new Rect(1, 1, w - 2, h - 2), 6, 6);
        void DrawLabel(string text, double center, bool selected)
        {
            var label = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(Theme.UiFont, FontStyles.Normal, selected ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
                Theme.BodySize, selected ? Theme.Text : Theme.Muted, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(label, new Point(center - label.Width / 2, (h - label.Height) / 2));
        }
    }
}
