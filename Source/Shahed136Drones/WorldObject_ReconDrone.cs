using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Shahed136Drones
{
    public enum ReconState
    {
        Outbound,
        OnStation,
        Returning
    }

    /// <summary>
    /// The recon drone while it is away from home. Lives on the world map; when it is on station
    /// over a tile that has a loaded map, it also spawns a ReconDroneFlying on that map.
    /// Ticked by GameComponent_DroneFlights.
    /// </summary>
    [StaticConstructorOnStartup]
    public class WorldObject_ReconDrone : WorldObject
    {
        private static readonly Texture2D RecallIcon = ContentFinder<Texture2D>.Get("UI/Commands/ReconRecall");
        private static readonly Texture2D OperatorIcon = ContentFinder<Texture2D>.Get("UI/Commands/LaunchRecon");

        public ReconState state = ReconState.Outbound;
        public Vector3 from;
        public Vector3 to;
        public int startTick;           // when it crosses the home map edge (not drawn before that)
        public int arrivalTick;
        public int fuelTicks;
        public int maxFuelTicks;
        public float ticksPerTile = 100f;
        public GlobalTargetInfo destination = GlobalTargetInfo.Invalid;
        public MapParent homeParent;
        public Building_DroneLauncher launcher;
        public Pawn operatorPawn;
        public ReconDroneFlying mapDrone;
        public bool autopilot = true;
        public bool recalled;
        private bool lowFuelWarned;
        private bool mapRequested;              // not saved: after loading we simply ask again
        public MapParent reconArea;             // our own map parent for tiles that had none
        private bool operatorLostWarned;

        public bool IsOnStation => state == ReconState.OnStation;

        public bool OperatorPresent =>
            operatorPawn != null && !operatorPawn.Dead && operatorPawn.Spawned &&
            operatorPawn.CurJobDef != null && operatorPawn.CurJobDef.defName == "Shahed_OperateReconDrone";

        private float Progress => arrivalTick <= startTick ? 1f : Mathf.Clamp01((float)(Find.TickManager.TicksGame - startTick) / (arrivalTick - startTick));

        public override Vector3 DrawPos
        {
            get
            {
                if (state == ReconState.OnStation)
                    return OrbitPos(Find.TickManager.TicksGame);
                return Vector3.Slerp(from, to, Progress);
            }
        }

        // Small circles around the tile it is watching.
        private Vector3 OrbitPos(float t)
        {
            Vector3 n = to.normalized;
            Vector3 e1 = Vector3.Cross(n, Vector3.up);
            if (e1.sqrMagnitude < 1e-6f) e1 = Vector3.Cross(n, Vector3.right);
            e1.Normalize();
            Vector3 e2 = Vector3.Cross(n, e1);
            float r = Find.WorldGrid.AverageTileSize * 0.8f;
            float a = t * 0.004f;
            return to + (e1 * Mathf.Cos(a) + e2 * Mathf.Sin(a)) * r;
        }

        public override void Draw()
        {
            if (Find.TickManager.TicksGame < startTick)
                return;
            Vector3 pos = DrawPos;
            Vector3 normal = pos.normalized;
            Vector3 dir = state == ReconState.OnStation ? OrbitPos(Find.TickManager.TicksGame + 30) - pos : to - pos;
            dir -= normal * Vector3.Dot(dir, normal);
            if (dir.sqrMagnitude < 1e-8f)
            {
                dir = to - from;
                dir -= normal * Vector3.Dot(dir, normal);
            }
            if (dir.sqrMagnitude < 1e-8f)
                dir = Vector3.Cross(normal, Vector3.right);
            Quaternion rot = Quaternion.LookRotation(dir.normalized, normal);
            float size = Find.WorldGrid.AverageTileSize * Mathf.Lerp(1.1f, 7f, Find.WorldCameraDriver.AltitudePercent);
            Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(pos + normal * 0.08f, rot, new Vector3(size, 1f, size)), Material, WorldCameraManager.WorldLayer);
        }

        // ------------------------------------------------------------------ ticking

        public void DroneTick()
        {
            int now = Find.TickManager.TicksGame;
            if (now < startTick)
                return; // still climbing out over the home map

            fuelTicks--;
            if (fuelTicks <= 0)
            {
                OutOfFuel();
                return;
            }

            if (mapDrone != null && (mapDrone.Destroyed || !mapDrone.Spawned))
                mapDrone = null;

            if (!OperatorPresent && !autopilot && !operatorLostWarned)
            {
                operatorLostWarned = true;
                autopilot = true;
                Messages.Message("Recon drone lost its operator and switched to autopilot.", new LookTargets(new GlobalTargetInfo(this)), MessageTypeDefOf.CautionInput, false);
            }
            if (OperatorPresent)
                operatorLostWarned = false;

            switch (state)
            {
                case ReconState.Outbound:
                    if (now >= arrivalTick)
                    {
                        state = ReconState.OnStation;
                        Tile = destination.Tile;
                        Messages.Message("Recon drone is on station over " + TileLabel() + ".", new LookTargets(new GlobalTargetInfo(this)), MessageTypeDefOf.PositiveEvent, false);
                    }
                    break;

                case ReconState.OnStation:
                    if (mapDrone == null && !recalled)
                    {
                        MapParent mp = Find.WorldObjects.MapParentAt(Tile);
                        if (mp != null && mp.HasMap)
                            SpawnMapDrone(mp.Map);
                        else if (!mapRequested)
                        {
                            mapRequested = true;
                            GenerateReconMap(mp);
                        }
                    }
                    if (recalled && mapDrone == null)
                        StartReturn();
                    if (!lowFuelWarned && fuelTicks < ReturnTicks() + 2500)
                    {
                        lowFuelWarned = true;
                        Messages.Message("Recon drone is low on fuel. Recall it now or it won't make it home.", new LookTargets(new GlobalTargetInfo(this)), MessageTypeDefOf.ThreatSmall, false);
                    }
                    break;

                case ReconState.Returning:
                    if (now >= arrivalTick)
                        ArriveHome();
                    break;
            }
        }

        private string TileLabel()
        {
            MapParent mp = Find.WorldObjects.MapParentAt(Tile);
            return mp != null ? mp.LabelCap.ToString() : "the target area";
        }

        private int ReturnTicks()
        {
            if (homeParent == null || homeParent.Destroyed)
                return 0;
            return Mathf.RoundToInt(Find.WorldGrid.ApproxDistanceInTiles(Tile, homeParent.Tile) * ticksPerTile);
        }

        /// <summary>Generate the map of the watched tile, like a caravan arriving would.</summary>
        private void GenerateReconMap(MapParent mp)
        {
            if (mp == null)
            {
                // empty tile: put our own temporary map parent there
                WorldObjectDef def = DefDatabase<WorldObjectDef>.GetNamedSilentFail("Shahed_ReconArea");
                if (def == null)
                    return;
                mp = (MapParent)WorldObjectMaker.MakeWorldObject(def);
                mp.Tile = Tile;
                Find.WorldObjects.Add(mp);
                reconArea = mp;
            }
            MapParent parent = mp;
            LongEventHandler.QueueLongEvent(() =>
            {
                if (!parent.Destroyed && !parent.HasMap)
                    GetOrGenerateMapUtility.GetOrGenerateMap(parent.Tile, parent.def);
            }, "GeneratingMap", false, null);
        }

        /// <summary>Remove our temporary map parent if it never got (or no longer has) a map.</summary>
        private void CleanupReconArea()
        {
            if (reconArea != null && !reconArea.Destroyed && !reconArea.HasMap)
                reconArea.Destroy();
            reconArea = null;
        }

        private void SpawnMapDrone(Map map)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail("Shahed_ReconDroneFlying");
            if (def == null)
                return;
            ReconDroneFlying d = (ReconDroneFlying)ThingMaker.MakeThing(def);
            d.owner = this;
            // enter from a random map edge, heading for the middle
            float a = Rand.Range(0f, 360f) * Mathf.Deg2Rad;
            Vector3 dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
            Vector3 c = map.Center.ToVector3Shifted();
            float half = Mathf.Max(map.Size.x, map.Size.z) * 0.5f;
            Vector3 start = c + dir * (half * 1.45f);
            d.exactPos = start;
            d.heading = Mathf.Atan2(-dir.x, -dir.z) * Mathf.Rad2Deg;
            GenSpawn.Spawn(d, ReconDroneFlying.ClampCell(start, map), map);
            mapDrone = d;
            Messages.Message("Recon drone is entering the map.", d, MessageTypeDefOf.PositiveEvent, false);
        }

        public void SetOperator(Pawn p)
        {
            operatorPawn = p;
            operatorLostWarned = false;
        }

        // ------------------------------------------------------------------ return / landing

        public void Recall()
        {
            if (state == ReconState.Returning)
                return;
            recalled = true;
            if (state == ReconState.Outbound)
            {
                // turn around mid-flight
                float p = Progress;
                Vector3 here = Vector3.Slerp(from, to, p);
                int back = Mathf.Max(60, Mathf.RoundToInt((Find.TickManager.TicksGame - startTick)));
                from = here;
                to = homeParent != null && !homeParent.Destroyed ? Find.WorldGrid.GetTileCenter(homeParent.Tile) : here;
                startTick = Find.TickManager.TicksGame;
                arrivalTick = startTick + back;
                state = ReconState.Returning;
                return;
            }
            if (mapDrone != null && mapDrone.Spawned)
                mapDrone.LeaveMap();
            else
                StartReturn();
        }

        /// <summary>Called when the on-map drone has flown off the map after a recall.</summary>
        public void OnMapDroneLeft()
        {
            mapDrone = null;
            StartReturn();
        }

        private void StartReturn()
        {
            if (state == ReconState.Returning)
                return;
            if (homeParent == null || homeParent.Destroyed)
            {
                Messages.Message("Recon drone has no home to return to and was lost.", MessageTypeDefOf.NegativeEvent, false);
                Destroy();
                return;
            }
            CleanupReconArea();
            from = DrawPos;
            to = Find.WorldGrid.GetTileCenter(homeParent.Tile);
            startTick = Find.TickManager.TicksGame;
            arrivalTick = startTick + Mathf.Max(60, ReturnTicks());
            state = ReconState.Returning;
            Messages.Message("Recon drone is returning home. ETA " + (arrivalTick - startTick).ToStringTicksToPeriod() + ".", new LookTargets(new GlobalTargetInfo(this)), MessageTypeDefOf.NeutralEvent, false);
        }

        private void ArriveHome()
        {
            if (homeParent != null && !homeParent.Destroyed && homeParent.HasMap)
            {
                Map map = homeParent.Map;
                IntVec3 cell = map.Center;
                if (launcher != null && launcher.Spawned && launcher.Map == map)
                    cell = launcher.InteractionCell + new IntVec3(0, 0, -2);
                if (!cell.InBounds(map) || !cell.Standable(map))
                    CellFinder.TryFindRandomCellNear(cell.InBounds(map) ? cell : map.Center, map, 8, c => c.Standable(map), out cell);
                Current.Game.GetComponent<GameComponent_DroneFlights>().StartLanding(map, cell);
            }
            else
            {
                Messages.Message("Recon drone had nowhere to land and was lost.", MessageTypeDefOf.NegativeEvent, false);
            }
            Destroy();
        }

        private void OutOfFuel()
        {
            if (mapDrone != null && mapDrone.Spawned)
            {
                mapDrone.Crash();
                mapDrone = null;
            }
            Messages.Message("Recon drone ran out of fuel and crashed.", MessageTypeDefOf.NegativeEvent, false);
            CleanupReconArea();
            Destroy();
        }

        // ------------------------------------------------------------------ UI

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
                yield return g;
            foreach (Gizmo g in ControlGizmos())
                yield return g;
        }

        /// <summary>Gizmos shared by the world marker and the on-map drone.</summary>
        public IEnumerable<Gizmo> ControlGizmos()
        {
            Command_Action back = new Command_Action
            {
                defaultLabel = "Go back",
                defaultDesc = "Fly back to the home colony and land next to the launch rail.",
                icon = RecallIcon,
                action = Recall
            };
            if (state == ReconState.Returning || recalled)
                back.Disable("Already returning home.");
            yield return back;

            if (!OperatorPresent && state != ReconState.Returning)
            {
                Command_Action op = new Command_Action
                {
                    defaultLabel = "Assign operator",
                    defaultDesc = "Send a colonist to the launch rail to take manual control of this drone.",
                    icon = OperatorIcon,
                    action = () => launcher?.ChooseOperator()
                };
                if (launcher == null || !launcher.Spawned)
                    op.Disable("The launch rail this drone belongs to no longer exists.");
                yield return op;
            }
        }

        public override string GetInspectString()
        {
            string s;
            int now = Find.TickManager.TicksGame;
            switch (state)
            {
                case ReconState.Outbound:
                    s = "Flying to target. Arrives in " + Mathf.Max(0, arrivalTick - now).ToStringTicksToPeriod() + ".";
                    break;
                case ReconState.Returning:
                    s = "Returning home. Arrives in " + Mathf.Max(0, arrivalTick - now).ToStringTicksToPeriod() + ".";
                    break;
                default:
                    s = "On station over " + TileLabel() + (autopilot ? " (autopilot)." : " (manual control).");
                    break;
            }
            s += "\nFuel: " + Mathf.Max(0, fuelTicks).ToStringTicksToPeriod() + " left";
            s += "\nOperator: " + (OperatorPresent ? operatorPawn.LabelShortCap : "none");
            return s;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                if (launcher != null && launcher.Destroyed) launcher = null;
                if (mapDrone != null && mapDrone.Destroyed) mapDrone = null;
            }
            Scribe_Values.Look(ref state, "state", ReconState.Outbound);
            Scribe_Values.Look(ref from, "from");
            Scribe_Values.Look(ref to, "to");
            Scribe_Values.Look(ref startTick, "startTick");
            Scribe_Values.Look(ref arrivalTick, "arrivalTick");
            Scribe_Values.Look(ref fuelTicks, "fuelTicks");
            Scribe_Values.Look(ref maxFuelTicks, "maxFuelTicks");
            Scribe_Values.Look(ref ticksPerTile, "ticksPerTile", 100f);
            Scribe_TargetInfo.Look(ref destination, "destination");
            Scribe_References.Look(ref homeParent, "homeParent");
            Scribe_References.Look(ref launcher, "launcher");
            Scribe_References.Look(ref operatorPawn, "operatorPawn");
            Scribe_References.Look(ref mapDrone, "mapDrone");
            Scribe_References.Look(ref reconArea, "reconArea");
            Scribe_Values.Look(ref autopilot, "autopilot", true);
            Scribe_Values.Look(ref recalled, "recalled");
            Scribe_Values.Look(ref lowFuelWarned, "lowFuelWarned");
            Scribe_Values.Look(ref operatorLostWarned, "operatorLostWarned");
        }
    }
}
