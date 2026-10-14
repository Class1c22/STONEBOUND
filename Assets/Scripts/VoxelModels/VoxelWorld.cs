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

    [Header("Відламані шматки")]
    [Tooltip("Шматки, що ні з чим не з'єднані із землею, відриваються і падають")]
    public bool detachFloating = true;
    [Tooltip("Наскільки далеко (у вокселях) конструкція може триматися «на вазі» без опори знизу. Менше = крихкіші арки й навіси")]
    public int maxOverhang = 8;
    [Tooltip("Якщо шматок більший за це число вокселів, він вважається міцним і не падає (захист від лагів)")]
    public int maxPieceVoxels = 100000;
    [Tooltip("Скільки падаючих шматків живе одночасно (найстаріші зникають)")]
    public int maxPieces = 80;

    [Header("Падіння шматків")]
    [Tooltip("Мінімальна швидкість удару, при якій шматок ламається")]
    public float pieceBreakSpeed = 6f;
    public float pieceBreakBaseRadius = 0.4f;
    [Tooltip("Скільки радіуса додає кожен м/с швидкості удару")]
    public float pieceBreakRadiusPerSpeed = 0.05f;
    public float pieceBreakMaxRadius = 2f;
    [Tooltip("Яку частку радіуса шматок ламає у воксельному світі, у який врізався (0 = не ламає)")]
    [Range(0f, 1f)] public float pieceWorldDamage = 0.6f;

    [Header("Уламки")]
    [Tooltip("Розмір пулу уламків (найстаріші використовуються повторно)")]
    public int maxDebris = 300;
    [Tooltip("Яка частка вибитих вокселів стає уламками (0.1 = кожен 10-й). Більше шкоди = більше уламків")]
    [Range(0f, 1f)] public float debrisFraction = 0.1f;
    [Tooltip("Мінімум уламків за один удар, навіть якщо шкода мала")]
    public int minDebrisPerHit = 3;
    [Tooltip("Стеля уламків за один удар (захист від просадки FPS)")]
    public int maxDebrisPerHit = 120;
    public float debrisScale = 0.9f;
    public float debrisSpread = 4f;

    [Header("Ґрунт (воксельна земля)")]
    [Tooltip("Вмикається автоматично, коли MountainMapGenerator насипає воксельну землю нижче y = 0. " +
             "Тоді вокселі з y < 0 це земля, а не споруда: вони не беруть участі в перевірці обвалу, " +
             "а споруда на y = 0 тримається, поки під нею лишається земля")]
    public bool voxelGround = false;
    [Tooltip("Яка частка уламків із землі лишається (0.3 = 30%). Менше = камінь менше росте від копання землі")]
    [Range(0f, 1f)] public float groundDebrisChance = 0.3f;
    [Tooltip("Наскільки далеко (у вокселях) земля може нависати над ямою без опори знизу. Більше нависання осипається")]
    public int groundOverhang = 2;
    [Tooltip("Нижній шар воксельної землі. Задає MountainMapGenerator. Нижче нього основа, що не ламається")]
    public int groundBottomY = -6;

    [System.Serializable]
    public struct MaterialRule
    {
        public string name;
        [Tooltip("Колір матеріалу (як у палітрі моделі)")]
        public Color color;
        [Tooltip("Наскільки колір воксела може відрізнятися від цього (0..1)")]
        [Range(0f, 1f)] public float tolerance;
        [Tooltip("Міцність: 1 = дерево, 2 = цегла, 3 = бетон... Камінь ламає матеріал, якщо його сила >= міцності")]
        public int hardness;
    }

    [Header("Міцність матеріалів")]
    [Tooltip("Міцність вокселя, колір якого не підійшов до жодного правила")]
    public int defaultHardness = 1;
    [Tooltip("Правила за кольором: найближчий колір у межах tolerance визначає міцність")]
    public List<MaterialRule> materialRules = new List<MaterialRule>
    {
        new MaterialRule { name = "Дерево", color = new Color(0.43f, 0.27f, 0.18f), tolerance = 0.2f,  hardness = 1 },
        new MaterialRule { name = "Цегла",  color = new Color(0.63f, 0.25f, 0.20f), tolerance = 0.18f, hardness = 2 },
        new MaterialRule { name = "Бетон",  color = new Color(0.45f, 0.45f, 0.45f), tolerance = 0.12f, hardness = 3 },
        new MaterialRule { name = "Метал",  color = new Color(0.27f, 0.33f, 0.41f), tolerance = 0.15f, hardness = 4 },
    };

    // (точка удару, скільки вокселів не вдалося зламати через міцність, скільки зламано)
    public event System.Action<Vector3, int, int> HitBlocked;

    public struct RemovedVoxel
    {
        public Vector3 position;
        public byte id;
    }

    readonly Dictionary<Vector3Int, VoxelChunk> chunks = new Dictionary<Vector3Int, VoxelChunk>();
    readonly HashSet<VoxelChunk> dirty = new HashSet<VoxelChunk>();
    readonly List<GameObject> pieces = new List<GameObject>();
    Rigidbody[] debrisPool;
    int debrisIndex;
    Material debrisMaterial;
    MaterialPropertyBlock debrisBlock;
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    readonly Color32[] palette = new Color32[256];
    readonly Dictionary<uint, byte> paletteLookup = new Dictionary<uint, byte>();
    int paletteCount;
    readonly byte[] hardness = new byte[256];   // міцність за індексом палітри
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
        hardness[paletteCount] = (byte)ComputeHardness(c);
        paletteLookup[key] = (byte)paletteCount;
        paletteDirty = true;
        return (byte)paletteCount;
    }

    public Color32 GetColor(byte id) => palette[id];

    public int GetHardness(byte id) => hardness[id];

    // Викликай, якщо змінив materialRules вже після завантаження моделей
    public void RecomputeHardness()
    {
        for (int i = 1; i <= paletteCount; i++) hardness[i] = (byte)ComputeHardness(palette[i]);
    }

    int ComputeHardness(Color32 c)
    {
        int best = defaultHardness;
        float bestDist = float.MaxValue;
        if (materialRules != null)
        {
            for (int i = 0; i < materialRules.Count; i++)
            {
                MaterialRule r = materialRules[i];
                float dr = c.r / 255f - r.color.r;
                float dg = c.g / 255f - r.color.g;
                float db = c.b / 255f - r.color.b;
                float d = Mathf.Sqrt(dr * dr + dg * dg + db * db);
                if (d <= r.tolerance && d < bestDist) { bestDist = d; best = r.hardness; }
            }
        }
        return Mathf.Clamp(best, 0, 255);
    }

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

    // Видаляє всі вокселі в кулі, міцність яких не більша за power. Повертає кількість видалених.
    // Без power ламає все (так руйнують шматки, що падають).
    public int DestroySphere(Vector3 worldCenter, float worldRadius, List<RemovedVoxel> removed = null, int power = int.MaxValue)
    {
        Vector3 c = transform.InverseTransformPoint(worldCenter) / voxelSize;   // у координатах вокселів
        float r = worldRadius / voxelSize;
        float r2 = r * r;

        Vector3Int min = new Vector3Int(Mathf.FloorToInt(c.x - r), Mathf.FloorToInt(c.y - r), Mathf.FloorToInt(c.z - r));
        Vector3Int max = new Vector3Int(Mathf.FloorToInt(c.x + r), Mathf.FloorToInt(c.y + r), Mathf.FloorToInt(c.z + r));

        int count = 0;
        int blocked = 0;
        var removedGrid = new List<Vector3Int>();
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

            // надто міцний матеріал для цієї сили: воксель лишається
            if (hardness[id] > power) { blocked++; continue; }

            chunk.Set(l.x, l.y, l.z, 0);
            dirty.Add(chunk);
            count++;
            removedGrid.Add(g);

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

        // спочатку осипається земля без опори: її вокселі додаються до removedGrid,
        // щоб споруди над ними теж перевірилися на обвал
        if (detachFloating && voxelGround && count > 0)
            CollapseGround(removedGrid);

        if (detachFloating && count > 0)
            DetachFloating(removedGrid);

        if (blocked > 0) HitBlocked?.Invoke(worldCenter, blocked, count);

        return count;
    }

    // ---------- відламані шматки ----------

    // Перевіряє, чи тримаються вокселі біля місця руйнування, з урахуванням гравітації.
    // Опора йде від землі (y = 0): вгору вона передається без втрат, а вбік і вниз
    // згасає за maxOverhang вокселів. Групи, що втратили опору через це руйнування, падають.
    void DetachFloating(List<Vector3Int> removedGrid)
    {
        // 1. збираємо всі вокселі, пов'язані з місцем руйнування
        var after = new HashSet<Vector3Int>();
        var queue = new Queue<Vector3Int>();
        foreach (var g in removedGrid)
            for (int i = 0; i < 6; i++)
            {
                Vector3Int n = g + Dirs[i];
                if (IsGroundCell(n) || GetVoxel(n) == 0 || !after.Add(n)) continue;

                queue.Enqueue(n);
                while (queue.Count > 0)
                {
                    Vector3Int p = queue.Dequeue();
                    for (int j = 0; j < 6; j++)
                    {
                        Vector3Int m = p + Dirs[j];
                        if (!IsGroundCell(m) && GetVoxel(m) != 0 && after.Add(m)) queue.Enqueue(m);
                    }
                    if (after.Count > maxPieceVoxels) return;   // надто велика споруда, не перевіряємо
                }
            }
        if (after.Count == 0) return;

        // 2. опора до руйнування (з видаленими вокселями) і після
        var before = new HashSet<Vector3Int>(after);
        foreach (var g in removedGrid) before.Add(g);

        var supportBefore = ComputeSupport(before);
        var supportAfter = ComputeSupport(after);

        // 3. вокселі, що тепер висять у повітрі
        var unsupported = new HashSet<Vector3Int>();
        foreach (var g in after)
            if (supportAfter[g] < 0) unsupported.Add(g);
        if (unsupported.Count == 0) return;

        // 4. ділимо їх на окремі шматки; падають ті, що раніше трималися
        //    (те, що висіло в повітрі ще до удару, наприклад задумане автором, лишається)
        var islands = new List<HashSet<Vector3Int>>();
        var seen = new HashSet<Vector3Int>();
        foreach (var start in unsupported)
        {
            if (!seen.Add(start)) continue;

            var island = new HashSet<Vector3Int> { start };
            queue.Enqueue(start);
            bool lostSupport = false;
            while (queue.Count > 0)
            {
                Vector3Int p = queue.Dequeue();
                if (supportBefore[p] >= 0) lostSupport = true;
                for (int j = 0; j < 6; j++)
                {
                    Vector3Int m = p + Dirs[j];
                    if (unsupported.Contains(m) && seen.Add(m))
                    {
                        island.Add(m);
                        queue.Enqueue(m);
                    }
                }
            }
            if (lostSupport) islands.Add(island);
        }

        foreach (var island in islands) CreatePiece(island);
    }

    // Клітинка належить воксельній землі (нижче y = 0), якщо земля увімкнена
    bool IsGroundCell(Vector3Int g) => voxelGround && g.y < 0;

    // Земля тримається, якщо вертикальний ланцюжок вокселів під нею доходить до нижнього шару
    bool GroundColumnSupported(Vector3Int g)
    {
        while (true)
        {
            if (g.y <= groundBottomY) return true;
            g += Vector3Int.down;
            if (GetVoxel(g) == 0) return false;
        }
    }

    // Після копання земля, що лишилась без опори знизу, осипається: нависання завдовжки до
    // groundOverhang вокселів тримається за сусідів збоку, решта падає шматками (дрібне розсипається уламками).
    // Падаючі вокселі додаються до removedGrid.
    void CollapseGround(List<Vector3Int> removedGrid)
    {
        // 1. кандидати: вокселі землі над викопаними, у тих самих стовпцях
        var candidates = new HashSet<Vector3Int>();
        for (int r = 0; r < removedGrid.Count; r++)
        {
            Vector3Int g = removedGrid[r];
            if (g.y >= 0) continue;
            for (int y = g.y + 1; y < 0; y++)
            {
                var c = new Vector3Int(g.x, y, g.z);
                if (GetVoxel(c) != 0) candidates.Add(c);
            }
        }
        if (candidates.Count == 0) return;

        // 2. серед них ті, що втратили вертикальну опору
        var unsupported = new HashSet<Vector3Int>();
        foreach (var c in candidates)
            if (!GroundColumnSupported(c)) unsupported.Add(c);
        if (unsupported.Count == 0) return;

        // 3. нависання в межах groundOverhang тримається за вокселі збоку (горизонтальні напрямки: 0, 1, 4, 5)
        var dist = new Dictionary<Vector3Int, int>();
        var queue = new Queue<Vector3Int>();
        foreach (var u in unsupported)
            for (int i = 0; i < 6; i++)
            {
                if (i == 2 || i == 3) continue;
                Vector3Int w = u + Dirs[i];
                if (unsupported.Contains(w) || GetVoxel(w) == 0) continue;
                dist[u] = 1;
                queue.Enqueue(u);
                break;
            }
        while (queue.Count > 0)
        {
            Vector3Int p = queue.Dequeue();
            int d = dist[p];
            if (d >= groundOverhang) continue;
            for (int i = 0; i < 6; i++)
            {
                if (i == 2 || i == 3) continue;
                Vector3Int w = p + Dirs[i];
                if (unsupported.Contains(w) && !dist.ContainsKey(w))
                {
                    dist[w] = d + 1;
                    queue.Enqueue(w);
                }
            }
        }

        var falling = new HashSet<Vector3Int>();
        foreach (var u in unsupported)
            if (!dist.ContainsKey(u)) falling.Add(u);
        if (falling.Count == 0) return;

        // 4. ділимо на окремі шматки
        var seen = new HashSet<Vector3Int>();
        var crumbs = new List<RemovedVoxel>();
        foreach (var start in falling)
        {
            if (!seen.Add(start)) continue;

            var island = new HashSet<Vector3Int> { start };
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                Vector3Int p = queue.Dequeue();
                for (int i = 0; i < 6; i++)
                {
                    Vector3Int m = p + Dirs[i];
                    if (falling.Contains(m) && seen.Add(m))
                    {
                        island.Add(m);
                        queue.Enqueue(m);
                    }
                }
            }

            removedGrid.AddRange(island);

            if (island.Count < 4)
            {
                // зовсім дрібне просто розсипається уламками
                foreach (var g in island)
                {
                    crumbs.Add(new RemovedVoxel
                    {
                        position = transform.TransformPoint(new Vector3(g.x + 0.5f, g.y + 0.5f, g.z + 0.5f) * voxelSize),
                        id = GetVoxel(g)
                    });
                    SetVoxel(g, 0);
                }
            }
            else if (island.Count <= maxPieceVoxels)
            {
                CreatePiece(island);
            }
        }

        if (crumbs.Count > 0) SpawnDebris(crumbs, crumbs[0].position, Vector3.zero);
    }

    // Запас опори кожного вокселя: maxOverhang на землі, вгору не зменшується,
    // вбік і вниз зменшується на 1. Менше 0 означає, що воксель висить у повітрі.
    Dictionary<Vector3Int, int> ComputeSupport(HashSet<Vector3Int> set)
    {
        int max = Mathf.Max(0, maxOverhang);
        var budget = new Dictionary<Vector3Int, int>(set.Count);
        foreach (var g in set) budget[g] = -1;

        var buckets = new List<Vector3Int>[max + 1];
        for (int i = 0; i <= max; i++) buckets[i] = new List<Vector3Int>();

        foreach (var g in set)
        {
            // y < 0: земля. y = 0: опора, якщо без воксельної землі, або якщо під вокселем лишилась земля
            bool root = g.y < 0 || (g.y == 0 && (!voxelGround || GetVoxel(g + Vector3Int.down) != 0));
            if (!root) continue;

            budget[g] = max;
            buckets[max].Add(g);
        }

        for (int b = max; b >= 0; b--)
        {
            var list = buckets[b];
            for (int idx = 0; idx < list.Count; idx++)
            {
                Vector3Int p = list[idx];
                if (budget[p] != b) continue;   // вже знайдено кращий шлях

                for (int i = 0; i < 6; i++)
                {
                    Vector3Int n = p + Dirs[i];
                    if (!budget.TryGetValue(n, out int current)) continue;

                    int nb = (i == 2) ? b : b - 1;   // Dirs[2] = вгору
                    if (nb >= 0 && nb > current)
                    {
                        budget[n] = nb;
                        buckets[nb].Add(n);
                    }
                }
            }
        }
        return budget;
    }

    void CreatePiece(HashSet<Vector3Int> comp)
    {
        // забираємо вокселі зі світу і передаємо їх новому шматку
        var voxels = new Dictionary<Vector3Int, byte>(comp.Count);
        Vector3 sum = Vector3.zero;
        foreach (var g in comp)
        {
            voxels[g] = GetVoxel(g);
            sum += new Vector3(g.x + 0.5f, g.y + 0.5f, g.z + 0.5f);
        }
        foreach (var g in comp) SetVoxel(g, 0);

        Vector3 pivot = sum / comp.Count;   // центр шматка у координатах вокселів
        VoxelPiece.Create(this, voxels, pivot, transform.TransformPoint(pivot * voxelSize),
                          transform.rotation, Vector3.zero, Vector3.zero);
    }

    public void UnregisterPiece(GameObject go)
    {
        pieces.Remove(go);
    }

    public void RegisterPiece(GameObject go)
    {
        pieces.RemoveAll(x => x == null);
        pieces.Add(go);
        while (pieces.Count > maxPieces)
        {
            Destroy(pieces[0]);
            pieces.RemoveAt(0);
        }
    }

    // ---------- уламки ----------

    public void SpawnDebris(List<RemovedVoxel> removed, Vector3 center, Vector3 inheritVelocity)
    {
        if (removed.Count == 0) return;

        if (debrisPool == null) debrisPool = new Rigidbody[Mathf.Max(1, maxDebris)];
        if (debrisBlock == null) debrisBlock = new MaterialPropertyBlock();
        if (debrisMaterial == null)
        {
            debrisMaterial = new Material(RuntimeMaterial);
            debrisMaterial.SetTexture("_BaseMap", Texture2D.whiteTexture);
        }

        // кількість уламків залежить від обсягу шкоди: скільки вокселів вибито
        int count = Mathf.RoundToInt(removed.Count * debrisFraction);
        count = Mathf.Clamp(count, minDebrisPerHit, Mathf.Max(minDebrisPerHit, maxDebrisPerHit));
        count = Mathf.Min(count, removed.Count);

        float step = (float)removed.Count / count;   // рівномірно беремо воксели з усього вибитого об'єму
        for (int k = 0; k < count; k++)
        {
            var v = removed[Mathf.Min(removed.Count - 1, (int)(k * step))];

            // з землі летить менше уламків, щоб камінь не ріс лише від копання
            if (voxelGround && transform.InverseTransformPoint(v.position).y < 0f && Random.value > groundDebrisChance)
                continue;

            Rigidbody d = GetDebris();

            d.transform.position = v.position;
            d.transform.rotation = Random.rotation;
            d.linearVelocity = (v.position - center).normalized * Random.Range(1f, debrisSpread)
                               + inheritVelocity * 0.3f + Random.insideUnitSphere;
            d.angularVelocity = Random.insideUnitSphere * 5f;

            debrisBlock.SetColor(BaseColorId, GetColor(v.id));
            d.GetComponent<Renderer>().SetPropertyBlock(debrisBlock);
        }
    }

    Rigidbody GetDebris()
    {
        Rigidbody d = debrisPool[debrisIndex];
        if (d == null)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "Debris";
            cube.transform.localScale = Vector3.one * voxelSize * debrisScale;
            cube.GetComponent<Renderer>().sharedMaterial = debrisMaterial;
            d = cube.AddComponent<Rigidbody>();
            d.mass = 0.05f;
            cube.AddComponent<Absorbable>().volume = Mathf.Pow(voxelSize * debrisScale, 3);
            debrisPool[debrisIndex] = d;
        }
        debrisIndex = (debrisIndex + 1) % debrisPool.Length;   // найстаріший уламок використовується повторно
        return d;
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