using System;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

// Перехід між рівнями (сценами). Хмарки робиш ти в Animator, скрипт лише запускає їх.
//
// Камінь під час рівня не росте: об'єм лише накопичується (RockAbsorber.growOnlyOnTierUp).
// Коли набрано поріг наступної стадії (RockAbsorber.CanTierUp), запускається перехід:
// 1) камінь завмирає, ОДРАЗУ виростає до наступної стадії, камера віддаляється вгору,
//    і грається анімація «хмарки закривають екран»;
// 2) коли екран закритий, вантажиться наступна сцена, а КАМІНЬ ПЕРЕНОСИТЬСЯ в неї
//    (на місце каменя-заглушки нової сцени; заглушка видаляється, камера й посилання перепризначаються);
// 3) у новій сцені грається анімація «хмарки розлітаються».
//
// У кожній сцені лишай свій RockAbsorber (з усім обладнанням) як заглушку: звідти беруться позиція
// старту і посилання на воксельний світ. Так сцену можна запускати й окремо.
//
// Постав на порожній ROOT-об'єкт у ПЕРШІЙ сцені. Об'єкт переживає завантаження сцен, тому
// Canvas/хмарки й аніматори роби ДІТЬМИ цього об'єкта (тоді вони теж не зникнуть).
// Сцени мають бути в Build Profiles по порядку. Дублікати в наступних сценах самі видаляються.
// У старій сцені потрібні RockAbsorber на камені (RockController і RockBlast за наявності) і Camera.
// FollowCamera необов'язковий. У новій сцені від скрипта нічого не потрібно.
//
// Налаштування аніматорів:
//  - Update Mode = Unscaled Time (щоб працювали, навіть якщо Time.timeScale = 0);
//  - стан за замовчуванням: ПОРОЖНІЙ Idle (хмарок не видно, екран чистий), без переходів з нього;
//  - Cover State (за замовчуванням "loading"): анімація, що закриває весь екран і лишається в кінці закритою (Loop Time вимкнено);
//  - Reveal State (за замовчуванням "loadingrev"): розліт; після нього екран чистий.
//  Скрипт запускає стани за назвою (Animator.Play), параметри й переходи між станами не потрібні.
//  Якщо натомість заповнити Cover Trigger / Reveal Trigger і лишити назву стану порожньою, використовуються тригери.
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
    [Tooltip("Автоперехід, щойно набрано поріг наступної стадії")]
    [UnityEngine.Serialization.FormerlySerializedAs("autoAtMaxStage")]
    public bool autoOnTierUp = true;
    [Tooltip("Пауза між досягненням порога і початком переходу, секунд")]
    public float autoDelay = 0.5f;
    [Tooltip("Тестова кнопка (тільки в Editor і Development build)")]
    public Key debugKey = Key.N;

    [Header("Відліт камери")]
    public float flyDuration = 3f;
    [Tooltip("За скільки секунд від початку відльоту камінь виростає до наступної стадії (0 = миттєво)")]
    public float growDuration = 0.8f;
    [Tooltip("Додаткове віддалення камери понад ріст каменя: мінімум, метрів")]
    public float minRise = 20f;
    [Tooltip("Додаткове віддалення = радіус каменя × це число (береться більше з двох)")]
    public float riseRadiusFactor = 8f;
    [Tooltip("На скільки градусів розширюється FOV під час відльоту")]
    public float fovBoost = 15f;

    [Header("UI переходу")]
    [Tooltip("Об'єкт (Canvas або його панель) з хмарками. Вимкнений на старті, вмикається на час переходу, потім знову вимикається. НЕ став сюди сам об'єкт із TierTransition")]
    public GameObject transitionUI;

    [Header("Хмарки: закрити екран")]
    public Animator coverAnimator;
    [Tooltip("Назва стану в Animator, що закриває екран. Якщо порожньо, використовується тригер")]
    public string coverState = "loading";
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
    [Tooltip("Назва стану в Animator, що відкриває екран. Якщо порожньо, використовується тригер")]
    public string revealState = "loadingrev";
    public string revealTrigger = "Reveal";
    [Tooltip("Тривалість анімації розльоту, секунд (як довжина кліпу)")]
    public float revealDuration = 1.5f;

    // ---- внутрішнє ----
    class Rig
    {
        public Camera cam;
        public Vector3 dirHome, dirTop;
        public float distHome, rise, fov0, radius0;
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
        SetUI(false);   // на старті гри UI переходу сховано
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

        if (!autoOnTierUp) return;

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

        maxTimer = rock.CanTierUp ? maxTimer + Time.deltaTime : 0f;
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

        RockAbsorber carried = rock;
        Camera cam = FindCamera();
        FollowCamera follow = FindFirstObjectByType<FollowCamera>();
        bool hasRig = carried != null && cam != null;

        Rig rig = null;
        float ratio0 = 1f, ratio1 = 1f;
        if (hasRig)
        {
            Freeze(carried, true);
            ratio0 = carried.SizeRatio;
            ratio1 = Mathf.Max(ratio0, carried.NextStageRatio);   // до якого розміру камінь виросте
            rig = BuildRig(cam, carried, carried.StartRadius * ratio1);
            if (follow != null) follow.enabled = false;
        }

        AsyncOperation op = loader();
        op.allowSceneActivation = false;   // вантажимо у фоні, поки летить камера

        SetUI(true);    // вмикаємо UI до Rebind/Play, бо на неактивному об'єкті Animator не працює
        ResetAnimators();
        Departing?.Invoke();

        // ===== відліт: камінь миттєво виростає, камера віддаляється вгору, хмарки закривають екран =====
        float coverAt = coverEndsWithFlight ? Mathf.Max(0f, flyDuration - coverDuration) : coverStartTime;
        bool coverStarted = false;
        float coverStartedAt = 0f;
        float elapsed = 0f;
        bool grown = ratio1 <= ratio0;

        while (elapsed < flyDuration)
        {
            if (!coverStarted && elapsed >= coverAt)
            {
                coverStarted = true;
                coverStartedAt = Time.unscaledTime;
                Fire(coverAnimator, coverState, coverTrigger);
            }

            if (hasRig && carried != null)
            {
                if (!grown)
                {
                    float gt = growDuration > 0f ? Mathf.Clamp01(elapsed / growDuration) : 1f;
                    carried.SetSizeRatio(Mathf.Lerp(ratio0, ratio1, Mathf.SmoothStep(0f, 1f, gt)));
                    if (gt >= 1f) grown = true;
                }
                Apply(rig, carried, Mathf.Clamp01(elapsed / flyDuration));
            }

            yield return null;
            elapsed += Time.unscaledDeltaTime;
        }
        if (hasRig && carried != null)
        {
            if (!grown) carried.SetSizeRatio(ratio1);
            Apply(rig, carried, 1f);
        }

        // якщо закриття ще не запускалось (coverStartTime більше за політ) або ще триває, чекаємо його кінця
        if (!coverStarted)
        {
            coverStarted = true;
            coverStartedAt = Time.unscaledTime;
            Fire(coverAnimator, coverState, coverTrigger);
        }
        while (Time.unscaledTime - coverStartedAt < coverDuration) yield return null;

        while (op.progress < 0.9f) yield return null;
        yield return new WaitForSecondsRealtime(holdClosed);

        FullyCovered?.Invoke();

        // камінь їде в нову сцену: стає кореневим і не знищується разом зі старою сценою
        if (carried != null)
        {
            carried.transform.SetParent(null, true);
            DontDestroyOnLoad(carried.gameObject);
        }

        op.allowSceneActivation = true;
        while (!op.isDone) yield return null;

        // ===== прибуття: екран ще закритий, поки нова сцена ініціалізується =====
        yield return null;
        yield return null;

        if (carried != null)
        {
            Arrive(carried);
            rock = carried;
        }
        else
        {
            rock = FindFirstObjectByType<RockAbsorber>();
        }

        Revealing?.Invoke();
        Fire(revealAnimator, revealState, revealTrigger);
        yield return new WaitForSecondsRealtime(revealDuration);

        ResetAnimators();   // назад у стан за замовчуванням (екран чистий), готово до наступного переходу
        SetUI(false);       // і ховаємо UI до наступного переходу
        maxTimer = 0f;
        IsBusy = false;
        Finished?.Invoke();
    }

    // Нова сцена вже активна: ставимо камінь на місце заглушки і перепризначаємо посилання.
    void Arrive(RockAbsorber r)
    {
        RockAbsorber spawn = null;
        foreach (var other in FindObjectsByType<RockAbsorber>(FindObjectsSortMode.None))
        {
            if (other == r) continue;
            if (spawn == null) spawn = other;
            else Destroy(other.gameObject);
        }

        VoxelWorld world = null;
        if (spawn != null)
        {
            var d = spawn.GetComponent<VoxelDestroyer>();
            if (d != null) world = d.world;

            // той самий «низ», що в заглушки: більший камінь піднімається на різницю радіусів
            Vector3 wanted = spawn.WorldCenter + Vector3.up * (r.Radius - spawn.Radius);
            r.transform.position += wanted - r.WorldCenter;
            Destroy(spawn.gameObject);
        }
        else
        {
            Debug.LogWarning("[TierTransition] У новій сцені немає каменя-заглушки (RockAbsorber): камінь лишився на старих координатах.", this);
        }
        Physics.SyncTransforms();

        if (world == null) world = FindFirstObjectByType<VoxelWorld>();

        FollowCamera follow = FindFirstObjectByType<FollowCamera>();
        Camera cam = FindCamera();
        if (follow != null)
        {
            follow.enabled = true;
            follow.SetTarget(r.transform);
            r.RebindCamera(follow);
        }

        var ctrl = r.GetComponent<RockController>();
        if (ctrl != null)
            ctrl.cameraTransform = follow != null ? follow.transform : (cam != null ? cam.transform : null);

        if (world != null)
        {
            var destroyer = r.GetComponent<VoxelDestroyer>();
            if (destroyer != null) destroyer.world = world;
            var blast = r.GetComponent<RockBlast>();
            if (blast != null) blast.world = world;
        }

        var speed = FindFirstObjectByType<SpeedEffects>();
        if (speed != null) speed.target = r.GetComponent<Rigidbody>();

        Freeze(r, false);
    }

    // ---------- UI ----------

    void SetUI(bool on)
    {
        if (transitionUI != null && transitionUI != gameObject)
            transitionUI.SetActive(on);
    }

    // ---------- аніматори ----------

    static void Fire(Animator a, string state, string trigger)
    {
        if (a == null) return;
        if (!string.IsNullOrEmpty(state))
        {
            a.Play(state, 0, 0f);
            return;
        }
        if (string.IsNullOrEmpty(trigger)) return;
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

    Rig BuildRig(Camera cam, RockAbsorber r, float finalRadius)
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
            rise = Mathf.Max(minRise, finalRadius * riseRadiusFactor),
            fov0 = cam.fieldOfView,
            radius0 = Mathf.Max(r.Radius, 0.01f)
        };
    }

    // s = 0: початок, s = 1: кінець відльоту. Розмір каменя вже виставлено, камера слідує за ним.
    void Apply(Rig r, RockAbsorber rk, float s)
    {
        float g = rk.Radius / r.radius0;   // у скільки разів камінь уже більший за початковий

        // камера: віддаляється разом із ростом каменя (кадр не змінюється) + ще підіймається вгору
        Vector3 focus = rk.WorldCenter;
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