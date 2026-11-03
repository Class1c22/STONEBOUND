using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Можна ставити де завгодно (rotation 0, scale 1). Y об'єкта = базовий рівень води.
// Центр води сам іде за камерою. Камінь збурює воду через AddDisturbance (див. FloatingRock).
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
    [Tooltip("Використовується, лише коли Use Camera вимкнено (або камери немає)")]
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
    [Tooltip("1 = звичайна синусоїда, більше = гостріші гребені й пласкіші западини")]
    [Range(1f, 4f)] public float crestSharpness = 1.8f;
    public Wave[] waves = new Wave[]
    {
        new Wave { direction = new Vector2(1f, 0.3f),    amplitude = 0.2f,  wavelength = 14f, speed = 2.5f },
        new Wave { direction = new Vector2(-0.4f, 1f),   amplitude = 0.12f, wavelength = 7f,  speed = 1.8f },
        new Wave { direction = new Vector2(0.7f, -0.7f), amplitude = 0.06f, wavelength = 3.5f, speed = 1.2f },
    };

    [Header("Брижі від каменя")]
    [Tooltip("Швидкість розходження брижів, м/с")]
    public float rippleSpeed = 8f;
    [Tooltip("Скільки руху лишається за крок (менше = швидше згасають)")]
    [Range(0.9f, 0.999f)] public float rippleDamping = 0.988f;
    [Tooltip("Максимальна висота бриж, м")]
    public float maxRipple = 3f;

    Mesh mesh;
    float timer;
    float findTimer;
    Transform camT;

    int n;
    float cs;
    int originI, originJ;
    float ox, oz, cx, cz, half;
    float px, pz;

    float[,] rip, ripPrev, scratch;
    float[,] centerH, cornerH;

    float meanCache, sharpCache = -1f;

    readonly List<Vector3> verts = new List<Vector3>();
    readonly List<Vector2> uvs = new List<Vector2>();
    readonly List<int> tris = new List<int>();

    void Awake()
    {
        Instance = this;
        mesh = new Mesh { name = "WaterMesh", indexFormat = IndexFormat.UInt32 };
        GetComponent<MeshFilter>().sharedMesh = mesh;
        var r = GetComponent<MeshRenderer>();
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        UpdateGrid();
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
        int steps = Mathf.Clamp(Mathf.FloorToInt(timer / updateInterval), 1, 3);
        timer = 0f;

        UpdateGrid();
        for (int s = 0; s < steps; s++) Step(updateInterval);
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

    // ───────────── сітка й симуляція брижів ─────────────

    void UpdateGrid()
    {
        int newN = Mathf.Max(8, cellsPerSide);
        cs = Mathf.Max(0.05f, cellSize);

        Vector3 pos = transform.position;
        px = pos.x;
        pz = pos.z;

        Transform ft = Focus();
        Vector3 f = ft != null ? ft.position : pos;
        int ni = Mathf.RoundToInt(f.x / cs) - newN / 2;
        int nj = Mathf.RoundToInt(f.z / cs) - newN / 2;

        if (rip == null || newN != n)
        {
            n = newN;
            rip = new float[n, n];
            ripPrev = new float[n, n];
            scratch = new float[n, n];
            centerH = null;
            cornerH = null;
            originI = ni;
            originJ = nj;
        }
        else if (ni != originI || nj != originJ)
        {
            int dx = ni - originI, dz = nj - originJ;
            Shift(rip, dx, dz);
            Shift(ripPrev, dx, dz);
            originI = ni;
            originJ = nj;
        }

        ox = originI * cs;
        oz = originJ * cs;
        half = n * cs * 0.5f;
        cx = ox + half;
        cz = oz + half;
    }

    // сітка їде за камерою, а брижі лишаються на місці у світі
    void Shift(float[,] a, int dx, int dz)
    {
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                int si = i + dx, sj = j + dz;
                scratch[i, j] = (si < 0 || sj < 0 || si >= n || sj >= n) ? 0f : a[si, sj];
            }
        System.Array.Copy(scratch, a, n * n);
    }

    void Step(float dt)
    {
        float k = Mathf.Min(0.45f, rippleSpeed * rippleSpeed * dt * dt / (cs * cs));
        float band = n * 0.1f;

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                float c = rip[i, j];
                float l = i > 0 ? rip[i - 1, j] : 0f;
                float r = i < n - 1 ? rip[i + 1, j] : 0f;
                float d = j > 0 ? rip[i, j - 1] : 0f;
                float u = j < n - 1 ? rip[i, j + 1] : 0f;

                float v = (c - ripPrev[i, j]) * rippleDamping;
                float nv = c + v + k * (l + r + d + u - 4f * c);

                // біля краю брижі гаснуть, щоб не відбивались назад
                int m = Mathf.Min(Mathf.Min(i, j), Mathf.Min(n - 1 - i, n - 1 - j));
                if (m < band) nv *= Mathf.Lerp(0.7f, 1f, m / band);

                ripPrev[i, j] = Mathf.Clamp(nv, -maxRipple, maxRipple);   // нове значення пишемо в prev
            }
        }
        var t = rip; rip = ripPrev; ripPrev = t;
    }

    // Збурення води (гаусова пляма). amount у метрах: + підняти, − опустити.
    public void AddDisturbance(Vector3 worldPos, float radius, float amount)
    {
        if (rip == null || Mathf.Abs(amount) < 0.00001f) return;

        float sigma = Mathf.Max(radius * 0.6f, cs * 0.8f);
        float fi = (worldPos.x - ox) / cs - 0.5f;
        float fj = (worldPos.z - oz) / cs - 0.5f;
        int reach = Mathf.CeilToInt(sigma * 2.5f / cs);
        int ci = Mathf.RoundToInt(fi), cj = Mathf.RoundToInt(fj);
        float inv = 1f / (2f * sigma * sigma);

        for (int i = ci - reach; i <= ci + reach; i++)
        {
            if (i < 0 || i >= n) continue;
            for (int j = cj - reach; j <= cj + reach; j++)
            {
                if (j < 0 || j >= n) continue;
                float dx = (i - fi) * cs, dz = (j - fj) * cs;
                float w = Mathf.Exp(-(dx * dx + dz * dz) * inv);
                rip[i, j] = Mathf.Clamp(rip[i, j] + amount * w, -maxRipple, maxRipple);
            }
        }
    }

    // ───────────── висота води ─────────────

    float CrestMean()
    {
        if (Mathf.Approximately(sharpCache, crestSharpness)) return meanCache;
        sharpCache = crestSharpness;
        const int N = 64;
        float s = 0f;
        for (int k = 0; k < N; k++)
            s += 2f * Mathf.Pow(0.5f + 0.5f * Mathf.Sin(k * 2f * Mathf.PI / N), crestSharpness) - 1f;
        meanCache = s / N;
        return meanCache;
    }

    float BaseHeight(float x, float z)
    {
        float t = Time.time;
        float mean = CrestMean();
        float h = 0f;
        for (int i = 0; i < waves.Length; i++)
        {
            Wave w = waves[i];
            Vector2 d = w.direction.sqrMagnitude > 0.0001f ? w.direction.normalized : Vector2.right;
            float k = 2f * Mathf.PI / Mathf.Max(0.01f, w.wavelength);
            float s = Mathf.Max(0f, 0.5f + 0.5f * Mathf.Sin(k * (d.x * x + d.y * z - w.speed * t)));
            h += w.amplitude * (2f * Mathf.Pow(s, crestSharpness) - 1f - mean);
        }
        return h;
    }

    float R(int i, int j)
    {
        return (i < 0 || j < 0 || i >= n || j >= n) ? 0f : rip[i, j];
    }

    float SampleRipple(float x, float z)
    {
        if (rip == null) return 0f;
        float fi = (x - ox) / cs - 0.5f, fj = (z - oz) / cs - 0.5f;
        int i0 = Mathf.FloorToInt(fi), j0 = Mathf.FloorToInt(fj);
        float tx = fi - i0, tz = fj - j0;
        float a = R(i0, j0), b = R(i0 + 1, j0), c = R(i0, j0 + 1), d = R(i0 + 1, j0 + 1);
        return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), tz);
    }

    float Fade(float x, float z)
    {
        if (half <= 0f) return 1f;
        float d = Mathf.Max(Mathf.Abs(x - cx), Mathf.Abs(z - cz)) / half;   // 0 центр .. 1 край
        float f = Mathf.Clamp01((1f - d) / Mathf.Max(0.01f, edgeFade));
        return f * f * (3f - 2f * f);
    }

    // Гладка висота води у світових координатах (хвилі + брижі). Для плавання.
    public float GetHeight(float x, float z)
    {
        return transform.position.y + (BaseHeight(x, z) + SampleRipple(x, z)) * Fade(x, z);
    }

    public Vector2 GetSlope(float x, float z)
    {
        const float e = 0.3f;
        float dx = GetHeight(x + e, z) - GetHeight(x - e, z);
        float dz = GetHeight(x, z + e) - GetHeight(x, z - e);
        return new Vector2(dx, dz) / (2f * e);
    }

    float Quantize(float h)
    {
        if (!blocky || heightStep <= 0.0001f) return h;
        return Mathf.Round(h / heightStep) * heightStep;
    }

    // ───────────── меш ─────────────

    void Rebuild()
    {
        if (rip == null) return;

        if (blocky)
        {
            if (centerH == null || centerH.GetLength(0) != n) centerH = new float[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    float x = ox + (i + 0.5f) * cs, z = oz + (j + 0.5f) * cs;
                    centerH[i, j] = Quantize((BaseHeight(x, z) + rip[i, j]) * Fade(x, z));
                }
        }
        else
        {
            if (cornerH == null || cornerH.GetLength(0) != n + 1) cornerH = new float[n + 1, n + 1];
            for (int i = 0; i <= n; i++)
                for (int j = 0; j <= n; j++)
                {
                    float x = ox + i * cs, z = oz + j * cs;
                    float rv = (R(i - 1, j - 1) + R(i, j - 1) + R(i - 1, j) + R(i, j)) * 0.25f;
                    cornerH[i, j] = (BaseHeight(x, z) + rv) * Fade(x, z);
                }
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
        mesh.bounds = new Bounds(new Vector3(cx - px, 0f, cz - pz), new Vector3(n * cs, maxRipple * 2f + 10f, n * cs));
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