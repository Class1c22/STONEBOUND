using System;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

// Перехід між рівнями (сценами). Хмарки робиш ти в Animator, скрипт лише запускає їх тригерами.
//
// 1) камінь завмирає, камера віддаляється, а камінь росте разом із нею;
//    у потрібний момент запускається аніматор «закрити екран хмарками»;
// 2) коли екран закритий, вантажиться наступна сцена;
// 3) у новій сцені запускається аніматор «хмарки розлітаються».
//
// Постав на порожній ROOT-об'єкт у ПЕРШІЙ сцені. Об'єкт переживає завантаження сцен, тому
// Canvas/хмарки й аніматори роби ДІТЬМИ цього об'єкта (тоді вони теж не зникнуть).
// Сцени мають бути в Build Profiles по порядку. Дублікати в наступних сценах самі видаляються.
// У старій сцені потрібні RockAbsorber на камені (RockController і RockBlast за наявності) і Camera.
// FollowCamera необов'язковий. У новій сцені від скрипта нічого не потрібно.
//
// Налаштування аніматорів:
//  - Update Mode = Unscaled Time (щоб працювали, навіть якщо Time.timeScale = 0);
//  - стан за замовчуванням: хмарок не видно (екран чистий);
//  - Cover: тригер (за замовчуванням "Cover") запускає анімацію, що закриває весь екран і лишається в кінці закритою;
//  - Reveal: тригер (за замовчуванням "Reveal") запускає розліт; після нього екран чистий.
//  Можна один Animator зі станами Idle -> Cover -> Reveal -> Idle, а можна два окремих.
//  Після кожного переходу скрипт робить Rebind(): аніматор повертається в стан за замовчуванням.
public class TierTransition : MonoBehaviour
{
    public static TierTransition Instance { get; private set; }
    public bool IsBusy { get; private set; }

    // події для всього іншого: звук, UI тощо
    public event Action Departing;      // старт переходу
    public event Action FullyCovered;   // екран закритий, зараз активується нова сцена
    public event Action Revealing;      // нова сцена готова, хмарки розлітаються
    public event Action Finished;       // перехід закінчено

    [Header("Запуск")]
    [Tooltip("Автоперехід, коли камінь тримає останню стадію")]
    public bool autoAtMaxStage = true;
    [Tooltip("Скільки секунд камінь має тримати останню стадію (щоб встиг показатись баннер)")]
    public float autoDelay = 3f;
    [Tooltip("Тестова кнопка (тільки в Editor і Development build)")]
    public Key debugKey = Key.N;

    [Header("Відліт камери")]
    public float flyDuration = 3f;
    [Tooltip("У скільки разів камінь виростає за час відльоту")]
    public float rockGrowMultiplier = 2.5f;
    [Tooltip("Додаткове віддалення камери понад ріст каменя: мінімум, метрів")]
    public float minRise = 20f;
    [Tooltip("Додаткове віддалення = радіус каменя × це число (береться більше з двох)")]
    public float riseRadiusFactor = 8f;
    [Tooltip("На скільки градусів розширюється FOV під час відльоту")]
    public float fovBoost = 15f;

    [Header("Хмарки: закрити екран")]
    public Animator coverAnimator;
    public string coverTrigger = "Cover";
    [Tooltip("Тривалість анімації закриття, секунд (як довжина кліпу)")]
    public float coverDuration = 1.5f;
    [Tooltip("Запустити закриття так, щоб воно закінчилось разом із відльотом камери")]
    public bool coverEndsWithFlight = true;
    [Tooltip("Якщо вимкнено: коли запускати закриття, секунд від початку відльоту")]
    public float coverStartTime = 1.5f;
    [Tooltip("Скільки тримати екран закритим перед активацією нової сцени")]
    public float holdClosed = 0.2f;

    [Header("Хмарки: розліт у новій сцені")]
    [Tooltip("Може бути той самий Animator, що й вище")]
    public Animator revealAnimator;
    public string revealTrigger = "Reveal";
    [Tooltip("Тривалість анімації розльоту, секунд (як довжина кліпу)")]
    public float revealDuration = 1.5f;

