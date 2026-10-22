using Xunit;

namespace Unvived.Tests;

public class LipNetworkTests
{
    /// <summary>
    /// Compares with the numpy implementation in make_fixtures.py. Needs the
    /// local fixture (built from HTC's weights, never committed); skipped
    /// silently without it.
    /// </summary>
    [Fact]
    public void MatchesNumpyReference()
    {
        using var fixture = TestFrames.LoadFixture(Path.Combine("local", "network.json"));
        if (fixture == null)
            return;
        string paramsPath = fixture.RootElement.GetProperty("params").GetString()!;
        if (!Path.IsPathRooted(paramsPath))
            paramsPath = Path.Combine(TestFrames.FixturePath(""), "..", "..", paramsPath);
        float[] expected = fixture.RootElement.GetProperty("outputs").EnumerateArray().Select(e => e.GetSingle()).ToArray();

        var network = LipNetwork.Load(paramsPath);
        byte[] frame = TestFrames.Synthetic();
        var input = new float[LipNetwork.InputLength];
        LipImage.BuildInput(frame, new byte[frame.Length], input);
        var output = new float[network.OutputCount];
        network.Run(input, output);

        for (int i = 0; i < expected.Length; i++)
            Assert.True(MathF.Abs(expected[i] - output[i]) < 1e-3f, $"output {i}: expected {expected[i]}, got {output[i]}");
    }
}
