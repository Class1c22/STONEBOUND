using System.Collections.Generic;
using UnityEngine;

// Шматок світу 16x16x16 вокселів. Один чанк = один mesh + один колайдер.
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
public class VoxelChunk : MonoBehaviour
{
    const int S = VoxelWorld.ChunkSize;

    VoxelWorld world;
    Vector3Int coord;
    readonly byte[] voxels = new byte[S * S * S];   // 0 = повітря, 1..255 = індекс кольору
    Mesh mesh;
    MeshCollider meshCollider;

    // порядок: +X, -X, +Y, -Y, +Z, -Z
    public static readonly Vector3Int[] Dirs =
    {
        new Vector3Int(1, 0, 0), new Vector3Int(-1, 0, 0),
        new Vector3Int(0, 1, 0), new Vector3Int(0, -1, 0),
        new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1),
    };

    // 4 вершини кожної грані (порядок підібраний так, щоб грань дивилась назовні)
    public static readonly Vector3[,] FaceVerts =
    {
        { new Vector3(1,0,0), new Vector3(1,1,0), new Vector3(1,1,1), new Vector3(1,0,1) },
        { new Vector3(0,0,1), new Vector3(0,1,1), new Vector3(0,1,0), new Vector3(0,0,0) },
        { new Vector3(0,1,0), new Vector3(0,1,1), new Vector3(1,1,1), new Vector3(1,1,0) },
        { new Vector3(0,0,1), new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(1,0,1) },
        { new Vector3(1,0,1), new Vector3(1,1,1), new Vector3(0,1,1), new Vector3(0,0,1) },
        { new Vector3(0,0,0), new Vector3(0,1,0), new Vector3(1,1,0), new Vector3(1,0,0) },
    };

    static readonly List<Vector3> verts = new List<Vector3>();
    static readonly List<Vector3> normals = new List<Vector3>();
    static readonly List<Vector2> uvs = new List<Vector2>();
    static readonly List<int> tris = new List<int>();

    public void Init(VoxelWorld w, Vector3Int c)
    {
        world = w;
        coord = c;
        mesh = new Mesh { name = "VoxelChunkMesh", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.MarkDynamic();
        GetComponent<MeshFilter>().sharedMesh = mesh;
        GetComponent<MeshRenderer>().sharedMaterial = world.RuntimeMaterial;
        meshCollider = GetComponent<MeshCollider>();
    }

    public byte Get(int x, int y, int z) => voxels[x + y * S + z * S * S];
    public void Set(int x, int y, int z, byte id) => voxels[x + y * S + z * S * S] = id;

    public void Rebuild()
    {
        verts.Clear(); normals.Clear(); uvs.Clear(); tris.Clear();

        Vector3Int baseG = coord * S;
        float vs = world.voxelSize;

        for (int z = 0; z < S; z++)
        for (int y = 0; y < S; y++)
        for (int x = 0; x < S; x++)
        {
            byte id = voxels[x + y * S + z * S * S];
            if (id == 0) continue;

            Vector2 uv = new Vector2((id + 0.5f) / 256f, 0.5f);   // колір береться з палітри-текстури

            for (int f = 0; f < 6; f++)
            {
                Vector3Int d = Dirs[f];
                int nx = x + d.x, ny = y + d.y, nz = z + d.z;

                byte neighbor;
                if (nx >= 0 && nx < S && ny >= 0 && ny < S && nz >= 0 && nz < S)
                    neighbor = voxels[nx + ny * S + nz * S * S];
                else
                    neighbor = world.GetVoxel(baseG + new Vector3Int(nx, ny, nz));

                if (neighbor != 0) continue;   // грань закрита сусідом, пропускаємо

                int start = verts.Count;
                for (int k = 0; k < 4; k++)
                {
                    verts.Add((new Vector3(x, y, z) + FaceVerts[f, k]) * vs);
                    normals.Add(new Vector3(d.x, d.y, d.z));
                    uvs.Add(uv);
                }
                tris.Add(start); tris.Add(start + 1); tris.Add(start + 2);
                tris.Add(start); tris.Add(start + 2); tris.Add(start + 3);
            }
        }

        mesh.Clear();
        if (verts.Count == 0)
        {
            meshCollider.sharedMesh = null;
            return;
        }

        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();

        meshCollider.sharedMesh = null;      // примусове оновлення колайдера
        meshCollider.sharedMesh = mesh;
    }
}