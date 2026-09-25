namespace RosAtlasBridge.Bridge;

/// <summary>--dry-run sink: prints what would go to ATLAS instead of writing to Kafka.</summary>
public sealed class ConsoleSink(Action<string> log) : IAtlasSink
{
    private readonly object gate = new();
    private readonly List<TopicLayout> layouts = new();
    private readonly Dictionary<ulong, long> rowCounts = new();
    private readonly Dictionary<ulong, long> lastPrintMs = new();

    public IReadOnlyList<ulong> DefineTopics(IReadOnlyList<TopicLayout> newLayouts)
    {
        var ids = new List<ulong>();
        lock (this.gate)
        {
            foreach (var layout in newLayouts)
            {
                this.layouts.Add(layout);
                ids.Add((ulong)this.layouts.Count);
                var names = string.Join(", ", layout.Parameters.Take(6).Select(p => p.Identifier));
                log($"[dry-run] configuration: {layout.Topic} ({layout.TypeName}) -> group '{layout.ApplicationName}', " +
                    $"{layout.Parameters.Count} parameters at ~{layout.FrequencyHz} Hz: {names}{(layout.Parameters.Count > 6 ? ", ..." : "")}");
            }
        }

        return ids;
    }

    public void WriteRows(ulong dataFormatId, IReadOnlyList<ulong> timestampsNs, IReadOnlyList<double[]> rows)
    {
        TopicLayout layout;
        long total;
        lock (this.gate)
        {
            layout = this.layouts[(int)dataFormatId - 1];
            total = this.rowCounts[dataFormatId] = this.rowCounts.GetValueOrDefault(dataFormatId) + rows.Count;
            var now = Environment.TickCount64;
            if (now - this.lastPrintMs.GetValueOrDefault(dataFormatId, long.MinValue / 2) < 1000)
            {
                return; // at most one line per topic per second
            }

            this.lastPrintMs[dataFormatId] = now;
        }

        var latest = DateTime.UnixEpoch.AddTicks((long)(timestampsNs[^1] / 100));
        var sample = string.Join(", ", layout.Parameters.Zip(rows[^1]).Take(4).Select(x => $"{x.First.Name}={x.Second:G5}"));
        log($"[dry-run] {layout.Topic}: {total} rows so far, latest {latest:HH:mm:ss.fff} UTC: {sample}");
    }
}
