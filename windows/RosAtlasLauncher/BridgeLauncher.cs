using System.Diagnostics;

namespace RosAtlasLauncher;

/// <summary>
/// Starts the existing RosAtlasBridge executable rather than duplicating any of its logic.
/// Program.cs already accepts "--robot ws://host:port", so the only thing we add is pointing it
/// at the IP the user gave us.
/// </summary>
public static class BridgeLauncher
{
    public static Process Start(LauncherConfig config, Action<string> log)
    {
        var path = Resolve(config);
        var robotUrl = $"ws://{config.Host}:{config.FoxglovePort}";
        log($"Starting RosAtlasBridge against {robotUrl} ...");

        var info = new ProcessStartInfo
        {
            FileName = path,
            WorkingDirectory = Path.GetDirectoryName(path)!,
            UseShellExecute = false,

            // Closing this stream is the stop request; see RosAtlasBridge's Program.cs.
            RedirectStandardInput = true,
        };
        info.ArgumentList.Add("--robot");
        info.ArgumentList.Add(robotUrl);
        info.ArgumentList.Add("--stop-on-stdin-close");

        return Process.Start(info) ?? throw new InvalidOperationException($"could not start '{path}'");
    }

    private static string Resolve(LauncherConfig config)
    {
        if (config.BridgeExecutable.Length > 0)
        {
            if (!File.Exists(config.BridgeExecutable))
            {
                throw new FileNotFoundException($"bridgeExecutable '{config.BridgeExecutable}' not found");
            }

            return Path.GetFullPath(config.BridgeExecutable);
        }

        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new List<string> { Path.Combine(baseDirectory, "RosAtlasBridge.exe") };

        // Development layout: both projects publish side by side under windows\<project>\bin\<cfg>\net8.0.
        foreach (var configuration in new[] { "Debug", "Release" })
        {
            candidates.Add(Path.Combine(
                baseDirectory, "..", "..", "..", "..", "RosAtlasBridge", "bin", configuration, "net8.0", "RosAtlasBridge.exe"));
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new FileNotFoundException(
            "Could not find RosAtlasBridge.exe. Build it, or set \"bridgeExecutable\" in bringup.json. Looked in:" +
            Environment.NewLine + string.Join(Environment.NewLine, candidates.Select(Path.GetFullPath)));
    }
}
