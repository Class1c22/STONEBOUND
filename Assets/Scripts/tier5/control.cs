using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

// Керування каменем на планеті. Створюється САМ у кожній сцені, нічого вішати не треба.
// Сам шукає PlanetGenerator, камінь і камеру. Є планета: керує каменем відносно камери
// (W/стік вперед = від камери), а старі скрипти руху на камені вимикаються. Нема планети: нічого не робить.
// Коли планета готова, камінь примусово стає динамічним (isKinematic = false, constraints = None).
public class PlanetRockController : MonoBehaviour
{
    [Header("Рух")]
    public float acceleration = 45f;
    [Tooltip("Максимальна швидкість по поверхні, м/с")]
    public float maxSpeed = 30f;
    [Tooltip("Гальмування, коли кнопки відпущено (0 = котиться по інерції)")]
    public float idleDrag = 0.4f;

    [Header("Пошук")]
    public float checkInterval = 0.5f;

    [Header("Старі скрипти руху")]
    [Tooltip("Вимикати на камені скрипти, у назві яких є ці слова")]
    public string[] disableNameParts = { "Controller", "Move", "Input", "Player", "Wind" };

    [Header("Дебаг")]
    [Tooltip("Панель зі станом у лівому верхньому куті")]
    public bool showDebug = true;

    PlanetGenerator planet;
    PlanetCamera planetCam;
    RockAbsorber rock;
    Rigidbody rb;

    readonly List<Behaviour> disabled = new List<Behaviour>();
    bool active;
    float nextCheck;
    Vector2 input;
    string inputSource = "-";

