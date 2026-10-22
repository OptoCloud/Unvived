using System.Diagnostics;
using Unvived;
using Unvived.Capture;

// unvived devices                   list video capture devices
// unvived fetch-model [dir]         get the lip weights from VIVE Console (installs it through Steam if needed)
// unvived bench [params]            time the pipeline on the GPU and the CPU
// unvived run [--cpu] [seconds]     live capture, print fps and the strongest shapes
// unvived grab <out.pgm> [count]    save captured frames as 800x400 PGM images
bool cpu = args.Contains("--cpu");
args = args.Where(a => a != "--cpu").ToArray();
string command = args.Length > 0 ? args[0] : "help";
try
{
    return command switch
    {
        "devices" => Devices(),
        "fetch-model" => FetchModel(args.Length > 1 ? args[1] : ModelFiles.CacheDirectory),
        "bench" => Bench(args.Length > 1 ? args[1] : null),
        "run" => Run(!cpu, args.Length > 1 ? int.Parse(args[1]) : 30),
        "grab" when args.Length > 1 => Grab(args[1], args.Length > 2 ? int.Parse(args[2]) : 1),
        _ => Help(),
    };
}
catch (Exception e)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}

static int Help()
{
    Console.WriteLine("usage: unvived devices | fetch-model [dir] | bench [params] | run [--cpu] [seconds] | grab <out.pgm> [count]");
    return 2;
}

static string ParamsPath(string? given)
{
    if (given != null)
        return given;
    return ModelFiles.FindLipParams(ModelFiles.CacheDirectory, Console.WriteLine, AppContext.BaseDirectory)
        ?? throw new FileNotFoundException($"{ModelFiles.LipParamsName} not found; run 'unvived fetch-model' or pass its path");
}

static int Devices()
{
    foreach (var (name, link) in FacialTrackerCamera.ListDevices())
        Console.WriteLine($"{name}\n    {link}");
    return 0;
}

static int FetchModel(string dir)
{
    string? path = ModelFiles.FetchFromSteam(dir, TimeSpan.FromMinutes(15), Console.WriteLine);
    if (path == null)
    {
        Console.Error.WriteLine("weights did not appear within 15 minutes");
        return 1;
    }
    Console.WriteLine($"weights at {path}; VIVE Console can be uninstalled now");
    return 0;
}

static int Bench(string? paramsPath)
{
    string path = ParamsPath(paramsPath);
    byte[] frame = new byte[LipImage.FrameBytes];
    new Random(1).NextBytes(frame);
    foreach (bool gpu in new[] { true, false })
    {
        using var pipeline = LipPipeline.Load(path, gpu, Console.WriteLine);
        var weights = new float[LipPostProcessor.WeightCount];
        for (int i = 0; i < 20; i++)
            pipeline.Process(frame, weights);

        const int runs = 300;
        var process = Process.GetCurrentProcess();
        TimeSpan cpuBefore = process.TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        for (int i = 0; i < runs; i++)
            pipeline.Process(frame, weights);
        double ms = wall.Elapsed.TotalMilliseconds / runs;
        process.Refresh();
        double cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds / runs;
        Console.WriteLine($"{pipeline.Device,-18} {ms,6:F2} ms/frame wall, {cpuMs,6:F2} ms CPU/frame "
            + $"({cpuMs * 60 / 1000:P0} of a core at 60 fps)");
    }
    return 0;
}

static int Run(bool gpu, int seconds)
{
    using var pipeline = LipPipeline.Load(ParamsPath(null), gpu, Console.WriteLine);
    Console.WriteLine($"model on {pipeline.Device}");
    using var camera = FacialTrackerCamera.Open(Console.WriteLine);
    var frame = new byte[LipImage.FrameBytes];
    var weights = new float[LipPostProcessor.WeightCount];
    var total = Stopwatch.StartNew();
    var tick = Stopwatch.StartNew();
    int frames = 0;
    while (total.Elapsed.TotalSeconds < seconds)
    {
        if (!camera.TryGetFrame(frame, 1000))
        {
            Console.WriteLine(camera.Faulted ? "camera stopped" : "no frame for 1 s");
            if (camera.Faulted)
                return 1;
            continue;
        }
        pipeline.Process(frame, weights);
        frames++;
        if (tick.ElapsedMilliseconds >= 500)
        {
            var top = Enumerable.Range(0, 37).OrderByDescending(i => weights[i]).Take(4)
                .Where(i => weights[i] > 0.05f).Select(i => $"{(LipShape)i}={weights[i]:F2}");
            Console.WriteLine($"{frames * 1000.0 / tick.ElapsedMilliseconds,5:F1} fps  presence={pipeline.RawOutputs[0]:F2}  "
                + (pipeline.Tracking ? string.Join("  ", top) : "(no face)"));
            frames = 0;
            tick.Restart();
        }
    }
    return 0;
}

static int Grab(string path, int count)
{
    using var camera = FacialTrackerCamera.Open(Console.WriteLine);
    var frame = new byte[LipImage.FrameBytes];
    for (int i = 0; i < count; i++)
    {
        if (!camera.TryGetFrame(frame, 3000))
            throw new TimeoutException("no frame within 3 s");
        string file = count == 1 ? path : Path.ChangeExtension(path, null) + $"_{i:D3}.pgm";
        using var stream = File.Create(file);
        stream.Write(System.Text.Encoding.ASCII.GetBytes($"P5\n{LipImage.FrameWidth} {LipImage.FrameHeight}\n255\n"));
        stream.Write(frame);
        Console.WriteLine($"wrote {file}");
    }
    return 0;
}
