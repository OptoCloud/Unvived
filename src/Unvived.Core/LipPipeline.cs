using System.Runtime.Versioning;
using Unvived.Gpu;

namespace Unvived;

/// <summary>
/// One Facial Tracker frame (800x400, 8-bit) to SRanipal's 60 lip weights.
/// Not thread-safe: use one instance per capture thread.
/// </summary>
public sealed class LipPipeline : IDisposable
{
    private readonly ILipModel _model;
    private readonly LipPostProcessor _post = new();
    private readonly float[] _raw;

    public LipPipeline(ILipModel model)
    {
        if (model.OutputCount != LipPostProcessor.RawCount)
            throw new ArgumentException(
                $"lip model has {model.OutputCount} outputs, expected {LipPostProcessor.RawCount}", nameof(model));
        _model = model;
        _raw = new float[model.OutputCount];
    }

    /// <summary>
    /// Loads the weights and runs them on the GPU, or on the CPU when
    /// <paramref name="useGpu"/> is false or no Direct3D 11 device works.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static LipPipeline Load(string paramsPath, bool useGpu = true, Action<string>? log = null)
    {
        var weights = TvmParams.Load(paramsPath);
        if (useGpu)
        {
            try
            {
                return new LipPipeline(GpuLipModel.Create(weights));
            }
            catch (Exception e)
            {
                log?.Invoke($"GPU unavailable ({e.Message}); using the CPU");
            }
        }
        return new LipPipeline(new CpuLipModel(new LipNetwork(weights)));
    }

    /// <summary>Where the model runs, for logs.</summary>
    public string Device => _model.Device;

    /// <summary>The model's outputs for the last frame, before post-processing.</summary>
    public ReadOnlySpan<float> RawOutputs => _raw;

    /// <summary>True when the last frame had a face and output has started.</summary>
    public bool Tracking => _post.Tracking;

    public void Process(ReadOnlySpan<byte> frame, Span<float> weights)
    {
        if (frame.Length < LipImage.FrameBytes)
            throw new ArgumentException($"frame must be {LipImage.FrameWidth}x{LipImage.FrameHeight}", nameof(frame));
        _model.Run(frame, _raw);
        _post.Process(_raw, weights);
    }

    public void Dispose() => _model.Dispose();
}
