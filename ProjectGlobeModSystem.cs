using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace projectglobe;

public class ProjectGlobeModSystem : ModSystem
{
    internal ProjectGlobeConfig Config { get; private set; } = null!;

    private int TriggerMargin => Config.TriggerMargin;
    private int PreloadDistance => Config.PreloadDistance;
    private const int LoadRadius = 2;
    private const long CooldownMs = 3000;
    private const long PendingTimeoutMs = 5000;
    private const long RescueRetryMs = 1500;
    private const int RescueReentryPadding = 12;
    private const int ArrivalSearchRadius = 4;
    private const double ArrivalClearance = 0.05;
    private const long DestinationRetryMs = 1500;
    private const long PreloadCheckMs = 1000;
    private const long LoadTrackingExpiryMs = 60000;
    private const int MaxNewPreloadsPerTick = 2;

    private class Pending
    {
        public double X, Z;
        public bool Pole;
        public long Started;
        public Task Loaded = null!;
    }

    private ICoreServerAPI _sapi = null!;
    private IServerNetworkChannel _globeChannel = null!;
    private int _chunkSize;
    private int _mapSizeX, _mapSizeY, _mapSizeZ;
    private int _chunkCountY;
    private int _maxChunkX, _maxChunkZ;
    private int _mapSizeCaptureStarted;
    private volatile bool _mapSizeReady;
    private readonly Dictionary<string, Pending> _pending = new();
    private readonly Dictionary<string, long> _cooldownUntil = new();
    private readonly Dictionary<long, (Task Ready, long Started, long LastUsed, long LastChecked)> _destinationLoads = new();
    private readonly List<long> _expiredLoadKeys = new();
    private long _nextLoadCleanup;
    private int _newPreloadsThisTick;
    private readonly Dictionary<string, long> _rescueNextTry = new();

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        _sapi = api;
        Config = ProjectGlobeConfig.Load(api);
        _globeChannel = api.Network
            .RegisterChannel(ProjectGlobeNetwork.ChannelName)
            .RegisterMessageType<PoleCrossingPacket>();
        
