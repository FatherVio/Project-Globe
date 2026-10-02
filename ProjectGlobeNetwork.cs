using ProtoBuf;

namespace projectglobe;

internal static class ProjectGlobeNetwork
{
    internal const string ChannelName = "projectglobe-yaw";
}

[ProtoContract]
public sealed class PoleCrossingPacket
{
    [ProtoMember(1)] public bool Mounted;
    [ProtoMember(2)] public long MountEntityId;
    [ProtoMember(3)] public float MountYaw;
}
