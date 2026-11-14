namespace Bitiq.Core.Capture;

/// <summary>
/// Küçültülmüş gri tonlama ızgarası üzerinden kare değişimini çok düşük maliyetle tespit eder.
/// </summary>
public sealed class FrameChangeDetector
{
    private const int GridCols = 96;
    private const int GridRows = 24;
    private const int LocalDeltaThreshold = 28;
    private const int MinLocalChangedCells = 12;
    private readonly byte[] _previousLuma = new byte[GridCols * GridRows];
    private readonly byte[] _currentLuma = new byte[GridCols * GridRows];
    private int _prevWidth;
    private int _prevHeight;
    private bool _hasPrevious;

    public void Reset()
    {
        _hasPrevious = false;
        _prevWidth = 0;
        _prevHeight = 0;
    }

    /// <summary>
    /// BGRA32 piksel arabelleğinde 96x24 küçültülmüş gri tonlama farkını (ortalama + yerel değişim) hesaplar.
    /// </summary>
    public unsafe bool HasSignificantChange(
        ReadOnlySpan<byte> bgraPixels,
        int width,
        int height,
        int stride,
        double threshold,
        out double meanAbsDiff)
    {
        meanAbsDiff = 255.0;
        if (width <= 0 || height <= 0 || bgraPixels.Length < height * stride)
            return false;

        int cols = Math.Min(GridCols, width);
        int rows = Math.Min(GridRows, height);
        int totalSamples = cols * rows;

        fixed (byte* ptr = bgraPixels)
        {
            for (int gy = 0; gy < rows; gy++)
            {
                int y = (gy * height) / rows;
                byte* rowPtr = ptr + (y * stride);
                int rowOffset = gy * cols;

                for (int gx = 0; gx < cols; gx++)
                {
                    int x = (gx * width) / cols;
                    byte* px = rowPtr + (x * 4);
                    // BGRA -> luma (BT.601 integer approximation)
                    byte luma = (byte)((px[0] * 29 + px[1] * 150 + px[2] * 77) >> 8);
                    _currentLuma[rowOffset + gx] = luma;
                }
            }
        }

        if (!_hasPrevious || _prevWidth != width || _prevHeight != height)
        {
            Array.Copy(_currentLuma, _previousLuma, totalSamples);
            _prevWidth = width;
            _prevHeight = height;
            _hasPrevious = true;
            return true;
        }

        long diffSum = 0;
        int localChangedCells = 0;
        for (int i = 0; i < totalSamples; i++)
        {
            int d = Math.Abs(_currentLuma[i] - _previousLuma[i]);
            diffSum += d;
            if (d >= LocalDeltaThreshold)
                localChangedCells++;
        }

        meanAbsDiff = (double)diffSum / totalSamples;
        if (meanAbsDiff >= threshold || localChangedCells >= MinLocalChangedCells)
        {
            Array.Copy(_currentLuma, _previousLuma, totalSamples);
            return true;
        }

        return false;
    }
}
