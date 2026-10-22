namespace Unvived.Gpu;

/// <summary>
/// HLSL for <see cref="GpuLipModel"/>: the same steps as LipImage and
/// LipNetwork. Each kernel is its own source so register bindings never clash.
/// </summary>
internal static class Shaders
{
    /// <summary>Shared constants; matches <see cref="GpuLipModel.Params"/>.</summary>
    private const string Header = """
        cbuffer Params : register(b0)
        {
            uint InC; uint InSize; uint OutC; uint OutSize;
            uint K; uint Pool; uint Relu; uint InLen;
            float4 Taps;
            float Scale; float3 Unused;
        };
        static const uint FrameW = 800;
        static const uint FrameH = 400;
        static const uint View = 400;
        static const uint Small = 100;

        """;

    /// <summary>cv::medianBlur 5x5, replicated borders. Frame bytes are packed four per uint.</summary>
    public const string Median = Header + """
        StructuredBuffer<uint> Frame : register(t0);
        RWStructuredBuffer<uint> Blurred : register(u0);

        uint Pixel(int x, int y)
        {
            uint i = (uint)clamp(y, 0, (int)FrameH - 1) * FrameW + (uint)clamp(x, 0, (int)FrameW - 1);
            return (Frame[i >> 2] >> ((i & 3) * 8)) & 0xFF;
        }

        [numthreads(16, 16, 1)]
        void Main(uint3 id : SV_DispatchThreadID)
        {
            if (id.x >= FrameW || id.y >= FrameH)
                return;
            uint v[25];
            [unroll] for (int dy = 0; dy < 5; dy++)
                [unroll] for (int dx = 0; dx < 5; dx++)
                    v[dy * 5 + dx] = Pixel((int)id.x + dx - 2, (int)id.y + dy - 2);
            // Selection sort up to the middle element.
            for (uint i = 0; i <= 12; i++)
            {
                uint m = i;
                for (uint j = i + 1; j < 25; j++)
                    m = v[j] < v[m] ? j : m;
                uint t = v[i]; v[i] = v[m]; v[m] = t;
            }
            Blurred[id.y * FrameW + id.x] = v[12];
        }
        """;

    /// <summary>Horizontal cubic pass in integers; x = output column, y = row, z = view.</summary>
    public const string ResizeRows = Header + """
        StructuredBuffer<uint> Blurred : register(t0);
        RWStructuredBuffer<int> Rows : register(u0);

        [numthreads(100, 1, 1)]
        void Main(uint3 id : SV_DispatchThreadID)
        {
            uint s = id.y * FrameW + id.z * View + id.x * 4;
            Rows[(id.z * View + id.y) * Small + id.x] =
                (int)Blurred[s] * -192 + (int)Blurred[s + 1] * 1216
                + (int)Blurred[s + 2] * 1216 + (int)Blurred[s + 3] * -192;
        }
        """;

    /// <summary>
    /// Vertical cubic pass in float, rounded half to even like OpenCV's SIMD
    /// path, then scaled to [0, 1]. "precise" keeps the multiply-adds unfused
    /// so the result matches the CPU bit for bit.
    /// </summary>
    public const string ResizeColumns = Header + """
        StructuredBuffer<int> Rows : register(t0);
        RWStructuredBuffer<float> Input : register(u0);

        [numthreads(10, 10, 1)]
        void Main(uint3 id : SV_DispatchThreadID)
        {
            uint s = (id.z * View + id.y * 4) * Small + id.x;
            precise float v = (float)Rows[s] * Taps.x + (float)Rows[s + Small] * Taps.y
                + (float)Rows[s + 2 * Small] * Taps.z + (float)Rows[s + 3 * Small] * Taps.w;
            precise float px = clamp(round(v), 0.0, 255.0);
            Input[(id.z * Small + id.y) * Small + id.x] = px * Scale;
        }
        """;

    /// <summary>
    /// Valid stride-1 convolution, bias, ReLU and (when Pool) 2x2 max pool
    /// in one pass; one thread per output value, z = output channel.
    /// </summary>
    public const string Conv = Header + """
        StructuredBuffer<float> X : register(t0);
        StructuredBuffer<float> W : register(t1);
        StructuredBuffer<float> B : register(t2);
        RWStructuredBuffer<float> Y : register(u0);

        [numthreads(8, 8, 1)]
        void Main(uint3 id : SV_DispatchThreadID)
        {
            if (id.x >= OutSize || id.y >= OutSize)
                return;
            uint oc = id.z;
            uint step = Pool + 1;
            float best = -3.402823e38;
            for (uint py = 0; py < step; py++)
            {
                for (uint px = 0; px < step; px++)
                {
                    uint cy = id.y * step + py, cx = id.x * step + px;
                    float acc = B[oc];
                    for (uint c = 0; c < InC; c++)
                    {
                        uint w = (oc * InC + c) * K * K;
                        uint x = (c * InSize + cy) * InSize + cx;
                        for (uint ky = 0; ky < K; ky++)
                            for (uint kx = 0; kx < K; kx++)
                                acc += W[w + ky * K + kx] * X[x + ky * InSize + kx];
                    }
                    best = max(best, acc);
                }
            }
            Y[(oc * OutSize + id.y) * OutSize + id.x] = max(best, 0.0);
        }
        """;

    /// <summary>Dense layer; one 256-thread group per output.</summary>
    public const string Dense = Header + """
        StructuredBuffer<float> X : register(t0);
        StructuredBuffer<float> W : register(t1);
        StructuredBuffer<float> B : register(t2);
        RWStructuredBuffer<float> Y : register(u0);

        groupshared float Partial[256];

        [numthreads(256, 1, 1)]
        void Main(uint3 group : SV_GroupID, uint t : SV_GroupIndex)
        {
            uint o = group.x;
            float acc = 0;
            for (uint i = t; i < InLen; i += 256)
                acc += W[o * InLen + i] * X[i];
            Partial[t] = acc;
            GroupMemoryBarrierWithGroupSync();
            [unroll] for (uint s = 128; s > 0; s >>= 1)
            {
                if (t < s)
                    Partial[t] += Partial[t + s];
                GroupMemoryBarrierWithGroupSync();
            }
            if (t == 0)
            {
                float v = Partial[0] + B[o];
                Y[o] = Relu != 0 ? max(v, 0.0) : v;
            }
        }
        """;
}
