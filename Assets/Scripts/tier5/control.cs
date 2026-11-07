using UnityEngine;
using UnityEngine.InputSystem;

// Керування каменем на планеті. Повісь на камінь (або будь-який об'єкт у сцені).
// Рух відносно камери: W/стік вперед = від камери, A/D = вліво/вправо від камери.
// Працює, коли PlanetCamera активна (є планета). Тоді старий RockController вимикається, інакше вмикається назад.
public class PlanetRockController : MonoBehaviour
{
    public PlanetGenerator planet;
    public PlanetCamera cam;
    public RockAbsorber rock;

    [Header("Рух")]
    public float acceleration = 45f;
    [Tooltip("Максимальна швидкість по поверхні, м/с")]
    public float maxSpeed = 30f;
    [Tooltip("Гальмування, коли кнопки відпущено (0 = котиться по інерції)")]
    public float idleDrag = 0.4f;

    Rigidbody rb;
    Behaviour oldController;
    bool oldWasEnabled;
    bool active;
    Vector2 input;

    void OnDisable()
    {
        SetActive(false);
    }

    static Behaviour FindBehaviour(string typeName, Component near)
    {
        var t = System.Type.GetType(typeName);
        if (t == null) return null;
        Behaviour b = null;
        if (near != null) b = near.GetComponentInParent(t) as Behaviour;
        if (b == null) b = FindFirstObjectByType(t) as Behaviour;
        return b;
    }

    void SetActive(bool on)
    {
        if (on == active) return;
        active = on;

        if (oldController == null) oldController = FindBehaviour("RockController", rock);
        if (oldController == null) return;

        if (on)
        {
            oldWasEnabled = oldController.enabled;
            oldController.enabled = false;
        }
        else if (oldWasEnabled)
        {
            oldController.enabled = true;
        }
    }

    void Update()
    {
        if (planet == null) planet = FindFirstObjectByType<PlanetGenerator>();
        if (cam == null) cam = FindFirstObjectByType<PlanetCamera>();
        if (rock == null) rock = FindFirstObjectByType<RockAbsorber>();
        if (rock != null && rb == null) rb = rock.GetComponentInParent<Rigidbody>();

        SetActive(planet != null && cam != null && cam.Active && rock != null && rb != null);
        if (!active) { input = Vector2.zero; return; }

        Vector2 v = Vector2.zero;
        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) v.y += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) v.y -= 1f;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) v.x += 1f;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) v.x -= 1f;
        }
        var gp = Gamepad.current;
        if (gp != null) v += gp.leftStick.ReadValue();

        input = Vector2.ClampMagnitude(v, 1f);
    }

    void FixedUpdate()
    {
        if (!active || rb == null || rb.isKinematic) return;

        Vector3 up = planet.UpAt(rb.worldCenterOfMass);
        Vector3 vt = Vector3.ProjectOnPlane(rb.linearVelocity, up);

        if (input.sqrMagnitude > 0.0001f)
        {
            // напрямок відносно камери, у дотичній площині планети
            Vector3 dir = cam.Forward * input.y + cam.Right * input.x;
            dir = Vector3.ProjectOnPlane(dir, up);
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
}