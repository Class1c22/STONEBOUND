using System.Collections.Generic;
using UnityEngine;

// Відламаний шматок конструкції, що падає. Сам складається з вокселів,
// тому при сильному ударі (об землю, об інший шматок, об камінь) ламається далі.
public class VoxelPiece : MonoBehaviour
{
    VoxelWorld world;
    Dictionary<Vector3Int, byte> voxels;   // координати вокселів у сітці шматка
    Vector3 pivot;                          // центр шматка у координатах вокселів
    Rigidbody rb;
    MeshCollider meshCollider;
    Mesh mesh;
    float immuneUntil;

    readonly List<VoxelWorld.RemovedVoxel> removed = new List<VoxelWorld.RemovedVoxel>();

    public static VoxelPiece Create(VoxelWorld world, Dictionary<Vector3Int, byte> voxels, Vector3 pivot,
                                    Vector3 worldPos, Quaternion rot, Vector3 velocity, Vector3 angularVelocity)
    {
        var go = new GameObject("FallingPiece");
        go.transform.SetPositionAndRotation(worldPos, rot);

        var p = go.AddComponent<VoxelPiece>();
        p.world = world;
        p.voxels = voxels;
        p.pivot = pivot;

        go.AddComponent<MeshRenderer>().sharedMaterial = world.RuntimeMaterial;
        var meshFilter = go.AddComponent<MeshFilter>();
        p.meshCollider = go.AddComponent<MeshCollider>();
        p.meshCollider.convex = true;
        p.rb = go.AddComponent<Rigidbody>();
        p.rb.interpolation = RigidbodyInterpolation.Interpolate;

        p.mesh = new Mesh { name = "VoxelPieceMesh", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        meshFilter.sharedMesh = p.mesh;
        p.Rebuild();

        p.rb.linearVelocity = velocity;
        p.rb.angularVelocity = angularVelocity;
        p.immuneUntil = Time.time + 0.3f;   // щойно створений шматок кілька десятих секунди не ламається

        world.RegisterPiece(go);
        return p;
    }

    void Rebuild()
    {
        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();
        float vs = world.voxelSize;

        foreach (var kv in voxels)
        {
            Vector3Int g = kv.Key;
            Vector2 uv = new Vector2((kv.Value + 0.5f) / 256f, 0.5f);

            for (int f = 0; f < 6; f++)
            {
                Vector3Int d = VoxelChunk.Dirs[f];
                if (voxels.ContainsKey(g + d)) continue;

                int start = verts.Count;
                for (int k = 0; k < 4; k++)
                {
                    Vector3 corner = new Vector3(g.x, g.y, g.z) + VoxelChunk.FaceVerts[f, k] - pivot;
                    verts.Add(corner * vs);
                    normals.Add(new Vector3(d.x, d.y, d.z));
                    uvs.Add(uv);
                }
                tris.Add(start); tris.Add(start + 1); tris.Add(start + 2);
                tris.Add(start); tris.Add(start + 2); tris.Add(start + 3);
            }
        }

        mesh.Clear();
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();

        meshCollider.sharedMesh = null;
        meshCollider.sharedMesh = mesh;

        rb.mass = Mathf.Clamp(voxels.Count * 0.01f, 0.5f, 200f);
    }

    void OnCollisionEnter(Collision c)
    {
        if (voxels == null || Time.time < immuneUntil) return;
        if (c.rigidbody != null && c.rigidbody.mass < 1f) return;   // дрібні уламки шматки не ламають

        ContactPoint cp = c.GetContact(0);

        // тільки швидкість удару по нормалі: ковзання чи легке торкання шматок не ламає
        float impact = Mathf.Abs(Vector3.Dot(c.relativeVelocity, cp.normal));
        if (impact < world.pieceBreakSpeed) return;
        immuneUntil = Time.time + 0.1f;

        float radius = Mathf.Min(world.pieceBreakMaxRadius,
                                 world.pieceBreakBaseRadius + impact * world.pieceBreakRadiusPerSpeed);

        // шматок, що падає на воксельний світ, трохи ламає і його
        if (world.pieceWorldDamage > 0f && c.collider.GetComponentInParent<VoxelWorld>() == world)
        {
            float wr = radius * world.pieceWorldDamage;
            Vector3 wc = cp.point - cp.normal * (wr * 0.5f);
            var worldRemoved = new List<VoxelWorld.RemovedVoxel>();
            if (world.DestroySphere(wc, wr, worldRemoved) > 0)
                world.SpawnDebris(worldRemoved, wc, rb.linearVelocity);
        }

        // і сам шматок ламається в місці удару
        Vector3 center = cp.point + cp.normal * (radius * 0.4f);
        BreakAt(center, radius);
    }

    void BreakAt(Vector3 worldCenter, float worldRadius)
    {
        float vs = world.voxelSize;
        Vector3 c = transform.InverseTransformPoint(worldCenter) / vs + pivot;
        float r = worldRadius / vs;
        float r2 = r * r;

        Vector3Int min = new Vector3Int(Mathf.FloorToInt(c.x - r), Mathf.FloorToInt(c.y - r), Mathf.FloorToInt(c.z - r));
        Vector3Int max = new Vector3Int(Mathf.FloorToInt(c.x + r), Mathf.FloorToInt(c.y + r), Mathf.FloorToInt(c.z + r));

        removed.Clear();
        for (int z = min.z; z <= max.z; z++)
        for (int y = min.y; y <= max.y; y++)
        for (int x = min.x; x <= max.x; x++)
        {
            Vector3Int g = new Vector3Int(x, y, z);
            if (!voxels.TryGetValue(g, out byte id)) continue;

            Vector3 vc = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
            if ((vc - c).sqrMagnitude > r2) continue;

            voxels.Remove(g);
            removed.Add(new VoxelWorld.RemovedVoxel { position = WorldPos(vc), id = id });
        }
        if (removed.Count == 0) return;

        world.SpawnDebris(removed, worldCenter, rb.linearVelocity);

        if (voxels.Count == 0)
        {
            Destroy(gameObject);
            return;
        }
        SplitOrRebuild();
    }

    Vector3 WorldPos(Vector3 voxelCoord) => transform.TransformPoint((voxelCoord - pivot) * world.voxelSize);

    void SplitOrRebuild()
    {
        // шукаємо окремі зв'язні групи вокселів
        var islands = new List<HashSet<Vector3Int>>();
        var seen = new HashSet<Vector3Int>();
        var queue = new Queue<Vector3Int>();
        foreach (var start in voxels.Keys)
        {
            if (!seen.Add(start)) continue;
            var island = new HashSet<Vector3Int> { start };
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                Vector3Int p = queue.Dequeue();
                for (int j = 0; j < 6; j++)
                {
                    Vector3Int m = p + VoxelChunk.Dirs[j];
                    if (voxels.ContainsKey(m) && seen.Add(m))
                    {
                        island.Add(m);
                        queue.Enqueue(m);
                    }
                }
            }
            islands.Add(island);
        }

        // зовсім дрібні групи просто розсипаються на уламки
        var keep = new List<HashSet<Vector3Int>>();
        removed.Clear();
        foreach (var island in islands)
        {
            if (island.Count >= 4) { keep.Add(island); continue; }
            foreach (var g in island)
            {
                removed.Add(new VoxelWorld.RemovedVoxel
                {
                    position = WorldPos(new Vector3(g.x + 0.5f, g.y + 0.5f, g.z + 0.5f)),
                    id = voxels[g]
                });
                voxels.Remove(g);
            }
        }
        if (removed.Count > 0) world.SpawnDebris(removed, transform.position, rb.linearVelocity);

        if (keep.Count == 0)
        {
            Destroy(gameObject);
            return;
        }
        if (keep.Count == 1)
        {
            Rebuild();
            return;
        }

        // шматок розпався на кілька: кожна група стає новим шматком
        float vs = world.voxelSize;
        Vector3 angular = rb.angularVelocity;
        foreach (var island in keep)
        {
            var dict = new Dictionary<Vector3Int, byte>(island.Count);
            Vector3 sum = Vector3.zero;
            foreach (var g in island)
            {
                dict[g] = voxels[g];
                sum += new Vector3(g.x + 0.5f, g.y + 0.5f, g.z + 0.5f);
            }
            Vector3 newPivot = sum / island.Count;
            Vector3 newPos = transform.TransformPoint((newPivot - pivot) * vs);
            Create(world, dict, newPivot, newPos, transform.rotation, rb.GetPointVelocity(newPos), angular);
        }
        Destroy(gameObject);
    }
}