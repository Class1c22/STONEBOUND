using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Повісь на камінь. Тримай E: камінь притягує до себе уламки й шматки, що менші за нього.
// Всмоктані фігури прилипають до поверхні як новий шар, а камінь (і його колайдер) росте.
// Кнопка R (скрипт RockBlast) віддає частину набраного: камінь меншає, а шматки розлітаються.
// Нижче початку поточної стадії камінь не меншає: на межі стадії R просто нічого не робить.
[RequireComponent(typeof(Rigidbody), typeof(SphereCollider))]
public class RockAbsorber : MonoBehaviour
{
    [Header("Всмоктування (кнопка E)")]
    [Tooltip("Дальність притягання (для початкового розміру каменя)")]
    public float suckRadius = 6f;
    public float pullSpeed = 14f;
    public float pullAcceleration = 60f;
    [Tooltip("Найбільша фігура відносно об'єму каменя (0.35 = до 35%)")]
    public float maxSizeRatio = 0.35f;

    [Header("Ріст")]
    [Tooltip("Візуальна модель каменя (дочірній об'єкт). Росте разом із колайдером")]
    public Transform coreVisual;
    [Tooltip("Множник приросту об'єму від поглинутих фігур (більше = камінь росте швидше)")]
    public float volumeGain = 1.5f;
    [Tooltip("Швидкість плавного росту, метрів за секунду")]
    public float growSpeed = 0.6f;
    [Tooltip("Швидкість зменшення після викиду (кнопка R), метрів за секунду")]
    public float shrinkSpeed = 6f;
    [Tooltip("Скільки фігур одночасно лишається видимими на поверхні (старі зникають, об'єм лишається)")]
    public int maxAttached = 300;

    [Header("Ріст за ярусами")]
    [Tooltip("Увімкнено: під час рівня камінь НЕ росте (розмір фіксований), об'єм лише накопичується. Камінь виростає одразу, коли досягнуто порогу наступної стадії (це робить TierTransition під час відльоту камери)")]
    public bool growOnlyOnTierUp = true;

    [Header("Стадії розміру (за зростанням radiusRatio)")]
    public List<RockStage> stages = DefaultStages();

    // Стандартні стадії. Радіус росте як корінь з об'єму: об'єм у стартових = radiusRatio^3.
    // Щоб підняти стадію, треба з'їсти (radiusRatio^3 - 1) / volumeGain стартових об'ємів.
    public static List<RockStage> DefaultStages()
    {
        return new List<RockStage>
        {
            new RockStage { name = "Камінь", radiusRatio = 1.0f, speedMultiplier = 1.0f, forceMultiplier = 1.0f,  destroyRadiusMultiplier = 1.0f,  absorbCostMultiplier = 1.0f },
            new RockStage { name = "Валун",  radiusRatio = 1.4f, speedMultiplier = 1.1f, forceMultiplier = 1.15f, destroyRadiusMultiplier = 1.2f,  absorbCostMultiplier = 1.5f },
            new RockStage { name = "Брила",  radiusRatio = 1.9f, speedMultiplier = 1.2f, forceMultiplier = 1.3f,  destroyRadiusMultiplier = 1.45f, absorbCostMultiplier = 2.0f },
            new RockStage { name = "Скеля",  radiusRatio = 2.6f, speedMultiplier = 1.3f, forceMultiplier = 1.5f,  destroyRadiusMultiplier = 1.75f, absorbCostMultiplier = 2.75f },
            new RockStage { name = "Гора",   radiusRatio = 3.5f, speedMultiplier = 1.4f, forceMultiplier = 1.7f,  destroyRadiusMultiplier = 2.1f,  absorbCostMultiplier = 3.5f },
        };
    }

