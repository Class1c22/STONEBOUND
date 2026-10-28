using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

// Глобальні частинки вітру для ВСІХ рівнів: одна пара систем частинок, що переживає завантаження сцен
// (як WindSystem). Створюється сама, нічого розставляти в сценах не треба.
//
// Як це працює:
//  * Системи їздять разом із каменем: спавняться в прямокутному обсязі навколо нього на висоті землі й
//    летять туди, куди дме глобальний WindSystem (напрям і сила, ті самі що для каменя й трави).
//  * Усі розміри в РАДІУСАХ КАМЕНЯ (rock.Radius), тож на 2 і 3 рівнях, де камінь більший, частинки теж
//    більші, летять далі й швидше. Налаштовуєш один раз.
//  * Вигляд: якщо на рівні 1 у GrassField призначені windParticlesA/B, скрипт один раз клонує їх і далі
//    використовує ці клони на всіх рівнях (твій матеріал і налаштування зберігаються). Якщо їх немає
//    (наприклад, запустив одразу 2 рівень), робляться прості м'які частинки.
//  * Поки на сцені живе GrassField зі своїми частинками й увімкненим вітром (рівень 1), цей скрипт мовчить,
//    щоб частинки не дублювались. Як тільки такої трави немає, вмикається цей скрипт.
//  * Налаштувати: поклади компонент на порожній об'єкт у першій сцені. Інакше береться стандартне.
[DefaultExecutionOrder(50)]
public class WindParticles : MonoBehaviour
{
    static WindParticles instance;
    static bool quitting;

