using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Можна ставити де завгодно (rotation 0, scale 1). Y об'єкта = базовий рівень води.
// Центр води сам іде за камерою. Камінь збурює воду через AddDisturbance (див. FloatingRock).
// Далі від камери вода будується кільцями з дедалі більшими клітинками (LOD).
// Топологія мешів фіксована: кожен кадр оновлюються лише позиції вершин.
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
    [Tooltip("Як часто оновлюється основна сітка, с (0.05 = 20 разів/с)")]
    public float updateInterval = 0.05f;
    [Tooltip("Частка зовнішнього кільця, де хвилі згасають до рівної води")]
    [Range(0.02f, 0.6f)] public float edgeFade = 0.25f;

    [Header("Далекі кільця (LOD)")]
    [Tooltip("Скільки кілець навколо основної сітки. Кожне вдвічі грубіше й вдвічі більше. 0 = без кілець")]
    [Range(0, 5)] public int lodRings = 3;
    [Tooltip("Як часто оновлюється кожне далеке кільце, с")]
    public float outerUpdateInterval = 0.15f;
    [Tooltip("Глибина стінок на краях кілець, щоб не було щілин")]
    public float skirtDepth = 1f;

    [Header("Воксельний вигляд")]
    public float heightStep = 0.1f;
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

    // ───────────── внутрішній стан ─────────────

    class Ring
    {
        public Mesh mesh;
        public Vector3[] pos;
        public Vector2[] uv;
        public float[] H;
        public int cells, skirts;
        public int[] cI, cJ, sI, sJ;
        public byte[] sSide;
        public bool uvDirty;
    }

    Ring[] rings;

    float timer, outerTimer, findTimer, timeNow;
    int nextRing;
    Transform camT;

    int n;
    float cs;
    int originI, originJ;
    float ox, oz, cx, cz, half;
    float px, pz;

    float[] rip, ripPrev, scratch;
    bool sleeping = true;

    // таблиця форми хвилі
    const int LUT = 1024;
    readonly float[] lut = new float[LUT + 2];
    float lutSharp = -1f;
    float[] wAx, wAz, wBt, wAmp;
    int wc;

    // буфери для AddDisturbance
    readonly float[] wxBuf = new float[96];
    readonly float[] wzBuf = new float[96];

    const MeshUpdateFlags FastFlags =
        MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers;

    void Awake()
    {
        Instance = this;
        lodRings = Mathf.Clamp(lodRings, 0, 5);
        int L = lodRings;

        var mainRend = GetComponent<MeshRenderer>();
        ConfigRenderer(mainRend);

        rings = new Ring[L + 1];
        for (int k = 0; k <= L; k++)
        {
            var r = new Ring();
            r.mesh = new Mesh { name = k == 0 ? "WaterMesh" : "WaterMeshLOD" + k };
            rings[k] = r;

            if (k == 0)
            {
                GetComponent<MeshFilter>().sharedMesh = r.mesh;
            }
            else
            {
                var go = new GameObject("WaterLOD" + k);
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = r.mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mainRend.sharedMaterial;
                ConfigRenderer(mr);
            }
        }

        timeNow = Time.time;
        PrepareWaves();
        UpdateGrid();
        for (int k = 0; k <= L; k++) BuildRing(k);
    }

    static void ConfigRenderer(MeshRenderer r)
    {
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.lightProbeUsage = LightProbeUsage.Off;
        r.reflectionProbeUsage = ReflectionProbeUsage.Off;
        r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        r.allowOcclusionWhenDynamic = false;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void LateUpdate()
    {
        float dt = Time.deltaTime;
        timeNow = Time.time;
        timer += dt;
        outerTimer += dt;

        PrepareWaves();

        if (timer >= updateInterval)
        {
            float elapsed = timer;
            timer = 0f;

            bool moved = UpdateGrid();
            StepRipples(elapsed);
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

    // ───────────── хвилі (через таблицю) ─────────────

    void BuildLut()
    {
        lutSharp = crestSharpness;
        float mean = 0f;
        for (int i = 0; i < LUT; i++)
        {
            float p = i / (float)LUT;
            float s = Mathf.Max(0f, 0.5f + 0.5f * Mathf.Sin(p * 2f * Mathf.PI));
            float v = 2f * Mathf.Pow(s, crestSharpness) - 1f;
            lut[i] = v;
            mean += v;
        }
        mean /= LUT;
        for (int i = 0; i < LUT; i++) lut[i] -= mean;
        lut[LUT] = lut[0];
        lut[LUT + 1] = lut[1];
    }

    void PrepareWaves()
    {
        if (!Mathf.Approximately(lutSharp, crestSharpness)) BuildLut();

        wc = waves != null ? waves.Length : 0;
        if (wAx == null || wAx.Length != wc)
        {
            wAx = new float[wc]; wAz = new float[wc]; wBt = new float[wc]; wAmp = new float[wc];
        }
        for (int i = 0; i < wc; i++)
        {
            Wave w = waves[i];
            Vector2 d = w.direction.sqrMagnitude > 0.0001f ? w.direction.normalized : Vector2.right;
            float inv = 1f / Mathf.Max(0.01f, w.wavelength);
            wAx[i] = d.x * inv;
            wAz[i] = d.y * inv;
            wBt[i] = w.speed * inv;
            wAmp[i] = w.amplitude;
        }
    }

    float BaseHeight(float x, float z, float t)
    {
        float h = 0f;
        for (int i = 0; i < wc; i++)
        {
            float p = wAx[i] * x + wAz[i] * z - wBt[i] * t;
            p -= Mathf.Floor(p);
            float f = p * LUT;
            int i0 = (int)f;
            float fr = f - i0;
            float a = lut[i0], b = lut[i0 + 1];
            h += wAmp[i] * (a + (b - a) * fr);
        }
        return h;
    }

    // ───────────── сітка й брижі ─────────────

    bool InHole(int k, int i, int j)
    {
        if (k == 0) return false;
        int q1 = n / 4, q3 = 3 * n / 4;
        return i >= q1 && i < q3 && j >= q1 && j < q3;
    }

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
            int len = n * n;
            rip = new float[len];
            ripPrev = new float[len];
            scratch = new float[len];
            sleeping = true;
            originI = ni;
            originJ = nj;
            for (int k = 0; k < rings.Length; k++) CreateTopology(k);
            moved = true;
        }
        else if (ni != originI || nj != originJ)
        {
            if (!sleeping)
            {
                int dx = ni - originI, dz = nj - originJ;
                Shift(rip, dx, dz);
                Shift(ripPrev, dx, dz);
            }
            originI = ni;
            originJ = nj;
            for (int k = 0; k < rings.Length; k++) rings[k].uvDirty = true;
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
    void Shift(float[] a, int dx, int dz)
    {
        for (int i = 0; i < n; i++)
        {
            int si = i + dx;
            for (int j = 0; j < n; j++)
            {
                int sj = j + dz;
                scratch[i * n + j] = (si < 0 || sj < 0 || si >= n || sj >= n) ? 0f : a[si * n + sj];
            }
        }
        System.Array.Copy(scratch, a, n * n);
    }

    void StepRipples(float elapsed)
    {
        if (sleeping) return;
        float dt = Mathf.Max(0.005f, updateInterval);
        int steps = Mathf.Clamp(Mathf.FloorToInt(elapsed / dt), 1, 3);
        for (int s = 0; s < steps && !sleeping; s++) Step(dt);
    }

    void Step(float dt)
    {
        float kk = Mathf.Min(0.45f, rippleSpeed * rippleSpeed * dt * dt / (cs * cs));
        float band = n * 0.1f;
        float damp = rippleDamping;
        float maxAbs = 0f;

        for (int i = 0; i < n; i++)
        {
            int row = i * n;
            for (int j = 0; j < n; j++)
            {
                int id = row + j;
                float c = rip[id];
                float l = i > 0 ? rip[id - n] : 0f;
                float r = i < n - 1 ? rip[id + n] : 0f;
                float d = j > 0 ? rip[id - 1] : 0f;
                float u = j < n - 1 ? rip[id + 1] : 0f;

                float v = (c - ripPrev[id]) * damp;
                float nv = c + v + kk * (l + r + d + u - 4f * c);

                // біля краю брижі гаснуть, щоб не відбивались назад
                int e = Mathf.Min(Mathf.Min(i, j), Mathf.Min(n - 1 - i, n - 1 - j));
                if (e < band) nv *= Mathf.Lerp(0.7f, 1f, e / band);

                if (nv > maxRipple) nv = maxRipple;
                else if (nv < -maxRipple) nv = -maxRipple;

                ripPrev[id] = nv;   // нове значення пишемо в prev, потім міняємо місцями
                float a = nv < 0f ? -nv : nv;
                if (a > maxAbs) maxAbs = a;
            }
        }

        var t = rip; rip = ripPrev; ripPrev = t;

        // вода заспокоїлась: вимикаємо симуляцію до наступного збурення
        if (maxAbs < 0.015f)
        {
            System.Array.Clear(rip, 0, rip.Length);
            System.Array.Clear(ripPrev, 0, ripPrev.Length);
            sleeping = true;
        }
    }

    // Збурення води (гаусова пляма). amount у метрах: + підняти, − опустити.
    public void AddDisturbance(Vector3 worldPos, float radius, float amount)
    {
        if (rip == null || Mathf.Abs(amount) < 0.00001f) return;

        float sigma = Mathf.Max(radius * 0.6f, cs * 0.8f);
        float fi = (worldPos.x - ox) / cs - 0.5f;
        float fj = (worldPos.z - oz) / cs - 0.5f;
        int reach = Mathf.Min(40, Mathf.CeilToInt(sigma * 2.5f / cs));
        int ci = Mathf.RoundToInt(fi), cj = Mathf.RoundToInt(fj);
        float inv = 1f / (2f * sigma * sigma);
        int w = 2 * reach + 1;

        // гаусіана розділяється по осях: 2*w експонент замість w*w
        for (int a = 0; a < w; a++)
        {
            float dx = (ci - reach + a - fi) * cs;
            float dz = (cj - reach + a - fj) * cs;
            wxBuf[a] = Mathf.Exp(-dx * dx * inv);
            wzBuf[a] = Mathf.Exp(-dz * dz * inv);
        }

        sleeping = false;

        for (int a = 0; a < w; a++)
        {
            int i = ci - reach + a;
            if (i < 0 || i >= n) continue;
            float wx = wxBuf[a] * amount;
            if (Mathf.Abs(wx) < 0.00005f) continue;
            int row = i * n;
            for (int b = 0; b < w; b++)
            {
                int j = cj - reach + b;
                if (j < 0 || j >= n) continue;
                float v = rip[row + j] + wx * wzBuf[b];
                rip[row + j] = v > maxRipple ? maxRipple : (v < -maxRipple ? -maxRipple : v);
            }
        }
    }

    // ───────────── висота води (для плавання) ─────────────

    float R(int i, int j)
    {
        return (i < 0 || j < 0 || i >= n || j >= n) ? 0f : rip[i * n + j];
    }

    float SampleRipple(float x, float z)
    {
        if (rip == null || sleeping) return 0f;
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

    // Гладка висота води у світових координатах (хвилі + брижі)
    public float GetHeight(float x, float z)
    {
        return transform.position.y + (BaseHeight(x, z, Time.time) + SampleRipple(x, z)) * Fade(x, z);
    }

    public Vector2 GetSlope(float x, float z)
    {
        const float e = 0.3f;
        float dx = GetHeight(x + e, z) - GetHeight(x - e, z);
        float dz = GetHeight(x, z + e) - GetHeight(x, z - e);
        return new Vector2(dx, dz) / (2f * e);
    }

    // ───────────── меш: фіксована топологія ─────────────
    // Кожна клітинка: верх (4 вершини) + стінка в +X (4) + стінка в +Z (4) = 12 вершин.
    // Стінка має нульову висоту, якщо сусід на тій самій висоті.
    // Додатково "спідниці" на лівому/нижньому краях кільця й отвору (-X, -Z).

    void CreateTopology(int k)
    {
        Ring r = rings[k];
        int q1 = n / 4, q3 = 3 * n / 4;

        int cells = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (!InHole(k, i, j)) cells++;

        r.cells = cells;
        r.cI = new int[cells];
        r.cJ = new int[cells];
        var sI = new List<int>();
        var sJ = new List<int>();
        var sS = new List<byte>();

        int c = 0;
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                if (InHole(k, i, j)) continue;
                r.cI[c] = i;
                r.cJ[c] = j;
                c++;

                bool inI = i >= q1 && i < q3, inJ = j >= q1 && j < q3;
                if (i == 0 || (k > 0 && i == q3 && inJ)) { sI.Add(i); sJ.Add(j); sS.Add(0); }
                if (j == 0 || (k > 0 && j == q3 && inI)) { sI.Add(i); sJ.Add(j); sS.Add(1); }
            }
        }

        r.skirts = sI.Count;
        r.sI = sI.ToArray();
        r.sJ = sJ.ToArray();
        r.sSide = sS.ToArray();

        int quads = cells * 3 + r.skirts;
        int V = quads * 4;
        r.pos = new Vector3[V];
        r.uv = new Vector2[V];
        r.H = new float[n * n];

        var nor = new Vector3[V];
        var idx = new int[quads * 6];

        for (c = 0; c < cells; c++)
        {
            int b = c * 12;
            for (int q = 0; q < 4; q++)
            {
                nor[b + q] = Vector3.up;
                nor[b + 4 + q] = Vector3.right;
                nor[b + 8 + q] = Vector3.forward;
            }
        }
        int sb = cells * 12;
        for (int s = 0; s < r.skirts; s++)
        {
            Vector3 nn = r.sSide[s] == 0 ? Vector3.left : Vector3.back;
            for (int q = 0; q < 4; q++) nor[sb + s * 4 + q] = nn;
        }
        for (int q = 0; q < quads; q++)
        {
            int v0 = q * 4, t = q * 6;
            idx[t] = v0; idx[t + 1] = v0 + 1; idx[t + 2] = v0 + 2;
            idx[t + 3] = v0; idx[t + 4] = v0 + 2; idx[t + 5] = v0 + 3;
        }

        Mesh m = r.mesh;
        m.Clear();
        m.indexFormat = IndexFormat.UInt32;
        m.MarkDynamic();
        m.SetVertices(r.pos);
        m.SetNormals(nor);
        m.SetUVs(0, r.uv);
        m.SetIndices(idx, MeshTopology.Triangles, 0, false);
        r.uvDirty = true;
    }

    void BuildRing(int k)
    {
        if (rings == null || k >= rings.Length || rip == null) return;
        Ring r = rings[k];
        if (r.cI == null) return;

        float csk = cs * (1 << k);
        float rox = cx - n * 0.5f * csk;
        float roz = cz - n * 0.5f * csk;
        float sk = Mathf.Max(0.05f, skirtDepth);
        float t = timeNow;
        int cells = r.cells;
        int[] cI = r.cI, cJ = r.cJ;
        float[] H = r.H;

        // UV залежать від світової позиції: оновлюємо лише коли сітка зсунулась
        if (r.uvDirty)
        {
            r.uvDirty = false;
            float inv = 1f / Mathf.Max(0.01f, uvTileSize);
            Vector2[] U = r.uv;

            for (int c = 0; c < cells; c++)
            {
                int i = cI[c], j = cJ[c];
                float x0 = (rox + i * csk) * inv, x1 = x0 + csk * inv;
                float z0 = (roz + j * csk) * inv, z1 = z0 + csk * inv;
                int b = c * 12;
                U[b] = new Vector2(x0, z0);
                U[b + 1] = new Vector2(x0, z1);
                U[b + 2] = new Vector2(x1, z1);
                U[b + 3] = new Vector2(x1, z0);
                Vector2 mid = new Vector2((x0 + x1) * 0.5f, (z0 + z1) * 0.5f);   // стінки: суцільний колір клітинки
                for (int q = 4; q < 12; q++) U[b + q] = mid;
            }
            int sb = cells * 12;
            for (int s = 0; s < r.skirts; s++)
            {
                int i = r.sI[s], j = r.sJ[s];
                float mx = (rox + (i + 0.5f) * csk) * inv, mz = (roz + (j + 0.5f) * csk) * inv;
                Vector2 mid = new Vector2(mx, mz);
                for (int q = 0; q < 4; q++) U[sb + s * 4 + q] = mid;
            }
            r.mesh.SetUVs(0, U, 0, U.Length);
            r.mesh.bounds = new Bounds(new Vector3(cx - px, 0f, cz - pz),
                                       new Vector3(n * csk, 20f + maxRipple * 2f + sk, n * csk));
        }

        // висоти центрів клітинок
        float step = heightStep;
        bool quant = step > 0.0001f;
        float invStep = quant ? 1f / step : 0f;
        for (int c = 0; c < cells; c++)
        {
            int i = cI[c], j = cJ[c];
            float x = rox + (i + 0.5f) * csk, z = roz + (j + 0.5f) * csk;
            float v = BaseHeight(x, z, t);
            if (k == 0) v += rip[i * n + j];
            v *= Fade(x, z);
            if (quant) v = Mathf.Round(v * invStep) * step;
            H[i * n + j] = v;
        }

        // позиції вершин (локальні: відносно позиції об'єкта)
        Vector3[] P = r.pos;
        for (int c = 0; c < cells; c++)
        {
            int i = cI[c], j = cJ[c];
            float h = H[i * n + j];
            float x0 = rox + i * csk - px, x1 = x0 + csk;
            float z0 = roz + j * csk - pz, z1 = z0 + csk;
            int b = c * 12;

            P[b] = new Vector3(x0, h, z0);
            P[b + 1] = new Vector3(x0, h, z1);
            P[b + 2] = new Vector3(x1, h, z1);
            P[b + 3] = new Vector3(x1, h, z0);

            // стінка в +X
            float hn = (i + 1 < n && !InHole(k, i + 1, j)) ? H[(i + 1) * n + j] : h - sk;
            float lo = hn < h ? hn : h, hi = hn < h ? h : hn;
            P[b + 4] = new Vector3(x1, lo, z0);
            P[b + 5] = new Vector3(x1, hi, z0);
            P[b + 6] = new Vector3(x1, hi, z1);
            P[b + 7] = new Vector3(x1, lo, z1);

            // стінка в +Z
            hn = (j + 1 < n && !InHole(k, i, j + 1)) ? H[i * n + j + 1] : h - sk;
            lo = hn < h ? hn : h; hi = hn < h ? h : hn;
            P[b + 8] = new Vector3(x1, lo, z1);
            P[b + 9] = new Vector3(x1, hi, z1);
            P[b + 10] = new Vector3(x0, hi, z1);
            P[b + 11] = new Vector3(x0, lo, z1);
        }

        int sbase = cells * 12;
        for (int s = 0; s < r.skirts; s++)
        {
            int i = r.sI[s], j = r.sJ[s];
            float h = H[i * n + j];
            float x0 = rox + i * csk - px, x1 = x0 + csk;
            float z0 = roz + j * csk - pz, z1 = z0 + csk;
            int b = sbase + s * 4;

            if (r.sSide[s] == 0)   // -X
            {
                P[b] = new Vector3(x0, h - sk, z0);
                P[b + 1] = new Vector3(x0, h, z0);
                P[b + 2] = new Vector3(x0, h, z1);
                P[b + 3] = new Vector3(x0, h - sk, z1);
            }
            else                   // -Z
            {
                P[b] = new Vector3(x0, h - sk, z0);
                P[b + 1] = new Vector3(x0, h, z0);
                P[b + 2] = new Vector3(x1, h, z0);
                P[b + 3] = new Vector3(x1, h - sk, z0);
            }
        }

        r.mesh.SetVertices(P, 0, P.Length, FastFlags);
    }
}