    // Список стадій зберігається в сцені, тому зміна коду його не оновлює. Натисни на ⋮ компонента -> цей пункт.
    [ContextMenu("Скинути стадії до стандартних")]
    void ResetStagesToDefault()
    {
        stages = DefaultStages();
#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    [Tooltip("Якщо вимкнути, стадія не падає після R (тримається по найбільшому досягнутому розміру)")]
    public bool stageCanDrop = true;
    [Tooltip("Увімкнено: R віддає лише те, що набрано понад початок поточної стадії. Камінь не падає на попередню стадію, а на межі стадії R не діє")]
    public bool stageFloorOnBlast = true;
    [Tooltip("Показати стадію у куті екрана (Game view) і писати зміну стадії в Console")]
    public bool showDebug = true;

    [Header("Камера (необов'язково)")]
    public FollowCamera followCamera;
    // Базова відстань камери для стартового розміру (без урахування росту). Потрібна для переносу камери між сценами.
    public float BaseCameraDistance => baseCameraDistance;
    [Range(0f, 1f)] public float cameraFollowsGrowth = 0.8f;

    public float Radius => currentRadius;
    public float Volume => volume;
    public Vector3 WorldCenter => Center;

    // ---- стадії ----
    public event System.Action<int> StageChanged;
    public int Stage { get; private set; }
    public int StageCount => stages != null ? stages.Count : 0;
    public string StageName => Cur.name;
    public float SpeedMultiplier => Cur.speedMultiplier;
    public float ForceMultiplier => Cur.forceMultiplier;
    public float DestroyRadiusMultiplier => Cur.destroyRadiusMultiplier;
    // Скільки шматків треба на поточній стадії (більша стадія = більше шматків для того ж приросту об'єму)
    public float AbsorbCostMultiplier => Mathf.Max(0.1f, Cur.absorbCostMultiplier);
    public float SizeRatio => currentRadius / startRadius;
    public float StartRadius => startRadius;

    // Скільки об'єму набрано, виражено як «радіус відносно стартового» (те саме, що radiusRatio стадій)
    public float VolumeRatio => Mathf.Pow(volume / startVolume, 1f / 3f);
    float ProgressRatio => growOnlyOnTierUp ? VolumeRatio : SizeRatio;

    // Найвища стадія, поріг якої вже набрано за об'ємом (може бути вища за поточну Stage, поки камінь ще не виріс)
    public int ReachedStage
    {
        get
        {
            if (stages == null || stages.Count == 0) return 0;
            float ratio = VolumeRatio;
            int s = 0;
            for (int i = 0; i < stages.Count; i++)
                if (ratio + 0.0001f >= stages[i].radiusRatio) s = i;
            return s;
        }
    }

    // true: об'єму вже досить для наступної стадії, але камінь ще не виріс (час переходу на новий рівень)
    public bool CanTierUp => growOnlyOnTierUp && stages != null && Stage < stages.Count - 1 && ReachedStage > Stage;

    // Розмір (radiusRatio) наступної стадії; на останній повертає поточний
    public float NextStageRatio => stages != null && Stage < stages.Count - 1 ? stages[Stage + 1].radiusRatio : SizeRatio;

    // Прогрес до наступної стадії (0..1), для шкали в UI. На останній стадії = 1
    public float StageProgress
    {
        get
        {
            if (stages == null || Stage >= stages.Count - 1) return 1f;
            float a = stages[Stage].radiusRatio, b = stages[Stage + 1].radiusRatio;
            return Mathf.InverseLerp(a, b, ProgressRatio);
        }
    }

    static readonly RockStage Fallback = new RockStage { name = "—" };
    RockStage Cur => stages != null && stages.Count > 0 ? stages[Mathf.Clamp(Stage, 0, stages.Count - 1)] : Fallback;

    Rigidbody rb;
    SphereCollider col;
    float startRadius, currentRadius, targetRadius;
    float startMass, volume, startVolume;
    Vector3 coreBaseScale, coreBaseLocalPos;
    float baseCameraDistance;

    readonly Collider[] buffer = new Collider[256];
    readonly List<Transform> attached = new List<Transform>();

    Vector3 Center => col.transform.TransformPoint(col.center);

    float MaxScale()
    {
        Vector3 s = col.transform.lossyScale;
        return Mathf.Max(s.x, s.y, s.z);
    }

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        col = GetComponent<SphereCollider>();

        startRadius = col.radius * MaxScale();
        currentRadius = targetRadius = startRadius;
        startMass = rb.mass;
        volume = startVolume = 4f / 3f * Mathf.PI * startRadius * startRadius * startRadius;

        if (coreVisual != null)
        {
            coreBaseScale = coreVisual.localScale;
            coreBaseLocalPos = coreVisual.localPosition;
        }
        if (followCamera != null) baseCameraDistance = followCamera.distance;

        UpdateStage(false);
    }

