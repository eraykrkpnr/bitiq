namespace Bitiq.Core.Capture;

public sealed class CapturedFrameData
{
    public required byte[] BgraPixels { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required double CaptureMs { get; init; }
    public required double ChangeDetectMs { get; init; }
    public required long CapturedAtTimestamp { get; init; }
}
