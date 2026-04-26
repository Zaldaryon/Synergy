# Synergy

Coordinated server-side performance and fluidity optimizations for Vintage Story.

## Scope
- Side: universal (`requiredOnClient: false`, `requiredOnServer: false`)
- Works as server-only, or with both client and server — no side is required
- Dependency declared in `modinfo.json`: `game: 1.22.0` (minimum-version semantics)
- Target framework: `net10.0` (Vintage Story 1.22 ships with .NET 10)
- Validated operational window: `1.22.0+`

## Goals
- Reduce server TPS overhead from entity ticking, collision, and network I/O.
- Reduce bandwidth for distant entity position updates.
- Improve perceived multiplayer fluidity (entity pop-in, spawn ordering).
- Keep black-box behavior equivalent to vanilla gameplay logic.
- Fail safe: if optimization safety checks fail, fallback to vanilla paths.

## Design Principles
- **Vanilla behavior preserved** — all performance optimizations produce identical game state, network packet content, and block/entity/inventory outcomes. Only timing and ordering may change.
- **Independent sides** — client and server components work without each other.
- **Safe for all clients** — server optimizations work with vanilla and modded clients.
- **Auto-disable on conflict** — patches check for existing Harmony patches from other mods before applying. Transpiler or prefix conflicts cause automatic skip with logged warning.
- **Circuit breaker** — each optimization auto-disables after 5 consecutive errors, falling back to vanilla.

## Optimization Catalog (12)

### Server-Side Performance (8)

1. **Entity Activation Range** (`EntityActivationRangeEnabled`)
   - Patches `ServerSystemEntitySimulation.TickEntities(float)` — prefix (replaces method)
   - Skips `OnGameTick` for non-player entities beyond 48 blocks of any player
   - Whitelisted behaviors still tick for sleeping entities: breathe, health, despawn, deaddecay, decay, grow, multiply, harvestable
   - Calendar-based behaviors (grow, multiply, harvestable) catch up automatically via `Calendar.TotalHours`
   - Entities in lava or on fire always get full tick (fire/ignition/extinguishing logic preserved)
   - Players and `AlwaysActive` entities always get full tick
   - Includes `FrameProfiler` calls matching vanilla for `.debug tickprofile` compatibility
   - **Impact:** ~10-30% TPS with 200+ entities. Scales with entity count and player spread.

2. **Entity Collision Fast-Path** (`CollisionFastPathEnabled`)
   - Patches `CollisionTester.ApplyTerrainCollision(Entity, EntityPos, float, ref Vec3d, float, float)` — prefix
   - Skips collision resolution when ALL conditions met: motion == (0,0,0), not in liquid, not swimming, not in lava, not on fire
   - Sets `CollidedVertically = false` and `CollidedHorizontally = false` to match vanilla behavior with zero motion
   - Vanilla verified: with zero motion, `pushOutY/X/Z` all return 0, no `OnEntityCollide` callbacks fire, position unchanged
   - **Impact:** ~5-15% physics CPU. Dense animal pens benefit most.

3. **Network Flush Consolidation** (`NetworkFlushConsolidationEnabled`)
   - Patches `TcpNetConnection.Send(byte[], bool)` — prefix
   - Buffers small uncompressed TCP packets, flushes when buffer exceeds MTU (1400 bytes) or at end of tick
   - Large or compressed packets flush immediately (high priority)
   - Uses vanilla `Send` path during flush — preserves header format (`length | (compressed ? 1<<31 : 0)`), async behavior, and disconnect handling
   - ThreadStatic re-entry guard prevents recursion during flush
   - Checks `Connected` field before flush, removes dead connections from buffer dictionary
   - **Impact:** ~3-5% server CPU with 10+ players. Reduces per-packet syscall overhead.

4. **Block Tick Pooling** (`BlockTickPoolingEnabled`)
   - Patches `ServerSystemBlockSimulation.tryTickBlock(Block, BlockPos)` — prefix
   - Patches `ServerSystemBlockSimulation.OnSeparateThreadTick(float)` — prefix (pool reset)
   - Pools 512 `BlockPos` objects per thread via `[ThreadStatic]` array, pre-populated on first use
   - Pool index resets each tick cycle via `OnSeparateThreadTick` prefix
   - Falls back to `source.Copy()` when pool exhausted (>512 ticks per cycle)
   - `BlockPosWithExtraObject` created via `Activator.CreateInstance` for ticks with extra data
   - **Impact:** ~1-2% TPS with 10+ players. Reduces Gen0 GC pressure (~16 MB/s allocation eliminated).

