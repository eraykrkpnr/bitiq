using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Bitiq.Core.Models;

namespace Bitiq.App.Windows;

public partial class TestSubtitleWindow : Window
{
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

    private static readonly string[] Lines =
    [
        "Wait! Don't open that door, it's a trap!",
        "You have died. Press X to continue.",
        "The ancient relics are hidden beneath the temple of shadows.",
        "We need to reach the extraction point before dawn.",
        "Who sent you here? Speak, or face the consequences.",
        "Warning: Reactor core temperature is reaching critical levels.",
        "Hold the line! Reinforcements are only two minutes away.",
        "Take this key, it unlocks the gate to the northern tower."
    ];

    private readonly DispatcherTimer _autoTimer = new();
    private readonly DispatcherTimer _typewriterTimer = new();
    private int _index;
    private string _targetText = Lines[0];
    private int _charPos;

    public TestSubtitleWindow()
    {
        InitializeComponent();
        _autoTimer.Interval = TimeSpan.FromSeconds(3.5);
        _autoTimer.Tick += (_, _) => ShowNextLine();

        _typewriterTimer.Interval = TimeSpan.FromMilliseconds(28);
        _typewriterTimer.Tick += (_, _) => AdvanceTypewriter();

        Closed += (_, _) =>
        {
            _autoTimer.Stop();
            _typewriterTimer.Stop();
        };
    }

    public void ShowLineByIndex(int index)
    {
        _index = ((index % Lines.Length) + Lines.Length) % Lines.Length;
        DisplayLine(Lines[_index]);
    }

    public CaptureRegion GetSubtitleBoxPhysicalRegion()
    {
        var topLeft = EnglishSubtitleRegion.PointToScreen(new Point(0, 0));
        var botRight = EnglishSubtitleRegion.PointToScreen(
            new Point(EnglishSubtitleRegion.ActualWidth, EnglishSubtitleRegion.ActualHeight));

        int px = (int)Math.Round(topLeft.X);
        int py = (int)Math.Round(topLeft.Y);
        int pw = Math.Max(32, (int)Math.Round(botRight.X - topLeft.X));
        int ph = Math.Max(24, (int)Math.Round(botRight.Y - topLeft.Y));

        IntPtr hMon = MonitorFromPoint(new POINT { X = px + (pw / 2), Y = py + (ph / 2) }, 2);
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(hMon, ref mi);

        return new CaptureRegion
        {
            X = px,
            Y = py,
            Width = pw,
            Height = ph,
            MonitorDeviceName = mi.szDevice ?? string.Empty,
            MonitorLeft = mi.rcMonitor.Left,
            MonitorTop = mi.rcMonitor.Top,
            MonitorWidth = mi.rcMonitor.Right - mi.rcMonitor.Left,
            MonitorHeight = mi.rcMonitor.Bottom - mi.rcMonitor.Top
        };
    }

    private void ShowNextLine()
    {
        _index = (_index + 1) % Lines.Length;
        DisplayLine(Lines[_index]);
    }

    private void DisplayLine(string line)
    {
        _typewriterTimer.Stop();
        _targetText = line;
        if (TypewriterCheck.IsChecked == true && !string.IsNullOrEmpty(line))
        {
            _charPos = 1;
            EnglishSubtitleText.Text = line[..1];
            _typewriterTimer.Start();
        }
        else
        {
            EnglishSubtitleText.Text = line;
        }
    }

    private void AdvanceTypewriter()
    {
        if (_charPos >= _targetText.Length)
        {
            _typewriterTimer.Stop();
            return;
        }
        _charPos++;
        EnglishSubtitleText.Text = _targetText[.._charPos];
    }

    private void NextLine_Click(object sender, RoutedEventArgs e) => ShowNextLine();

    private void ClearLine_Click(object sender, RoutedEventArgs e)
    {
        _typewriterTimer.Stop();
        EnglishSubtitleText.Text = string.Empty;
    }

    private void ToggleAutoCycle_Click(object sender, RoutedEventArgs e)
    {
        if (_autoTimer.IsEnabled)
        {
            _autoTimer.Stop();
            AutoCycleBtn.Content = "Otomatik Döngü: Kapalı";
        }
        else
        {
            _autoTimer.Start();
            AutoCycleBtn.Content = "Otomatik Döngü: Açık";
        }
    }
}
