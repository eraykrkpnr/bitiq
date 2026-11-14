using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Bitiq.Core.Capture;

public sealed record CaptureWindowInfo
{
    public IntPtr Hwnd { get; init; }
    public string Title { get; init; } = string.Empty;
    public string ProcessName { get; init; } = string.Empty;
    public int Left { get; init; }
    public int Top { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool IsMinimized { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(ProcessName)
        ? $"{Title} ({Width}×{Height})"
        : $"[{ProcessName}] {Title} ({Width}×{Height})";

    public override string ToString() => DisplayName;
}

/// <summary>
/// Açık masaüstü pencerelerini listeler, pencere sınırlarını okur ve
/// simge durumuna küçültülmüş pencereleri arka planda yeniden çizilebilir hale getirir.
/// </summary>
public static class WindowEnumerationService
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_APPWINDOW = 0x00040000L;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;
    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_RESTORE = 9;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_BOTTOM = new(1);

    private static readonly HashSet<string> IgnoredTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Program Manager",
        "Windows Input Experience",
        "Microsoft Text Input Application"
    };

    public static IReadOnlyList<CaptureWindowInfo> GetCapturableWindows()
    {
        var results = new List<CaptureWindowInfo>();
        uint selfPid = (uint)Environment.ProcessId;
        var processNameCache = new Dictionary<uint, string>();

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
                return true;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == selfPid)
                return true;

            long exStyle = GetWindowLongPtrCompat(hwnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0 && (exStyle & WS_EX_APPWINDOW) == 0)
                return true;

            if (DwmGetWindowAttributeInt(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;

            string title = GetWindowTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title) || IgnoredTitles.Contains(title))
                return true;

            bool isMinimized = IsIconic(hwnd);
            if (!TryGetWindowBounds(hwnd, out int left, out int top, out int width, out int height))
                return true;

            if (width < 120 || height < 80)
                return true;

            if (!processNameCache.TryGetValue(pid, out string? procName))
            {
                procName = TryGetProcessName(pid);
                processNameCache[pid] = procName;
            }

            results.Add(new CaptureWindowInfo
            {
                Hwnd = hwnd,
                Title = title,
                ProcessName = procName,
                Left = left,
                Top = top,
                Width = width,
                Height = height,
                IsMinimized = isMinimized
            });

            return true;
        }, IntPtr.Zero);

        return results;
    }

    public static bool TryGetWindowBounds(IntPtr hwnd, out int left, out int top, out int width, out int height)
    {
        left = 0;
        top = 0;
        width = 0;
        height = 0;

        if (!IsWindowValid(hwnd))
            return false;

        if (IsIconic(hwnd))
        {
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (GetWindowPlacement(hwnd, ref wp))
            {
                left = wp.rcNormalPosition.Left;
                top = wp.rcNormalPosition.Top;
                width = Math.Max(0, wp.rcNormalPosition.Right - wp.rcNormalPosition.Left);
                height = Math.Max(0, wp.rcNormalPosition.Bottom - wp.rcNormalPosition.Top);
                if (width > 0 && height > 0)
                    return true;
            }
        }

        if (DwmGetWindowAttributeRect(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT dwmRect, Marshal.SizeOf<RECT>()) == 0)
        {
            left = dwmRect.Left;
            top = dwmRect.Top;
            width = Math.Max(0, dwmRect.Right - dwmRect.Left);
            height = Math.Max(0, dwmRect.Bottom - dwmRect.Top);
            if (width > 0 && height > 0)
                return true;
        }

        if (GetWindowRect(hwnd, out RECT rect))
        {
            left = rect.Left;
            top = rect.Top;
            width = Math.Max(0, rect.Right - rect.Left);
            height = Math.Max(0, rect.Bottom - rect.Top);
            return width > 0 && height > 0;
        }

        return false;
    }

    public static bool IsWindowValid(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

    public static bool IsWindowMinimized(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd) && IsIconic(hwnd);

    /// <summary>
    /// Windows DWM küçültülmüş (minimized) pencerelerde kare üretmeyi durdurduğu için,
    /// pencere simge durumundaysa odağı çalmadan açıp Z-sırasında arka plana gönderir.
    /// </summary>
    public static void EnsureWindowRenderableInBackground(IntPtr hwnd)
    {
        if (!IsWindowMinimized(hwnd))
            return;

        IntPtr prevFg = GetForegroundWindow();
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        if (IsIconic(hwnd))
            ShowWindow(hwnd, SW_RESTORE);

        if (prevFg != IntPtr.Zero && prevFg != hwnd && IsWindow(prevFg))
        {
            SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            SetForegroundWindow(prevFg);
        }
    }

    public static IntPtr ResolveTargetWindow(Bitiq.Core.Models.CaptureRegion region)
    {
        if (!region.IsWindowLocked)
            return IntPtr.Zero;

        if (region.TargetWindowHandle != 0)
        {
            var candidate = new IntPtr(region.TargetWindowHandle);
            if (IsWindowValid(candidate))
                return candidate;
        }

        var windows = GetCapturableWindows();
        if (windows.Count == 0)
            return IntPtr.Zero;

        string proc = region.TargetWindowProcessName ?? string.Empty;
        string title = region.TargetWindowTitle ?? string.Empty;

        // 1. Tam süreç adı + tam başlık eşleşmesi
        var exact = windows.FirstOrDefault(w =>
            string.Equals(w.ProcessName, proc, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(w.Title, title, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact.Hwnd;

        // 2. Tam süreç adı + başlık içerik eşleşmesi (tarayıcı/video başlıkları sekme adına göre değişebilir)
        if (!string.IsNullOrWhiteSpace(title))
        {
            var partialTitle = windows.FirstOrDefault(w =>
                string.Equals(w.ProcessName, proc, StringComparison.OrdinalIgnoreCase) &&
                (w.Title.Contains(title, StringComparison.OrdinalIgnoreCase) ||
                 title.Contains(w.Title, StringComparison.OrdinalIgnoreCase)));
            if (partialTitle is not null)
                return partialTitle.Hwnd;
        }

        // 3. Süreç adına göre ilk uygun pencere
        if (!string.IsNullOrWhiteSpace(proc))
        {
            var byProcess = windows.FirstOrDefault(w =>
                string.Equals(w.ProcessName, proc, StringComparison.OrdinalIgnoreCase));
            if (byProcess is not null)
                return byProcess.Hwnd;
        }

        return IntPtr.Zero;
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len <= 0)
            return string.Empty;

        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString().Trim();
    }

    private static string TryGetProcessName(uint pid)
    {
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static long GetWindowLongPtrCompat(IntPtr hwnd, int nIndex)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hwnd, nIndex).ToInt64()
            : GetWindowLong32(hwnd, nIndex);
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeRect(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeInt(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }
}
