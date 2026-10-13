using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace Shahed136Drones
{
    /// <summary>Settings on the GNSS jammer building.</summary>
    public class GnssJammerExtension : DefModExtension
    {
        public float radius = 29.9f;
        public float driftMin = 8f;
        public float driftMax = 20f;
        public float crashChance = 0.15f;
        public bool beatsAntiJam;          // spoofers (Lima) also fool anti-jam antennas
    }

    public static class GnssJamming
    {
        /// <summary>Your own Shaheds ignore jammers once this is researched.</summary>
        public static bool AntiJamResearched =>
            DefDatabase<ResearchProjectDef>.GetNamedSilentFail("Shahed_AntiJamAntenna")?.IsFinished ?? false;

        private static readonly string[] JammerDefs = { "Shahed_LimaSpoofer" };

        /// <summary>
        /// Powered jammers/spoofers covering this position. If the drone has an anti-jam antenna,
        /// only spoofers that beat it count. ext = settings of the strongest one.
        /// </summary>
        public static int JammersCovering(Map map, Vector3 pos, bool antiJam, out GnssJammerExtension ext)
        {
            ext = null;
            int n = 0;
            foreach (string defName in JammerDefs)
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                if (def == null)
                    continue;
                List<Thing> list = map.listerThings.ThingsOfDef(def);
                for (int i = 0; i < list.Count; i++)
                {
                    Thing t = list[i];
                    CompPowerTrader power = t.TryGetComp<CompPowerTrader>();
                    if (power != null && !power.PowerOn)
                        continue;   // unpowered, switched off or broken down
                    GnssJammerExtension e = t.def.GetModExtension<GnssJammerExtension>() ?? new GnssJammerExtension();
                    if (antiJam && !e.beatsAntiJam)
                        continue;
                    if ((t.TrueCenter() - pos).MagnitudeHorizontal() <= e.radius)
                    {
                        n++;
                        if (ext == null || e.driftMax > ext.driftMax) ext = e;
                    }
                }
            }
            return n;
        }
    }
}
