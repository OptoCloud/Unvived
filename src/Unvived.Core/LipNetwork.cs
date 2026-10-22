using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Unvived;

/// <summary>
/// SRanipal's prediction CNN (the same graph for lip and eye, only the
/// output width differs), run on the CPU from the TVM weights:
/// conv5x5(2->20) ReLU, maxpool 2, conv5x5(20->48) ReLU, maxpool 2,
/// conv3x3(48->64) ReLU, dense 25600->500, dense 500->N ReLU.
/// All convolutions are stride 1 without padding.
/// </summary>
public sealed class LipNetwork
{
    public const int InputChannels = 2;
    public const int InputSize = 100;
    public const int InputLength = InputChannels * InputSize * InputSize;

    internal const int C1 = 20, K1 = 5, S1 = InputSize - K1 + 1; // 96
    internal const int P1 = S1 / 2;                              // 48
    internal const int C2 = 48, K2 = 5, S2 = P1 - K2 + 1;        // 44
    internal const int P2 = S2 / 2;                              // 22
    internal const int C3 = 64, K3 = 3, S3 = P2 - K3 + 1;        // 20
    internal const int Flat = C3 * S3 * S3;                      // 25600
    internal const int Hidden = 500;

    private readonly float[] _w1, _b1, _w2, _b2, _w3, _b3, _w5, _b5, _w6, _b6;
    private readonly float[] _input = new float[InputLength];
    private readonly float[] _output;
    private readonly float[] _conv1 = new float[C1 * S1 * S1];
    private readonly float[] _pool1 = new float[C1 * P1 * P1];
    private readonly float[] _conv2 = new float[C2 * S2 * S2];
    private readonly float[] _pool2 = new float[C2 * P2 * P2];
    private readonly float[] _conv3 = new float[Flat];
    private readonly float[] _hidden = new float[Hidden];
    private readonly ParallelOptions _parallel;

    public int OutputCount { get; }

