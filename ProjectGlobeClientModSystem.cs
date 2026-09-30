using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace projectglobe;

public sealed class ProjectGlobeClientModSystem : ModSystem
{
    private ICoreClientAPI _capi = null!;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        _capi = api;

        api.Network.RegisterChannel(ProjectGlobeNetwork.ChannelName)
            .RegisterMessageType<PoleCrossingPacket>()
            .SetMessageHandler<PoleCrossingPacket>(OnPoleCrossing);
    }

    private void OnPoleCrossing(PoleCrossingPacket packet)
    {
        _capi.Input.MouseYaw += GameMath.PI;
    }
}