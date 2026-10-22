using Unvived.Gpu;
using Xunit;

namespace Unvived.Tests;

/// <summary>
/// The GPU model against the CPU one. Uses HTC's weights through the local
/// network fixture; skipped silently without it or without a D3D11 GPU.
/// </summary>
public class GpuLipModelTests
{
    private static (GpuLipModel Gpu, LipNetwork Cpu)? Load()
    {
        using var fixture = TestFrames.LoadFixture(Path.Combine("local", "network.json"));
        if (fixture == null)
            return null;
        string paramsPath = fixture.RootElement.GetProperty("params").GetString()!;
        if (!Path.IsPathRooted(paramsPath))
            paramsPath = Path.Combine(TestFrames.FixturePath(""), "..", "..", paramsPath);
        var weights = TvmParams.Load(paramsPath);
        try
        {
            return (GpuLipModel.Create(weights), new LipNetwork(weights));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    [Fact]
    public void PreprocessingMatchesCpuExactly()
    {
        if (Load() is not var (gpu, _))
            return;
        using (gpu)
        {
            byte[] frame = TestFrames.Synthetic();
            gpu.Run(frame, new float[gpu.OutputCount]);
            var gpuInput = new float[LipNetwork.InputLength];
            gpu.ReadInput(gpuInput);

            var cpuInput = new float[LipNetwork.InputLength];
            LipImage.BuildInput(frame, new byte[frame.Length], cpuInput);
            Assert.Equal(cpuInput, gpuInput);
        }
    }

    [Fact]
    public void OutputsMatchCpu()
    {
        if (Load() is not var (gpu, cpu))
            return;
        using (gpu)
        {
            var random = new Random(3);
            var frame = new byte[LipImage.FrameBytes];
            var input = new float[LipNetwork.InputLength];
            var expected = new float[cpu.OutputCount];
            var actual = new float[gpu.OutputCount];
            for (int trial = 0; trial < 3; trial++)
            {
                if (trial == 0)
                    TestFrames.Synthetic().CopyTo(frame, 0);
                else
                    random.NextBytes(frame);
                LipImage.BuildInput(frame, new byte[frame.Length], input);
                cpu.Run(input, expected);
                gpu.Run(frame, actual);
                for (int i = 0; i < expected.Length; i++)
                    Assert.True(MathF.Abs(expected[i] - actual[i]) < 1e-3f, $"trial {trial} output {i}: CPU {expected[i]}, GPU {actual[i]}");
            }
        }
    }
}
