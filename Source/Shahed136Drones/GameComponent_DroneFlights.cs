using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace Shahed136Drones
{
    /// <summary>One drone currently flying across the world map.</summary>
    public class DroneFlight : IExposable
    {
        public Map targetMap;
        public IntVec3 targetCell = IntVec3.Invalid;
        public Thing targetThing;
        public Settlement targetSettlement;
        public int ticksLeft;
        public ThingDef strikeDef;
        public int goodwillImpact = -25;
        public WorldObject worldObject;
        // world-map marker, spawned when the drone crosses the map edge
        public int departTicksLeft = -1;
        public int worldTravelTicks;
        public MapParent originParent;
        public Vector3 worldFrom;
        public Vector3 worldTo;
        public string targetLabel = "target";

        public void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving && targetThing != null && targetThing.Destroyed)
                targetThing = null;

            Scribe_References.Look(ref targetMap, "targetMap");
            Scribe_Values.Look(ref targetCell, "targetCell", IntVec3.Invalid);
            Scribe_References.Look(ref targetThing, "targetThing");
            Scribe_References.Look(ref targetSettlement, "targetSettlement");
            Scribe_Values.Look(ref ticksLeft, "ticksLeft", 0);
            Scribe_Defs.Look(ref strikeDef, "strikeDef");
            Scribe_Values.Look(ref goodwillImpact, "goodwillImpact", -25);
            Scribe_References.Look(ref worldObject, "worldObject");
            Scribe_Values.Look(ref departTicksLeft, "departTicksLeft", -1);
            Scribe_Values.Look(ref worldTravelTicks, "worldTravelTicks");
            Scribe_References.Look(ref originParent, "originParent");
            Scribe_Values.Look(ref worldFrom, "worldFrom");
            Scribe_Values.Look(ref worldTo, "worldTo");
            Scribe_Values.Look(ref targetLabel, "targetLabel", "target");
        }
    }

    /// <summary>A drone in its final dive onto a map cell.</summary>
    public class DiveAnim : IExposable
    {
        public Map map;
        public IntVec3 cell;
        public Thing thing;
        public ThingDef warheadDef;
        public int startTick;
        public Vector3 fromDir;     // unit vector from the target towards where the drone comes from
        public float startDist;     // distance from the target at spawn (map edge)
        public float speed;         // cells per tick
        public bool soundPlayed;
        public bool landing;        // recon drone coming home instead of a Shahed diving
        public Faction attacker;    // who launched it (player, or an enemy faction)
        public Projectile_DroneDecoy decoy;   // invisible "shell" that air defense mods can shoot down
        public bool decoySpawned;
        public int hitsTaken;
        public bool gnssJammed;     // already lost satellite navigation once

        public int TotalTicks => Mathf.CeilToInt(startDist / speed);

        public Vector3 EndPos => cell.ToVector3Shifted();
        public Vector3 Heading => -fromDir;
        // Texture nose points north (+z); rotate clockwise to face the flight direction.
        public float Angle => Mathf.Atan2(Heading.x, Heading.z) * Mathf.Rad2Deg;

        public void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving && thing != null && thing.Destroyed)
                thing = null;
            if (Scribe.mode == LoadSaveMode.Saving && decoy != null && decoy.Destroyed)
                decoy = null;
            Scribe_References.Look(ref map, "map");
            Scribe_References.Look(ref attacker, "attacker");
            Scribe_References.Look(ref decoy, "decoy");
            Scribe_Values.Look(ref decoySpawned, "decoySpawned");
            Scribe_Values.Look(ref hitsTaken, "hitsTaken");
            Scribe_Values.Look(ref gnssJammed, "gnssJammed");
            Scribe_Values.Look(ref cell, "cell");
            Scribe_References.Look(ref thing, "thing");
            Scribe_Defs.Look(ref warheadDef, "warheadDef");
            Scribe_Values.Look(ref startTick, "startTick");
            Scribe_Values.Look(ref fromDir, "fromDir");
            Scribe_Values.Look(ref startDist, "startDist", 45f);
            Scribe_Values.Look(ref speed, "speed", 0.3f);
            Scribe_Values.Look(ref soundPlayed, "soundPlayed");
            Scribe_Values.Look(ref landing, "landing");
        }
    }

    /// <summary>Ticks all drones in flight and resolves them on arrival. Created automatically by the game.</summary>
    [StaticConstructorOnStartup]
    public class GameComponent_DroneFlights : GameComponent
    {
        private List<DroneFlight> flights = new List<DroneFlight>();
        private List<SiegeRecord> sieges = new List<SiegeRecord>();

        /// <summary>Should vanilla keep this map loaded (recon drone overhead, or recently bombed)?</summary>
        public static bool KeepMapOpen(MapParent mp)
        {
            if (ReconOver(mp))
                return true;
            GameComponent_DroneFlights comp = Current.Game?.GetComponent<GameComponent_DroneFlights>();
            return comp != null && SettlementSiege.KeepMapOpen(comp.sieges, mp);
        }

        /// <summary>Recon drones currently flying over a loaded map.</summary>
        public static readonly List<ReconDroneFlying> MapDrones = new List<ReconDroneFlying>();

        public GameComponent_DroneFlights(Game game)
        {
            MapDrones.Clear();
            FpvDroneFlying.Active.Clear();
        }

        /// <summary>True while a recon drone is circling over this map parent's tile.</summary>
        public static bool ReconOver(MapParent mp)
        {
            if (mp == null)
                return false;
            foreach (WorldObject wo in Find.WorldObjects.AllWorldObjects)
            {
                WorldObject_ReconDrone r = wo as WorldObject_ReconDrone;
                if (r != null && !r.Destroyed && r.IsOnStation && r.Tile == mp.Tile)
                    return true;
            }
            return false;
        }

        /// <summary>True while this pawn is the operator of a recon drone that is still in the air.</summary>
        public static bool IsOperating(Pawn p)
        {
            foreach (WorldObject wo in Find.WorldObjects.AllWorldObjects)
            {
                WorldObject_ReconDrone r = wo as WorldObject_ReconDrone;
                if (r != null && !r.Destroyed && r.operatorPawn == p)
                    return true;
            }
            return false;
        }

        public void Register(DroneFlight flight) => flights.Add(flight);

        // =====================================================================
        //  Visuals: launch animation (not saved) and dive animation (saved)
        // =====================================================================
        private class LaunchAnim
        {
            public Map map;
            public Vector3 origin;
            public int startTick;
            public bool recon;
        }

        /// <summary>How many air-defense hits a diving Shahed survives (it goes down on this one).</summary>
        public const int HitsToShootDown = 2;
        private const float CruiseSpeed = 0.3f;     // cells per tick (~18 cells per second at normal speed)
        private const int SoundTicks = 250;         // length of the incoming sound (~4.2 s)
        private const float DiveStartDist = 30f;    // starts descending this far from the target
        private const float EdgeMargin = 6f;        // spawn / despawn this far outside the map edge
        private const float LaunchDrawSize = 2.75f;   // matches the drone drawn on the launch rail
        private const float DiveDrawSize = 5.2f;      // big in flight so it reads well next to pawns
        private const float ReconDrawSize = 4.6f;
        private const int LandSoundTicks = 261;       // touchdown thump in the landing sound
        private static Texture2D reconTex;
        private static Material reconShadowMat;
        private readonly List<LaunchAnim> anims = new List<LaunchAnim>();
        private List<DiveAnim> dives = new List<DiveAnim>();
        private static Texture2D droneTex;
        private static Material shadowMat;

        public void StartLaunchAnimation(Map map, Vector3 origin, bool recon = false)
        {
            anims.Add(new LaunchAnim { map = map, origin = origin, startTick = Find.TickManager.TicksGame, recon = recon });
        }

        /// <summary>Recon drone flies in from the map edge and lands at cell, becoming an item.</summary>
        public void StartLanding(Map map, IntVec3 cell)
        {
            float a = Rand.Range(0f, 360f) * Mathf.Deg2Rad;
            Vector3 dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
            Vector3 c = cell.ToVector3Shifted();
            float tx = dir.x > 0.001f ? (map.Size.x - c.x) / dir.x : dir.x < -0.001f ? -c.x / dir.x : float.MaxValue;
            float tz = dir.z > 0.001f ? (map.Size.z - c.z) / dir.z : dir.z < -0.001f ? -c.z / dir.z : float.MaxValue;
            float dist = Mathf.Min(tx, tz) + EdgeMargin;
            dives.Add(new DiveAnim
            {
                map = map,
                cell = cell,
                startTick = Find.TickManager.TicksGame,
                fromDir = dir,
                startDist = dist,
                speed = Mathf.Min(CruiseSpeed * 0.7f, dist / LandSoundTicks),
                landing = true
            });
            Messages.Message("Recon drone is coming in to land.", new TargetInfo(cell, map), MessageTypeDefOf.NeutralEvent, false);
        }

        // Slides along the rail for ~60 ticks, then accelerates hard and climbs away to the north.
        private static void LaunchState(float t, out float forward, out float height)
        {
            if (t <= 60f)
                forward = 1.8f * Mathf.Pow(t / 60f, 3f);                    // sliding off the rail
            else if (t <= 120f)
            {
                float dt = t - 60f, v0 = 0.09f, acc = (CruiseSpeed - v0) / 60f;
                forward = 1.8f + v0 * dt + 0.5f * acc * dt * dt;           // accelerating
            }
            else
                forward = 13.5f + CruiseSpeed * (t - 120f);                 // cruising
            height = Mathf.Clamp01((t - 45f) / 140f);
        }

        /// <summary>Ticks until a drone launched from origin flies past the top edge of the map.</summary>
        public static int LaunchExitTicks(Map map, Vector3 origin)
        {
            float need = map.Size.z + EdgeMargin - origin.z;
            if (need <= 1.8f)
                return Mathf.CeilToInt(60f * Mathf.Pow(Mathf.Max(0f, need) / 1.8f, 1f / 3f));
            if (need <= 13.5f)
            {
                float v0 = 0.09f, acc = (CruiseSpeed - v0) / 60f, rem = need - 1.8f;
                float dt = (-v0 + Mathf.Sqrt(v0 * v0 + 2f * acc * rem)) / acc;
                return Mathf.CeilToInt(60f + dt);
            }
            return Mathf.CeilToInt(120f + (need - 13.5f) / CruiseSpeed);
        }

        private static void SpawnWorldMarker(DroneFlight f)
        {
            WorldObjectDef woDef = DefDatabase<WorldObjectDef>.GetNamedSilentFail("Shahed136InFlight");
            if (woDef == null || f.originParent == null || f.originParent.Destroyed)
                return;
            WorldObject_DroneInFlight wo = (WorldObject_DroneInFlight)WorldObjectMaker.MakeWorldObject(woDef);
            wo.Tile = f.originParent.Tile;
            wo.from = f.worldFrom;
            wo.to = f.worldTo;
            wo.startTick = Find.TickManager.TicksGame;
            wo.arrivalTick = wo.startTick + f.worldTravelTicks;
            wo.targetLabel = f.targetLabel;
            Find.WorldObjects.Add(wo);
            f.worldObject = wo;
        }

        // Dive: ground position, drawn position (lifted by height), heading and height (1 = high, 0 = impact).
        private static void DiveState(DiveAnim d, float t, out Vector3 ground, out Vector3 drawn, out float height)
        {
            float remaining = Mathf.Max(0f, d.startDist - d.speed * t);
            float p = Mathf.Clamp01(remaining / DiveStartDist);
            height = p * p * (3f - 2f * p);                  // smooth descent in the last stretch
            ground = d.EndPos + d.fromDir * remaining;
            drawn = ground + new Vector3(0f, 0f, 3.5f * height);
        }

        private bool AnythingToDraw => anims.Count > 0 || dives.Count > 0 || MapDrones.Count > 0 || FpvDroneFlying.Active.Count > 0;
        private const float FpvDrawSize = 1.7f;
        private static Texture2D fpvTexA, fpvTexB;   // two spinning-prop frames
        private static Material fpvShadowMat;

        // Calls act(drawnPos, groundPos, height, angle, size, isRecon) for every drone visible on the current map.
        private void ForEachVisibleDrone(System.Action<Vector3, Vector3, float, float, float, bool> act)
        {
            Map cur = Find.CurrentMap;
            if (cur == null)
                return;
            int now = Find.TickManager.TicksGame;
            foreach (LaunchAnim a in anims)
            {
                if (a.map != cur)
                    continue;
                LaunchState(now - a.startTick, out float fwd, out float h);
                Vector3 pos = a.origin + new Vector3(0f, 0f, fwd);
                Vector3 ground = a.origin + new Vector3(1.4f * h, 0f, fwd - 2.2f * h);
                // grows from rail size to full flight size as it climbs away
                act(pos, ground, h, 0f, Mathf.Lerp(LaunchDrawSize, a.recon ? ReconDrawSize : DiveDrawSize, h) * (1f + 0.3f * h), a.recon);
            }
            foreach (DiveAnim d in dives)
            {
                if (d.map != cur)
                    continue;
                DiveState(d, now - d.startTick, out Vector3 ground, out Vector3 drawn, out float h);
                act(drawn, ground, h, d.Angle, (d.landing ? ReconDrawSize : DiveDrawSize) * (1f + 0.3f * h), d.landing);
            }
            foreach (ReconDroneFlying r in MapDrones)
            {
                if (r.Map != cur)
                    continue;
                act(r.exactPos, r.exactPos + new Vector3(1.2f, 0f, -1.8f), 1f, r.heading, ReconDrawSize, true);
            }
        }

        // True while the world map is open: then nothing of the local map may be drawn.
        private static bool WorldMapOpen
        {
            get
            {
                if (Current.ProgramState != ProgramState.Playing)
                    return false;
                if (Find.MainTabsRoot?.OpenTab == MainButtonDefOf.World)
                    return true;
                // Ask the game's own "is the world being rendered" flag. Its name differs between
                // RimWorld versions (WorldRenderedNow / WorldRendered), so look it up by reflection.
                if (worldRenderedProp == null && !worldRenderedLookedUp)
                {
                    worldRenderedLookedUp = true;
                    System.Type t = typeof(WorldRendererUtility);
                    worldRenderedProp = t.GetProperty("WorldRenderedNow") ?? t.GetProperty("WorldRendered");
                }
                if (worldRenderedProp != null)
                {
                    try { return (bool)worldRenderedProp.GetValue(null, null); }
                    catch { }
                }
                return false;
            }
        }
        private static System.Reflection.PropertyInfo worldRenderedProp;
        private static bool worldRenderedLookedUp;

        // The shadow is drawn in the world, so it correctly goes under trees, buildings and mountains.
        public override void GameComponentUpdate()
        {
            if (Current.ProgramState != ProgramState.Playing)
                return;     // main menu / loading: nothing to draw (and the play UI doesn't exist yet)
            if (!WorldMapOpen && Find.CurrentMap != null)
                ElectronicJamming.DrawSelectedRings();
            if (!AnythingToDraw)
                return;
            Camera cam = Find.Camera;
            if (cam == null || WorldMapOpen)
                return;
            if (shadowMat == null)
            {
                shadowMat = MaterialPool.MatFrom("Things/Shahed136/Shahed136_Top", ShaderDatabase.Transparent, new Color(0f, 0f, 0f, 0.35f));
                reconShadowMat = MaterialPool.MatFrom("Things/Shahed136/ReconDrone_Top", ShaderDatabase.Transparent, new Color(0f, 0f, 0f, 0.35f));
            }
            ForEachVisibleDrone((drawn, ground, h, angle, droneSize, recon) =>
            {
                float size = droneSize * (1f - 0.25f * h);
                Vector3 sp = ground;
                sp.y = AltitudeLayer.Shadows.AltitudeFor();
                Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(sp, Quaternion.AngleAxis(angle, Vector3.up), new Vector3(size, 1f, size)), recon ? reconShadowMat : shadowMat, 0, cam);
            });
            if (FpvDroneFlying.Active.Count > 0)
            {
                if (fpvShadowMat == null)
                    fpvShadowMat = MaterialPool.MatFrom("Things/Shahed136/FPVDrone_FlyA", ShaderDatabase.Transparent, new Color(0f, 0f, 0f, 0.3f));
                Map cur = Find.CurrentMap;
                foreach (FpvDroneFlying f in FpvDroneFlying.Active)
                {
                    if (f.Map != cur) continue;
                    Vector3 sp = f.exactPos + new Vector3(0.5f, 0f, -0.7f);
                    sp.y = AltitudeLayer.Shadows.AltitudeFor();
                    Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(sp, Quaternion.AngleAxis(f.heading, Vector3.up), new Vector3(FpvDrawSize * 0.8f, 1f, FpvDrawSize * 0.8f)), fpvShadowMat, 0, cam);
                }
            }
        }

        // The drone itself is drawn as an overlay on top of the finished map image,
        // so nothing on the map (trees, buildings, mountains, fog, darkness) can ever cover it.
        public override void GameComponentOnGUI()
        {
            if (Current.ProgramState != ProgramState.Playing)
                return;
            if (Event.current.type != EventType.Repaint || !AnythingToDraw)
                return;
            Camera cam = Find.Camera;
            if (cam == null || !cam.isActiveAndEnabled || WorldMapOpen)
                return;
            if (droneTex == null)
            {
                droneTex = ContentFinder<Texture2D>.Get("Things/Shahed136/Shahed136_Top");
                reconTex = ContentFinder<Texture2D>.Get("Things/Shahed136/ReconDrone_Top");
            }
            float cellPx = Find.CameraDriver.CellSizePixels / Prefs.UIScale;
            ForEachVisibleDrone((drawn, ground, h, angle, droneSize, recon) =>
            {
                Vector2 center = drawn.MapToUIPosition();
                float px = droneSize * cellPx;
                Rect r = new Rect(center.x - px / 2f, center.y - px / 2f, px, px);
                Matrix4x4 saved = GUI.matrix;
                // RimWorld's own helper: respects the UI scale setting (Unity's version rotates
                // around the wrong point when UI scale isn't 100%, throwing the drone off-screen).
                UI.RotateAroundPivot(angle, center);
                GUI.DrawTexture(r, recon ? reconTex : droneTex);
                GUI.matrix = saved;
            });
            if (FpvDroneFlying.Active.Count > 0)
            {
                if (fpvTexA == null)
                {
                    fpvTexA = ContentFinder<Texture2D>.Get("Things/Shahed136/FPVDrone_FlyA");
                    fpvTexB = ContentFinder<Texture2D>.Get("Things/Shahed136/FPVDrone_FlyB");
                }
                // flicker between the two frames so the props look like they spin (pauses with the game)
                Texture2D fpvTex = (Find.TickManager.Paused ? 0 : (Time.frameCount / 3) % 2) == 0 ? fpvTexA : fpvTexB;
                Map cur = Find.CurrentMap;
                foreach (FpvDroneFlying f in FpvDroneFlying.Active)
                {
                    if (f.Map != cur) continue;
                    Vector2 center = f.exactPos.MapToUIPosition();
                    float px = FpvDrawSize * cellPx;
                    Matrix4x4 saved = GUI.matrix;
                    UI.RotateAroundPivot(f.heading, center);
                    GUI.DrawTexture(new Rect(center.x - px / 2f, center.y - px / 2f, px, px), fpvTex);
                    GUI.matrix = saved;
                }
            }
        }

        private void TickAnimations()
        {
            int now = Find.TickManager.TicksGame;
            for (int i = anims.Count - 1; i >= 0; i--)
            {
                LaunchAnim a = anims[i];
                int t = now - a.startTick;
                LaunchState(t, out float fwd, out float h);
                Vector3 pos = a.origin + new Vector3(0f, 0f, fwd);
                if (a.map == null || !Find.Maps.Contains(a.map) || pos.z > a.map.Size.z + EdgeMargin || t > 6000)
                {
                    anims.RemoveAt(i);
                    continue;
                }
                if (t % (a.recon ? 9 : 3) == 0 && pos.ToIntVec3().InBounds(a.map))
                    FleckMaker.ThrowSmoke(pos + new Vector3(0f, 0f, -1.3f * (1f + 0.4f * h)), a.map, a.recon ? 0.4f : 0.6f + 0.8f * h);
                if (t == 1 && !a.recon)
                    FleckMaker.ThrowFireGlow(pos, a.map, 1.2f);
            }

            for (int i = dives.Count - 1; i >= 0; i--)
            {
                DiveAnim d = dives[i];
                if (d.map == null || !Find.Maps.Contains(d.map))
                {
                    dives.RemoveAt(i);
                    continue;
                }
                int t = now - d.startTick;
                if (!d.soundPlayed && t >= d.TotalTicks - (d.landing ? LandSoundTicks : SoundTicks))
                {
                    d.soundPlayed = true;
                    DefDatabase<SoundDef>.GetNamedSilentFail(d.landing ? "Recon_Land" : "Shahed_Incoming")?.PlayOneShot(new TargetInfo(d.cell, d.map));
                }
                if (t >= d.TotalTicks)
                {
                    dives.RemoveAt(i);
                    IntVec3 c = d.EndPos.ToIntVec3();
                    if (!c.InBounds(d.map))
                        c = d.cell;
                    if (d.landing)
                    {
                        ThingDef itemDef = DefDatabase<ThingDef>.GetNamedSilentFail("Shahed_ReconDrone");
                        if (itemDef != null)
                        {
                            Thing item = ThingMaker.MakeThing(itemDef);
                            GenPlace.TryPlaceThing(item, c, d.map, ThingPlaceMode.Near);
                            Messages.Message("Recon drone has landed.", item, MessageTypeDefOf.PositiveEvent, false);
                        }
                        continue;
                    }
                    if (d.decoy != null && !d.decoy.Destroyed)
                    {
                        d.decoy.arrived = true;      // reached the target normally, not intercepted
                        d.decoy.Destroy();
                    }
                    DroneWarheadExtension ext = d.warheadDef?.GetModExtension<DroneWarheadExtension>() ?? DroneWarheadExtension.Default;
                    SettlementSiege.NotifyStrike(sieges, d.map);
                    Warhead.Detonate(c, d.map, ext);
                    continue;
                }
                // follow a moving target (pawns) while diving
                if (d.thing != null && d.thing.Spawned && d.thing.Map == d.map)
                    d.cell = d.thing.Position;
                DiveState(d, t, out Vector3 ground, out Vector3 drawn, out float h);

                if (!d.landing)
                {
                    // spawn the decoy shell as soon as the drone is over the map
                    if (!d.decoySpawned && ground.ToIntVec3().InBounds(d.map))
                    {
                        d.decoySpawned = true;
                        d.decoy = Projectile_DroneDecoy.LaunchFor(d.map, ground, d.cell, d.attacker);
                    }
                    // decoy gone before the drone arrived = hit by air defense / shield
                    if (d.decoySpawned && d.decoy != null && d.decoy.Destroyed && !d.decoy.arrived)
                    {
                        d.hitsTaken++;
                        if (d.hitsTaken >= HitsToShootDown)
                        {
                            dives.RemoveAt(i);
                            Intercepted(d, ground);
                            continue;
                        }
                        // damaged but still flying: sparks and smoke, and a fresh decoy to shoot at
                        FleckMaker.ThrowMicroSparks(ground, d.map);
                        FleckMaker.ThrowSmoke(ground, d.map, 1.5f);
                        d.decoy = ground.ToIntVec3().InBounds(d.map) ? Projectile_DroneDecoy.LaunchFor(d.map, ground, d.cell, d.attacker) : null;
                    }
                }

                // GNSS jammers: lose satellite navigation once, drift off target or crash
                if (!d.landing && !d.gnssJammed && ground.ToIntVec3().InBounds(d.map))
                {
                    bool antiJam = d.attacker == Faction.OfPlayer && GnssJamming.AntiJamResearched;
                    int jammers = GnssJamming.JammersCovering(d.map, ground, antiJam, out GnssJammerExtension jext);
                    if (jammers > 0)
                    {
                        d.gnssJammed = true;
                        if (Rand.Chance(jext.crashChance))
                        {
                            dives.RemoveAt(i);
                            if (d.decoy != null && !d.decoy.Destroyed) { d.decoy.arrived = true; d.decoy.Destroy(); }
                            IntVec3 cc = ground.ToIntVec3();
                            FleckMaker.ThrowSmoke(ground, d.map, 3f);
                            GenExplosion.DoExplosion(cc, d.map, 2.9f, DamageDefOf.Bomb, null, 20);
                            Messages.Message("A Shahed-136 lost its GPS signal and crashed.", new TargetInfo(cc, d.map), MessageTypeDefOf.PositiveEvent, false);
                            continue;
                        }
                        // push the aim point off target; more jammers = more drift
                        float drift = Rand.Range(jext.driftMin, jext.driftMax) * (1f + 0.5f * (jammers - 1));
                        float ang = Rand.Range(0f, 360f) * Mathf.Deg2Rad;
                        IntVec3 nc = (d.cell.ToVector3Shifted() + new Vector3(Mathf.Sin(ang), 0f, Mathf.Cos(ang)) * drift).ToIntVec3();
                        nc.x = Mathf.Clamp(nc.x, 1, d.map.Size.x - 2);
                        nc.z = Mathf.Clamp(nc.z, 1, d.map.Size.z - 2);
                        // rebuild the path from where the drone is now, so it swerves instead of jumping
                        Vector3 rel = ground - nc.ToVector3Shifted();
                        float rem = Mathf.Max(0.5f, rel.magnitude);
                        d.fromDir = rel.magnitude > 0.01f ? rel.normalized : d.fromDir;
                        d.startDist = rem + d.speed * t;
                        d.cell = nc;
                        d.thing = null;      // can't track a moving target anymore
                        if (d.decoy != null && !d.decoy.Destroyed) { d.decoy.arrived = true; d.decoy.Destroy(); }
                        d.decoy = Projectile_DroneDecoy.LaunchFor(d.map, ground, nc, d.attacker);
                        Messages.Message("A Shahed-136 lost its GPS signal and is drifting off course!", new TargetInfo(nc, d.map), MessageTypeDefOf.PositiveEvent, false);
                        DiveState(d, t, out ground, out drawn, out h);
                    }
                }

                Vector3 tail = drawn - d.Heading * 1.3f;
                if (!d.landing && t % 3 == 0 && tail.ToIntVec3().InBounds(d.map))
                    FleckMaker.ThrowSmoke(tail, d.map, 0.5f + 0.6f * h);
            }
        }

        private static void Intercepted(DiveAnim d, Vector3 ground)
        {
            IntVec3 c = ground.ToIntVec3();
            if (!c.InBounds(d.map))
                c = d.cell;
            FleckMaker.ThrowSmoke(ground, d.map, 3f);
            GenExplosion.DoExplosion(c, d.map, 2.9f, DamageDefOf.Bomb, null, 20);
            Messages.Message("A Shahed-136 was shot down before reaching its target!", new TargetInfo(c, d.map), MessageTypeDefOf.NeutralEvent, false);
        }

        /// <summary>An enemy Shahed diving onto a cell of this map (debug action, later enemy attacks).</summary>
        public void StartEnemyDive(Map map, IntVec3 cell, Faction faction)
        {
            StartDive(map, cell, null, DefDatabase<ThingDef>.GetNamedSilentFail("Shahed136Incoming"), faction);
            Find.LetterStack.ReceiveLetter("Shahed incoming",
                "An enemy Shahed-136" + (faction != null ? " of " + faction.Name : "") + " is diving toward your colony. Air defense can still shoot it down.",
                LetterDefOf.ThreatBig, new TargetInfo(cell, map));
        }

        private void StartDive(Map map, IntVec3 cell, Thing thing, ThingDef warheadDef, Faction attacker)
        {
            float a = Rand.Range(0f, 360f) * Mathf.Deg2Rad;
            Vector3 dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
            Vector3 c = cell.ToVector3Shifted();

            // distance from the target to the map edge along the approach direction
            float tx = dir.x > 0.001f ? (map.Size.x - c.x) / dir.x : dir.x < -0.001f ? -c.x / dir.x : float.MaxValue;
            float tz = dir.z > 0.001f ? (map.Size.z - c.z) / dir.z : dir.z < -0.001f ? -c.z / dir.z : float.MaxValue;
            float dist = Mathf.Min(tx, tz) + EdgeMargin;

            // cruise speed, but slow down if the path is too short for the incoming sound
            float speed = Mathf.Min(CruiseSpeed, dist / SoundTicks);

            dives.Add(new DiveAnim
            {
                map = map,
                cell = cell,
                thing = thing,
                warheadDef = warheadDef,
                startTick = Find.TickManager.TicksGame,
                fromDir = dir,
                startDist = dist,
                speed = speed,
                attacker = attacker
            });
        }

        public override void GameComponentTick()
        {
            if (anims.Count > 0 || dives.Count > 0)
                TickAnimations();

            if (Find.TickManager.TicksGame % 60 == 0)
                ElectronicJamming.DrainAll(60);

            if (sieges.Count > 0 && Find.TickManager.TicksGame % 120 == 0)
                SettlementSiege.Tick(sieges);

            // recon drones: world part and on-map part
            List<WorldObject_ReconDrone> recons = null;
            foreach (WorldObject wo in Find.WorldObjects.AllWorldObjects)
            {
                if (wo is WorldObject_ReconDrone r)
                    (recons ?? (recons = new List<WorldObject_ReconDrone>())).Add(r);
            }
            if (recons != null)
                foreach (WorldObject_ReconDrone r in recons)
                    if (!r.Destroyed) r.DroneTick();
            if (MapDrones.Count > 0)
                foreach (ReconDroneFlying d in MapDrones.ToList())
                    d.DroneTick();
            if (FpvDroneFlying.Active.Count > 0)
                foreach (FpvDroneFlying f in FpvDroneFlying.Active.ToList())
                    f.DroneTick();

            for (int i = flights.Count - 1; i >= 0; i--)
            {
                DroneFlight f = flights[i];
                f.ticksLeft--;
                if (f.departTicksLeft > 0)
                {
                    f.departTicksLeft--;
                    if (f.departTicksLeft == 0)
                        SpawnWorldMarker(f);
                }
                if (f.ticksLeft <= 0)
                {
                    flights.RemoveAt(i);
                    Arrive(f);
                }
            }
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref flights, "shahedFlights", LookMode.Deep);
            Scribe_Collections.Look(ref dives, "shahedDives", LookMode.Deep);
            Scribe_Collections.Look(ref sieges, "shahedSieges", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (flights == null) flights = new List<DroneFlight>();
                if (dives == null) dives = new List<DiveAnim>();
                if (sieges == null) sieges = new List<SiegeRecord>();
                sieges.RemoveAll(r => r.settlement == null);
            }
        }

        private void Arrive(DroneFlight f)
        {
            if (f.worldObject != null && !f.worldObject.Destroyed)
                f.worldObject.Destroy();

            if (f.strikeDef == null)
                f.strikeDef = DefDatabase<ThingDef>.GetNamed("Shahed136Incoming");

            // 1) Strike on a loaded map (another colony, raid site, etc.)
            if (f.targetMap != null)
            {
                if (!Find.Maps.Contains(f.targetMap))
                {
                    Messages.Message("A Shahed-136 lost its target: the map no longer exists.", MessageTypeDefOf.NeutralEvent, false);
                    return;
                }
                IntVec3 cell = f.targetCell;
                if (f.targetThing != null && f.targetThing.Spawned && f.targetThing.Map == f.targetMap)
                    cell = f.targetThing.Position; // follow moving targets like pawns
                if (!cell.IsValid || !cell.InBounds(f.targetMap))
                    cell = f.targetMap.Center;
                StrikeCell(f, f.targetMap, cell);
                return;
            }

            // 2) Strike on a settlement
            Settlement s = f.targetSettlement;
            if (s == null || s.Destroyed)
            {
                Messages.Message("A Shahed-136 found nothing at its target and crashed.", MessageTypeDefOf.NeutralEvent, false);
                return;
            }

            if (s.HasMap)
            {
                // The settlement map got loaded while the drone was flying (e.g. you attacked it). Hit an enemy building.
                Building b = s.Map.listerBuildings.allBuildingsNonColonist
                    .Where(x => x.Faction == s.Faction)
                    .RandomElementWithFallback();
                StrikeCell(f, s.Map, b != null ? b.Position : s.Map.Center);
            }
            else
            {
                Find.LetterStack.ReceiveLetter(
                    "Drone strike: " + s.LabelCap,
                    "A Shahed-136 struck " + s.LabelCap + " (" + s.Faction.Name + "). Buildings were damaged and the settlement was thrown into chaos.",
                    LetterDefOf.NeutralEvent,
                    new LookTargets(new GlobalTargetInfo(s)));
            }

            if (s.Faction != null && s.Faction != Faction.OfPlayer && f.goodwillImpact != 0)
            {
                HistoryEventDef reason = DefDatabase<HistoryEventDef>.GetNamedSilentFail("AttackedSettlement");
                s.Faction.TryAffectGoodwillWith(Faction.OfPlayer, f.goodwillImpact, true, true, reason);
            }
        }

        private void StrikeCell(DroneFlight f, Map map, IntVec3 cell)
        {
            Thing follow = (f.targetThing != null && f.targetThing.Spawned && f.targetThing.Map == map) ? f.targetThing : null;
            StartDive(map, cell, follow, f.strikeDef, Faction.OfPlayer);
            Messages.Message("A Shahed-136 is diving on its target!", new TargetInfo(cell, map), MessageTypeDefOf.ThreatSmall, false);
        }
    }
}