    // ---------- автостворення ----------

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        Ensure();
    }

    static void OnSceneLoaded(Scene s, LoadSceneMode m)
    {
        Ensure();
    }

    static void Ensure()
    {
        if (FindFirstObjectByType<PlanetRockController>() != null) return;
        new GameObject("PlanetRockController").AddComponent<PlanetRockController>();
    }

    // ---------- вимкнення старих скриптів руху ----------

    void OnDisable()
    {
        SetActive(false);
    }

    bool IsOldMover(Behaviour b)
    {
        if (b == null || b == this) return false;
        if (b is RockAbsorber || b is PlanetRockController || b is PlanetCamera || b is PlanetGenerator) return false;
        string n = b.GetType().Name;
        if (n.Contains("RockBlast")) return false;      // R: розкидати шматки, це не рух
        for (int i = 0; i < disableNameParts.Length; i++)
            if (!string.IsNullOrEmpty(disableNameParts[i]) && n.Contains(disableNameParts[i])) return true;
        return false;
    }

    void DisableOldMovers()
    {
        if (rock == null) return;

        var found = new HashSet<Behaviour>();
        foreach (var b in rock.GetComponentsInParent<Behaviour>(true)) found.Add(b);
        foreach (var b in rock.GetComponentsInChildren<Behaviour>(true)) found.Add(b);
        if (rb != null)
        {
            foreach (var b in rb.GetComponentsInParent<Behaviour>(true)) found.Add(b);
            foreach (var b in rb.GetComponentsInChildren<Behaviour>(true)) found.Add(b);
        }

        foreach (var b in found)
        {
            if (!IsOldMover(b) || !b.enabled) continue;
            b.enabled = false;
            disabled.Add(b);
        }
    }

    void RestoreOldMovers()
    {
        for (int i = 0; i < disabled.Count; i++)
            if (disabled[i] != null) disabled[i].enabled = true;
        disabled.Clear();
    }

    void SetActive(bool on)
    {
        if (on == active) return;
        active = on;
        if (on) DisableOldMovers();
        else RestoreOldMovers();
    }

    // ---------- пошук ----------

    void Search()
    {
        if (planet == null || !planet.isActiveAndEnabled)
        {
            planet = FindFirstObjectByType<PlanetGenerator>();
            if (planet != null && !planet.isActiveAndEnabled) planet = null;
        }

        if (rock == null) rock = FindFirstObjectByType<RockAbsorber>();

        if (rock != null && rb == null)
        {
            rb = rock.GetComponentInParent<Rigidbody>();
            if (rb == null) rb = rock.GetComponentInChildren<Rigidbody>();
        }

        if (planetCam == null) planetCam = FindFirstObjectByType<PlanetCamera>();

        // якщо щось нове з'явилось на камені, теж вимикаємо
        if (active) DisableOldMovers();
    }

    // ---------- вхід ----------

    void Update()
    {
        if (Time.unscaledTime >= nextCheck || (active && (planet == null || rock == null || rb == null)))
        {
            nextCheck = Time.unscaledTime + checkInterval;
            Search();
        }

        bool on = planet != null && rock != null && rb != null;
        SetActive(on);
        if (!active) { input = Vector2.zero; return; }

        Vector2 v = Vector2.zero;
        var kb = Keyboard.current;
        if (kb != null)
        {
            inputSource = "Input System (клавіатура)";
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) v.y += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) v.y -= 1f;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) v.x += 1f;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) v.x -= 1f;
        }
        else
        {
            // запасний варіант: старий Input (працює, якщо Active Input Handling = Both/Old)
            try
            {
                v.x = Input.GetAxisRaw("Horizontal");
                v.y = Input.GetAxisRaw("Vertical");
                inputSource = "старий Input";
            }
            catch (System.Exception)
            {
                inputSource = "НЕМАЄ КЛАВІАТУРИ";
            }
        }
        var gp = Gamepad.current;
        if (gp != null) v += gp.leftStick.ReadValue();

        input = Vector2.ClampMagnitude(v, 1f);
    }

    // напрямки "вперед" і "вправо" відносно камери в дотичній площині планети
    bool GetAxes(Vector3 up, out Vector3 fwd, out Vector3 right)
    {
        fwd = Vector3.zero;
        right = Vector3.zero;

        if (planetCam != null && planetCam.Active)
        {
            fwd = planetCam.Forward;
            right = planetCam.Right;
        }
        else
        {
            var cam = Camera.main;
            if (cam == null) return false;
            fwd = Vector3.ProjectOnPlane(cam.transform.forward, up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(cam.transform.up, up);
            if (fwd.sqrMagnitude < 1e-4f) return false;
            fwd.Normalize();
            right = Vector3.Cross(up, fwd).normalized;
        }
        return true;
    }

    void FixedUpdate()
    {
        if (!active || rb == null || planet == null) return;

        // поки планета генерується, камінь заморожений генератором
        if (!planet.Ready) return;

        // після генерації камінь має бути звичайним динамічним тілом
        if (rb.isKinematic) rb.isKinematic = false;
        if (rb.constraints != RigidbodyConstraints.None) rb.constraints = RigidbodyConstraints.None;
        if (rb.IsSleeping()) rb.WakeUp();

        Vector3 up = planet.UpAt(rb.worldCenterOfMass);
        Vector3 vt = Vector3.ProjectOnPlane(rb.linearVelocity, up);

        if (input.sqrMagnitude > 0.0001f)
        {
            Vector3 f, r;
            if (!GetAxes(up, out f, out r)) return;

            Vector3 dir = Vector3.ProjectOnPlane(f * input.y + r * input.x, up);
            if (dir.sqrMagnitude > 1e-6f)
            {
                dir.Normalize();
                // не розганяємо вище maxSpeed у напрямку руху
                if (Vector3.Dot(vt, dir) < maxSpeed)
                    rb.AddForce(dir * acceleration * input.magnitude, ForceMode.Acceleration);
            }
        }
        else if (idleDrag > 0f)
        {
            rb.AddForce(-vt * idleDrag, ForceMode.Acceleration);
        }
    }

    // ---------- дебаг-панель ----------

    void OnGUI()
    {
        if (!showDebug) return;

        string s = "";
        s += "PlanetRockController: " + (active ? "АКТИВНИЙ" : "неактивний") + "\n";
        s += "Планета: " + (planet != null ? "є, Ready=" + planet.Ready + ", " + Mathf.RoundToInt(planet.Progress * 100f) + "%" : "НЕ ЗНАЙДЕНО") + "\n";
        s += "Камінь (RockAbsorber): " + (rock != null ? "є" : "НЕ ЗНАЙДЕНО") + "\n";
        if (rb != null)
        {
            s += "Rigidbody: " + rb.name + ", kinematic=" + rb.isKinematic + ", constraints=" + rb.constraints + "\n";
            s += "Швидкість: " + rb.linearVelocity.magnitude.ToString("F1") + " м/с, спить=" + rb.IsSleeping() + "\n";
        }
        else
        {
            s += "Rigidbody: НЕ ЗНАЙДЕНО\n";
        }
        s += "Вхід: " + input + " (" + inputSource + ")\n";
        s += "PlanetCamera: " + (planetCam != null ? (planetCam.Active ? "активна" : "неактивна") : "НЕМАЄ") + "\n";

        string names = "";
        for (int i = 0; i < disabled.Count; i++)
            if (disabled[i] != null) names += disabled[i].GetType().Name + " ";
        s += "Вимкнено скрипти: " + (names.Length > 0 ? names : "жодного") + "\n";
        s += "timeScale: " + Time.timeScale;

        var style = new GUIStyle(GUI.skin.label) { fontSize = 14 };
        style.normal.textColor = Color.yellow;
        GUI.Box(new Rect(8, 8, 560, 190), GUIContent.none);
        GUI.Label(new Rect(14, 12, 550, 185), s, style);
    }
}