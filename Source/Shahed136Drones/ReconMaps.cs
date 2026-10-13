using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Shahed136Drones
{
    /// <summary>Temporary map parent for empty tiles watched by a recon drone.</summary>
    public class MapParent_ReconArea : MapParent
    {
        public override bool ShouldRemoveMapNow(out bool alsoRemoveWorldObject)
        {
            alsoRemoveWorldObject = true;
            return !GameComponent_DroneFlights.ReconOver(this) && !Map.mapPawns.AnyPawnBlockingMapRemoval;
        }
    }

    /// <summary>
    /// RimWorld closes maps nobody of yours is on (enemy settlements, sites...).
    /// Keep a map open while a recon drone circles over it, and for a few days after
    /// a settlement was bombed, so the damage stays.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ReconHarmony
    {
        static ReconHarmony()
        {
            Harmony harmony = new Harmony("custom.shahed136drones");
            MethodInfo target = AccessTools.Method(typeof(MapParent), "CheckRemoveMapNow");
            if (target != null)
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(ReconHarmony), nameof(CheckRemoveMapNow_Prefix)));
            else
                Log.Warning("[Shahed-136 Drones] MapParent.CheckRemoveMapNow not found; recon maps may close early.");

            // Vanilla assumes a settlement map only exists while your colonists are attacking it.
            // With just a drone overhead it would instantly "defeat" neutral/friendly bases
            // (no hostile defenders) and crash while writing the caravan-assault tale.
            MethodInfo defeat = AccessTools.Method(typeof(SettlementDefeatUtility), "CheckDefeated");
            if (defeat != null)
                harmony.Patch(defeat, prefix: new HarmonyMethod(typeof(ReconHarmony), nameof(CheckDefeated_Prefix)));
            else
                Log.Warning("[Shahed-136 Drones] SettlementDefeatUtility.CheckDefeated not found.");

            // some enemy combat groups bring a drone jammer
            MethodInfo genPawns = AccessTools.Method(typeof(PawnGroupMakerUtility), "GeneratePawns", new[] { typeof(PawnGroupMakerParms), typeof(bool) });
            if (genPawns != null)
                harmony.Patch(genPawns, postfix: new HarmonyMethod(typeof(EnemyJamming), nameof(EnemyJamming.GeneratePawns_Postfix)));
            else
                Log.Warning("[Loitering Munition Drones] PawnGroupMakerUtility.GeneratePawns not found; raiders won't carry jammers.");

            // "Jam drone" button for pawns holding an anti-drone gun
            MethodInfo gizmos = AccessTools.Method(typeof(Pawn), nameof(Pawn.GetGizmos));
            if (gizmos != null)
                harmony.Patch(gizmos, postfix: new HarmonyMethod(typeof(ReconHarmony), nameof(PawnGetGizmos_Postfix)));
        }

        public static void PawnGetGizmos_Postfix(Pawn __instance, ref IEnumerable<Gizmo> __result)
        {
            if (__instance == null || !__instance.Spawned || __instance.Faction != Faction.OfPlayer)
                return;
            CompDroneJammer jammer = CompDroneJammer.JammerOf(__instance);
            if (jammer != null)
                __result = __result.Concat(new Gizmo[] { jammer.MakeGizmo(__instance) });
            CompFpvDrone fpv = CompFpvDrone.Of(__instance);
            if (fpv != null)
                __result = __result.Concat(new Gizmo[] { fpv.MakeGizmo(__instance) });
        }

        public static bool CheckDefeated_Prefix(object[] __args)
        {
            Settlement s = __args != null && __args.Length > 0 ? __args[0] as Settlement : null;
            Map map = s?.Map;
            if (map == null)
                return true;
            // only let vanilla decide when your colonists are actually on that map
            return map.mapPawns.FreeColonistsSpawnedCount > 0;
        }

        public static bool CheckRemoveMapNow_Prefix(MapParent __instance)
        {
            // returning false skips the removal check
            return !GameComponent_DroneFlights.KeepMapOpen(__instance);
        }
    }
}
