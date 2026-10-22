namespace Unvived;

/// <summary>
/// Turns the lip model's 30 raw outputs into SRanipal's 60-float
/// <c>blend_shape_weight</c> array (37 shapes used), with the same presence
/// gating, adaptive neutral points, running-max normalisation, tongue rules
/// and smoothing as LipPredictionModule_v2. See docs/lip-pipeline.md.
/// </summary>
public sealed class LipPostProcessor
{
    public const int RawCount = 30;
    public const int WeightCount = 60;

    /// <summary>Raw output 0: is a face in view.</summary>
    private const float PresenceThreshold = 0.4f;
    private const int AbsentFramesBeforeReset = 25;
    private const int PresentFramesBeforeOutput = 26;
    private const int PresentFramesBeforeNeutralTracking = 150;
    private const int NeutralTrackingInterval = 13;

    private const int NeutralCount = 4;
    private const float NeutralNoise = 20.5f;
    private const float NeutralProcessNoise = 0.001f;

    private const int NormalisedCount = 32;
    private const int LateShapesStart = 27;
    private const float LateShapesMinMax = 0.502f;

    private const int SmoothedCount = 33;
    private const float SmoothNoise = 0.5f;
    private const float SmoothProcessNoise = 0.25f;

    // Raw outputs that move around a learned neutral value, by neutral slot:
    // 0 jaw sideways, 1 upper lip sideways, 2 lower lip sideways, 3 jaw forward.
    private static readonly int[] NeutralSources = [1, 5, 6, 2];

    private int _absentFrames;
    private int _presentFrames;
    private bool _lastAbsent;
    private int _neutralTick;

    private readonly float[] _neutralInput = new float[NeutralCount];
    private readonly float[] _neutral = new float[NeutralCount];
    private readonly float[] _neutralEstimate = new float[NeutralCount];
    private readonly float[] _neutralVariance = new float[NeutralCount];

    private readonly float[] _max = new float[NormalisedCount];

    private readonly float[] _smoothed = new float[SmoothedCount];
    private readonly float[] _smoothVariance = new float[SmoothedCount];

    public LipPostProcessor() => ResetAdaptation();

    /// <summary>True while the model reports a face and output has started.</summary>
    public bool Tracking { get; private set; }

    public void Process(ReadOnlySpan<float> raw, Span<float> weights)
    {
        if (raw.Length < RawCount)
            throw new ArgumentException($"need {RawCount} raw outputs", nameof(raw));
        if (weights.Length < WeightCount)
            throw new ArgumentException($"need room for {WeightCount} weights", nameof(weights));
        weights.Slice(0, WeightCount).Clear();
        Tracking = false;

        if (raw[0] < PresenceThreshold)
        {
            if (_lastAbsent)
                _absentFrames++;
            _lastAbsent = true;
            _presentFrames = 0;
            if (_absentFrames > AbsentFramesBeforeReset)
            {
                weights[37] = raw[0];
                ResetAdaptation();
            }
            return;
        }

        if (!_lastAbsent)
            _presentFrames++;
        _lastAbsent = false;
        _absentFrames = 0;
        if (_presentFrames < PresentFramesBeforeOutput)
            return;
        Tracking = true;

        for (int i = 0; i < NeutralCount; i++)
            _neutralInput[i] = raw[NeutralSources[i]];
        if (_presentFrames > PresentFramesBeforeNeutralTracking && ++_neutralTick >= NeutralTrackingInterval)
        {
            UpdateNeutrals();
            _neutralTick = 0;
        }

        SplitAroundNeutral(raw[1], _neutral[0], out weights[(int)LipShape.JawRight], out weights[(int)LipShape.JawLeft]);
        weights[(int)LipShape.JawForward] = MathF.Max(raw[2] - _neutral[3], 0f);
        weights[(int)LipShape.JawOpen] = raw[3];
        weights[(int)LipShape.MouthApeShape] = raw[4];
        SplitAroundNeutral(raw[5], _neutral[1],
            out weights[(int)LipShape.MouthUpperRight], out weights[(int)LipShape.MouthUpperLeft]);
        SplitAroundNeutral(raw[6], _neutral[2],
            out weights[(int)LipShape.MouthLowerRight], out weights[(int)LipShape.MouthLowerLeft]);
        raw.Slice(7, 16).CopyTo(weights.Slice(9));   // MouthUpperOverturn .. MouthLowerDownLeft
        raw.Slice(23, 7).CopyTo(weights.Slice(25));  // MouthLowerOverlay .. TongueRoll

        Normalise(weights);
        weights[41] = weights[(int)LipShape.TongueLongStep1];
        ApplyTongueRules(weights);
        Smooth(weights);

        if (weights[(int)LipShape.TongueLongStep2] > 0.05f)
            weights[(int)LipShape.TongueLongStep1] = 1f;
        weights[(int)LipShape.TongueUpRightMorph] = weights[(int)LipShape.TongueRight] * weights[(int)LipShape.TongueUp];
        weights[(int)LipShape.TongueUpLeftMorph] = weights[(int)LipShape.TongueLeft] * weights[(int)LipShape.TongueUp];
        weights[(int)LipShape.TongueDownRightMorph] = weights[(int)LipShape.TongueRight] * weights[(int)LipShape.TongueDown];
        weights[(int)LipShape.TongueDownLeftMorph] = weights[(int)LipShape.TongueLeft] * weights[(int)LipShape.TongueDown];
    }

