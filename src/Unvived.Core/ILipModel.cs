namespace Unvived;

/// <summary>One 800x400 Facial Tracker frame to the lip model's raw outputs.</summary>
public interface ILipModel : IDisposable
{
    /// <summary>Where the model runs, for logs.</summary>
    string Device { get; }

    int OutputCount { get; }

    void Run(ReadOnlySpan<byte> frame, Span<float> raw);
}

/// <summary>Preprocessing and the network on the CPU.</summary>
public sealed class CpuLipModel(LipNetwork network) : ILipModel
{
    private readonly byte[] _blurred = new byte[LipImage.FrameBytes];
    private readonly float[] _input = new float[LipNetwork.InputLength];

    public string Device => "CPU";

    public int OutputCount => network.OutputCount;

    public void Run(ReadOnlySpan<byte> frame, Span<float> raw)
    {
        LipImage.BuildInput(frame, _blurred, _input);
        network.Run(_input, raw);
    }

    public void Dispose()
    {
    }
}
