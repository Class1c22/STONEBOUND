using System;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

// Перехід між рівнями (сцен). Хмарки робиш ти в Animator, скрипт лише запускає їх.
//
// Камінь під час рівня не росте: об'єм лише накопичується (RockAbsorber.growOnlyOnTierUp).
// Коли набрано поріг наступної стадії (RockAbsorber.CanTierUp), запускається перехід. Послідовність:
// 1) камінь завмирає і росте до наступної стадії;
// 2) камера віддаляється вгору (разом із ростом каменя);
// 3) анімація 1: хмарки закривають екран;
// 4) вантажиться наступна сцена (екран закритий);
// 5) КАМІНЬ І КАМЕРА переносяться в нову сцену (камінь стає на місце заглушки, камера нової сцени видаляється);
// 6) анімація 2: хмарки розлітаються, а камера повертається від каменя назад (зворотний відліт).
//
// У кожній сцені лишай свій RockAbsorber (з усім обладнанням) і свою камеру як заглушки: звідти беруться
// позиція старту, відстань камери і посилання на воксельний світ. Так сцену можна запускати й окремо.
//
// Постав на порожній ROOT-об'єкт у ПЕРШІЙ сцені. Об'єкт переживає завантаження сцен, тому
// Canvas/хмарки й аніматори роби ДІТЬМИ цього об'єкта (тоді вони теж не зникнуть).
// Сцени мають бути в Build Profiles по порядку. Дублікати в наступних сценах самі видаляються.
// У старій сцені потрібні RockAbsorber на камені (RockController і RockBlast за наявності) і Camera
// (бажано з FollowCamera: тоді камера переноситься в нову сцену).
//
// Налаштування аніматорів:
//  - Update Mode = Unscaled Time (щоб працювали, навіть якщо Time.timeScale = 0);
//  - стан за замовчуванням: ПОРОЖНІЙ (хмарок не видно, екран чистий); перехід з нього на закриття лише за тригером Cover Trigger;
//  - закриття (Cover Trigger, за замовчуванням "loading"): анімація, що закриває весь екран і лишається в кінці закритою (Loop Time вимкнено);
//  - розліт (Reveal Trigger, за замовчуванням "loadingrev"): розліт; після нього екран чистий.
//  Скрипт запускає анімації лише тригерами (Animator.SetTrigger), умови переходів між станами налаштовуєш в Animator.
//  Після кожного переходу скрипт робить Rebind(): аніматор повертається в стан за замовчуванням.
[DefaultExecutionOrder(1000)]   // LateUpdate після FollowCamera, щоб зворотний підліт камери накладався поверх неї
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

    [Header("Ріст каменя і відліт камери (до хмарок)")]
    [Tooltip("Скільки триває ріст каменя і віддалення камери, секунд. Хмарки стартують ПІСЛЯ цього")]
    public float flyDuration = 3f;
    [Tooltip("За скільки секунд від початку відльоту камінь виростає до наступної стадії (0 = миттєво)")]
    public float growDuration = 0.8f;
    [Tooltip("Додаткове віддалення камери понад ріст каменя: мінімум, метрів")]
    public float minRise = 20f;
    [Tooltip("Додаткове віддалення = радіус каменя × це число (береться більше з двох)")]
    public float riseRadiusFactor = 8f;
    [Tooltip("На скільки градусів розширюється FOV під час відльоту")]
    public float fovBoost = 15f;

    [Header("Повернення камери в новій сцені")]
    [Tooltip("За скільки секунд камера підлітає назад до каменя, коли хмарки розлітаються (дзеркально до відльоту). 0 = без підльоту. Камінь стоїть, поки камера не повернулась")]
    public float approachDuration = 1.5f;
    [Tooltip("На скільки секунд підліт камери стартує РАНІШЕ за розліт хмарок (екран у цей час ще закритий). 0 = одночасно")]
    public float approachLead = 0.4f;

    [Header("Туман під час відльоту")]
    [Tooltip("Туман з'являється, коли камера віддаляється, лишається під час завантаження і тане, коли камера підлітає до каменя в новій сцені")]
    public bool useFog = true;
    [Tooltip("Колір туману (підбери під небо / колір хмарок)")]
    public Color fogColor = new Color(0.82f, 0.88f, 0.95f);
    [Tooltip("Густина туману в кінці відльоту (Exponential Squared). Більше = густіше. Підбери під масштаб сцени")]
    public float fogDensity = 0.012f;
    [Tooltip("Скільки першої частини відльоту туману ще немає (0.4 = туман починає з'являтись після 40% відльоту і повністю набирає густину в кінці). У новій сцені дзеркально: туман тане і зникає за цю частку до кінця підльоту камери")]
    [Range(0f, 0.9f)] public float fogDelay = 0.4f;

    [Header("UI переходу")]
    [Tooltip("Об'єкт (Canvas або його панель) з хмарками. Вимкнений на старті, вмикається на час переходу, потім знову вимикається. НЕ став сюди сам об'єкт із TierTransition")]
    public GameObject transitionUI;
    [Tooltip("Інший UI (HUD, лічильники тощо), який теж має переживати завантаження сцен (DontDestroyOnLoad). Сюди ставити кореневі Canvas. UI, що вже є дітьми цього об'єкта, додавати не треба")]
    public GameObject[] persistentUI;

    [Header("Хмарки: закрити екран")]
    public Animator coverAnimator;
    [Tooltip("Назва параметра-тригера, що запускає закриття")]
    public string coverTrigger = "loading";
    [Tooltip("Тривалість анімації закриття, секунд (як довжина кліпу)")]
    public float coverDuration = 1.5f;
    [Tooltip("Скільки тримати екран закритим перед початком завантаження сцени")]
    public float holdClosed = 0.2f;

    [Header("Хмарки: розліт у новій сцені")]
    [Tooltip("Може бути той самий Animator, що й вище")]
    public Animator revealAnimator;
    [Tooltip("Назва параметра-тригера, що запускає розліт")]
    public string revealTrigger = "loadingrev";
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
            // копія UI з нової сцени теж зайва: оригінал уже переїхав разом із першим ROOT
            DestroyIfOutside(transitionUI);
            if (persistentUI != null)
                foreach (var go in persistentUI) DestroyIfOutside(go);
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        // UI, що лежить поза цим об'єктом, теж переносимо між сценами
        MakePersistent(transitionUI);
        if (persistentUI != null)
            foreach (var go in persistentUI) MakePersistent(go);

        SetUI(false);   // на старті гри UI переходу сховано
    }

    // Робить кореневий об'єкт безсмертним. Діти цього ROOT і так переживають сцени.
    void MakePersistent(GameObject go)
    {
        if (go == null || go == gameObject) return;
        if (go.transform.IsChildOf(transform)) return;
        if (go.transform.parent != null)
        {
            Debug.LogWarning("[TierTransition] UI '" + go.name + "' лежить не в корені сцени, відчіплюю від батька, щоб перенести між сценами.", go);
            go.transform.SetParent(null, true);
        }
        DontDestroyOnLoad(go);
    }

    void DestroyIfOutside(GameObject go)
    {
        if (go == null || go == gameObject) return;
        if (go.transform.IsChildOf(transform)) return;   // піде разом із цим ROOT
        Destroy(go);
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
        FollowCamera follow = FindFirstObjectByType<FollowCamera>();
        Camera cam = null;
        if (follow != null)
        {
            cam = follow.GetComponent<Camera>();
            if (cam == null) cam = follow.GetComponentInChildren<Camera>(true);
            if (cam == null) cam = follow.GetComponentInParent<Camera>();
        }
        if (cam == null) cam = FindCamera();
        SpeedEffects fx = cam != null ? cam.GetComponentInParent<SpeedEffects>() : null;
        if (fx == null && cam != null) fx = cam.GetComponentInChildren<SpeedEffects>(true);
        bool hasRig = carried != null && cam != null;

        if (follow == null)
            Debug.LogWarning("[TierTransition] FollowCamera не знайдено в поточній сцені: камера НЕ переноситься, у новій сцені буде її власна камера.", this);
        else if (carried == null)
            Debug.LogWarning("[TierTransition] RockAbsorber не знайдено: камера не переноситься.", this);

        Rig rig = null;
        float ratio0 = 1f, ratio1 = 1f;
        if (hasRig)
        {
            Freeze(carried, true);
            ratio0 = carried.SizeRatio;
            ratio1 = Mathf.Max(ratio0, carried.NextStageRatio);   // до якого розміру камінь виросте
            rig = BuildRig(cam, carried, carried.StartRadius * ratio1);
            if (follow != null) follow.enabled = false;
            if (fx != null) fx.enabled = false;                   // щоб не змагався з відльотом за FOV
        }

        bool fogOn = hasRig && useFog;
        Scene sceneBefore = SceneManager.GetActiveScene();
        if (fogOn) CaptureFog();

        SetUI(true);    // вмикаємо UI до Rebind/Play, бо на неактивному об'єкті Animator не працює
        ResetAnimators();
        Departing?.Invoke();

        // ===== 1-2. камінь росте, камера віддаляється (хмарок ще немає) =====
        float elapsed = 0f;
        bool grown = ratio1 <= ratio0;

        while (elapsed < flyDuration)
        {
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
            if (fogOn) SetFog(Mathf.Clamp01(elapsed / flyDuration));

            yield return null;
            elapsed += Time.unscaledDeltaTime;
        }
        if (hasRig && carried != null)
        {
            if (!grown) carried.SetSizeRatio(ratio1);
            Apply(rig, carried, 1f);
        }
        if (fogOn) SetFog(1f);

        // ===== 3. анімація 1: хмарки закривають екран =====
        Fire(coverAnimator, coverTrigger);
        yield return new WaitForSecondsRealtime(coverDuration);
        yield return new WaitForSecondsRealtime(holdClosed);

        FullyCovered?.Invoke();

        // камінь і камера їдуть у нову сцену: стають кореневими і не знищуються разом зі старою сценою
        if (carried != null)
        {
            carried.transform.SetParent(null, true);
            DontDestroyOnLoad(carried.gameObject);
        }
        // переносимо той об'єкт, де лежить FollowCamera (він сам ставить позицію камери щокадру, тож відчепити його безпечно)
        FollowCamera keptFollow = hasRig && follow != null ? follow : null;
        Camera keptCam = keptFollow != null ? cam : null;
        if (keptFollow != null)
        {
            keptFollow.transform.SetParent(null, true);
            DontDestroyOnLoad(keptFollow.gameObject);
            // якщо Camera лежить окремо від FollowCamera (не в його ієрархії), переносимо й її
            if (keptCam != null && !keptCam.transform.IsChildOf(keptFollow.transform))
            {
                keptCam.transform.SetParent(null, true);
                DontDestroyOnLoad(keptCam.gameObject);
            }
            Debug.Log("[TierTransition] Камера '" + keptFollow.name + "' переноситься в нову сцену.", this);
        }

        // ===== 4. завантаження сцени (екран закритий) =====
        bool approach = hasRig && carried != null && keptFollow != null && keptCam != null && approachDuration > 0f;
        bool fogCarry = false;

        AsyncOperation op = loader();
        while (!op.isDone) yield return null;
        if (fogOn)
        {
            if (SceneManager.GetActiveScene() == sceneBefore)
                RestoreFog();                    // сцена не змінилась (завантаження не вдалось): повертаємо старий туман
            else if (approach)
            {
                CaptureFog();                    // налаштування туману нової сцени
                SetFog(1f);                      // туман лишається, поки камера підлітає, і розсіюється разом із нею
                fogCarry = true;
            }
            // без підльоту камери туман просто зникає: діють налаштування нової сцени
        }

        // ===== 5. прибуття: екран ще закритий, поки нова сцена ініціалізується =====
        yield return null;
        yield return null;

        if (carried != null)
        {
            Arrive(carried, keptFollow, rig != null ? rig.fov0 : 0f);
            rock = carried;
        }
        else
        {
            rock = FindFirstObjectByType<RockAbsorber>();
        }

        // ===== 6. анімація 2: хмарки розлітаються, камера підлітає назад до каменя =====
        // Підліт камери стартує на approachLead секунд раніше за розліт хмарок.
        float lead = approach ? Mathf.Max(0f, approachLead) : 0f;
        bool released = false, revealed = false;
        if (approach)
        {
            approachCam = keptCam;
            approachRock = carried;
            approachFov = rig.fov0;
            approachRise = rig.rise;
            approachS = 1f;
            approachActive = true;     // камінь стоїть, поки камера не повернулась
        }
        else
        {
            ReleaseRock(carried, keptFollow);
            released = true;
        }

        float total = Mathf.Max(approach ? approachDuration : 0f, lead + revealDuration);
        float rt = 0f;
        while (true)
        {
            if (!revealed && rt >= lead)
            {
                revealed = true;
                Revealing?.Invoke();
                Fire(revealAnimator != null ? revealAnimator : coverAnimator, revealTrigger);
            }

            if (approach && !released)
            {
                float u = Mathf.Clamp01(rt / approachDuration);
                approachS = 1f - Mathf.SmoothStep(0f, 1f, u);
                if (fogCarry) SetFog(1f - u);   // дзеркально до відльоту
                if (u >= 1f)
                {
                    approachActive = false;
                    ReleaseRock(carried, keptFollow);
                    released = true;
                    if (fogCarry) { RestoreFog(); fogCarry = false; }
                }
            }

            if (rt >= total) break;
            yield return null;
            rt += Time.unscaledDeltaTime;
        }
        approachActive = false;
        if (!released) ReleaseRock(carried, keptFollow);
        if (fogCarry) RestoreFog();

        ResetAnimators();   // назад у стан за замовчуванням (екран чистий), готово до наступного переходу
        SetUI(false);       // і ховаємо UI до наступного переходу
        maxTimer = 0f;
        IsBusy = false;
        Finished?.Invoke();
    }

    // Нова сцена вже активна: ставимо камінь на місце заглушки і перепризначаємо посилання.
    void Arrive(RockAbsorber r, FollowCamera keepCam, float fov0)
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

        FollowCamera follow;
        Camera cam;
        if (keepCam != null)
        {
            // камера з попередньої сцени лишається, камеру нової сцени видаляємо
            follow = keepCam;
            cam = keepCam.GetComponent<Camera>();

            // відстань беремо з камери нової сцени (задана для стартового розміру), інакше з базової
            float baseDist = r.BaseCameraDistance;
            foreach (var other in FindObjectsByType<FollowCamera>(FindObjectsSortMode.None))
            {
                if (other == keepCam) continue;
                baseDist = other.distance;
                Destroy(other.gameObject);
            }
            keepCam.distance = baseDist;

            // інші камери нової сцени, що малюють на екран, прибираємо, щоб вони не перекривали перенесену
            foreach (var c in FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (c == cam || c == null) continue;
                if (cam != null && c.transform.IsChildOf(keepCam.transform)) continue;
                if (c.targetTexture != null) continue;                 // камери в RenderTexture (мінікарта тощо) не чіпаємо
                if (c.CompareTag("MainCamera") || c.GetComponent<FollowCamera>() != null)
                    Destroy(c.gameObject);
                else
                    Debug.LogWarning("[TierTransition] У новій сцені є ще одна камера '" + c.name + "'. Вона може перекривати перенесену; зніми з неї тег MainCamera або видали.", c);
            }
            // зайві AudioListener (лишається один, на перенесеній камері)
            foreach (var l in FindObjectsByType<AudioListener>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (!l.transform.IsChildOf(keepCam.transform) && (cam == null || !l.transform.IsChildOf(cam.transform)))
                    Destroy(l);

            if (cam != null && fov0 > 0f) cam.fieldOfView = fov0;
        }
        else
        {
            follow = FindFirstObjectByType<FollowCamera>();
            cam = FindCamera();
        }

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

        var speed = keepCam != null ? keepCam.GetComponent<SpeedEffects>() : FindFirstObjectByType<SpeedEffects>();
        if (speed != null) speed.target = r.GetComponent<Rigidbody>();
        // камінь розморожується і ефекти швидкості вмикаються пізніше, в ReleaseRock, коли камера повернулась
    }

    // Камінь знову керований, ефекти швидкості (лінії, FOV) знову вмикаються
    void ReleaseRock(RockAbsorber r, FollowCamera keepFollow)
    {
        if (r == null) return;
        Freeze(r, false);
        if (keepFollow != null)
        {
            var fx = keepFollow.GetComponent<SpeedEffects>();
            if (fx != null) fx.enabled = true;
        }
    }

    // ---------- зворотний підліт камери ----------

    bool approachActive;
    float approachS, approachFov, approachRise;
    Camera approachCam;
    RockAbsorber approachRock;

    // s = 1: камера високо над каменем (кінець відльоту), s = 0: звичайне місце за каменем.
    // Позицію «вдома» щокадру рахує FollowCamera, ми лише відводимо камеру від неї за тією ж формулою, що й Apply.
    void LateUpdate()
    {
        if (!approachActive || approachCam == null || approachRock == null) return;

        Transform ct = approachCam.transform;
        Vector3 focus = approachRock.WorldCenter;
        Vector3 off = ct.position - focus;
        float dist = Mathf.Max(off.magnitude, 0.1f);
        Vector3 home = off / dist;

        Vector3 flat = new Vector3(home.x, 0f, home.z);
        flat = flat.sqrMagnitude > 1e-4f ? flat.normalized : Vector3.back;
        Vector3 top = (Vector3.up + flat * 0.06f).normalized;

        float s = approachS;
        float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(s * 1.6f));
        Vector3 dir = Vector3.Slerp(home, top, k).normalized;
        float d = Mathf.Lerp(dist, dist + approachRise, s * s);

        ct.position = focus + dir * d;
        ct.rotation = Quaternion.LookRotation(-dir, Vector3.up);
        approachCam.fieldOfView = approachFov + fovBoost * s;
    }

    // ---------- туман ----------

    bool fogWas;
    Color fogColorWas;
    FogMode fogModeWas;
    float fogDensityWas, fogStartDensity;

    void CaptureFog()
    {
        fogWas = RenderSettings.fog;
        fogColorWas = RenderSettings.fogColor;
        fogModeWas = RenderSettings.fogMode;
        fogDensityWas = RenderSettings.fogDensity;

        // якщо в сцені вже був такий самий туман, плавно підсилюємо його, інакше піднімаємо з нуля
        fogStartDensity = fogWas && fogModeWas == FogMode.ExponentialSquared ? fogDensityWas : 0f;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogDensity = fogStartDensity;
        RenderSettings.fog = true;
    }

    // t = 0..1: прогрес відльоту камери (у новій сцені: 1 - прогрес підльоту).
    // Перші fogDelay відльоту туману немає, потім він плавно набирає густину до повної в кінці.
    void SetFog(float t)
    {
        float d = Mathf.Clamp(fogDelay, 0f, 0.95f);
        float s = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t - d) / (1f - d)));
        RenderSettings.fogDensity = Mathf.Lerp(fogStartDensity, fogDensity, s);
        RenderSettings.fogColor = fogWas ? Color.Lerp(fogColorWas, fogColor, s) : fogColor;
    }

    // Повертає значення, які зберіг CaptureFog (налаштування сцени, у якій він викликався)
    void RestoreFog()
    {
        RenderSettings.fog = fogWas;
        RenderSettings.fogColor = fogColorWas;
        RenderSettings.fogMode = fogModeWas;
        RenderSettings.fogDensity = fogDensityWas;
    }

    // ---------- UI ----------

    void SetUI(bool on)
    {
        if (transitionUI != null && transitionUI != gameObject)
            transitionUI.SetActive(on);
    }

    // ---------- аніматори ----------

    static void Fire(Animator a, string trigger)
    {
        if (a == null)
        {
            Debug.LogWarning("[TierTransition] Animator не призначено, анімація не запуститься.");
            return;
        }
        if (!a.gameObject.activeInHierarchy)
            Debug.LogWarning("[TierTransition] Animator '" + a.name + "' неактивний.", a);
        if (string.IsNullOrEmpty(trigger)) return;
        if (!HasParameter(a, trigger))
        {
            Debug.LogWarning("[TierTransition] У '" + a.name + "' немає параметра '" + trigger + "'.", a);
            return;
        }
        a.ResetTrigger(trigger);
        a.SetTrigger(trigger);
    }

    static bool HasParameter(Animator a, string name)
    {
        foreach (var p in a.parameters)
            if (p.name == name) return true;
        return false;
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