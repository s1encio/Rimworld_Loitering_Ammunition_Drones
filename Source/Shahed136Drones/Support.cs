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

        // ---- the drone type that carries this warhead ----
        public string label = "Shahed-136";
        public string topTex = "Things/Shahed136/Shahed136_Top";
        public float sizeFactor = 1f;          // size in flight relative to the Shahed
        public float railScale = 1f;           // size on the launch rail relative to the Shahed
        public float speed = 0.3f;             // cells per tick on the final approach
        public int hitsToKill = 2;             // air-defense hits it survives minus one
        public int range = 20;                 // world tiles
        public int extendedRange = 45;         // after long-range navigation
        public float ticksPerTile = 150f;
        public bool localStrike = false;       // may hit the map it was launched from
        public string incomingSound = "Shahed_Incoming";
        public string launchSound = "Shahed_Launch";
        public float smokeTrail = 1f;          // 0 = electric, no exhaust
        public float enemyWeight = 0f;         // chance weight in enemy drone strikes

        private static readonly Dictionary<string, UnityEngine.Texture2D> texCache = new Dictionary<string, UnityEngine.Texture2D>();
        private static readonly Dictionary<string, UnityEngine.Material> shadowCache = new Dictionary<string, UnityEngine.Material>();
        private static readonly Dictionary<string, UnityEngine.Material> matCache = new Dictionary<string, UnityEngine.Material>();

        public static DroneWarheadExtension Of(ThingDef def) => def?.GetModExtension<DroneWarheadExtension>() ?? Default;

        public UnityEngine.Texture2D Tex
        {
            get
            {
                if (!texCache.TryGetValue(topTex, out UnityEngine.Texture2D t))
                {
                    t = ContentFinder<UnityEngine.Texture2D>.Get(topTex, false) ?? ContentFinder<UnityEngine.Texture2D>.Get("Things/Shahed136/Shahed136_Top");
                    texCache[topTex] = t;
                }
                return t;
            }
        }

        public UnityEngine.Material ShadowMat
        {
            get
            {
                if (!shadowCache.TryGetValue(topTex, out UnityEngine.Material m))
                {
                    m = MaterialPool.MatFrom(topTex, ShaderDatabase.Transparent, new UnityEngine.Color(0f, 0f, 0f, 0.35f));
                    shadowCache[topTex] = m;
                }
                return m;
            }
        }

        public UnityEngine.Material Mat
        {
            get
            {
                if (!matCache.TryGetValue(topTex, out UnityEngine.Material m))
                {
                    m = MaterialPool.MatFrom(topTex, ShaderDatabase.Cutout);
                    matCache[topTex] = m;
                }
                return m;
            }
        }

        /// <summary>A random drone type for an enemy strike, by enemyWeight.</summary>
        public static ThingDef RandomEnemyType()
        {
            List<ThingDef> types = new List<ThingDef>();
            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                DroneWarheadExtension e = d.GetModExtension<DroneWarheadExtension>();
                if (e != null && e.enemyWeight > 0f)
                    types.Add(d);
            }
            if (types.Count == 0)
                return DefDatabase<ThingDef>.GetNamedSilentFail("Shahed136Incoming");
            return types.RandomElementByWeight(d => d.GetModExtension<DroneWarheadExtension>().enemyWeight);
        }
    }

    /// <summary>On an "assemble ..." recipe: which drone type it mounts on the rail.</summary>
    public class DroneRecipeExtension : DefModExtension
    {
        public ThingDef warhead;
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
                ThingDef type = recipe?.GetModExtension<DroneRecipeExtension>()?.warhead;
                launcher.AddDrone(type);
                Messages.Message(DroneWarheadExtension.Of(launcher.LoadedType).label + " mounted on the launch rail (" + launcher.LoadedDrones + "/" + launcher.Ext.capacity + ").",
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
