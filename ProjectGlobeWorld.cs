using System;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.Datastructures;
using Vintagestory.ServerMods;
using Vintagestory.ServerMods.NoObf;

namespace projectglobe;

public sealed class ProjectGlobeWorldModSystem : ModSystem
{
    private const string InitialTeleportModDataKey = "projectglobe:latTpDone";
    private const long TeleportTimeoutMs = 30000;
    private const long ChunkRequestRetryMs = 2000;
    private const double ArrivalClearance = 0.05;

    private static readonly FieldInfo? GenMapsLatDataField =
        typeof(GenMaps).GetField("latdata", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? NoiseClimateHalfRangeField =
        typeof(NoiseClimateRealistic).GetField("halfRange", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly PropertyInfo? NoiseClimateZOffsetProp =
        typeof(NoiseClimateRealistic).GetProperty("ZOffset", BindingFlags.Instance | BindingFlags.Public);

    private static readonly FieldInfo? RockStrataNoisesField =
        typeof(GenRockStrataNew).GetField("strataNoises", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
    
    private enum SpawnStage
    {
        WaitingForEquatorChunks,
        WaitingForTargetChunks
    }

    private sealed class PendingInitialTeleport
    {
        public SpawnStage Stage;
        public int EquatorBlockX;
        public int EquatorBlockZ;
        public double TargetX;
        public double TargetZ;
        public double TargetLatitude;
        public long StartedMs;
        public long LastLoadRequestMs;
        public bool LoadInFlight;
    }

    private ICoreServerAPI _sapi = null!;
    private ProjectGlobeConfig _config = null!;
    private int _chunkSize;
    private int _mapSizeX, _mapSizeY, _mapSizeZ;
    private int _chunkCountY, _maxChunkX, _maxChunkZ;
    private bool _dimensionsReady;
    private double _chosenTargetLatitude;
    private int _chosenTargetZ;

    private readonly Dictionary<string, PendingInitialTeleport> _pendingInitialTeleports = new();
    
    private bool _seamHooksRegistered;
    private bool _seamReady;
    private bool _polarLandMapHookRegistered;
    private int _regionSize;
    private GenMaps? _genMaps;
    private GenRockStrataNew? _genRockStrata;
    private GenVegetationAndPatches? _genVeg;
    private GlobalConfig? _gcfg;
    private int _terrainGenOctaves;
    private NewNormalizedSimplexFractalNoise? _terrainNoise;
    private SimplexNoise? _distort2Dx;
    private SimplexNoise? _distort2Dz;
    private NormalizedSimplexNoise? _geoUpheavalNoise;
    private float[][]? _terrainYThresholds;
    
    public override double ExecuteOrder() => 0.11;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        _sapi = api;
        _chunkSize = GlobalConstants.ChunkSize;
        _config = api.ModLoader.GetModSystem<ProjectGlobeModSystem>()?.Config
                  ?? ProjectGlobeConfig.Load(api);
        _pendingInitialTeleports.Clear();
        _dimensionsReady = false;
        _seamHooksRegistered = false;
        _seamReady = false;
        _polarLandMapHookRegistered = false;

        api.Event.InitWorldGenerator(OnInitWorldGen, "standard");
        api.Event.InitWorldGenerator(OnInitWorldGen, "superflat");
        api.Event.ChunkColumnGeneration(
            OnTerrainColumnSeamSync,
            EnumWorldGenPass.Terrain,
            "standard");

        api.Event.PlayerNowPlaying += OnPlayerNowPlaying;
        api.Event.RegisterGameTickListener(OnServerTick, 100);
        api.Event.PlayerDisconnect += p => _pendingInitialTeleports.Remove(p.PlayerUID);
    }
    
    private void OnInitWorldGen()
    {
        CacheDimensions();
        if (!_dimensionsReady) return;

        var worldConfig = _sapi.WorldManager.SaveGame.WorldConfiguration;
        ComputeTargetLatitudeFromStartingClimate(worldConfig);
        
        InitSeamSync();
        RegisterPolarLandMapHook();

        if (!_config.EnableGlobeClimate) return;

        var genMaps = _sapi.ModLoader.GetModSystem<GenMaps>();
        if (genMaps == null)
        {
            _sapi.Logger.Warning("[projectglobe] GenMaps ModSystem not found; globe climate setup skipped.");
            return;
        }

        long seed = _sapi.WorldManager.Seed;
        var polarEquatorDistance = Math.Max(1, _mapSizeZ / 2);
        double climateScale = TerraGenConfig.climateMapScale * TerraGenConfig.climateMapSubScale;
        var mapSizeZScaled = _mapSizeZ / climateScale;
        var exactHalfRange = (_mapSizeZ / 2.0) / climateScale;

        var tempModifier = worldConfig.GetString("globalTemperature", "1").ToFloat(1f);
        var rainModifier = worldConfig.GetString("globalPrecipitation", "1").ToFloat(1f);
        var geoActivity = worldConfig.GetString("geologicActivity", "0.05").ToFloat(0.05f);
        var landformScale = worldConfig.GetString("landformScale", "1.0").ToFloat(1.0f);

        var noiseClimate = new NoiseClimateRealistic(seed, mapSizeZScaled, polarEquatorDistance, 6, 14)
        {
            tempMul = tempModifier,
            rainMul = rainModifier,
            GeologicActivityStrength = geoActivity
        };
        
        NoiseClimateHalfRangeField?.SetValue(noiseClimate, exactHalfRange);
        NoiseClimateZOffsetProp?.SetValue(noiseClimate, 0.0);

        genMaps.climateGen = GenMaps.GetClimateMapGen(seed + 1, noiseClimate);
        genMaps.landformsGen = GenMaps.GetLandformMapGen(seed + 4, noiseClimate, _sapi, landformScale);

        if (GenMapsLatDataField?.GetValue(genMaps) is LatitudeData latData)
        {
            latData.isRealisticClimate = true;
            latData.polarEquatorDistance = polarEquatorDistance;
            latData.ZOffset = 0.0;
        }

        _sapi.World.Calendar.OnGetLatitude = GetGlobeLatitude;
        
        if (_config.EnableInitialLatitudeTeleport && Math.Abs(_chosenTargetZ - _mapSizeZ / 2) > _chunkSize)
        {
            EnsureLandAtTargetLatitude(genMaps, (_mapSizeX + _chunkSize) / 2, _chosenTargetZ, 4 * _chunkSize);
        }

        _sapi.Logger.Notification(
            "[projectglobe] Globe climate active: Equator Z={0}, North Pole Z=0, South Pole Z={1}, startingClimate target Z={2} (lat={3:F2}).",
            _mapSizeZ / 2, _mapSizeZ, _chosenTargetZ, _chosenTargetLatitude);
    }

    private double GetGlobeLatitude(double posZ)
    {
        if (_mapSizeZ <= 0) return 0.0;
        var halfMapZ = _mapSizeZ / 2.0;
        return Math.Clamp(1.0 - posZ / halfMapZ, -1.0, 1.0);
    }

    private void ComputeTargetLatitudeFromStartingClimate(ITreeAttribute? worldConfig)
    {
        var startingClimate = worldConfig?.GetString("startingClimate", "temperate") ?? "temperate";
        var equatorZ = _mapSizeZ / 2;

        if (string.Equals(startingClimate, "hot", StringComparison.OrdinalIgnoreCase))
        {
            _chosenTargetLatitude = 0.0;
            _chosenTargetZ = equatorZ;
            return;
        }

        var (minTemp, maxTemp) = startingClimate.ToLowerInvariant() switch
        {
            "warm" => (19, 23),
            "cool" => (-5, 1),
            "icy" => (-15, -10),
            _ => (6, 14)
        };

        var minDescaled = Climate.DescaleTemperature(minTemp);
        var maxDescaled = Climate.DescaleTemperature(maxTemp);

        var rand = new LCGRandom(_sapi.WorldManager.Seed + 7919);
        rand.InitPositionSeed(minDescaled, maxDescaled);

        double rndTemp = minDescaled + rand.NextInt(Math.Max(1, maxDescaled - minDescaled + 1));
        var hemisphereSign = rand.NextInt(2) == 0 ? 1 : -1; // +1 = North (Z < Equator), -1 = South (Z > Equator)

        var latMagnitude = Math.Clamp(1.0 - rndTemp / 255.0, 0.0, 1.0);
        _chosenTargetLatitude = hemisphereSign * latMagnitude;

        var halfMapZ = _mapSizeZ / 2.0;
        var rawTargetZ = (int)Math.Round(equatorZ - _chosenTargetLatitude * halfMapZ);
        var margin = Math.Max(_config.TriggerMargin + 16, _chunkSize);
        _chosenTargetZ = _mapSizeZ > 2 * margin
            ? Math.Clamp(rawTargetZ, margin, _mapSizeZ - margin)
            : equatorZ;
    }

    private void EnsureLandAtTargetLatitude(GenMaps genMaps, int posX, int posZ, int radius)
    {
        var regionSize = _sapi.WorldManager.RegionSize;
        var noiseSizeOcean = genMaps.noiseSizeOcean;
        if (regionSize <= 0 || noiseSizeOcean <= 0) return;

        var minX = (posX - radius) * noiseSizeOcean / regionSize;
        var minZ = (posZ - radius) * noiseSizeOcean / regionSize;
        var maxX = (posX + radius) * noiseSizeOcean / regionSize;
        var maxZ = (posZ + radius) * noiseSizeOcean / regionSize;
        var sizeX = Math.Max(1, maxX - minX);
        var sizeZ = Math.Max(1, maxZ - minZ);

        var lcgRandom = new LCGRandom(_sapi.WorldManager.Seed);
        lcgRandom.InitPositionSeed(posX, posZ);
        var naturalShape = new NaturalShape(lcgRandom);
        naturalShape.InitSquare(sizeX, sizeZ);
        naturalShape.Grow(sizeX * sizeZ);

        foreach (var pos in naturalShape.GetPositions())
        {
            genMaps.requireLandAt.Add(new XZ(minX + pos.X, minZ + pos.Y));
        }
    }
    
    private void OnPlayerNowPlaying(IServerPlayer player)
    {
        CacheDimensions();
        if (!_dimensionsReady || !_config.EnableInitialLatitudeTeleport) return;

        if (player.WorldData.GetModdata(InitialTeleportModDataKey) != null)
            return;

        var worldConfig = _sapi.WorldManager.SaveGame.WorldConfiguration;
        ComputeTargetLatitudeFromStartingClimate(worldConfig);

        var equatorX = _mapSizeX / 2;
        var equatorZ = _mapSizeZ / 2;
        
        if (Math.Abs(_chosenTargetZ - equatorZ) < 1)
        {
            MarkInitialTeleportDone(player);
            return;
        }

        var spawnX = player.Entity?.Pos.X ?? equatorX;
        var margin = Math.Max(_config.TriggerMargin + 16, _chunkSize);
        var targetX = _mapSizeX > 2 * margin
            ? Math.Clamp(spawnX, margin, _mapSizeX - margin)
            : equatorX;

        _pendingInitialTeleports[player.PlayerUID] = new PendingInitialTeleport
        {
            Stage = SpawnStage.WaitingForEquatorChunks,
            EquatorBlockX = equatorX,
            EquatorBlockZ = equatorZ,
            TargetX = targetX,
            TargetZ = _chosenTargetZ,
            TargetLatitude = _chosenTargetLatitude,
            StartedMs = _sapi.World.ElapsedMilliseconds,
            LastLoadRequestMs = 0,
            LoadInFlight = false
        };
    }

    private void OnServerTick(float dt)
    {
        if (!_dimensionsReady || _pendingInitialTeleports.Count == 0) return;

        var now = _sapi.World.ElapsedMilliseconds;

        foreach (var p in _sapi.World.AllOnlinePlayers)
        {
            if (p is not IServerPlayer player || player.Entity == null) continue;
            if (!_pendingInitialTeleports.TryGetValue(player.PlayerUID, out var pending)) continue;

            if (now - pending.StartedMs > TeleportTimeoutMs)
            {
                _pendingInitialTeleports.Remove(player.PlayerUID);
                _sapi.Logger.Warning(
                    "[projectglobe] Initial latitude teleport timed out for {0} at stage {1}.",
                    player.PlayerName, pending.Stage);
                continue;
            }

            if (pending.Stage == SpawnStage.WaitingForEquatorChunks)
            {
                if (IsChunkAreaLoaded(pending.EquatorBlockX, pending.EquatorBlockZ, _config.EquatorSpawnLoadRadius))
                {
                    pending.Stage = SpawnStage.WaitingForTargetChunks;
                    pending.LastLoadRequestMs = 0;
                    pending.LoadInFlight = false;
                }
                else if (!pending.LoadInFlight && now - pending.LastLoadRequestMs >= ChunkRequestRetryMs)
                {
                    pending.LastLoadRequestMs = now;
                    pending.LoadInFlight = true;
                    RequestChunkAreaLoad(
                        pending.EquatorBlockX,
                        pending.EquatorBlockZ,
                        _config.EquatorSpawnLoadRadius,
                        () => pending.LoadInFlight = false);
                }
                continue;
            }
            
            var targetBlockX = (int)Math.Floor(pending.TargetX);
            var targetBlockZ = (int)Math.Floor(pending.TargetZ);

            if (!IsChunkAreaLoaded(targetBlockX, targetBlockZ, _config.LatitudeDestinationLoadRadius))
            {
                if (!pending.LoadInFlight && now - pending.LastLoadRequestMs >= ChunkRequestRetryMs)
                {
                    pending.LastLoadRequestMs = now;
                    pending.LoadInFlight = true;
                    RequestChunkAreaLoad(
                        targetBlockX,
                        targetBlockZ,
                        _config.LatitudeDestinationLoadRadius,
                        () => pending.LoadInFlight = false);
                }
                continue;
            }

            if (TryFindSafeLanding(player, pending.TargetX, pending.TargetZ, out var ax, out var ay, out var az))
            {
                _pendingInitialTeleports.Remove(player.PlayerUID);
                MarkInitialTeleportDone(player);

                player.Entity.TeleportToDouble(ax, ay, az);
                _sapi.Logger.Notification(
                    "[projectglobe] Teleported {0} from equator spawn to chosen latitude {1:F2} at ({2:F0}, {3:F0}, {4:F0}).",
                    player.PlayerName, pending.TargetLatitude, ax, ay, az);
            }
        }
    }

    private static void MarkInitialTeleportDone(IServerPlayer player)
    {
        player.WorldData.SetModdata(InitialTeleportModDataKey, [1]);
    }

    private void CacheDimensions()
    {
        if (_dimensionsReady) return;
        _mapSizeX = _sapi.WorldManager.MapSizeX;
        _mapSizeY = _sapi.WorldManager.MapSizeY;
        _mapSizeZ = _sapi.WorldManager.MapSizeZ;
        if (_mapSizeX < _chunkSize || _mapSizeY < _chunkSize || _mapSizeZ < _chunkSize) return;

        _maxChunkX = _mapSizeX / _chunkSize - 1;
        _maxChunkZ = _mapSizeZ / _chunkSize - 1;
        _chunkCountY = (_mapSizeY + _chunkSize - 1) / _chunkSize;
        _dimensionsReady = true;
    }

    private void RequestChunkAreaLoad(int blockX, int blockZ, int radiusChunks, Action onLoaded)
    {
        var cx = blockX / _chunkSize;
        var cz = blockZ / _chunkSize;
        _sapi.WorldManager.LoadChunkColumnPriority(
            Math.Max(0, cx - radiusChunks),
            Math.Max(0, cz - radiusChunks),
            Math.Min(_maxChunkX, cx + radiusChunks),
            Math.Min(_maxChunkZ, cz + radiusChunks),
            new ChunkLoadOptions { KeepLoaded = false, OnLoaded = onLoaded });
    }
    
    private void RegisterPolarLandMapHook()
    {
        if (!_config.EnablePolarLandBias || !_dimensionsReady || _polarLandMapHookRegistered)
            return;

        _sapi.Event.MapRegionGeneration(OnPolarLandMapRegionGen, "standard");
        _polarLandMapHookRegistered = true;

        _sapi.Logger.Notification(
            "[projectglobe] Polar land bias active (core={0}%, transition={1}%).",
            _config.PolarLandCorePercent,
            _config.PolarLandTransitionPercent);
    }

    private bool IsChunkAreaLoaded(int blockX, int blockZ, int radiusChunks)
    {
        var cx = blockX / _chunkSize;
        var cz = blockZ / _chunkSize;
        var ba = _sapi.World.BlockAccessor;
        for (var x = Math.Max(0, cx - radiusChunks); x <= Math.Min(_maxChunkX, cx + radiusChunks); x++)
        for (var z = Math.Max(0, cz - radiusChunks); z <= Math.Min(_maxChunkZ, cz + radiusChunks); z++)
        for (var cy = 0; cy < _chunkCountY; cy++)
            if (ba.GetChunk(x, cy, z) == null) return false;
        return true;
    }

    private bool TryFindSafeLanding(IServerPlayer player, double targetX, double targetZ, out double x, out double y, out double z)
    {
        x = targetX; y = 0; z = targetZ;
        var box = (player.Entity.OriginCollisionBox ?? player.Entity.CollisionBox).Clone();
        var searchRadius = _config.LatitudeArrivalSearchRadius;
        var margin = _config.TriggerMargin;

        for (var r = 0; r <= searchRadius; r++)
        {
            for (var dz = -r; dz <= r; dz++)
            {
                for (var dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
                    var tx = targetX + dx;
                    var tz = targetZ + dz;
                    if (tx < margin || tx > _mapSizeX - margin || tz < margin || tz > _mapSizeZ - margin)
                        continue;

                    if (!TryGetSurfaceY(tx, tz, out var surfaceY)) continue;
                    var ty = surfaceY - box.Y1 + ArrivalClearance;
                    if (!HasClearance(box, tx, ty, tz)) continue;

                    x = tx; y = ty; z = tz;
                    return true;
                }
            }
        }
        return false;
    }

    private bool TryGetSurfaceY(double x, double z, out double surfaceY)
    {
        surfaceY = 0;
        var ba = _sapi.World.BlockAccessor;
        int bx = (int)Math.Floor(x), bz = (int)Math.Floor(z);
        int cx = bx / _chunkSize, cz = bz / _chunkSize;
        for (var cy = 0; cy < _chunkCountY; cy++)
            if (ba.GetChunk(cx, cy, cz) == null) return false;

        var pos = new BlockPos(Dimensions.NormalWorld).Set(bx, 0, bz);
        var rainY = ba.GetRainMapHeightAt(pos);
        var terrainY = _sapi.WorldManager.GetSurfacePosY(bx, bz) ?? 0;
        var top = Math.Clamp(Math.Max(rainY, terrainY) + 1, 0, _mapSizeY - 1);

        for (var by = top; by >= 0; by--)
        {
            pos.Y = by;
            var fluid = ba.GetBlock(pos, BlockLayersAccess.Fluid);
            if (fluid.IsLiquid())
            {
                if (fluid.BlockMaterial != EnumBlockMaterial.Water) return false;
                surfaceY = by + 1;
                return true;
            }

            var solid = ba.GetBlock(pos, BlockLayersAccess.MostSolid);
            if (solid.Id == 0) continue;
            if (solid.BlockMaterial is EnumBlockMaterial.Fire or EnumBlockMaterial.Lava) return false;
            if (solid.BlockMaterial is EnumBlockMaterial.Leaves or EnumBlockMaterial.Plant) continue;

            var boxes = solid.GetCollisionBoxes(ba, pos);
            if (boxes == null || boxes.Length == 0) continue;

            var highest = double.NegativeInfinity;
            double lx = x - bx, lz = z - bz;
            foreach (var b in boxes)
            {
                if (b != null && lx >= b.X1 && lx <= b.X2 && lz >= b.Z1 && lz <= b.Z2)
                    highest = Math.Max(highest, b.Y2);
            }
            if (double.IsNegativeInfinity(highest)) continue;
            surfaceY = by + highest;
            return true;
        }
        return false;
    }

    private bool HasClearance(Cuboidf box, double x, double y, double z)
    {
        if (x + box.X1 < 0 || x + box.X2 >= _mapSizeX ||
            z + box.Z1 < 0 || z + box.Z2 >= _mapSizeZ ||
            y + box.Y1 < 0 || y + box.Y2 >= _mapSizeY) return false;

        var ba = _sapi.World.BlockAccessor;
        return !_sapi.World.CollisionTester.IsColliding(ba, box, new Vec3d(x, y, z), false);
    }
    
    private void InitSeamSync()
    {
        _seamReady = false;
        if (!_config.EnableEdgeTerrainSync || !_dimensionsReady) return;

        _regionSize = _sapi.WorldManager.RegionSize;
        if (_regionSize <= 0) return;
        
        _sapi.WorldManager.SaveGame.WorldConfiguration?.SetString("worldEdge", "traversable");

        _genMaps = _sapi.ModLoader.GetModSystem<GenMaps>();
        _genRockStrata = _sapi.ModLoader.GetModSystem<GenRockStrataNew>();
        _genVeg = _sapi.ModLoader.GetModSystem<GenVegetationAndPatches>();
        _gcfg = GlobalConfig.GetInstance(_sapi);

        var noiseScale = Math.Max(1f, _mapSizeY / 256f);
        _terrainGenOctaves = TerraGenConfig.GetTerrainOctaveCount(_mapSizeY);
        long seed = _sapi.WorldManager.Seed;

        _terrainNoise = NewNormalizedSimplexFractalNoise.FromDefaultOctaves(
            _terrainGenOctaves, 0.0005 * NewSimplexNoiseLayer.OldToNewFrequency / noiseScale, 0.9, seed);

        _distort2Dx = new SimplexNoise(
            [55, 40, 30, 10],
            ScaleFreqs([1 / 5.0, 1 / 2.50, 1 / 1.250, 1 / 0.65], noiseScale),
            seed + 9876 + 0);

        _distort2Dz = new SimplexNoise(
            [55, 40, 30, 10],
            ScaleFreqs([1 / 5.0, 1 / 2.50, 1 / 1.250, 1 / 0.65], noiseScale),
            seed + 9876 + 2);

        _geoUpheavalNoise = new NormalizedSimplexNoise(
            [55, 40, 30, 15, 7, 4],
            ScaleFreqs([1.0 / 5.5, 1.1 / 2.75, 1.2 / 1.375, 1.2 / 0.715, 1.2 / 0.45, 1.2 / 0.25], noiseScale),
            seed + 9876 + 1);

        _terrainYThresholds = null;
        
        if (!_seamHooksRegistered)
        {
            _seamHooksRegistered = true;
            _sapi.Event.MapRegionGeneration(OnMapRegionGenSeamSync, "standard");
        }

        _seamReady = true;
        _sapi.Logger.Notification(
            "[projectglobe] Edge terrain seam sync active (EdgeSyncWidth={0}, TriggerMargin={1}, Poles={2}).",
            _config.EdgeSyncWidth, _config.TriggerMargin, _config.EnablePoleLogic);
    }

    private static double[] ScaleFreqs(double[] freqs, float scale)
    {
        for (var i = 0; i < freqs.Length; i++) freqs[i] /= scale;
        return freqs;
    }
    
    private static double SeamAlpha(double signedDistFromSeam, double syncWidth)
    {
        var t = Math.Clamp(0.5 + signedDistFromSeam / (2.0 * syncWidth), 0.0, 1.0);
        return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
    }
    
    private static double InteriorFade(double signedDistFromSeam, double innerStart, double syncWidth)
    {
        if (signedDistFromSeam <= innerStart) return 0.0;
        if (signedDistFromSeam >= syncWidth) return 1.0;
        var t = (signedDistFromSeam - innerStart) / Math.Max(1.0, syncWidth - innerStart);
        return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
    }

    private bool TryGetSeamX(double worldX, out double signedS, out double pairedX)
    {
        var margin = _config.TriggerMargin;
        var width = _config.EdgeSyncWidth;
        var period = _mapSizeX - 2.0 * margin;
        if (period <= 2.0 * width)
        {
            signedS = 0; pairedX = worldX;
            return false;
        }

        if (worldX < margin + width)
        {
            signedS = worldX - margin;
            pairedX = worldX + period;
            return true;
        }

        double eastSeam = _mapSizeX - margin;
        if (worldX > eastSeam - width)
        {
            signedS = eastSeam - worldX;
            pairedX = worldX - period;
            return true;
        }

        signedS = 0; pairedX = worldX;
        return false;
    }

    private bool TryGetPoleSeamZ(double worldX, double worldZ, out double signedSz, out double pairedPoleX, out double pairedPoleZ)
    {
        signedSz = 0; pairedPoleX = worldX; pairedPoleZ = worldZ;
        if (!_config.EnablePoleLogic) return false;

        var margin = _config.TriggerMargin;
        var width = _config.EdgeSyncWidth;
        var periodX = _mapSizeX - 2.0 * margin;
        var spanZ = _mapSizeZ - 2.0 * margin;
        if (periodX <= 0 || spanZ <= 2.0 * width) return false;

        if (worldZ < margin + width)
        {
            signedSz = worldZ - margin;
            pairedPoleZ = 2.0 * margin - worldZ;
            pairedPoleX = WrapSeamX(worldX + periodX * 0.5, margin, periodX);
            return true;
        }

        double southSeam = _mapSizeZ - margin;
        if (worldZ > southSeam - width)
        {
            signedSz = southSeam - worldZ;
            pairedPoleZ = 2.0 * southSeam - worldZ;
            pairedPoleX = WrapSeamX(worldX + periodX * 0.5, margin, periodX);
            return true;
        }

        return false;
    }

    private static double WrapSeamX(double x, double margin, double period) =>
        margin + (((x - margin) % period) + period) % period;
    
    private void OnMapRegionGenSeamSync(IMapRegion mapRegion, int regionX, int regionZ, ITreeAttribute? chunkGenParams = null)
    {
        if (!_seamReady || !_config.EnableEdgeTerrainSync || _genMaps == null) return;

        var maxPadBlocks = 5 * TerraGenConfig.oceanMapScale;
        var syncReach = _config.TriggerMargin + _config.EdgeSyncWidth + maxPadBlocks;

        var regMinX = regionX * _regionSize;
        var regMaxX = regMinX + _regionSize;
        var regMinZ = regionZ * _regionSize;
        var regMaxZ = regMinZ + _regionSize;

        var nearEw = regMinX < syncReach || regMaxX > _mapSizeX - syncReach;
        var nearNs = _config.EnablePoleLogic && (regMinZ < syncReach || regMaxZ > _mapSizeZ - syncReach);
        if (!nearEw && !nearNs) return;
        
        SyncRegionMap2D(mapRegion.ClimateMap, _genMaps.climateGen, regionX, regionZ, TerraGenConfig.climateMapScale, MapBlendMode.RgbColor);
        
        SyncRegionMap2D(mapRegion.OceanMap, _genMaps.oceanGen, regionX, regionZ, TerraGenConfig.oceanMapScale, MapBlendMode.Scalar);
        SyncRegionMap2D(mapRegion.UpheavelMap, _genMaps.upheavelGen, regionX, regionZ, TerraGenConfig.geoUpheavelMapScale, MapBlendMode.Scalar);
        SyncRegionMap2D(mapRegion.BeachMap, _genMaps.beachGen, regionX, regionZ, TerraGenConfig.beachMapScale, MapBlendMode.Scalar);
        
        SyncRegionMap2D(mapRegion.LandformMap, _genMaps.landformsGen, regionX, regionZ, TerraGenConfig.landformMapScale, MapBlendMode.DiscreteIndex);
        SyncRegionMap2D(mapRegion.GeologicProvinceMap, _genMaps.geologicprovinceGen, regionX, regionZ, TerraGenConfig.geoProvMapScale, MapBlendMode.DiscreteIndex);
        
        if (_genMaps.forestGen != null && mapRegion.ForestMap != null)
        {
            _genMaps.forestGen.SetInputMap(mapRegion.ClimateMap, mapRegion.ForestMap);
            SyncRegionMap2D(mapRegion.ForestMap, _genMaps.forestGen, regionX, regionZ, TerraGenConfig.forestMapScale, MapBlendMode.Scalar);
        }
        if (_genMaps.bushGen != null && mapRegion.ShrubMap != null)
        {
            _genMaps.bushGen.SetInputMap(mapRegion.ClimateMap, mapRegion.ShrubMap);
            SyncRegionMap2D(mapRegion.ShrubMap, _genMaps.bushGen, regionX, regionZ, TerraGenConfig.shrubMapScale, MapBlendMode.Scalar);
        }
        if (_genMaps.flowerGen != null && mapRegion.BiomeMap != null)
        {
            _genMaps.flowerGen.SetInputMap(mapRegion.ClimateMap, mapRegion.BiomeMap);
            SyncRegionMap2D(mapRegion.BiomeMap, _genMaps.flowerGen, regionX, regionZ, TerraGenConfig.forestMapScale, MapBlendMode.Scalar);
        }
        
        if (_genRockStrata != null && mapRegion.RockStrata != null &&
            RockStrataNoisesField?.GetValue(_genRockStrata) is MapLayerBase[] strataNoises)
        {
            var count = Math.Min(mapRegion.RockStrata.Length, strataNoises.Length);
            for (var i = 0; i < count; i++)
            {
                SyncRegionMap2D(mapRegion.RockStrata[i], strataNoises[i], regionX, regionZ, TerraGenConfig.rockStrataScale, MapBlendMode.Scalar);
            }
        }
        
        if (_genVeg?.blockPatchMapGens != null && mapRegion.BlockPatchMaps != null)
        {
            foreach (var kvp in _genVeg.blockPatchMapGens)
            {
                if (mapRegion.BlockPatchMaps.TryGetValue(kvp.Key, out var patchMap) && patchMap != null)
                {
                    SyncRegionMap2D(patchMap, kvp.Value, regionX, regionZ, TerraGenConfig.blockPatchesMapScale, MapBlendMode.Scalar);
                }
            }
        }

        mapRegion.DirtyForSaving = true;
    }

    private enum MapBlendMode
    {
        Scalar,
        RgbColor,
        DiscreteIndex
    }

    private void SyncRegionMap2D(
        IntDataMap2D? map,
        MapLayerBase? generator,
        int regionX,
        int regionZ,
        int pixelScaleBlocks,
        MapBlendMode mode)
    {
        if (map?.Data == null || map.Data.Length == 0 || generator == null || pixelScaleBlocks <= 0) return;

        var size = map.Size;
        var topLeftPad = map.TopLeftPadding;
        var innerSize = _regionSize / pixelScaleBlocks;
        var baseCoordX = regionX * innerSize - topLeftPad;
        var baseCoordZ = regionZ * innerSize - topLeftPad;

        var margin = _config.TriggerMargin;
        var width = _config.EdgeSyncWidth;
        var periodBlocksX = _mapSizeX - 2 * margin;
        var periodPixelsX = Math.Max(1, (int)Math.Round((double)periodBlocksX / pixelScaleBlocks));
        
        var regBlockMinX = baseCoordX * pixelScaleBlocks;
        var regBlockMaxX = (baseCoordX + size) * pixelScaleBlocks;
        if (regBlockMinX < margin + width || regBlockMaxX > _mapSizeX - margin - width)
        {
            var isWestSide = (regionX * _regionSize + _regionSize / 2) < _mapSizeX / 2;
            var pairedStartX = isWestSide ? baseCoordX + periodPixelsX : baseCoordX - periodPixelsX;
            var pairedData = generator.GenLayer(pairedStartX, baseCoordZ, size, size);

            for (var pz = 0; pz < size; pz++)
            {
                for (var px = 0; px < size; px++)
                {
                    var worldX = (baseCoordX + px + 0.5) * pixelScaleBlocks;
                    if (!TryGetSeamX(worldX, out var sx, out _)) continue;

                    var alpha = SeamAlpha(sx, width);
                    var idx = pz * size + px;
                    map.Data[idx] = BlendMapValue(map.Data[idx], pairedData[idx], alpha, isWestSide, mode);
                }
            }
        }
        
        if (!_config.EnablePoleLogic) return;

        var regBlockMinZ = baseCoordZ * pixelScaleBlocks;
        var regBlockMaxZ = (baseCoordZ + size) * pixelScaleBlocks;
        if (regBlockMinZ >= margin + width && regBlockMaxZ <= _mapSizeZ - margin - width) return;

        var isNorthPole = (regionZ * _regionSize + _regionSize / 2) < _mapSizeZ / 2;
        var halfPeriodPixelsX = periodPixelsX / 2;
        var marginPixelsX = (int)Math.Round((double)margin / pixelScaleBlocks);
        var relX = (((baseCoordX - marginPixelsX + halfPeriodPixelsX) % periodPixelsX) + periodPixelsX) % periodPixelsX;
        var pairedPoleStartX = marginPixelsX + relX;

        var seamPixelsZ = isNorthPole
            ? (int)Math.Round((double)margin / pixelScaleBlocks)
            : (int)Math.Round((double)(_mapSizeZ - margin) / pixelScaleBlocks);
        
        var pairedPoleStartZ = 2 * seamPixelsZ - (baseCoordZ + size - 1);
        var poleData = generator.GenLayer(pairedPoleStartX, pairedPoleStartZ, size, size);

        var canonicalHalf = ((regionX * _regionSize + _regionSize / 2) - margin) < periodBlocksX / 2;

        for (var pz = 0; pz < size; pz++)
        {
            var worldZ = (baseCoordZ + pz + 0.5) * pixelScaleBlocks;
            var sz = isNorthPole ? (worldZ - margin) : ((_mapSizeZ - margin) - worldZ);
            if (sz >= width) continue;

            var alpha = SeamAlpha(sz, width);
            var mirroredPz = size - 1 - pz;

            for (var px = 0; px < size; px++)
            {
                var idx = pz * size + px;
                var pairedIdx = mirroredPz * size + px;
                map.Data[idx] = BlendMapValue(map.Data[idx], poleData[pairedIdx], alpha, canonicalHalf, mode);
            }
        }
    }

    private static int BlendMapValue(int localVal, int pairedVal, double alpha, bool isCanonicalSide, MapBlendMode mode)
    {
        return mode switch
        {
            MapBlendMode.RgbColor => GameMath.LerpRgbColor((float)(1.0 - alpha), localVal, pairedVal),
            MapBlendMode.Scalar => (int)Math.Round(localVal * alpha + pairedVal * (1.0 - alpha)),
            MapBlendMode.DiscreteIndex => alpha > 0.5
                ? localVal
                : (alpha < 0.5 ? pairedVal : (isCanonicalSide ? localVal : pairedVal)),
            _ => localVal
        };
    }
    
    private void OnTerrainColumnSeamSync(IChunkColumnGenerateRequest request)
    {
        if (!_seamReady || !_config.EnableEdgeTerrainSync) return;
        if (_terrainNoise == null || _distort2Dx == null || _distort2Dz == null || _geoUpheavalNoise == null || _gcfg == null)
            return;

        var chunkX = request.ChunkX;
        var chunkZ = request.ChunkZ;
        var chunkMinX = chunkX * _chunkSize;
        var chunkMaxX = chunkMinX + _chunkSize;
        var chunkMinZ = chunkZ * _chunkSize;
        var chunkMaxZ = chunkMinZ + _chunkSize;

        var syncReach = _config.TriggerMargin + _config.EdgeSyncWidth;
        var nearEw = chunkMinX < syncReach || chunkMaxX > _mapSizeX - syncReach;
        var nearNs = _config.EnablePoleLogic && (chunkMinZ < syncReach || chunkMaxZ > _mapSizeZ - syncReach);
        if (!nearEw && !nearNs) return;

        var landforms = NoiseLandforms.landforms;
        if (landforms?.LandFormsByIndex == null) return;

        if (_terrainYThresholds == null || _terrainYThresholds.Length != landforms.LandFormsByIndex.Length)
        {
            _terrainYThresholds = new float[landforms.LandFormsByIndex.Length][];
            for (var i = 0; i < _terrainYThresholds.Length; i++)
                _terrainYThresholds[i] = landforms.LandFormsByIndex[i].TerrainYThresholds;
        }

        var chunks = request.Chunks;
        var mapChunk = chunks[0].MapChunk;
        var mapRegion = mapChunk.MapRegion;
        var regionChunkSize = _regionSize / _chunkSize;
        var rlX = chunkX % regionChunkSize;
        var rlZ = chunkZ % regionChunkSize;
        
        var lmap = mapRegion.LandformMap;
        var landLerpMap = new LerpedWeightedIndex2DMap(
            lmap.Data, lmap.Size, TerraGenConfig.landFormSmoothingRadius, lmap.TopLeftPadding, lmap.BottomRightPadding);

        var chunkPixelSize = (float)lmap.InnerSize / regionChunkSize;
        var baseX = rlX * chunkPixelSize;
        var baseZ = rlZ * chunkPixelSize;
        var step = chunkPixelSize / _chunkSize;

        var landformCount = landforms.LandFormsByIndex.Length;
        var weightsTmp = new float[landformCount];
        GetInterpolatedOctaves(landLerpMap.WeightsAt(baseX, baseZ, weightsTmp), landforms, out var octAmp0, out var octTh0);
        GetInterpolatedOctaves(landLerpMap.WeightsAt(baseX + chunkPixelSize, baseZ, weightsTmp), landforms, out var octAmp1, out var octTh1);
        GetInterpolatedOctaves(landLerpMap.WeightsAt(baseX, baseZ + chunkPixelSize, weightsTmp), landforms, out var octAmp2, out var octTh2);
        GetInterpolatedOctaves(landLerpMap.WeightsAt(baseX + chunkPixelSize, baseZ + chunkPixelSize, weightsTmp), landforms, out var octAmp3, out var octTh3);
        
        var oceanMap = mapRegion.OceanMap;
        int oUl = 0, oUr = 0, oBl = 0, oBr = 0;
        if (oceanMap?.Data is { Length: > 0 })
        {
            var ofac = (float)oceanMap.InnerSize / regionChunkSize;
            oUl = oceanMap.GetUnpaddedInt((int)(rlX * ofac), (int)(rlZ * ofac));
            oUr = oceanMap.GetUnpaddedInt((int)(rlX * ofac + ofac), (int)(rlZ * ofac));
            oBl = oceanMap.GetUnpaddedInt((int)(rlX * ofac), (int)(rlZ * ofac + ofac));
            oBr = oceanMap.GetUnpaddedInt((int)(rlX * ofac + ofac), (int)(rlZ * ofac + ofac));
        }

        var upheavalMap = mapRegion.UpheavelMap;
        int uUl = 0, uUr = 0, uBl = 0, uBr = 0;
        if (upheavalMap?.Data is { Length: > 0 })
        {
            var ufac = (float)upheavalMap.InnerSize / regionChunkSize;
            uUl = upheavalMap.GetUnpaddedInt((int)(rlX * ufac), (int)(rlZ * ufac));
            uUr = upheavalMap.GetUnpaddedInt((int)(rlX * ufac + ufac), (int)(rlZ * ufac));
            uBl = upheavalMap.GetUnpaddedInt((int)(rlX * ufac), (int)(rlZ * ufac + ufac));
            uBr = upheavalMap.GetUnpaddedInt((int)(rlX * ufac + ufac), (int)(rlZ * ufac + ufac));
        }

        var climateMap = mapRegion.ClimateMap;
        var cfac = (float)climateMap.InnerSize / regionChunkSize;
        var cUl = climateMap.GetUnpaddedInt((int)(rlX * cfac), (int)(rlZ * cfac));
        var cUr = climateMap.GetUnpaddedInt((int)(rlX * cfac + cfac), (int)(rlZ * cfac));
        var cBl = climateMap.GetUnpaddedInt((int)(rlX * cfac), (int)(rlZ * cfac + cfac));
        var cBr = climateMap.GetUnpaddedInt((int)(rlX * cfac + cfac), (int)(rlZ * cfac + cfac));

        var oceanicityFac = _mapSizeY / 256f * 0.33333f;
        var lerpedAmps = new double[_terrainGenOctaves];
        var lerpedTh = new double[_terrainGenOctaves];

        var terrainHeightMap = mapChunk.WorldGenTerrainHeightMap;
        var rainHeightMap = mapChunk.RainHeightMap;
        var anyColumnAdjusted = false;

        _genRockStrata?.preLoad(chunks, chunkX, chunkZ);

        for (var lz = 0; lz < _chunkSize; lz++)
        {
            var worldZ = chunkMinZ + lz;
            var fz = (float)lz / _chunkSize;

            for (var lx = 0; lx < _chunkSize; lx++)
            {
                var worldX = chunkMinX + lx;
                var hasX = TryGetSeamX(worldX, out var sx, out var pairedX);
                var hasZ = TryGetPoleSeamZ(worldX, worldZ, out var sz, out var poleX, out var poleZ);
                if (!hasX && !hasZ) continue;

                var fx = (float)lx / _chunkSize;
                landLerpMap.WeightsAt(baseX + lx * step, baseZ + lz * step, weightsTmp);
                for (var i = 0; i < _terrainGenOctaves; i++)
                {
                    lerpedAmps[i] = GameMath.BiLerp(octAmp0[i], octAmp1[i], octAmp2[i], octAmp3[i], fx, fz);
                    lerpedTh[i] = GameMath.BiLerp(octTh0[i], octTh1[i], octTh2[i], octTh3[i], fx, fz);
                }

                var upheavalStrength = GameMath.BiLerp(uUl, uUr, uBl, uBr, fx, fz);
                var oceanicity = GameMath.BiLerp(oUl, oUr, oBl, oBr, fx, fz) * oceanicityFac;

                var hLocalDet = SampleDeterministicColumnY(worldX, worldZ, lerpedAmps, lerpedTh, weightsTmp, oceanicity, upheavalStrength);
                double hSeam = hLocalDet;
                var minSignedDist = double.MaxValue;

                if (hasX && !hasZ)
                {
                    var hPairedX = SampleDeterministicColumnY(pairedX, worldZ, lerpedAmps, lerpedTh, weightsTmp, oceanicity, upheavalStrength);
                    var alphaX = SeamAlpha(sx, _config.EdgeSyncWidth);
                    hSeam = alphaX * hLocalDet + (1.0 - alphaX) * hPairedX;
                    minSignedDist = sx;
                }
                else if (!hasX && hasZ)
                {
                    var hPairedZ = SampleDeterministicColumnY(poleX, poleZ, lerpedAmps, lerpedTh, weightsTmp, oceanicity, upheavalStrength);
                    var alphaZ = SeamAlpha(sz, _config.EdgeSyncWidth);
                    hSeam = alphaZ * hLocalDet + (1.0 - alphaZ) * hPairedZ;
                    minSignedDist = sz;
                }
                else if (hasX && hasZ)
                {
                    var hPairedX = SampleDeterministicColumnY(pairedX, worldZ, lerpedAmps, lerpedTh, weightsTmp, oceanicity, upheavalStrength);
                    var alphaX = SeamAlpha(sx, _config.EdgeSyncWidth);
                    var ewLocal = alphaX * hLocalDet + (1.0 - alphaX) * hPairedX;

                    TryGetSeamX(poleX, out _, out var pairedPoleX);
                    var hPoleLocal = SampleDeterministicColumnY(poleX, poleZ, lerpedAmps, lerpedTh, weightsTmp, oceanicity, upheavalStrength);
                    var hPolePairedX = SampleDeterministicColumnY(pairedPoleX, poleZ, lerpedAmps, lerpedTh, weightsTmp, oceanicity, upheavalStrength);
                    var ewPole = alphaX * hPoleLocal + (1.0 - alphaX) * hPolePairedX;

                    var alphaZ = SeamAlpha(sz, _config.EdgeSyncWidth);
                    hSeam = alphaZ * ewLocal + (1.0 - alphaZ) * ewPole;
                    minSignedDist = Math.Min(sx, sz);
                }

                var mapIdx = lz * _chunkSize + lx;
                int genTerraY = terrainHeightMap[mapIdx];
                
                var interiorWeight = InteriorFade(minSignedDist, _config.TriggerMargin + 8, _config.EdgeSyncWidth);
                var targetY = (int)Math.Round(hSeam * (1.0 - interiorWeight) + genTerraY * interiorWeight);
                targetY = Math.Clamp(targetY, 1, _mapSizeY - 2);

                var climate = GameMath.BiLerpRgbColor(fx, fz, cUl, cUr, cBl, cBr);
                ApplyColumnHeightAndWater(chunks, lx, lz, targetY, oceanicity, climate, terrainHeightMap, rainHeightMap);
                _genRockStrata?.genBlockColumn(chunks, chunkX, chunkZ, lx, lz);
                anyColumnAdjusted = true;
            }
        }

        if (!anyColumnAdjusted) return;
        {
            ushort yMax = 0;
            foreach (var t in rainHeightMap)
                if (t > yMax) yMax = t;

            mapChunk.YMax = yMax;
        }
    }

    private int SampleDeterministicColumnY(
        double worldX,
        double worldZ,
        double[] lerpedAmps,
        double[] lerpedTh,
        float[] landformWeights,
        float oceanicity,
        float upheavalStrength)
    {
        const double terrainDistMul = 4.0;
        const double terrainDistThresh = 40.0;
        const double geoDistMul = 10.0;
        const double geoDistThresh = 10.0;
        var maxDist = (55 + 40 + 30 + 10) * SimplexNoiseOctave.MAX_VALUE_2D_WARP;

        SimplexNoise.NoiseFairWarpVector(_distort2Dx!, _distort2Dz!, worldX / 400.0, worldZ / 400.0, out var dx, out var dz);
        ApplyIsotropicDistortionThreshold(dx * terrainDistMul, dz * terrainDistMul, terrainDistThresh, terrainDistMul * maxDist, out var tdx, out var tdz);
        ApplyIsotropicDistortionThreshold(dx * geoDistMul, dz * geoDistMul, geoDistThresh, geoDistMul * maxDist, out var gdx, out var gdz);

        var upNoise = (float)_geoUpheavalNoise!.Noise((worldX + gdx) / 400.0, (worldZ + gdz) / 400.0) * 0.9f;
        var distY = oceanicity + upheavalStrength * Math.Min(0f, 0.5f - upNoise);

        var vertFreq = 0.5 / TerraGenConfig.terrainNoiseVerticalScale;
        var colNoise = _terrainNoise!.ForColumn(vertFreq, lerpedAmps, lerpedTh, worldX + tdx, worldZ + tdz);
        var boundMin = colNoise.BoundMin;
        var boundMax = colNoise.BoundMax;

        var mapSizeYm2 = _mapSizeY - 2;
        var taperThreshold = (int)(_mapSizeY * 0.9f);
        var slide = distY - (int)Math.Floor(distY);
        var surfaceY = 1;

        for (var posY = 1; posY <= mapSizeYm2; posY++)
        {
            var yBase = GameMath.Clamp((int)Math.Floor(posY + distY), 0, mapSizeYm2);
            double threshold = 0;
            for (var i = 0; i < landformWeights.Length; i++)
            {
                var w = landformWeights[i];
                if (w == 0) continue;
                var thArr = _terrainYThresholds![i];
                threshold += w * GameMath.Lerp(thArr[yBase], thArr[yBase + 1], slide);
            }

            if (posY > taperThreshold && distY < -2f)
            {
                double upAmt = GameMath.Clamp(-distY, posY - _mapSizeY, posY);
                threshold += (posY - taperThreshold) * upAmt / (40.0 * 255.0);
            }

            if (threshold <= boundMin)
            {
                surfaceY = posY;
            }
            else if (!(threshold < boundMax))
            {
                break;
            }
            else
            {
                var sign = -NormalizedSimplexNoise.NoiseValueCurveInverse(threshold);
                if (colNoise.NoiseSign(posY, sign) > 0)
                    surfaceY = posY;
            }
        }

        return surfaceY;
    }

    private void ApplyColumnHeightAndWater(
        IServerChunk[] chunks,
        int lx,
        int lz,
        int targetSurfaceY,
        float oceanicity,
        int climateRgb,
        ushort[] terrainHeightMap,
        ushort[] rainHeightMap)
    {
        var mapIdx = lz * _chunkSize + lx;
        int oldSurfaceY = terrainHeightMap[mapIdx];
        var seaLevel = TerraGenConfig.seaLevel;
        var rockId = _gcfg!.defaultRockId;
        var waterId = oceanicity > 1f ? _gcfg.saltWaterBlockId : _gcfg.waterBlockId;
        var surfaceWaterId = waterId;

        if (targetSurfaceY < seaLevel - 1 && waterId != _gcfg.saltWaterBlockId)
        {
            var unscaledTemp = (climateRgb >> 16) & 0xFF;
            var tempC = Climate.GetScaledAdjustedTemperatureFloat(unscaledTemp, 0);
            if (tempC < TerraGenConfig.WaterFreezingTempOnGen)
                surfaceWaterId = _gcfg.lakeIceBlockId;
        }

        var maxCheckY = Math.Min(_mapSizeY - 2, Math.Max(Math.Max(oldSurfaceY, targetSurfaceY), seaLevel));
        for (var posY = 1; posY <= maxCheckY; posY++)
        {
            var cy = posY / _chunkSize;
            var ly = posY % _chunkSize;
            var idx3D = (ly * _chunkSize + lz) * _chunkSize + lx;
            var data = chunks[cy].Data;

            if (posY <= targetSurfaceY)
            {
                data[idx3D] = rockId;
                data.SetFluid(idx3D, 0);
            }
            else if (posY < seaLevel)
            {
                data[idx3D] = 0;
                data.SetFluid(idx3D, posY == seaLevel - 1 ? surfaceWaterId : waterId);
            }
            else
            {
                data[idx3D] = 0;
                data.SetFluid(idx3D, 0);
            }
        }

        terrainHeightMap[mapIdx] = (ushort)targetSurfaceY;
        rainHeightMap[mapIdx] = (ushort)Math.Max(targetSurfaceY, seaLevel - 1);
    }

    private void GetInterpolatedOctaves(float[] indices, LandformsWorldProperty landforms, out double[] amps, out double[] thresholds)
    {
        amps = new double[_terrainGenOctaves];
        thresholds = new double[_terrainGenOctaves];
        for (var oct = 0; oct < _terrainGenOctaves; oct++)
        {
            double a = 0, t = 0;
            for (var i = 0; i < indices.Length; i++)
            {
                var w = indices[i];
                if (w == 0) continue;
                var lf = landforms.LandFormsByIndex[i];
                a += lf.TerrainOctaves[oct] * w;
                t += lf.TerrainOctaveThresholds[oct] * w;
            }
            amps[oct] = a;
            thresholds[oct] = t;
        }
    }

    private static void ApplyIsotropicDistortionThreshold(double dx, double dz, double threshold, double maximum, out double outX, out double outZ)
    {
        var magSq = dx * dx + dz * dz;
        var thSq = threshold * threshold;
        if (magSq <= thSq)
        {
            outX = 0; outZ = 0;
            return;
        }
        var baseCurve = (magSq - thSq) / magSq;
        var maxSq = maximum * maximum;
        var slide = baseCurve * (maxSq / (maxSq - thSq));
        slide *= slide;
        var scale = slide * ((maximum - threshold) / maximum);
        outX = dx * scale;
        outZ = dz * scale;
    }
    
        private static double PolarLandSmooth01(double value)
    {
        var t = Math.Clamp(value, 0.0, 1.0);
        return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
    }

    private void OnPolarLandMapRegionGen(
        IMapRegion mapRegion,
        int regionX,
        int regionZ,
        ITreeAttribute? chunkGenParams = null)
    {
        if (!_config.EnablePolarLandBias || !_dimensionsReady || _mapSizeZ <= 0)
            return;

        var oceanMap = mapRegion.OceanMap;
        if (oceanMap?.Data == null || oceanMap.Data.Length == 0 || oceanMap.Size <= 0)
            return;

        var halfMapZ = _mapSizeZ / 2.0;
        if (halfMapZ <= 0) return;

        double corePercent = _config.PolarLandCorePercent;
        double transitionPercent = _config.PolarLandTransitionPercent;
        var pixelScale = TerraGenConfig.oceanMapScale;

        if (pixelScale <= 0) return;

        var changed = false;
        
        for (var localZ = 0; localZ < oceanMap.Size; localZ++)
        {
            var globalPixelZ =
                (double)regionZ * oceanMap.InnerSize +
                localZ -
                oceanMap.TopLeftPadding;

            var worldZ = (globalPixelZ + 0.5) * pixelScale;
            worldZ = Math.Clamp(worldZ, 0.0, _mapSizeZ);

            var distanceFromPole = Math.Min(worldZ, _mapSizeZ - worldZ);
            var percentFromPole = distanceFromPole / halfMapZ * 100.0;

            double landBias;
            if (percentFromPole <= corePercent)
            {
                landBias = 1.0;
            }
            else if (percentFromPole >= transitionPercent ||
                     transitionPercent <= corePercent)
            {
                landBias = 0.0;
            }
            else
            {
                var t = (percentFromPole - corePercent) /
                        (transitionPercent - corePercent);
                
                landBias = 1.0 - PolarLandSmooth01(t);
            }

            if (landBias <= 0.0) continue;

            var rowStart = localZ * oceanMap.Size;
            var rowEnd = Math.Min(rowStart + oceanMap.Size, oceanMap.Data.Length);

            for (var index = rowStart; index < rowEnd; index++)
            {
                var originalOceanValue = oceanMap.Data[index];
                
                var biasedValue = (int)Math.Round(
                    originalOceanValue * (1.0 - landBias));

                biasedValue = Math.Clamp(biasedValue, 0, 255);

                if (biasedValue == originalOceanValue) continue;

                oceanMap.Data[index] = biasedValue;
                changed = true;
            }
        }

        if (changed)
        {
            mapRegion.DirtyForSaving = true;
        }
    }
}
