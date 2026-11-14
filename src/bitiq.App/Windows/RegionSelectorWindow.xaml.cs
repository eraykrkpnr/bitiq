using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bitiq.Core.Capture;
using Bitiq.Core.Models;

namespace Bitiq.App.Windows;

public partial class RegionSelectorWindow : Window
{
    private readonly CaptureWindowInfo? _targetWindow;
    private readonly CapturedFrameData? _windowSnapshot;
    private Rect _previewRectDip = Rect.Empty;
    private Point _startPointDip;
    private bool _isDragging;

    public CaptureRegion? SelectedRegion { get; private set; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    public RegionSelectorWindow(CaptureWindowInfo? targetWindow = null, CapturedFrameData? windowSnapshot = null)
    {
        _targetWindow = targetWindow;
        _windowSnapshot = windowSnapshot;

        InitializeComponent();
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        if (_targetWindow is not null)
        {
            InstructionText.Text = $"🎯 [{_targetWindow.ProcessName}] {_targetWindow.Title} — Pencere içindeki İngilizce altyazı alanını fare ile seçin • İptal için ESC";
        }

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                SelectedRegion = null;
                DialogResult = false;
                Close();
            }
        };
        Loaded += (_, _) =>
        {
            InitializeWindowPreview();
            Focus();
        };
    }

    private void InitializeWindowPreview()
    {
        if (_targetWindow is null)
            return;

        int refW = _windowSnapshot is { Width: > 16 } ? _windowSnapshot.Width : Math.Max(16, _targetWindow.Width);
        int refH = _windowSnapshot is { Height: > 16 } ? _windowSnapshot.Height : Math.Max(16, _targetWindow.Height);

        Point topLeftDip = PointFromScreen(new Point(_targetWindow.Left, _targetWindow.Top));
        Point bottomRightDip = PointFromScreen(new Point(_targetWindow.Left + refW, _targetWindow.Top + refH));

        double dipW = Math.Max(40, bottomRightDip.X - topLeftDip.X);
        double dipH = Math.Max(40, bottomRightDip.Y - topLeftDip.Y);
        double dipX = topLeftDip.X;
        double dipY = topLeftDip.Y;

        if (_windowSnapshot is { BgraPixels.Length: > 0 })
        {
            var bmp = BitmapSource.Create(
                _windowSnapshot.Width,
                _windowSnapshot.Height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                _windowSnapshot.BgraPixels,
                _windowSnapshot.Width * 4);
            bmp.Freeze();
            WindowPreviewImage.Source = bmp;

            // Pencere ekranın dışındaysa veya belirgin şekilde taşmışsa mevcut monitör çalışma alanı içinde tam görünür hale getir
            if (dipX < -16 || dipY < -16 || dipX + dipW > ActualWidth + 16 || dipY + dipH > ActualHeight + 16)
            {
                double maxW = Math.Max(320, SystemParameters.PrimaryScreenWidth - 96);
                double maxH = Math.Max(200, SystemParameters.PrimaryScreenHeight - 120);
                double fitScale = Math.Min(1.0, Math.Min(maxW / dipW, maxH / dipH));
                double fittedW = dipW * fitScale;
                double fittedH = dipH * fitScale;
                Point primaryCenterDip = PointFromScreen(new Point(0, 0));
                dipX = Math.Max(24, primaryCenterDip.X + ((SystemParameters.PrimaryScreenWidth - fittedW) / 2.0));
                dipY = Math.Max(64, primaryCenterDip.Y + ((SystemParameters.PrimaryScreenHeight - fittedH) / 2.0));
                dipW = fittedW;
                dipH = fittedH;
            }
        }

        _previewRectDip = new Rect(dipX, dipY, dipW, dipH);
        Canvas.SetLeft(WindowPreviewContainer, dipX);
        Canvas.SetTop(WindowPreviewContainer, dipY);
        WindowPreviewContainer.Width = dipW;
        WindowPreviewContainer.Height = dipH;
        WindowPreviewContainer.Visibility = Visibility.Visible;
    }

    private Point ClampToPreviewIfLocked(Point pt)
    {
        if (_targetWindow is null || _previewRectDip.IsEmpty)
            return pt;

        double cx = Math.Clamp(pt.X, _previewRectDip.Left, _previewRectDip.Right);
        double cy = Math.Clamp(pt.Y, _previewRectDip.Top, _previewRectDip.Bottom);
        return new Point(cx, cy);
    }

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _startPointDip = ClampToPreviewIfLocked(e.GetPosition(OverlayCanvas));
        _isDragging = true;
        OverlayCanvas.CaptureMouse();

        SelectionBox.Visibility = Visibility.Visible;
        SizeBadge.Visibility = Visibility.Visible;
        UpdateSelectionVisual(_startPointDip, _startPointDip);
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
            return;

        UpdateSelectionVisual(_startPointDip, ClampToPreviewIfLocked(e.GetPosition(OverlayCanvas)));
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
            return;

        _isDragging = false;
        OverlayCanvas.ReleaseMouseCapture();

        Point endPointDip = ClampToPreviewIfLocked(e.GetPosition(OverlayCanvas));
        double x = Math.Min(_startPointDip.X, endPointDip.X);
        double y = Math.Min(_startPointDip.Y, endPointDip.Y);
        double w = Math.Abs(endPointDip.X - _startPointDip.X);
        double h = Math.Abs(endPointDip.Y - _startPointDip.Y);

        if (w < 20 || h < 12)
        {
            SelectedRegion = null;
            DialogResult = false;
            Close();
            return;
        }

        if (_targetWindow is not null && !_previewRectDip.IsEmpty && _previewRectDip.Width > 0 && _previewRectDip.Height > 0)
        {
            int refW = _windowSnapshot is { Width: > 16 } ? _windowSnapshot.Width : Math.Max(16, _targetWindow.Width);
            int refH = _windowSnapshot is { Height: > 16 } ? _windowSnapshot.Height : Math.Max(16, _targetWindow.Height);

            double normX = Math.Clamp((x - _previewRectDip.Left) / _previewRectDip.Width, 0.0, 1.0);
            double normY = Math.Clamp((y - _previewRectDip.Top) / _previewRectDip.Height, 0.0, 1.0);
            double normW = Math.Clamp(w / _previewRectDip.Width, 0.005, 1.0 - normX);
            double normH = Math.Clamp(h / _previewRectDip.Height, 0.005, 1.0 - normY);

            int relX = Math.Clamp((int)Math.Round(normX * refW), 0, Math.Max(0, refW - 16));
            int relY = Math.Clamp((int)Math.Round(normY * refH), 0, Math.Max(0, refH - 16));
            int physW = Math.Clamp((int)Math.Round(normW * refW), 16, Math.Max(16, refW - relX));
            int physH = Math.Clamp((int)Math.Round(normH * refH), 16, Math.Max(16, refH - relY));

            int absX = _targetWindow.Left + relX;
            int absY = _targetWindow.Top + relY;
            var monInfo = GetMonitorInfoForRect(absX, absY, physW, physH);

            SelectedRegion = new CaptureRegion
            {
                X = absX,
                Y = absY,
                Width = physW,
                Height = physH,
                MonitorDeviceName = monInfo.szDevice ?? string.Empty,
                MonitorLeft = monInfo.rcMonitor.Left,
                MonitorTop = monInfo.rcMonitor.Top,
                MonitorWidth = monInfo.rcMonitor.Right - monInfo.rcMonitor.Left,
                MonitorHeight = monInfo.rcMonitor.Bottom - monInfo.rcMonitor.Top,
                IsWindowLocked = true,
                TargetWindowHandle = _targetWindow.Hwnd.ToInt64(),
                TargetWindowTitle = _targetWindow.Title,
                TargetWindowProcessName = _targetWindow.ProcessName,
                WindowRelativeX = relX,
                WindowRelativeY = relY,
                ReferenceWindowWidth = refW,
                ReferenceWindowHeight = refH,
                NormalizedX = normX,
                NormalizedY = normY,
                NormalizedWidth = normW,
                NormalizedHeight = normH
            };

            DialogResult = true;
            Close();
            return;
        }

        // Fiziksel ekran piksellerine dönüştür (Windows.Graphics.Capture tam fiziksel piksel kullanır)
        Point topLeftScreen = PointToScreen(new Point(x, y));
        Point bottomRightScreen = PointToScreen(new Point(x + w, y + h));

        int screenPhysX = (int)Math.Round(topLeftScreen.X);
        int screenPhysY = (int)Math.Round(topLeftScreen.Y);
        int screenPhysW = Math.Max(16, (int)Math.Round(bottomRightScreen.X - topLeftScreen.X));
        int screenPhysH = Math.Max(16, (int)Math.Round(bottomRightScreen.Y - topLeftScreen.Y));

        var mi = GetMonitorInfoForRect(screenPhysX, screenPhysY, screenPhysW, screenPhysH);

        SelectedRegion = new CaptureRegion
        {
            X = screenPhysX,
            Y = screenPhysY,
            Width = screenPhysW,
            Height = screenPhysH,
            MonitorDeviceName = mi.szDevice ?? string.Empty,
            MonitorLeft = mi.rcMonitor.Left,
            MonitorTop = mi.rcMonitor.Top,
            MonitorWidth = mi.rcMonitor.Right - mi.rcMonitor.Left,
            MonitorHeight = mi.rcMonitor.Bottom - mi.rcMonitor.Top
        };

        DialogResult = true;
        Close();
    }

    private static MONITORINFOEX GetMonitorInfoForRect(int physX, int physY, int physW, int physH)
    {
        int centerX = physX + (physW / 2);
        int centerY = physY + (physH / 2);
        IntPtr hMon = MonitorFromPoint(new POINT { X = centerX, Y = centerY }, 2);

        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(hMon, ref mi);
        return mi;
    }

    private void UpdateSelectionVisual(Point a, Point b)
    {
        double x = Math.Min(a.X, b.X);
        double y = Math.Min(a.Y, b.Y);
        double w = Math.Abs(b.X - a.X);
        double h = Math.Abs(b.Y - a.Y);

        Canvas.SetLeft(SelectionBox, x);
        Canvas.SetTop(SelectionBox, y);
        SelectionBox.Width = w;
        SelectionBox.Height = h;

        int pw, ph;
        if (_targetWindow is not null && !_previewRectDip.IsEmpty && _previewRectDip.Width > 0 && _previewRectDip.Height > 0)
        {
            int refW = _windowSnapshot is { Width: > 16 } ? _windowSnapshot.Width : Math.Max(16, _targetWindow.Width);
            int refH = _windowSnapshot is { Height: > 16 } ? _windowSnapshot.Height : Math.Max(16, _targetWindow.Height);
            pw = Math.Max(0, (int)Math.Round((w / _previewRectDip.Width) * refW));
            ph = Math.Max(0, (int)Math.Round((h / _previewRectDip.Height) * refH));
        }
        else
        {
            var topLeftPhys = PointToScreen(new Point(x, y));
            var botRightPhys = PointToScreen(new Point(x + w, y + h));
            pw = Math.Max(0, (int)Math.Round(botRightPhys.X - topLeftPhys.X));
            ph = Math.Max(0, (int)Math.Round(botRightPhys.Y - topLeftPhys.Y));
        }

        SizeBadgeText.Text = $"{pw} × {ph} px";

        Canvas.SetLeft(SizeBadge, x);
        Canvas.SetTop(SizeBadge, Math.Max(8, y - 26));
    }
}
