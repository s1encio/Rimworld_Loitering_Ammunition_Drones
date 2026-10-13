using UnityEngine;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Shahed136Drones
{
    /// <summary>Marker on the world map that moves from the launcher to the target while the drone is in flight.</summary>
    public class WorldObject_DroneInFlight : WorldObject
    {
        public Vector3 from;
        public Vector3 to;
        public int startTick;
        public int arrivalTick;
        public string targetLabel = "target";

        private float Progress => arrivalTick <= startTick ? 1f : Mathf.Clamp01((float)(Find.TickManager.TicksGame - startTick) / (arrivalTick - startTick));

        public override Vector3 DrawPos => Vector3.Slerp(from, to, Progress);

        // Draw the drone ourselves so it always points along its flight path.
        public override void Draw()
        {
            Vector3 pos = DrawPos;
            Vector3 normal = pos.normalized;
            Vector3 dir = to - pos;
            dir -= normal * Vector3.Dot(dir, normal);
            if (dir.sqrMagnitude < 1e-6f)
            {
                dir = to - from;
                dir -= normal * Vector3.Dot(dir, normal);
            }
            if (dir.sqrMagnitude < 1e-6f)
                dir = Vector3.Cross(normal, Vector3.right);
            Quaternion rot = Quaternion.LookRotation(dir.normalized, normal);

            // grow when zoomed out so it stays visible, like vanilla icons
            float size = Find.WorldGrid.AverageTileSize * Mathf.Lerp(1.1f, 7f, Find.WorldCameraDriver.AltitudePercent);
            Matrix4x4 m = Matrix4x4.TRS(pos + normal * 0.08f, rot, new Vector3(size, 1f, size));
            Graphics.DrawMesh(MeshPool.plane10, m, Material, WorldCameraManager.WorldLayer);
        }

        public override string GetInspectString()
        {
            int left = Mathf.Max(0, arrivalTick - Find.TickManager.TicksGame);
            return "En route to " + targetLabel + ".\nTime to impact: " + left.ToStringTicksToPeriod();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref from, "from");
            Scribe_Values.Look(ref to, "to");
            Scribe_Values.Look(ref startTick, "startTick");
            Scribe_Values.Look(ref arrivalTick, "arrivalTick");
            Scribe_Values.Look(ref targetLabel, "targetLabel", "target");
        }
    }
}
