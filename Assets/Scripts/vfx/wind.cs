using UnityEngine;
using UnityEngine.SceneManagement;

// Возить твої готові системи частинок вітру за каменем і ганяє частинки за вітром, як GrassField на 1 рівні.
// Нічого не створює саме, нічого не клонує й не міняє вигляд систем: працює лише з тими, що в слотах.
// Самих систем (вигляд, матеріал, розмір, швидкість випуску) скрипт не міняє: лише ставить їх на камінь,
// регулює частоту випуску від сили вітру й рухає живі частинки в бік вітру.
//
// Як підключити: повісь на порожній GameObject у сцені 2 (і в 3, якщо треба), перетягни свої системи у слоти A і B.
// Напрям і силу бере з глобального WindSystem, тож вітер той самий, що на 1 рівні й для каменя.
//
// Усі відстані тут у «клітинках», як у GrassField: клітинка = радіус каменя × Cell In Rock Radii.
// На 1 рівні клітинка це voxelSizeInRockRadii × grassScale = 2.5 × 5 = 12.5 радіусів каменя, тож
// з цим числом усе виглядає так само. Захочеш менші/більші області й швидкості: міняй лише Cell In Rock Radii.
[DefaultExecutionOrder(50)]
public class WindParticles : MonoBehaviour
{
    [Header("Твої системи частинок")]
    [Tooltip("A: постійний вітер (смужки, пилинки)")]
    public ParticleSystem windParticlesA;
    [Tooltip("B: лише сильні пориви (листя, пил)")]
    public ParticleSystem windParticlesB;
    public bool particlesEnabled = true;

    [Header("Камінь і масштаб")]
    [Tooltip("Порожньо = знайти в активній сцені")]
    public RockAbsorber rock;
    [Tooltip("Розмір клітинки в радіусах каменя. 12.5 = як на рівні 1")]
    public float cellInRockRadii = 12.5f;
    public int seed = 1;

    [Header("Зона випуску (у клітинках)")]
    [Tooltip("Ширина й довжина зони навколо каменя. На 1 рівні вийшло 56")]
    public float spawnAreaCells = 56f;
    [Tooltip("Товщина зони по висоті")]
    public float spawnThicknessCells = 3f;
    [Tooltip("Для якої ширини зони налаштована частота випуску у твоїх системах. Частота росте разом із площею")]
    public float refAreaCells = 14f;
    public bool keepDensity = true;
    [Tooltip("Висота систем над землею під каменем")]
    public float heightCellsA = 1.5f;
    public float heightCellsB = 4f;
    [Tooltip("Висота землі рахується променем униз (інакше від каменя)")]
    public bool followGround = true;

    [Header("Частота випуску")]
    [Range(0f, 1f)] public float minEmission = 0.15f;
    [Tooltip("Система B мовчить, поки сила вітру нижча за це")]
    [Range(0f, 1f)] public float bMinStrength = 0.45f;

    [Header("Рух частинок (клітинок за секунду)")]
    public float speedMinCells = 4f;
    public float speedMaxCells = 16f;
    [Tooltip("Кожна частинка бере вітер у своїй точці (пориви й завихрення), а не один загальний")]
    public bool localWind = true;
    public float gustSizeCells = 7f;
    public float gustSpeedCells = 5f;
    public float turbulenceSizeCells = 1.5f;
    public float turbulenceSpeed = 1.6f;
    [Range(0f, 1f)] public float turbulence = 0.35f;
    [Range(0f, 120f)] public float localDirectionJitter = 45f;

    class Fx
    {
        public ParticleSystem ps;
        public float baseRate;
        public ParticleSystem.Particle[] buf;
    }

    readonly Fx fxA = new Fx();
    readonly Fx fxB = new Fx();

    float cell, noiseO, windT, baseY, findTimer;
    float windAngle, windStrength;
    Vector2 gustOffset;
    bool baseYInit;
    static readonly RaycastHit[] hits = new RaycastHit[16];

    void Start()
    {
        noiseO = 10000f + seed * 17.3f;
        Init(fxA, windParticlesA);
        Init(fxB, windParticlesB);
    }

    // Як InitFx у GrassField: світові координати, нульова початкова швидкість, вимкнені модулі, що гасять вітер
    void Init(Fx s, ParticleSystem ps)
    {
        s.ps = ps;
        if (ps == null) return;

        var main = ps.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startSpeed = 0f;

        var lim = ps.limitVelocityOverLifetime; lim.enabled = false;
        var vol = ps.velocityOverLifetime;      vol.enabled = false;
        var frc = ps.forceOverLifetime;         frc.enabled = false;
        var inh = ps.inheritVelocity;           inh.enabled = false;
        var ext = ps.externalForces;            ext.enabled = false;

        s.baseRate = ps.emission.rateOverTimeMultiplier;
        s.buf = new ParticleSystem.Particle[Mathf.Max(1, main.maxParticles)];
        if (!ps.isPlaying) ps.Play();
    }

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

