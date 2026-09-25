using System.Diagnostics;

using RosAtlasBridge.Foxglove;
using RosAtlasBridge.Ros;

namespace RosAtlasBridge.Bridge;

/// <summary>
/// Glue between the robot and ATLAS. Parses each topic's schema once, flattens every message into
/// a row, fixes each topic's parameter layout from its first message, and hands batches of rows
/// to the sink. State is keyed by topic name, so a reconnect continues the same ATLAS session.
/// </summary>
public sealed class TopicBridge : IDisposable
{
    // Stamps before 2000-01-01 are simulation time or unset, not wall-clock time.
    private const long MinWallClockNs = 946_684_800_000_000_000;

    private readonly BridgeConfig config;
    private readonly IAtlasSink sink;
    private readonly Action<string> log;
    private readonly object gate = new(); // the receive loop and the flush timer share all state below
    private readonly Dictionary<string, TopicState> topics = new();
    private readonly Stopwatch discoveryClock = new(); // starts on the first connection, not at start-up
    private readonly Timer? flushTimer;
    private bool discoveryDone;
    private bool disposed;

    public TopicBridge(BridgeConfig config, IAtlasSink sink, Action<string> log)
    {
        this.config = config;
        this.sink = sink;
        this.log = log;
        if (config.FlushIntervalMs > 0) // 0 = flush manually (tests)
        {
            this.flushTimer = new Timer(_ => this.Flush(final: false), null, config.FlushIntervalMs, config.FlushIntervalMs);
        }
    }

    /// <summary>
    /// Starts the discovery window. Timing it from start-up instead let a slow connect (e.g. "localhost"
    /// trying IPv6 first, ~2 s) use up the window, so every topic got the default rate rather than its measured one.
    /// </summary>
    public void OnConnected()
    {
        lock (this.gate)
        {
            if (!this.discoveryDone)
            {
                this.discoveryClock.Restart();
            }
        }
    }

    public bool ShouldSubscribe(FoxgloveChannel channel)
    {
        if (!this.config.IsTopicIncluded(channel.Topic))
        {
            return false;
        }

        if (channel.Encoding != "cdr" || channel.SchemaEncoding is not (null or "ros2msg"))
        {
            this.log($"Skipping {channel.Topic}: unsupported encoding {channel.Encoding}/{channel.SchemaEncoding}");
            return false;
        }

        MsgDefinition definition;
        try
        {
            definition = MsgSchema.Parse(channel.SchemaName, channel.Schema);
        }
        catch (FormatException ex)
        {
            this.log($"Skipping {channel.Topic}: can't parse schema {channel.SchemaName}: {ex.Message}");
            return false;
        }

        lock (this.gate)
        {
            if (this.topics.TryGetValue(channel.Topic, out var existing))
            {
                if (existing.Definition.FullName != definition.FullName)
                {
                    this.log($"Skipping {channel.Topic}: type changed from {existing.Definition.FullName} to {definition.FullName}");
                    return false;
                }

                existing.Definition = definition; // reconnected: keep the layout, refresh the schema
            }
            else
            {
                this.topics[channel.Topic] = new TopicState(channel.Topic, definition);
            }
        }

        this.log($"Subscribing to {channel.Topic} ({definition.FullName})");
        return true;
    }

    public void OnMessage(FoxgloveChannel channel, ulong logTimeNs, ArraySegment<byte> payload)
    {
        lock (this.gate)
        {
            if (this.disposed || !this.topics.TryGetValue(channel.Topic, out var topic) || topic.Ignored)
            {
                return;
            }

            FlatMessage flat;
            try
            {
                flat = MessageFlattener.Flatten(
                    topic.Definition, payload.Array!, payload.Offset, payload.Count, this.config.MaxArrayLength);
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException or OverflowException)
            {
                topic.WarnOnce(this.log, "decode", $"dropping undecodable message: {ex.Message}");
                return;
            }

            topic.MessageCount++;
            if (topic.Layout is null)
            {
                if (flat.Values.Count == 0)
                {
                    this.log($"{topic.Topic}: no numeric fields - ignoring");
                    topic.Ignored = true;
                    return;
                }

                topic.SetLayout(this.CreateLayout(topic.Topic, topic.Definition.FullName, flat), flat);
                this.log($"{topic.Topic}: {flat.Values.Count} parameters -> ATLAS group '{topic.Layout!.ApplicationName}'");
                if (this.discoveryDone)
                {
                    this.Define([topic], fromDiscovery: false);
                }
            }

            topic.PendingTimes.Add(this.ChooseTimestamp(topic, flat.StampNs, logTimeNs));
            topic.PendingRows.Add(topic.ToRow(flat, this.log));
        }
    }

