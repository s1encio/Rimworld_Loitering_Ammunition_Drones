using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace Shahed136Drones
{
    /// <summary>
    /// Grid pathfinding for low-flying drones.
    /// A drone can be anywhere open to the sky (it flies over walls, doors and fences),
    /// but never under a roof (so it can't fly into buildings) and never through natural rock.
    /// </summary>
    public static class FlyPath
    {
        private const int MaxExpanded = 200000;   // enough for any map size
        public const float BodyRadius = 0.3f;

        public static bool Passable(Map map, IntVec3 c)
        {
            if (!c.InBounds(map) || c.Roofed(map))
                return false;
            Building ed = c.GetEdifice(map);
            if (ed != null && ed.def.building != null && ed.def.building.isNaturalRock)
                return false;
            return true;
        }

        /// <summary>Can the drone's body (a small square) be at this exact position? The goal cell is always allowed.</summary>
        public static bool PosOk(Map map, Vector3 p, IntVec3 goal)
        {
            for (int i = 0; i < 4; i++)
            {
                float ox = (i & 1) == 0 ? -BodyRadius : BodyRadius;
                float oz = (i & 2) == 0 ? -BodyRadius : BodyRadius;
                IntVec3 c = new IntVec3(Mathf.FloorToInt(p.x + ox), 0, Mathf.FloorToInt(p.z + oz));
                if (c == goal) continue;
                if (!Passable(map, c)) return false;
            }
            return true;
        }

        /// <summary>Can the drone fly in a straight line from a to b without its body touching anything?</summary>
        public static bool SegmentClear(Map map, Vector3 a, Vector3 b, IntVec3 goal)
        {
            a.y = 0f; b.y = 0f;
            float len = (b - a).magnitude;
            int steps = Mathf.Max(1, Mathf.CeilToInt(len / 0.25f));
            for (int i = 1; i <= steps; i++)
            {
                Vector3 p = Vector3.Lerp(a, b, (float)i / steps);
                if (!PosOk(map, p, goal)) return false;
            }
            return true;
        }

        /// <summary>Nearest free (open-sky, non-rock) cell to c; c itself if it's free.</summary>
        public static IntVec3 NearestFree(Map map, IntVec3 c)
        {
            foreach (IntVec3 x in GenRadial.RadialCellsAround(c, 20f, true))
                if (Passable(map, x)) return x;
            return c;
        }

        /// <summary>Straight grid line between two cells without crossing anything blocked (goal cell excluded).</summary>
        public static bool ClearLine(Map map, IntVec3 a, IntVec3 b)
        {
            int x0 = a.x, z0 = a.z, x1 = b.x, z1 = b.z;
            int dx = Mathf.Abs(x1 - x0), dz = Mathf.Abs(z1 - z0);
            int sx = x0 < x1 ? 1 : -1, sz = z0 < z1 ? 1 : -1;
            int err = dx - dz;
            while (!(x0 == x1 && z0 == z1))
            {
                int e2 = 2 * err;
                int nx = x0, nz = z0;
                if (e2 > -dz) { err -= dz; nx += sx; }
                if (e2 < dx) { err += dx; nz += sz; }
                if (nx == x1 && nz == z1) return true;
                if (!Passable(map, new IntVec3(nx, 0, nz))) return false;
                if (nx != x0 && nz != z0 && (!Passable(map, new IntVec3(nx, 0, z0)) || !Passable(map, new IntVec3(x0, 0, nz))))
                    return false;
                x0 = nx; z0 = nz;
            }
            return true;
        }

        private static float H(IntVec3 a, IntVec3 b)
        {
            int dx = Mathf.Abs(a.x - b.x), dz = Mathf.Abs(a.z - b.z);
            return (dx + dz) + (1.4142f - 2f) * Mathf.Min(dx, dz);   // octile distance
        }

        /// <summary>
        /// A* from start to goal. If the goal can't be reached (e.g. a pawn inside a roofed building),
        /// returns a path to the reachable cell closest to it, so the drone hits the outer wall.
        /// </summary>
        public static List<IntVec3> Find(Map map, IntVec3 start, IntVec3 goal, out bool reached)
        {
            reached = false;
            CellIndices ci = map.cellIndices;
            int n = ci.NumGridCells;
            float[] g = new float[n];
            int[] parent = new int[n];
            bool[] closed = new bool[n];
            for (int i = 0; i < n; i++) { g[i] = float.MaxValue; parent[i] = -1; }

            int si = ci.CellToIndex(start), gi = ci.CellToIndex(goal);
            List<KeyValuePair<float, int>> heap = new List<KeyValuePair<float, int>>();
            g[si] = 0f;
            Push(heap, H(start, goal), si);
            int best = si; float bestH = H(start, goal);
            int expanded = 0;

            while (heap.Count > 0 && expanded < MaxExpanded)
            {
                int idx = Pop(heap);
                if (closed[idx]) continue;
                closed[idx] = true;
                expanded++;
                if (idx == gi) { reached = true; best = gi; break; }
                IntVec3 c = ci.IndexToCell(idx);
                float h = H(c, goal);
                if (h < bestH) { bestH = h; best = idx; }
                for (int k = 0; k < 8; k++)
                {
                    IntVec3 d = GenAdj.AdjacentCells[k];
                    IntVec3 nb = c + d;
                    if (!nb.InBounds(map)) continue;
                    int ni = ci.CellToIndex(nb);
                    if (closed[ni]) continue;
                    // the goal cell itself only counts if the drone can actually get there (open sky)
                    if (!Passable(map, nb)) continue;
                    bool diag = d.x != 0 && d.z != 0;
                    if (diag && (!Passable(map, new IntVec3(c.x + d.x, 0, c.z)) || !Passable(map, new IntVec3(c.x, 0, c.z + d.z))))
                        continue;
                    float ng = g[idx] + (diag ? 1.4142f : 1f);
                    if (ng < g[ni])
                    {
                        g[ni] = ng;
                        parent[ni] = idx;
                        Push(heap, ng + H(nb, goal), ni);
                    }
                }
            }

            List<IntVec3> path = new List<IntVec3>();
            for (int i = best; i != -1 && i != si; i = parent[i])
                path.Add(ci.IndexToCell(i));
            path.Reverse();
            return path;
        }

        // ---- tiny binary min-heap on (priority, index) ----
        private static void Push(List<KeyValuePair<float, int>> h, float pri, int val)
        {
            h.Add(new KeyValuePair<float, int>(pri, val));
            int i = h.Count - 1;
            while (i > 0)
            {
                int p = (i - 1) / 2;
                if (h[p].Key <= h[i].Key) break;
                KeyValuePair<float, int> t = h[p]; h[p] = h[i]; h[i] = t; i = p;
            }
        }

        private static int Pop(List<KeyValuePair<float, int>> h)
        {
            int top = h[0].Value;
            int last = h.Count - 1;
            h[0] = h[last];
            h.RemoveAt(last);
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1, r = l + 1, m = i;
                if (l < h.Count && h[l].Key < h[m].Key) m = l;
                if (r < h.Count && h[r].Key < h[m].Key) m = r;
                if (m == i) break;
                KeyValuePair<float, int> t = h[m]; h[m] = h[i]; h[i] = t; i = m;
            }
            return top;
        }
    }
}