    public static WindParticles Instance
    {
        get
        {
            if (instance == null && !quitting)
                new GameObject("WindParticles").AddComponent<WindParticles>();   // Awake сам виставить instance
            return instance;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { instance = null; quitting = false; }

    // Створюємо себе автоматично після завантаження першої сцени
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (instance == null && !quitting) { var _ = Instance; }
    }

    [Header("Загальне")]
    public bool particlesEnabled = true;
    [Tooltip("Клонувати частинки з GrassField (рівень 1), коли вони там є, і використовувати їх на всіх рівнях")]
    public bool copyLookFromGrass = true;
    [Tooltip("Не вмикатись, поки GrassField на сцені сам крутить свої частинки (щоб не було подвійних)")]
    public bool yieldToGrassField = true;

    [Header("Твої системи частинок (слоти)")]
    [Tooltip("Система A: постійний вітер. Можна кинути префаб або об'єкт зі сцени. Якщо порожньо, береться з GrassField, а якщо й там немає, прості точки")]
    public ParticleSystem windParticlesA;
    [Tooltip("Система B: лише сильні пориви (листя, пил). Те саме правило")]
    public ParticleSystem windParticlesB;

    [Header("Обсяг (у радіусах каменя)")]
    [Tooltip("Ширина зони випуску навколо каменя")]
    public float areaInRockRadii = 40f;
    [Tooltip("Товщина зони випуску по висоті")]
    public float thicknessInRockRadii = 4f;
    [Tooltip("Висота системи A (постійний вітер) над землею")]
    public float heightARockRadii = 0.8f;
    [Tooltip("Висота системи B (лише сильні пориви) над землею")]
    public float heightBRockRadii = 2.5f;
    [Tooltip("Розмір частинки. 0 = лишити розмір із системи")]
    public float particleSizeInRockRadii = 0.15f;

    [Header("Частота й швидкість")]
    public float baseRateA = 70f;
    public float baseRateB = 30f;
    [Tooltip("У штиль система A лишає цю частку частоти")]
    [Range(0f, 1f)] public float minEmission = 0.15f;
    [Tooltip("Система B мовчить, поки сила вітру нижча за це")]
    [Range(0f, 1f)] public float bMinStrength = 0.45f;
    [Tooltip("Швидкість частинок у радіусах каменя за секунду: у штиль і при найсильнішому вітрі")]
    public float speedMinRockRadii = 3f;
    public float speedMaxRockRadii = 12f;

    [Header("Турбулентність")]
    [Tooltip("Наскільки місцевий напрям відхиляється від загального, градусів")]
    [Range(0f, 90f)] public float directionJitter = 30f;
    [Tooltip("Розмір завихрень у радіусах каменя")]
    public float eddySizeRockRadii = 8f;

    class Fx
    {
        public ParticleSystem ps;
        public float baseRate;
        public bool cloned;
        public float baseSizeMult = 1f;   // розмір твоєї системи на момент клонування
        public float refR = 1f;           // радіус каменя на той момент
        public float lastR = -1f;
        public ParticleSystem.Particle[] buf;
    }

    readonly Fx fxA = new Fx();
    readonly Fx fxB = new Fx();

    RockAbsorber rock;
    float findTimer, pollTimer;
    float baseY;
    bool baseYInit;
    float gustT;
    Vector2 gustOffset;
    bool grassHandles;

    Material defaultMat;
    Texture2D dotTex;
    static readonly RaycastHit[] hits = new RaycastHit[16];

    // ---------- життєвий цикл ----------

    void Awake()
    {
        if (instance != null && instance != this) { Destroy(gameObject); return; }
        instance = this;
        transform.SetParent(null, true);
        DontDestroyOnLoad(gameObject);

        fxA.baseRate = baseRateA;
        fxB.baseRate = baseRateB;
        fxA.ps = MakeDefault("WindA", 600, 6f);
        fxB.ps = MakeDefault("WindB", 300, 7f);
        PrepareSystem(fxA);
        PrepareSystem(fxB);

        // слоти мають пріоритет над GrassField і простими точками
        if (windParticlesA != null) Adopt(fxA, windParticlesA);
        if (windParticlesB != null) Adopt(fxB, windParticlesB);
    }

    void OnApplicationQuit() { quitting = true; }

    void OnDestroy()
    {
        if (instance == this) instance = null;
        if (defaultMat != null) Destroy(defaultMat);
        if (dotTex != null) Destroy(dotTex);
    }

    // ---------- частинки за замовчуванням ----------

    Material GetDefaultMaterial()
    {
        if (defaultMat != null) return defaultMat;

        // м'яка кругла точка
        dotTex = new Texture2D(32, 32, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        for (int y = 0; y < 32; y++)
        for (int x = 0; x < 32; x++)
        {
            float dx = (x - 15.5f) / 15.5f, dy = (y - 15.5f) / 15.5f;
            float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
            dotTex.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
        }
        dotTex.Apply();

        Shader sh = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (sh != null)
        {
            defaultMat = new Material(sh) { name = "WindParticleMat" };
            defaultMat.SetFloat("_Surface", 1f);
            defaultMat.SetFloat("_Blend", 0f);
            defaultMat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            defaultMat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            defaultMat.SetFloat("_ZWrite", 0f);
            defaultMat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            defaultMat.SetOverrideTag("RenderType", "Transparent");
            defaultMat.renderQueue = (int)RenderQueue.Transparent;
            if (defaultMat.HasProperty("_BaseMap")) defaultMat.SetTexture("_BaseMap", dotTex);
            if (defaultMat.HasProperty("_MainTex")) defaultMat.SetTexture("_MainTex", dotTex);
        }
        else
        {
            defaultMat = new Material(Shader.Find("Sprites/Default")) { name = "WindParticleMat" };
            defaultMat.mainTexture = dotTex;
        }
        return defaultMat;
    }

    ParticleSystem MakeDefault(string name, int maxParticles, float lifetime)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.loop = true;
        main.playOnAwake = false;
        main.startLifetime = lifetime;
        main.startSpeed = 0f;
        main.startSize = 0.3f;
        main.maxParticles = maxParticles;
        main.gravityModifier = 0f;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startColor = new Color(1f, 1f, 1f, 0.5f);

        var em = ps.emission;
        em.rateOverTime = fxA.baseRate;

        var col = ps.colorOverLifetime;
        col.enabled = true;
        var g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.2f), new GradientAlphaKey(1f, 0.75f), new GradientAlphaKey(0f, 1f) });
        col.color = g;

        var r = go.GetComponent<ParticleSystemRenderer>();
        r.sharedMaterial = GetDefaultMaterial();
        r.renderMode = ParticleSystemRenderMode.Billboard;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        return ps;
    }

    // Налаштування, однакові для будь-яких систем (власних і клонів): рухом керує вітер із коду
    void PrepareSystem(Fx s)
    {
        ParticleSystem ps = s.ps;
        if (ps == null) return;

        var main = ps.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startSpeed = 0f;

        var lim = ps.limitVelocityOverLifetime; lim.enabled = false;
        var vol = ps.velocityOverLifetime;      vol.enabled = false;
        var frc = ps.forceOverLifetime;         frc.enabled = false;
        var inh = ps.inheritVelocity;           inh.enabled = false;
        var ext = ps.externalForces;            ext.enabled = false;

        int need = Mathf.Max(1, main.maxParticles);
        if (s.buf == null || s.buf.Length < need) s.buf = new ParticleSystem.Particle[need];
        s.lastR = -1f;
    }

    // ---------- клонування вигляду з GrassField ----------

    // Один раз бере частинки з GrassField і підміняє ними прості. Клони живуть у цьому об'єкті й переживають сцену.
    void TryCopyFromGrass(GrassField gf)
    {
        if (!copyLookFromGrass || gf == null) return;
        if (rock == null) return;   // потрібен радіус каменя, щоб далі масштабувати розмір частинок
        if (!fxA.cloned && gf.windParticlesA != null) Adopt(fxA, gf.windParticlesA);
        if (!fxB.cloned && gf.windParticlesB != null) Adopt(fxB, gf.windParticlesB);
    }

    void Adopt(Fx s, ParticleSystem template)
    {
        var go = Instantiate(template.gameObject, transform);
        go.name = template.gameObject.name + " (Global)";
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.SetActive(true);

        var ps = go.GetComponent<ParticleSystem>();
        if (ps == null) { Destroy(go); return; }

        // оригінал зі сцени вимикаємо, щоб він не крутився поруч із клоном (префаби не чіпаємо)
        // (якщо ту саму систему веде GrassField, лишаємо її: на рівні 1 нею керує трава)
        GrassField gf = FindFirstObjectByType<GrassField>();
        bool usedByGrass = gf != null && (gf.windParticlesA == template || gf.windParticlesB == template);
        if (!usedByGrass && template.gameObject.scene.IsValid()) template.gameObject.SetActive(false);

        if (s.ps != null) Destroy(s.ps.gameObject);
        s.ps = ps;
        s.cloned = true;
        s.baseSizeMult = Mathf.Max(0.0001f, ps.main.startSizeMultiplier);
        s.refR = rock != null ? Mathf.Max(0.01f, rock.Radius) : -1f;   // -1: візьмемо, коли з'явиться камінь
        s.lastR = -1f;
        s.baseRate = Mathf.Max(0.01f, ps.emission.rateOverTimeMultiplier);
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        PrepareSystem(s);
    }

    // ---------- камінь і земля ----------

    void FindRock()
    {
        if (rock != null) return;
        findTimer -= Time.deltaTime;
        if (findTimer > 0f) return;
        findTimer = 0.5f;

        RockAbsorber[] all = FindObjectsByType<RockAbsorber>(FindObjectsSortMode.None);
        if (all.Length == 0) return;
        Scene active = SceneManager.GetActiveScene();
        rock = all[0];
        for (int i = 0; i < all.Length; i++)
            if (all[i].gameObject.scene == active) { rock = all[i]; break; }
    }

    // Під час переходу між сценами камінь той самий об'єкт, але на всяк випадок перевіряємо активну сцену
    bool RockValid()
    {
        if (rock == null) return false;
        return true;
    }

    float GroundY(Vector3 focus, float radius)
    {
        Vector3 from = focus + Vector3.up * (radius * 20f);
        int n = Physics.RaycastNonAlloc(from, Vector3.down, hits, radius * 60f, ~0, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue, y = focus.y - radius;
        for (int i = 0; i < n; i++)
        {
            RaycastHit h = hits[i];
            if (h.collider.attachedRigidbody != null) continue;
            if (h.collider.GetComponent<VoxelChunk>() != null) continue;
            if (h.distance < best) { best = h.distance; y = h.point.y; }
        }
        return y;
    }

    // ---------- щокадру ----------

    void LateUpdate()
    {
        float dt = Time.deltaTime;

        FindRock();

        // клонуємо вигляд, поки GrassField живий; дивимось, чи він сам крутить частинки
        pollTimer -= dt;
        if (pollTimer <= 0f)
        {
            pollTimer = 0.4f;
            GrassField gf = FindFirstObjectByType<GrassField>();
            TryCopyFromGrass(gf);
            grassHandles = yieldToGrassField && gf != null && gf.windEnabled && gf.particlesEnabled && gf.windParticlesA != null;
        }

        WindSystem wind = WindSystem.Instance;
        bool on = particlesEnabled && wind != null && wind.Strength01 > 0f && !grassHandles;

        if (!RockValid()) rock = null;

        float R = 1f;
        Vector3 focus;
        if (rock != null) { R = Mathf.Max(0.01f, rock.Radius); focus = rock.WorldCenter; }
        else
        {
            Camera cam = Camera.main;
            if (cam == null) { SetEmission(fxA, false); SetEmission(fxB, false); return; }
            focus = cam.transform.position;
        }

        if (!on)
        {
            SetEmission(fxA, false);
            SetEmission(fxB, false);
            return;
        }

        float strength = wind.Strength01;
        Vector3 dir = wind.Direction;

        // пориви: поле шуму зсувається за вітром
        gustT += dt;
        gustOffset += new Vector2(dir.x, dir.z) * (speedMinRockRadii * R * (0.4f + strength) * dt);

        float targetY = GroundY(focus, R);
        baseY = baseYInit ? Mathf.Lerp(baseY, targetY, 1f - Mathf.Exp(-4f * dt)) : targetY;
        baseYInit = true;

        float kA = Mathf.Lerp(minEmission, 1f, strength);
        float kB = Mathf.InverseLerp(bMinStrength, 1f, strength);

        UpdateFx(fxA, heightARockRadii, kA, R, focus, dir);
        UpdateFx(fxB, heightBRockRadii, kB, R, focus, dir);

        SteerFx(fxA, R, wind, dt);
        SteerFx(fxB, R, wind, dt);
    }

    static void SetEmission(Fx s, bool on)
    {
        if (s.ps == null) return;
        var em = s.ps.emission;
        em.enabled = on;
    }

    void UpdateFx(Fx s, float heightRadii, float emissionK, float R, Vector3 focus, Vector3 dir)
    {
        ParticleSystem ps = s.ps;
        if (ps == null) return;

        var em = ps.emission;
        em.enabled = emissionK > 0.001f;
        if (!em.enabled) return;

        // розмір частинки змінюється разом із каменем
        // (твої системи: їхній розмір × R / R на момент клонування; прості: particleSizeInRockRadii × R)
        if (s.cloned && s.refR <= 0f) s.refR = R;
        if (Mathf.Abs(R - s.lastR) > s.lastR * 0.01f)
        {
            s.lastR = R;
            var m = ps.main;
            if (s.cloned) m.startSizeMultiplier = s.baseSizeMult * (R / s.refR);
            else if (particleSizeInRockRadii > 0f) m.startSize = particleSizeInRockRadii * R;
        }

        float w = Mathf.Max(1f, areaInRockRadii) * R;
        float h = Mathf.Max(0.2f, thicknessInRockRadii) * R;

        var sh = ps.shape;
        sh.enabled = true;
        sh.shapeType = ParticleSystemShapeType.Box;
        sh.position = Vector3.zero;
        sh.rotation = Vector3.zero;
        sh.scale = new Vector3(w, h, w);

        Vector3 pos = new Vector3(focus.x, baseY, focus.z) + Vector3.up * (heightRadii * R);
        ps.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(dir, Vector3.up));

        // не більше, ніж вміщує maxParticles за час життя
        float life = Mathf.Max(0.1f, ps.main.startLifetime.constantMax);
        float rate = Mathf.Min(s.baseRate * Mathf.Clamp01(emissionK), ps.main.maxParticles / life);
        em.rateOverTimeMultiplier = rate;

        if (!ps.isPlaying) ps.Play();
    }

    // Рухає кожну живу частинку за вітром (горизонтально; вертикаль не чіпаємо)
    void SteerFx(Fx s, float R, WindSystem wind, float dt)
    {
        ParticleSystem ps = s.ps;
        if (ps == null) return;

        int max = ps.main.maxParticles;
        if (s.buf == null || s.buf.Length < max) s.buf = new ParticleSystem.Particle[max];

        int n = ps.GetParticles(s.buf);
        if (n == 0) return;

        float baseAng = wind.AngleDegrees;
        float strength = wind.Strength01;
        float eddy = Mathf.Max(0.5f, eddySizeRockRadii) * R;
        float vMin = speedMinRockRadii * R, vMax = speedMaxRockRadii * R;

        for (int i = 0; i < n; i++)
        {
            ParticleSystem.Particle p = s.buf[i];
            Vector3 pos = p.position;

            float local = (Mathf.PerlinNoise(pos.x / eddy + gustT * 0.15f + 31.7f, pos.z / eddy + 17.3f) - 0.5f) * 2f;
            float gust = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.25f, 0.75f,
                Mathf.PerlinNoise((pos.x - gustOffset.x) / (eddy * 1.2f) + 5.1f, (pos.z - gustOffset.y) / (eddy * 1.2f) + 9.9f)));
            float str = Mathf.Clamp01(Mathf.Lerp(0.15f, 1f, gust) * strength);

            float ang = (baseAng + local * directionJitter) * Mathf.Deg2Rad;
            float speed = Mathf.Lerp(vMin, vMax, str);

            p.position = pos + new Vector3(Mathf.Sin(ang), 0f, Mathf.Cos(ang)) * (speed * dt);
            Vector3 v = p.velocity;
            v.x = 0f;
            v.z = 0f;
            p.velocity = v;
            s.buf[i] = p;
        }
        ps.SetParticles(s.buf, n);
    }
}