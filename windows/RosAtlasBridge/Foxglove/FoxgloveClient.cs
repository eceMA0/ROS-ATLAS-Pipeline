using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text.Json;

namespace RosAtlasBridge.Foxglove;

public sealed record FoxgloveChannel(
    uint Id, string Topic, string Encoding, string SchemaName, string Schema, string? SchemaEncoding);

/// <summary>
/// Minimal read-only client for the Foxglove WebSocket protocol served by the ROS foxglove_bridge.
/// One instance handles one connection.
/// </summary>
public sealed class FoxgloveClient : IDisposable
{
    /// <summary>
    /// foxglove_bridge 3.x (Foxglove SDK) only accepts "foxglove.sdk.v1"; older bridges, including
    /// the ROS 1 one, speak "foxglove.websocket.v1". Offer both, preferring the SDK name.
    /// </summary>
    public static readonly string[] SubProtocols = ["foxglove.sdk.v1", "foxglove.websocket.v1"];

    private const byte MessageDataOpcode = 0x01;

    private readonly ClientWebSocket socket = new();
    private readonly Dictionary<uint, FoxgloveChannel> subscriptions = new(); // subscription id -> channel
    private readonly Dictionary<uint, uint> subscriptionByChannel = new();    // channel id -> subscription id
    private uint nextSubscriptionId;

    public FoxgloveClient()
    {
        foreach (var subProtocol in SubProtocols)
        {
            this.socket.Options.AddSubProtocol(subProtocol);
        }
    }

    /// <summary>Decides which advertised channels to subscribe to.</summary>
    public Func<FoxgloveChannel, bool> ShouldSubscribe { get; init; } = _ => true;

    /// <summary>
    /// Called on the receive loop for every message: channel, server log time [ns], CDR payload.
    /// The payload buffer is reused afterwards, so consume it before returning.
    /// </summary>
    public Action<FoxgloveChannel, ulong, ArraySegment<byte>>? MessageReceived { get; init; }

    /// <summary>Called once the WebSocket is open, before any channel is advertised.</summary>
    public Action? Connected { get; init; }

    public Action<string>? Log { get; init; }

    /// <summary>Connects and processes messages until the server closes the connection or the token is cancelled.</summary>
    public async Task RunAsync(Uri uri, CancellationToken cancellationToken)
    {
        await this.socket.ConnectAsync(uri, cancellationToken);
        if (!SubProtocols.Contains(this.socket.SubProtocol))
        {
            throw new InvalidOperationException(
                $"server did not accept any of {string.Join(", ", SubProtocols)} (got '{this.socket.SubProtocol}')");
        }

        this.Log?.Invoke($"WebSocket connected ({this.socket.SubProtocol})");
        this.Connected?.Invoke();
        var chunk = new byte[64 * 1024];
        var message = new MemoryStream();
        while (this.socket.State == WebSocketState.Open)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await this.socket.ReceiveAsync(new ArraySegment<byte>(chunk), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                message.Write(chunk, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Text)
            {
                await this.OnJsonAsync(message.GetBuffer().AsMemory(0, (int)message.Length), cancellationToken);
            }
            else
            {
                this.OnBinary(message.GetBuffer(), (int)message.Length);
            }
        }
    }

    public void Dispose() => this.socket.Dispose();

    private async Task OnJsonAsync(ReadOnlyMemory<byte> json, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        switch (root.GetProperty("op").GetString())
        {
            case "serverInfo":
                var metadata = root.TryGetProperty("metadata", out var m) ? m.ToString() : "{}";
                this.Log?.Invoke($"Robot bridge info: {metadata}");
                break;

            case "advertise":
                var toSubscribe = new List<FoxgloveChannel>();
                foreach (var c in root.GetProperty("channels").EnumerateArray())
                {
                    var channel = new FoxgloveChannel(
                        c.GetProperty("id").GetUInt32(),
                        c.GetProperty("topic").GetString()!,
                        c.GetProperty("encoding").GetString()!,
                        c.GetProperty("schemaName").GetString()!,
                        c.GetProperty("schema").GetString()!,
                        c.TryGetProperty("schemaEncoding", out var schemaEncoding) ? schemaEncoding.GetString() : null);
                    if (!this.subscriptionByChannel.ContainsKey(channel.Id) && this.ShouldSubscribe(channel))
                    {
                        toSubscribe.Add(channel);
                    }
                }

                if (toSubscribe.Count > 0)
                {
                    await this.SubscribeAsync(toSubscribe, cancellationToken);
                }

                break;

            case "unadvertise":
                foreach (var id in root.GetProperty("channelIds").EnumerateArray())
                {
                    if (this.subscriptionByChannel.Remove(id.GetUInt32(), out var subscriptionId))
                    {
                        this.subscriptions.Remove(subscriptionId);
                    }
                }

                break;

            case "status":
                this.Log?.Invoke($"Robot bridge status: {root.GetProperty("message").GetString()}");
                break;
        }
    }

    private void OnBinary(byte[] data, int length)
    {
        // Message Data frame: opcode (1) | subscription id (uint32 LE) | log time ns (uint64 LE) | payload
        if (length < 13 || data[0] != MessageDataOpcode)
        {
            return;
        }

        var subscriptionId = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(1, 4));
        var logTimeNs = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(5, 8));
        if (this.subscriptions.TryGetValue(subscriptionId, out var channel))
        {
            this.MessageReceived?.Invoke(channel, logTimeNs, new ArraySegment<byte>(data, 13, length - 13));
        }
    }

    /// <summary>Only ever called from the receive loop, so sends never overlap.</summary>
    private async Task SubscribeAsync(List<FoxgloveChannel> channels, CancellationToken cancellationToken)
    {
        var entries = new List<object>();
        foreach (var channel in channels)
        {
            var subscriptionId = this.nextSubscriptionId++;
            this.subscriptions[subscriptionId] = channel;
            this.subscriptionByChannel[channel.Id] = subscriptionId;
            entries.Add(new { id = subscriptionId, channelId = channel.Id });
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { op = "subscribe", subscriptions = entries });
        await this.socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }
}
