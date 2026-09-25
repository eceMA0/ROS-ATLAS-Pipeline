using System.Diagnostics;
using System.Globalization;
using RosAtlasLauncher;

// rosatlas - one command to bring the ROS-ATLAS pipeline up.
//
// Everything robot-side happens inside ONE SSH session running one script, so you authenticate at
// most once and the robot cleans up after itself even if the link drops. See RemoteSession.cs for
// why that matters.
//
// Usage:
//   rosatlas [<host>] [--host <ip>] [--user <name>] [--config <bringup.json>]
//            [--build] [--bridge|--no-bridge] [--can-down] [--help]

const string Usage = """
rosatlas - bring up the ROS -> ATLAS pipeline

Usage:
  rosatlas [<host>] [options]

Options:
  --host <ip|name>     Robot address. Prompted for if not given here or in bringup.json.
  --user <name>        SSH user (default from bringup.json).
  --port <n>           SSH port.
  --config <path>      Config file (default: bringup.json next to the executable).
  --build              Run colcon build on the robot before launching.
  --bridge             Start RosAtlasBridge locally (default).
  --no-bridge          Robot-side bring-up only.
  --can-down           Bring the CAN interface down on exit.
  --dump-script        Print the robot-side script and exit, without connecting.
  -h, --help           Show this help.
""";

void Log(string message) =>
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

try
{
    var configPath = Path.Combine(AppContext.BaseDirectory, "bringup.json");
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] is "--config" or "-c")
        {
            configPath = args[i + 1];
        }
    }

    if (!File.Exists(configPath))
    {
        throw new FileNotFoundException($"config file not found: {configPath}");
    }

    var config = LauncherConfig.Load(configPath);
    var dumpScript = false;

    for (var i = 0; i < args.Length; i++)
    {
        var argument = args[i];
        string Next(string name) => i + 1 < args.Length
            ? args[++i]
            : throw new ArgumentException($"{name} needs a value");

        switch (argument)
        {
            case "-h" or "--help":
                Console.WriteLine(Usage);
                return 0;
            case "--host":
                config.Host = Next(argument);
                break;
            case "--user" or "-u":
                config.User = Next(argument);
                break;
            case "--port":
                config.Port = int.Parse(Next(argument), CultureInfo.InvariantCulture);
                break;
            case "--config" or "-c":
                Next(argument);
                break;
            case "--build":
                config.Build = true;
                break;
            case "--bridge":
                config.StartBridge = true;
                break;
            case "--no-bridge":
                config.StartBridge = false;
                break;
            case "--can-down":
                config.CanDownOnExit = true;
                break;
            case "--dump-script":
                dumpScript = true;
                break;
            default:
                if (argument.StartsWith('-'))
                {
                    throw new ArgumentException($"unknown option '{argument}'");
                }

                config.Host = argument;
                break;
        }
    }

    if (dumpScript)
    {
        // LF endings so the output can be piped straight into "bash -n" for a syntax check.
        Console.Out.Write(RemoteScript.Build(config).ReplaceLineEndings("\n"));
        return 0;
    }

    if (config.Host.Length == 0)
    {
        Console.Write("Robot IP or hostname: ");
        config.Host = (Console.ReadLine() ?? string.Empty).Trim();
        if (config.Host.Length == 0)
        {
            throw new ArgumentException("no host given");
        }
    }

    config.Validate();

    // Asked for up front, because once the session is running its stdin carries the script and we
    // cannot interleave a prompt. An empty value is fine when sudo is passwordless: the remote
    // script tries "sudo -n" first and only falls back to the password.
    var sudoPassword = SudoPassword.FromEnvironment(config.User, config.Host).GetForBringup();

    using var cancellation = new CancellationTokenSource();
    var stopping = 0;
    Console.CancelKeyPress += (_, e) =>
    {
        // First Ctrl+C: shut down cleanly so RosAtlasBridge can write EndOfSession and the robot
        // can kill its nodes. Second Ctrl+C: let the runtime terminate us.
        if (Interlocked.Exchange(ref stopping, 1) == 0)
        {
            e.Cancel = true;
            Log("Shutting down ... (Ctrl+C again to force)");
            cancellation.Cancel();
        }
    };

    Log($"Connecting to {config.User}@{config.Host}:{config.Port} ...");
    Log("(if prompted, answer the SSH host-key or password prompt below)");

    using var session = new RemoteSession(config, RemoteScript.Build(config), Log);
    Process? bridge = null;

    try
    {
        session.Start(sudoPassword, cancellation.Token);
        Log("Robot is up: CAN active, foxglove_bridge listening, caninput_node publishing.");

        if (!config.StartBridge)
        {
            Log($"Run the bridge with: RosAtlasBridge --robot ws://{config.Host}:{config.FoxglovePort}");
            Log("Press Ctrl+C to stop.");
            cancellation.Token.WaitHandle.WaitOne();
            return 0;
        }

        bridge = BridgeLauncher.Start(config, Log);
        Log("Pipeline running. Press Ctrl+C to stop.");

        while (!cancellation.IsCancellationRequested && !bridge.HasExited && session.IsAlive)
        {
            Thread.Sleep(500);
        }

        if (!cancellation.IsCancellationRequested)
        {
            Log(bridge.HasExited
                ? $"RosAtlasBridge exited with code {bridge.ExitCode}."
                : "The robot-side session ended; stopping.");
        }

        return 0;
    }
    finally
    {
        // Bridge first: it needs a live robot connection to close its ATLAS session tidily.
        // session.Dispose() then stops the robot-side nodes.
        if (bridge is not null)
        {
            if (!bridge.HasExited)
            {
                Log("Stopping RosAtlasBridge ...");

                // Closing its stdin asks it to flush and send EndOfSession. CloseMainWindow did
                // nothing here: a console child sharing our console has no window of its own.
                try
                {
                    bridge.StandardInput.Close();
                }
                catch (IOException)
                {
                    // Already exiting.
                }

                if (!bridge.WaitForExit(10_000))
                {
                    try
                    {
                        bridge.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
                    {
                        // Already gone.
                    }
                }
            }

            bridge.Dispose();
        }
    }
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
