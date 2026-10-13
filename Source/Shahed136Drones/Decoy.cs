using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

namespace Shahed136Drones
{
    /// <summary>
    /// Invisible overhead projectile that flies along with a diving Shahed. It looks like a mortar
    /// shell to other mods, so air-defense / CIWS mods (and vanilla shield projectors that stop
    /// overhead shells) can shoot it down. If it is destroyed before the drone arrives, the drone
    /// counts as intercepted. It never explodes itself.
    /// </summary>
    public class Projectile_DroneDecoy : Bullet
    {
        public bool arrived;

        public static Projectile_DroneDecoy LaunchFor(Map map, Vector3 origin, IntVec3 target, Faction faction)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail("Shahed_DroneDecoy");
            if (def == null)
                return null;
            Projectile_DroneDecoy p = (Projectile_DroneDecoy)GenSpawn.Spawn(def, origin.ToIntVec3(), map);
            // projectiles can't have a faction in RimWorld, so the decoy is simply its own launcher
            p.Launch(p, origin, new LocalTargetInfo(target), new LocalTargetInfo(target), ProjectileHitFlags.None);
            return p;
        }

        protected override void Impact(Thing hitThing, bool blockedByShield = false)
        {
            // never explodes; the drone itself does the damage
            arrived = !blockedByShield;
            Destroy();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref arrived, "arrived");
        }
    }

    /// <summary>Dev-mode debug actions (Debug actions menu → "Loitering Munition Drones").</summary>
    public static class DroneDebugActions
    {
        private static Faction EnemyFaction()
        {
            if (IncidentWorker_EnemyReconDrone.TryFindFaction(out Faction f))
                return f;
            return Find.FactionManager.RandomEnemyFaction();
        }

        [DebugAction("Loitering Munition Drones", "Spawn enemy recon drone", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SpawnEnemyRecon()
        {
            Map map = Find.CurrentMap;
            Faction f = EnemyFaction();
            if (map == null || f == null)
            {
                Messages.Message("No hostile faction to send a drone.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            ReconDroneFlying d = ReconDroneFlying.SpawnEnemy(map, f, 15000);
            if (d != null)
                Messages.Message("Enemy recon drone of " + f.Name + " spawned.", d, MessageTypeDefOf.NeutralEvent, false);
        }

        [DebugAction("Loitering Munition Drones", "Enemy Shahed strike (click target)", actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void EnemyShahedAtCell()
        {
            Map map = Find.CurrentMap;
            IntVec3 c = UI.MouseCell();
            if (map == null || !c.InBounds(map))
                return;
            Current.Game.GetComponent<GameComponent_DroneFlights>().StartEnemyDive(map, c, EnemyFaction());
        }

        [DebugAction("Loitering Munition Drones", "Enemy FPV strike (click target)", actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void EnemyFpvAtCell()
        {
            Map map = Find.CurrentMap;
            IntVec3 c = UI.MouseCell();
            if (map == null || !c.InBounds(map))
                return;
            Thing t = c.GetFirstPawn(map) ?? (Thing)c.GetFirstBuilding(map);
            FpvDroneFlying.SpawnEnemy(map, EnemyFaction(), t != null ? new LocalTargetInfo(t) : new LocalTargetInfo(c));
        }

        [DebugAction("Loitering Munition Drones", "Enemy FPV strike (3 drones at colonists)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void EnemyFpvSwarm()
        {
            Map map = Find.CurrentMap;
            if (map == null)
                return;
            for (int i = 0; i < 3; i++)
                FpvDroneFlying.SpawnEnemy(map, EnemyFaction(), LocalTargetInfo.Invalid);
        }

        [DebugAction("Loitering Munition Drones", "Enemy Shahed strike (random building)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void EnemyShahedRandom()
        {
            Map map = Find.CurrentMap;
            if (map == null)
                return;
            IntVec3 c = map.listerBuildings.allBuildingsColonist.Count > 0
                ? map.listerBuildings.allBuildingsColonist.RandomElement().Position
                : map.Center;
            Current.Game.GetComponent<GameComponent_DroneFlights>().StartEnemyDive(map, c, EnemyFaction());
        }
    }
}
