using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace projectglobe;

public class ProjectGlobeModSystem : ModSystem
{
    private const int TriggerMargin = 3;
    private const int PreloadDistance = 256;
    private const int LoadRadius = 2;
    private const long CooldownMs = 3000;
    private const long PendingTimeoutMs = 5000;

    private class Pending
    {
        public double X, Z;
        public bool Pole;
        public long Started;
        public volatile bool Ready;
    }

    private ICoreServerAPI _sapi = null!;
    private IServerNetworkChannel _globeChannel = null!;
    private int _chunkSize;
    private readonly Dictionary<string, Pending> _pending = new();
    private readonly Dictionary<string, long> _cooldownUntil = new();
    private readonly HashSet<long> _preloaded = new();

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        _sapi = api;
        _globeChannel = api.Network
            .RegisterChannel(ProjectGlobeNetwork.ChannelName)
            .RegisterMessageType<PoleCrossingPacket>();
        
        _chunkSize = GlobalConstants.ChunkSize;
        _sapi.Event.RegisterGameTickListener(OnTick, 100);
        _sapi.Event.PlayerDisconnect += p => { _pending.Remove(p.PlayerUID); _cooldownUntil.Remove(p.PlayerUID); };
    }

    private void OnTick(float dt)
    {
        long now = _sapi.World.ElapsedMilliseconds;

        foreach (IPlayer p in _sapi.World.AllOnlinePlayers)
        {
            if (p is not IServerPlayer player || player.Entity == null) continue;
            string uid = player.PlayerUID;
            var pos = player.Entity.Pos;
            
            if (_pending.TryGetValue(uid, out var pt))
            {
                if (pt.Ready && TryGetArrivalY(pt.X, pt.Z, out int arrivalY))
                {
                    _pending.Remove(uid);
                    DoTeleport(player, pt, arrivalY);
                }
                else if (now - pt.Started > PendingTimeoutMs)
                {
                    _pending.Remove(uid);
                    _cooldownUntil[uid] = now + CooldownMs;
                }
                continue;
            }

            if (_cooldownUntil.TryGetValue(uid, out long until) && now < until) continue;
            
            if (Arrival(pos.X, pos.Z, out double ax, out double az, out bool pole))
            {
                var np = new Pending { X = ax, Z = az, Pole = pole, Started = now };
                _pending[uid] = np;
                LoadAround(ax, az, () => np.Ready = true);
                continue;
            }
            
            if (PreloadTarget(pos.X, pos.Z, out double px, out double pz)
                && _preloaded.Add(ColumnKey(px, pz)))
            {
                LoadAround(px, pz, null);
            }
        }
    }
    
    private bool Arrival(double x, double z, out double nx, out double nz, out bool pole)
    {
        double sx = _sapi.WorldManager.MapSizeX, sz = _sapi.WorldManager.MapSizeZ;
        double period = sx - 2 * TriggerMargin;
        nx = x; nz = z; pole = false;
        if (sx <= 2 * TriggerMargin || sz <= 2 * TriggerMargin) return false;
        if (z > sz - TriggerMargin) { nz = 2 * (sz - TriggerMargin) - z; nx += period / 2; pole = true; }
        else if (z < TriggerMargin) { nz = 2 * TriggerMargin - z;        nx += period / 2; pole = true; }

        bool wrapX = nx < TriggerMargin || nx > sx - TriggerMargin;
        if (!pole && !wrapX) return false;

        nx = TriggerMargin + (((nx - TriggerMargin) % period) + period) % period;
        return true;
    }
    
    private bool PreloadTarget(double x, double z, out double nx, out double nz)
    {
        double sx = _sapi.WorldManager.MapSizeX, sz = _sapi.WorldManager.MapSizeZ;
        double vx = x, vz = z;

        if (x > sx - TriggerMargin - PreloadDistance) vx = sx - TriggerMargin + 1;
        else if (x < TriggerMargin + PreloadDistance) vx = TriggerMargin - 1;

        if (z > sz - TriggerMargin - PreloadDistance) vz = sz - TriggerMargin + 1;
        else if (z < TriggerMargin + PreloadDistance) vz = TriggerMargin - 1;

        return Arrival(vx, vz, out nx, out nz, out _);
    }
    
    private bool TryGetArrivalY(double x, double z, out int y)
    {
        y = 0;
        int? surfaceY = _sapi.WorldManager.GetSurfacePosY((int)x, (int)z);
        if (surfaceY == null) return false;
        y = surfaceY.Value + 1;
        return true;
    }

    private void LoadAround(double x, double z, Action? onLoaded)
    {
        int maxCx = _sapi.WorldManager.MapSizeX / _chunkSize - 1;
        int maxCz = _sapi.WorldManager.MapSizeZ / _chunkSize - 1;
        int cx = (int)x / _chunkSize, cz = (int)z / _chunkSize;

        _sapi.WorldManager.LoadChunkColumnPriority(
            Math.Max(0, cx - LoadRadius), Math.Max(0, cz - LoadRadius),
            Math.Min(maxCx, cx + LoadRadius), Math.Min(maxCz, cz + LoadRadius),
            new ChunkLoadOptions { OnLoaded = onLoaded });
    }

    private void DoTeleport(IServerPlayer player, Pending pt, int arrivalY)
    {
        double y = arrivalY;

        if (pt.Pole) player.Entity.Pos.Yaw += MathF.PI;

        _cooldownUntil[player.PlayerUID] = _sapi.World.ElapsedMilliseconds + CooldownMs;
        _sapi.Logger.Notification("[projectglobe] {0} -> ({1:F0}, {2}, {3:F0}){4}",
            player.PlayerName, pt.X, y, pt.Z, pt.Pole ? " (pole)" : "");

        Action? onTeleported = null;

        if (pt.Pole)
        {
            int packetSent = 0;

            onTeleported = () =>
            {
                // Avoid a second camera turn on the mounted-entity callback path.
                if (System.Threading.Interlocked.Exchange(ref packetSent, 1) == 0)
                {
                    _globeChannel.SendPacket(new PoleCrossingPacket(), player);
                }
            };
        }

        player.Entity.TeleportToDouble(pt.X, y, pt.Z, onTeleported);
    }

    private long ColumnKey(double x, double z) =>
        ((long)((int)x / _chunkSize) << 32) | (uint)((int)z / _chunkSize);
}
