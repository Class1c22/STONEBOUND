using UnityEngine;
using UnityEngine.InputSystem;

// виконується після FollowCamera, щоб партікли не відставали від камери
[DefaultExecutionOrder(100)]
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
    [Tooltip("Свій Particle System зі сцени. Якщо порожньо, скрипт створить простий сам")]
    public ParticleSystem customParticles;
    public Material particleMaterial;
    public float maxEmission = 150f;
    [Tooltip("Товщина лінії")]
    public float particleSize = 0.04f;
    [Tooltip("Довжина лінії залежно від швидкості частинки (більше = довші лінії)")]
    public float lineStretch = 0.15f;

    [Header("Форма потоку")]
    [Tooltip("Відстань перед камерою, звідки стартують лінії")]
    public float spawnDistance = 25f;
    [Tooltip("Радіус кільця, з якого вони стартують")]
    public float ringRadius = 18f;
    [Tooltip("0 = кільце тонке, 1 = суцільне коло. Менше = більше порожнього місця в центрі")]
    [Range(0.05f, 1f)] public float ringThickness = 0.7f;
    [Tooltip("Мінімальна швидкість польоту в бік камери")]
    public float minTowardSpeed = 25f;
    [Tooltip("0 = лінії летять паралельно погляду, 1 = точно в камеру (але тоді виглядають як точки)")]
    [Range(0f, 1f)] public float convergence = 0.5f;
    [Tooltip("Додаткове розходження від центру до країв (0 = прямі лінії)")]
    public float radialGain = 0f;

    Camera cam;
    float baseFov;
    ParticleSystem ps;
    Transform psTransform;
    ParticleSystem.Particle[] buffer;

    void Start()
    {
        cam = GetComponent<Camera>();
        baseFov = cam.fieldOfView;
        CreateParticles();
        ApplyLineLook();
        buffer = new ParticleSystem.Particle[ps.main.maxParticles];
    }

    void CreateParticles()
    {
        if (customParticles != null)
        {
            ps = customParticles;
            psTransform = ps.transform;
            var cm = ps.main;
            cm.simulationSpace = ParticleSystemSimulationSpace.World;
            cm.startSpeed = 0f;
            var ce = ps.emission;
            ce.rateOverTime = 0f;
            ps.Play();
            return;
        }

        var go = new GameObject("SpeedLines");
        psTransform = go.transform;
        ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.loop = true;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startSize = particleSize;
        main.startColor = Color.white;
        main.startSpeed = 0f;
        main.maxParticles = 800;

        var emission = ps.emission;
        emission.rateOverTime = 0f;

        var shape = ps.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Circle;
        shape.radius = ringRadius;
        shape.radiusThickness = ringThickness;

        var r = go.GetComponent<ParticleSystemRenderer>();
        r.renderMode = ParticleSystemRenderMode.Stretch;
        r.lengthScale = 4f;
        r.velocityScale = 0.02f;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.material = particleMaterial != null ? particleMaterial : CreateDefaultMaterial();

        ps.Play();
    }

    // робить з частинок витягнуті лінії (працює і для свого Particle System)
    void ApplyLineLook()
    {
        var r = ps.GetComponent<ParticleSystemRenderer>();
        r.renderMode = ParticleSystemRenderMode.Stretch;
        r.velocityScale = lineStretch;
        r.lengthScale = 2f;
        r.alignment = ParticleSystemRenderSpace.View;

        var main = ps.main;
        main.startSize = particleSize;
    }

    Material CreateDefaultMaterial()
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        var mat = new Material(sh);
        mat.SetColor("_BaseColor", Color.white);
        mat.color = Color.white;
        return mat;
    }

    void LateUpdate()
    {
        if (target == null || ps == null) return;

        var kb = Keyboard.current;
        bool sprint = kb != null && kb.leftShiftKey.isPressed;

        float speed = target.linearVelocity.magnitude;
        float t = Mathf.InverseLerp(speedStart, speedFull, speed);

        // FOV: швидкість або Shift
        float fovT = sprint ? Mathf.Max(t, sprintFovAmount) : t;
        float targetFov = baseFov + maxFovBoost * fovT;
        cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, targetFov, fovSmooth * Time.deltaTime);

        Vector3 camPos = cam.transform.position;
        Vector3 camFwd = cam.transform.forward;

        // кільце перед камерою, перпендикулярно до напрямку погляду
        psTransform.position = camPos + camFwd * spawnDistance;
        psTransform.rotation = Quaternion.LookRotation(-camFwd);

        float toward = Mathf.Max(speed * 1.2f, minTowardSpeed);
        var main = ps.main;
        main.startLifetime = spawnDistance * 1.15f / toward;

        var emission = ps.emission;
        emission.rateOverTime = speed > 1f ? Mathf.Lerp(0f, maxEmission, t) : 0f;

        // кожній частинці задаємо рух: в бік камери + розходження від осі погляду.
        // від цього лінії виглядають як дуги від центру до країв екрана
        int count = ps.GetParticles(buffer);
        for (int i = 0; i < count; i++)
        {
            Vector3 rel = buffer[i].position - camPos;
            Vector3 radial = rel - camFwd * Vector3.Dot(rel, camFwd);
            float r = radial.magnitude;
            Vector3 radialDir = r > 0.001f ? radial / r : Vector3.zero;

            Vector3 toCam = camPos - buffer[i].position;
            float dist = toCam.magnitude;
            Vector3 aimDir = dist > 0.001f ? toCam / dist : -camFwd;
            Vector3 flyDir = Vector3.Slerp(-camFwd, aimDir, convergence);

            buffer[i].velocity = flyDir * toward + radialDir * (r * radialGain);
        }
        ps.SetParticles(buffer, count);
    }
}