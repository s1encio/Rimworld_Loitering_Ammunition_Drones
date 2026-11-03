using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace Shahed136Drones
{
    /// <summary>Settings for an interceptor drone item (P1-SUN, Yolka).</summary>
    public class CompProperties_Interceptor : CompProperties
    {
        public bool autonomous;              // true = fire-and-forget (Yolka), false = piloted (P1-SUN)
        public bool canHitShahed = true;     // Shaheds diving on the map
        public bool canHitSmall = true;      // FPV and recon drones
        public float speed = 0.9f;           // cells per tick
        public float turnRate = 14f;         // degrees per tick
        public int batteryTicks = 3000;
        public float killChance = 0.85f;     // chance a pass actually kills the target
        public float acquireRange = 70f;     // how far it looks for targets / auto-launch range
        public float drawSize = 1.4f;
        public string flyTexA;
        public string flyTexB;
        public CompProperties_Interceptor() { compClass = typeof(CompInterceptor); }
    }

    /// <summary>On the interceptor item: launch button + auto-launch toggle for the colonist holding it.</summary>
    [StaticConstructorOnStartup]
    public class CompInterceptor : ThingComp
    {
        private static readonly Texture2D LaunchIcon = ContentFinder<Texture2D>.Get("UI/Commands/LaunchInterceptor");
        private static readonly Texture2D AutoIcon = ContentFinder<Texture2D>.Get("UI/Commands/ReconAutopilot");

        public bool autoLaunch = true;
        public CompProperties_Interceptor Props => (CompProperties_Interceptor)props;

        public static CompInterceptor Of(Pawn p) => p?.equipment?.Primary?.TryGetComp<CompInterceptor>();

        public IEnumerable<Gizmo> MakeGizmos(Pawn pawn)
        {
            string what = Props.canHitShahed && Props.canHitSmall ? "Shaheds, FPV and recon drones"
                        : Props.canHitShahed ? "Shaheds" : "FPV and recon drones";
            Command_Action launch = new Command_Action
            {
                defaultLabel = "Launch " + parent.def.label,
                defaultDesc = "Launch the " + parent.def.label + " at the nearest enemy drone in the air. It hunts " + what + ".\n\n"
                    + (Props.autonomous
                        ? "Fire-and-forget: it finds and chases its target on its own, the colonist is free right after throwing it."
                        : "Piloted: the colonist stays in place flying it. If they are interrupted, the link is lost and it falls.")
                    + "\n\nOne interceptor is used up per kill.",
                icon = LaunchIcon,
                action = () => Interception.StartLaunchJob(pawn)
            };
            if (pawn.Downed)
                launch.Disable("Downed.");
            else if (pawn.Spawned && pawn.Position.Roofed(pawn.Map))
                launch.Disable("Can't launch under a roof. Step outside first.");
            else if (!Interception.TargetsFor(pawn.Map, Props).Any())
                launch.Disable("No enemy drone in the air that this interceptor can hit.");
            yield return launch;

            yield return new Command_Toggle
            {
                defaultLabel = "Auto-launch",
                defaultDesc = "Launch automatically as soon as a fitting enemy drone comes within " + Props.acquireRange.ToString("0") + " cells (only while the colonist is outside and not busy fighting).",
                icon = AutoIcon,
                isActive = () => autoLaunch,
                toggleAction = () => autoLaunch = !autoLaunch
            };
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref autoLaunch, "autoLaunch", true);
        }
    }

    /// <summary>Something in the air an interceptor can chase.</summary>
    public struct AirTarget
    {
        public Thing thing;          // enemy FPV or recon drone
        public DiveAnim dive;        // enemy Shahed diving on the map
        public Vector3 pos;
        public bool IsShahed => dive != null;
    }

    public static class Interception
    {
        /// <summary>All enemy drones in the air on this map that these interceptor settings can hit.</summary>
        public static IEnumerable<AirTarget> TargetsFor(Map map, CompProperties_Interceptor props)
        {
            if (map == null)
                yield break;
            if (props.canHitSmall)
            {
                foreach (FpvDroneFlying f in FpvDroneFlying.Active)
                    if (f.Spawned && f.Map == map && f.IsEnemy)
                        yield return new AirTarget { thing = f, pos = f.exactPos };
                foreach (ReconDroneFlying r in GameComponent_DroneFlights.MapDrones)
                    if (r.Spawned && r.Map == map && r.IsEnemy)
                        yield return new AirTarget { thing = r, pos = r.exactPos };
            }
            if (props.canHitShahed)
            {
                GameComponent_DroneFlights comp = Current.Game.GetComponent<GameComponent_DroneFlights>();
                foreach (DiveAnim d in comp.EnemyDivesOn(map))
                {
                    Vector3 p = comp.DiveDrawnPos(d);
                    // only once it's over the map
                    if (p.x >= 0f && p.z >= 0f && p.x <= map.Size.x && p.z <= map.Size.z)
                        yield return new AirTarget { dive = d, pos = p };
                }
            }
        }

        public static bool StillValid(AirTarget t, Map map)
        {
            if (t.dive != null)
                return Current.Game.GetComponent<GameComponent_DroneFlights>().DiveAlive(t.dive);
            return t.thing != null && t.thing.Spawned && t.thing.Map == map;
        }

        public static Vector3 PosOf(AirTarget t)
        {
            if (t.dive != null)
                return Current.Game.GetComponent<GameComponent_DroneFlights>().DiveDrawnPos(t.dive);
            if (t.thing is FpvDroneFlying f) return f.exactPos;
            if (t.thing is ReconDroneFlying r) return r.exactPos;
            return t.thing != null ? t.thing.DrawPos : Vector3.zero;
        }

        /// <summary>Nearest target, preferring ones no other interceptor is already chasing.</summary>
        public static bool TryPick(Map map, CompProperties_Interceptor props, Vector3 from, out AirTarget best)
        {
            best = default;
            float bestD = float.MaxValue;
            bool found = false;
            foreach (AirTarget t in TargetsFor(map, props))
            {
                float d = (t.pos - from).MagnitudeHorizontal();
                if (d > props.acquireRange) continue;
                if (InterceptorFlying.Active.Any(i => i.Map == map && i.IsChasing(t))) d += 1000f;
                if (d < bestD) { bestD = d; best = t; found = true; }
            }
            return found;
        }

        public static void StartLaunchJob(Pawn pawn)
        {
            Job job = JobMaker.MakeJob(DefDatabase<JobDef>.GetNamed("Shahed_LaunchInterceptor"));
            pawn.jobs.TryTakeOrderedJob(job);
        }

        /// <summary>Called every half second: colonists with auto-launch on throw their interceptor at incoming drones.</summary>
        public static void AutoLaunchTick()
        {
            foreach (Map map in Find.Maps)
            {
                foreach (Pawn p in map.mapPawns.FreeColonistsSpawned.ToList())
                {
                    CompInterceptor c = CompInterceptor.Of(p);
                    if (c == null || !c.autoLaunch || p.Downed || p.InMentalState || p.Position.Roofed(map))
                        continue;
                    JobDef cur = p.CurJobDef;
                    if (cur != null && (cur.defName == "Shahed_LaunchInterceptor" || cur.defName == "Shahed_PilotFPV" || cur.defName == "Shahed_OperateReconDrone"))
                        continue;
                    if (p.stances?.curStance is Stance_Busy)
                        continue;   // in the middle of shooting/melee
                    if (TryPick(map, c.Props, p.DrawPos, out AirTarget t) && !InterceptorFlying.Active.Any(i => i.Map == map && i.IsChasing(t)))
                        StartLaunchJob(p);
                }
            }
        }
    }

    /// <summary>An interceptor drone in the air. Flies high, so it ignores walls and roofs.</summary>
    public class InterceptorFlying : Thing
    {
        public static readonly List<InterceptorFlying> Active = new List<InterceptorFlying>();

        public Pawn pilot;
        public ThingDef itemDef;
        public Vector3 exactPos;
        public float heading;
        public int battery;
        public AirTarget target;
        public bool hasTarget;
        private int missCooldown;
        private Vector3 home;

        public CompProperties_Interceptor Props =>
            itemDef?.GetCompProperties<CompProperties_Interceptor>() ?? new CompProperties_Interceptor();

        public override Vector3 DrawPos => exactPos;

        public bool IsChasing(AirTarget t) =>
            hasTarget && ((t.dive != null && t.dive == target.dive) || (t.thing != null && t.thing == target.thing));

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            if (!Active.Contains(this)) Active.Add(this);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            Active.Remove(this);
            base.DeSpawn(mode);
        }

        private bool PilotOk => Props.autonomous || (
            pilot != null && !pilot.Dead && !pilot.Downed && pilot.Spawned && pilot.Map == Map &&
            pilot.CurJobDef != null && pilot.CurJobDef.defName == "Shahed_LaunchInterceptor");

        public void DroneTick()
        {
            if (!Spawned)
                return;
            Map map = Map;
            battery--;
            if (battery <= 0)
            {
                Crash(Props.autonomous ? "An interceptor ran out of battery and self-destructed." : "The interceptor's battery ran out. It fell and crashed.");
                return;
            }
            if (!PilotOk)
            {
                Crash("The interceptor lost its pilot's link and crashed.");
                return;
            }

            // keep a target
            if (!hasTarget || !Interception.StillValid(target, map))
            {
                hasTarget = Interception.TryPick(map, Props, exactPos, out target);
                if (!hasTarget)
                {
                    // nothing left: piloted ones come home and land, autonomous ones loiter near launch
                    if (!Props.autonomous)
                    {
                        FlyToward(pilot.DrawPos, Props.speed * 0.6f);
                        if ((pilot.DrawPos - exactPos).MagnitudeHorizontal() < 1.2f)
                            Land();
                    }
                    else
                    {
                        float a = Find.TickManager.TicksGame * 0.05f;
                        FlyToward(home + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 6f, Props.speed * 0.5f);
                    }
                    UpdatePosition(map);
                    return;
                }
            }

            Vector3 tp = Interception.PosOf(target);
            FlyToward(tp, Props.speed);
            UpdatePosition(map);

            if (missCooldown > 0)
            {
                missCooldown--;
                return;
            }
            if ((tp - exactPos).MagnitudeHorizontal() < 1.0f)
            {
                if (Rand.Chance(Props.killChance))
                {
                    Kill(map, tp);
                    return;
                }
                missCooldown = 25;   // missed: overshoot, turn and try again
                FleckMaker.ThrowMicroSparks(exactPos, map);
            }
        }

        private void FlyToward(Vector3 p, float speed)
        {
            Vector3 d = p - exactPos; d.y = 0f;
            if (d.sqrMagnitude < 0.0001f) return;
            float desired = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            heading += Mathf.Clamp(Mathf.DeltaAngle(heading, desired), -Props.turnRate, Props.turnRate);
            float rad = heading * Mathf.Deg2Rad;
            exactPos += new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad)) * Mathf.Min(speed, Mathf.Max(0.05f, d.magnitude));
        }

        private void UpdatePosition(Map map)
        {
            exactPos.x = Mathf.Clamp(exactPos.x, 0.5f, map.Size.x - 0.5f);
            exactPos.z = Mathf.Clamp(exactPos.z, 0.5f, map.Size.z - 0.5f);
            exactPos.y = 0f;
            IntVec3 c = exactPos.ToIntVec3();
            if (c != Position && c.InBounds(map))
                Position = c;
        }

        private void Kill(Map map, Vector3 at)
        {
            AirTarget t = target;
            string name = itemDef != null ? itemDef.label : "interceptor";
            Destroy();
            FleckMaker.ThrowFireGlow(at, map, 1.2f);
            FleckMaker.ThrowSmoke(at, map, 1.5f);
            if (t.dive != null)
                Current.Game.GetComponent<GameComponent_DroneFlights>().KillDive(t.dive, "A " + name + " intercepted a Shahed-136 in mid-air!");
            else if (t.thing is FpvDroneFlying f)
            {
                Messages.Message("A " + name + " intercepted an enemy FPV drone.", new TargetInfo(f.Position, map), MessageTypeDefOf.PositiveEvent, false);
                f.ShotDown();
            }
            else if (t.thing is ReconDroneFlying r)
            {
                Messages.Message("A " + name + " intercepted an enemy recon drone.", new TargetInfo(r.Position, map), MessageTypeDefOf.PositiveEvent, false);
                r.Crash();
            }
        }

        private void Crash(string msg)
        {
            IntVec3 c = Position;
            Map map = Map;
            Destroy();
            FleckMaker.ThrowSmoke(c.ToVector3Shifted(), map, 1f);
            FleckMaker.ThrowMicroSparks(c.ToVector3Shifted(), map);
            Messages.Message(msg, new TargetInfo(c, map), MessageTypeDefOf.NeutralEvent, false);
        }

        private void Land()
        {
            Pawn p = pilot;
            Map map = Map;
            IntVec3 c = Position;
            ThingDef def = itemDef;
            Destroy();
            if (def == null) return;
            ThingWithComps item = (ThingWithComps)ThingMaker.MakeThing(def);
            if (p != null && !p.Dead && p.equipment != null && p.equipment.Primary == null)
            {
                p.equipment.AddEquipment(item);
                Messages.Message(p.LabelShortCap + " landed the interceptor: no enemy drones left.", p, MessageTypeDefOf.NeutralEvent, false);
            }
            else
                GenPlace.TryPlaceThing(item, c, map, ThingPlaceMode.Near);
        }

        public override string GetInspectString()
        {
            string s = (itemDef != null ? itemDef.LabelCap.ToString() : "Interceptor") + (hasTarget ? (target.IsShahed ? ": chasing a Shahed." : ": chasing a drone.") : ": searching.");
            if (!Props.autonomous)
                s += "\nPilot: " + (pilot != null ? pilot.LabelShortCap : "none");
            return s + "\nBattery: " + Mathf.Max(0, battery).ToStringTicksToPeriod();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref pilot, "pilot");
            Scribe_Defs.Look(ref itemDef, "itemDef");
            Scribe_Values.Look(ref exactPos, "exactPos");
            Scribe_Values.Look(ref heading, "heading");
            Scribe_Values.Look(ref battery, "battery");
            Scribe_Values.Look(ref home, "home");
            // the target is re-acquired after loading
        }

        public static InterceptorFlying Launch(Pawn pawn)
        {
            CompInterceptor comp = CompInterceptor.Of(pawn);
            ThingDef flyDef = DefDatabase<ThingDef>.GetNamedSilentFail("Shahed_InterceptorFlying");
            if (comp == null || flyDef == null)
                return null;
            CompProperties_Interceptor pr = comp.Props;
            if (!Interception.TryPick(pawn.Map, pr, pawn.DrawPos, out AirTarget t))
            {
                Messages.Message("No enemy drone in range.", pawn, MessageTypeDefOf.RejectInput, false);
                return null;
            }
            ThingWithComps item = pawn.equipment.Primary;
            ThingDef itemDef = item.def;
            pawn.equipment.DestroyEquipment(item);

            InterceptorFlying d = (InterceptorFlying)ThingMaker.MakeThing(flyDef);
            d.pilot = pr.autonomous ? null : pawn;
            d.itemDef = itemDef;
            d.battery = pr.batteryTicks;
            d.exactPos = pawn.DrawPos; d.exactPos.y = 0f;
            d.home = d.exactPos;
            d.target = t;
            d.hasTarget = true;
            Vector3 dir = t.pos - d.exactPos;
            d.heading = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            GenSpawn.Spawn(d, pawn.Position, pawn.Map);
            DefDatabase<SoundDef>.GetNamedSilentFail("FPV_Launch")?.PlayOneShot(new TargetInfo(pawn.Position, pawn.Map));
            return d;
        }
    }

    /// <summary>Launch: a short throw, then (piloted only) stay in place flying it.</summary>
    public class JobDriver_LaunchInterceptor : JobDriver
    {
        private InterceptorFlying drone;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref drone, "interceptor");
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            yield return Toils_General.Wait(20);   // aim + throw

            Toil launch = new Toil();
            launch.initAction = () =>
            {
                if (drone == null)
                    drone = InterceptorFlying.Launch(pawn);
                if (drone == null || drone.Props.autonomous)
                    EndJobWith(drone == null ? JobCondition.Incompletable : JobCondition.Succeeded);
            };
            launch.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return launch;

            Toil fly = Toils_General.Wait(1000000);
            fly.AddEndCondition(() => drone != null && drone.Spawned ? JobCondition.Ongoing : JobCondition.Succeeded);
            yield return fly;
        }
    }

    /// <summary>A hostile faction launches a wave of Shaheds at your colony.</summary>
    public class IncidentWorker_EnemyShahedStrike : IncidentWorker
    {
        protected override bool CanFireNowSub(IncidentParms parms)
        {
            return parms.target is Map map && map.listerBuildings.allBuildingsColonist.Count > 0 && IncidentWorker_EnemyReconDrone.TryFindFaction(out _);
        }

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            Map map = parms.target as Map;
            if (map == null || !IncidentWorker_EnemyReconDrone.TryFindFaction(out Faction faction))
                return false;
            int n = Rand.RangeInclusive(1, 4);
            return Current.Game.GetComponent<GameComponent_DroneFlights>().StartEnemyStrike(map, faction, n);
        }
    }
}
