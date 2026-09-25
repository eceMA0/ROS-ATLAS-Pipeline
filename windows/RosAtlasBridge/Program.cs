using RosAtlasBridge;
using RosAtlasBridge.Atlas;
using RosAtlasBridge.Bridge;
using RosAtlasBridge.Foxglove;

// RosAtlasBridge [config.json] [--robot ws://host:8765] [--dry-run] [--seconds N] [--stop-on-stdin-close]
// Subscribes to a robot's ROS 2 topics through foxglove_bridge and streams them into one ATLAS session.
//   --dry-run              print what would be sent to ATLAS instead of writing to Kafka
//   --seconds              stop cleanly after N seconds (scripted checks)
//   --stop-on-stdin-close  stop cleanly when stdin closes (used by the rosatlas launcher)

string? configPath = null;
string? robotOverride = null;
double? runSeconds = null;
var dryRun = false;
var stopOnStdinClose = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--dry-run":
            dryRun = true;
            break;
        case "--stop-on-stdin-close":
            stopOnStdinClose = true;
            break;
        case "--robot" when i + 1 < args.Length:
            robotOverride = args[++i];
            break;
        case "--seconds" when i + 1 < args.Length:
            runSeconds = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            break;
        default:
            configPath = args[i];
            break;
    }
}

configPath ??= Path.Combine(AppContext.BaseDirectory, "config.json");
var config = File.Exists(configPath) ? BridgeConfig.Load(configPath) : new BridgeConfig();
Log(File.Exists(configPath) ? $"Config: {configPath}" : $"No config at {configPath} - using defaults");
if (robotOverride is not null)
{
    config.RobotUrl = robotOverride;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // shut down cleanly so ATLAS gets EndOfSession (otherwise it shows "live" forever)
    cancellation.Cancel();
};
if (runSeconds is { } seconds)
{
    cancellation.CancelAfter(TimeSpan.FromSeconds(seconds));
}

if (stopOnStdinClose)
{
    // Started by another program (e.g. rosatlas): closing our stdin is its way to ask for a clean
    // stop, since a console child can't be sent Ctrl+C reliably. Opt-in, so runs with stdin
    // redirected from /dev/null or a pipe don't stop straight away.
    _ = Task.Run(() =>
    {
        while (Console.In.ReadLine() is not null)
        {
        }

        Log("Input closed - stopping");
        cancellation.Cancel();
    });
}

AtlasSession? atlas = null;
IAtlasSink sink;
if (dryRun)
{
    Log("Dry run: printing what would be sent instead of writing to ATLAS");
    sink = new ConsoleSink(Log);
}
else
{
    try
    {
        atlas = new AtlasSession(config, new Logger(LoggingLevel.Info));
    }
    catch (Exception ex)
    {
        Log($"Couldn't start the ATLAS session: {ex.Message}");
        Log($"Is Kafka running at {config.KafkaBroker}?");
        return 1;
    }

    sink = atlas;
}

using (atlas)
using (var bridge = new TopicBridge(config, sink, Log))
{
    var robot = new Uri(config.RobotUrl);
    while (!cancellation.IsCancellationRequested)
    {
        using var client = new FoxgloveClient
        {
            Connected = bridge.OnConnected,
            ShouldSubscribe = bridge.ShouldSubscribe,
            MessageReceived = bridge.OnMessage,
            Log = Log,
        };
        try
        {
            Log($"Connecting to {robot} ...");
            await client.RunAsync(robot, cancellation.Token);
            Log("The robot closed the connection.");
        }
        catch (Exception) when (cancellation.IsCancellationRequested)
        {
            break;
        }
        catch (Exception ex)
        {
            Log($"Connection to {robot} failed: {ex.Message}");
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellation.Token); // then reconnect, same ATLAS session
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }

    Log("Stopping: flushing rows and ending the session ...");
}

return 0;

static void Log(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