    void Update()
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // тест росту: G додає 30% поточного об'єму (у релізній збірці цього коду немає)
        var dbgKb = Keyboard.current;
        if (dbgKb != null && dbgKb.gKey.wasPressedThisFrame) DebugGrow(0.3f);
#endif
        // плавна зміна розміру колайдера (росте повільно, меншає швидко)
        if (!Mathf.Approximately(currentRadius, targetRadius))
        {
            float prev = currentRadius;
            float speed = currentRadius < targetRadius ? growSpeed : shrinkSpeed;
            currentRadius = Mathf.MoveTowards(currentRadius, targetRadius, speed * Time.deltaTime);
            ApplySize();

            // камінь меншає: шматки на поверхні стискаються разом із ним, а не висять у повітрі
            if (currentRadius < prev) PullAttachedIn(currentRadius / prev);
        }
    }

    // Радіус слідує за об'ємом лише коли ріст за ярусами вимкнено.
    // Інакше розмір фіксований, доки TierTransition не викличе SetSizeRatio.
    void RefreshTarget()
    {
        if (growOnlyOnTierUp) return;
        targetRadius = Mathf.Pow(volume * 3f / (4f * Mathf.PI), 1f / 3f);
    }

    // Миттєво ставить розмір (радіус відносно стартового). Об'єм підтягується до розміру, якщо камінь виріс.
    public void SetSizeRatio(float ratio)
    {
        ratio = Mathf.Max(ratio, 0.01f);
        currentRadius = targetRadius = startRadius * ratio;
        volume = Mathf.Max(volume, startVolume * ratio * ratio * ratio);
        ApplySize();
    }

    // Після переходу в нову сцену: прив'язати камінь до камери нової сцени
    public void RebindCamera(FollowCamera cam)
    {
        followCamera = cam;
        if (cam == null) return;
        baseCameraDistance = cam.distance;   // відстань, задана в новій сцені для стартового розміру
        cam.distance = baseCameraDistance * Mathf.Lerp(1f, currentRadius / startRadius, cameraFollowsGrowth);
    }

    // Тестовий приріст об'єму без всмоктування (для налаштування темпу й перевірки переходу між ярусами)
    public void DebugGrow(float fraction)
    {
        volume += volume * fraction;
        RefreshTarget();
    }

    void PullAttachedIn(float ratio)
    {
        Vector3 c = Center;
        for (int i = 0; i < attached.Count; i++)
        {
            Transform t = attached[i];
            if (t == null) continue;
            t.position = c + (t.position - c) * ratio;
        }
    }

    void FixedUpdate()
    {
        var kb = Keyboard.current;
        if (kb != null && kb.eKey.isPressed) Suck();
    }

    void ApplySize()
    {
        float ratio = currentRadius / startRadius;

        col.radius = currentRadius / MaxScale();
        rb.mass = startMass * ratio * ratio * ratio;   // маса росте разом з об'ємом

        if (coreVisual != null)
        {
            coreVisual.localScale = coreBaseScale * ratio;
            // тримаємо центр моделі в центрі колайдера, навіть якщо pivot моделі зміщений
            coreVisual.localPosition = col.center - ratio * (col.center - coreBaseLocalPos);
        }

        if (followCamera != null)
            followCamera.distance = baseCameraDistance * Mathf.Lerp(1f, ratio, cameraFollowsGrowth);

        UpdateStage();
    }

    void UpdateStage(bool notify = true)
    {
        if (stages == null || stages.Count == 0) return;

        float ratio = currentRadius / startRadius;
        int s = 0;
        for (int i = 0; i < stages.Count; i++)
            if (ratio + 0.0001f >= stages[i].radiusRatio) s = i;

        if (s == Stage) return;
        if (s < Stage && !stageCanDrop) return;
        Stage = s;
        if (notify)
        {
            if (showDebug) Debug.Log($"[Rock] Стадія {Stage + 1}/{StageCount}: {StageName} | час {Time.timeSinceLevelLoad:0}с | з'їдено ×{(volume - startVolume) / startVolume:0.0} стартового об'єму | розмір ×{SizeRatio:0.00}", this);
            StageChanged?.Invoke(s);
        }
    }

    GUIStyle debugStyle;

    void OnGUI()
    {
        if (!showDebug) return;

        if (debugStyle == null)
        {
            debugStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
            debugStyle.normal.textColor = Color.white;
        }

        string text = $"Стадія {Stage + 1}/{StageCount}: {StageName}\n" +
                      $"Розмір x{SizeRatio:0.00} | об'єм {volume:0.0}\n" +
                      $"Швидкість x{SpeedMultiplier:0.00} | сила x{ForceMultiplier:0.00} | радіус удару x{DestroyRadiusMultiplier:0.00}";

        // темна підкладка, щоб було видно на будь-якому фоні
        Rect box = new Rect(10, 10, 640, 100);
        Color old = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.65f);
        GUI.DrawTexture(box, Texture2D.whiteTexture);
        GUI.color = old;
        GUI.Label(new Rect(box.x + 10, box.y + 6, box.width - 20, box.height - 12), text, debugStyle);
    }

    void Suck()
    {
        Vector3 center = Center;
        float range = suckRadius * (currentRadius / startRadius);
        int n = Physics.OverlapSphereNonAlloc(center, range, buffer, ~0, QueryTriggerInteraction.Ignore);

        for (int i = 0; i < n; i++)
        {
            Collider c = buffer[i];
            var a = c.GetComponentInParent<Absorbable>();
            if (a == null || a.attached) continue;
            if (a.volume > volume * maxSizeRatio) continue;   // надто велика фігура поки що

            Rigidbody b = a.GetComponent<Rigidbody>();
            if (b == null) continue;

            Vector3 to = center - b.worldCenterOfMass;
            float dist = to.magnitude;
            float reach = currentRadius + c.bounds.extents.magnitude * 0.6f;

            if (dist <= reach)
            {
                Attach(a, b, center);
                continue;
            }

            Vector3 wanted = to / dist * pullSpeed;
            b.linearVelocity = Vector3.MoveTowards(b.linearVelocity, wanted, pullAcceleration * Time.fixedDeltaTime);
        }
    }

    void Attach(Absorbable a, Rigidbody b, Vector3 center)
    {
        a.attached = true;
        Transform t = a.transform;

        // фігура стає на поточну поверхню каменя
        Vector3 dir = t.position - center;
        dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : Random.onUnitSphere;
        t.position = center + dir * currentRadius;
        t.SetParent(transform, true);

        var piece = a.GetComponent<VoxelPiece>();
        if (piece != null) piece.OnAbsorbed();

        foreach (var c in a.GetComponents<Collider>()) Destroy(c);
        Destroy(b);

        attached.Add(t);
        while (attached.Count > maxAttached)
        {
            if (attached[0] != null) Destroy(attached[0].gameObject);
            attached.RemoveAt(0);
        }

        // камінь росте
        volume += a.volume * volumeGain / AbsorbCostMultiplier;
        RefreshTarget();
    }

    // Об'єм, нижче якого R не зменшує камінь: початок найвищої досягнутої стадії (за об'ємом).
    // Для першої стадії це стартовий об'єм.
    float BlastFloorVolume()
    {
        if (!stageFloorOnBlast || stages == null || stages.Count == 0) return startVolume;

        float ratio = Mathf.Pow(volume / startVolume, 1f / 3f);
        int s = 0;
        for (int i = 0; i < stages.Count; i++)
            if (ratio + 0.0001f >= stages[i].radiusRatio) s = i;

        float r = Mathf.Max(1f, stages[s].radiusRatio);
        return startVolume * r * r * r;
    }

    // Віддає частку набраного понад початок поточної стадії (fraction = 0.5 -> половину цього надлишку).
    // Камінь меншає, але не нижче початку своєї стадії, а відповідна частка прикріплених шматків
    // відв'язується від каменя і потрапляє в released.
    // Повертає false, якщо камінь уже на межі стадії і віддавати нічого.
    public bool ShedFraction(float fraction, List<Transform> released)
    {
        fraction = Mathf.Clamp01(fraction);

        float floorVolume = BlastFloorVolume();
        float excess = volume - floorVolume;
        if (excess <= startVolume * 0.02f) return false;   // на межі стадії: R більше не діє

        float totalGained = volume - startVolume;           // усе набране (для частки шматків)
        float shed = excess * fraction;

        volume -= shed;
        RefreshTarget();

        // шматків відв'язуємо стільки ж, яку частку всього набраного об'єму віддали
        float pieceFraction = totalGained > 1e-6f ? Mathf.Clamp01(shed / totalGained) : fraction;

        attached.RemoveAll(t => t == null);
        int take = Mathf.CeilToInt(attached.Count * pieceFraction);
        for (int i = 0; i < take && attached.Count > 0; i++)
        {
            int idx = Random.Range(0, attached.Count);   // випадкові шматки по всій поверхні
            released.Add(attached[idx]);
            attached.RemoveAt(idx);
        }
        return true;
    }
}