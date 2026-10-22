using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Unvived.Gpu;

/// <summary>
/// The Direct3D 11 compute calls the GPU model needs, made through raw
/// vtable slots (orders checked against the Windows SDK 10.0.26100 d3d11.h).
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class Direct3D11
{
    public const uint BindShaderResource = 0x8;
    public const uint BindUnorderedAccess = 0x80;
    public const uint BindConstantBuffer = 0x4;
    public const uint MiscBufferStructured = 0x40;
    public const uint CpuAccessRead = 0x20000;
    public const uint UsageDefault = 0;
    public const uint UsageStaging = 3;

    private const uint DriverTypeHardware = 1;
    private const uint SdkVersion = 7;
    private const uint FeatureLevel11 = 0xB000;
    private const uint MapRead = 1;
    private const uint MapFlagDoNotWait = 0x100000;
    private const uint ErrorWasStillDrawing = 0x887A000A; // DXGI_ERROR_WAS_STILL_DRAWING
    private const uint CompileStrict = 1 << 11, CompileOptimise3 = 1 << 15;

    private static readonly Guid IidDevice5 = new("8ffde202-a0e7-45df-9e01-e837801b5ea0");
    private static readonly Guid IidContext4 = new("917600da-f58c-4c33-98d8-3e15b390fa24");
    private static readonly Guid IidFence = new("affde9d1-1df7-4bb7-8a34-0f46251dab80");

    // IUnknown
    private const int SlotQueryInterface = 0, SlotRelease = 2;
    // ID3D11Device5 / ID3D11DeviceContext4 / ID3D11Fence (d3d11_3.h, d3d11_4.h)
    private const int SlotCreateFence = 68, SlotSignal = 147, SlotSetEventOnCompletion = 9;
    // ID3D10Blob
    private const int SlotGetBufferPointer = 3, SlotGetBufferSize = 4;
    // ID3D11Device
    private const int SlotCreateBuffer = 3, SlotCreateShaderResourceView = 7;
    private const int SlotCreateUnorderedAccessView = 8, SlotCreateComputeShader = 18;
    // ID3D11DeviceContext
    private const int SlotMap = 14, SlotUnmap = 15, SlotDispatch = 41, SlotCopyResource = 47;
    private const int SlotUpdateSubresource = 48, SlotCSSetShaderResources = 67;
    private const int SlotCSSetUnorderedAccessViews = 68, SlotCSSetShader = 69;
    private const int SlotCSSetConstantBuffers = 71, SlotFlush = 111;

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferDesc
    {
        public uint ByteWidth, Usage, BindFlags, CpuAccessFlags, MiscFlags, StructureByteStride;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SubresourceData
    {
        public void* SysMem;
        public uint Pitch, SlicePitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MappedSubresource
    {
        public void* Data;
        public uint RowPitch, DepthPitch;
    }

    [LibraryImport("d3d11.dll")]
    private static partial int D3D11CreateDevice(nint adapter, uint driverType, nint software, uint flags,
        uint* featureLevels, uint featureLevelCount, uint sdkVersion, out nint device, out uint featureLevel, out nint context);

    [LibraryImport("d3dcompiler_47.dll", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int D3DCompile(byte* source, nuint sourceSize, string? sourceName, nint defines, nint include,
        string entryPoint, string target, uint flags1, uint flags2, out nint code, out nint errors);

    private static nint Slot(nint obj, int slot) => (*(nint**)obj)[slot];

    private static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new COMException($"{what} failed (0x{hr:X8})", hr);
    }

    public static void Release(ref nint obj)
    {
        if (obj == 0)
            return;
        ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(obj, SlotRelease))(obj);
        obj = 0;
    }

    /// <summary>A hardware device on the default adapter, feature level 11.0.</summary>
    public static (nint Device, nint Context) CreateDevice()
    {
        uint level = FeatureLevel11;
        Check(D3D11CreateDevice(0, DriverTypeHardware, 0, 0, &level, 1, SdkVersion, out nint device, out _, out nint context),
            "D3D11CreateDevice");
        return (device, context);
    }

    /// <summary>Compiles one cs_5_0 entry point and creates the shader.</summary>
    public static nint CreateComputeShader(nint device, string source, string entryPoint)
    {
        byte[] text = Encoding.UTF8.GetBytes(source);
        nint code, errors;
        int hr;
        fixed (byte* p = text)
            hr = D3DCompile(p, (nuint)text.Length, entryPoint, 0, 0, entryPoint, "cs_5_0",
                CompileStrict | CompileOptimise3, 0, out code, out errors);
        try
        {
            if (hr < 0)
            {
                string message = errors == 0 ? "" : Marshal.PtrToStringUTF8(BlobPointer(errors), (int)BlobSize(errors));
                throw new COMException($"compiling {entryPoint} failed: {message.Trim()}", hr);
            }
            nint shader = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nuint, nint, nint*, int>)Slot(device, SlotCreateComputeShader))(
                device, BlobPointer(code), BlobSize(code), 0, &shader), "ID3D11Device::CreateComputeShader");
            return shader;
        }
        finally
        {
            Release(ref code);
            Release(ref errors);
        }
    }

    private static nint BlobPointer(nint blob) => ((delegate* unmanaged[Stdcall]<nint, nint>)Slot(blob, SlotGetBufferPointer))(blob);

    private static nuint BlobSize(nint blob) => ((delegate* unmanaged[Stdcall]<nint, nuint>)Slot(blob, SlotGetBufferSize))(blob);

    public static nint CreateBuffer(nint device, uint byteWidth, uint usage, uint bindFlags, uint cpuAccess,
        uint miscFlags, uint stride, ReadOnlySpan<byte> initial = default)
    {
        var desc = new BufferDesc
        {
            ByteWidth = byteWidth, Usage = usage, BindFlags = bindFlags,
            CpuAccessFlags = cpuAccess, MiscFlags = miscFlags, StructureByteStride = stride,
        };
        nint buffer = 0;
        fixed (byte* data = initial)
        {
            var init = new SubresourceData { SysMem = data };
            Check(((delegate* unmanaged[Stdcall]<nint, BufferDesc*, SubresourceData*, nint*, int>)Slot(device, SlotCreateBuffer))(
                device, &desc, initial.IsEmpty ? null : &init, &buffer), "ID3D11Device::CreateBuffer");
        }
        return buffer;
    }

    /// <summary>A view of the whole (structured) buffer.</summary>
    public static nint CreateShaderResourceView(nint device, nint resource)
    {
        nint view = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, void*, nint*, int>)Slot(device, SlotCreateShaderResourceView))(
            device, resource, null, &view), "ID3D11Device::CreateShaderResourceView");
        return view;
    }

    /// <summary>A view of the whole (structured) buffer.</summary>
    public static nint CreateUnorderedAccessView(nint device, nint resource)
    {
        nint view = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, void*, nint*, int>)Slot(device, SlotCreateUnorderedAccessView))(
            device, resource, null, &view), "ID3D11Device::CreateUnorderedAccessView");
        return view;
    }

    public static void Update(nint context, nint resource, ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data)
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void*, void*, uint, uint, void>)Slot(context, SlotUpdateSubresource))(
                context, resource, 0, null, p, 0, 0);
    }

    public static void SetShader(nint context, nint shader) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)Slot(context, SlotCSSetShader))(context, shader, null, 0);

    public static void SetConstantBuffer(nint context, nint buffer) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Slot(context, SlotCSSetConstantBuffers))(context, 0, 1, &buffer);

    public static void SetShaderResources(nint context, ReadOnlySpan<nint> views)
    {
        fixed (nint* p = views)
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Slot(context, SlotCSSetShaderResources))(
                context, 0, (uint)views.Length, p);
    }

    public static void SetUnorderedAccessView(nint context, nint view) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, uint*, void>)Slot(context, SlotCSSetUnorderedAccessViews))(
            context, 0, 1, &view, null);

    public static void Dispatch(nint context, uint x, uint y, uint z) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, void>)Slot(context, SlotDispatch))(context, x, y, z);

    public static void CopyResource(nint context, nint dst, nint src) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Slot(context, SlotCopyResource))(context, dst, src);

    public static void Flush(nint context) =>
        ((delegate* unmanaged[Stdcall]<nint, void>)Slot(context, SlotFlush))(context);

    /// <summary>
    /// A fence for waiting on the GPU without polling, or (0, 0) when the
    /// device or driver does not support ID3D11Fence (needs Windows 10 1703+).
    /// </summary>
    public static (nint Fence, nint Context4) CreateFence(nint device, nint context)
    {
        nint device5 = 0, context4 = 0, fence = 0;
        try
        {
            if (QueryInterface(device, IidDevice5, out device5) < 0 || QueryInterface(context, IidContext4, out context4) < 0)
                return (0, 0);
            Guid iid = IidFence;
            int hr = ((delegate* unmanaged[Stdcall]<nint, ulong, uint, Guid*, nint*, int>)Slot(device5, SlotCreateFence))(
                device5, 0, 0, &iid, &fence);
            if (hr < 0)
                return (0, 0);
            (nint f, nint c) = (fence, context4);
            fence = context4 = 0;
            return (f, c);
        }
        finally
        {
            Release(ref fence);
            Release(ref context4);
            Release(ref device5);
        }
    }

    /// <summary>Queues a fence signal and sleeps until the GPU has reached it.</summary>
    public static void WaitForGpu(nint context4, nint fence, ref ulong value, EventWaitHandle done)
    {
        value++;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, ulong, int>)Slot(context4, SlotSignal))(context4, fence, value),
            "ID3D11DeviceContext4::Signal");
        Flush(context4); // ID3D11DeviceContext4 extends the immediate context
        Check(((delegate* unmanaged[Stdcall]<nint, ulong, nint, int>)Slot(fence, SlotSetEventOnCompletion))(
            fence, value, done.SafeWaitHandle.DangerousGetHandle()), "ID3D11Fence::SetEventOnCompletion");
        if (!done.WaitOne(1000))
            throw new COMException("GPU did not finish within 1 s", unchecked((int)ErrorWasStillDrawing));
    }

    private static int QueryInterface(nint obj, Guid iid, out nint result)
    {
        nint r = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(obj, SlotQueryInterface))(obj, &iid, &r);
        result = r;
        return hr;
    }

    /// <summary>
    /// Copies a staging buffer to <paramref name="dst"/> once the GPU is done
    /// with it. Unless <paramref name="gpuDone"/> (a fence already confirmed
    /// the copy), polls with sleeps: a blocking Map spins in some drivers.
    /// </summary>
    public static void ReadStaging(nint context, nint staging, Span<byte> dst, bool gpuDone = false)
    {
        var mapped = new MappedSubresource();
        var map = (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, MappedSubresource*, int>)Slot(context, SlotMap);
        long deadline = Environment.TickCount64 + 1000;
        int hr;
        uint flags = gpuDone ? 0 : MapFlagDoNotWait;
        while ((uint)(hr = map(context, staging, 0, MapRead, flags, &mapped)) == ErrorWasStillDrawing)
        {
            if (Environment.TickCount64 > deadline)
            {
                hr = map(context, staging, 0, MapRead, 0, &mapped);
                break;
            }
            Thread.Sleep(1);
        }
        Check(hr, "ID3D11DeviceContext::Map");
        new ReadOnlySpan<byte>(mapped.Data, dst.Length).CopyTo(dst);
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Slot(context, SlotUnmap))(context, staging, 0);
    }
}
