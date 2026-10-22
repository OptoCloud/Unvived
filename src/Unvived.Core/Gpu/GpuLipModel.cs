using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using D3D = Unvived.Gpu.Direct3D11;

namespace Unvived.Gpu;

/// <summary>
/// The whole lip model on the GPU with Direct3D 11 compute shaders: median
/// blur, cubic downscale and the network. The CPU only uploads the frame
/// and reads back the outputs. Not thread-safe.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GpuLipModel : ILipModel
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Params
    {
        public uint InC, InSize, OutC, OutSize, K, Pool, Relu, InLen;
        public float Tap0, Tap1, Tap2, Tap3;
        public float Scale, Unused0, Unused1, Unused2;
    }

    private readonly record struct Pass(nint Shader, nint Constants, nint[] Inputs, nint Output, uint X, uint Y, uint Z);

    private nint _device, _context;
    private readonly List<nint> _owned = [];
    private readonly Pass[] _passes;
    private readonly nint _frame, _input, _output, _staging;
    private readonly int _outputCount;
    private nint _fence, _context4;
    private ulong _fenceValue;
    private readonly AutoResetEvent _gpuDone = new(false);

    private GpuLipModel(IReadOnlyDictionary<string, Tensor> weights)
    {
        (_device, _context) = D3D.CreateDevice();
        try
        {
            int n = weights["fc6_10_bias"].Shape[0];
            _outputCount = n;
            const int c1 = LipNetwork.C1, c2 = LipNetwork.C2, c3 = LipNetwork.C3;
            const int p1 = LipNetwork.P1, p2 = LipNetwork.P2, s3 = LipNetwork.S3;
            const int inSize = LipNetwork.InputSize;

            nint median = Shader(Shaders.Median), rows = Shader(Shaders.ResizeRows), columns = Shader(Shaders.ResizeColumns);
            nint conv = Shader(Shaders.Conv), dense = Shader(Shaders.Dense);

            _frame = Buffer(LipImage.FrameBytes / 4, sizeof(uint), D3D.BindShaderResource);
            var (_, blurredSrv, blurredUav) = Scratch(LipImage.FrameBytes, sizeof(uint));
            var (_, rowsSrv, rowsUav) = Scratch(2 * LipImage.ViewSize * inSize, sizeof(int));
            var (input, inputSrv, inputUav) = Scratch(LipNetwork.InputLength, sizeof(float));
            var (_, pool1Srv, pool1Uav) = Scratch(c1 * p1 * p1, sizeof(float));
            var (_, pool2Srv, pool2Uav) = Scratch(c2 * p2 * p2, sizeof(float));
            var (_, conv3Srv, conv3Uav) = Scratch(LipNetwork.Flat, sizeof(float));
            var (_, hiddenSrv, hiddenUav) = Scratch(LipNetwork.Hidden, sizeof(float));
            _output = Buffer(n, sizeof(float), D3D.BindUnorderedAccess);
            nint outputUav = Own(D3D.CreateUnorderedAccessView(_device, _output));
            _staging = Own(D3D.CreateBuffer(_device, (uint)(n * sizeof(float)), D3D.UsageStaging, 0, D3D.CpuAccessRead, 0, 0));
            _input = input;

            nint W(string name, params int[] shape) =>
                Own(D3D.CreateShaderResourceView(_device, Weights(LipNetwork.Get(weights, name, shape))));

            (_fence, _context4) = D3D.CreateFence(_device, _context);

            float[] taps = LipImage.VerticalTaps;
            var resize = new Params { Tap0 = taps[0], Tap1 = taps[1], Tap2 = taps[2], Tap3 = taps[3], Scale = LipImage.InputScale };
            nint frameSrv = Own(D3D.CreateShaderResourceView(_device, _frame));

            _passes =
            [
                new(median, Constants(resize), [frameSrv], blurredUav,
                    Groups(LipImage.FrameWidth, 16), Groups(LipImage.FrameHeight, 16), 1),
                new(rows, Constants(resize), [blurredSrv], rowsUav, 1, LipImage.ViewSize, 2),
                new(columns, Constants(resize), [rowsSrv], inputUav, inSize / 10, inSize / 10, 2),
                ConvPass(conv, inputSrv, W("conv1_weight", c1, LipNetwork.InputChannels, 5, 5), W("conv1_bias", c1),
                    pool1Uav, LipNetwork.InputChannels, inSize, c1, p1, 5, pool: true),
                ConvPass(conv, pool1Srv, W("conv2_weight", c2, c1, 5, 5), W("conv2_bias", c2),
                    pool2Uav, c1, p1, c2, p2, 5, pool: true),
                ConvPass(conv, pool2Srv, W("conv3_weight", c3, c2, 3, 3), W("conv3_bias", c3),
                    conv3Uav, c2, p2, c3, s3, 3, pool: false),
                new(dense, Constants(new Params { InLen = LipNetwork.Flat, Relu = 0 }),
                    [conv3Srv, W("fc5_ft_weight", LipNetwork.Hidden, LipNetwork.Flat), W("fc5_ft_bias", LipNetwork.Hidden)],
                    hiddenUav, LipNetwork.Hidden, 1, 1),
                new(dense, Constants(new Params { InLen = LipNetwork.Hidden, Relu = 1 }),
                    [hiddenSrv, W("fc6_10_weight", n, LipNetwork.Hidden), W("fc6_10_bias", n)],
                    outputUav, (uint)n, 1, 1),
            ];
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public static GpuLipModel Load(string paramsPath) => new(TvmParams.Load(paramsPath));

    public static GpuLipModel Create(IReadOnlyDictionary<string, Tensor> weights) => new(weights);

    public string Device => _fence != 0 ? "GPU (Direct3D 11)" : "GPU (Direct3D 11, polled)";

    public int OutputCount => _outputCount;

    public void Run(ReadOnlySpan<byte> frame, Span<float> raw)
    {
        if (frame.Length < LipImage.FrameBytes)
            throw new ArgumentException($"frame must be {LipImage.FrameWidth}x{LipImage.FrameHeight}", nameof(frame));
        if (raw.Length < _outputCount)
            throw new ArgumentException($"need room for {_outputCount} outputs", nameof(raw));

        D3D.Update(_context, _frame, frame[..LipImage.FrameBytes]);
        foreach (var pass in _passes)
        {
            // Output first: binding a view as input while it is still the
            // bound output would silently unbind the input.
            D3D.SetUnorderedAccessView(_context, pass.Output);
            D3D.SetShaderResources(_context, pass.Inputs);
            D3D.SetConstantBuffer(_context, pass.Constants);
            D3D.SetShader(_context, pass.Shader);
            D3D.Dispatch(_context, pass.X, pass.Y, pass.Z);
        }
        D3D.CopyResource(_context, _staging, _output);
        if (_fence != 0)
            D3D.WaitForGpu(_context4, _fence, ref _fenceValue, _gpuDone);
        else
            D3D.Flush(_context);
        D3D.ReadStaging(_context, _staging, MemoryMarshal.AsBytes(raw[.._outputCount]), gpuDone: _fence != 0);
    }

    public void Dispose()
    {
        for (int i = _owned.Count - 1; i >= 0; i--)
        {
            nint obj = _owned[i];
            D3D.Release(ref obj);
        }
        _owned.Clear();
        D3D.Release(ref _fence);
        D3D.Release(ref _context4);
        D3D.Release(ref _context);
        D3D.Release(ref _device);
        _gpuDone.Dispose();
    }

    private static uint Groups(int size, int perGroup) => (uint)((size + perGroup - 1) / perGroup);

    private nint Own(nint obj)
    {
        _owned.Add(obj);
        return obj;
    }

    private nint Shader(string source) => Own(D3D.CreateComputeShader(_device, source, "Main"));

    private nint Buffer(int count, int stride, uint bind) =>
        Own(D3D.CreateBuffer(_device, (uint)(count * stride), D3D.UsageDefault, bind, 0, D3D.MiscBufferStructured, (uint)stride));

    /// <summary>A GPU-only intermediate, readable by the next pass and writable by this one.</summary>
    private (nint Buffer, nint Srv, nint Uav) Scratch(int count, int stride)
    {
        nint buffer = Buffer(count, stride, D3D.BindShaderResource | D3D.BindUnorderedAccess);
        return (buffer, Own(D3D.CreateShaderResourceView(_device, buffer)), Own(D3D.CreateUnorderedAccessView(_device, buffer)));
    }

    private nint Weights(float[] data) =>
        Own(D3D.CreateBuffer(_device, (uint)(data.Length * sizeof(float)), D3D.UsageDefault, D3D.BindShaderResource, 0,
            D3D.MiscBufferStructured, sizeof(float), MemoryMarshal.AsBytes(data.AsSpan())));

    private nint Constants(Params p) =>
        Own(D3D.CreateBuffer(_device, (uint)Marshal.SizeOf<Params>(), D3D.UsageDefault, D3D.BindConstantBuffer, 0, 0, 0,
            MemoryMarshal.AsBytes(new ReadOnlySpan<Params>(in p))));

    private Pass ConvPass(nint shader, nint input, nint weights, nint bias, nint output,
        int inC, int inSize, int outC, int outSize, int k, bool pool)
    {
        var p = new Params
        {
            InC = (uint)inC, InSize = (uint)inSize, OutC = (uint)outC, OutSize = (uint)outSize,
            K = (uint)k, Pool = pool ? 1u : 0u,
        };
        return new(shader, Constants(p), [input, weights, bias], output, Groups(outSize, 8), Groups(outSize, 8), (uint)outC);
    }

    /// <summary>Reads the network input the GPU built for the last frame (tests only).</summary>
    internal void ReadInput(Span<float> input)
    {
        nint staging = D3D.CreateBuffer(_device, LipNetwork.InputLength * sizeof(float), D3D.UsageStaging, 0, D3D.CpuAccessRead, 0, 0);
        try
        {
            D3D.CopyResource(_context, staging, _input);
            D3D.ReadStaging(_context, staging, MemoryMarshal.AsBytes(input));
        }
        finally
        {
            D3D.Release(ref staging);
        }
    }
}