    /// <summary>
    /// Maps a value with a neutral point to two one-sided weights: below the
    /// neutral scales to <paramref name="below"/>, above to <paramref name="above"/>.
    /// </summary>
    private static void SplitAroundNeutral(float value, float neutral, out float below, out float above)
    {
        if (neutral <= value)
        {
            below = 0f;
            above = (value - neutral) / (1f - neutral);
        }
        else
        {
            above = 0f;
            below = (neutral - value) / neutral;
        }
    }

    /// <summary>
    /// Pulls each neutral point toward the current value when the face looks
    /// relaxed on that axis (1-D Kalman step with a slow process).
    /// </summary>
    private void UpdateNeutrals()
    {
        for (int i = 0; i < NeutralCount; i++)
        {
            float value = _neutralInput[i];
            float distance = MathF.Abs(value - _neutral[i]);
            bool relaxed = distance < 0.2f || (i == 3 && distance < 0.55f && value < 0.9f);
            if (!relaxed)
                continue;
            _neutral[i] = KalmanStep(ref _neutralEstimate[i], ref _neutralVariance[i], value,
                NeutralProcessNoise, NeutralNoise);
        }
    }

    /// <summary>Divides each of the first 32 weights by the largest value it has reached.</summary>
    private void Normalise(Span<float> weights)
    {
        for (int i = 0; i < NormalisedCount; i++)
        {
            if (_max[i] < weights[i])
                _max[i] = weights[i];
            if (i < LateShapesStart || _max[i] >= LateShapesMinMax)
                weights[i] /= _max[i];
        }
    }

    private static void ApplyTongueRules(Span<float> w)
    {
        ref float step1 = ref w[(int)LipShape.TongueLongStep1];
        ref float left = ref w[(int)LipShape.TongueLeft];
        ref float right = ref w[(int)LipShape.TongueRight];
        ref float up = ref w[(int)LipShape.TongueUp];
        ref float down = ref w[(int)LipShape.TongueDown];
        ref float step2 = ref w[(int)LipShape.TongueLongStep2];
        float jawOpen = w[(int)LipShape.JawOpen];

        // A rolled tongue is mostly out and has no direction.
        if (w[(int)LipShape.TongueRoll] > 0.4f)
        {
            step1 *= 1.5f;
            left = right = up = down = 0f;
        }
        // Keep only the stronger of each opposing pair (ties keep up / right).
        if (down <= up) down = 0f; else up = 0f;
        if (left <= right) left = 0f; else right = 0f;

        if (down > 0.1f)
        {
            step2 = down + 0.2f + 0.2f * left + 0.2f * right;
            step1 = 1f;
        }
        else if (up > 0.2f && jawOpen < 0.6f && step1 > 0.2f)
        {
            step2 = up + 0.12f + 0.2f * left + 0.2f * right;
            step1 = 1f;
        }
        else if (left > 0.2f && jawOpen < 0.6f && step1 > 0.3f)
        {
            step2 = 0.2f * up + left + 0.12f + 0.2f * down;
            step1 = 1f;
        }
        else if (right > 0.2f && jawOpen < 0.6f && step1 > 0.3f)
        {
            step2 = 0.2f * up + right + 0.12f + 0.2f * down;
            step1 = 1f;
        }
        else if (step1 >= 0.4f)
        {
            step2 = (step1 - 0.4f) / 0.6f;
            step1 = 1f;
        }
        else
        {
            step2 = 0f;
            step1 /= 0.4f;
        }

        // Mouth (nearly) closed: no tongue at all.
        if (jawOpen < 0.05f)
            w.Slice((int)LipShape.TongueLongStep1, 7).Clear();
    }

    private void Smooth(Span<float> weights)
    {
        for (int i = 0; i < SmoothedCount; i++)
        {
            weights[i] = KalmanStep(ref _smoothed[i], ref _smoothVariance[i], weights[i],
                SmoothProcessNoise, SmoothNoise);
        }
    }

    private static float KalmanStep(ref float estimate, ref float variance, float measurement,
        float processNoise, float measurementNoise)
    {
        float predicted = variance + processNoise;
        float gain = predicted / (predicted + measurementNoise);
        estimate += gain * (measurement - estimate);
        variance = (1f - gain) * predicted;
        return estimate;
    }

    private void ResetAdaptation()
    {
        Array.Fill(_neutral, 0.5f);
        Array.Fill(_neutralEstimate, 0.5f);
        Array.Fill(_neutralVariance, 0.5f);
        Array.Fill(_max, 0.5f);
        _max[(int)LipShape.JawForward] = 0.7f;
        Array.Clear(_smoothed);
        Array.Clear(_smoothVariance);
    }
}
