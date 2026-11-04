using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Можна ставити де завгодно (rotation 0, scale 1). Y об'єкта = базовий рівень води.
// Центр води сам іде за камерою. Камінь збурює воду через AddDisturbance (див. FloatingRock).
// Далі від камери вода будується кільцями з дедалі більшими клітинками (LOD).
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
    [Tooltip("Клітинок на сторону основної (дрібної) сітки. Округлюється до кратного 4")]
    public int cellsPerSide = 100;
    public float cellSize = 1f;
    public float updateInterval = 0.033f;
    [Tooltip("Частка зовнішнього кільця, де хвилі згасають до рівної води")]
    [Range(0.02f, 0.6f)] public float edgeFade = 0.25f;

    [Header("Далекі кільця (LOD)")]
    [Tooltip("Скільки кілець навколо основної сітки. Кожне вдвічі грубіше й вдвічі більше. 0 = без кілець")]
    [Range(0, 5)] public int lodRings = 3;
    [Tooltip("Як часто оновлюється кожне далеке кільце, с")]
    public float outerUpdateInterval = 0.1f;
    [Tooltip("Глибина стінок на краях кілець, щоб не було щілин")]
    public float skirtDepth = 1f;

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

    Mesh[] meshes;
    float[][,] centerHs, cornerHs;

    float timer, outerTimer, findTimer;
    int nextRing;
    Transform camT;

    int n;
    float cs;
    int originI, originJ;
    float ox, oz, cx, cz, half;
    float px, pz;

    float[,] rip, ripPrev, scratch;

    float meanCache, sharpCache = -1f;

    readonly List<Vector3> verts = new List<Vector3>();
    readonly List<Vector2> uvs = new List<Vector2>();
    readonly List<int> tris = new List<int>();

    void Awake()
    {
        Instance = this;
        lodRings = Mathf.Clamp(lodRings, 0, 5);
        int L = lodRings;

        meshes = new Mesh[L + 1];
        centerHs = new float[L + 1][,];
        cornerHs = new float[L + 1][,];

        var mainRend = GetComponent<MeshRenderer>();
        mainRend.shadowCastingMode = ShadowCastingMode.Off;
        mainRend.receiveShadows = false;

        meshes[0] = new Mesh { name = "WaterMesh", indexFormat = IndexFormat.UInt32 };
        GetComponent<MeshFilter>().sharedMesh = meshes[0];

        for (int k = 1; k <= L; k++)
        {
            var go = new GameObject("WaterLOD" + k);
            go.transform.SetParent(transform, false);
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mainRend.sharedMaterial;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            meshes[k] = new Mesh { name = "WaterMeshLOD" + k, indexFormat = IndexFormat.UInt32 };
            mf.sharedMesh = meshes[k];
        }

        UpdateGrid();
        for (int k = 0; k <= L; k++) BuildRing(k);
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void LateUpdate()
    {
        float dt = Time.deltaTime;
        timer += dt;
        outerTimer += dt;

        if (timer >= updateInterval)
        {
            int steps = Mathf.Clamp(Mathf.FloorToInt(timer / updateInterval), 1, 3);
            timer = 0f;

            bool moved = UpdateGrid();
            for (int s = 0; s < steps; s++) Step(updateInterval);
            BuildRing(0);

            if (moved)
            {
                // центр зсунувся: перебудувати всі кільця одразу, щоб не було щілин
                for (int k = 1; k <= lodRings; k++) BuildRing(k);
                outerTimer = 0f;
                return;
            }
        }

        // далекі кільця оновлюються по черзі
        if (lodRings > 0 && outerTimer >= outerUpdateInterval / lodRings)
        {
            outerTimer = 0f;
            nextRing = nextRing % lodRings + 1;
            BuildRing(nextRing);
        }
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

    // повертає true, якщо центр сітки зсунувся
    bool UpdateGrid()
    {
        int newN = Mathf.Max(8, cellsPerSide / 4 * 4);   // кратно 4: отвір кільця збігається з сусіднім
        cs = Mathf.Max(0.05f, cellSize);

        Vector3 pos = transform.position;
        px = pos.x;
        pz = pos.z;

        Transform ft = Focus();
        Vector3 f = ft != null ? ft.position : pos;

        // центр прив'язаний до найгрубішої клітинки, тож усі кільця вирівняні
        int m = 1 << lodRings;
        int ci = Mathf.RoundToInt(f.x / (cs * m)) * m;
        int cj = Mathf.RoundToInt(f.z / (cs * m)) * m;
        int ni = ci - newN / 2;
        int nj = cj - newN / 2;

        bool moved = false;
        if (rip == null || newN != n)
        {
            n = newN;
            rip = new float[n, n];
            ripPrev = new float[n, n];
            scratch = new float[n, n];
            for (int k = 0; k < centerHs.Length; k++) { centerHs[k] = null; cornerHs[k] = null; }
            originI = ni;
            originJ = nj;
            moved = true;
        }
        else if (ni != originI || nj != originJ)
        {
            int dx = ni - originI, dz = nj - originJ;
            Shift(rip, dx, dz);
            Shift(ripPrev, dx, dz);
            originI = ni;
            originJ = nj;
            moved = true;
        }

        ox = originI * cs;
        oz = originJ * cs;
        half = n * cs * m * 0.5f;     // півсторона найбільшого кільця
        cx = ci * cs;
        cz = cj * cs;
        return moved;
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

    // кільце k: клітинка = cs * 2^k, сітка n×n; для k>0 середина (n/2 × n/2) порожня: там сусіднє дрібніше кільце
    bool InHole(int k, int i, int j)
    {
        if (k == 0) return false;
        int q1 = n / 4, q3 = 3 * n / 4;
        return i >= q1 && i < q3 && j >= q1 && j < q3;
    }

    void BuildRing(int k)
    {
        if (rip == null || meshes == null || k >= meshes.Length) return;

        float csk = cs * (1 << k);
        float rox = cx - n * 0.5f * csk;
        float roz = cz - n * 0.5f * csk;
        int q1 = n / 4, q3 = 3 * n / 4;
        float sk = Mathf.Max(0.05f, skirtDepth);

        float[,] H = null, C = null;

        if (blocky)
        {
            if (centerHs[k] == null || centerHs[k].GetLength(0) != n) centerHs[k] = new float[n, n];
            H = centerHs[k];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    if (InHole(k, i, j)) continue;
                    float x = rox + (i + 0.5f) * csk, z = roz + (j + 0.5f) * csk;
                    float v = BaseHeight(x, z);
                    if (k == 0) v += rip[i, j];
                    H[i, j] = Quantize(v * Fade(x, z));
                }
        }
        else
        {
            if (cornerHs[k] == null || cornerHs[k].GetLength(0) != n + 1) cornerHs[k] = new float[n + 1, n + 1];
            C = cornerHs[k];
            for (int i = 0; i <= n; i++)
                for (int j = 0; j <= n; j++)
                {
                    if (k > 0 && i > q1 && i < q3 && j > q1 && j < q3) continue;   // всередині отвору
                    float x = rox + i * csk, z = roz + j * csk;
                    float v = BaseHeight(x, z);
                    if (k == 0) v += (R(i - 1, j - 1) + R(i, j - 1) + R(i - 1, j) + R(i, j)) * 0.25f;
                    C[i, j] = v * Fade(x, z);
                }
        }

        verts.Clear();
        uvs.Clear();
        tris.Clear();

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                if (InHole(k, i, j)) continue;

                float x0 = rox + i * csk, x1 = x0 + csk;
                float z0 = roz + j * csk, z1 = z0 + csk;
                float hx0, hx1, hz0, hz1;   // висота кожного з 4 країв (для стінок)

                if (blocky)
                {
                    float h = H[i, j];
                    hx0 = hx1 = hz0 = hz1 = h;
                    AddQuad(new Vector3(x0, h, z0), new Vector3(x0, h, z1),
                            new Vector3(x1, h, z1), new Vector3(x1, h, z0), 0);

                    if (walls)
                    {
                        if (i + 1 < n && !InHole(k, i + 1, j))
                        {
                            float hn = H[i + 1, j];
                            if (Mathf.Abs(h - hn) > 0.0001f)
                                WallX(x1, z0, z1, Mathf.Min(h, hn), Mathf.Max(h, hn), h > hn);
                        }
                        if (j + 1 < n && !InHole(k, i, j + 1))
                        {
                            float hn = H[i, j + 1];
                            if (Mathf.Abs(h - hn) > 0.0001f)
                                WallZ(z1, x0, x1, Mathf.Min(h, hn), Mathf.Max(h, hn), h > hn);
                        }
                    }
                }
                else
                {
                    float a = C[i, j], b = C[i, j + 1], c2 = C[i + 1, j + 1], d = C[i + 1, j];
                    AddQuad(new Vector3(x0, a, z0), new Vector3(x0, b, z1),
                            new Vector3(x1, c2, z1), new Vector3(x1, d, z0), 0);
                    hx0 = (a + b) * 0.5f;
                    hx1 = (d + c2) * 0.5f;
                    hz0 = (a + d) * 0.5f;
                    hz1 = (b + c2) * 0.5f;
                }

                // стінки-"спідниці" на краях кільця, щоб між кільцями не було щілин
                bool inI = i >= q1 && i < q3, inJ = j >= q1 && j < q3;
                if (i == n - 1) WallX(x1, z0, z1, hx1 - sk, hx1, true);
                if (i == 0)     WallX(x0, z0, z1, hx0 - sk, hx0, false);
                if (j == n - 1) WallZ(z1, x0, x1, hz1 - sk, hz1, true);
                if (j == 0)     WallZ(z0, x0, x1, hz0 - sk, hz0, false);
                if (k > 0)
                {
                    if (i == q1 - 1 && inJ) WallX(x1, z0, z1, hx1 - sk, hx1, true);
                    if (i == q3 && inJ)     WallX(x0, z0, z1, hx0 - sk, hx0, false);
                    if (j == q1 - 1 && inI) WallZ(z1, x0, x1, hz1 - sk, hz1, true);
                    if (j == q3 && inI)     WallZ(z0, x0, x1, hz0 - sk, hz0, false);
                }
            }
        }

        Mesh m = meshes[k];
        m.Clear();
        m.SetVertices(verts);
        m.SetUVs(0, uvs);
        m.SetTriangles(tris, 0);
        m.RecalculateNormals();   // вершини не спільні, тож нормалі пласкі
        m.bounds = new Bounds(new Vector3(cx - px, 0f, cz - pz),
                              new Vector3(n * csk, maxRipple * 2f + sk + 10f, n * csk));
    }

    // стінка в площині X (x = const), від lo до hi, дивиться в +X якщо facePos
    void WallX(float x, float z0, float z1, float lo, float hi, bool facePos)
    {
        if (facePos)
            AddQuad(new Vector3(x, lo, z0), new Vector3(x, hi, z0),
                    new Vector3(x, hi, z1), new Vector3(x, lo, z1), 1);
        else
            AddQuad(new Vector3(x, lo, z1), new Vector3(x, hi, z1),
                    new Vector3(x, hi, z0), new Vector3(x, lo, z0), 1);
    }

    // стінка в площині Z (z = const), дивиться в +Z якщо facePos
    void WallZ(float z, float x0, float x1, float lo, float hi, bool facePos)
    {
        if (facePos)
            AddQuad(new Vector3(x1, lo, z), new Vector3(x1, hi, z),
                    new Vector3(x0, hi, z), new Vector3(x0, lo, z), 2);
        else
            AddQuad(new Vector3(x0, lo, z), new Vector3(x0, hi, z),
                    new Vector3(x1, hi, z), new Vector3(x1, lo, z), 2);
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