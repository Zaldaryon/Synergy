using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace Synergy.Server
{
    /// <summary>
    /// Reduces auto-save freeze by deferring DB writes to AFTER the server resumes.
    ///
    /// Prefix on OnWorldBeingSaved: serialize inside suspend, skip DB writes.
    /// Postfix on doAutoSave: write deferred byte[] after Suspend(false) + Monitor.Exit.
    ///
    /// All reflection is cached at Initialize() — zero per-call AccessTools lookups.
    /// </summary>
    public static class SaveOptimization
    {
        private static ICoreServerAPI sapi;
        private static int errorCount;
        private static bool disabled;

        private static volatile List<(string uid, byte[] data)> deferredPlayers;
        private static volatile List<(long key, byte[] data)> deferredRegions;

        // --- ALL cached at Initialize() ---

        // LoadAndSaveGame fields
        private static AccessTools.FieldRef<object, object> lsg_server;
        private static AccessTools.FieldRef<object, object> lsg_chunkthread;
        private static AccessTools.FieldRef<object, bool> lsg_ignoreSave;
        private static FieldInfo lsg_savingLock;
        private static PropertyInfo lsg_reusableStream;

        // ServerMain fields
        private static FieldInfo sm_runPhase;
        private static FieldInfo sm_saveGameData;
        private static FieldInfo sm_playerDataManager;
        private static FieldInfo sm_loadedMapRegions;
        private static FieldInfo sm_worldMap;
        private static FieldInfo sm_config;
        private static FieldInfo sm_xPlatInterface; // static
        private static FieldInfo sm_chunkThread;

        // Config
        private static FieldInfo cfg_dieBelowDiskSpaceMb;

        // ChunkServerThread
        private static FieldInfo ct_runOffThreadSaveNow;
        private static FieldInfo ct_gameDatabase;

        // GameDatabase
        private static PropertyInfo gdb_databaseFilename;
        private static MethodInfo gdb_setPlayerData;
        private static MethodInfo gdb_setMapRegions;

        // xPlatInterface
        private static MethodInfo xplat_getFreeDiskSpace;

        // PlayerDataManager
        private static FieldInfo pdm_worldDataByUID;

        // ServerWorldPlayerData
        private static FieldInfo swpd_playerUID;
        private static MethodInfo swpd_beforeSerialization;

        // MapRegion
        private static AccessTools.FieldRef<object, bool> mr_dirtyForSaving;
        private static MethodInfo mr_toBytes;
        private static MethodInfo wm_mapRegionPosFromIndex2D;

        // DbChunk
        private static Type dbChunkType;
        private static FieldInfo dbc_position;
        private static FieldInfo dbc_data;

        // Serialize with reusable stream
        private static MethodInfo serializeWithStream;

        // WillSave
        private static MethodInfo sg_willSave;

        // PopulateChunksCopy
        private delegate void PopulateChunksCopyFn(object instance);
        private static PopulateChunksCopyFn populateChunksCopy;

        // ServerCoreAPI → ServerMain
        private static FieldInfo sapi_server;

        public static void Initialize(ICoreServerAPI api, Harmony harmony)
        {
            sapi = api;
            errorCount = 0;
            disabled = false;

            var loadSaveType = AccessTools.TypeByName("Vintagestory.Server.ServerSystemLoadAndSaveGame");
            var autoSaveType = AccessTools.TypeByName("Vintagestory.Server.ServerSystemAutoSaveGame");
            var serverMainType = AccessTools.TypeByName("Vintagestory.Server.ServerMain");
            var chunkThreadType = AccessTools.TypeByName("Vintagestory.Server.ChunkServerThread");
            var gameDatabaseType = AccessTools.TypeByName("Vintagestory.Common.GameDatabase");
            var mapRegionType = AccessTools.TypeByName("Vintagestory.Server.ServerMapRegion");
            var saveGameType = AccessTools.TypeByName("Vintagestory.Common.SaveGame");
            var playerDataType = AccessTools.TypeByName("Vintagestory.Server.ServerWorldPlayerData");
            var playerDataMgrType = AccessTools.TypeByName("Vintagestory.Server.PlayerDataManager");
            var fastMemStreamType = AccessTools.TypeByName("Vintagestory.API.Datastructures.FastMemoryStream");
            var worldMapType = AccessTools.TypeByName("Vintagestory.Server.ServerWorldMap");
            var configType = AccessTools.TypeByName("Vintagestory.Common.ServerConfig") ?? AccessTools.TypeByName("ServerConfig");
            dbChunkType = AccessTools.TypeByName("Vintagestory.Common.Database.DbChunk");

            if (loadSaveType == null || autoSaveType == null || serverMainType == null)
            { api.Logger.Warning("[Synergy] SaveOptimize: Types not found, skipping"); return; }

            var onWorldBeingSaved = AccessTools.Method(loadSaveType, "OnWorldBeingSaved");
            var doAutoSave = AccessTools.Method(autoSaveType, "doAutoSave");
            if (onWorldBeingSaved == null || doAutoSave == null)
            { api.Logger.Warning("[Synergy] SaveOptimize: Methods not found, skipping"); return; }
            if (!ConflictDetector.IsSafeToPatch(onWorldBeingSaved, SynergyMod.HarmonyId, api.Logger)) return;
            if (!ConflictDetector.IsSafeToPatch(doAutoSave, SynergyMod.HarmonyId, api.Logger)) return;

            // Cache EVERYTHING at init
            lsg_server = AccessTools.FieldRefAccess<object>(loadSaveType, "server");
            lsg_chunkthread = AccessTools.FieldRefAccess<object>(loadSaveType, "chunkthread");
            lsg_ignoreSave = AccessTools.FieldRefAccess<bool>(loadSaveType, "ignoreSave");
            lsg_savingLock = AccessTools.Field(loadSaveType, "savingLock");
            lsg_reusableStream = AccessTools.Property(loadSaveType, "reusableStream");

            sm_runPhase = AccessTools.Field(serverMainType, "RunPhase");
            sm_saveGameData = AccessTools.Field(serverMainType, "SaveGameData");
            sm_playerDataManager = AccessTools.Field(serverMainType, "PlayerDataManager");
            sm_loadedMapRegions = AccessTools.Field(serverMainType, "loadedMapRegions");
            sm_worldMap = AccessTools.Field(serverMainType, "WorldMap");
            sm_config = AccessTools.Field(serverMainType, "Config");
            sm_xPlatInterface = AccessTools.Field(serverMainType, "xPlatInterface");
            sm_chunkThread = AccessTools.Field(serverMainType, "chunkThread");

            if (configType != null) cfg_dieBelowDiskSpaceMb = AccessTools.Field(configType, "DieBelowDiskSpaceMb");

            ct_runOffThreadSaveNow = AccessTools.Field(chunkThreadType, "runOffThreadSaveNow");
            ct_gameDatabase = AccessTools.Field(chunkThreadType, "gameDatabase");

            if (gameDatabaseType != null)
            {
                gdb_databaseFilename = AccessTools.Property(gameDatabaseType, "DatabaseFilename");
                gdb_setPlayerData = AccessTools.Method(gameDatabaseType, "SetPlayerData", new[] { typeof(string), typeof(byte[]) });
                gdb_setMapRegions = AccessTools.Method(gameDatabaseType, "SetMapRegions");
            }

            if (playerDataMgrType != null) pdm_worldDataByUID = AccessTools.Field(playerDataMgrType, "WorldDataByUID");
            if (playerDataType != null)
            {
                swpd_playerUID = AccessTools.Field(playerDataType, "PlayerUID");
                swpd_beforeSerialization = AccessTools.Method(playerDataType, "BeforeSerialization");
            }

            if (mapRegionType != null)
            {
                mr_dirtyForSaving = AccessTools.FieldRefAccess<bool>(mapRegionType, "DirtyForSaving");
                mr_toBytes = AccessTools.Method(mapRegionType, "ToBytes", new[] { fastMemStreamType });
            }
            if (worldMapType != null)
                wm_mapRegionPosFromIndex2D = AccessTools.Method(worldMapType, "MapRegionPosFromIndex2D", new[] { typeof(long) });

            if (dbChunkType != null)
            {
                dbc_position = AccessTools.Field(dbChunkType, "Position");
                dbc_data = AccessTools.Field(dbChunkType, "Data");
            }

            if (saveGameType != null) sg_willSave = AccessTools.Method(saveGameType, "WillSave");

            // Resolve SerializerUtil.Serialize<T>(T, FastMemoryStream) for ServerWorldPlayerData
            if (playerDataType != null && fastMemStreamType != null)
            {
                var genericSerialize = typeof(SerializerUtil).GetMethod("Serialize",
                    new[] { typeof(object), fastMemStreamType });
                // Try to find the generic version and make it concrete
                foreach (var m in typeof(SerializerUtil).GetMethods())
                {
                    if (m.Name == "Serialize" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2)
                    {
                        var p = m.GetParameters();
                        if (p[1].ParameterType == fastMemStreamType)
                        {
                            serializeWithStream = m.MakeGenericMethod(playerDataType);
                            break;
                        }
                    }
                }
            }

            sapi_server = AccessTools.Field(api.GetType(), "server");

            // Find xPlatInterface.GetFreeDiskSpace at init
            var xPlat = sm_xPlatInterface?.GetValue(null);
            if (xPlat != null)
                xplat_getFreeDiskSpace = AccessTools.Method(xPlat.GetType(), "GetFreeDiskSpace", new[] { typeof(string) });

            // IL emit PopulateChunksCopy
            var popMethod = AccessTools.Method(loadSaveType, "PopulateChunksCopy");
            if (popMethod != null)
            {
                var dm = new DynamicMethod("Call_PopulateChunksCopy", typeof(void),
                    new[] { typeof(object) }, loadSaveType, true);
                var il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, loadSaveType);
                il.Emit(OpCodes.Call, popMethod);
                il.Emit(OpCodes.Ret);
                populateChunksCopy = (PopulateChunksCopyFn)dm.CreateDelegate(typeof(PopulateChunksCopyFn));
            }

            if (populateChunksCopy == null || gdb_setPlayerData == null)
            { api.Logger.Warning("[Synergy] SaveOptimize: Required methods not resolved, skipping"); return; }

            harmony.Patch(onWorldBeingSaved,
                prefix: new HarmonyMethod(typeof(SaveOptimization), nameof(Prefix_OnWorldBeingSaved)));
            harmony.Patch(doAutoSave,
                postfix: new HarmonyMethod(typeof(SaveOptimization), nameof(Postfix_doAutoSave)));

            api.Logger.Notification("[Synergy] SaveOptimize: Active");
        }

        public static bool Prefix_OnWorldBeingSaved(object __instance)
        {
            if (disabled) return true;

            try
            {
                if (lsg_ignoreSave(__instance)) return false;

                var server = lsg_server(__instance);
                var chunkthread = lsg_chunkthread(__instance);
                var savingLock = lsg_savingLock?.GetValue(__instance);
                if (server == null || chunkthread == null || savingLock == null) return true;

                // Only optimize auto-save, not shutdown
                if (sm_runPhase?.GetValue(server)?.ToString() == "Shutdown") return true;
                if ((bool)(ct_runOffThreadSaveNow?.GetValue(chunkthread) ?? false)) return true;

                // Disk space: if low, let vanilla handle (includes server.Stop if critical)
                try
                {
                    var gameDb = ct_gameDatabase?.GetValue(chunkthread);
                    var dbFilename = gdb_databaseFilename?.GetValue(gameDb) as string;
                    if (!string.IsNullOrEmpty(dbFilename) && xplat_getFreeDiskSpace != null)
                    {
                        var xPlat = sm_xPlatInterface?.GetValue(null);
                        long freeSpace = (long)(xplat_getFreeDiskSpace.Invoke(xPlat, new object[] { System.IO.Path.GetDirectoryName(dbFilename) }) ?? -1L);
                        var config = sm_config?.GetValue(server);
                        int dieBelowMb = cfg_dieBelowDiskSpaceMb != null ? (int)cfg_dieBelowDiskSpaceMb.GetValue(config) : 50;
                        if (freeSpace >= 0 && freeSpace < 1048576L * dieBelowMb * 2)
                            return true;
                    }
                }
                catch { }

                lock (savingLock)
                {
                    long suspendStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var stream = lsg_reusableStream?.GetValue(__instance);
                    if (stream == null) return true;

                    var gameDb = ct_gameDatabase?.GetValue(chunkthread);
                    if (gameDb == null) return true;

                    sapi.World.FrameProfiler.Mark("savegameworld-begin");
                    sapi.Logger.Event("[Synergy] SaveOptimize: Saving (optimized)...");
                    sapi.Logger.StoryEvent("It pauses.");

                    // 1. WillSave (serialize save game header)
                    sg_willSave?.Invoke(sm_saveGameData?.GetValue(server), new[] { stream });
                    sapi.World.FrameProfiler.Mark("savegameworld-mid-1");
                    sapi.Logger.StoryEvent("One last gaze...");

                    // 2. Serialize AND write players to DB inline (crash-safe, like vanilla)
                    var pdm = sm_playerDataManager?.GetValue(server);
                    var worldData = pdm != null ? pdm_worldDataByUID?.GetValue(pdm) as IDictionary : null;
                    int playerCount = 0;

                    if (worldData != null && swpd_beforeSerialization != null)
                    {
                        foreach (DictionaryEntry entry in worldData)
                        {
                            try
                            {
                                swpd_beforeSerialization.Invoke(entry.Value, null);
                                byte[] bytes;
                                if (serializeWithStream != null)
                                    bytes = (byte[])serializeWithStream.Invoke(null, new[] { entry.Value, stream });
                                else
                                    bytes = SerializerUtil.Serialize(entry.Value);
                                var uid = (string)swpd_playerUID?.GetValue(entry.Value);
                                if (uid != null && bytes != null)
                                {
                                    gdb_setPlayerData.Invoke(gameDb, new object[] { uid, bytes });
                                    playerCount++;
                                }
                            }
                            catch (Exception e)
                            {
                                sapi.Logger.Error("[Synergy] SaveOptimize: Player save: {0}", e.Message);
                            }
                        }
                    }
                    sapi.World.FrameProfiler.Mark("savegameworld-mid-2");
                    sapi.Logger.Event("[Synergy] SaveOptimize: Saved {0} players to DB (inline)", playerCount);

                    // 3. Serialize map regions (defer DB write to post-resume — regions are regenerable)
                    var mapRegions = sm_loadedMapRegions?.GetValue(server) as IDictionary;
                    var regions = new List<(long key, byte[] data)>();

                    if (mapRegions != null && mr_dirtyForSaving != null)
                    {
                        foreach (DictionaryEntry entry in mapRegions)
                        {
                            if (mr_dirtyForSaving(entry.Value))
                            {
                                mr_dirtyForSaving(entry.Value) = false;
                                var data = mr_toBytes?.Invoke(entry.Value, new[] { stream }) as byte[];
                                if (data != null)
                                    regions.Add(((long)entry.Key, data));
                            }
                        }
                    }
                    sapi.World.FrameProfiler.Mark("savegameworld-mid-3");
                    sapi.Logger.Event("[Synergy] SaveOptimize: Serialized {0} map regions (DB write deferred)", regions.Count);
                    sapi.Logger.StoryEvent("...then all goes quiet");

                    // 4. PopulateChunksCopy
                    populateChunksCopy(__instance);
                    sapi.World.FrameProfiler.Mark("savegameworld-mid-5");
                    sapi.Logger.StoryEvent("The waters recede...");

                    // 5. Store deferred regions (postfix writes after resume)
                    deferredPlayers = null; // players already written
                    deferredRegions = regions;
                    sapi.World.FrameProfiler.Mark("savegameworld-end");
                    sapi.Logger.StoryEvent("It sighs...");

                    long suspendMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - suspendStart;
                    SynergyMetrics.RecordSaveSuspendTime(suspendMs);
                }

                return false;
            }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref errorCount) >= 5)
                {
                    disabled = true;
                    sapi?.Logger.Warning("[Synergy] SaveOptimize: Auto-disabled: {0}", ex.Message);
                }
                return true;
            }
        }

        public static void Postfix_doAutoSave()
        {
            var players = Interlocked.Exchange(ref deferredPlayers, null);
            var regions = Interlocked.Exchange(ref deferredRegions, null);
            if (players == null && regions == null) return;

            object server = null, ct = null, gameDb = null;
            long writeStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            try
            {
                server = sapi_server?.GetValue(sapi);
                ct = sm_chunkThread?.GetValue(server);
                gameDb = ct != null ? ct_gameDatabase?.GetValue(ct) : null;
                if (gameDb == null) return;

                // Write player data (vanilla's SetPlayerData preserves schema)
                if (players != null)
                {
                    foreach (var (uid, data) in players)
                    {
                        try { gdb_setPlayerData.Invoke(gameDb, new object[] { uid, data }); }
                        catch (Exception e) { sapi?.Logger.Error("[Synergy] SaveOptimize: Player write: {0}", e.Message); }
                    }
                    sapi?.Logger.Event("[Synergy] SaveOptimize: Wrote {0} players to DB (post-resume)", players.Count);
                }

                // Write map regions (vanilla's SetMapRegions uses batched transaction)
                if (regions != null && regions.Count > 0 && gdb_setMapRegions != null && dbChunkType != null)
                {
                    var worldMap = sm_worldMap?.GetValue(server);
                    if (worldMap != null)
                    {
                        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(dbChunkType));
                        foreach (var (key, data) in regions)
                        {
                            var pos = wm_mapRegionPosFromIndex2D?.Invoke(worldMap, new object[] { key });
                            if (pos != null)
                            {
                                var chunk = Activator.CreateInstance(dbChunkType);
                                dbc_position?.SetValue(chunk, pos);
                                dbc_data?.SetValue(chunk, data);
                                list.Add(chunk);
                            }
                        }
                        gdb_setMapRegions.Invoke(gameDb, new object[] { list });
                        sapi?.Logger.Event("[Synergy] SaveOptimize: Wrote {0} map regions to DB (post-resume)", regions.Count);
                    }
                }
            }
            catch (Exception ex)
            {
                sapi?.Logger.Error("[Synergy] SaveOptimize: Post-resume write: {0}", ex.Message);
                sapi?.Logger.Debug("[Synergy] SaveOptimize: {0}", ex);
            }

            long writeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - writeStart;
            SynergyMetrics.RecordSaveWriteTime(writeMs); // suspend time already recorded in prefix

            // Signal off-thread chunk save AFTER writes complete
            try
            {
                if (ct != null) ct_runOffThreadSaveNow?.SetValue(ct, true);
            }
            catch { }
        }
    }
}
