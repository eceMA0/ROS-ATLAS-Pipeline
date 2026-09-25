using System.Text.Json;

namespace RosAtlasLauncher;

/// <summary>Settings from bringup.json. See the comments in that file for what each one does.</summary>
public sealed class LauncherConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string Host { get; set; } = string.Empty;
    public string User { get; set; } = "ubuntu";
    public int Port { get; set; } = 22;
    public string IdentityFile { get; set; } = string.Empty;

    public string CanChannel { get; set; } = "can0";
    public int CanBitrate { get; set; } = 1_000_000;

    public string RosDistro { get; set; } = "jazzy";
    public string Workspace { get; set; } = "~/dev/caninput/ros2_ws";
    public bool Build { get; set; }

    public int FoxglovePort { get; set; } = 8765;

    public double PublishRateHz { get; set; } = 100.0;
    public double StatusRateHz { get; set; } = 1.0;

    public int ReadyTimeoutSeconds { get; set; } = 30;

    public bool StartBridge { get; set; } = true;
    public string BridgeExecutable { get; set; } = string.Empty;

    public bool CanDownOnExit { get; set; }

    public static LauncherConfig Load(string path) =>
        JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(path), JsonOptions) ?? new LauncherConfig();

    public void Validate()
    {
        var problems = new List<string>();
        if (this.Port is < 1 or > 65535)
        {
            problems.Add($"port {this.Port} is out of range");
        }

        if (this.FoxglovePort is < 1 or > 65535)
        {
            problems.Add($"foxglovePort {this.FoxglovePort} is out of range");
        }

        if (this.CanBitrate <= 0)
        {
            problems.Add($"canBitrate {this.CanBitrate} must be positive");
        }

        if (this.PublishRateHz <= 0 || this.StatusRateHz <= 0)
        {
            problems.Add("publishRateHz and statusRateHz must be > 0");
        }

        if (this.ReadyTimeoutSeconds <= 0)
        {
            problems.Add($"readyTimeoutSeconds {this.ReadyTimeoutSeconds} must be positive");
        }

        if (this.IdentityFile.Length > 0 && !File.Exists(this.IdentityFile))
        {
            problems.Add($"identityFile '{this.IdentityFile}' does not exist");
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException("bringup.json: " + string.Join("; ", problems));
        }
    }
}
