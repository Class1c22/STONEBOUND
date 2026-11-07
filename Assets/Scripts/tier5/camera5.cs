using System.Collections.Generic;
using UnityEngine;

// Камера для планети. Висить на Main Camera постійно (генератор додасть її сам, якщо її нема).
// Знайшла PlanetGenerator у сцені: АВТОМАТИЧНО вимикає всі FollowCamera (щокадру, поки активна) і сама керує камерою.
// Планети нема: нічого не чіпає і повертає FollowCamera в той стан, у якому він був.
// Камера стоїть позаду каменя й трохи вище, видно кривину планети. "Вгору" = від центру планети.
[DefaultExecutionOrder(1000)]
public class PlanetCamera : MonoBehaviour
{
    public PlanetGenerator planet;
    public RockAbsorber rock;

    [Header("Позиція відносно каменя (у радіусах каменя)")]
    public float distanceInRadii = 18f;
    public float heightInRadii = 4f;
    public float lookAtHeightInRadii = 0.5f;
    public float minDistance = 10f;

    [Header("Плавність")]
    public float positionSmooth = 0.1f;
    public float rotationSharpness = 12f;

    [Header("Камера за каменем (слідує за напрямком руху)")]
    public bool followHeading = true;
    [Tooltip("Швидкість по поверхні, від якої камера починає повертатись")]
    public float minHeadingSpeed = 2f;
    [Tooltip("Градусів за секунду")]
    public float headingTurnRate = 70f;
    [Tooltip("Якщо рух під більшим кутом до камери, камера не розвертається (щоб не крутилась при русі назад)")]
    public float maxFollowAngle = 80f;

    [Header("Інше")]
    public float minFarClip = 5000f;
    [Tooltip("Як часто (сек) перевіряти, чи є планета в сцені")]
    public float checkInterval = 0.5f;

    // усі FollowCamera, які ми вимкнули, і їхній початковий стан
    readonly List<FollowCamera> oldCams = new List<FollowCamera>();
    readonly Dictionary<FollowCamera, bool> oldState = new Dictionary<FollowCamera, bool>();

    bool active;
    float nextCheck;
    Rigidbody rockRb;

    Vector3 fwd;
    Vector3 vel;
    bool init;

    // Для керування каменем: рухай камінь по цих напрямках
    public bool Active { get { return active; } }
    public Vector3 Forward { get { return fwd; } }
    public Vector3 Up { get; private set; }
    public Vector3 Right { get { return Vector3.Cross(Up, fwd).normalized; } }

    void OnDisable()
    {
        SetActive(false);
    }

    // знаходимо всі FollowCamera: у сцені, на цьому об'єкті, у каменя
    void CollectOldCams()
    {
        var all = FindObjectsByType<FollowCamera>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++) Track(all[i]);

        var mine = GetComponentInParent<FollowCamera>(true);
        Track(mine);

        if (rock != null) Track(rock.followCamera);
    }

    void Track(FollowCamera f)
    {
        if (f == null || oldState.ContainsKey(f)) return;
        oldState[f] = f.enabled;
        oldCams.Add(f);
    }

    // вимикаємо всі знайдені FollowCamera (викликається щокадру, поки камера активна)
    void KeepOldCamsOff()
    {
        for (int i = 0; i < oldCams.Count; i++)
        {
            var f = oldCams[i];
            if (f != null && f.enabled) f.enabled = false;
        }
    }

    void RestoreOldCams()
    {
        for (int i = 0; i < oldCams.Count; i++)
        {
            var f = oldCams[i];
            if (f == null) continue;
            bool was;
            if (oldState.TryGetValue(f, out was) && was) f.enabled = true;
        }
        oldCams.Clear();
        oldState.Clear();
    }

    void SetActive(bool on)
    {
        if (on == active) return;
        active = on;
        init = false;

        if (on)
        {
            var c = GetComponent<Camera>();
            if (c != null && c.farClipPlane < minFarClip) c.farClipPlane = minFarClip;

            CollectOldCams();
            KeepOldCamsOff();
        }
        else
        {
            RestoreOldCams();
        }
    }

    // Update виконується після Update інших скриптів (порядок 1000): якщо хтось увімкнув FollowCamera назад, вимикаємо до LateUpdate
    void Update()
    {
        if (active) KeepOldCamsOff();
    }

    void LateUpdate()
    {
        // раз на checkInterval дивимось, чи є планета і камінь
        if (Time.unscaledTime >= nextCheck || (active && (planet == null || rock == null)))
        {
            nextCheck = Time.unscaledTime + checkInterval;

            if (planet == null || !planet.isActiveAndEnabled)
                planet = FindFirstObjectByType<PlanetGenerator>();
            if (planet != null && !planet.isActiveAndEnabled) planet = null;

            if (planet != null && rock == null)
                rock = FindFirstObjectByType<RockAbsorber>();

            if (rock != null && rockRb == null)
                rockRb = rock.GetComponentInParent<Rigidbody>();

            bool on = planet != null && rock != null;
            SetActive(on);

            // з'явились нові FollowCamera (наприклад після переходу між сценами)
            if (active)
            {
                CollectOldCams();
                KeepOldCamsOff();
            }
        }

        if (!active || planet == null || rock == null) return;

        KeepOldCamsOff();

        Vector3 c = rock.WorldCenter;
        Vector3 up = planet.UpAt(c);
        Up = up;

        if (!init)
        {
            fwd = Vector3.ProjectOnPlane(transform.forward, up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(Vector3.forward, up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(Vector3.right, up);
            fwd.Normalize();
            transform.position = c + up * rock.Radius * heightInRadii - fwd * Mathf.Max(minDistance, rock.Radius * distanceInRadii);
            vel = Vector3.zero;
            init = true;
        }

        // переносимо "вперед" по сфері: проєкція на дотичну площину в новій точці
        Vector3 f = Vector3.ProjectOnPlane(fwd, up);
        if (f.sqrMagnitude < 1e-6f) f = Vector3.ProjectOnPlane(transform.right, up);
        fwd = f.normalized;

        // камера повільно розвертається за напрямком руху каменя
        if (followHeading && rockRb != null && !rockRb.isKinematic)
        {
            Vector3 v = Vector3.ProjectOnPlane(rockRb.linearVelocity, up);
            if (v.magnitude > minHeadingSpeed)
            {
                float ang = Vector3.SignedAngle(fwd, v.normalized, up);
                if (Mathf.Abs(ang) < maxFollowAngle)
                {
                    float maxStep = headingTurnRate * Time.deltaTime;
                    float step = Mathf.Clamp(ang, -maxStep, maxStep);
                    fwd = (Quaternion.AngleAxis(step, up) * fwd).normalized;
                }
            }
        }

        float r = rock.Radius;
        float back = Mathf.Max(minDistance, r * distanceInRadii);
        Vector3 target = c + up * (r * heightInRadii) - fwd * back;
        transform.position = Vector3.SmoothDamp(transform.position, target, ref vel, positionSmooth);

        Vector3 look = c + up * (r * lookAtHeightInRadii) - transform.position;
        if (look.sqrMagnitude > 1e-6f)
        {
            Quaternion want = Quaternion.LookRotation(look, up);
            transform.rotation = Quaternion.Slerp(transform.rotation, want, 1f - Mathf.Exp(-rotationSharpness * Time.deltaTime));
        }
    }
}