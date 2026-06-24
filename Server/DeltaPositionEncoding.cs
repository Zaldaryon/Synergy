using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using Synergy.Diagnostics;
using Synergy.Network;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace Synergy.Server
{
    /// <summary>
    /// Patches SendPositionsAndAnimations to send delta-encoded position packets
    /// to Synergy clients via a mod UDP channel, while vanilla clients receive unchanged
    /// absolute packets through the normal UDP path.
    ///
    /// Fixes applied vs initial implementation:
    /// - Sub-batching: groups of 3 entities per UDP packet (absolute-safe, stays under 508 bytes)
    /// - TOCTOU: uses TryGetValue on ConcurrentDictionary for tag packets
    /// - Null player: still sends vanilla UDP positions when Player is null (matches vanilla)
    /// - ThreadStatic lists: reused across calls, not allocated per call
    /// - IL emit: DynamicMethod delegates for Packet_EntityPosition field access (zero boxing)
    /// - Generation-based baseline reset: no dictionary Clear(), zero contention with physics threads
    /// - Tags included in delta packets (4 longs from Packet_TagSetFast)
    /// </summary>
    public static class DeltaPositionEncoding
    {
        private static ICoreServerAPI sapi;
        private static SynergyChannelManager channelManager;
        internal static int errorCount;
        internal static bool disabled;

        private const int MaxEntitiesPerBatchAbsolute = 3; // Absolute packets are larger; 3 × ~107 = ~321 bytes (safe under 508)

        // PhysicsManager instance field accessors
        private static AccessTools.FieldRef<object, IList> clientListRef;
        private static AccessTools.FieldRef<object, object> udpNetworkRef;
        private static AccessTools.FieldRef<object, IServerNetworkChannel> animChannelRef;
        private static FieldInfo entitiesTagPacketsField;

        // Internal types
        private static Type pktType, animPktType, tagPktType, bulkPktType, bulkAnimPktType, tagSetPktType;

        // ConnectedClient accessors
        private static AccessTools.FieldRef<object, IServerPlayer> ccPlayerRef;
        private static AccessTools.FieldRef<object, bool> ccFallBackRef;
        private static AccessTools.FieldRef<object, bool> ccSinglePlayerRef;
        private static System.Func<object, Entity> ccEntityPlayerGetter;
        private static System.Func<object, HashSet<long>> ccTrackedEntitiesGetter;
        private static System.Func<object, List<Entity>[]> ccThreadedTrackedGetter;

        // IL-emitted fast field readers for Packet_EntityPosition (zero boxing)
        private delegate long ReadLong(object pkt);
        private delegate int ReadInt(object pkt);
        private delegate bool ReadBool(object pkt);
        private delegate object ReadObj(object pkt);

        private static ReadLong pktEntityId, pktX, pktY, pktZ, pktMotionX, pktMotionY, pktMotionZ;
        private static ReadInt pktYaw, pktPitch, pktRoll, pktHeadYaw, pktHeadPitch, pktBodyYaw;
        private static ReadInt pktControls, pktTick, pktPosVersion, pktMountControls;
        private static ReadBool pktTeleport;
        private static ReadObj pktTags; // returns Packet_TagSetFast

        // IL-emitted fast field readers for Packet_TagSetFast
        private static ReadLong tagPart1, tagPart2, tagPart3, tagPart4;

        // Vanilla send methods
        private static MethodInfo sendBulkMethod, setBulkPositions;
        private static MethodInfo sendBulkAnimMethod, sendTagMethod;
        private static FieldInfo bulkAnimPacketsField;

        // ThreadStatic reusable lists and buffers — one set per physics thread
        [ThreadStatic] private static List<object> t_vanillaPosList;
        [ThreadStatic] private static List<object> t_animList;
        [ThreadStatic] private static List<object> t_tagList;
        [ThreadStatic] private static DeltaEntry[] t_deltaBuffer;
        [ThreadStatic] private static DeltaPositionBatch t_batch;

        public static void Initialize(ICoreServerAPI api, Harmony harmony, SynergyChannelManager manager)
        {
            sapi = api;
            channelManager = manager;
            errorCount = 0;
            disabled = false;

            var physMgr = AccessTools.TypeByName("Vintagestory.Server.PhysicsManager");
            if (physMgr == null) { api.Logger.Warning("[Synergy] DeltaEncoding: PhysicsManager not found"); return; }

            // Resolve types
            pktType = AccessTools.TypeByName("Packet_EntityPosition");
            animPktType = AccessTools.TypeByName("Vintagestory.Common.Network.Packets.AnimationPacket");
            tagPktType = AccessTools.TypeByName("Vintagestory.Common.Network.Packets.EntityTagPacket");
            bulkPktType = AccessTools.TypeByName("Packet_BulkEntityPosition");
            bulkAnimPktType = AccessTools.TypeByName("Vintagestory.Common.Network.Packets.BulkAnimationPacket");
            tagSetPktType = AccessTools.TypeByName("Packet_TagSetFast");

            if (pktType == null || animPktType == null || bulkPktType == null || tagSetPktType == null)
            { api.Logger.Warning("[Synergy] DeltaEncoding: Packet types not found"); return; }

            // Target method
            var dictPosType = typeof(Dictionary<,>).MakeGenericType(typeof(long), pktType);
            var dictAnimType = typeof(Dictionary<,>).MakeGenericType(typeof(long), animPktType);
            var target = AccessTools.Method(physMgr, "SendPositionsAndAnimations",
                new[] { dictPosType, dictAnimType, typeof(int), typeof(bool) });
            if (target == null) { api.Logger.Warning("[Synergy] DeltaEncoding: Target method not found"); return; }
            if (!ConflictDetector.IsSafeToPatch(target, SynergyMod.HarmonyId, api.Logger)) return;

            // PhysicsManager fields
            clientListRef = AccessTools.FieldRefAccess<IList>(physMgr, "ClientList");
            udpNetworkRef = AccessTools.FieldRefAccess<object>(physMgr, "udpNetwork");
            animChannelRef = AccessTools.FieldRefAccess<IServerNetworkChannel>(physMgr, "AnimationsAndTagsChannel");
            entitiesTagPacketsField = AccessTools.Field(physMgr, "entitiesTagPackets");

            // ConnectedClient fields
            var ccType = AccessTools.TypeByName("Vintagestory.Server.ConnectedClient");
            if (ccType == null) { api.Logger.Warning("[Synergy] DeltaEncoding: ConnectedClient not found"); return; }
            ccPlayerRef = AccessTools.FieldRefAccess<IServerPlayer>(ccType, "Player");
            ccFallBackRef = AccessTools.FieldRefAccess<bool>(ccType, "FallBackToTcp");
            ccSinglePlayerRef = AccessTools.FieldRefAccess<bool>(ccType, "IsSinglePlayerClient");

            var epProp = AccessTools.Property(ccType, "Entityplayer");
            if (epProp != null)
            {
                var epGet = epProp.GetGetMethod(true);
                ccEntityPlayerGetter = obj => (Entity)epGet.Invoke(obj, null);
            }
            var teField = AccessTools.Field(ccType, "TrackedEntities");
            if (teField != null) ccTrackedEntitiesGetter = obj => (HashSet<long>)teField.GetValue(obj);
            var ttField = AccessTools.Field(ccType, "threadedTrackedEntities");
            if (ttField != null) ccThreadedTrackedGetter = obj => (List<Entity>[])ttField.GetValue(obj);

            // IL-emitted field readers for Packet_EntityPosition
            pktEntityId = EmitLongReader(pktType, "EntityId");
            pktX = EmitLongReader(pktType, "X");
            pktY = EmitLongReader(pktType, "Y");
            pktZ = EmitLongReader(pktType, "Z");
            pktMotionX = EmitLongReader(pktType, "MotionX");
            pktMotionY = EmitLongReader(pktType, "MotionY");
            pktMotionZ = EmitLongReader(pktType, "MotionZ");
            pktYaw = EmitIntReader(pktType, "Yaw");
            pktPitch = EmitIntReader(pktType, "Pitch");
            pktRoll = EmitIntReader(pktType, "Roll");
            pktHeadYaw = EmitIntReader(pktType, "HeadYaw");
            pktHeadPitch = EmitIntReader(pktType, "HeadPitch");
            pktBodyYaw = EmitIntReader(pktType, "BodyYaw");
            pktControls = EmitIntReader(pktType, "Controls");
            pktTick = EmitIntReader(pktType, "Tick");
            pktPosVersion = EmitIntReader(pktType, "PositionVersion");
            pktMountControls = EmitIntReader(pktType, "MountControls");
            pktTeleport = EmitBoolReader(pktType, "Teleport");
            pktTags = EmitObjReader(pktType, "Tags");

            // IL-emitted field readers for Packet_TagSetFast
            tagPart1 = EmitLongReader(tagSetPktType, "Part1");
            tagPart2 = EmitLongReader(tagSetPktType, "Part2");
            tagPart3 = EmitLongReader(tagSetPktType, "Part3");
            tagPart4 = EmitLongReader(tagSetPktType, "Part4");

            // Vanilla send methods
            var udpType = AccessTools.TypeByName("Vintagestory.Server.Systems.ServerUdpNetwork")
                ?? AccessTools.TypeByName("Vintagestory.Server.ServerUdpNetwork");
            sendBulkMethod = AccessTools.Method(udpType, "SendPacket_Threadsafe", new[] { ccType, bulkPktType });
            setBulkPositions = AccessTools.Method(bulkPktType, "SetEntityPositions", new[] { pktType.MakeArrayType() });
            if (bulkAnimPktType != null) bulkAnimPacketsField = AccessTools.Field(bulkAnimPktType, "Packets");

            // Resolve generic SendPacket<T>(T, params IServerPlayer[]) for anim/tag
            foreach (var m in typeof(IServerNetworkChannel).GetMethods())
            {
                if (m.Name != "SendPacket" || !m.IsGenericMethodDefinition) continue;
                var p = m.GetParameters();
                if (p.Length == 2 && p[1].ParameterType == typeof(IServerPlayer[]))
                {
                    if (bulkAnimPktType != null) sendBulkAnimMethod = m.MakeGenericMethod(bulkAnimPktType);
                    if (tagPktType != null) sendTagMethod = m.MakeGenericMethod(tagPktType);
                    break;
                }
            }

            if (sendBulkMethod == null || setBulkPositions == null)
            { api.Logger.Warning("[Synergy] DeltaEncoding: Send methods not found"); return; }

            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(DeltaPositionEncoding), nameof(Prefix_SendPositionsAndAnimations)));

            // Postfix on ServerMain.DespawnEntity to prune stale baselines from all delta clients.
            // Without this, Baselines grows unboundedly with every entity ever tracked per client
            // (only cleared on disconnect). Short-lived entities (dropped items, projectiles) churn
            // thousands of entries on busy servers.
            var serverMainType = AccessTools.TypeByName("Vintagestory.Server.ServerMain");
            var despawnMethod = serverMainType != null
                ? AccessTools.Method(serverMainType, "DespawnEntity", new[] { typeof(Entity), typeof(EntityDespawnData) })
                : null;
            if (despawnMethod != null)
            {
                harmony.Patch(despawnMethod,
                    postfix: new HarmonyMethod(typeof(DeltaPositionEncoding), nameof(Postfix_DespawnEntity)));
            }

            api.Logger.Notification("[Synergy] DeltaEncoding: Active");
        }

        public static bool Prefix_SendPositionsAndAnimations(
            object __instance, object entityPositionPackets, object entityAnimPackets,
            int zeroBasedThreadNum, bool stateUpdateTick)
        {
            if (disabled || channelManager == null || channelManager.ClientCount == 0)
                return true;

            try
            {
                var clientList = clientListRef(__instance);
                if (clientList == null || clientList.Count == 0) return true;

                var udpNetwork = udpNetworkRef(__instance);
                var animChannel = animChannelRef(__instance);
                var tagPacketsObj = entitiesTagPacketsField?.GetValue(__instance);

                var posPackets = entityPositionPackets as IDictionary;
                var animPackets = entityAnimPackets as IDictionary;
                if (posPackets == null || animPackets == null) return true;

                // ThreadStatic reusable lists
                var vanillaPosList = t_vanillaPosList ??= new List<object>();
                var animList = t_animList ??= new List<object>();
                var tagList = t_tagList ??= new List<object>();
                var deltaBuffer = t_deltaBuffer ??= new DeltaEntry[1024];
                var reusableBatch = t_batch ??= new DeltaPositionBatch();
                int deltaCount;

                foreach (var clientObj in clientList)
                {
                    vanillaPosList.Clear();
                    animList.Clear();
                    tagList.Clear();
                    deltaCount = 0;
                    int buildGeneration = channelManager.BaselineGeneration;

                    // Determine if this is a delta client
                    var player = ccPlayerRef(clientObj);
                    SynergyChannelManager.ClientState clientState = null;
                    bool isDeltaClient = false;
                    if (player != null && !ccSinglePlayerRef(clientObj))
                    {
                        clientState = channelManager.GetDeltaClient(player.PlayerUID);
                        isDeltaClient = clientState != null && SynergyMod.Config?.DeltaEncodingEnabled == true;
                    }

                    // Collect packets for this client's tracked entities
                    if (stateUpdateTick)
                    {
                        var threadedLists = ccThreadedTrackedGetter?.Invoke(clientObj);
                        if (threadedLists == null || zeroBasedThreadNum >= threadedLists.Length) continue;
                        var entityList = threadedLists[zeroBasedThreadNum];

                        foreach (var entity in entityList)
                        {
                            CollectPackets(entity.EntityId, posPackets, animPackets, tagPacketsObj,
                                isDeltaClient, clientState, buildGeneration,
                                vanillaPosList, animList, tagList, deltaBuffer, ref deltaCount,
                                entity is EntityItem);
                        }

                        // Own player animation (vanilla: only in stateUpdateTick branch)
                        var entityPlayer = ccEntityPlayerGetter?.Invoke(clientObj);
                        if (entityPlayer != null && animPackets.Contains(entityPlayer.EntityId)
                            && !entityList.Contains(entityPlayer))
                        {
                            animList.Add(animPackets[entityPlayer.EntityId]);
                        }
                    }
                    else
                    {
                        var trackedIds = ccTrackedEntitiesGetter?.Invoke(clientObj);
                        if (trackedIds == null) continue;

                        foreach (long eid in trackedIds)
                        {
                            // Check if entity is EntityItem — excluded from delta encoding entirely
                            var ent = sapi.World.GetEntityById(eid);
                            CollectPackets(eid, posPackets, animPackets, tagPacketsObj,
                                isDeltaClient, clientState, buildGeneration,
                                vanillaPosList, animList, tagList, deltaBuffer, ref deltaCount,
                                isEntityItem: ent is EntityItem);
                        }
                    }

                    // Send delta batches (sub-batched to stay under UDP MTU). Use the absolute-safe
                    // limit because entries can be converted to absolute against the actual datagram
                    // generation immediately before encoding.
                    if (isDeltaClient && deltaCount > 0)
                    {
                        const int batchLimit = MaxEntitiesPerBatchAbsolute;

                        for (int i = 0; i < deltaCount; i += batchLimit)
                        {
                            int batchSize = Math.Min(batchLimit, deltaCount - i);
                            int sendGeneration = channelManager.NextGeneration();
                            PrepareEntriesForGeneration(deltaBuffer, i, batchSize, sendGeneration);
                            RecordSentEntries(clientState, deltaBuffer, i, batchSize, sendGeneration);
                            reusableBatch.Data = DeltaCodec.Encode(deltaBuffer, i, batchSize, sendGeneration);
                            channelManager.SendDeltaBatch(reusableBatch, player);
                            DiagDeltaEncoding.OnDeltaBatch(reusableBatch.Data.Length);
                            DiagDeltaEncoding.OnVanillaEquivBytes(batchSize * 107);
                        }
                    }

                    // Send vanilla positions (for non-delta clients, or when player is null)
                    if (vanillaPosList.Count > 0)
                    {
                        SendVanillaPositions(udpNetwork, clientObj, vanillaPosList);
                        DiagDeltaEncoding.OnVanillaBatch(vanillaPosList.Count * 107);
                    }

                    // Animations (vanilla path, all clients)
                    if (animList.Count > 0 && animChannel != null && sendBulkAnimMethod != null && player != null)
                    {
                        var bulkAnim = Activator.CreateInstance(bulkAnimPktType);
                        var arr = Array.CreateInstance(animPktType, animList.Count);
                        for (int i = 0; i < animList.Count; i++) arr.SetValue(animList[i], i);
                        bulkAnimPacketsField?.SetValue(bulkAnim, arr);
                        sendBulkAnimMethod.Invoke(animChannel, new object[] { bulkAnim, new IServerPlayer[] { player } });
                    }

                    // Tags (vanilla path, all clients)
                    if (tagList.Count > 0 && animChannel != null && sendTagMethod != null && player != null)
                    {
                        var players = new IServerPlayer[] { player };
                        foreach (var tag in tagList)
                            sendTagMethod.Invoke(animChannel, new object[] { tag, players });
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref errorCount) >= 5)
                {
                    disabled = true;
                    sapi?.Logger.Warning("[Synergy] DeltaEncoding: Auto-disabled after {0} errors: {1}", errorCount, ex.Message);
                }
                return true;
            }
        }

        public static void Postfix_DespawnEntity(Entity entity)
        {
            if (disabled || channelManager == null || entity == null) return;
            channelManager.PruneBaseline(entity.EntityId);
        }

        private static void CollectPackets(
            long entityId, IDictionary posPackets, IDictionary animPackets, object tagPacketsObj,
            bool isDeltaClient, SynergyChannelManager.ClientState clientState, int generation,
            List<object> vanillaPosList, List<object> animList, List<object> tagList,
            DeltaEntry[] deltaBuffer, ref int deltaCount, bool isEntityItem = false)
        {
            if (posPackets.Contains(entityId))
            {
                var pkt = posPackets[entityId];
                if (isDeltaClient && !isEntityItem && deltaCount < deltaBuffer.Length)
                {
                    var track = clientState.Tracks.GetOrAdd(entityId, _ => new SynergyChannelManager.EntityTrack());
                    if (TryBuildFromPacket(track, generation, pkt, out var entry))
                        deltaBuffer[deltaCount++] = entry;
                }
                else
                {
                    // Non-delta client, EntityItem, or delta buffer full — vanilla absolute.
                    vanillaPosList.Add(pkt);
                }
            }
            else if (isDeltaClient && !isEntityItem && deltaCount < deltaBuffer.Length
                     && clientState.Tracks.TryGetValue(entityId, out var track) && track.HasPending)
            {
                // Entity left vanilla's per-tick set (went stationary) but the client hasn't acked its
                // last position yet. Resend the current delta against the acked base until it does,
                // so a single dropped UDP packet can't leave it frozen (the reported dropped-item bug).
                if (TryBuildResend(track, generation, entityId, out var entry))
                    deltaBuffer[deltaCount++] = entry;
            }

            if (animPackets.Contains(entityId))
                animList.Add(animPackets[entityId]);

            if (tagPacketsObj is IDictionary tagDict)
            {
                try
                {
                    if (tagDict.Contains(entityId))
                        tagList.Add(tagDict[entityId]);
                }
                catch (KeyNotFoundException) { }
            }
        }

        private static bool TryBuildFromPacket(SynergyChannelManager.EntityTrack track, int generation, object pkt, out DeltaEntry entry)
        {
            long eid = pktEntityId(pkt);
            long x = pktX(pkt), y = pktY(pkt), z = pktZ(pkt);
            long mx = pktMotionX(pkt), my = pktMotionY(pkt), mz = pktMotionZ(pkt);
            bool teleport = pktTeleport(pkt);

            var tags = pktTags(pkt);
            long t1 = 0, t2 = 0, t3 = 0, t4 = 0;
            if (tags != null) { t1 = tagPart1(tags); t2 = tagPart2(tags); t3 = tagPart3(tags); t4 = tagPart4(tags); }

            lock (track)
                return BuildEntryLocked(track, generation, eid, x, y, z, mx, my, mz, teleport,
                    pktYaw(pkt), pktPitch(pkt), pktRoll(pkt), pktHeadYaw(pkt), pktHeadPitch(pkt), pktBodyYaw(pkt),
                    pktControls(pkt), pktTick(pkt), pktPosVersion(pkt), pktMountControls(pkt),
                    t1, t2, t3, t4, out entry);
        }

        private static bool TryBuildResend(SynergyChannelManager.EntityTrack track, int generation, long eid, out DeltaEntry entry)
        {
            entry = default;
            lock (track)
            {
                if (!track.HasPending) return false;
                var s = track.Latest();
                return BuildEntryLocked(track, generation, eid, s.X, s.Y, s.Z, s.MotionX, s.MotionY, s.MotionZ, false,
                    track.LYaw, track.LPitch, track.LRoll, track.LHeadYaw, track.LHeadPitch, track.LBodyYaw,
                    track.LControls, track.LTick, track.LPosVersion, track.LMountControls,
                    track.LTags1, track.LTags2, track.LTags3, track.LTags4, out entry);
            }
        }

        /// <summary>
        /// Build one delta entry against the client's ACKED baseline (Quake 3 / Source model).
        /// Caller must hold <c>lock(track)</c>. Returns false (no entry) when the
        /// entity's value already equals the acked baseline — the client provably has it, so nothing
        /// is sent (ack-aware stationary suppression).
        ///
        /// Absolute (full) is sent when there is no acked baseline yet, on teleport, or when the
        /// acked baseline is older than MaxBaseAge (Gaffer's "drop to absolute if the base is too old"
        /// — also bounds delta magnitude and ring depth). Everything else is a relative delta carrying
        /// BaseGen = AckedGen so the client reconstructs against the exact value it confirmed.
        /// </summary>
        private static bool BuildEntryLocked(SynergyChannelManager.EntityTrack track, int generation, long eid,
            long x, long y, long z, long mx, long my, long mz, bool teleport,
            int yaw, int pitch, int roll, int headYaw, int headPitch, int bodyYaw,
            int controls, int tick, int posVer, int mountControls,
            long t1, long t2, long t3, long t4, out DeltaEntry entry)
        {
            entry = default;

            if (!teleport && track.AckedEquals(x, y, z, mx, my, mz))
                return false; // fully synced — client already has this position

            bool absolute = !track.HasAcked || teleport
                            || (generation - track.AckedGen) > SynergyChannelManager.MaxBaseAge;

            entry = new DeltaEntry
            {
                EntityId = eid,
                Yaw = yaw, Pitch = pitch, Roll = roll, HeadYaw = headYaw, HeadPitch = headPitch, BodyYaw = bodyYaw,
                Controls = controls, Tick = tick, PositionVersion = posVer, MountControls = mountControls,
                TagsPart1 = t1, TagsPart2 = t2, TagsPart3 = t3, TagsPart4 = t4,
                AbsoluteX = x, AbsoluteY = y, AbsoluteZ = z,
                AbsoluteMotionX = mx, AbsoluteMotionY = my, AbsoluteMotionZ = mz,
                Flags = teleport ? DeltaCodec.FlagTeleport : (byte)0
            };

            if (absolute)
            {
                entry.DeltaX = x; entry.DeltaY = y; entry.DeltaZ = z;
                entry.DeltaMotionX = mx; entry.DeltaMotionY = my; entry.DeltaMotionZ = mz;
                entry.BaseGen = generation;
                entry.Flags |= DeltaCodec.FlagAbsolute;
            }
            else
            {
                entry.DeltaX = x - track.X; entry.DeltaY = y - track.Y; entry.DeltaZ = z - track.Z;
                entry.DeltaMotionX = mx - track.MotionX; entry.DeltaMotionY = my - track.MotionY; entry.DeltaMotionZ = mz - track.MotionZ;
                entry.BaseGen = track.AckedGen;
            }

            track.SetLastFields(yaw, pitch, roll, headYaw, headPitch, bodyYaw, controls, tick, posVer, mountControls, t1, t2, t3, t4);
            return true;
        }

        private static void PrepareEntriesForGeneration(DeltaEntry[] entries, int offset, int count, int generation)
        {
            int end = offset + count;
            for (int i = offset; i < end; i++)
            {
                ref var entry = ref entries[i];
                if ((entry.Flags & (DeltaCodec.FlagAbsolute | DeltaCodec.FlagTeleport)) != 0)
                    continue;

                if (generation - entry.BaseGen <= SynergyChannelManager.MaxBaseAge)
                    continue;

                entry.DeltaX = entry.AbsoluteX;
                entry.DeltaY = entry.AbsoluteY;
                entry.DeltaZ = entry.AbsoluteZ;
                entry.DeltaMotionX = entry.AbsoluteMotionX;
                entry.DeltaMotionY = entry.AbsoluteMotionY;
                entry.DeltaMotionZ = entry.AbsoluteMotionZ;
                entry.BaseGen = generation;
                entry.Flags |= DeltaCodec.FlagAbsolute;
            }
        }

        private static void RecordSentEntries(SynergyChannelManager.ClientState clientState, DeltaEntry[] entries, int offset, int count, int generation)
        {
            if (clientState == null) return;

            int end = offset + count;
            for (int i = offset; i < end; i++)
            {
                ref var entry = ref entries[i];
                if (!clientState.Tracks.TryGetValue(entry.EntityId, out var track)) continue;

                lock (track)
                {
                    track.RecordSend(generation,
                        entry.AbsoluteX, entry.AbsoluteY, entry.AbsoluteZ,
                        entry.AbsoluteMotionX, entry.AbsoluteMotionY, entry.AbsoluteMotionZ);
                }
            }
        }

        private static void SendVanillaPositions(object udpNetwork, object client, List<object> posList)
        {
            int count = posList.Count;
            bool isSingle = ccSinglePlayerRef(client);
            bool fallback = ccFallBackRef(client);

            if (count > 8 && !isSingle && !fallback)
            {
                for (int i = 0; i < count; i += 8)
                {
                    int batchSize = Math.Min(8, count - i);
                    var arr = Array.CreateInstance(pktType, batchSize);
                    for (int j = 0; j < batchSize; j++) arr.SetValue(posList[i + j], j);
                    var bulk = Activator.CreateInstance(bulkPktType);
                    setBulkPositions.Invoke(bulk, new object[] { arr });
                    sendBulkMethod.Invoke(udpNetwork, new[] { client, bulk });
                }
            }
            else if (count > 0)
            {
                var arr = Array.CreateInstance(pktType, count);
                for (int i = 0; i < count; i++) arr.SetValue(posList[i], i);
                var bulk = Activator.CreateInstance(bulkPktType);
                setBulkPositions.Invoke(bulk, new object[] { arr });
                sendBulkMethod.Invoke(udpNetwork, new[] { client, bulk });
            }
        }

        // --- IL Emit helpers: generate DynamicMethod delegates for zero-boxing field access ---

        private static ReadLong EmitLongReader(Type type, string fieldName)
        {
            var field = AccessTools.Field(type, fieldName);
            if (field == null) return _ => 0;
            var dm = new DynamicMethod($"Read_{type.Name}_{fieldName}", typeof(long),
                new[] { typeof(object) }, type, true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, type);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            return (ReadLong)dm.CreateDelegate(typeof(ReadLong));
        }

        private static ReadInt EmitIntReader(Type type, string fieldName)
        {
            var field = AccessTools.Field(type, fieldName);
            if (field == null) return _ => 0;
            var dm = new DynamicMethod($"Read_{type.Name}_{fieldName}", typeof(int),
                new[] { typeof(object) }, type, true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, type);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            return (ReadInt)dm.CreateDelegate(typeof(ReadInt));
        }

        private static ReadBool EmitBoolReader(Type type, string fieldName)
        {
            var field = AccessTools.Field(type, fieldName);
            if (field == null) return _ => false;
            var dm = new DynamicMethod($"Read_{type.Name}_{fieldName}", typeof(bool),
                new[] { typeof(object) }, type, true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, type);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            return (ReadBool)dm.CreateDelegate(typeof(ReadBool));
        }

        private static ReadObj EmitObjReader(Type type, string fieldName)
        {
            var field = AccessTools.Field(type, fieldName);
            if (field == null) return _ => null;
            var dm = new DynamicMethod($"Read_{type.Name}_{fieldName}", typeof(object),
                new[] { typeof(object) }, type, true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, type);
            il.Emit(OpCodes.Ldfld, field);
            if (field.FieldType.IsValueType) il.Emit(OpCodes.Box, field.FieldType);
            il.Emit(OpCodes.Ret);
            return (ReadObj)dm.CreateDelegate(typeof(ReadObj));
        }
    }
}