    static float Contrast(float v)
    {
        return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.25f, 0.75f, v));
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
            if (h.collider.GetComponent<VoxelChunk>() != null) continue;   // будинки й дерева землею не вважаємо
            if (h.distance < best) { best = h.distance; y = h.point.y; }
        }
        return y;
    }

    void LateUpdate()
    {
        FindRock();
        if (rock == null) return;

        // якщо ці самі системи веде GrassField (рівень 1), не заважаємо
        GrassField gf = FindFirstObjectByType<GrassField>();
        if (gf != null && gf.windEnabled && gf.particlesEnabled &&
            ((windParticlesA != null && gf.windParticlesA == windParticlesA) || (windParticlesB != null && gf.windParticlesB == windParticlesB)))
            return;

        float dt = Time.deltaTime;
        WindSystem ws = WindSystem.Instance;
        if (ws == null) return;

        windAngle = ws.AngleDegrees;
        windStrength = ws.Strength01;
        windT += dt;
        cell = Mathf.Max(0.001f, rock.Radius * cellInRockRadii);

        Vector3 dir = ws.Direction;
        gustOffset += new Vector2(dir.x, dir.z) * (gustSpeedCells * cell * (0.4f + windStrength) * dt);

        Vector3 focus = rock.WorldCenter;
        float targetY = followGround ? GroundY(focus, rock.Radius) : focus.y;
        baseY = baseYInit ? Mathf.Lerp(baseY, targetY, 1f - Mathf.Exp(-4f * dt)) : targetY;
        baseYInit = true;

        float kA = Mathf.Lerp(minEmission, 1f, windStrength);
        float kB = Mathf.InverseLerp(bMinStrength, 1f, windStrength);

        UpdateFx(fxA, heightCellsA, kA, focus, dir);
        UpdateFx(fxB, heightCellsB, kB, focus, dir);

        if (particlesEnabled)
        {
            Steer(fxA, dt);
            Steer(fxB, dt);
        }
    }

    // Ставить систему на камінь, дивиться за вітром, регулює частоту випуску
    void UpdateFx(Fx s, float heightCells, float emissionK, Vector3 focus, Vector3 dir)
    {
        ParticleSystem ps = s.ps;
        if (ps == null) return;

        var em = ps.emission;
        em.enabled = particlesEnabled;
        if (!particlesEnabled) return;

        var sh = ps.shape;
        sh.enabled = true;
        sh.shapeType = ParticleSystemShapeType.Box;
        sh.position = Vector3.zero;
        sh.rotation = Vector3.zero;
        sh.scale = new Vector3(Mathf.Max(1f, spawnAreaCells) * cell, Mathf.Max(0.2f, spawnThicknessCells) * cell, Mathf.Max(1f, spawnAreaCells) * cell);

        Vector3 pos = new Vector3(focus.x, baseY, focus.z) + Vector3.up * (heightCells * cell);
        ps.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(dir, Vector3.up));

        float rate = s.baseRate * Mathf.Clamp01(emissionK);
        if (keepDensity)
        {
            float ratio = spawnAreaCells / Mathf.Max(1f, refAreaCells);
            rate *= ratio * ratio;
        }
        float life = Mathf.Max(0.1f, ps.main.startLifetime.constantMax);
        rate = Mathf.Min(rate, ps.main.maxParticles / life);
        em.rateOverTimeMultiplier = rate;
        if (!ps.isPlaying) ps.Play();
    }

    // Вітер у точці: напрям (радіани від +Z) і місцева сила 0..1
    void WindAt(Vector3 p, out float ang, out float strength)
    {
        float o = noiseO;
        float gs = Mathf.Max(0.5f, gustSizeCells) * cell;
        float ts = Mathf.Max(0.2f, turbulenceSizeCells) * cell;

        float gust = Contrast(Mathf.PerlinNoise((p.x - gustOffset.x) / gs + o * 0.17f, (p.z - gustOffset.y) / gs + o * 0.23f));
        float turb = Mathf.PerlinNoise(p.x / ts + windT * turbulenceSpeed + o * 0.11f, p.z / ts - windT * turbulenceSpeed * 0.7f + o * 0.37f);
        float local = (Mathf.PerlinNoise(p.x / (gs * 0.8f) + windT * 0.15f + o * 0.43f, p.z / (gs * 0.8f) + o * 0.59f) - 0.5f) * 2f;

        strength = Mathf.Lerp(0.15f, 1f, gust) * windStrength;
        strength = Mathf.Clamp01(strength + (turb - 0.5f) * 2f * turbulence * 0.4f);
        ang = (windAngle + local * localDirectionJitter + (turb - 0.5f) * turbulence * 40f) * Mathf.Deg2Rad;
    }

    // Рухає кожну живу частинку за вітром (горизонтально, вертикаль не чіпаємо)
    void Steer(Fx s, float dt)
    {
        ParticleSystem ps = s.ps;
        if (ps == null) return;

        int max = ps.main.maxParticles;
        if (s.buf == null || s.buf.Length < max) s.buf = new ParticleSystem.Particle[max];

        int n = ps.GetParticles(s.buf);
        if (n == 0) return;

        float globalAng = windAngle * Mathf.Deg2Rad;
        for (int i = 0; i < n; i++)
        {
            ParticleSystem.Particle p = s.buf[i];
            float ang, str;
            if (localWind) WindAt(p.position, out ang, out str);
            else { ang = globalAng; str = windStrength; }

            float speed = Mathf.Lerp(speedMinCells, speedMaxCells, str) * cell;
            p.position += new Vector3(Mathf.Sin(ang), 0f, Mathf.Cos(ang)) * (speed * dt);

            Vector3 v = p.velocity;
            v.x = 0f;
            v.z = 0f;
            p.velocity = v;
            s.buf[i] = p;
        }
        ps.SetParticles(s.buf, n);
    }
}