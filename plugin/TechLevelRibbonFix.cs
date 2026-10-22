using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HsPuzzleBridge
{
    /// <summary>
    /// Local replay: one tavern-tier ribbon (the star badge next to Bob), not one per jump.
    ///
    /// Every jump is a full reset: GameState.OnResetGame decommissions the game entity (TB_BaconShop) and creates
    /// a new one. Each TB_BaconShop instantiates its own BaconTechLevelRibbon prefab as a free-standing object
    /// (InitTurnCounter) and keeps it current from a coroutine on the static GameEntity.Coroutines host;
    /// OnDecommissionGame destroys neither, so ribbons piled up on top of each other after rewinds and skips.
    /// On decommission the old instance's ribbon is destroyed, and its update coroutine ends itself the next
    /// time it runs (it can't be stopped from outside: the host is shared by every game entity).
    /// </summary>
    [HarmonyPatch(typeof(TB_BaconShop), nameof(TB_BaconShop.OnDecommissionGame))]
    internal static class TechLevelRibbonFix
    {
        private static readonly AccessTools.FieldRef<TB_BaconShop, Notification> Ribbon =
            AccessTools.FieldRefAccess<TB_BaconShop, Notification>("m_techLevelCounter");

        private static void Postfix(TB_BaconShop __instance)
        {
            if (!ReplaySpeed.InLocalGame) return;
            var ribbon = Ribbon(__instance);
            if (ribbon == null) return;
            UnityEngine.Object.Destroy(ribbon.gameObject);
            Ribbon(__instance) = null;
        }

        /// <summary>KeepTechLevelUpToDateCoroutine's state machine: stop once its owner's ribbon is gone.</summary>
        [HarmonyPatch]
        private static class EndOrphanedCoroutine
        {
            private static FieldInfo _owner;

            private static IEnumerable<MethodBase> TargetMethods()
            {
                var stateMachine = typeof(TB_BaconShop).GetNestedTypes(BindingFlags.NonPublic)
                    .FirstOrDefault(t => t.Name.Contains("KeepTechLevelUpToDateCoroutine"));
                _owner = stateMachine == null ? null : AccessTools.Field(stateMachine, "<>4__this");
                if (_owner == null)
                {
                    Plugin.Log.LogWarning("Tier ribbon fix: KeepTechLevelUpToDateCoroutine state machine not found");
                    yield break;
                }
                yield return AccessTools.Method(stateMachine, "MoveNext");
            }

            private static bool Prefix(object __instance, ref bool __result)
            {
                if (!(_owner.GetValue(__instance) is TB_BaconShop owner)) return true;
                // Unity's == treats a destroyed ribbon as null too.
                if (Ribbon(owner) != null) return true;
                __result = false;
                return false;
            }
        }
    }
}
