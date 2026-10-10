using UnityEngine;
using UnityEngine.InputSystem;

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
    public Material particleMaterial;
    public float maxEmission = 150f;
    public float spawnDistance = 25f;
    public float particleSize = 0.06f;

    Camera cam;
    float baseFov;
    ParticleSystem ps;
    Transform psTransform;

    void Start()
    {
        cam = GetComponent<Camera>();
        baseFov = cam.fieldOfView;
        CreateParticles();
    }

    void CreateParticles()
    {
        var go = new GameObject("SpeedLines");
        psTransform = go.transform;
        ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.loop = true;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startSize = particleSize;
        main.startColor = Color.white;
        main.maxParticles = 800;

        var emission = ps.emission;
        emission.rateOverTime = 0f;

        var shape = ps.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(24f, 14f, 1f);

        var r = go.GetComponent<ParticleSystemRenderer>();
        r.renderMode = ParticleSystemRenderMode.Stretch;
        r.lengthScale = 4f;
        r.velocityScale = 0.02f;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.material = particleMaterial != null ? particleMaterial : CreateDefaultMaterial();

        ps.Play();
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
        if (target == null) return;

        var kb = Keyboard.current;
        bool sprint = kb != null && kb.leftShiftKey.isPressed;

        Vector3 vel = target.linearVelocity;
        float speed = vel.magnitude;
        float t = Mathf.InverseLerp(speedStart, speedFull, speed);

        // FOV: швидкість або Shift
        float fovT = sprint ? Mathf.Max(t, sprintFovAmount) : t;
        float targetFov = baseFov + maxFovBoost * fovT;
        cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, targetFov, fovSmooth * Time.deltaTime);

        // Партікли: з'являються попереду по ходу руху і летять повз камеру
        var emission = ps.emission;
        if (speed > 1f)
        {
            Vector3 dir = vel / speed;
            psTransform.position = target.position + dir * spawnDistance;
            psTransform.rotation = Quaternion.LookRotation(-dir);

            float startSpeed = Mathf.Max(speed * 1.2f, 20f);
            var main = ps.main;
            main.startSpeed = startSpeed;
            main.startLifetime = spawnDistance * 2.2f / startSpeed;

            emission.rateOverTime = Mathf.Lerp(0f, maxEmission, t);
        }
        else
        {
            emission.rateOverTime = 0f;
        }
    }
}