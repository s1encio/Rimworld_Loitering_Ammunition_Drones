using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace Shahed136Drones
{
    /// <summary>
    /// Remembers enemy settlements you have struck with a Shahed-136.
    /// - Their map stays loaded for a few days after the last strike, so the damage stays.
    /// - When most of their fighters are dead or downed, the rest flee and the base is abandoned.
    /// </summary>
    public class SiegeRecord : IExposable
    {
        public Settlement settlement;
        public int lastStrikeTick;
        public int baselineFighters;
        public bool fleeing;

        public void ExposeData()
        {
            Scribe_References.Look(ref settlement, "settlement");
            Scribe_Values.Look(ref lastStrikeTick, "lastStrikeTick");
            Scribe_Values.Look(ref baselineFighters, "baselineFighters");
            Scribe_Values.Look(ref fleeing, "fleeing");
        }
    }

    public static class SettlementSiege
    {
        public const int KeepMapTicks = 180000;       // 3 days after the last strike
        public const float FleeThreshold = 0.4f;      // flee when 40% or less of the fighters are left

        public static int CountFighters(Map map, Faction faction)
        {
            int n = 0;
            foreach (Pawn p in map.mapPawns.SpawnedPawnsInFaction(faction))
            {
                if (!p.Dead && !p.Downed && (p.RaceProps.Humanlike || p.RaceProps.IsMechanoid))
                    n++;
            }
            return n;
        }

        /// <summary>Called right before a Shahed explodes on a map.</summary>
        public static void NotifyStrike(List<SiegeRecord> records, Map map)
        {
            Settlement s = map?.Parent as Settlement;
            if (s == null || s.Faction == null || s.Faction == Faction.OfPlayer)
                return;
            SiegeRecord rec = records.Find(r => r.settlement == s);
            if (rec == null)
            {
                rec = new SiegeRecord { settlement = s, baselineFighters = CountFighters(map, s.Faction) };
                records.Add(rec);
            }
            rec.lastStrikeTick = Find.TickManager.TicksGame;
        }

        public static bool KeepMapOpen(List<SiegeRecord> records, MapParent mp)
        {
            if (records == null || mp == null)
                return false;
            int now = Find.TickManager.TicksGame;
            foreach (SiegeRecord r in records)
            {
                if (r.settlement == mp && !r.settlement.Destroyed && now - r.lastStrikeTick < KeepMapTicks)
                    return true;
            }
            return false;
        }

        /// <summary>Checked every few seconds: morale, fleeing and defeat.</summary>
        public static void Tick(List<SiegeRecord> records)
        {
            int now = Find.TickManager.TicksGame;
            for (int i = records.Count - 1; i >= 0; i--)
            {
                SiegeRecord r = records[i];
                Settlement s = r.settlement;
                if (s == null || s.Destroyed || !s.HasMap)
                {
                    // defeated, or its map was closed after the keep-open window: forget it
                    if (s == null || s.Destroyed || now - r.lastStrikeTick >= KeepMapTicks)
                        records.RemoveAt(i);
                    continue;
                }
                Map map = s.Map;
                Faction f = s.Faction;
                int fighters = CountFighters(map, f);

                if (!r.fleeing && r.baselineFighters > 0 && fighters <= r.baselineFighters * FleeThreshold)
                {
                    r.fleeing = true;
                    MakeSurvivorsFlee(map, f);
                    Find.LetterStack.ReceiveLetter("Enemy abandoning " + s.LabelCap,
                        "The drone strikes have broken the defenders of " + s.LabelCap + " (" + f.Name + "). The survivors are fleeing and abandoning their base.",
                        LetterDefOf.PositiveEvent, new LookTargets(new GlobalTargetInfo(s)));
                }

                // No defenders left standing (dead, downed or fled): the base is abandoned.
                if (r.baselineFighters > 0 && fighters == 0)
                {
                    records.RemoveAt(i);
                    Abandon(s, map);
                }
            }
        }

        /// <summary>
        /// Our own version of the vanilla "settlement defeated" step. The vanilla one expects your
        /// colonists to be on the map (it records a caravan-assault tale about them) and treats every
        /// non-hostile base as already defeated, so it can't be used for drone strikes.
        /// </summary>
        private static void Abandon(Settlement s, Map map)
        {
            Faction f = s.Faction;
            string label = s.LabelCap;
            WorldObject ruin = WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.DestroyedSettlement);
            ruin.Tile = s.Tile;
            ruin.SetFaction(f);
            Find.WorldObjects.Add(ruin);
            MapParent ruinParent = ruin as MapParent;
            if (ruinParent != null)
                map.info.parent = ruinParent;   // the loaded map now belongs to the ruin
            s.Destroy();

            bool lastBase = true;
            foreach (Settlement other in Find.WorldObjects.Settlements)
            {
                if (other != s && !other.Destroyed && other.Faction == f)
                {
                    lastBase = false;
                    break;
                }
            }
            string text = "Your drone strikes have destroyed " + label + ". The " + f.Name + " have abandoned it; whatever is left there is free for the taking.";
            if (lastBase)
                text += "\n\nThat was their last settlement.";
            Find.LetterStack.ReceiveLetter("Settlement destroyed: " + label, text, LetterDefOf.PositiveEvent,
                new LookTargets(new GlobalTargetInfo(ruin)));
        }

        private static void MakeSurvivorsFlee(Map map, Faction faction)
        {
            List<Pawn> flee = new List<Pawn>();
            foreach (Pawn p in map.mapPawns.SpawnedPawnsInFaction(faction))
            {
                if (!p.Dead && !p.Downed && (p.RaceProps.Humanlike || p.RaceProps.IsMechanoid))
                    flee.Add(p);
            }
            if (flee.Count == 0)
                return;
            foreach (Pawn p in flee)
                p.GetLord()?.Notify_PawnLost(p, PawnLostCondition.ForcedToJoinOtherLord);
            LordMaker.MakeNewLord(faction, new LordJob_ExitMapBest(LocomotionUrgency.Sprint, true, true), map, flee);
        }
    }
}
