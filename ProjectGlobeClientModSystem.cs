using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace projectglobe;

public sealed class ProjectGlobeClientModSystem : ModSystem
{
    private ICoreClientAPI _capi = null!;
    private PoleCrossingPacket? _pendingMountedPole;
    private long _pendingMountedUntil;
    private long? _poleTickListener;
    private bool _mountAngleHistoryResolved;
    private Vec3f? _mountAngleHistory;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        _capi = api;
        _pendingMountedPole = null;
        _mountAngleHistoryResolved = false;
        _mountAngleHistory = null;

        api.Network.RegisterChannel(ProjectGlobeNetwork.ChannelName)
            .RegisterMessageType<PoleCrossingPacket>()
            .SetMessageHandler<PoleCrossingPacket>(OnPoleCrossing);
        _poleTickListener = api.Event.RegisterGameTickListener(OnPoleTick, 20);
    }

    private void OnPoleCrossing(PoleCrossingPacket packet)
    {
        _pendingMountedPole = null;
        if (!packet.Mounted)
        {
            _capi.Input.MouseYaw += GameMath.PI;
            return;
        }
        if (TryApplyMountedPole(packet)) return;
        _pendingMountedPole = packet;
        _pendingMountedUntil = _capi.World.ElapsedMilliseconds + 2000;
    }

    private void OnPoleTick(float dt)
    {
        var packet = _pendingMountedPole;
        if (packet == null) return;
        if (TryApplyMountedPole(packet))
        {
            _pendingMountedPole = null;
        }
        else if (_capi.World.ElapsedMilliseconds >= _pendingMountedUntil)
        {
            _pendingMountedPole = null;
            _capi.Logger.Warning("[projectglobe] mounted pole correction could not be applied for mount {0}.", packet.MountEntityId);
        }
    }

    private bool TryApplyMountedPole(PoleCrossingPacket packet)
    {
        var player = _capi.World.Player;
        var entity = player?.Entity;
        var seat = entity?.MountedOn;
        var mount = seat?.MountSupplier?.OnEntity;
        if (mount == null || mount.EntityId != packet.MountEntityId) return false;
        var history = GetMountAngleHistory();
        if (history == null) return false;
        mount.Pos.Yaw = packet.MountYaw;
        var seatPos = seat!.SeatPosition;
        var nativePushesYaw = player!.CameraMode == EnumCameraMode.FirstPerson
                              || seat.AngleMode is EnumMountAngleMode.Push or EnumMountAngleMode.PushYaw;
        var turn = nativePushesYaw
            ? GameMath.AngleRadDistance(history.Y, seatPos.Yaw)
            : GameMath.PI;
        if (entity!.HeadYawLimits != null) entity.HeadYawLimits.X = seatPos.Yaw;
        if (entity.BodyYawLimits != null) entity.BodyYawLimits.X = seatPos.Yaw;
        _capi.Input.MouseYaw += turn;
        entity.BodyYaw += turn;
        entity.Pos.Yaw = !nativePushesYaw && (seat.AngleMode is EnumMountAngleMode.Fixate or EnumMountAngleMode.FixateYaw)
            ? seatPos.Yaw : _capi.Input.MouseYaw;
        history.Y = seatPos.Yaw;
        _capi.Logger.Debug("[projectglobe] mounted pole: entity {0}, seat mode {1}, client yaw correction {2:F3} rad.",
            packet.MountEntityId, seat.AngleMode, turn);
        return true;
    }

    private Vec3f? GetMountAngleHistory()
    {
        if (_mountAngleHistoryResolved) return _mountAngleHistory;
        _mountAngleHistoryResolved = true;
        var field = _capi.World.GetType().GetField("prevMountAngles", BindingFlags.Instance | BindingFlags.NonPublic);
        _mountAngleHistory = field?.GetValue(_capi.World) as Vec3f;
        if (_mountAngleHistory == null)
            _capi.Logger.Error("[projectglobe] native mounted-camera yaw history was not found; mounted correction is disabled.");
        return _mountAngleHistory;
    }

    public override void Dispose()
    {
        if (_poleTickListener.HasValue)
            _capi.Event.UnregisterGameTickListener(_poleTickListener.Value);
        _pendingMountedPole = null;
        _mountAngleHistory = null;
        base.Dispose();
    }
}
