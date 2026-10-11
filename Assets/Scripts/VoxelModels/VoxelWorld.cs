using System.Collections.Generic;
using UnityEngine;

// Зберігає всі чанки, палітру кольорів і відповідає за руйнування.
// Став на порожній об'єкт у позиції (0,0,0) з масштабом (1,1,1).
public class VoxelWorld : MonoBehaviour
{
    public const int ChunkSize = 16;

    [Header("Налаштування")]
    [Tooltip("Розмір одного вокселя в метрах")]
    public float voxelSize = 0.25f;
    [Tooltip("Необов'язково: будь-який URP/Lit матеріал. Якщо порожньо, створюється автоматично")]
    public Material voxelMaterial;

    public struct RemovedVoxel
    {
        public Vector3 position;
        public byte id;
    }

    readonly Dictionary<Vector3Int, VoxelChunk> chunks = new Dictionary<Vector3Int, VoxelChunk>();
    readonly HashSet<VoxelChunk> dirty = new HashSet<VoxelChunk>();
    readonly Color32[] palette = new Color32[256];
    readonly Dictionary<uint, byte> paletteLookup = new Dictionary<uint, byte>();
    int paletteCount;
    bool paletteDirty = true;
    Texture2D paletteTexture;

    public Material RuntimeMaterial { get; private set; }

    static readonly Vector3Int[] Dirs =
    {
        new Vector3Int(1, 0, 0), new Vector3Int(-1, 0, 0),
        new Vector3Int(0, 1, 0), new Vector3Int(0, -1, 0),
        new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1),
    };

    void Awake()
    {
        // палітра-текстура: піксель i = колір з індексом i
        paletteTexture = new Texture2D(256, 1, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        for (int i = 0; i < 256; i++) palette[i] = new Color32(255, 0, 255, 255);

        Shader lit = Shader.Find("Universal Render Pipeline/Lit");
        RuntimeMaterial = voxelMaterial != null ? new Material(voxelMaterial) : new Material(lit);
        RuntimeMaterial.SetTexture("_BaseMap", paletteTexture);
        RuntimeMaterial.SetColor("_BaseColor", Color.white);
        RuntimeMaterial.SetFloat("_Smoothness", 0f);
    }

    // ---------- палітра ----------

    public byte GetOrAddColor(Color32 c)
    {
        uint key = (uint)(c.r | (c.g << 8) | (c.b << 16));
        if (paletteLookup.TryGetValue(key, out byte existing)) return existing;
        if (paletteCount >= 255) return 255;

        paletteCount++;
        palette[paletteCount] = new Color32(c.r, c.g, c.b, 255);
        paletteLookup[key] = (byte)paletteCount;
        paletteDirty = true;
        return (byte)paletteCount;
    }

    public Color32 GetColor(byte id) => palette[id];

    // ---------- координати ----------

    static int FloorDiv(int a, int b)
    {
        int q = a / b;
        if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
        return q;
    }

    public static Vector3Int ToChunkCoord(Vector3Int g) =>
        new Vector3Int(FloorDiv(g.x, ChunkSize), FloorDiv(g.y, ChunkSize), FloorDiv(g.z, ChunkSize));

    // ---------- доступ до вокселів ----------

    public byte GetVoxel(Vector3Int g)
    {
        Vector3Int cc = ToChunkCoord(g);
        if (!chunks.TryGetValue(cc, out VoxelChunk chunk)) return 0;
        Vector3Int l = g - cc * ChunkSize;
        return chunk.Get(l.x, l.y, l.z);
    }

    public void SetVoxel(Vector3Int g, byte id)
    {
        Vector3Int cc = ToChunkCoord(g);
        if (!chunks.TryGetValue(cc, out VoxelChunk chunk))
        {
            if (id == 0) return;
            chunk = CreateChunk(cc);
        }
        Vector3Int l = g - cc * ChunkSize;
        chunk.Set(l.x, l.y, l.z, id);
        dirty.Add(chunk);
    }

    VoxelChunk CreateChunk(Vector3Int cc)
    {
        var go = new GameObject($"Chunk {cc.x}_{cc.y}_{cc.z}");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = new Vector3(cc.x, cc.y, cc.z) * (ChunkSize * voxelSize);

        var chunk = go.AddComponent<VoxelChunk>();
        chunk.Init(this, cc);
        chunks[cc] = chunk;
        return chunk;
    }

    public void FillBox(Vector3Int min, Vector3Int size, byte id)
    {
        for (int z = 0; z < size.z; z++)
        for (int y = 0; y < size.y; y++)
        for (int x = 0; x < size.x; x++)
            SetVoxel(min + new Vector3Int(x, y, z), id);
    }

    // ---------- руйнування ----------

    // Видаляє всі вокселі в кулі. Повертає кількість видалених.
    public int DestroySphere(Vector3 worldCenter, float worldRadius, List<RemovedVoxel> removed = null)
    {
        Vector3 c = transform.InverseTransformPoint(worldCenter) / voxelSize;   // у координатах вокселів
        float r = worldRadius / voxelSize;
        float r2 = r * r;

        Vector3Int min = new Vector3Int(Mathf.FloorToInt(c.x - r), Mathf.FloorToInt(c.y - r), Mathf.FloorToInt(c.z - r));
        Vector3Int max = new Vector3Int(Mathf.FloorToInt(c.x + r), Mathf.FloorToInt(c.y + r), Mathf.FloorToInt(c.z + r));

        int count = 0;
        for (int z = min.z; z <= max.z; z++)
        for (int y = min.y; y <= max.y; y++)
        for (int x = min.x; x <= max.x; x++)
        {
            Vector3 center = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
            if ((center - c).sqrMagnitude > r2) continue;

            Vector3Int g = new Vector3Int(x, y, z);
            Vector3Int cc = ToChunkCoord(g);
            if (!chunks.TryGetValue(cc, out VoxelChunk chunk)) continue;

            Vector3Int l = g - cc * ChunkSize;
            byte id = chunk.Get(l.x, l.y, l.z);
            if (id == 0) continue;

            chunk.Set(l.x, l.y, l.z, 0);
            dirty.Add(chunk);
            count++;

            if (removed != null)
                removed.Add(new RemovedVoxel { position = transform.TransformPoint(center * voxelSize), id = id });

            // якщо воксель на краю чанка, відкриваються грані сусіднього чанка
            for (int i = 0; i < 6; i++)
            {
                Vector3Int ncc = ToChunkCoord(g + Dirs[i]);
                if (ncc != cc && chunks.TryGetValue(ncc, out VoxelChunk neighbor))
                    dirty.Add(neighbor);
            }
        }
        return count;
    }

    // ---------- імпорт .vox ----------

    public void LoadVox(byte[] data, Vector3Int origin)
    {
        var model = VoxLoader.Load(data);
        if (model == null) return;

        byte[] map = new byte[256];   // індекс кольору у файлі -> індекс у нашій палітрі
        foreach (var v in model.voxels)
        {
            if (map[v.color] == 0)
                map[v.color] = GetOrAddColor(model.palette[Mathf.Max(0, v.color - 1)]);

            // у MagicaVoxel вертикаль це Z, у Unity це Y
            SetVoxel(origin + new Vector3Int(v.x, v.z, v.y), map[v.color]);
        }
    }

    // ---------- оновлення ----------

    void LateUpdate()
    {
        if (paletteDirty)
        {
            paletteTexture.SetPixels32(palette);
            paletteTexture.Apply(false);
            paletteDirty = false;
        }

        if (dirty.Count == 0) return;
        foreach (var chunk in dirty) chunk.Rebuild();
        dirty.Clear();
    }
}