        _chunkSize = GlobalConstants.ChunkSize;
        _pending.Clear();
        _cooldownUntil.Clear();
        _rescueNextTry.Clear();
        _destinationLoads.Clear();
        _expiredLoadKeys.Clear();
        _nextLoadCleanup = 0;
        _newPreloadsThisTick = 0;
        _mapSizeCaptureStarted = 0;
        _mapSizeReady = false;
        _sapi.Event.PlayerNowPlaying += OnPlayerNowPlaying;
        _sapi.Event.RegisterGameTickListener(OnTick, 100);
        _sapi.Event.PlayerDisconnect += p =>
        {
            _pending.Remove(p.PlayerUID);
            _cooldownUntil.Remove(p.PlayerUID);
            _rescueNextTry.Remove(p.PlayerUID);
        };
    }
    
    private void OnPlayerNowPlaying(IServerPlayer player)
    {
        if (System.Threading.Interlocked.CompareExchange(ref _mapSizeCaptureStarted, 1, 0) != 0)
            return;
        _mapSizeX = _sapi.WorldManager.MapSizeX;
        _mapSizeY = _sapi.WorldManager.MapSizeY;
        _mapSizeZ = _sapi.WorldManager.MapSizeZ;
        if (_mapSizeX < _chunkSize || _mapSizeY < _chunkSize || _mapSizeZ < _chunkSize)
        {
            _sapi.Logger.Error(
                "[projectglobe] Unusable map size read after PlayerNowPlaying: {0} x {1} x {2}. " +
                "Core traversal stays inactive; dimensions will not be read again this session.",
                _mapSizeX, _mapSizeY, _mapSizeZ);
            return;
        }
        
        _maxChunkX = _mapSizeX / _chunkSize - 1;
        _maxChunkZ = _mapSizeZ / _chunkSize - 1;
        _chunkCountY = (_mapSizeY + _chunkSize - 1) / _chunkSize;
        _mapSizeReady = true;
        _sapi.Logger.Notification(
            "[projectglobe] map size cached after PlayerNowPlaying: {0} x {1} x {2} blocks (X/Y/Z).",
            _mapSizeX, _mapSizeY, _mapSizeZ);
    }

    private void OnTick(float dt)
    {
        if (!_mapSizeReady) return;
        
        var now = _sapi.World.ElapsedMilliseconds;
        _newPreloadsThisTick = 0;
        CleanupDestinationLoads(now);

        foreach (var p in _sapi.World.AllOnlinePlayers)
        {
            if (p is not IServerPlayer player || player.Entity == null) continue;
            var uid = player.PlayerUID;
            var pos = player.Entity.Pos;
            
            if (_pending.TryGetValue(uid, out var pt))
            {
                if (pt.Loaded.IsCompletedSuccessfully && TryFindArrival(player, pt, out var arrivalX, out var arrivalY, out var arrivalZ))
                {
                    pt.X = arrivalX;
                    pt.Z = arrivalZ;
                    _pending.Remove(uid);
                    DoTeleport(player, pt, arrivalY);
                }
                else if (now - pt.Started > PendingTimeoutMs)
                {
                    _pending.Remove(uid);
                    _cooldownUntil[uid] = now + CooldownMs;
                    _sapi.Logger.Warning("[projectglobe] crossing timed out for {0}: {1}.", 
                        player.PlayerName, pt.Loaded.IsCompletedSuccessfully ? "no clear surface landing within the search radius or destination unloaded" : "destination chunks not ready");
                }
                else if (!pt.Loaded.IsCompletedSuccessfully || !IsDestinationAreaLoaded(pt.X, pt.Z))
                {
                    pt.Loaded = RequestDestinationLoad(pt.X, pt.Z, now, false)!;
                }
                continue;
            }

            if (TryEdgeRescue(player, now)) continue;
            if (_cooldownUntil.TryGetValue(uid, out var until) && now < until) continue;
            
            if (Arrival(pos.X, pos.Z, out var ax, out var az, out var pole))
            {
                BeginTeleport(player, ax, az, pole);
                continue;
            }
            
            PreloadNearEdges(pos.X, pos.Z, now);
        }
    }
    
    private bool Arrival(double x, double z, out double nx, out double nz, out bool pole)
    {
        double sx = _mapSizeX, sz = _mapSizeZ;
        var period = sx - 2 * TriggerMargin;
        nx = x; nz = z; pole = false;
        if (sx <= 2 * TriggerMargin || sz <= 2 * TriggerMargin) return false;
        switch (Config.EnablePoleLogic)
        {
            case true when z > sz - TriggerMargin:
                nz = 2 * (sz - TriggerMargin) - z; nx += period / 2; pole = true;
                break;
            case true when z < TriggerMargin:
                nz = 2 * TriggerMargin - z;        nx += period / 2; pole = true;
                break;
        }

        var wrapX = nx < TriggerMargin || nx > sx - TriggerMargin;
        if (!pole && !wrapX) return false;

        nx = TriggerMargin + (((nx - TriggerMargin) % period) + period) % period;
        return true;
    }

    private void PreloadNearEdges(double x, double z, long now)
    { 
        if (PreloadDistance <= 0) return;
        var west = x < TriggerMargin + PreloadDistance;
        var east = x > _mapSizeX - TriggerMargin - PreloadDistance;
        var north = Config.EnablePoleLogic && z < TriggerMargin + PreloadDistance;
        var south = Config.EnablePoleLogic && z > _mapSizeZ - TriggerMargin - PreloadDistance;
        double wx = TriggerMargin - 1, ex = _mapSizeX - TriggerMargin + 1;
        double nz = TriggerMargin - 1, sz = _mapSizeZ - TriggerMargin + 1;
        if (west) PreloadCrossing(wx, z, now);
        if (east) PreloadCrossing(ex, z, now);
        if (north) PreloadCrossing(x, nz, now);
        if (south) PreloadCrossing(x, sz, now);
        if (west && north) PreloadCrossing(wx, nz, now);
        if (west && south) PreloadCrossing(wx, sz, now);
        if (east && north) PreloadCrossing(ex, nz, now);
        if (east && south) PreloadCrossing(ex, sz, now);
    }

    private void PreloadCrossing(double x, double z, long now)
    {

        if (Arrival(x, z, out var nx, out var nz, out _))
            RequestDestinationLoad(nx, nz, now, true);
    }

    private Task? RequestDestinationLoad(double x, double z, long now, bool preload)
    {
        var key = ColumnKey(x, z);
        if (_destinationLoads.TryGetValue(key, out var load))
        {
            load.LastUsed = now;
            _destinationLoads[key] = load;
            if (!load.Ready.IsCompleted && now - load.Started <= PendingTimeoutMs)
                return load.Ready;
            if (load.Ready.IsCompletedSuccessfully)
            {
                if (preload && now - load.LastChecked < PreloadCheckMs) return load.Ready;
                load.LastChecked = now;
                _destinationLoads[key] = load;
                if (IsDestinationAreaLoaded(x, z)) return load.Ready;
            }
            if (now - load.Started < DestinationRetryMs) return load.Ready;
        }
        switch (preload)
        {
            case true when _newPreloadsThisTick >= MaxNewPreloadsPerTick:
                return null;
            case true:
                _newPreloadsThisTick++;
                break;
        }

        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _destinationLoads[key] = (ready.Task, now, now, now);
        try
        {
            LoadAround(x, z, () => ready.TrySetResult(true));
        }
        catch (Exception e)
        {
            ready.TrySetException(e);
            _ = ready.Task.Exception;
            _sapi.Logger.Warning("[projectglobe] destination load request failed: {0}", e.Message);
        }
        return ready.Task;
    }

    private bool IsDestinationAreaLoaded(double x, double z)
    {

        int cx = (int)x / _chunkSize, cz = (int)z / _chunkSize;
        var ba = _sapi.World.BlockAccessor;
        for (var x1 = Math.Max(0, cx - LoadRadius); x1 <= Math.Min(_maxChunkX, cx + LoadRadius); x1++)
        for (var z1 = Math.Max(0, cz - LoadRadius); z1 <= Math.Min(_maxChunkZ, cz + LoadRadius); z1++)
        for (var cy = 0; cy < _chunkCountY; cy++)
            if (ba.GetChunk(x1, cy, z1) == null) return false;
        return true;
    }

    private void CleanupDestinationLoads(long now)
    {

        if (now < _nextLoadCleanup) return;
        _nextLoadCleanup = now + 5000;
        _expiredLoadKeys.Clear();
        foreach (var entry in _destinationLoads)
            if (now - entry.Value.LastUsed > LoadTrackingExpiryMs) _expiredLoadKeys.Add(entry.Key);
        foreach (var key in _expiredLoadKeys) _destinationLoads.Remove(key);
    }

    private bool TryFindArrival(IServerPlayer player, Pending pt, out double x, out double y, out double z)
    { 
        x = pt.X; y = 0; z = pt.Z; 
        var supplier = player.Entity.MountedOn?.MountSupplier; 
        var root = supplier?.OnEntity ?? player.Entity; 
        var rootBox = GetArrivalBox(root, root != player.Entity, pt.Pole); 
        for (var distanceSq = 0; distanceSq <= ArrivalSearchRadius * ArrivalSearchRadius; distanceSq++)
        {
            for (var dz = -ArrivalSearchRadius; dz <= ArrivalSearchRadius; dz++)
            {
                for (var dx = -ArrivalSearchRadius; dx <= ArrivalSearchRadius; dx++)
                {
                    if (dx * dx + dz * dz != distanceSq) continue;
                    double tx = pt.X + dx, tz = pt.Z + dz;
                    if (tx < TriggerMargin || tx > _mapSizeX - TriggerMargin) continue;
                    if (Config.EnablePoleLogic
                            ? tz < TriggerMargin || tz > _mapSizeZ - TriggerMargin
                            : tz < 0 || tz >= _mapSizeZ) continue;
                    if (!TryGetArrivalSurface(tx, tz, out var surface)) continue;
                    var ty = surface - rootBox.Y1 + ArrivalClearance;
                    if (!HasArrivalClearance(rootBox, tx, ty, tz)) continue;
                    var passengersClear = true;
                    if (supplier?.OnEntity != null)
                    {
                        foreach (var seat in supplier.Seats)
                        {
                            var passenger = seat.Passenger;
                            if (passenger == null) continue;
                            var seatPos = seat.SeatPosition;
                            var ox = seatPos.X - root.Pos.X;
                            var oy = seatPos.Y - root.Pos.Y;
                            var oz = seatPos.Z - root.Pos.Z;
                            if (pt.Pole) { ox = -ox; oz = -oz; }
                            var passengerBox = GetArrivalBox(passenger, false, pt.Pole);
                            if (!HasArrivalClearance(passengerBox, tx + ox, ty + oy, tz + oz))
                            {
                                passengersClear = false;
                                break;
                            }
                        }
                    }
                    if (!passengersClear) continue;
                    x = tx; y = ty; z = tz;
                    return true;
                }
            }
        }
        return false;
    }

    private Cuboidf GetArrivalBox(Entity entity, bool mounted, bool pole)
    { 
        var box = entity.OriginCollisionBox ?? entity.CollisionBox; 
        var result = box.Clone();
        if (mounted && entity.SelectionBox != null)
        {
            var selection = entity.SelectionBox;
            result.X1 = Math.Min(result.X1, selection.X1);
            result.Y1 = Math.Min(result.Y1, selection.Y1);
            result.Z1 = Math.Min(result.Z1, selection.Z1);
            result.X2 = Math.Max(result.X2, selection.X2);
            result.Y2 = Math.Max(result.Y2, selection.Y2);
            result.Z2 = Math.Max(result.Z2, selection.Z2);
        }
        if (pole)
            result = new Cuboidf(-result.X2, result.Y1, -result.Z2, -result.X1, result.Y2, -result.Z1);
        return result;
    }

    private bool TryGetArrivalSurface(double x, double z, out double surface)
    {
        surface = 0;
        var ba = _sapi.World.BlockAccessor;
        int bx = (int)Math.Floor(x), bz = (int)Math.Floor(z);
        if (!IsArrivalColumnLoaded(bx, bz)) return false;
        var pos = new BlockPos(Dimensions.NormalWorld).Set(bx, 0, bz);
        var rainHeight = ba.GetRainMapHeightAt(pos);
        var terrainHeight = _sapi.WorldManager.GetSurfacePosY(bx, bz) ?? 0;
        var top = Math.Clamp(Math.Max(rainHeight, terrainHeight), 0, _mapSizeY - 1);
        while (top + 1 < _mapSizeY)
        {
            pos.Y = top + 1;
            if (!ba.GetBlock(pos, BlockLayersAccess.Fluid).IsLiquid()) break;
            top++;
        }

        top = Math.Min(top + 1, _mapSizeY - 1);
        for (var by = top; by >= 0; by--)
        {
            pos.Y = by;
            var fluid = ba.GetBlock(pos, BlockLayersAccess.Fluid);
            if (fluid.IsLiquid())
            {
                if (fluid.BlockMaterial != EnumBlockMaterial.Water) return false;
                surface = by + 1; // Above the highest liquid block, never the lake bed.
                return true;
            }

            var solid = ba.GetBlock(pos, BlockLayersAccess.MostSolid);
            if (solid.Id == 0) continue;
            switch (solid.BlockMaterial)
            {
                case EnumBlockMaterial.Fire or EnumBlockMaterial.Lava:
                    return false;
                case EnumBlockMaterial.Leaves or EnumBlockMaterial.Plant:
                    continue;
            }

            if (solid.Code?.Path.StartsWith("log-grown", StringComparison.Ordinal) == true
                || (solid.BlockMaterial == EnumBlockMaterial.Wood
                    && solid.Attributes?["treeFellingGroupCode"].AsString() != null)) continue;
            var boxes = solid.GetCollisionBoxes(ba, pos);
            if (boxes == null) continue;
            var highest = double.NegativeInfinity;
            double localX = x - bx, localZ = z - bz;
            foreach (var box in boxes)
            {
                if (box == null || localX < box.X1 || localX > box.X2
                    || localZ < box.Z1 || localZ > box.Z2) continue;
                highest = Math.Max(highest, box.Y2);
            }

            if (double.IsNegativeInfinity(highest)) continue;
            surface = by + highest;
            return true;
        }
        return false;
    }

    private bool IsArrivalColumnLoaded(int bx, int bz)
    {
        int cx = bx / _chunkSize, cz = bz / _chunkSize;
        for (var cy = 0; cy < _chunkCountY; cy++)
            if (_sapi.World.BlockAccessor.GetChunk(cx, cy, cz) == null)
                return false;
        return true;
    }

    private bool HasArrivalClearance(Cuboidf box, double x, double y, double z)
    {
        if (x + box.X1 < 0 || x + box.X2 >= _mapSizeX 
                           || z + box.Z1 < 0 || z + box.Z2 >= _mapSizeZ
                           || y + box.Y1 < 0 || y + box.Y2 >= _mapSizeY) return false;
        var ba = _sapi.World.BlockAccessor;
        int minX = (int)Math.Floor(x + box.X1), maxX = (int)Math.Floor(x + box.X2);
        var minY = Math.Max(0, (int)Math.Floor(y + box.Y1) - 1);
        var maxY = (int)Math.Floor(y + box.Y2);
        int minZ = (int)Math.Floor(z + box.Z1), maxZ = (int)Math.Floor(z + box.Z2);
        for (var cx = minX / _chunkSize; cx <= maxX / _chunkSize; cx++)
        for (var cz = minZ / _chunkSize; cz <= maxZ / _chunkSize; cz++) 
        for (var cy = minY / _chunkSize; cy <= maxY / _chunkSize; cy++) 
            if (ba.GetChunk(cx, cy, cz) == null) return false;
    
        if (_sapi.World.CollisionTester.IsColliding(ba, box, new Vec3d(x, y, z), false)) return false;
        var pos = new BlockPos(Dimensions.NormalWorld);
        for (var by = minY; by <= maxY; by++)
        for (var bx = minX; bx <= maxX; bx++)
        for (var bz = minZ; bz <= maxZ; bz++)
        {
            pos.Set(bx, by, bz);
            var fluid = ba.GetBlock(pos, BlockLayersAccess.Fluid);
            if (fluid.IsLiquid() && fluid.BlockMaterial != EnumBlockMaterial.Water) return false;
            if (ba.GetBlock(pos, BlockLayersAccess.Solid).BlockMaterial == EnumBlockMaterial.Fire) return false;
        }
 
        return true;
    }
    
    private bool TryEdgeRescue(IServerPlayer player, long nowMs)
    {
        var cfg = Config;
        if (!cfg.EnableEdgeRescue) return false;
        var pos = player.Entity.Pos;
        double sx = _mapSizeX, sz = _mapSizeZ;
        var outsideX = pos.X < 0 || pos.X >= sx;
        var outsideZ = pos.Z < 0 || pos.Z >= sz;
        var belowWorld = pos.Y < cfg.RescueBelowY;
        if (!outsideX && !outsideZ && !belowWorld) return false;
        var uid = player.PlayerUID;
        if (_rescueNextTry.TryGetValue(uid, out var next) && nowMs < next) return false;
        var pad = cfg.TriggerMargin + RescueReentryPadding;
        if (sx <= 2 * pad || sz <= 2 * pad) return false;
        double nx = pos.X, nz = pos.Z;
        var pole = false;
        if (outsideX || outsideZ)
        {
            if (!Arrival(pos.X, pos.Z, out nx, out nz, out pole))
            {
                nx = pos.X;
                nz = pos.Z;
                pole = false;
            }
        }
        nx = Math.Clamp(nx, pad, sx - pad);
        nz = Math.Clamp(nz, pad, sz - pad);
        _rescueNextTry[uid] = nowMs + RescueRetryMs;
        _sapi.Logger.Notification(
            "[projectglobe] rescue: {0} at ({1:F0}, {2:F0}, {3:F0}) -> ({4:F0}, {5:F0}){6}",
            player.PlayerName, pos.X, pos.Y, pos.Z, nx, nz, pole ? " (pole)" : "");
        BeginTeleport(player, nx, nz, pole);
        return true;
    }

    private void LoadAround(double x, double z, Action? onLoaded)
    {
        int cx = (int)x / _chunkSize, cz = (int)z / _chunkSize;

        _sapi.WorldManager.LoadChunkColumnPriority(
            Math.Max(0, cx - LoadRadius), Math.Max(0, cz - LoadRadius),
            Math.Min(_maxChunkX, cx + LoadRadius), Math.Min(_maxChunkZ, cz + LoadRadius),
            new ChunkLoadOptions { KeepLoaded = false, OnLoaded = onLoaded });
    }

    private void DoTeleport(IServerPlayer player, Pending pt, double arrivalY)
    {
        var y = arrivalY;

        var supplier = player.Entity.MountedOn?.MountSupplier;
        var mountEntity = supplier?.OnEntity;
        var mounted = mountEntity != null;
        if (pt.Pole) (mountEntity ?? player.Entity).Pos.Yaw += MathF.PI;
        var mountYaw = mountEntity?.Pos.Yaw ?? 0;
        var mountId = mountEntity?.EntityId ?? 0;
        var riders = new List<IServerPlayer> { player };
        if (mounted)
        {
            foreach (var seat in supplier!.Seats)
            {
                if (seat.Passenger is not EntityPlayer passenger) continue;
                if (_sapi.World.PlayerByUid(passenger.PlayerUID) is not IServerPlayer rider) continue;
                if (!riders.Exists(p => p.PlayerUID == rider.PlayerUID)) riders.Add(rider);
            }
        }
        var until = _sapi.World.ElapsedMilliseconds + CooldownMs;
        foreach (var rider in riders)
        {
            _pending.Remove(rider.PlayerUID);
            _cooldownUntil[rider.PlayerUID] = until;
        }
        _sapi.Logger.Notification("[projectglobe] {0} -> ({1:F0}, {2}, {3:F0}){4}",
            player.PlayerName, pt.X, y, pt.Z, pt.Pole ? " (pole)" : "");

        Action? onTeleported = null;

        if (pt.Pole)
        {
            var packetSent = 0;

            onTeleported = () =>
            {
                if (System.Threading.Interlocked.Exchange(ref packetSent, 1) == 0)
                {
                    _globeChannel.SendPacket(new PoleCrossingPacket
                    {
                        Mounted = mounted,
                        MountEntityId = mountId,
                        MountYaw = mountYaw
                    }, riders.ToArray());
                }
            };
        }

        player.Entity.TeleportToDouble(pt.X, y, pt.Z, onTeleported);
    }
    
    private void BeginTeleport(IServerPlayer player, double x, double z, bool pole)
    {
        var now = _sapi.World.ElapsedMilliseconds;
        var np = new Pending
        {
            X = x, Z = z, Pole = pole, Started = now,
            Loaded = RequestDestinationLoad(x, z, now, false)!
        };
        _pending[player.PlayerUID] = np;
    }

    private long ColumnKey(double x, double z) =>
        ((long)((int)x / _chunkSize) << 32) | (uint)((int)z / _chunkSize);
}
