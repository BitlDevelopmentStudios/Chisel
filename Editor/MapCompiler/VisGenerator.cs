using Chisel.Utils;
using Microsoft.Xna.Framework;
using Rockwall;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MapCompiler
{
    public class TempVisLeaf
    {
        public Vector3 center;
        public List<Portal> portals = new List<Portal>();
        public List<uint> pvs = new List<uint>();
        public bool IsEmpty;
        public bool HasSkybox;
        public int bspLeafID;
        public int visitedCount;
    }
    public class PortalComparer : IEqualityComparer<Portal>
    {
        public int GetHashCode([DisallowNull] Portal obj)
        {
            int lo = Math.Min(obj.LeafFront, obj.LeafBack);
            int hi = Math.Max(obj.LeafFront, obj.LeafBack);
            return HashCode.Combine(lo, hi);
        }

        public bool Equals(Portal? a, Portal? b)
        {
            if (a is null || b is null) return a is null && b is null;
            // Same leaf pair (either direction)
            if (a.LeafFront == b.LeafFront && a.LeafBack == b.LeafBack) return true;
            if (a.LeafFront == b.LeafBack && a.LeafBack == b.LeafFront) return true;
            return false;
        }
    }
    public static class Portalizer
    {
        static TempVisLeaf outsideNode;
        static TempVisLeaf[] visLeaves;
        public static List<Plane> planes = new List<Plane>();
        static Dictionary<Portal, int> portalIndexMap = new Dictionary<Portal, int>();
        static List<Portal> portals = new List<Portal>();
        static Dictionary<int, List<int>> invisibleBrushFaces = new ();
        public static Dictionary<int, List<(Vector3[] sourcePoly, Plane sourcePlane, Vector3[] passPoly, Plane passPlane)>> portalTrace = new ();
        static volatile ProgressBar progressBar = new ProgressBar();
        static volatile float progressTarget;
        static volatile int progress;
        static int outleaves;
        struct PlaneBasis
        {
            public Vector3 origin, u, v;
        }

        static PlaneBasis BuildPlaneBasis(Vector3 normal, float d)
        {
            Vector3 arbitrary = (Math.Abs(normal.X) > 0.9f)
                              ? new Vector3(0, 1, 0)
                              : new Vector3(1, 0, 0);
            Vector3 u = Vector3.Cross(arbitrary, normal);
            u.Normalize();
            Vector3 v = Vector3.Cross(normal, u);
            v.Normalize();
            Vector3 origin = normal * -(d);

            return new PlaneBasis { origin = origin, u = u, v = v };
        }

        // A standard monotone‐chain convex hull on 2D points:
        static List<Vector2> ConvexHull2D(List<Vector2> pts)
        {
            pts.Sort((a, b) => a.X != b.X
                ? a.X.CompareTo(b.X)
                : a.Y.CompareTo(b.Y));
            List<Vector2> lower = new(), upper = new();
            void Build(List<Vector2> hull, Vector2 p)
            {
                while (hull.Count >= 2)
                {
                    var q = hull[hull.Count - 2];
                    var r = hull[hull.Count - 1];
                    if (((r - q).Cross(p - r)) <= 0)
                        hull.RemoveAt(hull.Count - 1);
                    else break;
                }
                hull.Add(p);
            }
            foreach (var p in pts) Build(lower, p);
            for (int i = pts.Count - 1; i >= 0; i--) Build(upper, pts[i]);

            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);
            return lower;
        }

        // convenience: 2D cross‐product
        static float Cross(this Vector2 a, Vector2 b)
            => a.X * b.Y - a.Y * b.X;
        public static List<VisLeaf> GetVisLeaves(Brush[] brushes)
        {
            int leafCount = visLeaves.Length;
            var result = new List<VisLeaf>(leafCount);

            progressBar = new ProgressBar();
            progressTarget = leafCount;
            progress = 0;

            ConcurrentDictionary<int, ushort[]> finalBrushMapping = new ConcurrentDictionary<int, ushort[]>();

            Parallel.For(0, leafCount, i =>
            {
                var tv = visLeaves[i];
                var b = tv.portals.SelectMany(p => p.Brushes).Distinct().ToArray();
                finalBrushMapping.TryAdd(i, b);

                tv.HasSkybox = b.Any(i => brushes[i].IsSkybox) || tv.HasSkybox;
            });

            for (int i = 0; i < leafCount; i++)
            {
                progress++;
                progressBar.Report(progress/progressTarget);

                var tv = visLeaves[i];
                var localPortals = tv.portals;
                int pCount = localPortals.Count;
                var ids = new int[pCount];

                for (int j = 0; j < pCount; j++)
                    ids[j] = portalIndexMap[localPortals[j]];

                result.Add(new VisLeaf
                {
                    BspLeafID = tv.bspLeafID,
                    IsEmpty = tv.IsEmpty,
                    HasSkybox = tv.HasSkybox,
                    Portals = ids,
                    PVS = tv.pvs!=null? tv.pvs.Distinct().ToArray():Array.Empty<uint>(),
                    Brushes = finalBrushMapping[i]
                });
            }
            progressBar.Dispose();
            return result;
        }
        public static List<Portal> GetPortals() => portals;
        public static void Reset()
        {
            outsideNode = null;
            visLeaves = null;
            planes = new List<Plane>();
            portalIndexMap = new Dictionary<Portal, int>();
            portals = new List<Portal>();
            invisibleBrushFaces = new();
            portalTrace = new();
            progressBar = new ProgressBar();
            progressTarget = 0;
            progress = 0;
            outleaves = 0;
            wasSolid = null;
        }
        public static Portal Clone(Portal from)
        {
            return new Portal
            {
                Brushes = from.Brushes == null || from.Brushes.Length == 0
                ? Array.Empty<ushort>()
                : (ushort[])from.Brushes.Clone(),
                Vertices = from.Vertices,
                LeafFront = from.LeafFront,
                LeafBack = from.LeafBack,
                Plane = from.Plane,
            };
        }
        static Portal AddPortalToNodes(Portal portal, TempVisLeaf front, TempVisLeaf back)
        {
            if (portal == null) return null;

            var p = Clone(portal);

            p.LeafFront = front.bspLeafID;
            p.LeafBack = back.bspLeafID;

            front.portals.Add(p);
            back.portals.Add(p);

            return p;
        }

        static void RemovePortalFromNode(Portal portal, TempVisLeaf leaf)
        {
            if (portal.LeafFront != -1) visLeaves[portal.LeafFront].portals.RemoveAll(p=>(p.LeafFront == portal.LeafFront && p.LeafBack == portal.LeafBack) || (p.LeafBack == portal.LeafFront && p.LeafFront == portal.LeafBack));
            if (portal.LeafBack != -1) visLeaves[portal.LeafBack].portals.RemoveAll(p=>(p.LeafFront == portal.LeafFront && p.LeafBack == portal.LeafBack) || (p.LeafBack == portal.LeafFront && p.LeafFront == portal.LeafBack));
            leaf.portals.RemoveAll(p=>(p.LeafFront == portal.LeafFront && p.LeafBack == portal.LeafBack) || (p.LeafBack == portal.LeafFront && p.LeafFront == portal.LeafBack));
            portals.RemoveAll(p=>(p.LeafFront == portal.LeafFront && p.LeafBack == portal.LeafBack) || (p.LeafBack == portal.LeafFront && p.LeafFront == portal.LeafBack));

            if(portal.LeafFront == leaf.bspLeafID)
            {
                portal.LeafFront = -1;
            }
            if (portal.LeafBack == leaf.bspLeafID)
            {
                portal.LeafBack = -1;
            }

            //portals.Remove(portal);
        }

        static void PrintPortal(Portal p)
        {
            Console.WriteLine($"Portal F:{p.LeafFront} B:{p.LeafBack}, V:{p.Vertices}");
        }

        public static Plane FlipPlane(Plane plane)
            => new Plane(-plane.Normal, -plane.D);
        public static float SignedWindingArea(Vector3[] verts, Plane plane)
        {
            // build a 2D basis on the plane
            var basis = BuildPlaneBasis(plane.Normal, plane.D);
            var pts2D = verts.Select(v => new Vector2(
                Vector3.Dot(v - basis.origin, basis.u),
                Vector3.Dot(v - basis.origin, basis.v)
            )).ToArray();

            // shoelace formula
            float area = 0;
            for (int i = 0, n = pts2D.Length; i < n; i++)
            {
                var a = pts2D[i];
                var b = pts2D[(i + 1) % n];
                area += a.X * b.Y - b.X * a.Y;
            }
            return area * 0.5f;
        }

        [ThreadStatic] static List<Portal> t_portalScratch;

        static List<Portal> GetTraversablePortals(TempVisLeaf leaf, bool excludeSolid)
        {
            var buf = t_portalScratch ??= new List<Portal>(16);
            buf.Clear();
            var portals = leaf.portals;
            for (int i = 0; i < portals.Count; i++)
            {
                var p = portals[i];
                bool flip = p.LeafBack == leaf.bspLeafID;
                int next = flip ? p.LeafFront : p.LeafBack;
                if (next == -1 || p.Brushes.Length > 0 || BSPRoot.Nodes[next].split) continue;
                if (excludeSolid && BSPRoot.Nodes[next].solid) continue;
                buf.Add(p);
            }
            return buf;
        }
        static Vector3[] ReverseCopy(Vector3[] v)
        {
            var r = new Vector3[v.Length];
            for (int i = 0; i < v.Length; i++) r[i] = v[v.Length - 1 - i];
            return r;
        }

        public static void CalculateLeafCenters()
        {
            Console.WriteLine("--- CalculateLeafCenters ---");

            progressTarget = visLeaves.Length;
            progressBar = new ProgressBar();
            progress = 0;
            foreach (var leaf in visLeaves)
            {
                progress++;
                progressBar.Report(progress / (float)progressTarget);
                foreach (var portal in leaf.portals)
                {
                    Vector3 portalCenter = Vector3.Zero;
                    foreach (var vert in portal.Vertices) portalCenter += vert;
                    portalCenter /= portal.Vertices.Length;

                    leaf.center += portalCenter;
                }
                leaf.center /= leaf.portals.Count;
            }
            progressBar.Dispose();
        }
        public static void CalculateFinePVS()
        {
            Console.WriteLine("--- CalculateFinePVS ---");

            var leavesToCheck = visLeaves.Where(l => !BSPRoot.Nodes[l.bspLeafID].solid && !BSPRoot.Nodes[l.bspLeafID].split).ToArray();

            progressTarget = leavesToCheck.Length;
            progressBar = new ProgressBar();
            progress = 0;
            progressBar.Report(0);

            for(int i = 0; i < leavesToCheck.Length; i++)
            {
                var leaf = leavesToCheck[i];
                leavesToCheck[i].pvs.Clear();
                progress++;
                progressBar.Report(progress / (float)progressTarget);

                if (BSPRoot.Nodes[leaf.bspLeafID].solid) return;
                if (BSPRoot.Nodes[leaf.bspLeafID].split) return;

                var sourcePortals = GetTraversablePortals(leaf, true).ToArray();

                //foreach (var sourcePortal in sourcePortals)
                Parallel.ForEach(sourcePortals,(sourcePortal)=>
                {
                    bool sourceFlip = sourcePortal.LeafBack == leaf.bspLeafID;
                    int next = (sourceFlip ? sourcePortal.LeafFront : sourcePortal.LeafBack);

                    float ldot = sourcePortal.Plane.DotCoordinate(leaf.center);
                    sourceFlip = ldot > 0;

                    LeafFlow(visLeaves[next], sourceFlip? ReverseCopy(sourcePortal.Vertices) : sourcePortal.Vertices, sourceFlip ? FlipPlane(sourcePortal.Plane) : sourcePortal.Plane, null, new Plane(), sourcePortal, sourcePortal.mightsee);

                    Interlocked.Increment(ref sourcePortal.worked);
                });
            }

            //var portalsToCheck = portals.Where(p =>
            //{
            //    return !(p.LeafFront == -1 || p.Brushes.Length > 0 || BSPRoot.nodes[p.LeafFront].split || BSPRoot.nodes[p.LeafFront].solid) && !((p.LeafBack == -1 || p.Brushes.Length > 0 || BSPRoot.nodes[p.LeafBack].split || BSPRoot.nodes[p.LeafBack].solid));
            //}).ToArray();

            //Parallel.ForEach(portalsToCheck, sourcePortal =>
            //{
            //    progress++;
            //    progressBar.Report(progress / progressTarget);

            //    var leaf = visLeaves[sourcePortal.LeafFront];

            //    bool sourceFlip = sourcePortal.Plane.DotCoordinate(leaf.center) > 0;
            //    LeafFlow(leaf, sourcePortal.Vertices, sourceFlip ? FlipPlane(sourcePortal.Plane) : sourcePortal.Plane, null, new Plane(), sourcePortal, sourcePortal.mightsee.ToHashSet());

            //    leaf = visLeaves[sourcePortal.LeafBack];

            //    sourceFlip = sourcePortal.Plane.DotCoordinate(leaf.center) > 0;
            //    LeafFlow(leaf, sourcePortal.Vertices, sourceFlip ? FlipPlane(sourcePortal.Plane) : sourcePortal.Plane, null, new Plane(), sourcePortal, sourcePortal.mightsee.ToHashSet());
            //});

            foreach (var leaf in leavesToCheck)
            {
                var sourcePortals = GetTraversablePortals(leaf, true);

                leaf.pvs = sourcePortals.SelectMany(p => Bitset.ToList(p.pvs)).Append((uint)leaf.bspLeafID).ToList();
            }

            progressBar.Dispose();
        }
        struct FlowInfo
        {
            public TempVisLeaf leaf;
            public Vector3[] sourcePoly;
            public Plane sourcePlane;
            public Vector3[] passPoly;
            public Plane passPlane;
            public int previousNode;
            public ulong[] mightsee;

            public FlowInfo(TempVisLeaf leaf, Vector3[] sourcePoly, Plane sourcePlane, Vector3[] passPoly, Plane passPlane, ulong[] mightsee, int prevnode)
            {
                this.leaf = leaf;
                this.sourcePoly = sourcePoly;
                this.sourcePlane = sourcePlane;
                this.passPoly = passPoly;
                this.passPlane = passPlane;
                this.mightsee = mightsee;
                this.previousNode = previousNode;
            }
        }
        static void LeafFlow(TempVisLeaf leaf, Vector3[] sourcePoly, Plane sourcePlane, Vector3[] passPoly, Plane passPlane, Portal sourcePortal, ulong[] mightsee)
        {
            var stack = new Stack<FlowInfo>();
            stack.Push(new(leaf, sourcePoly, sourcePlane, passPoly, passPlane, mightsee, -1));

            var currentPVS = Bitset.Create(visLeaves.Length);
            while (stack.Count > 0)
            {
                CheckLeaf(stack.Pop(), currentPVS, stack);
            }

            lock (sourcePortal.pvs) Bitset.Or(sourcePortal.pvs, currentPVS);
        }
        public static void InitPortalBitsets()
        {
            foreach (var p in portals)
            {
                p.mightsee = Bitset.Create(visLeaves.Length);
                p.pvs = Bitset.Create(visLeaves.Length);
            }
        }
        static void CheckLeaf(FlowInfo info, ulong[] currentPVS, Stack<FlowInfo> source)
        {
            Bitset.Set(currentPVS, info.leaf.bspLeafID);

            if (BSPRoot.Nodes[info.leaf.bspLeafID].solid) return;
            if (BSPRoot.Nodes[info.leaf.bspLeafID].split) return;

            var portals = GetTraversablePortals(info.leaf, false);

            visLeaves[info.leaf.bspLeafID].visitedCount++;
            
            foreach (var portal in portals)
            {
                bool flip;
                bool isBack = portal.LeafBack == info.leaf.bspLeafID;
                int next = (isBack ? portal.LeafFront : portal.LeafBack);

                if (!Bitset.Get(info.mightsee, next)) continue;

                var t_msee = new ulong[info.mightsee.Length];
                Array.Copy(info.mightsee, t_msee, t_msee.Length);

                if (portal.mightsee.Length > 0 && portal.worked != 2) 
                    Bitset.And(t_msee, portal.mightsee);
                else if (portal.worked == 2) 
                    Bitset.And(t_msee, portal.pvs);

                if (!Bitset.AnyOutside(t_msee, currentPVS)) continue;

                //if (currentPVS.Contains((uint)next) && visLeaves[next].visitedCount >= 6) continue;

                float ldot = portal.Plane.DotCoordinate(info.leaf.center);

                // If this portal is facing toward the center of the leaf, we need to flip it.
                flip = ldot > 0;

                var pPlane = flip ?FlipPlane(portal.Plane):portal.Plane;
                var pVertices = flip ?portal.Vertices.Reverse().ToArray():portal.Vertices;

                var sourceClipped = info.sourcePoly;
                var targetPoly = pVertices;
                targetPoly = ClipWinding(pVertices, info.sourcePlane, false);
                if (targetPoly == null) continue;

                if (info.passPoly == null)
                {
                    source.Push(new(visLeaves[next], sourceClipped, info.sourcePlane, targetPoly, pPlane, t_msee, info.leaf.bspLeafID));
                    continue;
                }

                Plane backplane = FlipPlane(pPlane);

                if (backplane.Normal == info.passPlane.Normal)
                    continue;

                // line 311
                targetPoly = ClipWinding(targetPoly, info.passPlane, false);
                if (targetPoly == null) { continue; }

                // clip to opposite of this plane, line 315
                sourceClipped = ClipWinding(sourceClipped, backplane, false);
                if (sourceClipped == null) { continue; }

                const int testlevel = 4;

                if(testlevel > 0)
                {
                    targetPoly = ClipToSeparators(sourceClipped, info.passPoly, targetPoly);
                    if (targetPoly == null) { continue; }
                }

                if(testlevel > 1)
                {
                    targetPoly = ClipToSeparators(info.passPoly, sourceClipped, targetPoly, true);
                    if (targetPoly == null) { continue; }
                }

                if(testlevel > 2)
                {
                    sourceClipped = ClipToSeparators(targetPoly, info.passPoly, sourceClipped);
                    if (sourceClipped == null) continue;
                }

                if(testlevel > 3)
                {
                    sourceClipped = ClipToSeparators(info.passPoly, targetPoly, sourceClipped, true);
                    if (sourceClipped == null) continue;
                }

                //trace.Add((sourcePoly, sourcePlane, passPoly, passPlane));
                source.Push(new(visLeaves[next], sourceClipped, info.sourcePlane, targetPoly, pPlane, t_msee, info.leaf.bspLeafID));
            }
        }
        public static void CalculateCoarsePVS()
        {
            Console.WriteLine("--- CalculateCoarsePVS ---");

            var leavesToCheck = visLeaves.Where(l => !BSPRoot.Nodes[l.bspLeafID].solid && !BSPRoot.Nodes[l.bspLeafID].split).ToArray();

            progressTarget = leavesToCheck.Length;
            progressBar = new ProgressBar();
            progress = 0;

            progressBar.Report(0);
            for(int i = 0; i < leavesToCheck.Length; i++)
            {
                var leaf = leavesToCheck[i];
                progress++;
                progressBar.Report(progress / (float)progressTarget);

                if (BSPRoot.Nodes[leaf.bspLeafID].solid) continue;
                if (BSPRoot.Nodes[leaf.bspLeafID].split) continue;

                // wtf c#
                var sourcePortals = GetTraversablePortals(leaf, true).ToArray();

                Parallel.ForEach(sourcePortals, sourcePortal =>
                {
                    bool sourceFlip = sourcePortal.LeafBack == leaf.bspLeafID;
                    int next = (sourceFlip ? sourcePortal.LeafFront : sourcePortal.LeafBack);

                    float ldot = sourcePortal.Plane.DotCoordinate(leaf.center);
                    sourceFlip = ldot >= ON_EPSILON;

                    var sourcePlane = sourceFlip ? FlipPlane(sourcePortal.Plane) : sourcePortal.Plane;

                    Stack<int> checkStack = new Stack<int>();

                    checkStack.Push(next);

                    ulong[] currentMightSee = Bitset.Create(visLeaves.Length);

                    while (checkStack.Count > 0)
                    {
                        int id = checkStack.Pop();
                        Bitset.Set(currentMightSee, id);

                        var targPortals = GetTraversablePortals(visLeaves[id], true);

                        foreach (var targetPortal in targPortals)
                        {
                            bool targetFlip = targetPortal.LeafBack == visLeaves[id].bspLeafID;
                            next = (targetFlip ? targetPortal.LeafFront : targetPortal.LeafBack);

                            if (Bitset.Get(currentMightSee, next)) continue;

                            ldot = targetPortal.Plane.DotCoordinate(visLeaves[id].center);
                            //if (ldot > -ON_EPSILON && ldot < ON_EPSILON)
                            //{
                            //    checkStack.Push(next);
                            //    continue;
                            //}

                            targetFlip = ldot > 0;

                            var targetPlane = targetFlip ? FlipPlane(targetPortal.Plane) : targetPortal.Plane;

                            //if (ClassifyWinding(targetPortal.Vertices, sourcePlane) == 2 && ClassifyWinding(sourcePortal.Vertices, targetPlane) == 2)
                            //{
                            //    checkStack.Push(next);
                            //    continue;
                            //}

                            if (ClipWinding(targetPortal.Vertices, sourcePlane, true) == null) continue;
                            if (ClipWinding(sourcePortal.Vertices, FlipPlane(targetPlane), true) == null) continue;

                            checkStack.Push(next);
                        }
                    }

                    lock (sourcePortal.mightsee)
                    {
                        Bitset.Or(sourcePortal.mightsee, currentMightSee);
                    }
                }); 
                leaf.pvs = sourcePortals.SelectMany(p => Bitset.ToList(p.mightsee)).Append((uint)leaf.bspLeafID).Distinct().ToList();
            }

            progressBar.Dispose();
        }
        public static bool PlanesNearlyEqual(Plane a, Plane b)
        {
            return Math.Abs(a.D - b.D) < ON_EPSILON && Vector3.Dot(a.Normal, b.Normal) > 1.0f - ON_EPSILON;
        }
        static bool[] wasSolid;
        public static void Finalize(Brush[] brushes)
        {
            for (int i = 0; i < visLeaves.Length; i++)
            {
                BSPRoot.Nodes[visLeaves[i].bspLeafID].solid = wasSolid[i];
            }

            //invisibleBrushFaces.Clear();

            //for(int i = 0; i < brushes.Length; i++)
            //{
            //    invisibleBrushFaces.Add(i, Enumerable.Range(0, brushes[i].faces.Length).ToList());
            //}
            //foreach(var leaf in visLeaves)
            //{
            //    foreach(var portal in leaf.portals.Where(p =>
            //    {
            //        int id = p.LeafBack == leaf.bspLeafID ? p.LeafFront : p.LeafBack;

            //        return !BSPRoot.nodes[id].split && BSPRoot.nodes[id].solid;
            //    }))
            //    {
            //        foreach(var bf in portal.brushFaces)
            //        {
            //            invisibleBrushFaces[bf.Item1].Remove(bf.Item2);
            //        }
            //    }
            //}

            //int hidden = 0;
            //foreach(var id in invisibleBrushFaces)
            //{
            //    var brush = brushes[id.Key];

            //    if (brush.isEntity) continue;
            //    if (brush.isClip) continue;

            //    foreach (var f in id.Value)
            //    {
            //        brush.faces[f].drawn = false;
            //        hidden++;
            //    }
            //}

            //Console.WriteLine($"\nCulled {hidden} faces!\n");
        }
        static Vector3 ClosestPointOnPolygonBoundary(Vector3[] verts, Vector3 point)
        {
            float bestDist = float.MaxValue;
            Vector3 best = verts[0];
            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 a = verts[i];
                Vector3 b = verts[(i + 1) % verts.Length];
                Vector3 ab = b - a;
                float t = MathHelper.Clamp(Vector3.Dot(point - a, ab) / Vector3.Dot(ab, ab), 0f, 1f);
                Vector3 candidate = a + ab * t;
                float dist = Vector3.DistanceSquared(point, candidate);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = candidate;
                }
            }
            return best;
        }
        public static bool FillOutside(EntityReference[] entities, string mapName)
        {
            Console.WriteLine("--- FillOutside ---");

            bool inside = false;
            for(int i = 0; i < entities.Length; i++)
            {
                if (entities[i].IsBrushEntity) continue;

                if (Outside.PlaceOccupant(i, entities[i].Position))
                {
                    inside = true;
                }
            }

            wasSolid = new bool[visLeaves.Length];
            for(int i = 0; i < visLeaves.Length; i++)
            {
                wasSolid[i] = BSPRoot.Nodes[visLeaves[i].bspLeafID].solid;
                BSPRoot.Nodes[visLeaves[i].bspLeafID].solid = true;
            }

            if(!inside)
            {
                Console.WriteLine("WARNING: no filling performed -- no entities in empty space.");
                return false;
            }

            if(!FillOutsideFromEntities(wasSolid, out var path, out var seedEntityIndex))
            {
                Vector3[] points = new Vector3[path.Length + 1];
                points[0] = entities[seedEntityIndex].Position;
                for (int i = 0; i < path.Length; i++)
                {
                    var center = path[i].Vertices.Aggregate((a, b) => a + b) / path[i].Vertices.Length;
                    var closest = ClosestPointOnPolygonBoundary(path[i].Vertices, points[i]);
                    var dir = center - closest; dir.Normalize();
                    points[i + 1] = (closest + dir * 0.2f);
                }

                string[] lines = points.Select(p => $"{p.X:F3} {p.Y:F3} {p.Z:F3}").ToArray();
                System.IO.File.WriteAllLines(System.IO.Path.ChangeExtension(mapName, "leak"), lines);

                // there may be a good reason for this... - bitl
                //throw new Exception("EEK! A map leak!");
                Console.WriteLine($"WARNING: EEK! A map leak! Found at {string.Join(" ", lines)}");
            }

            return true;
        }
        public static bool FillOutsideFromEntities(bool[] wasSolid, out Portal[] leakPath, out int seedEntityIndex)
        {
            leakPath = null;
            seedEntityIndex = -1;
            foreach (var seed in Outside.floodSeed)
            {
                if(!RecursiveFillOutside(visLeaves[seed], wasSolid, out leakPath))
                {
                    seedEntityIndex = Outside.bspNodeOccupants[seed][0];
                    return false;
                }
            }
            return true;
        }
        public static bool RecursiveFillOutside(TempVisLeaf start, bool[] wasSolid, out Portal[] finalLeakPath)
        {
            Queue<(TempVisLeaf leaf, Portal via)> leaves = new Queue<(TempVisLeaf, Portal)>();
            leaves.Enqueue((start, null));

            progressTarget = visLeaves.Length;
            progress = 0;
            progressBar = new ProgressBar();

            bool[] visited = new bool[visLeaves.Length];

            int[] cameFromLeaf = new int[visLeaves.Length];
            Portal[] cameFromPortal = new Portal[visLeaves.Length];
            for (int i = 0; i < cameFromLeaf.Length; i++)
            {
                cameFromLeaf[i] = -1;
            }

            Portal leakPortal = null;
            int leakFromLeaf = 0;

            finalLeakPath = new Portal[0];

            while (leaves.Count > 0)
            {
                progress++;
                progressBar.Report(progress/progressTarget);

                var (l, viaPortal) = leaves.Dequeue();

                if (visited[l.bspLeafID]) continue;
                BSPRoot.Nodes[l.bspLeafID].solid = false;
                visited[l.bspLeafID] = true;

                outleaves++;

                if (viaPortal != null)
                {
                    cameFromPortal[l.bspLeafID] = viaPortal;
                }

                var sourcePortals = l.portals.Where(p =>
                {
                    if (p.LeafFront == -1 || p.LeafBack == -1) return true;

                    bool targetFlip = p.LeafBack == l.bspLeafID;
                    int next = (targetFlip ? p.LeafFront : p.LeafBack);

                    return !(p.Brushes.Length > 0 || BSPRoot.Nodes[next].split || wasSolid[next]);
                }).ToList();

                foreach (var p in sourcePortals)
                {
                    bool s = p.LeafFront == l.bspLeafID;
                    if (p.LeafFront == -1 || p.LeafBack == -1)
                    {
                        leakPortal = p;
                        leakFromLeaf = l.bspLeafID;
                        goto buildleakpath;
                    }
                    int nextID = s ? p.LeafBack : p.LeafFront;
                    if (!visited[nextID])
                    {
                        cameFromLeaf[nextID] = l.bspLeafID;
                        leaves.Enqueue((visLeaves[nextID], p));
                    }
                }
            }

            progressBar.Dispose();

            return true;

        buildleakpath:

            progressBar.Dispose();

            List<Portal> leakPath = new List<Portal>();
            leakPath.Add(leakPortal);
            int cur = leakFromLeaf;
            while (cameFromLeaf[cur] != -1)
            {
                leakPath.Add(cameFromPortal[cur]);
                cur = cameFromLeaf[cur];
            }
            leakPath.Reverse();

            finalLeakPath = leakPath.ToArray();

            return false;
        }

        public static void MakeHeadnodePortals(BoundingBox mapBoundaries)
        {
            Console.WriteLine("--- MakeHeadnodePortals ---");

            visLeaves = new TempVisLeaf[BSPRoot.Nodes.Length];
            for (int i = 0; i < BSPRoot.Nodes.Length; i++) { visLeaves[i] = (new TempVisLeaf { bspLeafID = i, portals = [] }); }

            Vector3[] bounds = new Vector3[4];
            Portal[] boundingPortals = new Portal[6];
            Plane[] boundingPlanes = new Plane[6];

            const float SideSpace = 64f;

            bounds[0] = mapBoundaries.Min - Vector3.One * SideSpace;
            bounds[1] = mapBoundaries.Max + Vector3.One * SideSpace;
            bounds[2] = mapBoundaries.Min;
            bounds[3] = mapBoundaries.Max;

            outsideNode = new TempVisLeaf() { IsEmpty = true, portals = new (), bspLeafID = -1 };

            for (int i = 0; i < 3; i++)
            {
                for(int j = 0; j < 2; j++)
                {
                    int num = j * 3 + i;
                    boundingPortals[num] = new Portal();

                    Vector3 normal = new Vector3();
                    float d;
                    if (j != 0)
                    {
                        normal = normal.Set(i, -1);
                    }
                    else
                    {
                        normal = normal.Set(i, 1);
                    }
                    Vector3 knifePoint = bounds[j];
                    d = -Vector3.Dot(normal, knifePoint);
                    Plane plane = new Plane(normal, d);

                    boundingPlanes[num] = plane;
                    boundingPortals[num].Plane = plane;
                    boundingPortals[num].Vertices = BaseWindingForPlane(plane).ToArray();

                    boundingPortals[num] = AddPortalToNodes(boundingPortals[num], visLeaves[0], outsideNode);
                }
            }

            for (int i = 0; i < 6; i++)
            {
                var p = boundingPortals[i];
                if (p == null) continue;
                var verts = p.Vertices;
                if (verts == null || verts.Length == 0)
                {
                    boundingPortals[i] = null;
                    continue;
                }

                for (int j = 0; j < 6; j++)
                {
                    if (i == j) continue;
                    verts = ClipWinding(verts, boundingPlanes[j], true);
                    if (verts == null || verts.Length < 3)
                    {
                        boundingPortals[i] = null;
                        break;
                    }
                }

                if (boundingPortals[i] != null)
                    boundingPortals[i].Vertices = verts;
            }

            foreach (var bp in boundingPortals)
            {
                if (bp != null) portals.Add(bp);
            }
        }
        public static void MergePortals()
        {
            Console.WriteLine("--- MergePortals ---");

            portals?.Clear();

            foreach (var leaf in visLeaves)
            {
                if (BSPRoot.Nodes[leaf.bspLeafID].solid)
                {
                    leaf.portals.Clear();
                    continue;
                }

                portals.AddRange(leaf.portals.Where(portal =>
                {
                    if (portal.LeafFront == -1 || portal.LeafBack == -1) return true;

                    var leafA = visLeaves[portal.LeafFront];
                    var leafB = visLeaves[portal.LeafBack];

                    return !BSPRoot.Nodes[leafA.bspLeafID].split && !BSPRoot.Nodes[leafB.bspLeafID].split;
                }));
            }

            portals = portals.Distinct(new PortalComparer()).ToList();
            portals.RemoveAll(p =>
            {
                if (p.LeafFront == -1 || p.LeafBack == -1) return false;
                var leafA = visLeaves[p.LeafFront];
                var leafB = visLeaves[p.LeafBack];
                return BSPRoot.Nodes[leafA.bspLeafID].solid && BSPRoot.Nodes[leafB.bspLeafID].solid;
            });
            // This seems a bit hacky, but it DOES remove all of the weird leftovers, and nothing looks to have been removed where it shouldnt. So it stays.
            portals.RemoveAll(p =>
            {
                if (MathF.Abs(SignedWindingArea(p.Vertices, p.Plane)) >= BIGNUMBER * BIGNUMBER) return true;
                return false;
            });

            foreach (var leaf in visLeaves)
                leaf.portals.Clear();
            outsideNode.portals.Clear();
            var newPortalIndexMap = new Dictionary<Portal, int>();

            for (int i = 0; i < portals.Count; i++)
            {
                var p = portals[i];
                newPortalIndexMap[p] = i;

                if (p.LeafFront != -1) visLeaves[p.LeafFront].portals.Add(p);
                else outsideNode.portals.Add(p);
                if (p.LeafBack != -1) visLeaves[p.LeafBack].portals.Add(p);
                else outsideNode.portals.Add(p);
            }
            portalIndexMap = newPortalIndexMap;
        }
        public static void MarkBrushesOnPortals(Brush[] brushes, BoundingBox[] brushBounds)
        {
            Console.WriteLine("--- MarkBrushesOnPortals ---");

            var brushToPortals = new List<Portal>[brushes.Length];

            for (int i = 0; i < brushes.Length; i++)
            {
                brushToPortals[i] = new List<Portal>();
            }

            for (int i = portals.Count - 1; i >= 0; i--)
            {
                var portal = portals[i];
                // compute portal bounding-box once:
                var pb = BoundingBox.CreateFromPoints(portal.Vertices);
                pb.Min -= Vector3.One * 0.1f;
                pb.Max += Vector3.One * 0.1f;
                for (int bi = 0; bi < brushes.Length; bi++)
                {
                    if (pb.Intersects(brushBounds[bi]) == true)
                        brushToPortals[bi].Add(portal);
                }
            }

            Console.WriteLine($"Total portals: {portals.Count}");

            progressBar = new ProgressBar();
            progressTarget = brushes.Length;
            progress = 0;

            for (int b = 0; b < brushes.Length; b++)
            {
                progress++;
                progressBar.Report(progress / progressTarget);

                var brush = brushes[b];
                var bounds = brushBounds[b];

                if (brush.IsEntity) continue;
                if (brush.IsClip) continue;
                if (brush.IsTrigger) continue;
                if (brush.IsLightNodeVolume) continue;

                foreach (var portal in brushToPortals[b])
                {
                    for (int f = 0; f < brush.Faces.Length; f++)
                    {
                        var face = brush.Faces[f];
                        bool skip = false;
                        foreach (var i in face.Indices)
                        {
                            var vertex = brush.Vertices[i] + brush.Position;

                            const float FACE_MATCH_EPSILON = 0.05f;
                            float d = portal.Plane.DotCoordinate(vertex);
                            if (d > FACE_MATCH_EPSILON || d < -FACE_MATCH_EPSILON) skip = true;
                        }

                        if (skip) continue;

                        // Now we have to check if the portal polygon contains the face's polygon.
                        Vector3 normal = portal.Plane.Normal;
                        float v = 0;
                        float max = -2048;
                        int x = -1;
                        for (int i = 0; i < 3; i++)
                        {
                            v = float.Abs(normal.Get(i));

                            if (v > max)
                            {
                                x = i;
                                max = v;
                            }
                        }
                        Vector3 vup = Vector3.Zero;
                        switch (x)
                        {
                            case 0:
                            case 1:
                                vup = vup.Set(2, 1);
                                break;
                            case 2:
                                vup = vup.Set(1, 1);
                                break;
                        }

                        var dot = Vector3.Dot(vup, normal);
                        Vector3 vupProj = vup - normal * dot;
                        vupProj.Normalize();

                        Vector3 vright = Vector3.Cross(vupProj, normal);
                        vright.Normalize();

                        Vector2 To2D(Vector3 p) => new Vector2(Vector3.Dot(p, vupProj), Vector3.Dot(p, vright));

                        var faceVerts3D = face.Indices
                            .Select(i => brush.Vertices[i] + brush.Position)
                            .ToArray();
                        var face2D = faceVerts3D.Select(To2D).ToArray();
                        var portal2D = portal.Vertices.Select(To2D).ToArray();

                        if (!ConvexPolygonsOverlap(face2D, portal2D, -0.01f)) continue;

                        if (!portal.Brushes.ToList().Contains((ushort)b))
                        {
                            portal.Brushes = portal.Brushes.Append((ushort)b).ToArray();
                        }
                        if (!portal.brushFaces.Contains(((ushort)b, (ushort)f))) portal.brushFaces.Add(((ushort)b, (ushort)f));
                    }
                }
            }
            progressBar.Dispose();
        }
        static bool ConvexPolygonsOverlap(Vector2[] A, Vector2[] B, float expansion = 0f)
        {
            // check all edges of A then all edges of B
            return !HasSeparatingAxis(A, B, expansion)
                && !HasSeparatingAxis(B, A, expansion);
        }
        static bool HasSeparatingAxis(Vector2[] poly1, Vector2[] poly2, float expansion)
        {
            int n = poly1.Length;
            for (int i = 0; i < n; i++)
            {
                var p1 = poly1[i];
                var p2 = poly1[(i + 1) % n];
                // edge
                Vector2 edge = p2 - p1;
                // get a perpendicular axis (not normalized)
                Vector2 axis = new Vector2(-edge.Y, edge.X);
                // normalize and apply expansion
                float len = axis.Length();
                if (len < 1e-6f) continue;
                axis /= len;
                // project both polys onto this axis
                Project(poly1, axis, out float minA, out float maxA);
                Project(poly2, axis, out float minB, out float maxB);
                // expand A’s interval by 'expansion' each side
                minA -= expansion;
                maxA += expansion;
                // if intervals [minA,maxA] and [minB,maxB] do not overlap, we found a separating axis
                if (maxA < minB || maxB < minA)
                    return true;
            }
            return false;
        }
        static void Project(Vector2[] poly, Vector2 axis, out float min, out float max)
        {
            min = max = Vector2.Dot(poly[0], axis);
            for (int i = 1; i < poly.Length; i++)
            {
                float d = Vector2.Dot(poly[i], axis);
                if (d < min) min = d;
                else if (d > max) max = d;
            }
        }
        public static void CutNodePortals()
        {
            Console.WriteLine("--- CutNodePortals ---");
            progressBar = new ProgressBar();
            progressTarget = BSPRoot.Nodes.Length;
            CutNodePortals_r(0);
            progressBar.Dispose();
        }
        static void CutNodePortals_r(int node, int parallel = 0)
        {
            progress++;
            progressBar.Report(progress / progressTarget);

            var visNode = visLeaves[node];
            var bspNode = BSPRoot.Nodes[node];

            // If this BSP node is a leaf (no split) -> nothing to do
            if (!bspNode.split)
                return;

            Plane plane = bspNode.SplittingPlane;
            FindPlane(ref plane, out _);
            uint f = bspNode.front;
            uint b = bspNode.back;

            // Build a new portal for the splitting plane (base winding clipped by all node portals)
            var splitPortal = new Portal();
            splitPortal.Plane = plane;
            var w = BaseWindingForPlane(splitPortal.Plane).ToArray();

            foreach (var raw in visNode.portals)
            {
                Plane clip = raw.Plane;
                int side;

                if (raw.LeafFront == node)
                {
                    side = 0;
                }
                else if (raw.LeafBack == node)
                {
                    side = 1;
                    clip = FlipPlane(clip); // flip clipping plane if portal is reversed relative to this node
                }
                else
                {
                    throw new Exception("CutNodePortals_r: mislinked portal while clipping base winding");
                }

                w = ClipWinding(w, clip, true);
                if (w == null || w.Length < 3)
                {
                    // completely clipped away
                    w = null;
                    //Console.WriteLine("WARNING: CutNodePortals_r: new portal was completely clipped away");
                    break;
                }
            }

            if (w != null && w.Length >= 3)
            {
                splitPortal.Vertices = w;
                // Add the new "splitting" portal between the two child nodes (use visLeaves for children)
                AddPortalToNodes(splitPortal, visLeaves[(int)f], visLeaves[(int)b]);
            }

            // Partition all portals currently listed on this node into the two children.
            // Use a snapshot because we'll be removing/adding portals as we go.
            var portalsToProcess = visNode.portals.ToArray();
            foreach (var p in portalsToProcess)
            {
                int side;
                if (p.LeafFront == node) side = 0;
                else if (p.LeafBack == node) side = 1;
                else throw new Exception("CutNodePortals_r: mislinked portal during partition");

                int otherNodeIndex = (side != 0) ? p.LeafFront : p.LeafBack;

                // If it's referencing the outside node (-1) we must remove it from outsideNode.
                if (p.LeafFront != -1) RemovePortalFromNode(p, visLeaves[p.LeafFront]);
                else RemovePortalFromNode(p, outsideNode);

                if (p.LeafBack != -1) RemovePortalFromNode(p, visLeaves[p.LeafBack]);
                else RemovePortalFromNode(p, outsideNode);

                // Prepare the other-side TempVisLeaf (either an index into visLeaves or the outsideNode)
                TempVisLeaf otherLeaf = otherNodeIndex == -1 ? outsideNode : visLeaves[otherNodeIndex];

                // Split the portal winding by this node's splitting plane
                var (frontWinding, backWinding) = DivideWinding(p.Vertices, plane);

                // Cases:
                // - If front side is empty => portal belongs entirely to back child.
                // - If back side is empty  => portal belongs entirely to front child.
                // - Otherwise, split into two portals, one for each child.

                // Helper: pick child's TempVisLeaf from f/b child indices
                TempVisLeaf childF = visLeaves[(int)f];
                TempVisLeaf childB = visLeaves[(int)b];

                childF.HasSkybox |= BSPRoot.Nodes[(int)f].nodeFlag == BSPNode.SkyboxNode;
                childB.HasSkybox |= BSPRoot.Nodes[(int)b].nodeFlag == BSPNode.SkyboxNode;

                if (frontWinding == null || frontWinding.Length < 3)
                {
                    // entirely on back side
                    if (side == 0)
                    {
                        // original portal had this node as LeafFront => now belongs to back child and other
                        AddPortalToNodes(p, childB, otherLeaf);
                    }
                    else
                    {
                        AddPortalToNodes(p, otherLeaf, childB);
                    }
                    continue;
                }

                if (backWinding == null || backWinding.Length < 3)
                {
                    // entirely on front side
                    if (side == 0)
                    {
                        AddPortalToNodes(p, childF, otherLeaf);
                    }
                    else
                    {
                        AddPortalToNodes(p, otherLeaf, childF);
                    }
                    continue;
                }

                // The winding is split
                Portal newPortal = Clone(p);
                newPortal.Vertices = backWinding;
                p.Vertices = frontWinding;

                if (side == 0)
                {
                    AddPortalToNodes(p, childF, otherLeaf);
                    AddPortalToNodes(newPortal, childB, otherLeaf);
                }
                else
                {
                    AddPortalToNodes(p, otherLeaf, childF);
                    AddPortalToNodes(newPortal, otherLeaf, childB);
                }
            }

            // Recurse into children
            CutNodePortals_r((int)f);
            CutNodePortals_r((int)b);
        }

        static List<Vector3> BaseWindingForPlane(Plane plane)
        {
            float v = 0;
            float max = -BIGNUMBER;
            int x = -1;
            Vector3 normal = plane.Normal;
            for (int i = 0; i < 3; i++)
            {
                v = float.Abs(normal.Get(i));

                if(v > max)
                {
                    x = i;
                    max = v;
                }
            }
            Vector3 vup = Vector3.Zero;
            switch(x)
            {
                case 0:
                case 1:
                    vup = vup.Set(2,1);
                    break;
                case 2:
                    vup = vup.Set(1, 1);
                    break;
            }

            var dot = Vector3.Dot(vup, normal);
            Vector3 vupProj = vup - normal * dot;
            vupProj.Normalize();

            Vector3 vright = Vector3.Cross(vupProj, normal);
            vright.Normalize();

            vupProj *= BIGNUMBER;
            vright *= BIGNUMBER;

            Vector3 origin = normal * -plane.D;

            return new List<Vector3>
            {
                origin - vupProj + vright,
                origin + vupProj + vright,
                origin + vupProj - vright,
                origin - vupProj - vright
            };
        }

        const float BIGNUMBER = 4096f;
        const float ON_EPSILON = 0.001953125f;

        const int MaxPointsOnWinding = 256;
        public static Vector3[] ClipToSeparators(Vector3[] firstPoly, Vector3[] secondPoly, Vector3[] clippedPoly, bool otherSide = false)
        {
            var target = clippedPoly;
            int[] counts = new int[3];

            for(int i = 0; i < firstPoly.Length; i++)
            {
                int l = (i + 1) % firstPoly.Length;
                var v1 = firstPoly[l] - firstPoly[i];

                for(int j = 0; j < secondPoly.Length; j++)
                {
                    var v2 = secondPoly[j] - firstPoly[i];

                    float pd = 0;
                    Vector3 normal = Vector3.Zero;
                    normal.X = (v1.Get(1) * v2.Get(2) - v1.Get(2) * v2.Get(1));
                    normal.Y = (v1.Get(2) * v2.Get(0) - v1.Get(0) * v2.Get(2));
                    normal.Z = (v1.Get(0) * v2.Get(1) - v1.Get(1) * v2.Get(0));

                    var length = normal.Length();
                    if (length < ON_EPSILON) { continue; }

                    normal.Normalize();

                    pd = -Vector3.Dot(secondPoly[j],normal);

                    Plane plane = new Plane(normal, pd);

                    bool fliptest = false;
                    int k = 0;
                    for(k = 0; k < firstPoly.Length; k++)
                    {
                        if (k == i || k == l)
                            continue;

                        float d = plane.DotCoordinate(firstPoly[k]);

                        if(d < -ON_EPSILON)
                        {
                            fliptest = false;
                            break;
                        }
                        else if (d > ON_EPSILON)
                        {
                            fliptest = true;
                            break;
                        }
                    }
                    // Portals are coplanar, skip
                    if (k == firstPoly.Length) continue;

                    if (fliptest) plane = FlipPlane(plane);

                    counts[0] = counts[1] = counts[2] = 0;
                    for(k = 0; k < secondPoly.Length; k++)
                    {
                        if (k == j) continue;

                        var d = plane.DotCoordinate(secondPoly[k]);

                        if (d < -ON_EPSILON)
                            break;
                        else if (d > ON_EPSILON)
                            counts[0]++;
                        else
                            counts[2]++;
                    }
                    if (k != secondPoly.Length)
                        continue;

                    if (counts[0] == 0)
                        continue;

                    if (otherSide) plane = FlipPlane(plane);

                    target = ClipWinding(target, plane, false);

                    if (target == null || target.Length == 0) return null;
                }
            }

            return target;
        }
        public static bool WindingOnFace(Vector3[] winding, Plane split)
        {
            foreach (var v in winding)
            {
                float d = split.DotCoordinate(v);
                if (Math.Abs(d) > ON_EPSILON)
                    return false;
            }
            return true;
        }
        public static int ClassifyWinding(Vector3[] winding, Plane split, float eps = ON_EPSILON)
        {
            int[] counts = new int[3];
            int[] sides = new int[MaxPointsOnWinding];
            float[] dists = new float[MaxPointsOnWinding];
            int i;
            for (i = 0; i < winding.Length; i++)
            {
                float dot = split.DotCoordinate(winding[i]);
                dists[i] = dot;
                if (dot >= eps)
                {
                    sides[i] = 0;
                }
                else if (dot < -eps)
                {
                    sides[i] = 1;
                }
                else
                {
                    sides[i] = 2;
                }
                counts[sides[i]]++;
            }

            sides[i] = sides[0];
            dists[i] = dists[0];

            if (counts[0] == 0 && counts[1] == 0)
            {
                return 2;
            }

            if (counts[0] == 0)
            {
                return 1;
            }
            if (counts[1] == 0)
                return 0;

            return 2;
        }
        public static Vector3[] ClipWinding(Vector3[] winding, Plane split, bool keepon)
        {
            if (winding == null || winding.Length == 0) return null;
            int n = winding.Length;

            Span<int> sides = stackalloc int[MaxPointsOnWinding + 1];
            Span<float> dists = stackalloc float[MaxPointsOnWinding + 1];
            Span<int> counts = stackalloc int[3];

            for (int i = 0; i < n; i++)
            {
                float dot = split.DotCoordinate(winding[i]);
                dists[i] = dot;
                sides[i] = dot >= ON_EPSILON ? 0 : dot < -ON_EPSILON ? 1 : 2;
                counts[sides[i]]++;
            }
            sides[n] = sides[0];
            dists[n] = dists[0];

            if (keepon && counts[0] == 0 && counts[1] == 0) return winding;
            if (counts[0] == 0) return null;
            if (counts[1] == 0) return winding;

            Span<Vector3> buf = stackalloc Vector3[MaxPointsOnWinding];
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                Vector3 p1 = winding[i];
                if (sides[i] == 2 || sides[i] == 0) buf[count++] = p1;
                if (sides[i + 1] == 2 || sides[i + 1] == sides[i]) continue;

                Vector3 p2 = winding[(i + 1) % n];
                float denom = dists[i] - dists[i + 1];
                float t = Math.Abs(denom) < 1e-6f ? 0.5f : Math.Clamp(dists[i] / denom, 0f, 1f);
                buf[count++] = Vector3.Lerp(p1, p2, t);
            }
            return buf[..count].ToArray();
        }
        public static (Vector3[] front, Vector3[] back) DivideWinding(Vector3[] winding,Plane split)
        {
            if (winding == null || winding.Length == 0) return (null, null);

            int n = winding.Length;
            int[] sides = new int[MaxPointsOnWinding]; // 0=front,1=back,2=on
            float[] dists = new float[MaxPointsOnWinding];
            int[] counts = new int[3];

            for (int i = 0; i < n; i++)
            {
                float dot = split.DotCoordinate(winding[i]);
                dists[i] = dot;
                if (dot > ON_EPSILON) sides[i] = 0;
                else if (dot < -ON_EPSILON) sides[i] = 1;
                else sides[i] = 2;
                counts[sides[i]]++;
            }

            sides[n] = sides[0];
            dists[n] = dists[0];

            // If everything on one side, return that side as the original winding
            if (counts[0] == 0)
            {
                // no front points -> all back (or on)
                return (null, winding);
            }
            if (counts[1] == 0)
            {
                // no back points -> all front (or on)
                return (winding, null);
            }

            var f = new List<Vector3>(n + 4);
            var b = new List<Vector3>(n + 4);

            for (int i = 0; i < n; i++)
            {
                Vector3 p1 = winding[i];

                if (sides[i] == 2) // ON
                {
                    f.Add(p1);
                    b.Add(p1);
                }
                else if (sides[i] == 0) // FRONT
                {
                    f.Add(p1);
                }
                else // BACK
                {
                    b.Add(p1);
                }

                if (sides[i + 1] == 2 || sides[i + 1] == sides[i]) continue;

                Vector3 p2 = winding[(i + 1) % n];

                float denom = dists[i] - dists[i + 1];
                float t;
                if (Math.Abs(denom) < 1e-6f)
                {
                    t = 0.5f;
                }
                else
                {
                    t = dists[i] / denom;
                    if (t < 0f) t = 0f;
                    else if (t > 1f) t = 1f;
                }

                Vector3 mid = Vector3.Lerp(p1, p2, t);
                f.Add(mid);
                b.Add(mid);
            }

            Vector3[] frontArr = f.Count == 0 ? null : f.ToArray();
            Vector3[] backArr = b.Count == 0 ? null : b.ToArray();

            return (frontArr, backArr);
        }
        public static void NormalizePlane(ref Plane plane, out int pType)
        {
            float ax, ay, az;
            const int P_X = 0;
            const int P_Y = 1;
            const int P_Z = 2;
            const int P_ANYX = 3;
            const int P_ANYY = 4;
            const int P_ANYZ = 5;

            pType = 0;

            if (plane.Normal.X == -1)
            {
                plane.Normal.X = 1;
                plane.D = -plane.D;
            }
            if (plane.Normal.Y == -1)
            {
                plane.Normal.Y = 1;
                plane.D = -plane.D;
            }
            if (plane.Normal.Z == -1)
            {
                plane.Normal.Z = 1;
                plane.D = -plane.D;
            }

            if (plane.Normal.X == 1)
            {
                pType = P_X;
                return;
            }
            if (plane.Normal.Y == 1)
            {
                pType = P_Y;
                return;
            }
            if (plane.Normal.Z == 1)
            {
                pType = P_Z;
                return;
            }

            ax = float.Abs(plane.Normal.X);
            ay = float.Abs(plane.Normal.Y);
            az = float.Abs(plane.Normal.Z);

            if (ax >= ay && ax >= az)
                pType = P_ANYX;
            else if (ay >= ax && ay >= az)
                pType = P_ANYY;
            else
                pType = P_ANYZ;

            if (plane.Normal.Get(pType - P_ANYX) < 0)
            {
                plane.Normal = -plane.Normal;
                plane.D = -plane.D;
            }
        }

        public static int FindPlane(ref Plane plane, out int side)
        {
            Plane pl = plane;
            NormalizePlane(ref pl, out _);

            if (Vector3.Dot(pl.Normal, plane.Normal) > 0)
                side = 0;
            else
                side = 1;

            for (int i = 0; i < planes.Count; i++)
            {
                if (PlanesNearlyEqual(planes[i],plane))
                {
                    plane = planes[i];
                    return i;
                }
            }

            planes.Add(plane);
            return planes.Count - 1;
        }
    }

    public static class Vector3Extensions
    {
        public static float Get(this Vector3 v, int axis)
        {
            switch (axis)
            {
                case 0:
                    return v.X;
                case 1:
                    return v.Y;
                case 2:
                    return v.Z;
            }
            throw new NotImplementedException();
        }
        public static Vector3 Set(this Vector3 v, int axis, float val)
        {
            Vector3 newV = v;
            switch (axis)
            {
                case 0:
                    newV.X = val;
                    break;
                case 1:
                    newV.Y = val;
                    break;
                case 2:
                    newV.Z = val;
                    break;
            }

            return newV;
        }
    }
}