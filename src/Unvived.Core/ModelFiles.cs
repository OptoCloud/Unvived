using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Unvived;

/// <summary>
/// Finds SRanipal's lip model weights. They are HTC's and are never shipped
/// with this project. HTC distributes SRanipal through the free Steam app
/// "VIVE Console for SteamVR"; the first run copies the weights from there
/// (or an older standalone install) into a cache directory, after which
/// VIVE Console can be uninstalled.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class ModelFiles
{
    public const string LipParamsName = "lip_opencl.params";

    /// <summary>SHA-256 of the lip weights in SRanipal 1.3.1.1 to 1.3.6.5 (see docs/model-sources.md).</summary>
    public const string LipParamsSha256 = "45d8f94445c8dc727e13fff30048453894e1b931c2affe74d1b75ff214755b28";
    public const int ViveConsoleAppId = 1635730;
    private const string DefaultSRanipalDir = @"C:\Program Files\VIVE\SRanipal";
    private static readonly string[] ModelSubPath = ["model", "LPrediction", LipParamsName];

    /// <summary>
    /// Per-user home for the weights. Not the module folder: VRCFT deletes
    /// that whenever the module is reinstalled.
    /// </summary>
    public static string CacheDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Unvived", "models");

    /// <summary>
    /// The cached weights, else a copy made into <paramref name="cacheDirectory"/>
    /// from <paramref name="alsoLookIn"/> (e.g. an older module folder) or an
    /// SRanipal / VIVE Console install.
    /// </summary>
    public static string? FindLipParams(string cacheDirectory, Action<string>? log = null, params string[] alsoLookIn)
    {
        string cached = Path.Combine(cacheDirectory, LipParamsName);
        if (File.Exists(cached))
            return cached;

        string? source = alsoLookIn.Select(d => Path.Combine(d, LipParamsName)).FirstOrDefault(File.Exists)
            ?? FindInstalled();
        if (source == null)
            return null;
        if (!IsKnownLipParams(source))
            log?.Invoke($"{source} is not the known SRanipal 1.3.x lip model (SHA-256 differs); using it anyway");
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            File.Copy(source, cached + ".tmp", overwrite: true);
            File.Move(cached + ".tmp", cached, overwrite: true);
            log?.Invoke($"copied {source} to {cached}");
            return cached;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"could not copy {source}: {e.Message}; using it in place");
            return source;
        }
    }

    public static bool IsKnownLipParams(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream)) == LipParamsSha256;
    }

    /// <summary>The weights inside an SRanipal or VIVE Console install, if any.</summary>
    public static string? FindInstalled()
    {
        foreach (string dir in SRanipalDirectories())
        {
            string path = Path.Combine([dir, .. ModelSubPath]);
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    /// <summary>
    /// Asks Steam to install VIVE Console (Steam shows its own confirmation)
    /// and waits for the weights to appear, then copies them to the cache.
    /// </summary>
    public static string? FetchFromSteam(string cacheDirectory, TimeSpan timeout, Action<string>? log = null)
    {
        if (FindLipParams(cacheDirectory, log) is { } found)
            return found;
        log?.Invoke($"asking Steam to install VIVE Console for SteamVR (app {ViveConsoleAppId}); confirm it in Steam");
        Process.Start(new ProcessStartInfo($"steam://install/{ViveConsoleAppId}") { UseShellExecute = true });
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            Thread.Sleep(2000);
            if (FindInstalled() != null)
            {
                Thread.Sleep(2000); // let Steam finish writing the file
                return FindLipParams(cacheDirectory, log);
            }
        }
        return null;
    }

    private static IEnumerable<string> SRanipalDirectories()
    {
        string? runtime = ReadRegistry(Registry.LocalMachine, @"SOFTWARE\VIVE\SRWorks\SRanipal", "ModuleFileName");
        if (Path.GetDirectoryName(runtime) is { Length: > 0 } fromRegistry)
            yield return fromRegistry;
        yield return DefaultSRanipalDir;
        foreach (string library in SteamLibraries())
            yield return Path.Combine(library, "steamapps", "common", "VIVEDriver", "App", "SRanipal");
    }

    private static IEnumerable<string> SteamLibraries()
    {
        string? steam = ReadRegistry(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath")
            ?? ReadRegistry(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (steam == null)
            yield break;
        steam = Path.GetFullPath(steam);
        yield return steam;

        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        string text;
        try
        {
            text = File.ReadAllText(vdf);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (Match m in LibraryPath().Matches(text))
        {
            string path = Path.GetFullPath(m.Groups[1].Value.Replace(@"\\", @"\"));
            if (!string.Equals(path, steam, StringComparison.OrdinalIgnoreCase))
                yield return path;
        }
    }

    private static string? ReadRegistry(RegistryKey root, string key, string name)
    {
        try
        {
            using var k = root.OpenSubKey(key);
            return k?.GetValue(name) as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex LibraryPath();
}
