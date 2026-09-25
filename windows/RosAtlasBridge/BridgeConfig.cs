using System.Text.Json;
using System.Text.RegularExpressions;

namespace RosAtlasBridge;

public sealed class ParameterMetadata
{
    public string? Description { get; set; }
    public string? Units { get; set; }
    public string? FormatString { get; set; }
    public double? MinValue { get; set; }
    public double? MaxValue { get; set; }
}

/// <summary>Settings from config.json. See the comments in that file for what each one does.</summary>
public sealed class BridgeConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string RobotUrl { get; set; } = "ws://robot.local:8765";
    public string KafkaBroker { get; set; } = "localhost:9094";
    public string DataSource { get; set; } = "Default";
    public string[] Streams { get; set; } = ["", "Stream1"];
    public string SessionName { get; set; } = "ROS";
    public string[] IncludeTopics { get; set; } = [".*"];
    public string[] ExcludeTopics { get; set; } = ["^/rosout$", "^/parameter_events$", "^/tf(_static)?$"];
    public int MaxArrayLength { get; set; } = 32;
    public double DiscoveryWindowSeconds { get; set; } = 2.0;
    public int FlushIntervalMs { get; set; } = 50;
    public uint DefaultFrequencyHz { get; set; } = 100;
    public double DefaultMinValue { get; set; }
    public double DefaultMaxValue { get; set; } = 100;
    public Dictionary<string, ParameterMetadata> Parameters { get; set; } = new();

    public static BridgeConfig Load(string path) =>
        JsonSerializer.Deserialize<BridgeConfig>(File.ReadAllText(path), JsonOptions) ?? new BridgeConfig();

    public bool IsTopicIncluded(string topic) =>
        this.IncludeTopics.Any(pattern => Regex.IsMatch(topic, pattern)) &&
        !this.ExcludeTopics.Any(pattern => Regex.IsMatch(topic, pattern));

    /// <summary>Metadata for "<topic>/<path>": an exact key wins, otherwise the first matching '*' pattern.</summary>
    public ParameterMetadata? FindMetadata(string topic, string path)
    {
        var key = $"{topic}/{path}";
        if (this.Parameters.TryGetValue(key, out var exact))
        {
            return exact;
        }

        foreach (var (pattern, metadata) in this.Parameters)
        {
            if (pattern.Contains('*') &&
                Regex.IsMatch(key, "^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$"))
            {
                return metadata;
            }
        }

        return null;
    }
}
