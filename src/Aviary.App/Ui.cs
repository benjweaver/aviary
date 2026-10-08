using Aviary.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Aviary.App;

internal static class Ui
{
    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    public static TextBlock Text(string text, double size = 14, bool muted = false) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = Brush(muted ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush") };
    public static TextBlock Heading(string text, double size = 28) { var result = Text(text, size); result.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; return result; }
    public static FontIcon Icon(string glyph, double size = 20) => new() { Glyph = glyph, FontSize = size };
    public static StackPanel Stack(params UIElement[] children) { var stack = new StackPanel { Spacing = 16 }; foreach (var child in children) stack.Children.Add(child); return stack; }
    public static Border Card(UIElement child, double padding = 24) => new() { Child = child, Padding = new Thickness(padding), CornerRadius = new CornerRadius(12), Background = Brush("CardBackgroundFillColorDefaultBrush"), BorderBrush = Brush("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1) };
    public static Button Action(string label, string glyph, Func<Task> action, bool primary = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; content.Children.Add(Icon(glyph, 16)); content.Children.Add(new TextBlock { Text = label });
        var button = new Button { Content = content, Padding = new Thickness(16, 10, 16, 10) }; if (primary) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        AutomationProperties.SetName(button, label);
        button.Click += async (_, _) => { button.IsEnabled = false; try { await action(); } finally { button.IsEnabled = true; } }; return button;
    }
    public static Color OsColor(string os) => os == "Windows" ? Color.FromArgb(255, 37, 99, 235) : os == "Linux" ? Color.FromArgb(255, 115, 79, 209) : Color.FromArgb(255, 21, 128, 115);
    public static Border OsIcon(string os, double size = 56)
    {
        UIElement symbol;
        if (os == "Windows")
        {
            var panes = new Grid { Width = size / 2, Height = size / 2, RowSpacing = 2, ColumnSpacing = 2 };
            for (int i = 0; i < 2; i++) { panes.RowDefinitions.Add(new()); panes.ColumnDefinitions.Add(new()); }
            for (int i = 0; i < 4; i++) { var pane = new Microsoft.UI.Xaml.Shapes.Rectangle { Fill = new SolidColorBrush(Colors.White) }; Grid.SetRow(pane, i / 2); Grid.SetColumn(pane, i % 2); panes.Children.Add(pane); }
            symbol = panes;
        }
        else if (os == "Linux") symbol = new TextBlock { Text = ">_", FontFamily = new FontFamily("Cascadia Code"), FontSize = size * .38, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        else symbol = new FontIcon { Glyph = "\uE7F4", FontSize = size / 2, Foreground = new SolidColorBrush(Colors.White) };
        return new() { Width = size, Height = size, CornerRadius = new CornerRadius(size / 4), Background = new SolidColorBrush(OsColor(os)), Child = symbol };
    }
    public static Border Status(VmStatus state)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 }; var color = state == VmStatus.Running ? Colors.MediumSeaGreen : state == VmStatus.Error ? Colors.OrangeRed : state == VmStatus.Paused ? Colors.Goldenrod : Colors.Gray;
        panel.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center }); panel.Children.Add(Text(state.ToString(), 12));
        return new Border { Child = panel, Padding = new Thickness(10, 5, 10, 5), CornerRadius = new CornerRadius(20), Background = Brush("SubtleFillColorSecondaryBrush"), HorizontalAlignment = HorizontalAlignment.Left };
    }
    public static string Memory(int mb) => mb % 1024 == 0 ? $"{mb / 1024} GB" : $"{mb / 1024d:0.#} GB";
}
