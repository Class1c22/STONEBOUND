using UnityEngine;

// Глобальний вітер для всієї гри: один на всі рівні, переживає завантаження сцен.
// Створюється автоматично, нічого розставляти в сценах не треба.
// Хочеш підкрутити налаштування: поклади цей компонент на порожній об'єкт у першій сцені.
[DefaultExecutionOrder(-100)]
public class WindSystem : MonoBehaviour
{
    static WindSystem instance;
    static bool quitting;

    public static WindSystem Instance
    {
        get
        {
            if (instance == null && !quitting)
                new GameObject("WindSystem").AddComponent<WindSystem>();   // Awake сам виставить instance
            return instance;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { instance = null; quitting = false; }

    [Header("Вітер")]
    public bool windOn = true;
    public int seed = 1;
    [Tooltip("Швидкість часу вітру (1 = звичайно)")]
    public float timeScale = 1f;
    [Tooltip("Головний напрям, градусів від осі +Z")]
    public float angleBase = 0f;
    [Tooltip("Наскільки далеко напрям відхиляється від головного, градусів. 180 = дме з будь-якого боку")]
    [Range(0f, 180f)] public float directionWander = 110f;
    [Tooltip("Як швидко змінюється напрям (більше = частіше)")]
    public float directionChangeSpeed = 0.12f;
    [Tooltip("Як швидко змінюється загальна сила (більше = частіші шквали й штилі)")]
    public float strengthChangeSpeed = 0.25f;
    [Tooltip("Різкість шквалів: 0 = рівний вітер, 1 = різкі удари")]
    [Range(0f, 1f)] public float squallAmount = 0.6f;

    public float AngleDegrees { get; private set; }
    float strength = 0.4f;
    float t;

    // 0..1 (0, якщо вітер вимкнено)
    public float Strength01 => windOn ? strength : 0f;

    public Vector3 Direction
    {
        get
        {
            float a = AngleDegrees * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
        }
    }

    // +1 = дме в спину (у напрямку руху), -1 = у лице, 0 = збоку. Помножене на силу вітру.
    public float Alignment(Vector3 moveDir)
    {
        moveDir.y = 0f;
        if (moveDir.sqrMagnitude < 1e-4f) return 0f;
        return Vector3.Dot(moveDir.normalized, Direction) * Strength01;
    }

    void Awake()
    {
        if (instance != null && instance != this) { Destroy(gameObject); return; }
        instance = this;
        transform.SetParent(null, true);
        DontDestroyOnLoad(gameObject);
        Tick(0f);
    }

    void OnApplicationQuit() { quitting = true; }
    void OnDestroy() { if (instance == this) instance = null; }

    void Update() { Tick(Time.deltaTime); }

    static float Contrast(float n)
    {
        return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.25f, 0.75f, n));
    }

    void Tick(float dt)
    {
        t += dt * Mathf.Max(0f, timeScale);
        float o = 10000f + seed * 17.3f;

        // напрям: повільне блукання + швидші смикання
        float slow = (Mathf.PerlinNoise(o + t * directionChangeSpeed, o * 0.31f) - 0.5f) * 2f;
        float fast = (Mathf.PerlinNoise(o * 0.7f + t * directionChangeSpeed * 5f, o * 0.13f + 9.1f) - 0.5f) * 2f;
        AngleDegrees = angleBase + slow * directionWander * 1.6f + fast * directionWander * 0.3f;

        // сила: хвилі штилю й вітру + рідкісні різкі шквали
        float baseN = Contrast(Mathf.PerlinNoise(o * 0.53f + t * strengthChangeSpeed, o * 0.77f + 3.3f));
        float flutter = Mathf.PerlinNoise(o * 0.41f + t * strengthChangeSpeed * 3f, o * 0.19f + 7.7f);
        float squall = Contrast(Mathf.PerlinNoise(o * 0.67f + t * strengthChangeSpeed * 2.3f, o * 0.29f + 5.5f));
        squall = squall * squall * squall;
        strength = Mathf.Clamp01(Mathf.Lerp(0.2f, 0.85f, baseN) * Mathf.Lerp(0.8f, 1f, flutter) + squall * squallAmount * 0.6f);
    }
}