using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using Bitiq.Core.Models;

namespace Bitiq.App.Windows;

public partial class SubtitleOverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private bool _isPositionEditMode;

    public event Action<OverlayWindowBounds>? BoundsCommitted;
    public event Action? PositionEditExited;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    public SubtitleOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        LocationChanged += (_, _) => NotifyBoundsChangedIfEditing();
        SizeChanged += (_, _) => NotifyBoundsChangedIfEditing();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // UYARI: SetWindowDisplayAffinity KESİNLİKLE KULLANILMAZ (yakalama engellenmez).
        ApplyClickThroughStyle(!_isPositionEditMode);
    }

    public void ApplySavedBounds(OverlayWindowBounds bounds)
    {
        if (!bounds.IsCustomPositioned)
        {
            double workW = SystemParameters.WorkArea.Width;
            double workH = SystemParameters.WorkArea.Height;
            Left = SystemParameters.WorkArea.Left + (workW * 0.08);
            Width = workW * 0.84;
            Height = Math.Max(220, workH * 0.28);
            Top = SystemParameters.WorkArea.Top + workH - Height - 24;
            return;
        }

        Left = bounds.Left;
        Top = bounds.Top;
        Width = Math.Max(260, bounds.Width);
        Height = Math.Max(100, bounds.Height);
    }

    public void UpdateStyle(StyleProfile style)
    {
        SubtitlePresenter.SetStyle(style);
    }

    public void UpdateSubtitle(SubtitleUpdateEvent subtitle)
    {
        SubtitlePresenter.SetSubtitle(subtitle);
    }

    public void SetPositionEditMode(bool enabled)
    {
        _isPositionEditMode = enabled;
        EditBorder.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        ApplyClickThroughStyle(!enabled);

        if (enabled && !IsVisible)
            Show();

        if (!enabled)
            CommitBounds();
    }

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

    private void ApplyClickThroughStyle(bool clickThrough)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW;
        if (clickThrough)
            exStyle |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
        else
            exStyle &= ~(WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);

        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
    }

    private void HeaderDrag_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isPositionEditMode && e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_isPositionEditMode)
            return;

        Width = Math.Max(260, Width + e.HorizontalChange);
        Height = Math.Max(100, Height + e.VerticalChange);
    }

    private void DonePositioning_Click(object sender, RoutedEventArgs e)
    {
        SetPositionEditMode(false);
        PositionEditExited?.Invoke();
    }

    private void NotifyBoundsChangedIfEditing()
    {
        if (_isPositionEditMode)
            CommitBounds();
    }

    private void CommitBounds()
    {
        BoundsCommitted?.Invoke(new OverlayWindowBounds
        {
            Left = Left,
            Top = Top,
            Width = Width,
            Height = Height,
            IsCustomPositioned = true
        });
    }
}
