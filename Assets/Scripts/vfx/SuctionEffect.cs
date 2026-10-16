using UnityEngine;
using UnityEngine.InputSystem;

// Повісь на камінь (де є RockAbsorber) і перетягни в поле Suction Particles систему частинок.
// Ефект працює, поки затиснута E; після відпускання нові частинки не з'являються,
// а ті, що вже летять, спокійно долітають і зникають.
public class SuctionEffect : MonoBehaviour
{
    [Tooltip("Particle System з ефектом всмоктування (дочірній об'єкт каменя)")]
    public ParticleSystem suctionParticles;

    [Tooltip("Збільшувати ефект разом із ростом каменя")]
    public bool scaleWithRock = true;

    RockAbsorber absorber;
    Vector3 baseScale = Vector3.one;
    bool playing;

    void Awake()
    {
        absorber = GetComponent<RockAbsorber>();

        if (suctionParticles == null)
        {
            Debug.LogWarning("[SuctionEffect] Не призначено Suction Particles", this);
            enabled = false;
            return;
        }

        baseScale = suctionParticles.transform.localScale;

        // щоб ефект не стартував сам при запуску гри
        var main = suctionParticles.main;
        main.playOnAwake = false;
        suctionParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
    }

    void Update()
    {
        var kb = Keyboard.current;
        bool pressed = kb != null && kb.eKey.isPressed;

        if (pressed && !playing)
        {
            suctionParticles.Play();
            playing = true;
        }
        else if (!pressed && playing)
        {
            // не обриваємо одразу: наявні частинки долітають
            suctionParticles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            playing = false;
        }

        // ефект росте разом з камінням (коллайдер росте, а root-scale ні)
        if (scaleWithRock && absorber != null)
            suctionParticles.transform.localScale = baseScale * absorber.SizeRatio;
    }
}
