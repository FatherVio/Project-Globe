using System;
using System.IO;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace projectglobe;

public sealed class ProjectGlobeConfig
{
    private const string FileName = "ProjectGlobeConfig.json";
    private const int CurrentVersion = 2;

    public int ConfigVersion = CurrentVersion;
    
    public int TriggerMargin = 1; //Blocks from the border at which a crossing triggers.
    
    public int PreloadDistance = 256; //How far from the border (blocks) the destination starts loading.
    
    public bool EnablePoleLogic = true; //false = the north/south edges are left to vanilla behavior (wall or void).
    
    public bool EnableEdgeRescue = true; //Put players back on the map if they end up outside it or below the world.
    
    public int RescueBelowY = -16;

    public bool EnableEdgeTerrainSync = false; // !!!Experimental. Touch at your own risk!!! Currently not functional.
    
    public int EdgeSyncWidth = 256; //How far in from the seam (blocks) the bias fades out.
    
    public bool EnableGlobeClimate = false; // Force 1 equator at map center and 2 poles at N/S borders.
    
    public bool EnableInitialLatitudeTeleport = false; // Teleport newly spawned players from equator to startingClimate latitude once.
    
    public int EquatorSpawnLoadRadius = 2; // Chunk radius around equator spawn that must generate before teleporting.
    
    public int LatitudeDestinationLoadRadius = 2; // Chunk radius to generate/load around the target latitude before teleporting.
    
    public int LatitudeArrivalSearchRadius = 16; // Block search radius for a clear landing spot at the target latitude.
    
    public bool EnablePolarLandBias = false;
    
    public int PolarLandCorePercent = 5;
    public int PolarLandTransitionPercent = 10;

    public static ProjectGlobeConfig Load(ICoreServerAPI api)
    {
        var path = Path.Combine(api.GetOrCreateDataPath("ModConfig"), FileName);

        ProjectGlobeConfig? cfg = null;
        var unreadable = false;

        if (File.Exists(path))
        {
            try
            {
                cfg = api.LoadModConfig<ProjectGlobeConfig>(FileName);
            }
            catch (Exception e)
            {
                unreadable = true;
                api.Logger.Error(
                    "[projectglobe] Could not read {0}: {1}. Using defaults for this session and leaving the file alone.",
                    FileName, e.Message);
            }
        }

        cfg ??= new ProjectGlobeConfig();
        cfg.Validate(api.Logger);
        
        if (!unreadable) api.StoreModConfig(cfg, FileName);

        return cfg;
    }

    private void Validate(ILogger log)
    {
        TriggerMargin = Limit(nameof(TriggerMargin), TriggerMargin, 1, 64, log);
        PreloadDistance = Limit(nameof(PreloadDistance), PreloadDistance, 0, 2048, log);
        EdgeSyncWidth = Limit(nameof(EdgeSyncWidth), EdgeSyncWidth, 32, 2048, log);
        EquatorSpawnLoadRadius = Limit(nameof(EquatorSpawnLoadRadius), EquatorSpawnLoadRadius, 1, 8, log);
        LatitudeDestinationLoadRadius = Limit(nameof(LatitudeDestinationLoadRadius), LatitudeDestinationLoadRadius, 1, 8, log);
        LatitudeArrivalSearchRadius = Limit(nameof(LatitudeArrivalSearchRadius), LatitudeArrivalSearchRadius, 4, 64, log);
        RescueBelowY = Limit(nameof(RescueBelowY), RescueBelowY, -512, 0, log);
        PolarLandCorePercent = Limit(nameof(PolarLandCorePercent), PolarLandCorePercent, 1, 40, log);
        PolarLandTransitionPercent = Limit(nameof(PolarLandTransitionPercent), PolarLandTransitionPercent, 1, 70, log);

        if (PolarLandTransitionPercent < PolarLandCorePercent)
        {
            log.Warning(
                "[projectglobe] PolarLandTransitionPercent must be at least PolarLandCorePercent; raising it to {0}.",
                PolarLandCorePercent);
            PolarLandTransitionPercent = PolarLandCorePercent;
        }
        ConfigVersion = CurrentVersion;
    }

    private static int Limit(string name, int value, int min, int max, ILogger log)
    {
        var clamped = Math.Clamp(value, min, max);
        if (clamped != value)
            log.Warning("[projectglobe] {0} = {1} is out of range ({2}..{3}), using {4}.", name, value, min, max, clamped);
        return clamped;
    }
}
