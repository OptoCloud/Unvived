using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Unvived.Tests;

internal static class TestFrames
{
    /// <summary>Same generator as synthetic_frame() in tests/reference/make_fixtures.py.</summary>
    public static byte[] Synthetic()
    {
        var frame = new byte[LipImage.FrameBytes];
        uint state = 12345;
        for (int i = 0; i < frame.Length; i++)
        {
            state = (1103515245 * state + 12345) & 0x7FFFFFFF;
            int noise = (int)(state >> 16) & 0x3F;
            int y = i / LipImage.FrameWidth, x = i % LipImage.FrameWidth;
            frame[i] = (byte)((x / 4 + y / 3 + noise) & 0xFF);
        }
        return frame;
    }

    public static string FixturePath(string name, [CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "fixtures", name);

    public static JsonDocument? LoadFixture(string name)
    {
        string path = FixturePath(name);
        return File.Exists(path) ? JsonDocument.Parse(File.ReadAllText(path)) : null;
    }
}
