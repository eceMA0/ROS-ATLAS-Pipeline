namespace RosAtlasBridge.Atlas;

/// <summary>Sequential packet ids. Not thread-safe: AtlasSession only calls it under its write lock.</summary>
internal sealed class PacketIdGenerator
{
    private ulong packetId;

    public ulong GetPacketId() => this.packetId++;
}