    // ---- внутрішнє ----
    class Rig
    {
        public Camera cam;
        public Vector3 dirHome, dirTop;
        public float distHome, rise, fov0;
    }

    RockAbsorber rock;
    float maxTimer, findTimer;
    bool warnedNoNext;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            enabled = false;
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void Start()
    {
        if (Instance != this) return;
        rock = FindFirstObjectByType<RockAbsorber>();
    }

    void Update()
    {
        if (Instance != this || IsBusy) return;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        var kb = Keyboard.current;
        if (kb != null && kb[debugKey].wasPressedThisFrame) { GoToNext(); return; }
#endif

        if (!autoAtMaxStage) return;

        if (rock == null)
        {
            findTimer -= Time.deltaTime;
            if (findTimer <= 0f)
            {
                findTimer = 0.5f;
                rock = FindFirstObjectByType<RockAbsorber>();
            }
            maxTimer = 0f;
            return;
        }

        bool atMax = rock.StageCount > 0 && rock.Stage >= rock.StageCount - 1;
        maxTimer = atMax ? maxTimer + Time.deltaTime : 0f;
        if (maxTimer >= autoDelay) GoToNext();
    }

    // ---------- публічне API ----------

    // Наступна сцена за порядком у Build Profiles
    public void GoToNext()
    {
        if (IsBusy) return;
        int next = SceneManager.GetActiveScene().buildIndex + 1;
        if (next >= SceneManager.sceneCountInBuildSettings)
        {
            if (!warnedNoNext)
            {
                warnedNoNext = true;
                Debug.Log("[TierTransition] Це останній рівень, далі сцен немає.", this);
            }
            return;
        }
        GoTo(next);
    }

    public void GoTo(int buildIndex)
    {
        if (IsBusy) return;
        StartCoroutine(Run(() => SceneManager.LoadSceneAsync(buildIndex)));
    }

    public void GoTo(string sceneName)
    {
        if (IsBusy) return;
        StartCoroutine(Run(() => SceneManager.LoadSceneAsync(sceneName)));
    }

    // ---------- послідовність ----------

    IEnumerator Run(Func<AsyncOperation> loader)
    {
        IsBusy = true;
        maxTimer = 0f;

        Camera cam = FindCamera();
        FollowCamera follow = FindFirstObjectByType<FollowCamera>();
        bool hasRig = rock != null && cam != null;

        Rig rig = null;
        Vector3 scale0 = Vector3.one;
        if (hasRig)
        {
            Freeze(rock, true);
            scale0 = rock.transform.localScale;
            rig = BuildRig(cam, rock);
            if (follow != null) follow.enabled = false;
        }

        AsyncOperation op = loader();
        op.allowSceneActivation = false;   // вантажимо у фоні, поки летить камера

        ResetAnimators();
        Departing?.Invoke();

        // ===== відліт: камера віддаляється, камінь росте, хмарки закривають екран =====
        float coverAt = coverEndsWithFlight ? Mathf.Max(0f, flyDuration - coverDuration) : coverStartTime;
        bool coverStarted = false;
        float coverStartedAt = 0f;
        float elapsed = 0f;

        while (elapsed < flyDuration)
        {
            if (!coverStarted && elapsed >= coverAt)
            {
                coverStarted = true;
                coverStartedAt = Time.unscaledTime;
                Fire(coverAnimator, coverTrigger);
            }

            float t = Mathf.Clamp01(elapsed / flyDuration);
            if (hasRig && rock != null) Apply(rig, scale0, t);

            yield return null;
            elapsed += Time.unscaledDeltaTime;
        }
        if (hasRig && rock != null) Apply(rig, scale0, 1f);

        // якщо закриття ще не запускалось (coverStartTime більше за політ) або ще триває, чекаємо його кінця
        if (!coverStarted)
        {
            coverStarted = true;
            coverStartedAt = Time.unscaledTime;
            Fire(coverAnimator, coverTrigger);
        }
        while (Time.unscaledTime - coverStartedAt < coverDuration) yield return null;

        while (op.progress < 0.9f) yield return null;
        yield return new WaitForSecondsRealtime(holdClosed);

        FullyCovered?.Invoke();
        op.allowSceneActivation = true;
        while (!op.isDone) yield return null;

        // ===== прибуття: екран ще закритий, поки нова сцена ініціалізується =====
        yield return null;
        yield return null;

        rock = FindFirstObjectByType<RockAbsorber>();

        Revealing?.Invoke();
        Fire(revealAnimator, revealTrigger);
        yield return new WaitForSecondsRealtime(revealDuration);

        ResetAnimators();   // назад у стан за замовчуванням (екран чистий), готово до наступного переходу
        maxTimer = 0f;
        IsBusy = false;
        Finished?.Invoke();
    }