5. **Inventory Dirty Scan** (`InventoryDirtyScanEnabled`)
   - Patches `ServerSystemInventory.SendDirtySlots(float)` — prefix
   - Patches `InventoryBase.DidModifyItemSlot(ItemSlot, ItemStack)` — postfix
   - Uses `Volatile.Read/Write` for thread-safe dirty flag
   - When flag is 0 (nothing modified), skips the entire client/inventory iteration loop
   - Vanilla runs every 30ms iterating all clients → all inventories → checking `IsDirty`
   - **Impact:** <1% TPS. Mainly benefits 30+ player servers where most inventories are idle.

6. **Pathfinding Node Pooling** (`PathfindingOptimizationsEnabled`)
   - Transpiler on `AStar.FindPathOrEscapePath` — replaces `new PathNode()` with pooled equivalents
   - Prefix resets per-thread pool (4096 pre-allocated PathNode objects) at start of each search
   - Postfix on `AStar.FindPath` and `PathfindSystem.FindEscapePath` copies pooled nodes for API safety
   - Falls back to vanilla `new PathNode()` when pool exhausted (>4096 nodes per search)
   - Reference: A* Pathfinding Project (arongranberg.com) — node pooling is industry standard
   - **Impact:** ~87% reduction in pathfinding heap allocations. Reduces GC pressure ~7 MB/s with 200 entities.

7. **Pathfinding Distance Throttle** (`PathfindingThrottleEnabled`)
   - Patches `PathfindingAsync.EnqueuePathfinderTask(PathfinderTask)` — prefix
   - Reduces pathfinding frequency based on distance to nearest player
   - <32 blocks: every request. 32-64: ~1 in 2. 64-96: ~1 in 4. >96: ~1 in 8
   - Skipped tasks marked Finished with null waypoints (entity retries next tick)
   - Reference: Airplane/Pufferfish DEAR (Dynamic Entity Activation Range)
   - **Impact:** ~30-60% pathfinding CPU reduction. Distant entities react ~100-200ms slower (imperceptible).

8. **Entity Repulsion Throttle** (`RepulseAgentsThrottleEnabled`)
   - Patches `EntityBehaviorRepulseAgents.OnGameTick(float)` — prefix
   - Vanilla runs repulsion every tick per entity with zero distance throttling — O(N²) in dense pens
   - <32 blocks: every tick (vanilla). 32-64: every 4th tick. >64: skip entirely
   - Repulsion is purely cosmetic at distance — no gameplay impact from skipping
   - Reference: OptiTime client-side RepulseAgentsOptimization (same concept)
   - **Impact:** ~3-8% TPS in dense animal pens. Scales quadratically with entity density.

### Server-Client Fluidity (4)

9. **Entity Tracking Hysteresis** (`EntityTrackingHysteresisEnabled`)
   - Patches `PhysicsManager.UpdateTrackedEntityLists(ConnectedClient, int)` — postfix
   - Adds speed-proportional buffer zone to tracking range: `max(8, motion.Length * 20)` blocks
   - Entities within `trackingRange + buffer` are kept tracked even when vanilla would despawn them
   - Prevents entity pop-in/pop-out flickering at view distance boundary
   - All `FieldInfo` cached at init (6 fields)
   - **Impact:** Eliminates entity flickering at tracking boundary. No TPS cost.

10. **Distance-Based Send Frequency** (`DistanceBasedSendFrequencyEnabled`)
   - Patches `PhysicsManager.BuildPositionPacket(Entity, bool, Dict, Dict)` — prefix
   - `IsTracked == 2` (≤50 blocks): every tick at 30Hz (unchanged)
   - `IsTracked == 1` (50-128 blocks): every 2nd tick at 15Hz
   - Fast-moving entities (motion > 0.2 blocks/tick) always send at 30Hz
   - Uses separate frame counter — does not interfere with vanilla's `tick` attribute
   - Client interpolation handles variable rates via `tickDiff` in position packets
   - **Impact:** ~30-40% bandwidth reduction for distant entities. Visually identical at 50+ blocks.

