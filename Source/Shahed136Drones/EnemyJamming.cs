using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Shahed136Drones
{
    /// <summary>
    /// Enemy jamming against your FPV drones:
    ///  - hostile pawns carrying an anti-drone gun (equipped or in their inventory), and
    ///  - hostile industrial settlements, which have one or two jammers hidden among their buildings.
    /// </summary>
    public static class EnemyJamming
    {
        public const float PawnJamRange = 30f;
        public const float BaseJamRange = 32f;
        private static readonly Dictionary<int, List<IntVec3>> baseJammers = new Dictionary<int, List<IntVec3>>();

        private static bool CarriesJammer(Pawn p)
        {
            if (p.equipment?.Primary?.TryGetComp<CompDroneJammer>() != null)
                return true;
            if (p.inventory != null)
                foreach (Thing t in p.inventory.innerContainer)
                    if (t.TryGetComp<CompDroneJammer>() != null) return true;
            return false;
        }

        private static List<IntVec3> BaseJammersOf(Map map)
        {
            Settlement s = map.Parent as Settlement;
            if (s == null || s.Faction == null || s.Faction == Faction.OfPlayer || !s.Faction.HostileTo(Faction.OfPlayer)
                || s.Faction.def.techLevel < TechLevel.Industrial)
                return null;
            if (baseJammers.TryGetValue(map.uniqueID, out List<IntVec3> list))
                return list;
            list = new List<IntVec3>();
            List<Building> blds = new List<Building>();
            foreach (Building b in map.listerBuildings.allBuildingsNonColonist)
                if (b.Faction == s.Faction) blds.Add(b);
            if (blds.Count > 0)
            {
                Rand.PushState(map.uniqueID);          // same spots every time for this map
                int n = Rand.RangeInclusive(1, 2);
                for (int i = 0; i < n; i++) list.Add(blds.RandomElement().Position);
                Rand.PopState();
            }
            baseJammers[map.uniqueID] = list;
            return list;
        }

        public static bool Covers(Map map, IntVec3 cell)
        {
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                if (p.Dead || p.Downed || !p.HostileTo(Faction.OfPlayer)) continue;
                if (p.Position.DistanceTo(cell) <= PawnJamRange && CarriesJammer(p))
                    return true;
            }
            List<IntVec3> bj = BaseJammersOf(map);
            if (bj != null)
                foreach (IntVec3 c in bj)
                    if (c.DistanceTo(cell) <= BaseJamRange) return true;
            return false;
        }

        /// <summary>Harmony postfix on PawnGroupMakerUtility.GeneratePawns: some enemy combat groups bring a jammer.</summary>
        public static void GeneratePawns_Postfix(object[] __args, ref IEnumerable<Pawn> __result)
        {
            PawnGroupMakerParms parms = __args != null && __args.Length > 0 ? __args[0] as PawnGroupMakerParms : null;
            if (__result == null)
                return;
            if (parms == null || parms.faction == null || parms.faction.IsPlayer || parms.groupKind != PawnGroupKindDefOf.Combat
                || parms.faction.def.techLevel < TechLevel.Industrial || !parms.faction.HostileTo(Faction.OfPlayer))
                return;
            List<Pawn> pawns = new List<Pawn>(__result);
            __result = pawns;
            if (pawns.Count < 3 || !Rand.Chance(0.25f))
                return;
            ThingDef gun = DefDatabase<ThingDef>.GetNamedSilentFail("Shahed_AntiDroneGun");
            if (gun == null)
                return;
            List<Pawn> humans = pawns.FindAll(p => p.RaceProps.Humanlike && p.inventory != null);
            if (humans.Count == 0)
                return;
            Pawn carrier = humans.RandomElement();
            ThingDef portable = DefDatabase<ThingDef>.GetNamedSilentFail("Shahed_PortableJammer");
            if (portable != null && carrier.apparel != null && Rand.Chance(0.5f))
                carrier.apparel.Wear((Apparel)ThingMaker.MakeThing(portable), false);    // wears a portable jammer
            else
                carrier.inventory.innerContainer.TryAdd(ThingMaker.MakeThing(gun));       // or carries an anti-drone gun
        }
    }

    /// <summary>Hostile factions send FPV drones at your colonists and turrets.</summary>
    public class IncidentWorker_EnemyFPVStrike : IncidentWorker
    {
        protected override bool CanFireNowSub(IncidentParms parms)
        {
            return parms.target is Map map && map.mapPawns.FreeColonistsSpawnedCount > 0 && IncidentWorker_EnemyReconDrone.TryFindFaction(out _);
        }

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            Map map = parms.target as Map;
            if (map == null || !IncidentWorker_EnemyReconDrone.TryFindFaction(out Faction faction))
                return false;
            int n = Rand.RangeInclusive(1, 3);
            FpvDroneFlying first = null;
            for (int i = 0; i < n; i++)
            {
                FpvDroneFlying d = FpvDroneFlying.SpawnEnemy(map, faction, LocalTargetInfo.Invalid);
                if (first == null) first = d;
            }
            if (first == null)
                return false;
            Find.LetterStack.ReceiveLetter("Enemy FPV drones",
                faction.Name + " is sending " + n + " FPV drone" + (n > 1 ? "s" : "") + " at your colony. They go for your colonists and turrets.\n\nShoot them down, or jam them with an anti-drone gun.",
                LetterDefOf.ThreatBig, first);
            return true;
        }
    }
}
