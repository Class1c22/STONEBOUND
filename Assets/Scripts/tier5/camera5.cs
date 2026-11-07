using UnityEngine;

// Камера для планети. Висить на Main Camera постійно.
// Знайшла PlanetGenerator у сцені: вимикає FollowCamera і сама керує камерою.
// Планети нема: нічого не чіпає і повертає FollowCamera.
// Позиція завжди одна відносно каменя: позаду й вище, "вгору" = від центру планети.
public class PlanetCamera : MonoBehaviour
{
    public PlanetGenerator planet;
    public RockAbsorber rock;

    [Header("Позиція відносно каменя (у радіусах каменя)")]
    public float distanceInRadii = 14f;
    public float heightInRadii = 7f;
    public float lookAtHeightInRadii = 0.5f;
    public float minDistance = 8f;

    [Header("Плавність")]
    public float positionSmooth = 0.1f;
    public float rotationSharpness = 12f;

    [Header("Пошук")]
    [Tooltip("Як часто (сек) перевіряти, чи є планета в сцені")]
    public float checkInterval = 0.5f;

    Behaviour oldCam;
    bool oldWasEnabled;
    bool active;
    float nextCheck;

    Vector3 fwd;
    Vector3 vel;
    bool init;

    // Для керування каменем: рухай камінь по цих напрямках
    public bool Active { get { return active; } }
    public Vector3 Forward { get { return fwd; } }
    public Vector3 Up { get; private set; }
    public Vector3 Right { get { return Vector3.Cross(Up, fwd).normalized; } }

    void Awake()
    {
        FindOldCamera();
    }

    void OnDisable()
    {
        SetActive(false);
    }

    void FindOldCamera()
    {
        oldCam = GetComponent("FollowCamera") as Behaviour;
        if (oldCam == null)
        {
            var t = System.Type.GetType("FollowCamera");
            if (t != null) oldCam = GetComponentInParent(t) as Behaviour;
        }
    }

    void SetActive(bool on)
    {
        if (on == active) return;
        active = on;
        init = false;

        if (oldCam == null) FindOldCamera();
        if (oldCam == null) return;

        if (on)
        {
            oldWasEnabled = oldCam.enabled;
            oldCam.enabled = false;
        }
        else if (oldWasEnabled)
        {
            oldCam.enabled = true;
        }
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

            SetActive(planet != null && rock != null);
        }

        if (!active || planet == null || rock == null) return;

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