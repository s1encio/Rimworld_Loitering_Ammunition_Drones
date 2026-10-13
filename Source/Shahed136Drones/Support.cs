using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace Shahed136Drones
{
    /// <summary>Settings for a launch rail, set in the launcher ThingDef's modExtensions.</summary>
    public class DroneLauncherExtension : DefModExtension
    {
        public static readonly DroneLauncherExtension Default = new DroneLauncherExtension();

        public int capacity = 1;
        public int range = 20;
        public int extendedRange = 45;
        public ResearchProjectDef rangeResearch;
        public float ticksPerTile = 150f;
        public int minFlightTicks = 300;
        public ThingDef strikeSkyfaller;
        public int settlementGoodwillImpact = -25;
        // recon drone
        public int reconRange = 30;
        public float reconTicksPerTile = 100f;
        public int reconFuelTicks = 60000;
    }

    /// <summary>Warhead settings, stored on the Shahed136Incoming ThingDef.</summary>
    public class DroneWarheadExtension : DefModExtension
    {
        public static readonly DroneWarheadExtension Default = new DroneWarheadExtension();

        public float explosionRadius = 9.9f;
        public int damage = 250;
        public float armorPenetration = 2f;
        public int secondaryExplosions = 4;
        public float secondaryRadius = 4.5f;
        public int secondaryDamage = 120;
        public float fireRadius = 6.9f;
        public int fireDamage = 8;
        public float cameraShake = 4f;
    }

    public static class Warhead
    {
        public static void Detonate(IntVec3 c, Map map, DroneWarheadExtension ext)
        {
            if (map == null)
                return;
            // main blast
            GenExplosion.DoExplosion(c, map, ext.explosionRadius, DamageDefOf.Bomb, null, ext.damage, ext.armorPenetration);
            // fragmentation / secondary blasts scattered around the impact point
            for (int i = 0; i < ext.secondaryExplosions; i++)
            {
                IntVec3 o = c + (Rand.InsideUnitCircleVec3 * ext.explosionRadius * 0.6f).ToIntVec3();
                if (o.InBounds(map))
                    GenExplosion.DoExplosion(o, map, ext.secondaryRadius, DamageDefOf.Bomb, null, ext.secondaryDamage, ext.armorPenetration);
            }
            // fuel fire
            if (ext.fireRadius > 0f)
                GenExplosion.DoExplosion(c, map, ext.fireRadius, DamageDefOf.Flame, null, ext.fireDamage);
            if (Find.CurrentMap == map && ext.cameraShake > 0f)
                Find.CameraDriver.shaker.DoShake(ext.cameraShake);
        }
    }

    /// <summary>
    /// The recipe has no products: when a bill finishes, the drone is mounted
    /// straight onto the launch rail the pawn was working at.
    /// </summary>
    public class RecipeWorker_AssembleDrone : RecipeWorker
    {
        public override void Notify_IterationCompleted(Pawn billDoer, List<Thing> ingredients)
        {
            base.Notify_IterationCompleted(billDoer, ingredients);
            Building_DroneLauncher launcher = billDoer?.CurJob?.GetTarget(TargetIndex.A).Thing as Building_DroneLauncher;
            if (launcher != null)
            {
                launcher.AddDrone();
                Messages.Message("Shahed-136 mounted on the launch rail (" + launcher.LoadedDrones + "/" + launcher.Ext.capacity + ").",
                    launcher, MessageTypeDefOf.PositiveEvent, false);
            }
        }
    }

    /// <summary>Assembling or mounting a recon drone puts it on the launch rail.</summary>
    public class RecipeWorker_AssembleRecon : RecipeWorker
    {
        public override void Notify_IterationCompleted(Pawn billDoer, List<Thing> ingredients)
        {
            base.Notify_IterationCompleted(billDoer, ingredients);
            Building_DroneLauncher launcher = billDoer?.CurJob?.GetTarget(TargetIndex.A).Thing as Building_DroneLauncher;
            if (launcher != null)
            {
                launcher.AddRecon();
                Messages.Message("Recon drone mounted on the launch rail.", launcher, MessageTypeDefOf.PositiveEvent, false);
            }
        }
    }

    /// <summary>Normal bill work giver, but pawns skip the launch rail while it is full.</summary>
    public class WorkGiver_DoBillDroneLauncher : WorkGiver_DoBill
    {
        public override Job JobOnThing(Pawn pawn, Thing thing, bool forced = false)
        {
            Building_DroneLauncher launcher = thing as Building_DroneLauncher;
            if (launcher != null && launcher.IsFull)
            {
                JobFailReason.Is("Launch rail is full");
                return null;
            }
            return base.JobOnThing(pawn, thing, forced);
        }
    }

    /// <summary>Legacy: only used by saves that still contain a falling drone.</summary>
    public class Skyfaller_Shahed : Skyfaller
    {
        protected override void Impact()
        {
            DroneWarheadExtension ext = def.GetModExtension<DroneWarheadExtension>() ?? DroneWarheadExtension.Default;
            IntVec3 pos = Position;
            Map map = Map;
            Destroy();
            Warhead.Detonate(pos, map, ext);
        }
    }
}
