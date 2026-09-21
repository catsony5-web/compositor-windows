using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Compositor.Windows;

public enum DocumentCloseChoice { Cancel, Discard, Save }

public sealed class SaveChangesDialog : Window
{
    public DocumentCloseChoice Choice { get; private set; } = DocumentCloseChoice.Cancel;

    public SaveChangesDialog(Window? owner, string documentName)
    {
        ArgumentNullException.ThrowIfNull(documentName);
        if (owner != null) Owner = owner;
        Title = "Morupixel · 문서 닫기";
        Icon = Theme.BrandIcon;
        Width = 560;
        MinWidth = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Theme.Header;
        Foreground = Theme.Text;
        FontFamily = Theme.UiFont;
        FontSize = Theme.BodySize;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        SourceInitialized += (_, _) => WindowAppearance.Apply(this);

        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        KeyboardNavigation.SetTabNavigation(root, KeyboardNavigationMode.Cycle);

        var heading = Theme.Label("변경 내용을 저장할까요?", 20);
        heading.FontWeight = FontWeights.SemiBold;
        heading.Margin = new Thickness(0);
        root.Children.Add(heading);

        var documentCard = new Border
        {
            Background = Theme.Panel,
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 18, 0, 0)
        };
        var documentRow = new Grid();
        documentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        documentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var documentIcon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 5,2 L 17,2 L 25,10 L 25,30 L 5,30 Z M 17,2 L 17,10 L 25,10 M 10,17 L 20,17 M 10,22 L 20,22"),
            Stroke = Theme.Accent,
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 30,
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Focusable = false
        };
        documentRow.Children.Add(documentIcon);
        var name = Theme.Label(documentName, 14);
        name.Name = "DocumentName";
        name.FontWeight = FontWeights.SemiBold;
        name.Margin = new Thickness(2, 3, 4, 3);
        name.VerticalAlignment = VerticalAlignment.Top;
        name.TextWrapping = TextWrapping.Wrap;
        name.TextTrimming = TextTrimming.None;
        AutomationProperties.SetName(name, documentName);
        var nameScroll = new ScrollViewer
        {
            Content = name,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 120,
            CanContentScroll = false,
            Focusable = true
        };
        AutomationProperties.SetName(nameScroll, "문서 이름");
        Grid.SetColumn(nameScroll, 1);
        documentRow.Children.Add(nameScroll);
        documentCard.Child = documentRow;
        Grid.SetRow(documentCard, 1);
        root.Children.Add(documentCard);

        var warning = Theme.Label("저장하지 않은 변경 내용은 사라집니다.", Theme.BodySize, Theme.Muted);
        warning.Margin = new Thickness(0, 14, 0, 0);
        Grid.SetRow(warning, 2);
        root.Children.Add(warning);

        var actions = new Grid
        {
            Margin = new Thickness(0, 22, 0, 0)
        };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var cancel = ActionButton("취소", DocumentCloseChoice.Cancel, 80);
        cancel.IsCancel = true;
        var discard = ActionButton("저장하지 않고 닫기", DocumentCloseChoice.Discard, 152);
        discard.Margin = new Thickness(8, 0, 0, 0);
        var save = ActionButton("저장 후 닫기", DocumentCloseChoice.Save, 128);
        save.Margin = new Thickness(8, 0, 0, 0);
        save.Background = Theme.Primary;
        save.BorderBrush = Theme.Primary;
        save.IsDefault = true;
        Grid.SetColumn(discard, 2); Grid.SetColumn(save, 3);
        actions.Children.Add(cancel);
        actions.Children.Add(discard);
        actions.Children.Add(save);
        Grid.SetRow(actions, 3);
        root.Children.Add(actions);
        Content = root;
        FocusManager.SetFocusedElement(this, save);
    }

    Button ActionButton(string label, DocumentCloseChoice choice, double width)
    {
        var button = Theme.Button(label, () =>
        {
            Choice = choice;
            Close();
        });
        button.Width = width;
        button.MinHeight = 38;
        button.Margin = new Thickness(0);
        button.Padding = new Thickness(12, 8, 12, 8);
        button.Focusable = true;
        return button;
    }
}
