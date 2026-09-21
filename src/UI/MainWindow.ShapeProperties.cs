using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Compositor.Windows;

public sealed partial class MainWindow
{
    void AddShapeProperties(Layer layer)
    {
        if (layer.Shape is not { } shape) return;
        var content = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        content.Children.Add(Theme.Section("도형 크기 · px"));
        content.Children.Add(TransformRow(layer,
            ("너비", shape.Width, 1, 8192, "도형 너비", (l, v) => VectorShapes.Update(l, l.Shape! with { Width = (int)Math.Round(v) })),
            ("높이", shape.Height, 1, 8192, "도형 높이", (l, v) => VectorShapes.Update(l, l.Shape! with { Height = (int)Math.Round(v) }))));
        var boundDocument = doc; long version = inspectorVersion;
        bool Current() => ReferenceEquals(doc, boundDocument) && inspectorVersion == version && doc.ActiveId == layer.Id && !IsLockedWithParents(layer);
        void ColorRow(string name, bool enabled, uint argb, bool fill)
        {
            var row = new Grid { Margin = new Thickness(2, 5, 2, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(148) });
            var toggle = new CheckBox { Content = name, IsChecked = enabled, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center, IsEnabled = !IsLockedWithParents(layer) };
            toggle.Click += (_, _) => { if (Current()) EditLayer(name, l => VectorShapes.Update(l, fill ? l.Shape! with { FillEnabled = toggle.IsChecked == true } : l.Shape! with { StrokeEnabled = toggle.IsChecked == true })); };
            row.Children.Add(toggle);
            var chip = Theme.Button("", () =>
            {
                if (!Current()) return;
                var dialog = new ColorPickerDialog(this, VectorShapes.Color(argb), name);
                if (dialog.ShowDialog() == true && Current()) EditLayer(name + " 색상", l => VectorShapes.Update(l, fill ? l.Shape! with { FillArgb = VectorShapes.Argb(dialog.SelectedColor) } : l.Shape! with { StrokeArgb = VectorShapes.Argb(dialog.SelectedColor) }));
            }, name + " 색상 변경");
            var chipContent = new StackPanel { Orientation = Orientation.Horizontal };
            chipContent.Children.Add(new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(VectorShapes.Color(argb)), BorderBrush = Theme.Muted, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0) });
            chipContent.Children.Add(Theme.Label($"#{argb & 0xFFFFFF:X6}", 11)); chip.Content = chipContent; chip.MinHeight = 34; chip.IsEnabled = !IsLockedWithParents(layer);
            Grid.SetColumn(chip, 1); row.Children.Add(chip); content.Children.Add(row);
        }
        ColorRow("채우기", shape.FillEnabled, shape.FillArgb, true); ColorRow("선", shape.StrokeEnabled, shape.StrokeArgb, false);
        content.Children.Add(TransformRow(layer,
            ("선 두께 · px", shape.StrokeWidth, 0, 512, "선 두께", (l, v) => VectorShapes.Update(l, l.Shape! with { StrokeWidth = v })),
            ("모서리 · px", shape.CornerRadius, 0, 4096, "둥근 모서리", (l, v) => VectorShapes.Update(l, l.Shape! with { CornerRadius = v }))));
        if (shape.Kind == ShapeKind.Ellipse) content.ToolTip = "타원에는 모서리 값이 적용되지 않습니다.";
        properties.Children.Add(content);
    }
}
