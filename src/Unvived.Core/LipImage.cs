using System.Buffers;
using System.Numerics;

namespace Unvived;

/// <summary>
/// Image steps SRanipal applies before the lip model, reproduced to match
/// OpenCV 3.1 bit for bit on 8-bit input.
/// </summary>
public static class LipImage
{
    public const int FrameWidth = 800;
    public const int FrameHeight = 400;
    public const int FrameBytes = FrameWidth * FrameHeight;
    public const int ViewSize = 400;

    internal const int ResizeFactor = ViewSize / LipNetwork.InputSize; // 4
    private const int ResizeShift = 22; // INTER_RESIZE_COEF_BITS * 2

    // cv::resize INTER_CUBIC (A = -0.75) taps for a 4x downscale: every
    // output sample sits half-way between source pixels 4k+1 and 4k+2, so
    // the four taps are 4k..4k+3 with weights cubic(1.5), cubic(0.5) x2,
    // cubic(1.5), scaled by 2048 as OpenCV does for 8-bit images.
    internal static readonly int[] CubicTaps = [-192, 1216, 1216, -192];

    /// <summary>The taps with both passes' 2048 scale divided out, for the float vertical pass.</summary>
    internal static readonly float[] VerticalTaps = Array.ConvertAll(CubicTaps, t => t * (1f / (1 << ResizeShift)));

    internal const float InputScale = (float)(1.0 / 255.0);

    private static readonly (int A, int B)[] Median25 = BuildMedianNetwork(25);

    /// <summary>cv::medianBlur(src, dst, 5) with replicated borders.</summary>
    public static void MedianBlur5(ReadOnlySpan<byte> src, Span<byte> dst, int width, int height)
    {
        int count = Vector<byte>.Count;
        if (width < count)
            throw new ArgumentException($"width must be at least {count}", nameof(width));

        int paddedWidth = width + 4;
        byte[] padded = ArrayPool<byte>.Shared.Rent(paddedWidth * (height + 4));
        try
        {
            MedianBlur5(src, dst, width, height, padded, paddedWidth);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(padded);
        }
    }

    private static void MedianBlur5(ReadOnlySpan<byte> src, Span<byte> dst, int width, int height,
        byte[] padded, int paddedWidth)
    {
        int count = Vector<byte>.Count;
        for (int y = 0; y < height + 4; y++)
        {
            ReadOnlySpan<byte> row = src.Slice(Math.Clamp(y - 2, 0, height - 1) * width, width);
            Span<byte> p = padded.AsSpan(y * paddedWidth, paddedWidth);
            p[0] = p[1] = row[0];
            row.CopyTo(p.Slice(2));
            p[width + 2] = p[width + 3] = row[width - 1];
        }

        var window = new Vector<byte>[25];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; ; x += count)
            {
                // The last block is shifted left to end at the row's end; it
                // recomputes a few pixels but never reads past the padding.
                int bx = Math.Min(x, width - count);
                for (int dy = 0; dy < 5; dy++)
                {
                    int rowStart = (y + dy) * paddedWidth + bx;
                    for (int dx = 0; dx < 5; dx++)
                        window[dy * 5 + dx] = new Vector<byte>(padded.AsSpan(rowStart + dx, count));
                }
                foreach (var (a, b) in Median25)
                {
                    var lo = Vector.Min(window[a], window[b]);
                    window[b] = Vector.Max(window[a], window[b]);
                    window[a] = lo;
                }
                window[12].CopyTo(dst.Slice(y * width + bx, count));
                if (bx + count >= width)
                    break;
            }
        }
    }

    /// <summary>
    /// Crops the 400x400 view starting at column <paramref name="x0"/> of an
    /// 800x400 frame and shrinks it to 100x100 like
    /// <c>cv::resize(..., Size(100,100), 0, 0, INTER_CUBIC)</c>.
    /// </summary>
    public static void Downscale(ReadOnlySpan<byte> frame, int x0, Span<byte> dst)
    {
        const int outSize = LipNetwork.InputSize;
        int[] rented = ArrayPool<int>.Shared.Rent(ViewSize * outSize);
        try
        {
            Downscale(frame, x0, dst, rented.AsSpan(0, ViewSize * outSize));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rented);
        }
    }

    private static void Downscale(ReadOnlySpan<byte> frame, int x0, Span<byte> dst, Span<int> rows)
    {
        const int outSize = LipNetwork.InputSize;
        for (int y = 0; y < ViewSize; y++)
        {
            ReadOnlySpan<byte> row = frame.Slice(y * FrameWidth + x0, ViewSize);
            for (int x = 0; x < outSize; x++)
            {
                int s = x * ResizeFactor;
                rows[y * outSize + x] = row[s] * CubicTaps[0] + row[s + 1] * CubicTaps[1]
                    + row[s + 2] * CubicTaps[2] + row[s + 3] * CubicTaps[3];
            }
        }
        // OpenCV's SIMD vertical pass works in float and rounds half to even;
        // an integer shift disagrees on a few pixels.
        float b0 = VerticalTaps[0], b1 = VerticalTaps[1], b2 = VerticalTaps[2], b3 = VerticalTaps[3];
        for (int y = 0; y < outSize; y++)
        {
            int s = y * ResizeFactor * outSize;
            for (int x = 0; x < outSize; x++)
            {
                float v = rows[s + x] * b0 + rows[s + outSize + x] * b1
                    + rows[s + 2 * outSize + x] * b2 + rows[s + 3 * outSize + x] * b3;
                dst[y * outSize + x] = (byte)Math.Clamp(MathF.Round(v), 0f, 255f);
            }
        }
    }

    /// <summary>
    /// Builds the model input from an 800x400 frame: median blur, both
    /// 400x400 views downscaled to 100x100, scaled to [0, 1]. Channel 0 is
    /// the left view (columns 0-399), channel 1 the right.
    /// </summary>
    public static void BuildInput(ReadOnlySpan<byte> frame, Span<byte> blurred, Span<float> input)
    {
        MedianBlur5(frame, blurred, FrameWidth, FrameHeight);
        const int plane = LipNetwork.InputSize * LipNetwork.InputSize;
        Span<byte> small = stackalloc byte[plane];
        for (int view = 0; view < 2; view++)
        {
            Downscale(blurred, view * ViewSize, small);
            Span<float> channel = input.Slice(view * plane, plane);
            for (int i = 0; i < plane; i++)
                channel[i] = small[i] * InputScale;
        }
    }

    /// <summary>
    /// Batcher's odd-even merge sort for the next power of two, keeping only
    /// comparators inside <paramref name="n"/> (the missing inputs act as
    /// +infinity, so dropping their comparators leaves a valid network).
    /// </summary>
    internal static (int A, int B)[] BuildMedianNetwork(int n)
    {
        int size = (int)BitOperations.RoundUpToPowerOf2((uint)n);
        var pairs = new List<(int, int)>();
        for (int p = 1; p < size; p += p)
            for (int k = p; k > 0; k /= 2)
                for (int j = k % p; j + k < size; j += k + k)
                    for (int i = 0; i < Math.Min(k, size - j - k); i++)
                        if ((i + j) / (p + p) == (i + j + k) / (p + p) && i + j + k < n)
                            pairs.Add((i + j, i + j + k));
        return [.. pairs];
    }
}
