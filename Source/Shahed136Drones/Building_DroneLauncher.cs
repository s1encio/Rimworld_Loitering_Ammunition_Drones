using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace Shahed136Drones
{
    [StaticConstructorOnStartup]
    public class Building_DroneLauncher : Building_WorkTable
    {
        private static readonly Texture2D LaunchIcon = ContentFinder<Texture2D>.Get("UI/Commands/LaunchShahed");
        private static readonly Texture2D ReconIcon = ContentFinder<Texture2D>.Get("UI/Commands/LaunchRecon");

        private int loadedDrones;
        private bool reconLoaded;
        private GlobalTargetInfo pendingReconTarget = GlobalTargetInfo.Invalid;
        private Graphic loadedGraphic;
        private Graphic reconGraphic;

        // Shows a drone sitting on the rail while at least one is loaded.
        public override Graphic Graphic
        {
            get
            {
                if (def.graphicData == null || (loadedDrones <= 0 && !reconLoaded))
                    return base.Graphic;
                if (reconLoaded)
                {
                    if (reconGraphic == null)
                        reconGraphic = GraphicDatabase.Get<Graphic_Single>(def.graphicData.texPath + "_Recon",
                            ShaderDatabase.Cutout, def.graphicData.drawSize, Color.white);
                    return reconGraphic;
                }
                if (loadedGraphic == null)
                    loadedGraphic = GraphicDatabase.Get<Graphic_Single>(def.graphicData.texPath + "_Loaded",
                        ShaderDatabase.Cutout, def.graphicData.drawSize, Color.white);
                return loadedGraphic;
            }
        }

        public DroneLauncherExtension Ext => def.GetModExtension<DroneLauncherExtension>() ?? DroneLauncherExtension.Default;
        public int LoadedDrones => loadedDrones;
        public bool IsFull => loadedDrones + (reconLoaded ? 1 : 0) >= Ext.capacity;
        public bool ReconLoaded => reconLoaded;
        public bool ReconReadyToLaunch => reconLoaded && pendingReconTarget.IsValid;
        public int ReconRange => Ext.reconRange;

        /// <summary>A recon drone from this rail that is in the air without an operator.</summary>
        public WorldObject_ReconDrone ReconAwaitingOperator
        {
            get
            {
                foreach (WorldObject wo in Find.WorldObjects.AllWorldObjects)
                {
                    WorldObject_ReconDrone r = wo as WorldObject_ReconDrone;
                    if (r != null && r.launcher == this && !r.Destroyed && r.state != ReconState.Returning && !r.OperatorPresent)
                        return r;
                }
                return null;
            }
        }

        public void AddRecon()
        {
            if (!IsFull)
                reconLoaded = true;
        }

        public int Range
        {
            get
            {
                DroneLauncherExtension ext = Ext;
                return (ext.rangeResearch != null && ext.rangeResearch.IsFinished) ? ext.extendedRange : ext.range;
            }
        }

        public void AddDrone()
        {
            loadedDrones = Mathf.Min(loadedDrones + 1, Ext.capacity);
        }


        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref loadedDrones, "loadedDrones", 0);
            Scribe_Values.Look(ref reconLoaded, "reconLoaded", false);
            Scribe_TargetInfo.Look(ref pendingReconTarget, "pendingReconTarget");
        }

        public override string GetInspectString()
        {
            string baseStr = base.GetInspectString();
            string loaded = loadedDrones > 0 ? "Shahed-136" : reconLoaded ? "recon drone" : "empty";
            string extra = "Loaded: " + loaded + "\nStrike range: " + Range + " world tiles, recon range: " + ReconRange + " world tiles";
            if (ReconReadyToLaunch)
                extra += "\nRecon launch ordered, waiting for operator";
            return baseStr.NullOrEmpty() ? extra : baseStr + "\n" + extra;
        }

        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            Map map = Map;
            IntVec3 pos = Position;
            int drones = loadedDrones;
            base.Destroy(mode);
            // Loaded warheads cook off if the rail is destroyed.
            if (mode == DestroyMode.KillFinalize && drones > 0 && map != null)
            {
                GenExplosion.DoExplosion(pos, map, 2.9f + drones, DamageDefOf.Bomb, null, 50);
            }
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
                yield return g;

            if (Faction != Faction.OfPlayer)
                yield break;

            Command_Action launch = new Command_Action
            {
                defaultLabel = "Launch Shahed-136",
                defaultDesc = "Launch a loaded drone at a target up to " + Range + " world tiles away. Pick a tile on the world map, then (if a map is open there) the exact spot, building or pawn to hit.\n\nValid targets: any map you currently have open, or faction settlements. You need recon at the target: a caravan on that tile, or a colonist on that map. You can't strike your own launch site.\n\nLoaded: " + loadedDrones + " / " + Ext.capacity,
                icon = LaunchIcon,
                action = StartChoosingDestination
            };
            if (loadedDrones <= 0)
                launch.Disable("No Shahed-136 loaded. Add an \"assemble Shahed-136\" bill to this launch rail.");
            yield return launch;

            Command_Action recon = new Command_Action
            {
                defaultLabel = "Launch recon drone",
                defaultDesc = "Pick a tile up to " + ReconRange + " world tiles away, then a colonist to operate the drone. The operator launches it and stays at the rail to control it. While the drone circles over a tile, it counts as recon there, so you can strike that tile with a Shahed-136.",
                icon = ReconIcon,
                action = StartChoosingReconDestination
            };
            if (!reconLoaded)
                recon.Disable("No recon drone loaded. Add an \"assemble recon drone\" or \"mount recon drone\" bill to this launch rail.");
            yield return recon;

            if (DebugSettings.godMode)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Load drone",
                    action = AddDrone
                };
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Load recon drone",
                    action = AddRecon
                };
            }
        }

        // ---------------- Targeting ----------------

        private float DistanceTo(GlobalTargetInfo target)
        {
            return Find.WorldGrid.ApproxDistanceInTiles(Map.Tile, target.Tile);
        }

        private void StartChoosingDestination()
        {
            CameraJumper.TryJump(CameraJumper.GetWorldTarget(this));
            Find.WorldSelector.ClearSelection();
            Find.WorldTargeter.BeginTargeting(
                ChoseWorldTarget,
                true,
                LaunchIcon,
                true,
                () => { if (Spawned) GenDraw.DrawWorldRadiusRing(Map.Tile, Range); },
                t => TargetLabel(t));
        }

        /// <summary>Returns null if the tile can be struck, otherwise the reason why not.</summary>
        private string InvalidReason(GlobalTargetInfo target, out float dist)
        {
            dist = 0f;
            if (!target.IsValid)
                return "Invalid target";
            dist = DistanceTo(target);
            if (dist < 0.5f)
                return "Can't strike your own launch site";
            if (dist > Range)
                return "Out of range (" + dist.ToString("0") + " / " + Range + " tiles)";

            MapParent mp = Find.WorldObjects.MapParentAt(target.Tile);
            Settlement s = Find.WorldObjects.SettlementAt(target.Tile);
            bool hasMap = mp != null && mp.HasMap;
            bool enemySettlement = s != null && s.Faction != null && s.Faction != Faction.OfPlayer;
            if (!hasMap && !enemySettlement)
                return "Nothing to strike here";
            if (!HasRecon(target, mp))
                return "No recon at target: send a recon drone, caravan or colonist there first";
            return null;
        }

        /// <summary>Someone of yours has eyes on the target: a caravan on the tile, or a colonist/mech on its map.</summary>
        private static bool HasRecon(GlobalTargetInfo target, MapParent mp)
        {
            if (Find.WorldObjects.PlayerControlledCaravanAt(target.Tile) != null)
                return true;
            // a recon drone circling over the tile
            foreach (WorldObject wo in Find.WorldObjects.AllWorldObjects)
            {
                WorldObject_ReconDrone r = wo as WorldObject_ReconDrone;
                if (r != null && !r.Destroyed && r.IsOnStation && r.Tile == target.Tile)
                    return true;
            }
            if (mp != null && mp.HasMap)
            {
                foreach (Pawn p in mp.Map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer))
                {
                    if (p.RaceProps.Humanlike || p.RaceProps.IsMechanoid)
                        return true;
                }
            }
            return false;
        }

        private string TargetLabel(GlobalTargetInfo target)
        {
            if (!target.IsValid || !Spawned)
                return null;
            string reason = InvalidReason(target, out float dist);
            if (reason != null)
                return reason.Colorize(new Color(1f, 0.4f, 0.4f));

            MapParent mp = Find.WorldObjects.MapParentAt(target.Tile);
            string name = (mp != null ? mp.LabelCap.ToString() : "target");
            return "Strike " + name + " (" + dist.ToString("0") + " tiles)";
        }

        private bool ChoseWorldTarget(GlobalTargetInfo target)
        {
            if (!Spawned || loadedDrones <= 0)
                return true;
            string reason = InvalidReason(target, out float dist);
            if (reason != null)
            {
                Messages.Message(reason + ".", MessageTypeDefOf.RejectInput, false);
                return false;
            }
            Vector3 destPos = Find.WorldGrid.GetTileCenter(target.Tile);

            // A map is loaded at that tile: let the player choose exactly what to hit.
            MapParent mp = Find.WorldObjects.MapParentAt(target.Tile);
            if (mp != null && mp.HasMap)
            {
                BeginMapTargeting(mp.Map, dist, destPos);
                return true;
            }

            // A settlement without a loaded map: abstract strike.
            Settlement s = Find.WorldObjects.SettlementAt(target.Tile);
            if (s != null && s.Faction != null && s.Faction != Faction.OfPlayer)
            {
                string text = "Launch a Shahed-136 at " + s.LabelCap + " (" + s.Faction.Name + ")?";
                if (!s.Faction.HostileTo(Faction.OfPlayer))
                    text += "\n\nThis faction is NOT hostile. The strike will badly damage relations.";
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(text, () => Launch(null, IntVec3.Invalid, null, s, dist, destPos), true));
                return true;
            }

            return false;
        }

        private void BeginMapTargeting(Map map, float dist, Vector3 destPos)
        {
            CameraJumper.TryJump(map.Center, map);
            TargetingParameters parms = new TargetingParameters
            {
                canTargetLocations = true,
                canTargetPawns = true,
                canTargetBuildings = true,
                canTargetItems = true,
                mapObjectTargetsMustBeAutoAttackable = false
            };
            Find.Targeter.BeginTargeting(parms, (LocalTargetInfo t) =>
            {
                Launch(map, t.Cell, t.Thing, null, dist, destPos);
            }, (Pawn)null, null, LaunchIcon);
        }

        // ---------------- Recon drone ----------------

        private void StartChoosingReconDestination()
        {
            CameraJumper.TryJump(CameraJumper.GetWorldTarget(this));
            Find.WorldSelector.ClearSelection();
            Find.WorldTargeter.BeginTargeting(
                ChoseReconTarget,
                true,
                ReconIcon,
                true,
                () => { if (Spawned) GenDraw.DrawWorldRadiusRing(Map.Tile, ReconRange); },
                t => ReconTargetLabel(t));
        }

        /// <summary>Null if a recon drone may fly there, otherwise the reason why not.</summary>
        private string ReconInvalidReason(GlobalTargetInfo target, out float dist)
        {
            dist = 0f;
            if (!target.IsValid)
                return "Invalid target";
            dist = DistanceTo(target);
            if (dist > ReconRange)
                return "Out of range (" + dist.ToString("0") + " / " + ReconRange + " tiles)";
            Settlement s = Find.WorldObjects.SettlementAt(target.Tile);
            if (s != null && s.Faction != null && s.Faction != Faction.OfPlayer && !s.Faction.HostileTo(Faction.OfPlayer))
                return "Can't fly recon over " + s.Faction.Name + ": they are not your enemy";
            return null;
        }

        private string ReconTargetLabel(GlobalTargetInfo target)
        {
            if (!target.IsValid || !Spawned)
                return null;
            string reason = ReconInvalidReason(target, out float dist);
            if (reason != null)
                return reason.Colorize(new Color(1f, 0.4f, 0.4f));
            return "Recon here (" + dist.ToString("0") + " tiles)";
        }

        private bool ChoseReconTarget(GlobalTargetInfo target)
        {
            if (!Spawned || !reconLoaded)
                return true;
            string reason = ReconInvalidReason(target, out float _);
            if (reason != null)
            {
                Messages.Message(reason + ".", MessageTypeDefOf.RejectInput, false);
                return false;
            }
            pendingReconTarget = target;
            CameraJumper.TryJump(this);
            ChooseOperator();
            return true;
        }

        /// <summary>Float menu of colonists who can walk to the rail and operate the drone.</summary>
        public void ChooseOperator()
        {
            if (!Spawned)
                return;
            JobDef jobDef = DefDatabase<JobDef>.GetNamed("Shahed_OperateReconDrone");
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            foreach (Pawn p in Map.mapPawns.FreeColonistsSpawned)
            {
                if (p.Downed || p.Dead || !p.CanReach(this, PathEndMode.InteractionCell, Danger.Deadly))
                    continue;
                Pawn pawn = p;
                options.Add(new FloatMenuOption(pawn.LabelShortCap, () =>
                {
                    Job job = JobMaker.MakeJob(jobDef, this);
                    pawn.jobs.TryTakeOrderedJob(job);
                }));
            }
            if (options.Count == 0)
            {
                Messages.Message("No colonist can reach the launch rail to operate the drone.", this, MessageTypeDefOf.RejectInput, false);
                return;
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        /// <summary>Called by the operator's job once they are ready at the rail.</summary>
        public void LaunchRecon(Pawn operatorPawn)
        {
            if (!Spawned || !ReconReadyToLaunch)
                return;
            DroneLauncherExtension ext = Ext;
            GlobalTargetInfo target = pendingReconTarget;
            reconLoaded = false;
            pendingReconTarget = GlobalTargetInfo.Invalid;

            GameComponent_DroneFlights comp = Current.Game.GetComponent<GameComponent_DroneFlights>();
            Vector3 origin = DrawPos + new Vector3(0f, 0f, -0.1f);
            int exitTicks = GameComponent_DroneFlights.LaunchExitTicks(Map, origin);
            int travel = Mathf.Max(ext.minFlightTicks, Mathf.RoundToInt(DistanceTo(target) * ext.reconTicksPerTile));

            WorldObject_ReconDrone wo = (WorldObject_ReconDrone)WorldObjectMaker.MakeWorldObject(DefDatabase<WorldObjectDef>.GetNamed("Shahed_ReconDroneWorld"));
            wo.Tile = Map.Tile;
            wo.from = Find.WorldGrid.GetTileCenter(Map.Tile);
            wo.to = Find.WorldGrid.GetTileCenter(target.Tile);
            wo.startTick = Find.TickManager.TicksGame + exitTicks;
            wo.arrivalTick = wo.startTick + travel;
            wo.fuelTicks = ext.reconFuelTicks;
            wo.maxFuelTicks = ext.reconFuelTicks;
            wo.ticksPerTile = ext.reconTicksPerTile;
            wo.destination = target;
            wo.homeParent = Map.Parent;
            wo.launcher = this;
            wo.operatorPawn = operatorPawn;
            wo.autopilot = true;
            Find.WorldObjects.Add(wo);

            comp.StartLaunchAnimation(Map, origin, true);
            FleckMaker.ThrowSmoke(DrawPos, Map, 1.5f);
            DefDatabase<SoundDef>.GetNamedSilentFail("Recon_Launch")?.PlayOneShot(new TargetInfo(Position, Map));
            Messages.Message("Recon drone launched. Time to target: " + (exitTicks + travel).ToStringTicksToPeriod() + ".", this, MessageTypeDefOf.NeutralEvent, false);
        }

        private void Launch(Map targetMap, IntVec3 cell, Thing targetThing, Settlement settlement, float dist, Vector3 destPos)
        {
            if (!Spawned || loadedDrones <= 0)
                return;

            DroneLauncherExtension ext = Ext;
            loadedDrones--;

            GameComponent_DroneFlights comp = Current.Game.GetComponent<GameComponent_DroneFlights>();
            Vector3 origin = DrawPos + new Vector3(0f, 0f, -0.1f);

            int worldTicks = Mathf.Max(ext.minFlightTicks, Mathf.RoundToInt(dist * ext.ticksPerTile));
            int exitTicks = GameComponent_DroneFlights.LaunchExitTicks(Map, origin);
            int ticks = exitTicks + worldTicks;
            DroneFlight flight = new DroneFlight
            {
                targetMap = targetMap,
                targetCell = cell,
                targetThing = targetThing,
                targetSettlement = settlement,
                ticksLeft = ticks,
                strikeDef = ext.strikeSkyfaller ?? DefDatabase<ThingDef>.GetNamed("Shahed136Incoming"),
                goodwillImpact = ext.settlementGoodwillImpact,
                // the world-map marker only appears once the drone has left this map
                departTicksLeft = exitTicks,
                worldTravelTicks = worldTicks,
                originParent = Map.Parent,
                worldFrom = Find.WorldGrid.GetTileCenter(Map.Tile),
                worldTo = destPos,
                targetLabel = settlement != null ? settlement.LabelCap.ToString() : (targetMap != null ? targetMap.Parent.LabelCap.ToString() : "target")
            };
            comp.Register(flight);

            // Launch animation on this map: the drone slides off the rail and climbs away
            if (Find.CurrentMap != Map)
                CameraJumper.TryJump(this);
            comp.StartLaunchAnimation(Map, origin);
            FleckMaker.ThrowSmoke(DrawPos, Map, 2.5f);
            FleckMaker.ThrowFireGlow(DrawPos, Map, 1.5f);
            (DefDatabase<SoundDef>.GetNamedSilentFail("Shahed_Launch") ?? DefDatabase<SoundDef>.GetNamedSilentFail("DropPod_Leaving"))?.PlayOneShot(new TargetInfo(Position, Map));

            Messages.Message("Shahed-136 launched. Estimated time to target: " + ticks.ToStringTicksToPeriod() + ".",
                this, MessageTypeDefOf.NeutralEvent, false);
        }
    }
}