    public LipNetwork(IReadOnlyDictionary<string, Tensor> weights, int maxThreads = 4)
    {
        _w1 = Get(weights, "conv1_weight", C1, InputChannels, K1, K1);
        _b1 = Get(weights, "conv1_bias", C1);
        _w2 = Get(weights, "conv2_weight", C2, C1, K2, K2);
        _b2 = Get(weights, "conv2_bias", C2);
        _w3 = Get(weights, "conv3_weight", C3, C2, K3, K3);
        _b3 = Get(weights, "conv3_bias", C3);
        _w5 = Get(weights, "fc5_ft_weight", Hidden, Flat);
        _b5 = Get(weights, "fc5_ft_bias", Hidden);
        OutputCount = weights["fc6_10_bias"].Shape[0];
        _w6 = Get(weights, "fc6_10_weight", OutputCount, Hidden);
        _b6 = Get(weights, "fc6_10_bias", OutputCount);
        _output = new float[OutputCount];
        _parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxThreads) };
    }

    public static LipNetwork Load(string paramsPath, int maxThreads = 4) =>
        new(TvmParams.Load(paramsPath), maxThreads);

    /// <summary>Runs the network on <paramref name="input"/> (2x100x100, channel-major).</summary>
    public void Run(ReadOnlySpan<float> input, Span<float> output)
    {
        if (input.Length != InputLength)
            throw new ArgumentException($"input must have {InputLength} values", nameof(input));
        if (output.Length < OutputCount)
            throw new ArgumentException($"output needs {OutputCount} values", nameof(output));

        input.CopyTo(_input);
        Conv(_input, InputChannels, InputSize, _w1, _b1, C1, K1, _conv1);
        MaxPool2(_conv1, C1, S1, _pool1);
        Conv(_pool1, C1, P1, _w2, _b2, C2, K2, _conv2);
        MaxPool2(_conv2, C2, S2, _pool2);
        Conv(_pool2, C2, P2, _w3, _b3, C3, K3, _conv3);
        Dense(_conv3, _w5, _b5, _hidden, relu: false);
        Dense(_hidden, _w6, _b6, _output, relu: true);
        _output.CopyTo(output);
    }

    internal static float[] Get(IReadOnlyDictionary<string, Tensor> weights, string name, params int[] shape)
    {
        if (!weights.TryGetValue(name, out var tensor))
            throw new InvalidDataException($"model has no tensor {name}");
        if (!tensor.Shape.SequenceEqual(shape))
            throw new InvalidDataException(
                $"{name} has shape ({string.Join(",", tensor.Shape)}), expected ({string.Join(",", shape)})");
        return tensor.Data;
    }

    /// <summary>Valid (unpadded) stride-1 convolution with bias and ReLU; OIHW weights.</summary>
    private void Conv(float[] src, int inC, int inSize, float[] w, float[] b, int outC, int k, float[] dst)
    {
        int outSize = inSize - k + 1;
        int plane = outSize * outSize;
        Parallel.For(0, outC, _parallel, oc =>
        {
            Span<float> o = dst.AsSpan(oc * plane, plane);
            o.Fill(b[oc]);
            for (int ic = 0; ic < inC; ic++)
            {
                int wBase = (oc * inC + ic) * k * k;
                int inBase = ic * inSize * inSize;
                for (int ky = 0; ky < k; ky++)
                {
                    for (int kx = 0; kx < k; kx++)
                    {
                        float weight = w[wBase + ky * k + kx];
                        for (int y = 0; y < outSize; y++)
                        {
                            Axpy(weight, src.AsSpan(inBase + (y + ky) * inSize + kx, outSize),
                                o.Slice(y * outSize, outSize));
                        }
                    }
                }
            }
            for (int i = 0; i < o.Length; i++)
                o[i] = MathF.Max(o[i], 0f);
        });
    }

    private static void MaxPool2(float[] src, int channels, int inSize, float[] dst)
    {
        int outSize = inSize / 2;
        for (int c = 0; c < channels; c++)
        {
            int si = c * inSize * inSize, di = c * outSize * outSize;
            for (int y = 0; y < outSize; y++)
            {
                int r0 = si + 2 * y * inSize, r1 = r0 + inSize;
                for (int x = 0; x < outSize; x++)
                {
                    int i = 2 * x;
                    dst[di + y * outSize + x] = MathF.Max(
                        MathF.Max(src[r0 + i], src[r0 + i + 1]),
                        MathF.Max(src[r1 + i], src[r1 + i + 1]));
                }
            }
        }
    }

    private void Dense(float[] x, float[] w, float[] b, float[] y, bool relu)
    {
        int n = x.Length;
        Parallel.For(0, y.Length, _parallel, o =>
        {
            float v = b[o] + Dot(w.AsSpan(o * n, n), x);
            y[o] = relu ? MathF.Max(v, 0f) : v;
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Axpy(float a, ReadOnlySpan<float> x, Span<float> y)
    {
        ref float xr = ref MemoryMarshal.GetReference(x);
        ref float yr = ref MemoryMarshal.GetReference(y);
        int n = y.Length, i = 0;
        var va = new Vector<float>(a);
        for (; i <= n - Vector<float>.Count; i += Vector<float>.Count)
        {
            var vy = Vector.LoadUnsafe(ref yr, (nuint)i) + va * Vector.LoadUnsafe(ref xr, (nuint)i);
            vy.StoreUnsafe(ref yr, (nuint)i);
        }
        for (; i < n; i++)
            Unsafe.Add(ref yr, i) += a * Unsafe.Add(ref xr, i);
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        ref float ar = ref MemoryMarshal.GetReference(a);
        ref float br = ref MemoryMarshal.GetReference(b);
        int n = a.Length, i = 0;
        var acc = Vector<float>.Zero;
        for (; i <= n - Vector<float>.Count; i += Vector<float>.Count)
            acc += Vector.LoadUnsafe(ref ar, (nuint)i) * Vector.LoadUnsafe(ref br, (nuint)i);
        float sum = Vector.Sum(acc);
        for (; i < n; i++)
            sum += Unsafe.Add(ref ar, i) * Unsafe.Add(ref br, i);
        return sum;
    }
}
