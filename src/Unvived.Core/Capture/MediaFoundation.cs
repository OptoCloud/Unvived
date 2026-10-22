using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Unvived.Capture;

/// <summary>
/// The few Media Foundation calls the camera needs, made through raw vtable
/// slots (orders checked against the Windows SDK 10.0.26100 headers).
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class MediaFoundation
{
    public const uint FirstVideoStream = 0xFFFFFFFC;
    public const uint ReaderFlagError = 0x1;
    public const uint ReaderFlagEndOfStream = 0x2;

    public static readonly Guid DevSourceType = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");
    public static readonly Guid DevSourceTypeVideo = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
    public static readonly Guid DevSourceFriendlyName = new("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");
    public static readonly Guid DevSourceSymbolicLink = new("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f");
    public static readonly Guid MtSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MtFrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid VideoFormatYuy2 = new("32595559-0000-0010-8000-00aa00389b71");
    public static readonly Guid ReadWriteDisableConverters = new("98d5b065-1374-4847-8d5d-31520fee7156");
    public static readonly Guid IidMediaSource = new("279a808d-aec7-40c8-9c6b-a6b492c78a66");

    private const uint Version = 0x00020070; // MF_SDK_VERSION << 16 | MF_API_VERSION
    private const uint ErrorNoMoreTypes = 0xC00D36B9; // MF_E_NO_MORE_TYPES

    // IUnknown
    private const int SlotQueryInterface = 0, SlotRelease = 2;
    // IMFAttributes
    private const int SlotGetGuid = 10, SlotGetUInt64 = 8, SlotGetAllocatedString = 13;
    private const int SlotSetUInt32 = 21, SlotSetGuid = 24;
    // IMFActivate
    private const int SlotActivateObject = 33;
    // IMFMediaSource
    private const int SlotShutdown = 12;
    // IMFSourceReader
    private const int SlotGetNativeMediaType = 5, SlotSetCurrentMediaType = 7, SlotReadSample = 9;
    // IMFSample
    private const int SlotConvertToContiguousBuffer = 41;
    // IMFMediaBuffer
    private const int SlotLock = 3, SlotUnlock = 4;

    [LibraryImport("mfplat.dll")]
    private static partial int MFStartup(uint version, uint flags);

    [LibraryImport("mfplat.dll")]
    private static partial int MFShutdown();

    [LibraryImport("mfplat.dll")]
    private static partial int MFCreateAttributes(out nint attributes, uint initialSize);

    [LibraryImport("mf.dll")]
    private static partial int MFEnumDeviceSources(nint attributes, out nint activates, out uint count);

    [LibraryImport("mfreadwrite.dll")]
    private static partial int MFCreateSourceReaderFromMediaSource(nint source, nint attributes, out nint reader);

    [LibraryImport("ole32.dll")]
    private static partial void CoTaskMemFree(nint p);

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    /// <summary>COM (multithreaded) and Media Foundation for the calling thread.</summary>
    public static void Startup()
    {
        Check(CoInitializeEx(0, 0), "CoInitializeEx");
        Check(MFStartup(Version, 0), "MFStartup");
    }

    public static void Shutdown()
    {
        MFShutdown();
        CoUninitialize();
    }

    public static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new COMException($"{what} failed (0x{hr:X8})", hr);
    }

    private static nint Slot(nint obj, int slot) => (*(nint**)obj)[slot];

    public static void Release(ref nint obj)
    {
        if (obj == 0)
            return;
        ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(obj, SlotRelease))(obj);
        obj = 0;
    }

    public static int QueryInterface(nint obj, in Guid iid, out nint result)
    {
        nint r = 0;
        int hr;
        fixed (Guid* g = &iid)
            hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(obj, SlotQueryInterface))(obj, g, &r);
        result = r;
        return hr;
    }

    /// <summary>Video capture devices, as IMFActivate pointers the caller must release.</summary>
    public static nint[] EnumerateVideoDevices()
    {
        Check(MFCreateAttributes(out nint attributes, 1), "MFCreateAttributes");
        try
        {
            SetGuid(attributes, DevSourceType, DevSourceTypeVideo);
            Check(MFEnumDeviceSources(attributes, out nint array, out uint count), "MFEnumDeviceSources");
            var result = new nint[count];
            for (int i = 0; i < count; i++)
                result[i] = ((nint*)array)[i];
            CoTaskMemFree(array);
            return result;
        }
        finally
        {
            Release(ref attributes);
        }
    }

    public static string GetString(nint attributes, in Guid key)
    {
        nint text = 0;
        uint length = 0;
        int hr;
        fixed (Guid* k = &key)
            hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, uint*, int>)Slot(attributes, SlotGetAllocatedString))(
                attributes, k, &text, &length);
        if (hr < 0)
            return "";
        string s = Marshal.PtrToStringUni(text, (int)length);
        CoTaskMemFree(text);
        return s;
    }

    public static Guid GetGuid(nint attributes, in Guid key)
    {
        Guid value = default;
        fixed (Guid* k = &key)
            ((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(attributes, SlotGetGuid))(attributes, k, &value);
        return value;
    }

    public static ulong GetUInt64(nint attributes, in Guid key)
    {
        ulong value = 0;
        fixed (Guid* k = &key)
            ((delegate* unmanaged[Stdcall]<nint, Guid*, ulong*, int>)Slot(attributes, SlotGetUInt64))(attributes, k, &value);
        return value;
    }

    public static void SetGuid(nint attributes, in Guid key, in Guid value)
    {
        fixed (Guid* k = &key, v = &value)
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(attributes, SlotSetGuid))(attributes, k, v),
                "IMFAttributes::SetGUID");
    }

    public static void SetUInt32(nint attributes, in Guid key, uint value)
    {
        fixed (Guid* k = &key)
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Slot(attributes, SlotSetUInt32))(attributes, k, value),
                "IMFAttributes::SetUINT32");
    }

    public static nint ActivateMediaSource(nint activate)
    {
        nint source = 0;
        fixed (Guid* iid = &IidMediaSource)
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(activate, SlotActivateObject))(
                activate, iid, &source), "IMFActivate::ActivateObject");
        return source;
    }

    public static void ShutdownSource(nint source) =>
        ((delegate* unmanaged[Stdcall]<nint, int>)Slot(source, SlotShutdown))(source);

    public static nint CreateSourceReader(nint source)
    {
        Check(MFCreateAttributes(out nint attributes, 1), "MFCreateAttributes");
        try
        {
            SetUInt32(attributes, ReadWriteDisableConverters, 1);
            Check(MFCreateSourceReaderFromMediaSource(source, attributes, out nint reader),
                "MFCreateSourceReaderFromMediaSource");
            return reader;
        }
        finally
        {
            Release(ref attributes);
        }
    }

    /// <summary>The reader's native media type at <paramref name="index"/>, or 0 past the end.</summary>
    public static nint GetNativeMediaType(nint reader, uint index)
    {
        nint type = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)Slot(reader, SlotGetNativeMediaType))(
            reader, FirstVideoStream, index, &type);
        if ((uint)hr == ErrorNoMoreTypes)
            return 0;
        Check(hr, "IMFSourceReader::GetNativeMediaType");
        return type;
    }

    public static void SetCurrentMediaType(nint reader, nint type) =>
        Check(((delegate* unmanaged[Stdcall]<nint, uint, uint*, nint, int>)Slot(reader, SlotSetCurrentMediaType))(
            reader, FirstVideoStream, null, type), "IMFSourceReader::SetCurrentMediaType");

    /// <summary>Blocking read of the next sample (0 when the stream sent none).</summary>
    public static int ReadSample(nint reader, out uint streamFlags, out long timestamp, out nint sample)
    {
        uint actual, flags;
        long time;
        nint s = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, uint*, long*, nint*, int>)Slot(reader, SlotReadSample))(
            reader, FirstVideoStream, 0, &actual, &flags, &time, &s);
        streamFlags = flags;
        timestamp = time;
        sample = s;
        return hr;
    }

    /// <summary>Copies a sample's bytes into <paramref name="dst"/>; returns the sample's length.</summary>
    public static int CopySample(nint sample, Span<byte> dst)
    {
        nint buffer = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(sample, SlotConvertToContiguousBuffer))(sample, &buffer),
            "IMFSample::ConvertToContiguousBuffer");
        try
        {
            byte* data;
            uint maxLength, length;
            Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Slot(buffer, SlotLock))(
                buffer, &data, &maxLength, &length), "IMFMediaBuffer::Lock");
            try
            {
                new ReadOnlySpan<byte>(data, (int)Math.Min(length, (uint)dst.Length)).CopyTo(dst);
                return (int)length;
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<nint, int>)Slot(buffer, SlotUnlock))(buffer);
            }
        }
        finally
        {
            Release(ref buffer);
        }
    }
}