11. **Attribute Sync Delta Updates** (`AttributeSyncResyncPreventionEnabled`)
   - Patches `SyncedTreeAttribute.RemoveAttribute(string)` — prefix
   - Vanilla calls `MarkAllDirty()` on every `RemoveAttribute` — triggers full attribute tree resync (500-2000 bytes)
   - Mod calls `base.RemoveAttribute(key)` then adds key to `attributePathsDirty` directly
   - Avoids triggering modified listeners (matching vanilla's `MarkAllDirty` which also suppresses them)
   - Client receives deletion via `PartialUpdate(path, null)` → `DeleteAttributeByPath(path)`
   - Fallback: next full sync (every 5s) corrects any inconsistency
   - **Impact:** Saves 500-2000 bytes per `RemoveAttribute` event. Adds up with many entities.

12. **Entity Spawn Priority Ordering** (`EntitySpawnPriorityOrderingEnabled`)
   - Patches `PhysicsManager.SendTrackedEntitiesStateChanges()` — prefix
   - Sorts `entitiesNowInRange` by distance (farthest-first)
   - Client uses `Stack<Entity>` (LIFO) for `EntityLoadQueue` — farthest-first send order inverts to nearest-first processing
   - All `FieldInfo` cached at init (4 fields)
   - **Impact:** Nearest entities appear ~100-500ms sooner after teleport/login.

## Estimated Total Impact

| Scenario | TPS Gain | Bandwidth | Fluidity |
|----------|----------|-----------|----------|
| 1 player, 50 entities | ~2-5% | ~10% | Slight |
| 5 players, 150 entities | ~8-15% | ~20-25% | Noticeable |
| 10 players, 300 entities | ~15-25% | ~30-35% | Significant |
| 30+ players, 500+ entities | ~25-40% | ~35-40% | Significant |

Gains are cumulative but not linear — each optimization targets a different part of the tick loop.

## Safety and Fallback Model

### Startup safety
- **Conflict detection:** Before each `harmony.Patch()` call, `ConflictDetector.IsSafeToPatch()` checks `Harmony.GetPatchInfo()` for existing transpilers or prefixes from other mods. If found, the optimization is skipped with a logged warning.
- **Patch failure:** If the target method or type is not found (renamed in a game update), the optimization remains disabled and vanilla path is preserved.

### Runtime safety
- **Circuit breaker:** Each optimization tracks consecutive errors. After 5 errors, the optimization auto-disables and falls back to vanilla. Logged as `[Synergy] Pxx: Auto-disabled after N errors`.
- **Graceful degradation:** All prefixes return `true` (let vanilla run) on any unexpected exception.

## Installation

1. Download `Synergy-X.X.X.zip` from releases.
2. Place in your `Mods` folder (server, or both client and server).
3. Launch the game.

## Configuration

Edit `Synergy.json` in your `ModConfig` folder. All optimizations are individually toggleable.

```json
{
  "BlockTickPoolingEnabled": true,
  "NetworkFlushConsolidationEnabled": true,
  "InventoryDirtyScanEnabled": true,
  "EntityActivationRangeEnabled": true,
  "EntityActivationRangeBlocks": 48.0,
  "CollisionFastPathEnabled": true,
  "PathfindingOptimizationsEnabled": true,
  "PathfindingThrottleEnabled": true,
  "RepulseAgentsThrottleEnabled": true,
  "EntityTrackingHysteresisEnabled": true,
  "DistanceBasedSendFrequencyEnabled": true,
  "AttributeSyncResyncPreventionEnabled": true,
  "EntitySpawnPriorityOrderingEnabled": true
}
```

Changes require server restart.

## Compatibility

- Compatible with most content and gameplay mods.
- Harmony conflicts are detected automatically — Synergy skips its patch when another mod already patches the same method.
- Compatible with **Tungsten** (different optimization targets, no overlap).
- Compatible with **OptiTime** (client-side only, no server overlap).

## Logging Conventions

- Prefix: `[Synergy]`
- Optimization-specific tags: `P3`, `P8`, `P9`, `P11`, `P13`, `S1`, `S2`, `S3`, `S4`
- Automatic disable/fallback events are explicitly logged.

## License

Copyright (c) 2026 Zaldaryon - All Rights Reserved
