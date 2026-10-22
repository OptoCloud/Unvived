using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Unvived.Capture;

/// <summary>
/// The Facial Tracker's vendor channel: one UVC extension-unit control
/// (selector 2) that carries fixed-size command buffers, reached through
/// IKsControl on the media source. See docs/lip-pipeline.md.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class TrackerControl : IDisposable
{
    private static readonly Guid IidKsControl = new("28f54685-06fd-11d2-b27a-00a0c9223196");
    private static readonly Guid IidKsTopologyInfo = new("720d4ac0-7533-11d0-a5d6-28db04c10000");
    private static readonly Guid NodeTypeDevSpecific = new("941c7ac0-c559-11d0-8a2b-00a0c9255ac1");
    private static readonly Guid PropertySet = new("2ccb0bda-6331-4fdb-850e-79054dbd5671");

    private const uint Selector = 2;
    private const uint TypeGet = 0x10000001, TypeSet = 0x10000002; // KSPROPERTY_TYPE_TOPOLOGY | GET/SET
    private const int ErrorMoreData = unchecked((int)0x800700EA);

    private const byte TaskSet = 0x50, TaskGet = 0x51;
    private const byte RegisterSensor = 0xAB;
    private const byte ReplyPending = 0x55, ReplyDone = 0x56;
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Image sensor setup sent before enabling the stream (address, value).
    /// Without it the IR illuminators stay off. Values from
    /// LordOfDragons/vivefacialtracker (MIT); see docs/lip-pipeline.md.
    /// </summary>
    private static readonly (byte Address, byte Value)[] SensorSetup =
    [
        (0x00, 0x40), (0x08, 0x01), (0x70, 0x00), (0x02, 0xFF), (0x03, 0xFF), (0x04, 0xFF),
        (0x0E, 0x00), (0x05, 0xB2), (0x06, 0xB2), (0x07, 0xB2), (0x0F, 0x03),
    ];

    // IKsTopologyInfo (vidcap.h) and IKsControl (ks.h)
    private const int SlotGetNumNodes = 8, SlotGetNodeType = 9, SlotKsProperty = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct KspNode
    {
        public Guid Set;
        public uint Id, Flags, NodeId, Reserved;
    }

    private nint _control;
    private readonly uint _node;
    private readonly byte[] _send, _reply, _test;

    private TrackerControl(nint control, uint node, int length)
    {
        _control = control;
        _node = node;
        Length = length;
        _send = new byte[length];
        _reply = new byte[length];
        // Sent before every command, as HTC's runtime and other drivers do.
        _test = new byte[length];
        _test[0] = 0x51;
        _test[1] = 0x52;
        if (length >= 256)
        {
            _test[0xFE] = 0x53;
            _test[0xFF] = 0x54;
        }
    }

    /// <summary>Size of every command and reply buffer: 384, or 64 on some units.</summary>
    public int Length { get; }

    /// <summary>
    /// Finds the extension unit on <paramref name="source"/>; null (with a
    /// log line) if the camera has none.
    /// </summary>
    public static TrackerControl? Open(nint source, Action<string> log)
    {
        if (FindNode(source) is not uint node)
        {
            log("no vendor extension unit on this camera");
            return null;
        }
        if (MediaFoundation.QueryInterface(source, IidKsControl, out nint control) < 0)
        {
            log("camera has no IKsControl");
            return null;
        }
        try
        {
            // A zero-length GET reports the buffer size through ERROR_MORE_DATA.
            int hr = Property(control, node, TypeGet, null, 0, out uint length);
            if (hr != ErrorMoreData || (length != 384 && length != 64))
            {
                log($"unexpected vendor unit (size probe 0x{hr:X8}, {length} bytes)");
                return null;
            }
            log($"vendor channel open (node {node}, {length}-byte commands)");
            var result = new TrackerControl(control, node, (int)length);
            control = 0;
            return result;
        }
        finally
        {
            MediaFoundation.Release(ref control);
        }
    }

    /// <summary>Stream off, sensor setup (turns the IR on), stream on.</summary>
    public void Activate()
    {
        SetStream(false);
        Thread.Sleep(250);
        Command(_test);
        foreach (var (address, value) in SensorSetup)
            SetSensorRegister(address, value);
        SetStream(true);
        Thread.Sleep(250);
    }

    /// <summary>Stream on or off (<c>50 14 00 01</c> / <c>50 14 00 00</c>; no reply expected).</summary>
    public void SetStream(bool on)
    {
        Command(_test);
        Array.Clear(_send);
        _send[0] = TaskSet;
        _send[1] = 0x14;
        _send[3] = on ? (byte)1 : (byte)0;
        Check(Property(_control, _node, TypeSet, _send, Length, out _), "stream command");
    }

    public void Dispose() => MediaFoundation.Release(ref _control);

    /// <summary>
    /// Register write: <c>50 reg 60 addrLen valLen addr[4] 90 01 00 01 value[4]</c>,
    /// both big-endian (the same layout as HTC's register read with 0x51).
    /// </summary>
    private void SetSensorRegister(byte address, byte value)
    {
        var command = new byte[17];
        command[0] = TaskSet;
        command[1] = RegisterSensor;
        command[2] = 0x60;
        command[3] = 1; // address width
        command[4] = 1; // value width
        command[8] = address;
        command[9] = 0x90;
        command[10] = 0x01;
        command[12] = 0x01;
        command[16] = value;
        Command(command);
    }

    /// <summary>
    /// SET a command, then GET until the device echoes it back after 0x56
    /// (0x55 means still busy).
    /// </summary>
    private void Command(byte[] command)
    {
        Array.Clear(_send);
        command.AsSpan(0, Math.Min(command.Length, Length)).CopyTo(_send);
        Check(Property(_control, _node, TypeSet, _send, Length, out _), "vendor command");
        var clock = Stopwatch.StartNew();
        while (true)
        {
            Array.Clear(_reply);
            Check(Property(_control, _node, TypeGet, _reply, Length, out _), "vendor reply");
            if (_reply[0] == ReplyDone)
            {
                if (!_reply.AsSpan(1, 16).SequenceEqual(_send.AsSpan(0, 16)))
                    throw new InvalidOperationException($"vendor command 0x{_send[0]:X2} {_send[1]:X2}: reply does not match");
                return;
            }
            if (_reply[0] != ReplyPending)
                throw new InvalidOperationException($"vendor command 0x{_send[0]:X2} {_send[1]:X2}: bad reply 0x{_reply[0]:X2}");
            if (clock.Elapsed > ReplyTimeout)
                throw new TimeoutException($"vendor command 0x{_send[0]:X2} {_send[1]:X2}: no reply");
            Thread.Sleep(1);
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new COMException($"{what} failed (0x{hr:X8})", hr);
    }

    private static uint? FindNode(nint source)
    {
        if (MediaFoundation.QueryInterface(source, IidKsTopologyInfo, out nint topology) < 0)
            return null;
        try
        {
            uint count = 0;
            if (((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(topology, SlotGetNumNodes))(topology, &count) < 0)
                return null;
            for (uint i = 0; i < count; i++)
            {
                Guid type;
                if (((delegate* unmanaged[Stdcall]<nint, uint, Guid*, int>)Slot(topology, SlotGetNodeType))(topology, i, &type) < 0)
                    return null;
                if (type == NodeTypeDevSpecific)
                    return i;
            }
            return null;
        }
        finally
        {
            MediaFoundation.Release(ref topology);
        }
    }

    private static int Property(nint control, uint node, uint flags, byte[]? data, int length, out uint returned)
    {
        var property = new KspNode { Set = PropertySet, Id = Selector, Flags = flags, NodeId = node };
        uint got = 0;
        int hr;
        fixed (byte* p = data)
            hr = ((delegate* unmanaged[Stdcall]<nint, KspNode*, uint, byte*, uint, uint*, int>)Slot(control, SlotKsProperty))(
                control, &property, (uint)sizeof(KspNode), p, (uint)length, &got);
        returned = got;
        return hr;
    }

    private static nint Slot(nint obj, int slot) => (*(nint**)obj)[slot];
}
