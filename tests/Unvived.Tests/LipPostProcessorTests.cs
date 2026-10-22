using Xunit;

namespace Unvived.Tests;

public class LipPostProcessorTests
{
    private static float[] Raw(float presence = 1f)
    {
        var raw = new float[LipPostProcessor.RawCount];
        raw[0] = presence;
        raw[1] = raw[2] = raw[5] = raw[6] = 0.5f; // at the initial neutral points
        return raw;
    }

    [Fact]
    public void OutputStartsAfter26PresentFrames()
    {
        var post = new LipPostProcessor();
        var raw = Raw();
        raw[3] = 0.8f; // jaw open
        var w = new float[LipPostProcessor.WeightCount];
        for (int frame = 1; frame <= 25; frame++)
        {
            post.Process(raw, w);
            Assert.False(post.Tracking);
            Assert.All(w, v => Assert.Equal(0f, v));
        }
        post.Process(raw, w);
        Assert.True(post.Tracking);
        Assert.True(w[(int)LipShape.JawOpen] > 0f);
    }

    [Fact]
    public void AbsentFaceGivesZeros()
    {
        var post = new LipPostProcessor();
        var w = new float[LipPostProcessor.WeightCount];
        post.Process(Raw(presence: 0.1f), w);
        Assert.False(post.Tracking);
        Assert.All(w, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void SidewaysValuesSplitAroundNeutral()
    {
        var post = new LipPostProcessor();
        var raw = Raw();
        raw[1] = 0.25f; // below the 0.5 neutral: JawRight
        var w = new float[LipPostProcessor.WeightCount];
        for (int frame = 0; frame < 26; frame++)
            post.Process(raw, w);
        Assert.True(w[(int)LipShape.JawRight] > 0f);
        Assert.Equal(0f, w[(int)LipShape.JawLeft]);
    }

    [Fact]
    public void SmoothingConvergesToSteadyInput()
    {
        var post = new LipPostProcessor();
        var raw = Raw();
        raw[3] = 0.6f;
        var w = new float[LipPostProcessor.WeightCount];
        for (int frame = 0; frame < 100; frame++)
            post.Process(raw, w);
        // 0.6 normalised by its running max (0.6) is 1.
        Assert.Equal(1f, w[(int)LipShape.JawOpen], 3);
    }
}
