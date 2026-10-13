using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace Shahed136Drones
{
    public enum FpvMode
    {
        Attack,
        Hover,
        Return
    }

    public class CompProperties_FpvDrone : CompProperties
    {
        public int batteryTicks = 30000;        // 12 in-game hours
        public float speed = 0.28f;              // cells per tick
        public float explosionRadius = 2.4f;
        public int damage = 45;
        public float armorPenetration = 0.8f;
        public int hitPoints = 1;                // one hit brings it down
        public float enemyFireRange = 26f;
        public CompProperties_FpvDrone() { compClass = typeof(CompFpvDrone); }
    }

    /// <summary>On the FPV drone item: lets the colonist holding it launch it.</summary>
    [StaticConstructorOnStartup]
    public class CompFpvDrone : ThingComp
    {
        public static readonly Texture2D LaunchIcon = ContentFinder<Texture2D>.Get("UI/Commands/LaunchFPV");
        public CompProperties_FpvDrone Props => (CompProperties_FpvDrone)props;

        public static CompFpvDrone Of(Pawn p) => p?.equipment?.Primary?.TryGetComp<CompFpvDrone>();

        public static TargetingParameters TargetParams() => new TargetingParameters
        {
            canTargetPawns = true,
            canTargetBuildings = true,
            canTargetLocations = true,
            canTargetItems = true,
            mapObjectTargetsMustBeAutoAttackable = false
        };

        public Gizmo MakeGizmo(Pawn pawn)
        {
            Command_Action cmd = new Command_Action
            {
                defaultLabel = "Launch FPV",
                defaultDesc = "Launch the FPV drone and fly it into a target: a pawn, a building or a spot on this map. It flies around mountains and over open-air walls, but can't fly under roofs, so a target inside a roofed building gets hit at the outer wall.\n\nThe colonist stays in place piloting it the whole time; if they are interrupted, drafted or downed, the link is lost and the drone falls and explodes.\n\nBattery: 12 hours. Enemies can shoot it down.",
                icon = LaunchIcon,
                action = () =>
                {
                    Find.Targeter.BeginTargeting(TargetParams(), (LocalTargetInfo t) =>
                    {
                        Job job = JobMaker.MakeJob(DefDatabase<JobDef>.GetNamed("Shahed_PilotFPV"), t);
                        pawn.jobs.TryTakeOrderedJob(job);
                    }, (Pawn)null, null, LaunchIcon);
                }
            };
            if (pawn.Downed)
                cmd.Disable("Downed.");
            else if (pawn.Spawned && pawn.Position.Roofed(pawn.Map))
                cmd.Disable("Can't launch under a roof. Step outside first.");
            return cmd;
        }
    }

    /// <summary>The FPV drone in the air. Drawn and ticked by GameComponent_DroneFlights.</summary>
    [StaticConstructorOnStartup]
    public class FpvDroneFlying : Thing, IJammable
    {
        private static readonly Texture2D HoverIcon = ContentFinder<Texture2D>.Get("UI/Commands/ReconFlyTo");
        private static readonly Texture2D ReturnIcon = ContentFinder<Texture2D>.Get("UI/Commands/ReconRecall");

        public static readonly List<FpvDroneFlying> Active = new List<FpvDroneFlying>();

        public Pawn pilot;
        public ThingDef itemDef;
        public FpvMode mode = FpvMode.Attack;
        public Thing targetThing;
        public IntVec3 targetCell = IntVec3.Invalid;
        public Vector3 exactPos;
        public float heading;
        public int battery;
        public int hp = 1;
        public Faction enemyFaction;          // set for hostile FPVs (their pilot is off-map)

        public bool IsEnemy => enemyFaction != null;
        public float JamTimeFactor => 0.4f;

        public CompProperties_FpvDrone Props =>
            itemDef?.GetCompProperties<CompProperties_FpvDrone>() ?? new CompProperties_FpvDrone();

        public override Vector3 DrawPos => exactPos;

        // ---- navigation state (not saved; rebuilt after loading) ----
        private List<IntVec3> path;
        private int pathIdx;
        private bool pathReachesGoal;
        private IntVec3 pathGoal = IntVec3.Invalid;
        private int nextRepath;
        private int stuckTicks;

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            if (!Active.Contains(this))
                Active.Add(this);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            Active.Remove(this);
            base.DeSpawn(mode);
        }

        private bool PilotOk => IsEnemy || (
            pilot != null && !pilot.Dead && !pilot.Downed && pilot.Spawned && pilot.Map == Map &&
            pilot.CurJobDef != null && pilot.CurJobDef.defName == "Shahed_PilotFPV");

        public void SetTarget(LocalTargetInfo t)
        {
            mode = FpvMode.Attack;
            targetThing = t.HasThing ? t.Thing : null;
            targetCell = t.Cell;
            path = null;
        }

        // =================================================================
        //  Main tick
        // =================================================================
        public void DroneTick()
        {
            if (!Spawned)
                return;
            Map map = Map;

            battery--;
            if (battery <= 0)
            {
                FallAndExplode("The FPV drone's battery ran out. It fell and exploded.");
                return;
            }
            if (!PilotOk)
            {
                FallAndExplode("The FPV drone lost its pilot's link. It fell and exploded.");
                return;
            }

            // ---- where are we going? ----
            Vector3 dest;
            switch (mode)
            {
                case FpvMode.Return:
                    dest = pilot.DrawPos;
                    break;
                case FpvMode.Hover:
                    dest = targetCell.ToVector3Shifted();
                    break;
                default:
                    if (IsEnemy && (targetThing == null || !targetThing.Spawned || (targetThing is Pawn tp && tp.Dead)))
                    {
                        Thing nt = PickEnemyTarget(map, exactPos.ToIntVec3());
                        if (nt != null) { targetThing = nt; targetCell = nt.Position; path = null; }
                    }
                    if (targetThing != null && targetThing.Spawned && targetThing.Map == map)
                        targetCell = targetThing.Position;
                    dest = targetThing != null && targetThing.Spawned ? targetThing.DrawPos : targetCell.ToVector3Shifted();
                    break;
            }
            dest.y = 0f;
            IntVec3 goal = dest.ToIntVec3();

            // ---- navigate ----
            bool atEnd = Navigate(map, dest, goal, out Vector3 waypoint);
            Vector3 here = new Vector3(exactPos.x, 0f, exactPos.z);
            float distToDest = (dest - here).magnitude;

            if (atEnd && !pathReachesGoal)
            {
                // got as close as possible to an unreachable spot (inside a roofed building, in rock...)
                if (mode == FpvMode.Attack)
                {
                    Detonate(Position, map);             // hit the outer wall
                    return;
                }
                if (mode == FpvMode.Return)
                {
                    Land();                              // pilot is indoors: land at the door
                    return;
                }
                // hover: just hold position here
            }
            else
            {
                float speed = Props.speed;
                if (mode == FpvMode.Hover && distToDest < 1.5f) speed = Mathf.Min(speed, 0.06f);
                Move(map, waypoint, speed, goal);
            }

            IntVec3 cell = exactPos.ToIntVec3();
            if (cell != Position && cell.InBounds(map))
                Position = cell;
            distToDest = (dest - new Vector3(exactPos.x, 0f, exactPos.z)).magnitude;

            if (mode == FpvMode.Attack && distToDest < 0.9f)
            {
                Detonate(cell, map);
                return;
            }
            if (mode == FpvMode.Return && distToDest < 1.2f)
            {
                Land();
                return;
            }

            // ---- electronic jammers of the other side: the drone collapses ----
            if (Find.TickManager.TicksGame % 10 == 0 && ElectronicJamming.Jams(map, exactPos, IsEnemy ? enemyFaction : Faction.OfPlayer))
            {
                Collapse();
                return;
            }

            if (Find.TickManager.TicksGame % 30 == 0)
            {
                EnemyFire(map, cell);
                if (Spawned && !IsEnemy && EnemyJamming.Covers(map, cell) && Rand.Chance(0.4f))
                {
                    LinkLost("Enemy jamming cut the FPV drone's link.");
                    return;
                }
            }
        }

        // =================================================================
        //  Navigation
        // =================================================================

        /// <summary>
        /// Picks the point to fly toward this tick. Returns true when the drone is at the end
        /// of its route (only meaningful when the goal itself can't be reached).
        /// </summary>
        private bool Navigate(Map map, Vector3 dest, IntVec3 goal, out Vector3 waypoint)
        {
            Vector3 here = new Vector3(exactPos.x, 0f, exactPos.z);
            waypoint = dest;
            if (!goal.InBounds(map))
                return false;

            // nothing in the way: fly straight
            if (FlyPath.Passable(map, goal) && FlyPath.SegmentClear(map, here, dest, goal))
            {
                path = null;
                pathReachesGoal = true;
                return false;
            }

            int now = Find.TickManager.TicksGame;
            if (path == null || pathGoal.DistanceToSquared(goal) > 4 || now >= nextRepath)
                Repath(map, goal);

            if (path == null || path.Count == 0)
            {
                // already standing on the closest reachable cell
                Vector3 c = exactPos.ToIntVec3().ToVector3Shifted(); c.y = 0f;
                waypoint = c;
                return !pathReachesGoal && (c - here).magnitude < 0.3f;
            }

            // advance past waypoints we've reached
            while (pathIdx < path.Count - 1)
            {
                Vector3 p = path[pathIdx].ToVector3Shifted(); p.y = 0f;
                if ((p - here).magnitude > 0.35f) break;
                pathIdx++;
            }
            // smooth the route: skip ahead to the farthest waypoint we can fly to in a straight line
            for (int k = Mathf.Min(path.Count - 1, pathIdx + 12); k > pathIdx; k--)
            {
                Vector3 p = path[k].ToVector3Shifted(); p.y = 0f;
                if (FlyPath.SegmentClear(map, here, p, goal))
                {
                    pathIdx = k;
                    break;
                }
            }

            Vector3 wp = path[pathIdx].ToVector3Shifted(); wp.y = 0f;
            bool last = pathIdx >= path.Count - 1;
            if (last && pathReachesGoal)
                wp = dest;                                      // final approach to the exact target
            waypoint = wp;
            return last && !pathReachesGoal && (wp - here).magnitude < 0.3f;
        }

        private void Repath(Map map, IntVec3 goal)
        {
            IntVec3 from = exactPos.ToIntVec3();
            if (!FlyPath.Passable(map, from))
                from = FlyPath.NearestFree(map, from);
            path = FlyPath.Find(map, from, goal, out pathReachesGoal);
            // if we're not on the start cell (pushed off), go to its center first
            if (from != exactPos.ToIntVec3())
                path.Insert(0, from);
            pathIdx = 0;
            pathGoal = goal;
            nextRepath = Find.TickManager.TicksGame + 120;
        }

        /// <summary>Move toward the waypoint. The drone's center never enters a roofed or rock cell.</summary>
        private void Move(Map map, Vector3 wp, float speed, IntVec3 goal)
        {
            Vector3 here = new Vector3(exactPos.x, 0f, exactPos.z);
            Vector3 delta = wp - here;
            float d = delta.magnitude;
            if (d < 0.02f)
            {
                stuckTicks = 0;
                return;
            }
            Vector3 np = here + delta / d * Mathf.Min(speed, d);
            IntVec3 nc = np.ToIntVec3();
            bool ok = nc == here.ToIntVec3() || nc == goal || FlyPath.Passable(map, nc);
            if (!ok)
            {
                // blocked (shouldn't happen on a fresh path): step back to the middle of a free cell and replan
                IntVec3 free = FlyPath.NearestFree(map, here.ToIntVec3());
                Vector3 fc = free.ToVector3Shifted(); fc.y = 0f;
                Vector3 dd = fc - here;
                np = dd.magnitude < 0.02f ? here : here + dd.normalized * Mathf.Min(speed, dd.magnitude);
                path = null;
            }

            if ((np - here).sqrMagnitude < 0.0001f) stuckTicks++;
            else stuckTicks = 0;
            if (stuckTicks > 20)
            {
                path = null;
                stuckTicks = 0;
            }

            exactPos.x = Mathf.Clamp(np.x, 0.5f, map.Size.x - 0.5f);   // can't leave the map
            exactPos.z = Mathf.Clamp(np.z, 0.5f, map.Size.z - 0.5f);
            Vector3 mv = np - here;
            if (mv.sqrMagnitude > 0.0001f)
            {
                float desired = Mathf.Atan2(mv.x, mv.z) * Mathf.Rad2Deg;
                heading += Mathf.Clamp(Mathf.DeltaAngle(heading, desired), -12f, 12f);
            }
        }

        // =================================================================
        //  Damage, jamming, endings
        // =================================================================

        // Shooters on the other side with line of sight take shots at it.
        private void EnemyFire(Map map, IntVec3 cell)
        {
            float range = Props.enemyFireRange;
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned.ToList())
            {
                if (p.Downed || p.Dead)
                    continue;
                bool shooter = IsEnemy ? (p.Faction == Faction.OfPlayer) : p.HostileTo(Faction.OfPlayer);
                if (!shooter)
                    continue;
                ThingWithComps gun = p.equipment?.Primary;
                if (gun == null || !gun.def.IsRangedWeapon)
                    continue;
                if (p.Position.DistanceTo(cell) > range || !GenSight.LineOfSight(p.Position, cell, map, true))
                    continue;
                if (!Rand.Chance(0.35f))
                    continue;   // not every shooter fires every half second
                gun.def.Verbs?.FirstOrDefault()?.soundCast?.PlayOneShot(new TargetInfo(p.Position, map));
                int skill = p.skills?.GetSkill(SkillDefOf.Shooting)?.Level ?? 6;
                if (Rand.Chance(0.05f + skill * 0.012f))
                {
                    hp--;
                    FleckMaker.ThrowMicroSparks(exactPos, map);
                    if (hp <= 0)
                    {
                        ShotDown();
                        return;
                    }
                    Messages.Message("An FPV drone was hit but is still flying.", this, MessageTypeDefOf.NeutralEvent, false);
                }
            }
        }

        /// <summary>Hit by gunfire: either the bullet sets off the warhead, or it just drops.</summary>
        private void ShotDown()
        {
            IntVec3 c = Position;
            Map map = Map;
            if (Rand.Chance(0.5f))
            {
                Messages.Message((IsEnemy ? "Your colonists hit an enemy FPV drone" : "Enemies hit the FPV drone") + " and its warhead went off mid-air.",
                    new TargetInfo(c, map), IsEnemy ? MessageTypeDefOf.PositiveEvent : MessageTypeDefOf.NegativeEvent, false);
                Detonate(c, map);
            }
            else
            {
                Destroy();
                FleckMaker.ThrowSmoke(c.ToVector3Shifted(), map, 1.5f);
                FleckMaker.ThrowMicroSparks(c.ToVector3Shifted(), map);
                Messages.Message((IsEnemy ? "Your colonists shot down an enemy FPV drone." : "Enemies shot down the FPV drone.") + " It crashed without exploding.",
                    new TargetInfo(c, map), IsEnemy ? MessageTypeDefOf.PositiveEvent : MessageTypeDefOf.NegativeEvent, false);
            }
        }

        /// <summary>Radio link lost (jammed): usually drops without exploding, sometimes the warhead goes off.</summary>
        private void LinkLost(string why)
        {
            IntVec3 c = Position;
            Map map = Map;
            bool boom = Rand.Chance(0.3f);
            Messages.Message(why + (boom ? " It fell and exploded." : " It crashed without exploding."), new TargetInfo(c, map),
                IsEnemy ? MessageTypeDefOf.PositiveEvent : MessageTypeDefOf.NegativeEvent, false);
            if (boom)
                Detonate(c, map);
            else
            {
                Destroy();
                FleckMaker.ThrowSmoke(c.ToVector3Shifted(), map, 1.5f);
                FleckMaker.ThrowMicroSparks(c.ToVector3Shifted(), map);
            }
        }

        /// <summary>Flew into an electronic jammer: drops to the ground. Half the time the warhead goes off,
        /// otherwise it breaks apart and leaves some salvage.</summary>
        private void Collapse()
        {
            IntVec3 c = Position;
            Map map = Map;
            bool ours = !IsEnemy;
            if (Rand.Chance(0.5f))
            {
                Messages.Message((ours ? "Your FPV drone" : "An enemy FPV drone") + " flew into a jammer, collapsed and detonated.", new TargetInfo(c, map),
                    ours ? MessageTypeDefOf.NegativeEvent : MessageTypeDefOf.PositiveEvent, false);
                Detonate(c, map);
                return;
            }
            Destroy();
            FleckMaker.ThrowSmoke(c.ToVector3Shifted(), map, 1.2f);
            FleckMaker.ThrowMicroSparks(c.ToVector3Shifted(), map);
            Thing comps = ThingMaker.MakeThing(ThingDefOf.ComponentIndustrial);
            comps.stackCount = Rand.RangeInclusive(1, 2);
            GenPlace.TryPlaceThing(comps, c, map, ThingPlaceMode.Near);
            Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
            steel.stackCount = Rand.RangeInclusive(10, 20);
            GenPlace.TryPlaceThing(steel, c, map, ThingPlaceMode.Near);
            Messages.Message((ours ? "Your FPV drone" : "An enemy FPV drone") + " flew into a jammer and fell apart without exploding.", new TargetInfo(c, map),
                ours ? MessageTypeDefOf.NegativeEvent : MessageTypeDefOf.PositiveEvent, false);
        }

        /// <summary>Jammed by a colonist with an anti-drone gun.</summary>
        public void JammedDown(Pawn by)
        {
            if (!Spawned)
                return;
            by?.skills?.Learn(SkillDefOf.Shooting, 100f);
            LinkLost((by != null ? by.LabelShortCap : "A colonist") + " jammed the enemy FPV drone.");
        }

        /// <summary>What an enemy FPV goes for: the nearest colonist or player turret.</summary>
        public static Thing PickEnemyTarget(Map map, IntVec3 from)
        {
            Thing best = null; float bestD = float.MaxValue;
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (p.Dead) continue;
                float d = p.Position.DistanceToSquared(from);
                if (d < bestD) { bestD = d; best = p; }
            }
            foreach (Building b in map.listerBuildings.allBuildingsColonist)
            {
                if (!(b is Building_Turret)) continue;
                float d = b.Position.DistanceToSquared(from) * 1.3f;    // prefer people a little
                if (d < bestD) { bestD = d; best = b; }
            }
            return best;
        }

        /// <summary>A hostile FPV flying in from the map edge at a colonist or turret (or a given target).</summary>
        public static FpvDroneFlying SpawnEnemy(Map map, Faction faction, LocalTargetInfo target)
        {
            ThingDef flyDef = DefDatabase<ThingDef>.GetNamedSilentFail("Shahed_FPVDroneFlying");
            ThingDef itemDef = DefDatabase<ThingDef>.GetNamedSilentFail("Shahed_FPVDrone");
            if (flyDef == null)
                return null;
            if (!target.IsValid)
            {
                Thing t = PickEnemyTarget(map, map.Center);
                if (t == null) return null;
                target = t;
            }
            FpvDroneFlying d = (FpvDroneFlying)ThingMaker.MakeThing(flyDef);
            d.enemyFaction = faction;
            d.itemDef = itemDef;
            CompProperties_FpvDrone pr = itemDef?.GetCompProperties<CompProperties_FpvDrone>() ?? new CompProperties_FpvDrone();
            d.battery = pr.batteryTicks;
            d.hp = pr.hitPoints;
            if (!CellFinder.TryFindRandomEdgeCellWith(c => FlyPath.Passable(map, c), map, 0f, out IntVec3 edge))
                edge = CellFinder.RandomEdgeCell(map);
            d.exactPos = edge.ToVector3Shifted(); d.exactPos.y = 0f;
            d.SetTarget(target);
            GenSpawn.Spawn(d, edge, map);
            DefDatabase<SoundDef>.GetNamedSilentFail("FPV_Launch")?.PlayOneShot(new TargetInfo(edge, map));
            return d;
        }

        private void Detonate(IntVec3 c, Map map)
        {
            CompProperties_FpvDrone pr = Props;
            Pawn instigator = pilot;
            Destroy();
            GenExplosion.DoExplosion(c, map, pr.explosionRadius, DamageDefOf.Bomb, instigator, pr.damage, pr.armorPenetration);
        }

        private void FallAndExplode(string msg)
        {
            IntVec3 c = Position;
            Map map = Map;
            Messages.Message(msg, new TargetInfo(c, map), IsEnemy ? MessageTypeDefOf.PositiveEvent : MessageTypeDefOf.NegativeEvent, false);
            Detonate(c, map);
        }

        private void Land()
        {
            Pawn p = pilot;
            Map map = Map;
            IntVec3 c = Position;
            ThingDef def = itemDef;
            Destroy();
            if (def == null)
                return;
            ThingWithComps item = (ThingWithComps)ThingMaker.MakeThing(def);
            bool nearPilot = p != null && p.Spawned && p.Map == map && p.Position.DistanceTo(c) <= 2.5f;
            if (nearPilot && !p.Dead && p.equipment != null && p.equipment.Primary == null)
            {
                p.equipment.AddEquipment(item);                        // straight back into the pilot's hands
                Messages.Message(p.LabelShortCap + " landed the FPV drone and picked it up.", p, MessageTypeDefOf.NeutralEvent, false);
            }
            else
            {
                GenPlace.TryPlaceThing(item, c, map, ThingPlaceMode.Near);
                Messages.Message("The FPV drone landed.", item, MessageTypeDefOf.NeutralEvent, false);
            }
        }

        // =================================================================
        //  UI + saving
        // =================================================================
        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
                yield return g;
            if (IsEnemy)
                yield break;
            yield return new Command_Action
            {
                defaultLabel = "Attack target",
                defaultDesc = "Fly into a new target and explode.",
                icon = CompFpvDrone.LaunchIcon,
                action = () => Find.Targeter.BeginTargeting(CompFpvDrone.TargetParams(), (LocalTargetInfo t) => SetTarget(t), (Pawn)null, null, CompFpvDrone.LaunchIcon)
            };
            yield return new Command_Action
            {
                defaultLabel = "Hover here",
                defaultDesc = "Fly to a spot and hover there, watching. If the spot is under a roof, it hovers as close as it can get.",
                icon = HoverIcon,
                action = () => Find.Targeter.BeginTargeting(new TargetingParameters { canTargetLocations = true }, (LocalTargetInfo t) =>
                {
                    mode = FpvMode.Hover;
                    targetThing = null;
                    targetCell = t.Cell;
                    path = null;
                }, (Pawn)null, null, HoverIcon)
            };
            yield return new Command_Action
            {
                defaultLabel = "Return and land",
                defaultDesc = "Cancel the attack: fly back to the pilot and land, so the drone can be used again.",
                icon = ReturnIcon,
                action = () => { mode = FpvMode.Return; path = null; }
            };
        }

        public override string GetInspectString()
        {
            if (IsEnemy)
                return "Hostile FPV drone (" + enemyFaction.Name + ").\nShoot it down, or jam it with an anti-drone gun.";
            string m = mode == FpvMode.Attack ? "attacking" : mode == FpvMode.Hover ? "hovering" : "returning to land";
            return "FPV drone: " + m + ".\nPilot: " + (pilot != null ? pilot.LabelShortCap : "none")
                + "\nBattery: " + Mathf.Max(0, battery).ToStringTicksToPeriod();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving && targetThing != null && targetThing.Destroyed)
                targetThing = null;
            Scribe_References.Look(ref pilot, "pilot");
            Scribe_Defs.Look(ref itemDef, "itemDef");
            Scribe_Values.Look(ref mode, "mode", FpvMode.Attack);
            Scribe_References.Look(ref targetThing, "targetThing");
            Scribe_Values.Look(ref targetCell, "targetCell", IntVec3.Invalid);
            Scribe_Values.Look(ref exactPos, "exactPos");
            Scribe_Values.Look(ref heading, "heading");
            Scribe_Values.Look(ref battery, "battery");
            Scribe_Values.Look(ref hp, "hp", 1);
            Scribe_References.Look(ref enemyFaction, "enemyFaction");
        }

        /// <summary>Take the FPV drone out of the pilot's hands and put it in the air.</summary>
        public static FpvDroneFlying Launch(Pawn pilot, LocalTargetInfo target)
        {
            CompFpvDrone comp = CompFpvDrone.Of(pilot);
            ThingDef flyDef = DefDatabase<ThingDef>.GetNamedSilentFail("Shahed_FPVDroneFlying");
            if (comp == null || flyDef == null)
                return null;
            ThingWithComps item = pilot.equipment.Primary;
            ThingDef itemDef = item.def;
            pilot.equipment.DestroyEquipment(item);

            // take off from open sky
            IntVec3 start = FlyPath.Passable(pilot.Map, pilot.Position) ? pilot.Position : FlyPath.NearestFree(pilot.Map, pilot.Position);

            FpvDroneFlying d = (FpvDroneFlying)ThingMaker.MakeThing(flyDef);
            d.pilot = pilot;
            d.itemDef = itemDef;
            d.battery = comp.Props.batteryTicks;
            d.hp = comp.Props.hitPoints;
            d.exactPos = start == pilot.Position ? pilot.DrawPos : start.ToVector3Shifted();
            d.exactPos.y = 0f;
            d.SetTarget(target);
            GenSpawn.Spawn(d, start, pilot.Map);
            DefDatabase<SoundDef>.GetNamedSilentFail("FPV_Launch")?.PlayOneShot(new TargetInfo(pilot.Position, pilot.Map));
            return d;
        }
    }

    /// <summary>Launch the FPV drone, then stand still piloting it until it explodes or lands.</summary>
    public class JobDriver_PilotFPV : JobDriver
    {
        private FpvDroneFlying drone;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref drone, "fpvDrone");
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            Toil launch = new Toil();
            launch.initAction = () =>
            {
                if (drone == null)
                    drone = FpvDroneFlying.Launch(pawn, job.GetTarget(TargetIndex.A));
                if (drone == null)
                    EndJobWith(JobCondition.Incompletable);
            };
            launch.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return launch;

            Toil pilotToil = Toils_General.Wait(1000000);
            pilotToil.AddEndCondition(() => drone != null && drone.Spawned ? JobCondition.Ongoing : JobCondition.Succeeded);
            yield return pilotToil;
        }
    }
}
