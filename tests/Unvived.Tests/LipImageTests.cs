using System.Security.Cryptography;
using Xunit;

namespace Unvived.Tests;

public class LipImageTests
{
    [Fact]
    public void MedianNetworkSortsRandomInputs()
    {
        var network = LipImage.BuildMedianNetwork(25);
        var random = new Random(1);
        var values = new int[25];
        for (int trial = 0; trial < 20000; trial++)
        {
            for (int i = 0; i < values.Length; i++)
                values[i] = random.Next(trial % 2 == 0 ? 4 : 256);
            var expected = values.Order().ToArray();
            foreach (var (a, b) in network)
            {
                if (values[a] > values[b])
                    (values[a], values[b]) = (values[b], values[a]);
            }
            Assert.Equal(expected, values);
        }
    }

    [Fact]
    public void MedianBlurMatchesBruteForceAtEdges()
    {
        const int width = 37, height = 9; // not a multiple of the vector width
        var random = new Random(2);
        var src = new byte[width * height];
        random.NextBytes(src);
        var dst = new byte[src.Length];
        LipImage.MedianBlur5(src, dst, width, height);

        var window = new byte[25];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int n = 0;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                        window[n++] = src[Math.Clamp(y + dy, 0, height - 1) * width + Math.Clamp(x + dx, 0, width - 1)];
                Array.Sort(window);
                Assert.Equal(window[12], dst[y * width + x]);
            }
        }
    }

    [Fact]
    public void MatchesOpenCv()
    {
        using var fixture = TestFrames.LoadFixture("image.json");
        Assert.NotNull(fixture);
        var root = fixture!.RootElement;

        byte[] frame = TestFrames.Synthetic();
        Assert.Equal(root.GetProperty("frame_sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(frame)));

        var blurred = new byte[frame.Length];
        LipImage.MedianBlur5(frame, blurred, LipImage.FrameWidth, LipImage.FrameHeight);
        Assert.Equal(root.GetProperty("blurred_sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(blurred)));

        var small = new byte[100 * 100];
        LipImage.Downscale(blurred, 0, small);
        Assert.Equal(root.GetProperty("left_100").EnumerateArray().Select(e => e.GetByte()), small);
        LipImage.Downscale(blurred, 400, small);
        Assert.Equal(root.GetProperty("right_100").EnumerateArray().Select(e => e.GetByte()), small);
    }
}
