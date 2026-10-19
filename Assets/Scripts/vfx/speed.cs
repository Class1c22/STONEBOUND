using UnityEngine;
using UnityEngine.InputSystem;

// Керує лише FOV і кількістю частинок. Вигляд партіклів налаштовується в самому Particle System.
[RequireComponent(typeof(Camera))]
public class SpeedEffects : MonoBehaviour
{
    [Header("Ціль")]
    public Rigidbody target;

    [Header("FOV")]
    public float maxFovBoost = 25f;
    public float fovSmooth = 6f;
    [Tooltip("Мінімальний ефект FOV, коли тримаєш Shift")]
    public float sprintFovAmount = 0.6f;

    [Header("Швидкість для ефекту")]
    public float speedStart = 8f;
    public float speedFull = 25f;

    [Header("Партікли")]
    [Tooltip("Твій Particle System з лініями")]
    public ParticleSystem speedLines;
    [Tooltip("Кількість частинок на секунду при повній швидкості")]
    public float maxEmission = 150f;
    [Tooltip("Крива емісії: по горизонталі швидкість (0 = Speed Start, 1 = Speed Full), по вертикалі частка від Max Emission")]
    public AnimationCurve emissionCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
    [Tooltip("Яку частку емісії (0-1) давати, коли тримаєш Shift, навіть якщо швидкість мала")]
    [Range(0f, 1f)] public float sprintEmissionAmount = 0f;
    [Tooltip("Прив'язати лінії до камери, щоб вони рухались разом з нею")]
    public bool followCamera = true;

    Camera cam;
    float baseFov;
    bool started;

    void Start()
    {
        cam = GetComponent<Camera>();
        baseFov = cam.fieldOfView;

        if (speedLines != null)
        {
            if (followCamera)
            {
                // система стає дочірньою до камери, а частинки рухаються разом із нею
                if (speedLines.transform.parent != transform)
                    speedLines.transform.SetParent(transform, true);

                var m = speedLines.main;
                m.simulationSpace = ParticleSystemSimulationSpace.Local;
            }

            var e = speedLines.emission;
            e.rateOverTime = 0f;
            if (!speedLines.isPlaying) speedLines.Play();
        }

        started = true;
    }

    // Нова сцена: TierTransition знову вмикає скрипт, партікли оживають
    void OnEnable()
    {
        if (started) SetLines(true);
    }

    // Початок переходу: TierTransition вимикає скрипт, партікли зупиняються і зникають
    void OnDisable()
    {
        SetLines(false);
    }

    void SetLines(bool on)
    {
        if (speedLines == null) return;
        var e = speedLines.emission;
        e.rateOverTime = 0f;
        if (on)
        {
            if (!speedLines.isPlaying) speedLines.Play();
        }
        else
        {
            speedLines.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }
    }

    void LateUpdate()
    {
        if (target == null) return;

        var kb = Keyboard.current;
        bool sprint = kb != null && kb.leftShiftKey.isPressed;

        float speed = target.linearVelocity.magnitude;
        float t = Mathf.InverseLerp(speedStart, speedFull, speed);

        // FOV: швидкість або Shift
        float fovT = sprint ? Mathf.Max(t, sprintFovAmount) : t;
        float targetFov = baseFov + maxFovBoost * fovT;
        cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, targetFov, fovSmooth * Time.deltaTime);

        // Партікли: чим швидше, тим більше ліній
        if (speedLines != null)
        {
            float emissionT = sprint ? Mathf.Max(t, sprintEmissionAmount) : t;
            var e = speedLines.emission;
            e.rateOverTime = maxEmission * Mathf.Max(0f, emissionCurve.Evaluate(emissionT));
        }
    }
}