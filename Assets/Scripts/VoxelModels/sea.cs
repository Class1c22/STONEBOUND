using UnityEngine;

// Нескінченне море з хвилями замість землі. Заміна FloorGenerator.
// Повісь на порожній GameObject на y = 0 (там, де стоять споруди).
// Створює: (1) далеку плоску підлогу кольору води (тільки картинка), яка їде за каменем кроками по плитках,
// (2) невидиме тверде дно на seabedDepth нижче, (3) воксельну хвилясту воду навколо каменя (WaterSurface).
// Острови малює й будує CountryGenerator: вони лежать поверх цього моря. Море не частина VoxelWorld.
public class SeaGroundGenerator : MonoBehaviour
{
    [Header("Море")]
    [Tooltip("Сторона далекої підлоги в метрах. Має бути більша за дальність видимості камери")]
    public float size = 1200f;
    [Tooltip("Товщина колайдера дна вниз")]
    public float thickness = 20f;
    [Tooltip("Розмір одного повтору текстури в метрах. Далека підлога пересувається саме цим кроком")]
    public float tileSize = 8f;
    [Tooltip("Наскільки нижче за цей об'єкт лежить рівень води (мінус = вода трохи нижче суші)")]
    public float levelOffset = -0.15f;
    [Tooltip("Глибина дна під водою. Має бути більша за діаметр каменя на найбільшій стадії")]
    public float seabedDepth = 50f;

    [Header("Хвилі")]
    public int waterCells = 100;
    public float waterCellSize = 1f;
    [Range(0f, 3f)] public float waveHeight = 1f;
    public bool blockyWaves = true;
    public float heightStep = 0.1f;
    public WaterSurface.Wave[] waves = new WaterSurface.Wave[]
    {
        new WaterSurface.Wave { direction = new Vector2(1f, 0.3f),    amplitude = 0.2f,  wavelength = 14f, speed = 2.5f },
        new WaterSurface.Wave { direction = new Vector2(-0.4f, 1f),   amplitude = 0.12f, wavelength = 7f,  speed = 1.8f },
        new WaterSurface.Wave { direction = new Vector2(0.7f, -0.7f), amplitude = 0.06f, wavelength = 3.5f, speed = 1.2f },
    };

    [Header("Вигляд")]
    public bool matchVoxelSize = true;
    public int textureResolution = 32;
    public Color colorA = new Color(0.235f, 0.51f, 0.78f);
    public Color colorB = new Color(0.215f, 0.47f, 0.74f);
    [Range(0f, 0.5f)] public float sparkleAmount = 0.12f;
    public Color sparkleColor = new Color(0.51f, 0.745f, 0.92f);
    public int seed = 11;
    public bool receiveShadows = true;

    [Header("Фізика дна")]
    [Range(0f, 2f)] public float friction = 0.6f;
    [Range(0f, 1f)] public float bounciness = 0f;

    [Header("Зв'язки")]
    public RockAbsorber rock;
    public VoxelWorld world;
    public GameObject oldGround;
    public GameObject oldMountains;

    Transform floor;
    WaterSurface water;
    float findTimer;

    void Start()
    {
        if (world != null) world.voxelGround = false;
        if (oldGround != null) oldGround.SetActive(false);
        if (oldMountains != null) oldMountains.SetActive(false);
        Build();
    }

    void OnDestroy()
    {
        if (water != null) Destroy(water.gameObject);
    }

    [ContextMenu("Rebuild")]
    public void Build()
    {
        if (floor != null) Destroy(floor.gameObject);
        if (water != null) Destroy(water.gameObject);

        size = Mathf.Max(10f, size);
        tileSize = Mathf.Max(0.5f, tileSize);
        size = Mathf.Max(2f * tileSize, Mathf.Round(size / (2f * tileSize)) * 2f * tileSize);

        float waterY = transform.position.y + levelOffset;
        Material mat = BuildMaterial();

        // 1) далека підлога (тільки візуально), трохи нижче води, щоб не мерехтіла
        var go = new GameObject("SeaFar");
        go.transform.SetParent(transform, false);
        floor = go.transform;

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
        rend.sharedMaterial = mat;
        rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        rend.receiveShadows = receiveShadows;

        // 2) тверде невидиме дно глибоко під водою
        var seabed = new GameObject("Seabed");
        seabed.transform.SetParent(go.transform, false);
        seabed.transform.localPosition = new Vector3(0f, -seabedDepth, 0f);
        var box = seabed.AddComponent<BoxCollider>();
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

        // 3) хвиляста вода (окремий об'єкт у X=0,Z=0 без батька, щоб координати збігались зі світовими)
        var wgo = new GameObject("SeaWater");
        wgo.SetActive(false);   // налаштуємо поля до Awake
        wgo.transform.position = new Vector3(0f, waterY, 0f);
        wgo.AddComponent<MeshFilter>();
        wgo.AddComponent<MeshRenderer>().sharedMaterial = mat;
        water = wgo.AddComponent<WaterSurface>();
        water.cellsPerSide = Mathf.Max(8, waterCells);
        water.cellSize = Mathf.Max(0.05f, waterCellSize);
        water.blocky = blockyWaves;
        water.heightStep = heightStep;
        water.uvTileSize = tileSize;
        water.waves = ScaledWaves();
        wgo.SetActive(true);

        Follow(true);
    }

    WaterSurface.Wave[] ScaledWaves()
    {
        var w = new WaterSurface.Wave[waves.Length];
        for (int i = 0; i < w.Length; i++)
        {
            w[i] = waves[i];
            w[i].amplitude *= waveHeight;
        }
        return w;
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
        // далека підлога: трохи нижче води
        floor.position = new Vector3(x, transform.position.y + levelOffset - 0.03f, z);

        if (water != null) water.follow = rock != null ? rock.transform : null;
    }
}