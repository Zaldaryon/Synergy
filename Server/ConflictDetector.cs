using System.Linq;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;

namespace Synergy.Server
{
    /// <summary>
    /// Checks if a target method already has Harmony patches from other mods.
    /// Auto-disables Synergy patches when conflicts are detected to avoid breaking other mods.
    /// </summary>
    public static class ConflictDetector
    {
        public static bool IsSafeToPatch(MethodBase target, string ownHarmonyId, ILogger logger)
        {
            var patches = Harmony.GetPatchInfo(target);
            if (patches == null) return true;

            var otherTranspilers = patches.Transpilers?.Where(p => p.owner != ownHarmonyId).ToList();
            if (otherTranspilers != null && otherTranspilers.Count > 0)
            {
                var owners = string.Join(", ", otherTranspilers.Select(p => p.owner));
                logger.Warning("[Synergy] Skipping patch on {0}.{1} — transpiler conflict with: {2}",
                    target.DeclaringType?.Name, target.Name, owners);
                return false;
            }

            var otherPrefixes = patches.Prefixes?.Where(p => p.owner != ownHarmonyId).ToList();
            if (otherPrefixes != null && otherPrefixes.Count > 0)
            {
                var owners = string.Join(", ", otherPrefixes.Select(p => p.owner));
                logger.Warning("[Synergy] Skipping patch on {0}.{1} — prefix conflict with: {2}",
                    target.DeclaringType?.Name, target.Name, owners);
                return false;
            }

            return true;
        }
    }
}
