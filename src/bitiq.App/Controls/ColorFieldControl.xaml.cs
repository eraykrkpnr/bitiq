using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Bitiq.Core.Styling;

namespace Bitiq.App.Controls;

public partial class ColorFieldControl : UserControl
{
    private static readonly string[] SwatchHexes =
    [
        "#FFFFFF", "#FFF6D1", "#FACC15", "#38BDF8", "#4ADE80", "#F87171",
        "#E2E8F0", "#FDE047", "#FB923C", "#818CF8", "#2DD4BF", "#F472B6",
        "#000000", "#0B0E14", "#1E293B", "#0F172A", "#00FF00", "#334155"
    ];

    public static readonly DependencyProperty ColorHexProperty = DependencyProperty.Register(
        nameof(ColorHex), typeof(string), typeof(ColorFieldControl),
        new FrameworkPropertyMetadata("#FFFFFF", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnColorHexChanged));

    public event Action<string>? ColorChanged;

    public string ColorHex
    {
        get => (string)GetValue(ColorHexProperty);
        set => SetValue(ColorHexProperty, value);
    }

    public ColorFieldControl()
    {
        InitializeComponent();
        BuildPalette();
        UpdateVisual("#FFFFFF");
    }

    private void BuildPalette()
    {
        foreach (string hex in SwatchHexes)
        {
            var (r, g, b) = StyleCssMapper.ParseHexRgb(hex);
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();

            var btn = new Button
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(2),
                Background = brush,
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Tag = hex,
                ToolTip = hex
            };
            btn.Click += (_, _) =>
            {
                PalettePopup.IsOpen = false;
                SetAndNotify(hex);
            };
            PaletteGrid.Children.Add(btn);
        }
    }

    private static void OnColorHexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ColorFieldControl ctrl)
        {
            ctrl.UpdateVisual(e.NewValue as string ?? "#FFFFFF");
        }
    }

    private void UpdateVisual(string hex)
    {
        if (SwatchBtn is null || HexBox is null)
            return;

        var (r, g, b) = StyleCssMapper.ParseHexRgb(hex);
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        SwatchBtn.Background = brush;
        HexBox.Text = hex.StartsWith('#') ? hex.ToUpperInvariant() : "#" + hex.ToUpperInvariant();
    }

    private void SwatchBtn_Click(object sender, RoutedEventArgs e)
    {
        PalettePopup.IsOpen = !PalettePopup.IsOpen;
    }

    private void HexBox_LostFocus(object sender, RoutedEventArgs e) => CommitText();

    private void HexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            CommitText();
    }

    private void CommitText()
    {
        string txt = HexBox.Text.Trim();
        if (!txt.StartsWith('#'))
            txt = "#" + txt;
        var (r, g, b) = StyleCssMapper.ParseHexRgb(txt);
        string canonical = $"#{r:X2}{g:X2}{b:X2}";
        SetAndNotify(canonical);
    }

    private void SetAndNotify(string hex)
    {
        ColorHex = hex;
        UpdateVisual(hex);
        ColorChanged?.Invoke(hex);
    }
}