    // ---------- аніматори ----------

    static void Fire(Animator a, string trigger)
    {
        if (a == null || string.IsNullOrEmpty(trigger)) return;
        a.ResetTrigger(trigger);
        a.SetTrigger(trigger);
    }

    static void Reset(Animator a)
    {
        if (a == null) return;
        a.Rebind();
        a.Update(0f);
    }

    void ResetAnimators()
    {
        Reset(coverAnimator);
        if (revealAnimator != coverAnimator) Reset(revealAnimator);
    }

    // ---------- камера і камінь ----------

    Camera FindCamera()
    {
        Camera c = Camera.main;
        return c != null ? c : FindFirstObjectByType<Camera>();
    }

    Rig BuildRig(Camera cam, RockAbsorber r)
    {
        Vector3 focus = r.WorldCenter;
        Vector3 off = cam.transform.position - focus;
        float dist = Mathf.Max(off.magnitude, 0.1f);
        Vector3 home = off / dist;

        Vector3 flat = new Vector3(home.x, 0f, home.z);
        flat = flat.sqrMagnitude > 1e-4f ? flat.normalized : Vector3.back;

        return new Rig
        {
            cam = cam,
            dirHome = home,
            dirTop = (Vector3.up + flat * 0.06f).normalized,   // майже вертикально вгору (без вирождення LookRotation)
            distHome = dist,
            rise = Mathf.Max(minRise, r.Radius * riseRadiusFactor),
            fov0 = cam.fieldOfView
        };
    }

    // s = 0: початок, s = 1: кінець відльоту.
    void Apply(Rig r, Vector3 scale0, float s)
    {
        // камінь росте
        float g = Mathf.Lerp(1f, rockGrowMultiplier, Mathf.SmoothStep(0f, 1f, s));
        rock.transform.localScale = scale0 * g;

        // камера: віддаляється разом із ростом каменя (кадр не змінюється) + ще підіймається вгору
        Vector3 focus = rock.WorldCenter;   // центр може зсуватись разом зі scale
        float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(s * 1.6f));
        Vector3 dir = Vector3.Slerp(r.dirHome, r.dirTop, k).normalized;
        float baseDist = r.distHome * g;
        float d = Mathf.Lerp(baseDist, baseDist + r.rise, s * s);

        r.cam.transform.position = focus + dir * d;
        r.cam.transform.rotation = Quaternion.LookRotation(-dir, Vector3.up);   // завжди дивиться на камінь
        r.cam.fieldOfView = r.fov0 + fovBoost * s;
    }

    void Freeze(RockAbsorber r, bool on)
    {
        var rb = r.GetComponent<Rigidbody>();
        if (rb != null)
        {
            if (on)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
            rb.isKinematic = on;
        }

        r.enabled = !on;
        var ctrl = r.GetComponent<RockController>();
        if (ctrl != null) ctrl.enabled = !on;
        var blast = r.GetComponent<RockBlast>();
        if (blast != null) blast.enabled = !on;
    }
}
