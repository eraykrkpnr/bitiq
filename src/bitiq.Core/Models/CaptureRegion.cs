namespace Bitiq.Core.Models;

public sealed record CaptureRegion
{
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string MonitorDeviceName { get; init; } = string.Empty;
    public int MonitorLeft { get; init; }
    public int MonitorTop { get; init; }
    public int MonitorWidth { get; init; }
    public int MonitorHeight { get; init; }

    public bool IsWindowLocked { get; init; }
    public long TargetWindowHandle { get; init; }
    public string TargetWindowTitle { get; init; } = string.Empty;
    public string TargetWindowProcessName { get; init; } = string.Empty;
    public int WindowRelativeX { get; init; }
    public int WindowRelativeY { get; init; }
    public int ReferenceWindowWidth { get; init; }
    public int ReferenceWindowHeight { get; init; }
    public double NormalizedX { get; init; }
    public double NormalizedY { get; init; }
    public double NormalizedWidth { get; init; }
    public double NormalizedHeight { get; init; }

    public bool IsValid => Width > 8 && Height > 8;

    public int RelativeX => Math.Max(0, X - MonitorLeft);
    public int RelativeY => Math.Max(0, Y - MonitorTop);

    public bool IntersectsWith(int otherX, int otherY, int otherWidth, int otherHeight)
    {
        if (!IsValid || otherWidth <= 0 || otherHeight <= 0)
            return false;

        return X < otherX + otherWidth &&
               X + Width > otherX &&
               Y < otherY + otherHeight &&
               Y + Height > otherY;
    }
}

public sealed record OverlayWindowBounds
{
    public double Left { get; init; } = 160;
    public double Top { get; init; } = 780;
    public double Width { get; init; } = 1100;
    public double Height { get; init; } = 180;
    public bool IsCustomPositioned { get; init; }
}
