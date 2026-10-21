using UnityEngine;

// Нескінченна плоска земля замість гір.
// Повісь на порожній GameObject. Створює одну велику плоску підлогу з колайдером, яка їде за каменем
// (стрибками по клітинках, тож текстура стоїть у світі на місці), тому краю карти не видно.
// Підлога не частина VoxelWorld, тож камінь її не руйнує. Трава (GrassField) бачить її колайдер променем.
// Верх підлоги = y цього об'єкта (став його на y = 0, де стоять споруди).
// Для заміни гір: вимкни об'єкт з MountainMapGenerator і старий plane, повісь цей скрипт.
public class FloorGenerator : MonoBehaviour
{
    [Header("Підлога")]
    [Tooltip("Сторона квадрата підлоги в метрах. Має бути більша за дальність видимості трави й камери")]
    public float size = 1200f;
    [Tooltip("Товщина колайдера вниз, щоб камінь не провалився на швидкості")]
    public float thickness = 20f;
    [Tooltip("Розмір одного повтору текстури в метрах. Підлога пересувається саме цим кроком")]
    public float tileSize = 8f;

    [Header("Вигляд")]
    [Tooltip("Розмір пікселя текстури землі: більше = крупніші плями")]
    public int textureResolution = 32;
    public Color colorA = new Color(0.30f, 0.48f, 0.18f);
    public Color colorB = new Color(0.36f, 0.55f, 0.22f);
    [Tooltip("Плями землі між травою (0 = немає)")]
    [Range(0f, 0.5f)] public float dirtAmount = 0.12f;
    public Color dirtColor = new Color(0.40f, 0.30f, 0.20f);
    public int seed = 7;
    public bool receiveShadows = true;

    [Header("Фізика")]
    [Range(0f, 2f)] public float friction = 0.6f;
    [Range(0f, 1f)] public float bounciness = 0f;

    [Header("Зв'язки")]
    [Tooltip("Камінь, за яким їде підлога. Якщо порожньо, шукається сам (після переходу між сценами теж)")]
    public RockAbsorber rock;
    [Tooltip("Воксельний світ: його власна воксельна земля вимикається, бо земля тепер ця підлога")]
    public VoxelWorld world;
    [Tooltip("Старий plane/земля: вимкнеться")]
    public GameObject oldGround;
    [Tooltip("Старий генератор гір: вимкнеться, щоб не будував гори")]
    public GameObject oldMountains;

    Transform floor;
    float findTimer;

    void Start()
    {
        if (world != null) world.voxelGround = false;   // як у MountainMapGenerator: земля тепер окремий меш
        if (oldGround != null) oldGround.SetActive(false);
        if (oldMountains != null) oldMountains.SetActive(false);
        Build();
    }

    [ContextMenu("Rebuild")]
    public void Build()
    {
        if (floor != null) Destroy(floor.gameObject);

        size = Mathf.Max(10f, size);
        tileSize = Mathf.Max(0.5f, tileSize);
        size = Mathf.Max(2f * tileSize, Mathf.Round(size / (2f * tileSize)) * 2f * tileSize);   // кратно плитці, щоб текстура не зсувалась

        var go = new GameObject("Floor");
        go.transform.SetParent(transform, false);
        floor = go.transform;

        // один квад; UV = метри / tileSize, тож текстура прив'язана до світу
        float h = size * 0.5f, uv = size / tileSize;
        var mesh = new Mesh { name = "FloorQuad" };
        mesh.vertices = new[]
        {
            new Vector3(-h, 0, -h), new Vector3(-h, 0, h), new Vector3(h, 0, h), new Vector3(h, 0, -h)
        };
        mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
        mesh.uv = new[] { new Vector2(0, 0), new Vector2(0, uv), new Vector2(uv, uv), new Vector2(uv, 0) };
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.RecalculateBounds();
        go.AddComponent<MeshFilter>().sharedMesh = mesh;

        var rend = go.AddComponent<MeshRenderer>();
        rend.sharedMaterial = BuildMaterial();
        rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        rend.receiveShadows = receiveShadows;

        // колайдер-коробка: верх на рівні підлоги
        var box = go.AddComponent<BoxCollider>();
        box.size = new Vector3(size, thickness, size);
        box.center = new Vector3(0f, -thickness * 0.5f, 0f);
        box.sharedMaterial = new PhysicsMaterial("Floor")
        {
            dynamicFriction = friction,
            staticFriction = friction,
            bounciness = bounciness,
            frictionCombine = PhysicsMaterialCombine.Average,
            bounceCombine = PhysicsMaterialCombine.Minimum
        };

        Follow(true);
    }

    Material BuildMaterial()
    {
        Shader lit = Shader.Find("Universal Render Pipeline/Lit");
        if (lit == null) lit = Shader.Find("Standard");
        var m = new Material(lit) { name = "FloorMat" };

        var tex = BuildTexture();
        if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
        if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", tex);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
        if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0f);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
        return m;
    }

    // Піксельна плиточна текстура: два відтінки трави й плями землі (шум з перенесенням країв, тому швів немає)
    Texture2D BuildTexture()
    {
        int n = Mathf.Max(4, textureResolution);
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, true)
        {
            name = "FloorTex",
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Point
        };

        var rng = new System.Random(seed);
        var px = new Color[n * n];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            Color c = Color.Lerp(colorA, colorB, (float)rng.NextDouble());
            if (rng.NextDouble() < dirtAmount) c = Color.Lerp(c, dirtColor, 0.8f);
            px[y * n + x] = c;
        }
        tex.SetPixels(px);
        tex.Apply(true);
        return tex;
    }

    void LateUpdate()
    {
        Follow(false);
    }

    // Підлога стоїть там, де камінь, але зсувається лише кроками tileSize, щоб текстура не пливла
    void Follow(bool force)
    {
        if (floor == null) return;

        if (rock == null)
        {
            findTimer -= Time.deltaTime;
            if (findTimer <= 0f || force)
            {
                findTimer = 0.5f;
                rock = FindFirstObjectByType<RockAbsorber>();
            }
        }

        Vector3 focus = rock != null ? rock.WorldCenter : transform.position;
        float x = Mathf.Round(focus.x / tileSize) * tileSize;
        float z = Mathf.Round(focus.z / tileSize) * tileSize;
        floor.position = new Vector3(x, transform.position.y, z);
    }
}