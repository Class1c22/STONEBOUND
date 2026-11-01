using UnityEngine;

// Нескінченне море замість землі. Заміна FloorGenerator.
// Повісь на порожній GameObject на y = 0 (там, де стоять споруди). Створює одну велику плоску підлогу
// кольору води з колайдером, яка їде за каменем кроками по клітинках (текстура стоїть у світі на місці),
// тому краю моря не видно. Острови малює й будує CountryGenerator: вони лежать поверх цього моря.
// Підлога не частина VoxelWorld, тож камінь її не руйнує.
// Щоб замінити траву: вимкни старий об'єкт з FloorGenerator (або вкажи його в Old Ground нижче).
public class SeaGroundGenerator : MonoBehaviour
{
    [Header("Море")]
    [Tooltip("Сторона квадрата моря в метрах. Має бути більша за дальність видимості камери")]
    public float size = 1200f;
    [Tooltip("Товщина колайдера вниз, щоб камінь не провалився на швидкості")]
    public float thickness = 20f;
    [Tooltip("Розмір одного повтору текстури в метрах. Море пересувається саме цим кроком")]
    public float tileSize = 8f;

    [Header("Вигляд")]
    [Tooltip("Якщо задано World, піксель текстури дорівнює вокселю, тож вода має той самий масштаб, що й споруди")]
    public bool matchVoxelSize = true;
    [Tooltip("Розмір пікселя текстури: більше = дрібніші плями. Ігнорується, якщо Match Voxel Size й задано World")]
    public int textureResolution = 32;
    public Color colorA = new Color(0.235f, 0.51f, 0.78f);
    public Color colorB = new Color(0.215f, 0.47f, 0.74f);
    [Tooltip("Світлі блікі на воді (0 = немає)")]
    [Range(0f, 0.5f)] public float sparkleAmount = 0.12f;
    public Color sparkleColor = new Color(0.51f, 0.745f, 0.92f);
    public int seed = 11;
    public bool receiveShadows = true;

    [Header("Фізика")]
    [Range(0f, 2f)] public float friction = 0.6f;
    [Range(0f, 1f)] public float bounciness = 0f;

    [Header("Зв'язки")]
    [Tooltip("Камінь, за яким їде море. Якщо порожньо, шукається сам (після переходу між сценами теж)")]
    public RockAbsorber rock;
    [Tooltip("Воксельний світ: його власна воксельна земля вимикається, бо земля тепер це море")]
    public VoxelWorld world;
    [Tooltip("Старий plane/земля: вимкнеться")]
    public GameObject oldGround;
    [Tooltip("Старий генератор гір: вимкнеться")]
    public GameObject oldMountains;

    Transform floor;
    float findTimer;

    void Start()
    {
        if (world != null) world.voxelGround = false;
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
        size = Mathf.Max(2f * tileSize, Mathf.Round(size / (2f * tileSize)) * 2f * tileSize);   // кратно плитці

        var go = new GameObject("Sea");
        go.transform.SetParent(transform, false);
        floor = go.transform;

        // один квад; UV = метри / tileSize, тож текстура прив'язана до світу
        float h = size * 0.5f, uv = size / tileSize;
        var mesh = new Mesh { name = "SeaQuad" };
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
        box.sharedMaterial = new PhysicsMaterial("Sea")
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
        var m = new Material(lit) { name = "SeaMat" };

        var tex = BuildTexture();
        if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
        if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", tex);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
        if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0f);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
        return m;
    }

    // Піксельна плиточна текстура: два відтінки води й світлі блікі
    Texture2D BuildTexture()
    {
        int n = textureResolution;
        if (matchVoxelSize && world != null && world.voxelSize > 0.0001f)
            n = Mathf.RoundToInt(tileSize / world.voxelSize);
        n = Mathf.Clamp(n, 4, 512);

        var tex = new Texture2D(n, n, TextureFormat.RGBA32, true)
        {
            name = "SeaTex",
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Point
        };

        var rng = new System.Random(seed);
        var px = new Color[n * n];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            Color c = Color.Lerp(colorA, colorB, (float)rng.NextDouble());
            if (rng.NextDouble() < sparkleAmount) c = sparkleColor;
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

    // Море стоїть там, де камінь, але зсувається лише кроками tileSize, щоб текстура не пливла
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