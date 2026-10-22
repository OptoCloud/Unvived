using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MF = Unvived.Capture.MediaFoundation;

namespace Unvived.Capture;

/// <summary>
/// The VIVE Facial Tracker as a plain UVC camera. Its only mode is YUY2
/// 400x400, but the bytes are an 800x400 8-bit image: the left and right IR
/// views side by side. Frames are read on a dedicated thread; the latest
/// one is handed out by <see cref="TryGetFrame"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FacialTrackerCamera : IDisposable
{
    public const int Width = 400;
    public const int Height = 400;

    private static readonly (string Vid, string Pid)[] UsbIds = [("vid_0bb4", "pid_0321"), ("vid_05a9", "pid_0581")];
    private static readonly string[] FriendlyNames = ["HTC Multimedia Camera", "USB Camera-OV580"];

    private readonly Action<string> _log;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly object _lock = new();
    private readonly object _sourceLock = new();
    private byte[] _latest = new byte[LipImage.FrameBytes];
    private byte[] _back = new byte[LipImage.FrameBytes];
    private long _frameNumber;
    private long _deliveredFrame;
    private Exception? _openError;
    private volatile bool _stopping;
    private nint _source;
    private TrackerControl? _control;

    private FacialTrackerCamera(Action<string> log)
    {
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "Facial Tracker capture" };
    }

    /// <summary>Name of the opened device.</summary>
    public string DeviceName { get; private set; } = "";

    /// <summary>The capture thread hit a read error or end of stream.</summary>
    public bool Faulted { get; private set; }

    public long FramesReceived => Interlocked.Read(ref _frameNumber);

    public DateTime LastFrameUtc { get; private set; }

    /// <summary>Video capture devices as (friendly name, symbolic link).</summary>
    public static IReadOnlyList<(string Name, string Link)> ListDevices()
    {
        var result = new List<(string, string)>();
        RunOnMtaThread(() =>
        {
            foreach (nint a in MF.EnumerateVideoDevices())
            {
                nint activate = a;
                result.Add((MF.GetString(activate, MF.DevSourceFriendlyName), MF.GetString(activate, MF.DevSourceSymbolicLink)));
                MF.Release(ref activate);
            }
        });
        return result;
    }

    /// <summary>Finds and starts the tracker; throws if it is missing or cannot stream.</summary>
    public static FacialTrackerCamera Open(Action<string>? log = null)
    {
        var camera = new FacialTrackerCamera(log ?? (_ => { }));
        camera._thread.Start();
        camera._ready.Wait();
        if (camera._openError is { } error)
        {
            camera.Dispose();
            throw error;
        }
        return camera;
    }

    /// <summary>
    /// Copies the newest frame not yet returned into <paramref name="dst"/>
    /// (800x400 bytes), waiting up to <paramref name="timeoutMs"/> for one.
    /// </summary>
    public bool TryGetFrame(Span<byte> dst, int timeoutMs)
    {
        lock (_lock)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (_frameNumber == _deliveredFrame && !Faulted && !_stopping)
            {
                long left = deadline - Environment.TickCount64;
                if (left <= 0 || !Monitor.Wait(_lock, (int)left))
                    return false;
            }
            if (_frameNumber == _deliveredFrame)
                return false;
            _latest.CopyTo(dst);
            _deliveredFrame = _frameNumber;
            return true;
        }
    }

    public void Dispose()
    {
        _stopping = true;
        lock (_lock)
            Monitor.PulseAll(_lock);
        // While frames flow, the pending ReadSample returns within a frame
        // and the loop sees _stopping. Shutting the source down under a
        // pending ReadSample can hang it, so that is only the fallback for
        // a camera that has stopped sending.
        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromMilliseconds(500)))
        {
            lock (_sourceLock)
            {
                if (_source != 0)
                    MF.ShutdownSource(_source);
            }
            if (!_thread.Join(TimeSpan.FromSeconds(3)))
                _log("capture thread did not stop within 3.5 s");
        }
        _ready.Dispose();
    }

    private void Run()
    {
        nint reader = 0;
        bool started = false;
        try
        {
            MF.Startup();
            started = true;
            reader = OpenReader();
        }
        catch (Exception e)
        {
            _openError = e;
        }
        _ready.Set();

        Thread? starter = null;
        try
        {
            if (_openError == null)
            {
                if (_control != null)
                {
                    starter = new Thread(SendStart) { IsBackground = true, Name = "Facial Tracker start" };
                    starter.Start();
                }
                ReadLoop(reader);
            }
        }
        catch (Exception e)
        {
            _log($"capture stopped: {e.Message}");
            Faulted = true;
        }
        finally
        {
            starter?.Join(TimeSpan.FromSeconds(2));
            try
            {
                _control?.SetStream(false); // also turns the IR off
            }
            catch (Exception e) when (e is COMException or InvalidOperationException or TimeoutException)
            {
                _log($"stream off failed: {e.Message}");
            }
            _control?.Dispose();
            _control = null;
            MF.Release(ref reader);
            lock (_sourceLock)
            {
                if (_source != 0)
                {
                    MF.ShutdownSource(_source);
                    MF.Release(ref _source);
                }
            }
            if (started)
                MF.Shutdown();
            lock (_lock)
                Monitor.PulseAll(_lock);
        }
    }

    private nint OpenReader()
    {
        nint[] devices = MF.EnumerateVideoDevices();
        nint chosen = 0;
        try
        {
            int best = int.MaxValue;
            foreach (nint activate in devices)
            {
                string name = MF.GetString(activate, MF.DevSourceFriendlyName);
                string link = MF.GetString(activate, MF.DevSourceSymbolicLink);
                int rank = Rank(name, link);
                if (rank < best)
                {
                    best = rank;
                    chosen = activate;
                    DeviceName = name;
                }
            }
            if (chosen == 0)
                throw new InvalidOperationException("VIVE Facial Tracker not found (no camera with USB ID 0BB4:0321)");

            nint source = MF.ActivateMediaSource(chosen);
            lock (_sourceLock)
                _source = source;
            _log($"opened {DeviceName}");
        }
        finally
        {
            for (int i = 0; i < devices.Length; i++)
                MF.Release(ref devices[i]);
        }

        _control = TrackerControl.Open(_source, _log);
        try
        {
            _control?.Activate();
            if (_control != null)
                _log("tracker activated (sensor setup, IR on)");
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or TimeoutException)
        {
            _log($"tracker activation failed: {e.Message}");
        }

        nint reader = MF.CreateSourceReader(_source);
        try
        {
            SelectFormat(reader);
            return reader;
        }
        catch
        {
            MF.Release(ref reader);
            throw;
        }
    }

    /// <summary>Lower is better; int.MaxValue means not the tracker.</summary>
    private static int Rank(string name, string link)
    {
        for (int i = 0; i < UsbIds.Length; i++)
        {
            if (link.Contains(UsbIds[i].Vid, StringComparison.OrdinalIgnoreCase)
                && link.Contains(UsbIds[i].Pid, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        int n = Array.FindIndex(FriendlyNames, f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
        return n >= 0 ? UsbIds.Length + n : int.MaxValue;
    }

    private void SelectFormat(nint reader)
    {
        var seen = new List<string>();
        for (uint i = 0; ; i++)
        {
            nint type = MF.GetNativeMediaType(reader, i);
            if (type == 0)
                break;
            try
            {
                Guid subtype = MF.GetGuid(type, MF.MtSubtype);
                ulong size = MF.GetUInt64(type, MF.MtFrameSize);
                uint width = (uint)(size >> 32), height = (uint)size;
                seen.Add($"{FourCc(subtype)} {width}x{height}");
                if (subtype == MF.VideoFormatYuy2 && width == Width && height == Height)
                {
                    MF.SetCurrentMediaType(reader, type);
                    return;
                }
            }
            finally
            {
                MF.Release(ref type);
            }
        }
        throw new InvalidOperationException(
            $"{DeviceName} has no YUY2 {Width}x{Height} mode (has: {string.Join(", ", seen)})");
    }

    private static string FourCc(Guid subtype)
    {
        uint code = BitConverter.ToUInt32(subtype.ToByteArray(), 0);
        Span<char> c = stackalloc char[4];
        for (int i = 0; i < 4; i++)
            c[i] = (char)((code >> (8 * i)) & 0xFF);
        return new string(c);
    }

    /// <summary>
    /// Re-sends stream-on every second while no frame has arrived (HTC sends
    /// it once video is running; activation already sent it before).
    /// </summary>
    private void SendStart()
    {
        for (int attempt = 1; attempt <= 5 && !_stopping; attempt++)
        {
            Thread.Sleep(1000);
            if (_stopping || FramesReceived != 0)
                break;
            try
            {
                _control!.SetStream(true);
                _log($"no frames yet; sent stream-on again (attempt {attempt})");
            }
            catch (Exception e) when (e is COMException or InvalidOperationException or TimeoutException)
            {
                _log($"stream-on failed: {e.Message}");
            }
        }
    }

    private void ReadLoop(nint reader)
    {
        while (!_stopping)
        {
            int hr = MF.ReadSample(reader, out uint flags, out _, out nint sample);
            if (_stopping)
            {
                MF.Release(ref sample);
                return;
            }
            MF.Check(hr, "IMFSourceReader::ReadSample");
            if ((flags & (MF.ReaderFlagError | MF.ReaderFlagEndOfStream)) != 0)
            {
                MF.Release(ref sample);
                throw new InvalidOperationException($"stream ended (flags 0x{flags:X})");
            }
            if (sample == 0)
                continue;

            int length;
            try
            {
                length = MF.CopySample(sample, _back);
            }
            finally
            {
                MF.Release(ref sample);
            }
            if (length < LipImage.FrameBytes)
            {
                _log($"short frame: {length} bytes");
                continue;
            }

            lock (_lock)
            {
                (_latest, _back) = (_back, _latest);
                _frameNumber++;
                LastFrameUtc = DateTime.UtcNow;
                Monitor.PulseAll(_lock);
            }
        }
    }

    private static void RunOnMtaThread(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                MF.Startup();
                try { action(); }
                finally { MF.Shutdown(); }
            }
            catch (Exception e)
            {
                error = e;
            }
        });
        thread.Start();
        thread.Join();
        if (error != null)
            throw error;
    }
}
