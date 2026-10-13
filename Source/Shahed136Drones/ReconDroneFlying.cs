using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Shahed136Drones
{
    /// <summary>
    /// The recon drone while it flies over a loaded map. Selectable, controlled with gizmos.
    /// Not drawn by the game itself (drawn as an overlay by GameComponent_DroneFlights),
    /// and ticked by the component too.
    /// </summary>
    [StaticConstructorOnStartup]
    public class ReconDroneFlying : Thing, IJammable
    {
        private static readonly Texture2D FlyToIcon = ContentFinder<Texture2D>.Get("UI/Commands/ReconFlyTo");
        private static readonly Texture2D AutopilotIcon = ContentFinder<Texture2D>.Get("UI/Commands/ReconAutopilot");

        public const float Speed = 0.2f;          // cells per tick
        public const float TurnRate = 2.2f;       // degrees per tick
        public const int RevealRadius = 14;

        public WorldObject_ReconDrone owner;
        public Vector3 exactPos;
        public float heading;                     // degrees, 0 = north, clockwise
        public IntVec3 waypoint = IntVec3.Invalid;
        public bool leaving;
        private Vector3 exitPoint;

        // ---- enemy drones (no owner): circle over the colony, then leave ----
        public int leaveTick;
        public Vector3 orbitCenter;
        public float orbitRadius = 22f;
        public bool IsEnemy => owner == null;
        public float JamTimeFactor => 1f;
        public Faction enemyFaction;   // drones can't carry a real faction, so we remember it here

        public override Vector3 DrawPos => exactPos;

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            if (!GameComponent_DroneFlights.MapDrones.Contains(this))
                GameComponent_DroneFlights.MapDrones.Add(this);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            GameComponent_DroneFlights.MapDrones.Remove(this);
            base.DeSpawn(mode);
        }

        public static IntVec3 ClampCell(Vector3 v, Map map)
        {
            IntVec3 c = v.ToIntVec3();
            c.x = Mathf.Clamp(c.x, 0, map.Size.x - 1);
            c.z = Mathf.Clamp(c.z, 0, map.Size.z - 1);
            c.y = 0;
            return c;
        }

        // Point on a circle of radius r around center, a bit ahead of the drone (counter-clockwise loiter).
        private Vector3 OrbitTarget(Vector3 center, float r)
        {
            Vector3 rel = exactPos - center;
            float ang = Mathf.Atan2(rel.z, rel.x) + 0.5f;
            return center + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * r;
        }

        public void DroneTick()
        {
            if (!Spawned)
                return;
            Map map = Map;
            Vector3 center = map.Center.ToVector3Shifted();
            bool manual = owner != null && owner.OperatorPresent && !owner.autopilot;

            Vector3 target;
            if (leaving)
                target = exitPoint;
            else if (manual && waypoint.IsValid && waypoint.InBounds(map))
            {
                Vector3 wp = waypoint.ToVector3Shifted();
                target = (wp - exactPos).magnitude > 12f ? wp : OrbitTarget(wp, 7f);
            }
            else if (IsEnemy)
                target = OrbitTarget(orbitCenter, orbitRadius);
            else
                target = OrbitTarget(center, Mathf.Min(map.Size.x, map.Size.z) * 0.33f);

            if (IsEnemy && !leaving && Find.TickManager.TicksGame >= leaveTick)
                LeaveMap();

            // steer like a fixed-wing aircraft: limited turn rate, always moving
            Vector3 d = target - exactPos;
            float desired = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            heading += Mathf.Clamp(Mathf.DeltaAngle(heading, desired), -TurnRate, TurnRate);
            float rad = heading * Mathf.Deg2Rad;
            exactPos += new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad)) * Speed;

            if (leaving)
            {
                float m = 8f;
                if (exactPos.x < -m || exactPos.z < -m || exactPos.x > map.Size.x + m || exactPos.z > map.Size.z + m)
                {
                    WorldObject_ReconDrone o = owner;
                    Faction enemy = IsEnemy ? enemyFaction : null;
                    Destroy();
                    if (o != null)
                        o.OnMapDroneLeft();
                    else
                        EnemyDroneEscaped(map, enemy);
                    return;
                }
            }

            IntVec3 cell = ClampCell(exactPos, map);
            if (cell != Position)
                Position = cell;

            // camera: reveal what's below (only our own drones)
            if (!IsEnemy && Find.TickManager.TicksGame % 30 == 0)
            {
                foreach (IntVec3 c in GenRadial.RadialCellsAround(cell, RevealRadius, true))
                {
                    if (c.InBounds(map) && map.fogGrid.IsFogged(c))
                        map.fogGrid.Unfog(c);
                }
            }
        }

        public void LeaveMap()
        {
            if (leaving)
                return;
            leaving = true;
            // fly out over the nearest edge
            Map map = Map;
            float dl = exactPos.x, dr = map.Size.x - exactPos.x, db = exactPos.z, dt = map.Size.z - exactPos.z;
            float min = Mathf.Min(Mathf.Min(dl, dr), Mathf.Min(db, dt));
            if (min == dl) exitPoint = new Vector3(-30f, 0f, exactPos.z);
            else if (min == dr) exitPoint = new Vector3(map.Size.x + 30f, 0f, exactPos.z);
            else if (min == db) exitPoint = new Vector3(exactPos.x, 0f, -30f);
            else exitPoint = new Vector3(exactPos.x, 0f, map.Size.z + 30f);
        }

        public void Crash()
        {
            if (!Spawned)
                return;
            IntVec3 c = Position;
            Map map = Map;
            Destroy();
            GenExplosion.DoExplosion(c, map, 1.9f, DamageDefOf.Bomb, null, 10);
            FleckMaker.ThrowSmoke(c.ToVector3Shifted(), map, 2f);
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
                yield return g;
            if (owner == null)
                yield break;

            Command_Action fly = new Command_Action
            {
                defaultLabel = "Fly to",
                defaultDesc = "Fly to a spot on this map and circle over it.",
                icon = FlyToIcon,
                action = () =>
                {
                    TargetingParameters parms = new TargetingParameters { canTargetLocations = true, canTargetPawns = true, canTargetBuildings = true, canTargetItems = true, mapObjectTargetsMustBeAutoAttackable = false };
                    Find.Targeter.BeginTargeting(parms, (LocalTargetInfo t) =>
                    {
                        waypoint = t.Cell;
                        owner.autopilot = false;
                    }, (Pawn)null, null, FlyToIcon);
                }
            };
            if (!owner.OperatorPresent)
                fly.Disable("No operator. Assign a colonist to the launch rail to fly manually.");
            else if (leaving)
                fly.Disable("Leaving the map.");
            yield return fly;

            Command_Toggle auto = new Command_Toggle
            {
                defaultLabel = "Autopilot",
                defaultDesc = "Circle around the map on its own until fuel runs out or it is recalled.",
                icon = AutopilotIcon,
                isActive = () => owner.autopilot,
                toggleAction = () =>
                {
                    owner.autopilot = !owner.autopilot;
                    if (owner.autopilot) waypoint = IntVec3.Invalid;
                }
            };
            if (!owner.OperatorPresent)
                auto.Disable("No operator: the drone is always on autopilot.");
            yield return auto;

            foreach (Gizmo g in owner.ControlGizmos())
                yield return g;
        }

        public override string GetInspectString()
        {
            if (owner != null)
                return owner.GetInspectString();
            string f = enemyFaction != null ? enemyFaction.Name : "unknown";
            return "Hostile recon drone (" + f + ").\nLeaves in " + Mathf.Max(0, leaveTick - Find.TickManager.TicksGame).ToStringTicksToPeriod()
                + ". If it gets away, expect a raid.\nUse a colonist with an anti-drone gun to jam it.";
        }

        // ------------------------------------------------------------ enemy drones

        /// <summary>Spawn a hostile recon drone that enters from the map edge and circles over the colony.</summary>
        public static ReconDroneFlying SpawnEnemy(Map map, Faction faction, int stayTicks)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail("Shahed_ReconDroneFlying");
            if (def == null)
                return null;
            ReconDroneFlying d = (ReconDroneFlying)ThingMaker.MakeThing(def);
            d.enemyFaction = faction;
            d.leaveTick = Find.TickManager.TicksGame + stayTicks;

            // circle over the middle of the colony's buildings
            Vector3 sum = Vector3.zero; int n = 0;
            foreach (Building b in map.listerBuildings.allBuildingsColonist)
            {
                sum += b.Position.ToVector3Shifted();
                n++;
            }
            d.orbitCenter = n > 0 ? sum / n : map.Center.ToVector3Shifted();

            float a = Rand.Range(0f, 360f) * Mathf.Deg2Rad;
            Vector3 dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
            float half = Mathf.Max(map.Size.x, map.Size.z) * 0.5f;
            Vector3 start = map.Center.ToVector3Shifted() + dir * (half * 1.45f);
            d.exactPos = start;
            d.heading = Mathf.Atan2(-dir.x, -dir.z) * Mathf.Rad2Deg;
            GenSpawn.Spawn(d, ClampCell(start, map), map);
            return d;
        }

        private static void EnemyDroneEscaped(Map map, Faction faction)
        {
            if (map == null || faction == null || faction.defeated || !faction.HostileTo(Faction.OfPlayer))
                return;
            IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
            parms.faction = faction;
            parms.forced = true;
            Find.Storyteller.incidentQueue.Add(IncidentDefOf.RaidEnemy, Find.TickManager.TicksGame + Rand.Range(30000, 60000), parms);
            Messages.Message("The enemy recon drone got away. " + faction.Name + " now knows your defenses. Expect a raid soon.", MessageTypeDefOf.ThreatBig, false);
        }

        /// <summary>Called when a colonist finishes jamming this drone.</summary>
        public void JammedDown(Pawn by)
        {
            if (!Spawned)
                return;
            Map map = Map;
            IntVec3 c = Position;
            if (!c.Standable(map))
                CellFinder.TryFindRandomCellNear(c, map, 6, x => x.Standable(map), out c);
            Destroy();

            if (Rand.Chance(0.10f))
            {
                // lost its link and glided down intact
                ThingDef itemDef = DefDatabase<ThingDef>.GetNamedSilentFail("Shahed_ReconDrone");
                if (itemDef != null)
                {
                    Thing item = ThingMaker.MakeThing(itemDef);
                    GenPlace.TryPlaceThing(item, c, map, ThingPlaceMode.Near);
                    Messages.Message(by.LabelShortCap + " jammed the enemy drone and it landed intact! You can mount it on a launch rail.", item, MessageTypeDefOf.PositiveEvent, false);
                }
                return;
            }

            // crashed: smoke, a small bang, some salvage
            FleckMaker.ThrowSmoke(c.ToVector3Shifted(), map, 2.5f);
            GenExplosion.DoExplosion(c, map, 1.5f, DamageDefOf.Bomb, null, 6);
            Thing comps = ThingMaker.MakeThing(ThingDefOf.ComponentIndustrial);
            comps.stackCount = Rand.RangeInclusive(1, 3);
            GenPlace.TryPlaceThing(comps, c, map, ThingPlaceMode.Near);
            Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
            steel.stackCount = Rand.RangeInclusive(15, 30);
            GenPlace.TryPlaceThing(steel, c, map, ThingPlaceMode.Near);
            Messages.Message(by.LabelShortCap + " jammed the enemy drone. It crashed and left some salvage.", new TargetInfo(c, map), MessageTypeDefOf.PositiveEvent, false);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref owner, "owner");
            Scribe_Values.Look(ref exactPos, "exactPos");
            Scribe_Values.Look(ref heading, "heading");
            Scribe_Values.Look(ref waypoint, "waypoint", IntVec3.Invalid);
            Scribe_Values.Look(ref leaving, "leaving");
            Scribe_Values.Look(ref exitPoint, "exitPoint");
            Scribe_Values.Look(ref leaveTick, "leaveTick");
            Scribe_References.Look(ref enemyFaction, "enemyFaction");
            Scribe_Values.Look(ref orbitCenter, "orbitCenter");
            Scribe_Values.Look(ref orbitRadius, "orbitRadius", 22f);
        }
    }

    /// <summary>
    /// A colonist walks to the launch rail, launches the recon drone (or takes over one already
    /// in the air) and stays there controlling it until it lands or the job is interrupted.
    /// </summary>
    public class JobDriver_OperateReconDrone : JobDriver
    {
        private Building_DroneLauncher Launcher => job.GetTarget(TargetIndex.A).Thing as Building_DroneLauncher;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.InteractionCell);

            Toil prep = Toils_General.Wait(150, TargetIndex.A);
            prep.WithProgressBarToilDelay(TargetIndex.A);
            prep.FailOn(() => Launcher == null || (!Launcher.ReconReadyToLaunch && Launcher.ReconAwaitingOperator == null));
            yield return prep;

            Toil start = new Toil();
            start.initAction = () =>
            {
                if (Launcher.ReconReadyToLaunch)
                    Launcher.LaunchRecon(pawn);
                else
                    Launcher.ReconAwaitingOperator?.SetOperator(pawn);
            };
            start.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return start;

            Toil control = Toils_General.Wait(1000000, TargetIndex.A);
            control.AddEndCondition(() => GameComponent_DroneFlights.IsOperating(pawn) ? JobCondition.Ongoing : JobCondition.Succeeded);
            yield return control;
        }
    }
}