    /// <summary>Ends discovery once its window has passed, then writes every topic's pending rows.</summary>
    public void Flush(bool final)
    {
        lock (this.gate)
        {
            if (this.disposed)
            {
                return;
            }

            if (!this.discoveryDone && (final ||
                (this.discoveryClock.IsRunning && this.discoveryClock.Elapsed.TotalSeconds >= this.config.DiscoveryWindowSeconds)))
            {
                this.discoveryDone = true;
                this.Define(this.topics.Values.Where(t => t.Layout is not null && !t.Ignored).ToList(), fromDiscovery: true);
            }

            foreach (var topic in this.topics.Values)
            {
                if (topic.FormatId is { } formatId && topic.PendingRows.Count > 0)
                {
                    this.sink.WriteRows(formatId, topic.PendingTimes, topic.PendingRows);
                    topic.PendingTimes = new List<ulong>();
                    topic.PendingRows = new List<double[]>();
                }
            }

            this.disposed = final;
        }
    }

    public void Dispose()
    {
        this.flushTimer?.Dispose();
        this.Flush(final: true);
    }

    private void Define(List<TopicState> newTopics, bool fromDiscovery)
    {
        if (newTopics.Count == 0)
        {
            return;
        }

        var seconds = Math.Max(this.discoveryClock.Elapsed.TotalSeconds, 0.001);
        var layouts = newTopics
            .Select(t => t.Layout! with
            {
                FrequencyHz = fromDiscovery
                    ? (uint)Math.Max(1, Math.Round(t.MessageCount / seconds))
                    : this.config.DefaultFrequencyHz,
            })
            .ToList();

        try
        {
            var formatIds = this.sink.DefineTopics(layouts);
            for (var i = 0; i < newTopics.Count; i++)
            {
                newTopics[i].FormatId = formatIds[i];
            }
        }
        catch (InvalidOperationException ex)
        {
            this.log($"ATLAS rejected the configuration for {string.Join(", ", newTopics.Select(t => t.Topic))}: {ex.Message}");
            foreach (var topic in newTopics)
            {
                topic.Ignored = true;
            }
        }
    }

    private TopicLayout CreateLayout(string topic, string typeName, FlatMessage flat)
    {
        var application = ParameterNaming.Sanitize(topic);
        var used = new HashSet<string>();
        var parameters = new List<AtlasParameter>(flat.Values.Count);
        foreach (var (path, _) in flat.Values)
        {
            var baseName = ParameterNaming.Sanitize(path);
            var name = baseName;
            for (var n = 2; !used.Add(name); n++)
            {
                name = $"{baseName}_{n}"; // e.g. "a.b" and "a_b" both sanitize to "a_b"
            }

            var metadata = this.config.FindMetadata(topic, path);
            parameters.Add(new AtlasParameter(
                Identifier: $"{name}:{application}",
                Name: name,
                Description: metadata?.Description ?? $"{topic} {path}",
                Units: metadata?.Units ?? string.Empty,
                FormatString: metadata?.FormatString ?? "%5.3f",
                Min: metadata?.MinValue ?? this.config.DefaultMinValue,
                Max: metadata?.MaxValue ?? this.config.DefaultMaxValue));
        }

        return new TopicLayout(topic, typeName, application, parameters, this.config.DefaultFrequencyHz);
    }

    private ulong ChooseTimestamp(TopicState topic, long? stampNs, ulong logTimeNs)
    {
        if (stampNs >= MinWallClockNs)
        {
            return (ulong)stampNs.Value;
        }

        topic.WarnOnce(this.log, "time", stampNs is null
            ? "no header - using the robot bridge's receive time"
            : "header.stamp is not wall-clock time (sim time or unset) - using the robot bridge's receive time");
        return logTimeNs >= MinWallClockNs
            ? logTimeNs
            : (ulong)(DateTime.UtcNow - DateTime.UnixEpoch).Ticks * 100;
    }

    private sealed class TopicState(string topic, MsgDefinition definition)
    {
        private readonly Dictionary<string, int> columns = new(); // flattened path -> column
        private readonly HashSet<string> warnings = new();

        public string Topic { get; } = topic;
        public MsgDefinition Definition { get; set; } = definition;
        public TopicLayout? Layout { get; private set; }
        public ulong? FormatId { get; set; }
        public bool Ignored { get; set; }
        public long MessageCount { get; set; }
        public List<ulong> PendingTimes { get; set; } = new();
        public List<double[]> PendingRows { get; set; } = new();

        /// <summary>The first message fixes the columns: one per flattened path, in message order.</summary>
        public void SetLayout(TopicLayout layout, FlatMessage first)
        {
            this.Layout = layout;
            for (var i = 0; i < first.Values.Count; i++)
            {
                this.columns[first.Values[i].Path] = i;
            }
        }

        /// <summary>Values in column order; fields this message lacks (shorter sequences) stay NaN = missing.</summary>
        public double[] ToRow(FlatMessage flat, Action<string> log)
        {
            var row = new double[this.columns.Count];
            Array.Fill(row, double.NaN);
            foreach (var (path, value) in flat.Values)
            {
                if (this.columns.TryGetValue(path, out var column))
                {
                    row[column] = value;
                }
                else
                {
                    this.WarnOnce(log, "extra", $"'{path}' wasn't in the first message - not bridged");
                }
            }

            return row;
        }

        public void WarnOnce(Action<string> log, string key, string message)
        {
            if (this.warnings.Add(key))
            {
                log($"{this.Topic}: {message}");
            }
        }
    }
}
