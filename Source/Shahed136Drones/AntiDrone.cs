using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace Shahed136Drones
{
    /// <summary>Anything the anti-drone gun can jam.</summary>
    public interface IJammable
    {
        bool IsEnemy { get; }
        float JamTimeFactor { get; }     // FPVs are close and small: quicker to jam
        void JammedDown(Pawn by);
    }

    public class CompProperties_DroneJammer : CompProperties
    {
        public float range = 54.9f;
        public int baseJamTicks = 1200;     // at Shooting 0; skill shortens it
        public CompProperties_DroneJammer() { compClass = typeof(CompDroneJammer); }
    }

    /// <summary>On the anti-drone gun: gives the wielder a "Jam drone" action.</summary>
    [StaticConstructorOnStartup]
    public class CompDroneJammer : ThingComp
    {
        private static readonly Texture2D JamIcon = ContentFinder<Texture2D>.Get("UI/Commands/JamDrone");
        public CompProperties_DroneJammer Props => (CompProperties_DroneJammer)props;

        public static CompDroneJammer JammerOf(Pawn p) => p?.equipment?.Primary?.TryGetComp<CompDroneJammer>();

        /// <summary>Time to break the link: 1200 ticks at Shooting 0, ~480 at 10, ~300 at 20.</summary>
        public int JamTicks(Pawn p, IJammable target = null)
        {
            int skill = p.skills?.GetSkill(SkillDefOf.Shooting)?.Level ?? 0;
            float f = target != null ? target.JamTimeFactor : 1f;
            return Mathf.Max(30, Mathf.RoundToInt(Props.baseJamTicks * f / (1f + skill * 0.15f)));
        }

        public static IEnumerable<Thing> EnemyDronesOn(Map map)
        {
            foreach (ReconDroneFlying d in GameComponent_DroneFlights.MapDrones)
                if (d.Spawned && d.Map == map && d.IsEnemy) yield return d;
            foreach (FpvDroneFlying f in FpvDroneFlying.Active)
                if (f.Spawned && f.Map == map && f.IsEnemy) yield return f;
        }

        public Gizmo MakeGizmo(Pawn pawn)
        {
            float range = Props.range;
            Command_Action cmd = new Command_Action
            {
                defaultLabel = "Jam drone",
                defaultDesc = "Aim the anti-drone gun at an enemy drone (recon or FPV) and jam its control link. Takes a while; colonists with a higher Shooting skill are faster. The drone must stay within " + range.ToString("0") + " cells.\n\nUsually the drone crashes and leaves some salvage. Sometimes it lands intact and you can keep it.",
                icon = JamIcon,
                action = () =>
                {
                    List<Thing> drones = EnemyDronesOn(pawn.Map).ToList();
                    List<Thing> inRange = drones.Where(d => d.Position.DistanceTo(pawn.Position) <= range).ToList();
                    if (inRange.Count == 1)
                    {
                        StartJam(pawn, inRange[0]);
                        return;
                    }
                    TargetingParameters parms = new TargetingParameters { canTargetLocations = true, canTargetPawns = false, canTargetBuildings = false, mapObjectTargetsMustBeAutoAttackable = false };
                    Find.Targeter.BeginTargeting(parms, (LocalTargetInfo t) =>
                    {
                        Thing best = drones.OrderBy(d => d.Position.DistanceToSquared(t.Cell)).FirstOrDefault();
                        if (best == null || best.Position.DistanceTo(t.Cell) > 8f)
                        {
                            Messages.Message("Click on an enemy drone.", MessageTypeDefOf.RejectInput, false);
                            return;
                        }
                        if (best.Position.DistanceTo(pawn.Position) > range)
                        {
                            Messages.Message("That drone is out of range of the anti-drone gun.", MessageTypeDefOf.RejectInput, false);
                            return;
                        }
                        StartJam(pawn, best);
                    }, (Pawn)null, null, JamIcon);
                }
            };
            if (pawn.Downed)
                cmd.Disable("Downed.");
            else if (!EnemyDronesOn(pawn.Map).Any())
                cmd.Disable("No enemy drone overhead.");
            return cmd;
        }

        private static void StartJam(Pawn pawn, Thing drone)
        {
            Job job = JobMaker.MakeJob(DefDatabase<JobDef>.GetNamed("Shahed_JamDrone"), drone);
            pawn.jobs.TryTakeOrderedJob(job);
        }
    }

    /// <summary>Stand, aim at the drone and hold the jammer on it until the link breaks.</summary>
    public class JobDriver_JamDrone : JobDriver
    {
        private Thing Drone => job.GetTarget(TargetIndex.A).Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOn(() => Drone == null || !Drone.Spawned || Drone.Map != pawn.Map);
            this.FailOn(() => CompDroneJammer.JammerOf(pawn) == null);

            CompDroneJammer jammer = CompDroneJammer.JammerOf(pawn);
            float range = jammer != null ? jammer.Props.range : 35f;
            int ticks = jammer != null ? jammer.JamTicks(pawn, Drone as IJammable) : 1200;

            Toil start = new Toil();
            start.initAction = () => DefDatabase<SoundDef>.GetNamedSilentFail("Jammer_Shot")?.PlayOneShot(new TargetInfo(pawn.Position, pawn.Map));
            start.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return start;

            Toil aim = Toils_General.Wait(ticks, TargetIndex.A);
            aim.WithProgressBarToilDelay(TargetIndex.A);
            aim.AddEndCondition(() =>
            {
                if (Drone != null && Drone.Spawned && Drone.Position.DistanceTo(pawn.Position) > range)
                {
                    Messages.Message(pawn.LabelShortCap + " lost the drone: it flew out of range.", pawn, MessageTypeDefOf.NeutralEvent, false);
                    return JobCondition.Incompletable;
                }
                return JobCondition.Ongoing;
            });
            yield return aim;

            Toil done = new Toil();
            done.initAction = () =>
            {
                DefDatabase<SoundDef>.GetNamedSilentFail("Jammer_Shot")?.PlayOneShot(new TargetInfo(pawn.Position, pawn.Map));
                pawn.skills?.Learn(SkillDefOf.Shooting, 250f);
                (Drone as IJammable)?.JammedDown(pawn);
            };
            done.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return done;
        }
    }

    /// <summary>A hostile faction sends a recon drone to scout your colony.</summary>
    public class IncidentWorker_EnemyReconDrone : IncidentWorker
    {
        public static bool TryFindFaction(out Faction faction)
        {
            return Find.FactionManager.AllFactions
                .Where(f => !f.IsPlayer && !f.defeated && !f.Hidden && f.def.techLevel >= TechLevel.Industrial && f.HostileTo(Faction.OfPlayer))
                .TryRandomElement(out faction);
        }

        protected override bool CanFireNowSub(IncidentParms parms)
        {
            return parms.target is Map && TryFindFaction(out _);
        }

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            Map map = parms.target as Map;
            if (map == null || !TryFindFaction(out Faction faction))
                return false;
            int stay = Rand.Range(10000, 20000);   // 4-8 hours
            ReconDroneFlying drone = ReconDroneFlying.SpawnEnemy(map, faction, stay);
            if (drone == null)
                return false;
            Find.LetterStack.ReceiveLetter("Enemy recon drone",
                "A recon drone of " + faction.Name + " is circling over your colony, mapping your defenses.\n\nIt will leave in about " + stay.ToStringTicksToPeriod() + ". If it gets away, expect a raid soon after.\n\nGive a colonist an anti-drone gun and use \"Jam drone\" to bring it down.",
                LetterDefOf.ThreatSmall, drone);
            return true;
        }
    }
}
