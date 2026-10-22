using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VRCFaceTracking;
using VRCFaceTracking.Core.Library;
using Unvived.Capture;

namespace Unvived.FacialTracker;

/// <summary>
/// VRCFaceTracking module for the VIVE Facial Tracker that needs no
/// SRanipal runtime: capture, SRanipal's lip model and its post-processing
/// all run in this process.
/// </summary>
public sealed class FacialTrackerModule : ExtTrackingModule
{
    private const int FrameTimeoutMs = 100;
    private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxReopenDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HealthLogInterval = TimeSpan.FromSeconds(30);

    private readonly byte[] _frame = new byte[LipImage.FrameBytes];
    private readonly float[] _weights = new float[LipPostProcessor.WeightCount];
    private LipPipeline? _pipeline;
    private string? _weightsPath;
    private FacialTrackerCamera? _camera;
    private DateTime _lastFrameUtc;
    private DateTime _nextReopenUtc;
    private TimeSpan _reopenDelay = TimeSpan.FromMilliseconds(500);
    private readonly Stopwatch _health = Stopwatch.StartNew();
    private long _processed;
    private double _processMs;

    public override (bool SupportsEye, bool SupportsExpression) Supported => (false, true);

    public override (bool eyeSuccess, bool expressionSuccess) Initialize(bool eyeAvailable, bool expressionAvailable)
    {
        if (!expressionAvailable)
            return (false, false);

        string moduleDir = Path.GetDirectoryName(typeof(FacialTrackerModule).Assembly.Location) ?? ".";
        string? weights = ModelFiles.FindLipParams(ModelFiles.CacheDirectory, Info, moduleDir);
        if (weights == null)
        {
            Logger.LogError("Lip model {name} not found. Install \"VIVE Console for SteamVR\" from Steam once "
                + "(it is copied into {dir} on the next start, then VIVE Console can be uninstalled), "
                + "or copy the file there from an SRanipal install's model\\LPrediction folder.",
                ModelFiles.LipParamsName, ModelFiles.CacheDirectory);
            return (false, false);
        }

        try
        {
            _weightsPath = weights;
            _pipeline = LipPipeline.Load(weights, useGpu: true, Info);
            _camera = FacialTrackerCamera.Open(Info);
        }
        catch (Exception e)
        {
            Logger.LogError("VIVE Facial Tracker failed to start: {error}", e.Message);
            _camera?.Dispose();
            _camera = null;
            _pipeline?.Dispose();
            _pipeline = null;
            return (false, false);
        }

        _lastFrameUtc = DateTime.UtcNow;
        ModuleInformation = new ModuleMetadata { Name = "VIVE Facial Tracker" };
        Logger.LogInformation("Tracking with {device}, model on {model}", _camera.DeviceName, _pipeline.Device);
        return (false, true);
    }

    public override void Update()
    {
        if (Status != ModuleState.Active || _pipeline == null)
        {
            Thread.Sleep(10);
            return;
        }

        if (_camera == null || _camera.Faulted || DateTime.UtcNow - _lastFrameUtc > StallLimit)
        {
            Reopen();
            return;
        }

        if (!_camera.TryGetFrame(_frame, FrameTimeoutMs))
            return;
        _lastFrameUtc = DateTime.UtcNow;
        _reopenDelay = TimeSpan.FromMilliseconds(500);

        long start = Stopwatch.GetTimestamp();
        try
        {
            _pipeline.Process(_frame, _weights);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException && _weightsPath != null)
        {
            // GPU reset or driver update: carry on with the CPU.
            Logger.LogWarning("Model failed on {device} ({error}); switching to the CPU", _pipeline.Device, e.Message);
            _pipeline.Dispose();
            _pipeline = LipPipeline.Load(_weightsPath, useGpu: false);
            return;
        }
        _processMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _processed++;
        ExpressionMapping.Apply(_weights, UnifiedTracking.Data.Shapes);
        LogHealth();
    }

    public override void Teardown()
    {
        _camera?.Dispose();
        _camera = null;
        _pipeline?.Dispose();
        _pipeline = null;
    }

    /// <summary>Restarts capture after a stall or error, backing off between attempts.</summary>
    private void Reopen()
    {
        if (DateTime.UtcNow < _nextReopenUtc)
        {
            Thread.Sleep(50);
            return;
        }
        Logger.LogWarning("Facial Tracker {reason}; reopening", _camera?.Faulted == true ? "stopped" : "stalled");
        _camera?.Dispose();
        _camera = null;
        try
        {
            _camera = FacialTrackerCamera.Open(Info);
            _lastFrameUtc = DateTime.UtcNow;
        }
        catch (Exception e)
        {
            Logger.LogWarning("Reopen failed: {error}", e.Message);
        }
        _nextReopenUtc = DateTime.UtcNow + _reopenDelay;
        _reopenDelay = TimeSpan.FromTicks(Math.Min(_reopenDelay.Ticks * 2, MaxReopenDelay.Ticks));
    }

    private void LogHealth()
    {
        if (_health.Elapsed < HealthLogInterval)
            return;
        double seconds = _health.Elapsed.TotalSeconds;
        Logger.LogInformation("{fps:F1} fps, {ms:F1} ms/frame, face {tracking}",
            _processed / seconds, _processMs / Math.Max(1, _processed), _pipeline!.Tracking ? "in view" : "not in view");
        _processed = 0;
        _processMs = 0;
        _health.Restart();
    }

    private void Info(string message) => Logger.LogInformation("{message}", message);
}
