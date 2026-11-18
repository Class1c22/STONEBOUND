using UnityEngine;

// Запускає фінальну синематику, коли камінь з'їв достатньо кори планети.
// Дивиться на ту саму цілісність кори, що й PixelTubeBar (PlanetGenerator.Integrity01).
public class PlanetEndTrigger : MonoBehaviour
{
    [Header("Зв'язки (порожньо = знайдеться саме)")]
    public PlanetGenerator planet;
    public EndingCinematicStarter starter;

    [Header("Умова")]
    [Tooltip("Яка частка кори має бути з'їдена (0.9 = 90%). Підбери за логом у Console")]
    [Range(0.3f, 1f)] public float eatenThreshold = 0.9f;
    [Tooltip("Скільки секунд поріг має трим=> не спрацює від випадкового стрибка")]
    public float holdSeconds = 1f;
    [Tooltip("Як часто перевіряти (сек)")]
    public float checkInterval = 0.25f;

    [Header("Діагностика")]
    public bool debugLog = true;
    public float logInterval = 2f;

    bool fired;
    float holdTimer;
    float nextCheck;
    float nextLog;

    void Start()
    {
        if (planet == null) planet = FindFirstObjectByType<PlanetGenerator>();
        if (starter == null) starter = FindFirstObjectByType<EndingCinematicStarter>(FindObjectsInactive.Include);

        if (planet == null) Debug.LogWarning("[PlanetEndTrigger] Не знайдено PlanetGenerator", this);
        if (starter == null) Debug.LogWarning("[PlanetEndTrigger] Не знайдено EndingCinematicStarter", this);
    }

    void Update()
    {
        if (fired || planet == null || starter == null || !planet.Ready) return;
        if (Time.time < nextCheck) return;
        nextCheck = Time.time + Mathf.Max(0.05f, checkInterval);

        float eaten = 1f - planet.Integrity01;

        if (debugLog && Time.time >= nextLog)
        {
            nextLog = Time.time + Mathf.Max(0.5f, logInterval);
            Debug.Log("[PlanetEndTrigger] З'їдено " + Mathf.RoundToInt(eaten * 100f) + "% (поріг " +
                      Mathf.RoundToInt(eatenThreshold * 100f) + "%)", this);
        }

        if (eaten >= eatenThreshold)
        {
            holdTimer += checkInterval;
            if (holdTimer >= holdSeconds) Fire();
        }
        else
        {
            holdTimer = 0f;
        }
    }

    [ContextMenu("Fire (тест)")]
    public void Fire()
    {
        if (fired) return;
        fired = true;
        if (debugLog) Debug.Log("[PlanetEndTrigger] Запуск фінальної синематики", this);
        if (starter != null) starter.Begin();
    }
}