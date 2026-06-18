using Vintagestory.API.Common.Entities;

namespace Synergy.Server
{
    /// <summary>
    /// Identifies entities from mods that use custom AI/pathfinding systems incompatible
    /// with Synergy's throttling optimizations.
    ///
    /// Currently excludes:
    /// - VSVillage (domain "vsvillage"): custom VillagerAStarNew pathfinder, custom AI tasks
    ///   that set WalkVector directly, village waypoint system. Throttling these entities
    ///   causes villagers to freeze at town edges.
    /// </summary>
    public static class ModEntityExclusions
    {
        public static bool IsExcludedMod(Entity entity)
        {
            var code = entity.Code;
            if (code == null) return false;
            return code.Domain == "vsvillage";
        }
    }
}
