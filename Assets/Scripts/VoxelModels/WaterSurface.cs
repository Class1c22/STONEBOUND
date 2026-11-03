using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Можна ставити де завгодно (rotation 0, scale 1). Y об'єкта = базовий рівень води.
// Центр води сам іде за камерою.
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class WaterSurface : MonoBehaviour
{
    public static WaterSurface Instance { get; private set; }

    [System.Serializable]
    public struct Wave
    {
        public Vector2 direction;   // напрямок руху хвилі (X,Z)
        public float amplitude;     // висота
        public float wavelength;    // довжина хвилі
        public float speed;         // швидкість руху
    }

    [Header("Сітка")]
    [Tooltip("Якщо увімкнено, вода сама шукає камеру й ігнорує поле Follow")]
    public bool useCamera = true;
    [Tooltip("Використовується, лише коли Use Camera вимкнено")]
    public Transform follow;
    public int cellsPerSide = 100;
    public float cellSize = 1f;
    public float updateInterval = 0.033f;
    [Range(0.02f, 0.6f)] public float edgeFade = 0.25f;

    [Header("Воксельний вигляд")]
    public bool blocky = true;
    public float heightStep = 0.1f;
    public bool walls = true;
    public float uvTileSize = 8f;

    [Header("Хвилі")]
    public Wave[] waves = new Wave[]
    {
        new Wave { direction = new Vector2(1f, 0.3f),    amplitude = 0.2f,  wavelength = 14f, speed = 2.5f },
        new Wave { direction = new Vector2(-0.4f, 1f),   amplitude = 0.12f, wavelength = 7f,  speed = 1.8f },
        new Wave { direction = new Vector2(0.7f, -0.7f), amplitude = 0.06f, wavelength = 3.5f, speed = 1.2f },
    };

    Mesh mesh;
    float timer;
    float findTimer;
    Transform camT;
    float[,] centerH;
    float[,] cornerH;
    readonly List<Vector3> verts = new List<Vector3>();
    readonly List<Vector2> uvs = new List<Vector2>();
    readonly List<int> tris = new List<int>();

    float cx, cz, half;     // центр і півсторона сітки у світових координатах
    float px, pz;           // світова позиція об'єкта (вершини рахуються відносно неї)

    void Awake()
    {
        Instance = this;
        mesh = new Mesh { name = "WaterMesh", indexFormat = IndexFormat.UInt32 };
        GetComponent<MeshFilter>().sharedMesh = mesh;
        var r = GetComponent<MeshRenderer>();
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        Rebuild();
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void LateUpdate()
    {
        timer += Time.deltaTime;
        if (timer < updateInterval) return;
        timer = 0f;
        Rebuild();
    }

    Transform Focus()
    {
        if (!useCamera) return follow;

        if (camT == null)
        {
            findTimer -= Time.unscaledDeltaTime;
            if (findTimer <= 0f)
            {
                findTimer = 0.5f;
                Camera cam = Camera.main;
                if (cam == null) cam = FindFirstObjectByType<Camera>();
                if (cam != null) camT = cam.transform;
            }
        }
        return camT != null ? camT : follow;
    }

    // Гладка висота води у світових координатах (для плавання)
    public float GetHeight(float x, float z)
    {
        float t = Time.time;
        float h = transform.position.y;
        for (int i = 0; i < waves.Length; i++)
        {
            Wave w = waves[i];
            Vector2 d = w.direction.sqrMagnitude > 0.0001f ? w.direction.normalized : Vector2.right;
            float k = 2f * Mathf.PI / Mathf.Max(0.01f, w.wavelength);
            h += w.amplitude * Mathf.Sin(k * (d.x * x + d.y * z - w.speed * t));
        }
        return h;
    }

    public Vector2 GetSlope(float x, float z)
    {
        const float e = 0.2f;
        float dx = GetHeight(x + e, z) - GetHeight(x - e, z);
        float dz = GetHeight(x, z + e) - GetHeight(x, z - e);
        return new Vector2(dx, dz) / (2f * e);
    }

    float Fade(float x, float z)
    {
        float d = Mathf.Max(Mathf.Abs(x - cx), Mathf.Abs(z - cz)) / half;   // 0 центр .. 1 край
        float f = Mathf.Clamp01((1f - d) / Mathf.Max(0.01f, edgeFade));
        return f * f * (3f - 2f * f);
    }

    // висота відносно рівня води (baseY)
    float VisibleHeight(float x, float z, bool quantize)
    {
        float baseY = transform.position.y;
        float h = (GetHeight(x, z) - baseY) * Fade(x, z);
        if (quantize && heightStep > 0.0001f) h = Mathf.Round(h / heightStep) * heightStep;
        return h;
    }

    void Rebuild()
    {
        int n = Mathf.Max(2, cellsPerSide);
        float cs = cellSize;

        Vector3 pos = transform.position;
        px = pos.x;
        pz = pos.z;

        Transform ft = Focus();
        Vector3 f = ft != null ? ft.position : pos;

        // світові координати кута сітки (прив'язка до клітинок у світі)
        float ox = Mathf.Round(f.x / cs) * cs - n * cs * 0.5f;
        float oz = Mathf.Round(f.z / cs) * cs - n * cs * 0.5f;
        half = n * cs * 0.5f;
        cx = ox + half;
        cz = oz + half;

        if (blocky)
        {
            if (centerH == null || centerH.GetLength(0) != n) centerH = new float[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    centerH[i, j] = VisibleHeight(ox + (i + 0.5f) * cs, oz + (j + 0.5f) * cs, true);
        }
        else
        {
            if (cornerH == null || cornerH.GetLength(0) != n + 1) cornerH = new float[n + 1, n + 1];
            for (int i = 0; i <= n; i++)
                for (int j = 0; j <= n; j++)
                    cornerH[i, j] = VisibleHeight(ox + i * cs, oz + j * cs, false);
        }

        verts.Clear();
        uvs.Clear();
        tris.Clear();

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                float x0 = ox + i * cs, x1 = x0 + cs;
                float z0 = oz + j * cs, z1 = z0 + cs;

                if (blocky)
                {
                    float h = centerH[i, j];
                    AddQuad(new Vector3(x0, h, z0), new Vector3(x0, h, z1),
                            new Vector3(x1, h, z1), new Vector3(x1, h, z0), 0);

                    if (!walls) continue;

                    if (i + 1 < n)
                    {
                        float hn = centerH[i + 1, j];
                        if (Mathf.Abs(h - hn) > 0.0001f)
                        {
                            float lo = Mathf.Min(h, hn), hi = Mathf.Max(h, hn);
                            if (h > hn)
                                AddQuad(new Vector3(x1, lo, z0), new Vector3(x1, hi, z0),
                                        new Vector3(x1, hi, z1), new Vector3(x1, lo, z1), 1);
                            else
                                AddQuad(new Vector3(x1, lo, z1), new Vector3(x1, hi, z1),
                                        new Vector3(x1, hi, z0), new Vector3(x1, lo, z0), 1);
                        }
                    }
                    if (j + 1 < n)
                    {
                        float hn = centerH[i, j + 1];
                        if (Mathf.Abs(h - hn) > 0.0001f)
                        {
                            float lo = Mathf.Min(h, hn), hi = Mathf.Max(h, hn);
                            if (h > hn)
                                AddQuad(new Vector3(x1, lo, z1), new Vector3(x1, hi, z1),
                                        new Vector3(x0, hi, z1), new Vector3(x0, lo, z1), 2);
                            else
                                AddQuad(new Vector3(x0, lo, z1), new Vector3(x0, hi, z1),
                                        new Vector3(x1, hi, z1), new Vector3(x1, lo, z1), 2);
                        }
                    }
                }
                else
                {
                    AddQuad(new Vector3(x0, cornerH[i, j], z0),
                            new Vector3(x0, cornerH[i, j + 1], z1),
                            new Vector3(x1, cornerH[i + 1, j + 1], z1),
                            new Vector3(x1, cornerH[i + 1, j], z0), 0);
                }
            }
        }

        mesh.Clear();
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();   // вершини не спільні, тож нормалі пласкі
        mesh.bounds = new Bounds(new Vector3(cx - px, 0f, cz - pz), new Vector3(n * cs, 20f, n * cs));
    }

    // uvMode: 0 = верх (x,z), 1 = стінка в площині X (z,y), 2 = стінка в площині Z (x,y)
    // Вершини приходять у світових X,Z; у меш пишуться відносно позиції об'єкта.
    void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, int uvMode)
    {
        float inv = 1f / Mathf.Max(0.01f, uvTileSize);
        int s = verts.Count;
        for (int k = 0; k < 4; k++)
        {
            Vector3 v = k == 0 ? a : k == 1 ? b : k == 2 ? c : d;
            if (uvMode == 0) uvs.Add(new Vector2(v.x * inv, v.z * inv));
            else if (uvMode == 1) uvs.Add(new Vector2(v.z * inv, v.y * inv));
            else uvs.Add(new Vector2(v.x * inv, v.y * inv));
            verts.Add(new Vector3(v.x - px, v.y, v.z - pz));
        }
        tris.Add(s); tris.Add(s + 1); tris.Add(s + 2);
        tris.Add(s); tris.Add(s + 2); tris.Add(s + 3);
    }
}