using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Compositor.Windows;

public sealed record ParameterField(string Label, double Minimum, double Maximum, double Value, double Reset = 0, double MinimumStep = 0);

/// <summary>Collects bounded filter values without rendering or modifying a document.</summary>
public sealed class ParameterDialog : Window
{
    readonly ParameterField[] fields;
    readonly List<ParameterSlider> controls = [];
    readonly Func<IReadOnlyList<double>, string?>? validate;
    readonly TextBlock error = Theme.Label("", Theme.CaptionSize, Theme.Brush("#F8ABAD"));
    double[] acceptedValues;

    // Callers receive only successfully validated values and cannot mutate the dialog's state.
    public double[] Values => (double[])acceptedValues.Clone();
    internal string ValidationMessage => error.Text;

    public ParameterDialog(Window? owner, string title, IReadOnlyList<ParameterField> fields, Func<IReadOnlyList<double>, string?>? validate = null)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Count is < 1 or > 32) throw new ArgumentException("조절할 항목을 1~32개 지정하세요.", nameof(fields));
        this.fields = fields.ToArray(); this.validate = validate;
        foreach (var field in this.fields)
        {
            if (field == null || string.IsNullOrWhiteSpace(field.Label) ||
                !double.IsFinite(field.Minimum) || !double.IsFinite(field.Maximum) || field.Maximum <= field.Minimum ||
                !double.IsFinite(field.Value) || field.Value < field.Minimum || field.Value > field.Maximum ||
                !double.IsFinite(field.Reset) || !double.IsFinite(field.MinimumStep) || field.MinimumStep < 0 || field.MinimumStep > field.Maximum - field.Minimum)
                throw new ArgumentException("조절 항목의 이름·범위·초깃값이 올바르지 않습니다.", nameof(fields));
        }
        acceptedValues = this.fields.Select(field => field.Value).ToArray();
        Owner = owner; Title = "Morupixel · " + title;
        Width = 480; MinWidth = 400; Height = Math.Clamp(200 + this.fields.Length * 128, 300, 760); MinHeight = 300;
        WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        Background = Theme.Header; Foreground = Theme.Text; FontFamily = Theme.UiFont;

        var root = new Grid { Margin = new Thickness(20) }; Content = root;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var label = Theme.Label(title, 20); label.FontWeight = FontWeights.SemiBold;
        label.ToolTip = "슬라이더를 움직이거나 숫자를 직접 입력하세요.\n방향키: 한 단계 · 연속 이동에서 Shift: 미세 조절";
        heading.Children.Add(label);
        root.Children.Add(heading);

        var body = new StackPanel { Margin = new Thickness(2, 2, 8, 2) };
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        foreach (var field in this.fields)
        {
            var parameter = new ParameterSlider(field.Label, field.Minimum, field.Maximum, field.Value, field.Reset, showStepControls: true, minimumStep: field.MinimumStep);
            parameter.Margin = new Thickness(0, 5, 0, 3);
            parameter.Changed += _ => SetError("");
            controls.Add(parameter); body.Children.Add(parameter);
            string range = field.Minimum.ToString("0.####", CultureInfo.InvariantCulture) + " ~ " + field.Maximum.ToString("0.####", CultureInfo.InvariantCulture);
            var bounds = Theme.Label("범위 " + range, Theme.CaptionSize, Theme.Muted); bounds.Margin = new Thickness(2, 0, 2, 14);
            System.Windows.Automation.AutomationProperties.SetName(bounds, field.Label + " 범위"); body.Children.Add(bounds);
        }

        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; Grid.SetRow(footer, 2); root.Children.Add(footer);
        error.TextWrapping = TextWrapping.Wrap; error.Visibility = Visibility.Collapsed;
        error.Margin = new Thickness(3, 0, 3, 8);
        System.Windows.Automation.AutomationProperties.SetName(error, "입력 확인"); footer.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        // Close yields false for a modal dialog and also works in offscreen cancellation tests.
        var cancel = Theme.Button("취소", Close); cancel.IsCancel = true; cancel.MinWidth = 76;
        var apply = Theme.Button("적용", () => { if (TryCommitFields()) DialogResult = true; }); apply.IsDefault = true; apply.MinWidth = 76; apply.Background = Theme.Primary;
        buttons.Children.Add(cancel); buttons.Children.Add(apply); footer.Children.Add(buttons);
    }

    internal bool TryCommitFields()
    {
        var invalid = new List<string>();
        // Check every number box. A pending invalid edit must not silently reuse its slider's old value.
        for (int i = 0; i < controls.Count; i++) if (!controls[i].TryCommit()) invalid.Add(fields[i].Label);
        if (invalid.Count > 0)
        {
            SetError(string.Join(", ", invalid) + ": 표시된 범위 안의 숫자를 입력하세요."); return false;
        }
        double[] candidate = controls.Select(control => control.Value).ToArray();
        string? message = validate?.Invoke(Array.AsReadOnly(candidate));
        if (!string.IsNullOrWhiteSpace(message)) { SetError(message); return false; }
        acceptedValues = candidate; SetError(""); return true;
    }

    void SetError(string message)
    {
        error.Text = message; error.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
