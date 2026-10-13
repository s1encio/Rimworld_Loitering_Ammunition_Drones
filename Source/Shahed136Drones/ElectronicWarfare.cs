using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace Shahed136Drones
{
    // ======================================================================
    //  Portable jammer (worn in the utility slot)
    // ======================================================================
    public class CompProperties_PortableJammer : CompProperties
    {
        public float radius = 12.9f;
        public int batteryTicks = 60000;        // 24 hours
        public int ticksPerChemfuel = 2500;     // 1 chemfuel = 1 hour
        public CompProperties_PortableJammer() { compClass = typeof(CompPortableJammer); }
    }

    [StaticConstructorOnStartup]
    public class CompPortableJammer : ThingComp
    {
        private static readonly Texture2D ToggleIcon = ContentFinder<Texture2D>.Get("UI/Commands/JamDrone");
        private static readonly Texture2D RechargeIcon = ContentFinder<Texture2D>.Get("UI/Commands/RechargeJammer");

        public int battery = -1;
        public bool on = true;

        public CompProperties_PortableJammer Props => (CompProperties_PortableJammer)props;
        public Pawn Wearer => (parent as Apparel)?.Wearer;
        public bool Active => on && battery > 0 && Wearer != null && Wearer.Spawned && !Wearer.Dead && !Wearer.Downed;
        public int MissingChemfuel => Mathf.CeilToInt((Props.batteryTicks - battery) / (float)Props.ticksPerChemfuel);

        public override void PostPostMake()
        {
            base.PostPostMake();
            battery = Props.batteryTicks;           // comes fully charged
        }

        public void Drain(int ticks)
        {
            if (battery < 0) battery = Props.batteryTicks;
            if (!on || battery <= 0)
                return;
            battery = Mathf.Max(0, battery - ticks);
            if (battery == 0 && Wearer != null && Wearer.Faction == Faction.OfPlayer)
                Messages.Message(Wearer.LabelShortCap + "'s portable jammer battery is empty. Recharge it with chemfuel.", Wearer, MessageTypeDefOf.CautionInput, false);
        }

        public void AddChemfuel(int count)
        {
            battery = Mathf.Min(Props.batteryTicks, battery + count * Props.ticksPerChemfuel);
        }

        public override IEnumerable<Gizmo> CompGetWornGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetWornGizmosExtra())
                yield return g;
            Pawn w = Wearer;
            if (w == null || w.Faction != Faction.OfPlayer)
                yield break;
            float hours = battery / 2500f;
            yield return new Command_Toggle
            {
                defaultLabel = "Jammer " + (on ? "ON" : "OFF") + " (" + hours.ToString("0.0") + " h)",
                defaultDesc = "Switch the portable jammer on or off. While on, hostile FPV drones entering " + Props.radius.ToString("0") + " cells collapse. Battery: " + hours.ToString("0.0") + " of " + (Props.batteryTicks / 2500f).ToString("0") + " hours.",
                icon = ToggleIcon,
                isActive = () => on,
                toggleAction = () => on = !on
            };
            Command_Action recharge = new Command_Action
            {
                defaultLabel = "Recharge",
                defaultDesc = "Go fetch chemfuel and recharge the jammer's generator (1 chemfuel = 1 hour of battery). Needs " + MissingChemfuel + " chemfuel for a full charge.",
                icon = RechargeIcon,
                action = () =>
                {
                    Thing fuel = GenClosest.ClosestThingReachable(w.Position, w.Map, ThingRequest.ForDef(ThingDefOf.Chemfuel), PathEndMode.ClosestTouch,
                        TraverseParms.For(w), 9999f, t => !t.IsForbidden(w) && w.CanReserve(t));
                    if (fuel == null)
                    {
                        Messages.Message("No reachable chemfuel.", w, MessageTypeDefOf.RejectInput, false);
                        return;
                    }
                    Job job = JobMaker.MakeJob(DefDatabase<JobDef>.GetNamed("Shahed_RechargeJammer"), fuel, parent);
                    job.count = Mathf.Min(MissingChemfuel, fuel.stackCount);
                    w.jobs.TryTakeOrderedJob(job);
                }
            };
            if (MissingChemfuel <= 0)
                recharge.Disable("Fully charged.");
            yield return recharge;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref battery, "jamBattery", -1);
            Scribe_Values.Look(ref on, "jamOn", true);
        }
    }

    /// <summary>Walk to chemfuel, pick it up, pour it into the jammer.</summary>
    public class JobDriver_RechargeJammer : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed) =>
            pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch).FailOnDespawnedNullOrForbidden(TargetIndex.A);
            yield return Toils_Haul.StartCarryThing(TargetIndex.A);
            yield return Toils_General.Wait(120);
            Toil done = new Toil();
            done.initAction = () =>
            {
                Thing carried = pawn.carryTracker.CarriedThing;
                CompPortableJammer jam = job.GetTarget(TargetIndex.B).Thing?.TryGetComp<CompPortableJammer>();
                if (carried == null || jam == null)
                    return;
                int n = Mathf.Min(carried.stackCount, Mathf.Max(0, jam.MissingChemfuel));
                jam.AddChemfuel(n);
                if (n >= carried.stackCount)
                    pawn.carryTracker.DestroyCarriedThing();
                else
                {
                    carried.SplitOff(n).Destroy();
                    pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
                }
                Messages.Message(pawn.LabelShortCap + " recharged the portable jammer.", pawn, MessageTypeDefOf.PositiveEvent, false);
            };
            done.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return done;
        }
    }

    // ======================================================================
    //  Stationary jammer settings
    // ======================================================================
    public class ElectronicJammerExtension : DefModExtension
    {
        public float radius = 29.9f;
    }

    // ======================================================================
    //  Shared: who is jamming where
    // ======================================================================
    public static class ElectronicJamming
    {
        private const string StationDef = "Shahed_EWJammerStation";

        /// <summary>Is a jammer hostile to the drone's side covering this spot?</summary>
        public static bool Jams(Map map, Vector3 pos, Faction droneSide)
        {
            if (droneSide == null)
                return false;
            // stationary jammers
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(StationDef);
            if (def != null)
            {
                List<Thing> list = map.listerThings.ThingsOfDef(def);
                for (int i = 0; i < list.Count; i++)
                {
                    Thing t = list[i];
                    if (t.Faction == null || !t.Faction.HostileTo(droneSide)) continue;
                    CompPowerTrader power = t.TryGetComp<CompPowerTrader>();
                    if (power != null && !power.PowerOn) continue;
                    float r = t.def.GetModExtension<ElectronicJammerExtension>()?.radius ?? 29.9f;
                    if ((t.TrueCenter() - pos).MagnitudeHorizontal() <= r) return true;
                }
            }
            // portable jammers worn by pawns
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                if (p.apparel == null || p.Faction == null || !p.Faction.HostileTo(droneSide)) continue;
                foreach (Apparel a in p.apparel.WornApparel)
                {
                    CompPortableJammer j = a.TryGetComp<CompPortableJammer>();
                    if (j != null && j.Active && (p.DrawPos - pos).MagnitudeHorizontal() <= j.Props.radius)
                        return true;
                }
            }
            return false;
        }

        /// <summary>Battery drain for every worn portable jammer (called every 60 ticks).</summary>
        public static void DrainAll(int ticks)
        {
            foreach (Map map in Find.Maps)
                foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
                {
                    if (p.apparel == null) continue;
                    foreach (Apparel a in p.apparel.WornApparel)
                        a.TryGetComp<CompPortableJammer>()?.Drain(ticks);
                }
        }

        /// <summary>Show the jamming circle of selected colonists wearing an active jammer.</summary>
        public static void DrawSelectedRings()
        {
            foreach (object o in Find.Selector.SelectedObjectsListForReading)
            {
                Pawn p = o as Pawn;
                if (p?.apparel == null || p.Map != Find.CurrentMap) continue;
                foreach (Apparel a in p.apparel.WornApparel)
                {
                    CompPortableJammer j = a.TryGetComp<CompPortableJammer>();
                    if (j != null && j.Active)
                        GenDraw.DrawRadiusRing(p.Position, j.Props.radius);
                }
            }
        }
    }
}
