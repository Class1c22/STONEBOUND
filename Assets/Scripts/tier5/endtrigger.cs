using System.Collections;
using UnityEngine;

// Запускає фінальну синематику, коли бар цілісності планети (PixelTubeBar) порожній.
// Значення бару рахується ТІЄЮ САМОЮ формулою, що й у PixelTubeBar:
//   shown = 1 - (1 - Integrity01) * planetLossScale
// Planet Loss Scale береться прямо з PixelTubeBar, тож тригер завжди збігається з тим, що видно на екрані.
public class PlanetEndTrigger : MonoBehaviour
{
    [Header("Зв'язки (порожньо = знайдеться саме)")]
    public PlanetGenerator planet;
    public EndingCinematicStarter starter;
    public PixelTubeBar bar;
    [Tooltip("Вибух планети. Порожньо = знайдеться, а якщо немає, додасться автоматично")]
    public PlanetExplosion explosion;

    [Header("Умова")]
    [Tooltip("Бар вважається порожнім, коли його заповнення <= цього значення (0.02 = 2%). Значення бару, а не кори")]
    [Range(0f, 0.3f)] public float barEmptyThreshold = 0.02f;
    [Tooltip("Якщо PixelTubeBar не знайдено, беремо цей множник (як Planet Loss Scale)")]
    [Min(1f)] public float fallbackLossScale = 10f;
    [Tooltip("Скільки секунд бар має лишатись порожнім, щоб не спрацювало випадково")]
    public float holdSeconds = 1f;
    public float checkInterval = 0.25f;

    [Header("Вибух")]
    [Tooltip("Підірвати планету, коли бар порожній")]
    public bool explodePlanet = true;
    [Tooltip("Через скільки секунд після спрацювання тригера планета вибухає")]
    public float explosionDelay = 2f;
    [Tooltip("Через скільки секунд після спрацювання тригера стартує синематика (0 = одразу). Рахується окремо від вибуху")]
    public float cinematicDelay = 0.5f;

    [Header("Діагностика")]
    public bool debugLog = true;
    public float logInterval = 2f;

    bool fired;
    float holdTimer, nextCheck, nextLog;

    void Start()
    {
        if (planet == null) planet = FindFirstObjectByType<PlanetGenerator>();
        if (starter == null) starter = FindFirstObjectByType<EndingCinematicStarter>(FindObjectsInactive.Include);
        if (bar == null) bar = FindFirstObjectByType<PixelTubeBar>(FindObjectsInactive.Include);
        if (explosion == null) explosion = FindFirstObjectByType<PlanetExplosion>(FindObjectsInactive.Include);
        if (explosion == null && explodePlanet) explosion = gameObject.AddComponent<PlanetExplosion>();

        if (planet == null) Debug.LogWarning("[PlanetEndTrigger] Не знайдено PlanetGenerator", this);
        if (starter == null) Debug.LogWarning("[PlanetEndTrigger] Не знайдено EndingCinematicStarter", this);
        if (bar == null) Debug.LogWarning("[PlanetEndTrigger] PixelTubeBar не знайдено, беру Fallback Loss Scale", this);
    }

    // Те саме, що малює PixelTubeBar на планеті
    float BarValue()
    {
        if (bar == null) bar = FindFirstObjectByType<PixelTubeBar>(FindObjectsInactive.Include);
        float scale = bar != null ? bar.planetLossScale : fallbackLossScale;
        float integrity = planet.Integrity01;
        return Mathf.Clamp01(1f - (1f - integrity) * Mathf.Max(1f, scale));
    }

    void Update()
    {
        if (fired || planet == null || starter == null || !planet.Ready) return;
        if (Time.time < nextCheck) return;

        float step = Mathf.Max(0.05f, checkInterval);
        nextCheck = Time.time + step;

        float shown = BarValue();

        if (debugLog && Time.time >= nextLog)
        {
            nextLog = Time.time + Mathf.Max(0.5f, logInterval);
            Debug.Log("[PlanetEndTrigger] Бар " + Mathf.RoundToInt(shown * 100f) + "% (кора " +
                      Mathf.RoundToInt(planet.Integrity01 * 100f) + "%), поріг " +
                      Mathf.RoundToInt(barEmptyThreshold * 100f) + "%", this);
        }

        if (shown <= barEmptyThreshold)
        {
            holdTimer += step;
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
        if (debugLog) Debug.Log("[PlanetEndTrigger] Бар порожній: вибух планети й фінальна синематика", this);
        StartCoroutine(FireRoutine());
    }

    // Обидва відліки йдуть від моменту спрацювання тригера
    IEnumerator FireRoutine()
    {
        bool explode = explodePlanet && explosion != null;
        bool cinema = starter != null;
        float tExplode = Mathf.Max(0f, explosionDelay);
        float tCinema = Mathf.Max(0f, cinematicDelay);

        float t = 0f;
        while (explode || cinema)
        {
            if (cinema && t >= tCinema) { starter.Begin(); cinema = false; }
            if (explode && t >= tExplode) { explosion.Explode(); explode = false; }
            if (!explode && !cinema) break;
            yield return null;
            t += Time.deltaTime;
        }
    }
}