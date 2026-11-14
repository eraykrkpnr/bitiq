using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Bitiq.Core.Models;
using Bitiq.Core.Styling;

namespace Bitiq.App.Windows;

public partial class ObsChromaWindow : Window
{
    public ObsChromaWindow()
    {
        InitializeComponent();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    public void SetChromaColor(string? hex)
    {
        var (r, g, b) = StyleCssMapper.ParseHexRgb(string.IsNullOrWhiteSpace(hex) ? "#00FF00" : hex);
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        Background = brush;
        ChromaRoot.Background = brush;
    }

    public void UpdateStyle(StyleProfile style) => SubtitlePresenter.SetStyle(style);
    public void UpdateSubtitle(SubtitleUpdateEvent subtitle) => SubtitlePresenter.SetSubtitle(subtitle);

    public bool GetPhysicalBounds(out int x, out int y, out int w, out int h)
    {
        x = y = w = h = 0;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !IsVisible)
            return false;

        if (GetWindowRect(hwnd, out var rect))
        {
            x = rect.Left;
            y = rect.Top;
            w = Math.Max(0, rect.Right - rect.Left);
            h = Math.Max(0, rect.Bottom - rect.Top);
            return w > 0 && h > 0;
        }
        return false;
    }
}
