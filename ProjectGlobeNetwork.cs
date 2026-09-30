using ProtoBuf;

namespace projectglobe;

public class ProjectGlobeNetwork
{
    internal const string ChannelName = "projectglobe-yaw";
}

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public sealed class PoleCrossingPacket
{

}
